// Decision 0011 (run W2, WU7): the web-approval extension, after command-guard's BeforeTool pattern (PiSharp.Durable.Hosting Apps/Extras.cs).
// Before a tool call's intent is recorded it reads the call's decision memo. With none, and when Settings › Tools needs it (Ask, or
// Allow-list with no match, and no grant of this chat), it registers the call with the ApprovalBroker and waits, cancelled by the run's
// DurableContext. The decision is stored as the call's memo (MemoAsync, first stored wins), so a crash after deciding never asks again and
// the tool runs at most once; a crash while waiting finds no memo when the hook runs again on recovery, and asks again. Read-only tools
// (ToolEffects) never ask. Deny blocks the call with the user's reason, which the model gets as the tool result. Only people's decisions are
// memos: a call that the mode, the allow-list or a grant let through stores nothing, so it is judged again if it runs again.
//
// Run W4 (R5): a per-session override. Agent compare's runner attaches a resolver to the app's ApprovalBroker (SetOverrides); for a session
// it names (a model's workspace session that was not opened as a chat), the run's approval mode replaces Settings › Tools: Ask asks before
// every command and every other changing tool, except write and edit calls whose path is inside the workspace (no link on the way, and not
// through a .git file or folder, which names the repository the app's own git commands would use there: those writes ask); Allow-list
// is Ask plus the commands on Settings › Tools' allow-list; Auto asks nothing (workspaces are copies, not sandboxes). The run's own grants
// ("allow this command in all workspaces") let matching calls through in every mode. A resolver that fails asks (it never lets a call
// through unchecked). Every other session (every chat) keeps Settings › Tools.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Contracts;
using ChordJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.WebAgent.Chat;

/// <summary>How a session's tool calls are judged instead of Settings › Tools (agent compare's run approval mode for one workspace).</summary>
/// <param name="Mode">The run's approval mode.</param>
/// <param name="WorkspaceRoot">The workspace's root: write and edit calls inside it don't ask in Ask and Allow-list modes.</param>
/// <param name="WorkingDirectory">The session's working directory, which relative paths are resolved against.</param>
public sealed record ApprovalOverride(ToolApprovalMode Mode, string WorkspaceRoot, string WorkingDirectory)
{
    /// <summary>Rules that let matching calls through without asking in any mode (the run's "allow this command in all workspaces").</summary>
    public IReadOnlyList<ToolAllowRule> Grants { get; init; } = [];
}

/// <summary>The <c>web-approval</c> extension: tool calls wait for the user's decision in the browser.</summary>
public static partial class WebApproval
{
    private static readonly ConditionalWeakTable<ApprovalBroker, Func<string, ApprovalOverride?>> Overrides = new();

    /// <summary>The tools whose <c>path</c> argument names the one file they change: <c>write</c> and <c>edit</c>.</summary>
    public static IReadOnlySet<string> FileTools { get; } = new HashSet<string>(StringComparer.Ordinal) { "write", "edit" };

    /// <summary>Attaches <paramref name="resolver"/> to <paramref name="broker"/>'s sessions: for a session it answers an override for, the
    /// override replaces Settings › Tools. Null removes it. One resolver per broker; a later one replaces it.</summary>
    public static void SetOverrides(ApprovalBroker broker, Func<string, ApprovalOverride?>? resolver)
    {
        ArgumentNullException.ThrowIfNull(broker);
        if (resolver is null) Overrides.Remove(broker);
        else Overrides.AddOrUpdate(broker, resolver);
    }

    /// <summary>The override of <paramref name="sessionId"/> on <paramref name="broker"/>, or null when it has none. A resolver that throws
    /// throws here (the hook then asks).</summary>
    public static ApprovalOverride? OverrideFor(ApprovalBroker broker, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(broker);
        return Overrides.TryGetValue(broker, out var resolver) ? resolver(sessionId) : null;
    }

