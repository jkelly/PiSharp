// PiSharp-only (decision 0005): no upstream counterpart. Plan mode, as in Claude Code and Codex, except that the agent may create and edit
// files inside a plan directory (the plan file, notes, prototypes); write and edit fail for any other path (PlanScope.cs). Entering and
// leaving plan mode are single commits that save and restore the conversation's stored extension selection and tool filter.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable.Tools;

/// <summary>
/// Plan mode: the <c>plan-mode</c> extension (the <c>plan_mode</c> prompt section and the <c>write</c>/<c>edit</c> wrappers), its state
/// document, and the host API to enter, leave, approve and reject. A host installs <see cref="CreateExtension"/> and
/// <see cref="UserInteraction.CreateExtension"/> (which holds <c>exit_plan_mode</c>), keeps <c>plan-mode</c> out of its default extension
/// selection, creates the plan directory and calls <see cref="EnterAsync"/>. While in plan mode the conversation's stored selection adds
/// <c>plan-mode</c> and its tool filter allows only <see cref="DefaultAllowedTools"/> (or <see cref="PlanModeOptions.AllowedTools"/>);
/// <c>bash</c> and <c>powershell</c> stay available and unrestricted by owner decision, so plan mode does not stop the agent from changing
/// files through the shell.
/// </summary>
public static class PlanMode
{
    /// <summary>The extension name.</summary>
    public const string ExtensionName = "plan-mode";

    /// <summary>The name of the tool that submits the plan.</summary>
    public const string ExitToolName = "exit_plan_mode";

    /// <summary>The <c>kind</c> of <c>exit_plan_mode</c>'s details.</summary>
    public const string DetailsKind = "pisharp.plan-approval";

    /// <summary>The interaction document's <c>kind</c> for a plan review.</summary>
    internal const string DocKind = "plan-approval";

    /// <summary>The plan file <c>exit_plan_mode</c> reads when the call names none.</summary>
    internal const string DefaultPlanFile = "plan.md";

    /// <summary>Submissions the state keeps.</summary>
    internal const int MaxSubmissions = 20;

    private static readonly ConditionalWeakTable<Extension, PlanModeOptions> ExtensionOptions = new();

    /// <summary>
    /// The plan-mode state of a conversation (kind <c>pisharp.plan-mode</c>, version 1), rewindable and forked as of the fork entry, as
    /// <c>pi.agent</c> is, so a fork sees the mode its agent has: <c>{mode: "plan"|"default", directory, enteredAt, saved: {extensions, tools},
    /// submissions: [{taskId, callId, planFile?, status, at}] (the last 20), approved?: {taskId, callId, planFile?, at}}</c>. <c>saved</c> holds
    /// the stored <c>pi.agent</c> <c>extensions</c> and <c>tools</c> (null when unset) while in plan mode. <c>directory</c> stays after plan
    /// mode ends, so a wrapped call still running is still confined.
    /// </summary>
    public static RewindableConversationDocToken StateDoc { get; } = Documents.DefineRewindableConversationDoc("pisharp.plan-mode", 1, DocumentFork.AsOf,
        () => JsonObjectValue.Empty, checkpointWhen: (_, _, _) => true);

    /// <summary>The tools plan mode allows by default: <c>read</c>, <c>write</c> and <c>edit</c> (confined to the plan directory),
    /// <c>bash</c> and <c>powershell</c> (not confined), <c>ask_user_question</c> and <c>exit_plan_mode</c>.</summary>
    public static IReadOnlyList<string> DefaultAllowedTools { get; } =
        ["read", "write", "edit", "bash", "powershell", UserQuestions.ToolName, ExitToolName];

    /// <summary>The <c>plan-mode</c> extension: the <c>plan_mode</c> section and the confining wrappers of <c>write</c> and <c>edit</c>.
    /// Install it, but keep it out of the default selection: wherever it is selected, write and edit are confined.</summary>
    public static Extension CreateExtension(PlanModeOptions? options = null)
    {
        var extension = Define.Extension(new Extension
        {
            Name = ExtensionName,
            Sections = [PlanPrompt.Create()],
            Wraps = [new ToolWrap("write", PlanScope.Wrap), new ToolWrap("edit", PlanScope.Wrap)],
        });
        ExtensionOptions.Add(extension, options ?? new PlanModeOptions());
        return extension;
    }

    /// <summary>The <c>exit_plan_mode</c> tool (in <see cref="UserInteraction.CreateExtension"/>): replay-safe, callable only by the model.</summary>
    public static ToolRegistration CreateExitTool() => ExitPlanModeTool.Create();

    // ─── Host API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Enters plan mode in one commit: saves the stored <c>pi.agent</c> <c>extensions</c> and <c>tools</c>, adds <c>plan-mode</c> to the
    /// selection, sets the tool filter to the allowed tools (narrowed by a stored filter, <c>exit_plan_mode</c> always kept), and records
    /// <paramref name="directory"/>. Does nothing when already in plan mode, and returns the current state. The host creates the directory
    /// first. Throws <see cref="ArgumentException"/> for a directory that is not absolute, is a device or network path, a drive or file
    /// system root, the home directory or one of its ancestors, or the conversation's working directory or one of its ancestors; and
    /// <see cref="InvalidOperationException"/> when <paramref name="registry"/> lacks the <c>plan-mode</c> extension or <c>exit_plan_mode</c>.
    /// </summary>
    public static async Task<PlanModeState> EnterAsync(IConversation conversation, IRegistryReader registry, string directory, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(context);
        var snapshot = registry.Snapshot();
        var extension = snapshot.GetExtension(ExtensionName)
            ?? throw new InvalidOperationException($"Plan mode needs the {ExtensionName} extension installed (PlanMode.CreateExtension)");
        if (!snapshot.Tools().Any(tool => tool.Tool.Name == ExitToolName))
            throw new InvalidOperationException($"Plan mode needs {ExitToolName} installed (UserInteraction.CreateExtension)");
        var allowed = (ExtensionOptions.TryGetValue(extension, out var options) ? options.AllowedTools : null) ?? DefaultAllowedTools;
        var root = CheckDirectory(directory);
        var entered = await conversation.CommitAsync(async tx =>
        {
            var state = await tx.DocAsync(StateDoc, conversation.Id).ConfigureAwait(false);
            if (Mode(state) == "plan") return (JsonObjectValue)state.ToValue();
            var agent = await tx.DocAsync(Agents.AgentDoc, conversation.Id).ConfigureAwait(false);
            if (agent.Get("cwd") is JsonStringValue { Value: { Length: > 0 } cwd } && IsSameOrAncestor(root, cwd))
                throw new ArgumentException($"The plan directory {root} is the conversation's working directory {cwd} or one of its ancestors", nameof(directory));
            var savedExtensions = agent.Get("extensions");
            var savedTools = agent.Get("tools");
            agent.Set("extensions", WithPlanMode(savedExtensions));
            agent.Set("tools", AllowedTools(allowed, savedTools));
            // Older submissions stay listed; one still pending belongs to a call that ended without a response.
            CancelPending(state);
            state.Set("mode", "plan");
            state.Set("directory", root);
            state.Set("enteredAt", UserInteraction.Now());
            state.Set("saved", new RecordJson.Writer()
                .Add("extensions", savedExtensions ?? JsonValue.Null)
                .Add("tools", savedTools ?? JsonValue.Null)
                .Build());
            state.Delete("approved");
            return (JsonObjectValue)state.ToValue();
        }, context).ConfigureAwait(false);
        return StateOf(entered)!;
    }