    /// <summary>Whether a call of <paramref name="tool"/> must wait for a person under the session's override <paramref name="over"/> (the
    /// run's mode and grants; <paramref name="settings"/> gives the allow-list for Allow-list mode) and the session's
    /// <paramref name="grants"/>. Read-only tools are not asked about at all (the hook checks them first).</summary>
    public static bool NeedsApproval(ApprovalOverride over, ToolsSettings settings, IReadOnlyList<ToolAllowRule> grants, string tool, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(over);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grants);
        if (over.Mode == ToolApprovalMode.Auto) return false;
        if (ToolAllowList.Allows(grants, tool, arguments) || ToolAllowList.Allows(over.Grants, tool, arguments)) return false;
        if (over.Mode == ToolApprovalMode.AllowList && ToolAllowList.Allows(settings.AllowList ?? [], tool, arguments)) return false;
        var path = arguments?["path"] is System.Text.Json.Nodes.JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>() : null;
        return !(FileTools.Contains(tool) && IsInside(over, path));
    }

    /// <summary>Whether <paramref name="path"/> (a write or edit call's, relative to the working directory or absolute; a leading <c>@</c>
    /// is dropped) is inside the override's workspace with no link (reparse point) between the workspace root and it. <c>~</c>, anything
    /// that can't be resolved, and any path through git's metadata (see <see cref="IsGitMetadata"/>) count as outside.</summary>
    public static bool IsInside(ApprovalOverride over, string? path)
    {
        ArgumentNullException.ThrowIfNull(over);
        if (string.IsNullOrWhiteSpace(path)) return false;
        var text = path.Trim();
        if (text.StartsWith('@')) text = text[1..];
        if (text.Length == 0 || text.StartsWith('~') || text.Contains('\0', StringComparison.Ordinal)) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(over.WorkspaceRoot));
            var full = Path.GetFullPath(Path.IsPathFullyQualified(text) ? text : Path.Join(over.WorkingDirectory, text));
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return false;
            // A worktree's .git file (or a .git folder anywhere) names the repository git uses there: writing it could plant a config that
            // runs commands when the app diffs or keeps the workspace. Such writes always ask.
            if (full[(root.Length + 1)..].Split(Separators, StringSplitOptions.RemoveEmptyEntries).Any(IsGitMetadata)) return false;
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
            // A link inside the workspace may point anywhere: a path through one (or a file that is one) asks.
            for (var current = Path.TrimEndingDirectorySeparator(full); current.Length > root.Length;
                current = Path.GetDirectoryName(current) is { Length: > 0 } parent ? parent : root)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether one path component may name git's metadata: <c>.git</c> in any case. On Windows also a name the file system reads
    /// as <c>.git</c> (trailing dots or spaces), a stream of a file (any <c>:</c>), and an 8.3 short name (<c>GIT~1</c> may be
    /// <c>.git</c>'s).</summary>
    public static bool IsGitMetadata(string component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var name = component;
        if (OperatingSystem.IsWindows())
        {
            if (name.Contains(':', StringComparison.Ordinal) || ShortName().IsMatch(name)) return true;
            name = name.TrimEnd('.', ' ');
        }
        return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    [System.Text.RegularExpressions.GeneratedRegex(@"~[0-9]+(\.[^.]*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ShortName();

    /// <summary>The extension's name.</summary>
    public const string ExtensionName = "web-approval";

    /// <summary>The memo of a call's decision: <c>{allow, choice, reason?, by}</c>.</summary>
    public const string MemoName = "web-approval";

    /// <summary>The extension for one session: <paramref name="tools"/> is read at every call (Settings › Tools), <paramref name="effects"/>
    /// tells read-only tools.</summary>
    public static Extension Create(string sessionId, ApprovalBroker broker, Func<ToolsSettings> tools, ToolEffects effects)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(effects);
        return Define.Extension(new Extension
        {
            Name = ExtensionName,
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (call, api, context) => BeforeToolAsync(sessionId, broker, tools, effects, call, api, context),
                }),
            ],
        });
    }

    /// <summary>Whether a call of <paramref name="tool"/> must wait for a person under <paramref name="settings"/> and the chat's
    /// <paramref name="grants"/>. Read-only tools are not asked about at all (the hook checks them first).</summary>
    public static bool NeedsApproval(ToolsSettings settings, IReadOnlyList<ToolAllowRule> grants, string tool, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(grants);
        if (settings.ApprovalMode == ToolApprovalMode.Auto) return false;
        if (ToolAllowList.Allows(grants, tool, arguments)) return false;
        return !(settings.ApprovalMode == ToolApprovalMode.AllowList && ToolAllowList.Allows(settings.AllowList ?? [], tool, arguments));
    }

    /// <summary>The block reason of a denied call of <paramref name="tool"/>.</summary>
    public static string DenyReason(string tool, string? reason) =>
        $"The user denied this {tool} call." + (string.IsNullOrWhiteSpace(reason) ? "" : " Reason: " + reason.Trim());

    private static async Task<BeforeToolDecision?> BeforeToolAsync(string sessionId, ApprovalBroker broker, Func<ToolsSettings> tools, ToolEffects effects,
        JsonObjectValue call, IHookApi api, DurableContext context)
    {
        if (!(call.TryGetValue("name", out var name) && name is JsonStringValue { Value: { Length: > 0 } tool })) return null;
        if (!effects.IsWriter(tool)) return null;
        if (await api.MemoAsync(MemoName, context).ConfigureAwait(false) is JsonObjectValue memo) return Apply(memo, tool);
        var arguments = call.TryGetValue("arguments", out var args) && args is JsonObjectValue values ? ToNode(values) : new JsonObject();
        bool needed;
        try
        {
            needed = OverrideFor(broker, sessionId) is { } over
                ? NeedsApproval(over, tools(), broker.Grants(sessionId), tool, arguments)
                : NeedsApproval(tools(), broker.Grants(sessionId), tool, arguments);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            needed = true; // A failed override never lets a call through unchecked.
        }
        if (!needed) return null;
        var callId = call.TryGetValue("id", out var id) && id is JsonStringValue text ? text.Value : "";
        var parent = call.TryGetValue("parent", out var owner) && owner is JsonObjectValue nested && nested.TryGetValue("callId", out var parentId) &&
            parentId is JsonStringValue parentText ? parentText.Value : null;
        var request = new ApprovalRequest(sessionId, api.ConversationId.Value, api.TaskId.Value, callId, tool, arguments, parent);
        var decision = await broker.WaitAsync(request, context.CancellationToken).ConfigureAwait(false);
        var stored = await api.MemoAsync(MemoName, ToMemo(decision), context).ConfigureAwait(false) as JsonObjectValue;
        broker.Stored(request);
        return stored is null ? null : Apply(stored, tool);
    }

    private static JsonObjectValue ToMemo(ApprovalDecision decision)
    {
        var fields = new List<KeyValuePair<string, ChordJson>>
        {
            new("allow", decision.Allows),
            new("choice", decision.Name),
            new("by", "user"),
        };
        if (!string.IsNullOrWhiteSpace(decision.Reason)) fields.Add(new("reason", decision.Reason.Trim()));
        return ChordJson.Object(fields);
    }

    private static BeforeToolDecision? Apply(JsonObjectValue memo, string tool)
    {
        if (memo.TryGetValue("allow", out var allow) && allow is JsonBooleanValue { Value: true }) return null;
        var reason = memo.TryGetValue("reason", out var because) && because is JsonStringValue text ? text.Value : null;
        return new BeforeToolDecision(Block: DenyReason(tool, reason));
    }

    private static JsonObject ToNode(JsonObjectValue value) => JsonNode.Parse(value.ToJson()) as JsonObject ?? [];
}