    /// <summary>
    /// Leaves plan mode in one commit: restores the saved <c>extensions</c> and <c>tools</c> (clearing those that were unset) and cancels a
    /// pending plan review, whose <c>exit_plan_mode</c> call then returns that the user left plan mode and ends the run. Does nothing outside
    /// plan mode.
    /// </summary>
    public static Task ExitAsync(IConversation conversation, IRegistryReader registry, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        return conversation.CommitAsync(async tx =>
        {
            var state = await tx.DocAsync(StateDoc, conversation.Id).ConfigureAwait(false);
            if (Mode(state) != "plan") return;
            var submissions = state.ContainsKey("submissions") && state.Get("submissions") is JsonArrayValue ? state.GetArray("submissions") : null;
            for (var index = 0; submissions is not null && index < submissions.Count; index++)
            {
                var submission = submissions.GetObject(index);
                if (submission.Get("status") is not JsonStringValue { Value: "pending" }) continue;
                if (submission.Get("taskId") is JsonNumberValue id)
                {
                    var taskId = new TaskId((long)id.Value);
                    var task = await tx.GetTaskAsync(taskId).ConfigureAwait(false);
                    if (task is not null && task.ConversationId == conversation.Id && task.State.Status is not (DurableTaskStatus.Terminal or DurableTaskStatus.Completing))
                    {
                        var doc = await tx.DocAsync(UserInteraction.InteractionDoc, taskId).ConfigureAwait(false);
                        if (doc.Get("kind") is JsonStringValue { Value: DocKind } && !doc.ContainsKey("response"))
                            doc.Set("response", new RecordJson.Writer().Add("status", "cancelled").Number("at", UserInteraction.Now()).Build());
                    }
                }
                submission.Set("status", "cancelled");
            }
            Restore(await tx.DocAsync(Agents.AgentDoc, conversation.Id).ConfigureAwait(false), state);
            state.Set("mode", "default");
        }, context);
    }

    /// <summary>
    /// Approves the plan <c>exit_plan_mode</c> call <paramref name="exitCall"/> submitted, in one commit: the call's response, the restored
    /// <c>extensions</c> and <c>tools</c>, and the state (default mode, the submission approved). The call then returns that the plan was
    /// approved; with <paramref name="stop"/>, that the user will say when to start, and the run ends. Approving again with the same
    /// <paramref name="stop"/> does nothing. Throws <see cref="InteractionNotPendingException"/> when the call no longer waits (ended, aborted,
    /// reviewed otherwise, or plan mode was left) and <see cref="ArgumentException"/> when <paramref name="exitCall"/> is not a plan review of
    /// this conversation.
    /// </summary>
    public static Task ApproveAsync(IConversation conversation, IRegistryReader registry, TaskId exitCall, bool stop, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);
        var response = new RecordJson.Writer().Add("status", "approved").Add("stop", stop).Build();
        return ReviewAsync(conversation, exitCall, response, context, (state, submission, at) =>
        {
            state.Set("approved", new RecordJson.Writer()
                .Add("taskId", exitCall.ToJson())
                .Add("callId", submission?.Get("callId"))
                .Add("planFile", submission?.Get("planFile"))
                .Number("at", at)
                .Build());
            state.Set("mode", "default");
            return true;
        });
    }

    /// <summary>
    /// Rejects the plan <c>exit_plan_mode</c> call <paramref name="exitCall"/> submitted, with optional <paramref name="feedback"/>, in one
    /// commit. The call returns the feedback and the agent stays in plan mode to revise the plan. Otherwise as <see cref="ApproveAsync"/>.
    /// </summary>
    public static Task RejectAsync(IConversation conversation, TaskId exitCall, string? feedback, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(context);
        var text = feedback is null ? null : feedback.Trim();
        var response = new RecordJson.Writer().Add("status", "rejected").Add("feedback", text is { Length: > 0 } ? text : null).Build();
        return ReviewAsync(conversation, exitCall, response, context, (_, _, _) => false);
    }

    /// <summary>The plan-mode state of a conversation from committed state; null when it never entered plan mode.</summary>
    public static async Task<PlanModeState?> StateAsync(IDocumentReader reader, ConversationId conversationId, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return StateOf(await reader.SnapshotAsync(StateDoc, conversationId, context).ConfigureAwait(false));
    }

    /// <summary>Reads a <see cref="StateDoc"/> value; null for an absent or empty document.</summary>
    public static PlanModeState? StateOf(JsonObjectValue? doc)
    {
        if (doc is null || !doc.TryGetValue("mode", out var mode) || mode is not JsonStringValue { Value: var text }) return null;
        var submissions = doc.TryGetValue("submissions", out var listed) && listed is JsonArrayValue list
            ? list.OfType<JsonObjectValue>().Select(PlanSubmission.FromJson).ToImmutableArray()
            : [];
        var approved = doc.TryGetValue("approved", out var value) && value is JsonObjectValue record ? PlanSubmission.FromJson(record.SetItem("status", "approved")) : null;
        return new PlanModeState(text, doc.TryGetValue("directory", out var directory) && directory is JsonStringValue path ? path.Value : null,
            doc.TryGetValue("enteredAt", out var entered) && entered is JsonNumberValue at ? at.Value : null, submissions, approved);
    }

    /// <summary>Whether a <c>pi.agent</c> document value selects <c>plan-mode</c>: its stored <c>extensions</c> array lists it, or its
    /// <c>{add}</c> edit adds it. A host whose default selection includes <c>plan-mode</c> is misconfigured and is not detected here.</summary>
    public static bool IsActive(JsonObjectValue? agentDoc)
    {
        if (agentDoc is null || !agentDoc.TryGetValue("extensions", out var extensions)) return false;
        if (extensions is JsonArrayValue names) return Names(names).Contains(ExtensionName);
        return extensions is JsonObjectValue edit && edit.TryGetValue("add", out var add) && add is JsonArrayValue added && Names(added).Contains(ExtensionName) &&
            !(edit.TryGetValue("remove", out var remove) && remove is JsonArrayValue removed && Names(removed).Contains(ExtensionName));
    }

    // ─── Commits ────────────────────────────────────────────────────────────

    private static Task ReviewAsync(IConversation conversation, TaskId exitCall, JsonObjectValue response, DurableContext context,
        Func<DraftObject, DraftObject?, double, bool> apply) =>
        conversation.CommitAsync(async tx =>
        {
            var doc = await UserInteraction.OpenAsync(tx, conversation.Id, exitCall, DocKind).ConfigureAwait(false);
            if (doc.Get("response") is JsonObjectValue existing)
            {
                if (JsonValue.DeepEquals(existing.Remove("at"), response)) return;
                throw new InteractionNotPendingException(exitCall, "the plan was already reviewed");
            }
            var state = await tx.DocAsync(StateDoc, conversation.Id).ConfigureAwait(false);
            if (Mode(state) != "plan") throw new InteractionNotPendingException(exitCall, "plan mode has ended");
            var at = UserInteraction.Now();
            doc.Set("response", response.SetItem("at", at));
            var submission = Submission(state, exitCall);
            submission?.Set("status", ((JsonStringValue)response["status"]).Value);
            if (apply(state, submission, at)) Restore(await tx.DocAsync(Agents.AgentDoc, conversation.Id).ConfigureAwait(false), state);
        }, context);

    /// <summary>Restores the saved <c>extensions</c> and <c>tools</c> and drops <c>saved</c>.</summary>
    private static void Restore(DraftObject agent, DraftObject state)
    {
        var saved = state.Get("saved") as JsonObjectValue;
        foreach (var key in new[] { "extensions", "tools" })
        {
            var value = saved is not null && saved.TryGetValue(key, out var stored) ? stored : null;
            if (value is null or JsonNullValue) agent.Delete(key);
            else agent.Set(key, value);
        }
        state.Delete("saved");
    }

    /// <summary>Inside the exit tool's commit: records a pending submission of <paramref name="taskId"/>, once, marking older pending ones
    /// cancelled (their calls ended without a response) and keeping the last <see cref="MaxSubmissions"/>.</summary>
    internal static void AddSubmission(DraftObject state, TaskId taskId, string callId, string? planFile, double at)
    {
        if (Submission(state, taskId) is not null) return;
        CancelPending(state);
        if (!state.ContainsKey("submissions") || state.Get("submissions") is not JsonArrayValue) state.Set("submissions", JsonArrayValue.Empty);
        var submissions = state.GetArray("submissions");
        submissions.Add(new RecordJson.Writer()
            .Add("taskId", taskId.ToJson())
            .Add("callId", callId)
            .Add("planFile", planFile)
            .Add("status", "pending")
            .Number("at", at)
            .Build());
        while (submissions.Count > MaxSubmissions) submissions.RemoveAt(0);
    }

    private static void CancelPending(DraftObject state)
    {
        if (!state.ContainsKey("submissions") || state.Get("submissions") is not JsonArrayValue) return;
        var submissions = state.GetArray("submissions");
        for (var index = 0; index < submissions.Count; index++)
            if (submissions.GetObject(index) is var submission && submission.Get("status") is JsonStringValue { Value: "pending" }) submission.Set("status", "cancelled");
    }

    private static DraftObject? Submission(DraftObject state, TaskId taskId)
    {
        if (!state.ContainsKey("submissions") || state.Get("submissions") is not JsonArrayValue) return null;
        var submissions = state.GetArray("submissions");
        for (var index = submissions.Count - 1; index >= 0; index--)
            if (((JsonObjectValue)submissions.Get(index)).TryGetValue("taskId", out var id) && id is JsonNumberValue number && (long)number.Value == taskId.Value)
                return submissions.GetObject(index);
        return null;
    }

    internal static string? Mode(DraftObject state) => state.Get("mode") is JsonStringValue { Value: var mode } ? mode : null;

    /// <summary>The stored extension selection with <c>plan-mode</c> added: appended to an array, or to an edit's <c>add</c> (and taken out
    /// of its <c>remove</c>).</summary>
    private static JsonValue WithPlanMode(JsonValue? stored)
    {
        if (stored is JsonArrayValue names)
            return Names(names).Contains(ExtensionName) ? names : JsonValue.Array([.. names, ExtensionName]);
        if (stored is JsonObjectValue edit)
        {
            var add = edit.TryGetValue("add", out var listed) && listed is JsonArrayValue added ? Names(added).ToList() : [];
            if (!add.Contains(ExtensionName)) add.Add(ExtensionName);
            var next = edit.SetItem("add", JsonValue.Array(add.Select(name => (JsonValue)name)));
            if (edit.TryGetValue("remove", out var removeValue) && removeValue is JsonArrayValue removed)
                next = next.SetItem("remove", JsonValue.Array(Names(removed).Where(name => name != ExtensionName).Select(name => (JsonValue)name)));
            return next;
        }
        return JsonValue.Object([KeyValuePair.Create("add", (JsonValue)JsonValue.Array(ExtensionName))]);
    }

    /// <summary>The plan-mode tool filter: the allowed tools, narrowed by the stored filter (an array keeps only its names, a
    /// <c>{remove}</c> drops its names), with <c>exit_plan_mode</c> always kept.</summary>
    private static JsonArrayValue AllowedTools(IReadOnlyList<string> allowed, JsonValue? stored)
    {
        IEnumerable<string> names = allowed.Contains(ExitToolName) ? allowed : [.. allowed, ExitToolName];
        if (stored is JsonArrayValue only)
        {
            var kept = Names(only).ToHashSet(StringComparer.Ordinal);
            names = names.Where(name => name == ExitToolName || kept.Contains(name));
        }
        else if (stored is JsonObjectValue filter && filter.TryGetValue("remove", out var value) && value is JsonArrayValue removed)
        {
            var dropped = Names(removed).ToHashSet(StringComparer.Ordinal);
            names = names.Where(name => name == ExitToolName || !dropped.Contains(name));
        }
        return JsonValue.Array(names.Distinct(StringComparer.Ordinal).Select(name => (JsonValue)name));
    }

    /// <summary>The normalized plan directory, or why it cannot be one.</summary>
    private static string CheckDirectory(string directory)
    {
        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException($"The plan directory must be an absolute path: {directory}", nameof(directory));
        if (PlanScopedEnv.HasDevicePrefix(directory))
            throw new ArgumentException($"The plan directory cannot be a device or network path: {directory}", nameof(directory));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (PlanScopedEnv.HasDevicePrefix(full) || string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase) ||
            Path.GetPathRoot(full) is not { Length: > 0 })
            throw new ArgumentException($"The plan directory cannot be a file system root: {full}", nameof(directory));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && IsSameOrAncestor(full, home))
            throw new ArgumentException($"The plan directory {full} is the home directory or one of its ancestors", nameof(directory));
        return full;
    }

    /// <summary>Whether <paramref name="directory"/> is <paramref name="path"/> or one of its ancestors; case-insensitive on Windows.</summary>
    private static bool IsSameOrAncestor(string directory, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Normal(string value)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)); }
            catch (Exception) { return value; }
        }
        var parent = Normal(directory);
        var child = Normal(path);
        if (string.Equals(parent, child, comparison)) return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, comparison);
    }

    private static IEnumerable<string> Names(JsonArrayValue names) => names.OfType<JsonStringValue>().Select(name => name.Value);
}

/// <summary>Options of the <c>plan-mode</c> extension.</summary>
public sealed record PlanModeOptions
{
    /// <summary>The tools plan mode allows; null for <see cref="PlanMode.DefaultAllowedTools"/>. <c>exit_plan_mode</c> is always allowed.
    /// Only <c>write</c> and <c>edit</c> are confined to the plan directory: any other tool listed here can change the project.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
}

/// <summary>The plan-mode state of a conversation (<see cref="PlanMode.StateDoc"/>).</summary>
/// <param name="Mode"><c>plan</c> or <c>default</c>.</param>
/// <param name="Directory">The plan directory; it stays after plan mode ends.</param>
/// <param name="EnteredAt">When plan mode was last entered, in wall-clock milliseconds.</param>
/// <param name="Submissions">The last plans submitted with <c>exit_plan_mode</c>, oldest first.</param>
/// <param name="Approved">The plan approved last, since plan mode was last entered.</param>
public sealed record PlanModeState(string Mode, string? Directory, double? EnteredAt, ImmutableArray<PlanSubmission> Submissions, PlanSubmission? Approved)
{
    /// <summary>Whether the conversation is in plan mode.</summary>
    public bool Active => Mode == "plan";
}

/// <summary>One plan submitted with <c>exit_plan_mode</c>.</summary>
/// <param name="TaskId">The <c>exit_plan_mode</c> tool task.</param>
/// <param name="CallId">The model's tool call ID.</param>
/// <param name="PlanFile">The plan file, when the plan was read from one.</param>
/// <param name="Status"><c>pending</c>, <c>approved</c>, <c>rejected</c> or <c>cancelled</c>. A call aborted while pending stays
/// <c>pending</c> until plan mode is left or another plan is submitted; <see cref="UserInteraction.PendingAsync"/> tells what still waits.</param>
/// <param name="At">When it was submitted, or for <see cref="PlanModeState.Approved"/> when it was approved, in wall-clock milliseconds.</param>
public sealed record PlanSubmission(TaskId TaskId, string CallId, string? PlanFile, string Status, double At)
{
    internal static PlanSubmission FromJson(JsonObjectValue value) => new(
        value.TryGetValue("taskId", out var id) && id is JsonNumberValue number ? new TaskId((long)number.Value) : default,
        value.TryGetValue("callId", out var call) && call is JsonStringValue callId ? callId.Value : "",
        value.TryGetValue("planFile", out var file) && file is JsonStringValue planFile ? planFile.Value : null,
        value.TryGetValue("status", out var status) && status is JsonStringValue text ? text.Value : "",
        value.TryGetValue("at", out var at) && at is JsonNumberValue time ? time.Value : 0);
}
