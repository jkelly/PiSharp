// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/types.ts. The openable Harness (P6) and the task runtime (P7)
// are complete; submissions and the generation hooks are final (P8); tools are final (P9); views are final (P10); compaction and the task
// graph are final (P11). v1.2.0: input entries (area i), documentRetentionMs and onReport's contract (area b), and tool task v2 (area a):
// ToolExecutionResult.Output and StructuredOutput, NestedToolExecutionResult, IToolExecutionApi.RetainedOutput and ExecuteToolAsync,
// ToolRegistration.StructuredOutputSchema, Callers and Deferred, AgentState modelTools (AgentChange.ModelTools) and Agent.Callable and
// Deferred, and the ToolHookCall's parent.
//
// Mappings: upstream's `Tool` generic (a host's own ToolRegistration subtype) becomes record inheritance, so `{ ...tool, x }` is
// `tool with { X = ... }` and keeps the subtype; pi-ai messages and tool calls are wire JSON (JsonValue); a field that may be given,
// cleared with `null` or left `undefined` is a nullable Patch<T>; a stored agent state is the pi.agent document's JsonObjectValue.
// A ToolHookCall is the call's wire JSON with, for a nested call, a `parent` object {taskId, callId}; a NestedToolExecutionResult is a
// record whose JSON (ToJson, in upstream's key order) is what NestedCallDoc stores and tool events carry. A JSON field upstream leaves
// undefined is a C# null; an explicit JSON null is JsonValue.Null.
//
// Pi v1.5.0 sync (area harness-options): CompactionSettings.ModelOverrides and Settings.Compaction's overrides (upstream 6085986e4,
// #10942), HarnessOptions.CurrentModels and CurrentSettings (upstream's function forms of models and settings, 7ec1f648e) and
// HarnessOptions.ReadOnly (be14d9732), and ToolControl.Terminate as a bool? that may be false (6085986e4, #10858).
// Pi v1.5.0 sync (area model-requests): HarnessOptions.Identity (adc5fa9f8, 04a93bad8) and GenerationHooks.BeforeProviderRequest
// (d280e1ffa).
// Pi v1.5.0 sync (area reports): HarnessOptions.OnReport takes one Report (TypesReports.cs; a30c2a81d, 91b33cab3, 5f312dac4),
// settings.afterFailedRun (AfterFailedRun, a30c2a81d), and TaskInspectionKind.Pending replaces Ready (TaskState's word).
// Pi v1.5.0 sync (area telemetry): HarnessOptions.Telemetry, the host-owned adapter of DURABLE_TELEMETRY_SCHEMA (2ec55d808, 48909d987).
// Pi v1.5.0 sync (area watches, 92ab53602): Conversation.viewState() and Harness.taskGraph() are removed; the view and task graph watches
// are Chord's IWatchHandle<T>. Area watch-folding (d8d99950a): their docs say commits made while a listener runs arrive folded.
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): IConversation.CommitAsync and IToolExecutionApi.CommitAsync take CommitOptions, and
// ProgressPolicy and ProgressSettings PersistWithinMs: progress commits are deferred (CommitPersistence).
// Pi v1.5.0 sync (area observers, 8b0caefc2): IHarness.ObserveConversationAsync and ObserveTaskGraphAsync, synchronous Observer<T>s of
// the view and task graph mounts the watches share.
// Pi v1.5.0 sync (area view, f3255de76): IConversation.WatchAsync and IHarness.ObserveConversationAsync take a ConversationWindow; the
// view's entries are a window over the transcript.
// Pi v1.5.0 sync (area tool-tasks, 87eff524f, 1c6928b30): ToolHooks.AfterTool runs on every result a tool task writes, the Harness's own error
// results included, and its doc says which (#11043).
// Pi v1.5.0 sync (area admission): SubmissionDraft.Continue, a run on the context as it is (fb524dd2e, #11043), and IConversationHandle's
// SubmitAsync takes any draft, as IConversation's does; WhenIdle.Reject on input (6a2825113, #11149); and SubmitAsync(tx, draft) on both,
// admission inside the caller's commit (cc9c52310, #11149). settings.afterFailedRun's doc names nothing_to_answer.
// Pi v1.5.0 sync (area live-turns, f6a9db1d8, #11043): AgentChange.MaxTurns and Agent.MaxTurns, the per-conversation turn limit (AgentState
// maxTurns), and settings.afterFailedRun's doc names turn_limit.
// Pi v1.5.0 sync (area run-end): IHookApi's entry reads, EntryAsync and EntriesAsync (65fba65b2), which ITaskRuntime inherits;
// GenerationHooks.AfterRun with RunSummary and RunEndTask (Define.RunEndTask, 7e15f9601); WhenIdle's doc says a run waiting for its
// afterRun tasks is busy; ToolControl.Handoff continues the run (38ff99177). Upstream's afterRun returns {tasks}; AfterRun returns the
// list itself (null for none), as BeforeRequest returns its messages. Upstream's tools-schema typing (ToolArgs, object schemas) has no
// C# counterpart beyond the JsonObjectValue schemas area tools-schema documented. With this area every area that changes the file has
// landed, so it cites v1.5.0.
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Internal;

namespace PiSharp.Durable;

/// <summary>Provider and model ID resolved through <see cref="IDurableModels"/> (upstream <c>ModelRef</c>). JSON key order:
/// <c>provider, modelId</c>.</summary>
public sealed record ModelRef(string Provider, string ModelId)
{
    /// <summary><c>{provider, modelId}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("provider", Provider).Add("modelId", ModelId).Build();

    /// <summary>Reads <c>{provider, modelId}</c>; null when the value has another shape.</summary>
    public static ModelRef? FromJson(JsonValue? json) =>
        json is JsonObjectValue value && value.TryGetValue("provider", out var provider) && provider is JsonStringValue p &&
        value.TryGetValue("modelId", out var modelId) && modelId is JsonStringValue m ? new ModelRef(p.Value, m.Value) : null;
}

/// <summary>One field of a change: a given value replaces the stored one and <see cref="Clear"/> removes it (upstream <c>T | null</c>);
/// a null <c>Patch&lt;T&gt;?</c> changes nothing (upstream <c>undefined</c>).</summary>
public readonly struct Patch<T> where T : notnull
{
    private readonly T? value;
    private Patch(bool clear, T? value) { IsClear = clear; this.value = value; }

    /// <summary>Removes the stored value (upstream <c>null</c>).</summary>
    public static Patch<T> Clear => new(true, default);

    /// <summary>Replaces the stored value.</summary>
    public static Patch<T> Set(T value) => new(false, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Whether this clears the field.</summary>
    public bool IsClear { get; }

    /// <summary>The replacement value; throws for <see cref="Clear"/>.</summary>
    public T Value => IsClear ? throw new InvalidOperationException("The patch clears its field.") : value!;

    /// <summary>Replaces the stored value.</summary>
    public static implicit operator Patch<T>(T value) => Set(value);
}

/// <summary>A selection of extensions in an <see cref="AgentChange"/>: exactly these, in order, or the host default edited by
/// <see cref="Add"/> and <see cref="Remove"/> (upstream <c>readonly Extension[] | { add?, remove? }</c>). Extensions are stored by
/// name.</summary>
public sealed class ExtensionSelection
{
    private ExtensionSelection(ImmutableArray<Extension>? exactly, ImmutableArray<Extension>? add, ImmutableArray<Extension>? remove)
    { Exactly = exactly; Add = add; Remove = remove; }

    /// <summary>Selects exactly <paramref name="extensions"/>, in order.</summary>
    public static ExtensionSelection Only(params IEnumerable<Extension> extensions) => new([.. extensions], null, null);

    /// <summary>Edits the host default selection.</summary>
    public static ExtensionSelection Edit(IEnumerable<Extension>? add = null, IEnumerable<Extension>? remove = null) =>
        new(null, add is null ? null : [.. add], remove is null ? null : [.. remove]);

    /// <summary>The exact selection; null for an edit.</summary>
    public ImmutableArray<Extension>? Exactly { get; }
    /// <summary>Extensions an edit appends.</summary>
    public ImmutableArray<Extension>? Add { get; }
    /// <summary>Extensions an edit removes.</summary>
    public ImmutableArray<Extension>? Remove { get; }
}

/// <summary>A filter of the selected extensions' tools in an <see cref="AgentChange"/>: exactly these, in order, or every tool but these
/// (upstream <c>readonly ToolRegistration[] | { remove }</c>). Tools are stored by name.</summary>
public sealed class ToolSelection
{
    private ToolSelection(ImmutableArray<ToolRegistration>? exactly, ImmutableArray<ToolRegistration>? remove) { Exactly = exactly; Remove = remove; }

    /// <summary>Offers exactly <paramref name="tools"/>, in order.</summary>
    public static ToolSelection Only(params IEnumerable<ToolRegistration> tools) => new([.. tools], null);

    /// <summary>Offers every selected tool except <paramref name="tools"/>.</summary>
    public static ToolSelection Without(params IEnumerable<ToolRegistration> tools) => new(null, [.. tools]);

    /// <summary>The exact filter; null for a removal.</summary>
    public ImmutableArray<ToolRegistration>? Exactly { get; }
    /// <summary>The removed tools; null for an exact filter.</summary>
    public ImmutableArray<ToolRegistration>? Remove { get; }
}

/// <summary>
/// The filter of the tools a conversation offers the model in an <see cref="AgentChange"/> (upstream <c>modelTools</c>:
/// <c>readonly ToolRegistration[] | { remove?, add? }</c>): exactly these, in order, deferred ones too; or every enabled model-callable
/// tool except deferred ones and <see cref="Remove"/>, followed by the deferred tools in <see cref="Add"/> that are not removed, in
/// <see cref="Add"/> order. It replaces the stored filter as a whole; tools loaded with <c>control.addTools</c> are in its <c>add</c>. Tools
/// are stored by name.
/// </summary>
public sealed class ModelToolSelection
{
    private ModelToolSelection(ImmutableArray<ToolRegistration>? exactly, ImmutableArray<ToolRegistration>? remove,
        ImmutableArray<ToolRegistration>? add)
    { Exactly = exactly; Remove = remove; Add = add; }

    /// <summary>Offers exactly <paramref name="tools"/>, in order (an array).</summary>
    public static ModelToolSelection Only(params IEnumerable<ToolRegistration> tools) => new([.. tools], null, null);

    /// <summary>Offers every enabled model-callable tool except deferred ones and <paramref name="remove"/>, then the deferred tools of
    /// <paramref name="add"/> (<c>{ remove?, add? }</c>; a null list is left out).</summary>
    public static ModelToolSelection Edit(IEnumerable<ToolRegistration>? remove = null, IEnumerable<ToolRegistration>? add = null) =>
        new(null, remove is null ? null : [.. remove], add is null ? null : [.. add]);

    /// <summary>The exact filter; null for an edit.</summary>
    public ImmutableArray<ToolRegistration>? Exactly { get; }
    /// <summary>The removed tools of an edit; null when the edit has no <c>remove</c>.</summary>
    public ImmutableArray<ToolRegistration>? Remove { get; }
    /// <summary>The loaded deferred tools of an edit; null when the edit has no <c>add</c>.</summary>
    public ImmutableArray<ToolRegistration>? Add { get; }
}

/// <summary>A change to <c>pi.agent</c> (upstream <c>AgentChange</c>): a given field replaces the stored one, <see cref="Patch{T}.Clear"/>
/// clears it, and a null field changes nothing.</summary>
public sealed record AgentChange
{
    /// <summary>The model.</summary>
    public Patch<ModelRef>? Model { get; init; }
    /// <summary>The thinking level (pi-ai <c>ModelThinkingLevel</c>: off, minimal, low, medium, high, xhigh).</summary>
    public Patch<string>? ThinkingLevel { get; init; }
    /// <summary>The extension selection.</summary>
    public Patch<ExtensionSelection>? Extensions { get; init; }
    /// <summary>The tool filter: the enabled tools.</summary>
    public Patch<ToolSelection>? Tools { get; init; }
    /// <summary>The filter of the enabled tools the model may call: the tools offered to it. Replaces the stored filter as a whole.</summary>
    public Patch<ModelToolSelection>? ModelTools { get; init; }
    /// <summary>Rendered after every extension section, as the section <c>instructions</c>.</summary>
    public Patch<string>? Instructions { get; init; }
    /// <summary>Directory within the environment's file system, passed to <c>HarnessOptions.Env</c>.</summary>
    public Patch<string>? Cwd { get; init; }
    /// <summary>Tool rounds one run takes at most (<see cref="Agent.MaxTurns"/>). Configuring throws
    /// <see cref="ArgumentOutOfRangeException"/> (upstream <c>RangeError</c>) unless it is an integer 1 or more, within JavaScript's safe
    /// integers, or <see cref="Patch{T}.Clear"/>.</summary>
    public Patch<long>? MaxTurns { get; init; }
    /// <summary>PiSharp-only (decision 0007): the service tier of the conversation's requests (auto, normal, economy, priority, fast,
    /// ultrafast). A child conversation starts with its owner's.</summary>
    public Patch<string>? ServiceTier { get; init; }
}

/// <summary>A conversation's agent resolved against a registry snapshot and the settings (upstream <c>Agent</c>).</summary>
public sealed record Agent
{
    /// <summary>The model; null when unset.</summary>
    public ModelRef? Model { get; init; }
    /// <summary>The thinking level; <c>off</c> when unset.</summary>
    public required string ThinkingLevel { get; init; }
    /// <summary>The selected installed extensions, in order.</summary>
    public required ImmutableArray<Extension> Extensions { get; init; }
    /// <summary>The tools a request offers, in order: enabled, callable by the model, and selected by <c>modelTools</c>.</summary>
    public required ImmutableArray<ToolRegistration> Tools { get; init; }
    /// <summary>The tools nested calls (<see cref="IToolExecutionApi.ExecuteToolAsync"/>) resolve among, in order: enabled and callable by
    /// tools.</summary>
    public required ImmutableArray<ToolRegistration> Callable { get; init; }
    /// <summary>Enabled deferred tools the model may call and is not offered yet, in registry order: what <c>tool_search</c> loads.</summary>
    public required ImmutableArray<ToolRegistration> Deferred { get; init; }
    /// <summary>Extension sections, then <c>instructions</c> when set.</summary>
    public required ImmutableArray<PromptSection> Sections { get; init; }
    /// <summary>The agent's instructions.</summary>
    public string? Instructions { get; init; }
    /// <summary>The environment directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>
    /// Tool rounds one run takes at most (<c>pi.live.run.turns</c>): the round that reaches it runs its tools, then the run ends
    /// <c>unanswered</c> with <c>turn_limit</c>. An integer, 1 or more; null, no limit. Task-owned children and forks inherit it.
    /// </summary>
    public long? MaxTurns { get; init; }
    /// <summary>PiSharp-only (decision 0007): the service tier; null when unset.</summary>
    public string? ServiceTier { get; init; }
}

// ─── Tools ──────────────────────────────────────────────────────────────

/// <summary>Whether an interrupted execution may rerun on recovery.</summary>
public enum ToolReplay
{
    /// <summary><c>safe</c>.</summary>
    Safe,
    /// <summary><c>unsafe</c> (the default).</summary>
    Unsafe,
}

/// <summary>Whether the tools of one round run at once or one after another in call order (upstream <c>ToolExecutionMode</c>).</summary>
public enum ToolExecutionMode
{
    /// <summary><c>parallel</c>.</summary>
    Parallel,
    /// <summary><c>sequential</c>.</summary>
    Sequential,
}

/// <summary>How many queued items of one mode a boundary places: the first, or all of them (upstream <c>QueueMode</c>).</summary>
public enum QueueMode
{
    /// <summary><c>all</c>.</summary>
    All,
    /// <summary><c>one-at-a-time</c>.</summary>
    OneAtATime,
}

/// <summary>Severity of a tool diagnostic.</summary>
public enum ToolDiagnosticSeverity
{
    /// <summary><c>info</c>.</summary>
    Info,
    /// <summary><c>warn</c>.</summary>
    Warn,
    /// <summary><c>error</c>.</summary>
    Error,
}

/// <summary>Remark about a call for the model and the UI, such as truncation or a spill path; never part of the tool's data (upstream
/// <c>ToolDiagnostic</c>).</summary>
public sealed record ToolDiagnostic(ToolDiagnosticSeverity Severity, string Message, string? Code = null);

/// <summary>Post-tools controls requested by a tool result (upstream <c>ToolControl</c>).</summary>
public sealed record ToolControl
{
    /// <summary>Tools to add to the agent's filter.</summary>
    public ImmutableArray<string>? AddTools { get; init; }
    /// <summary>End the run without another model request when every result of the round sets it to true; false is a no-op and is stored as
    /// given (upstream's <c>terminate?: boolean</c>, #10858).</summary>
    public bool? Terminate { get; init; }
    /// <summary>Start a new context with this text as its user message, as <c>reset(handoff)</c> writes, and continue the run there. The last
    /// handoff in call order wins.</summary>
    public string? Handoff { get; init; }
}

/// <summary>
/// The result of one tool execution (upstream <c>ToolExecutionResult</c>), on three channels: <see cref="Output"/> for the model,
/// <see cref="StructuredOutput"/> for programs that call the tool (<see cref="IToolExecutionApi.ExecuteToolAsync"/> callers, code mode
/// scripts), and <see cref="Details"/> for UIs.
/// </summary>
public sealed record ToolExecutionResult
{
    /// <summary>For the model: pi-ai tool result content (wire JSON); null: the retained running output.</summary>
    public JsonArrayValue? Output { get; init; }
    /// <summary>
    /// For programs. Required unless <see cref="IsError"/> when the tool declares <see cref="ToolRegistration.StructuredOutputSchema"/>, and
    /// validated against it; not allowed without one, where programs get the output itself
    /// (<see cref="NestedToolExecutionResult.StructuredOutput"/>). Null is upstream's undefined; <see cref="JsonValue.Null"/> is a JSON null.
    /// </summary>
    public JsonValue? StructuredOutput { get; init; }
    /// <summary>Whether the result is an error.</summary>
    public bool? IsError { get; init; }
    /// <summary>Null: the last running details become the details.</summary>
    public JsonValue? Details { get; init; }
    /// <summary>Added after those recorded while running.</summary>
    public ImmutableArray<ToolDiagnostic>? Diagnostics { get; init; }
    /// <summary>pi-ai usage of the execution itself (wire JSON), stored on the result and in <c>pi.usage.tools</c>.</summary>
    public JsonObjectValue? Usage { get; init; }
    /// <summary>Post-tools controls.</summary>
    public ToolControl? Control { get; init; }
}

/// <summary>
/// Result of a nested call, as <see cref="IToolExecutionApi.ExecuteToolAsync"/> returns it (upstream <c>NestedToolExecutionResult</c>).
/// Programs read <see cref="StructuredOutput"/>; the output for the model is not kept, and <c>control</c> does not apply. Its JSON
/// (<see cref="ToJson"/>) is what the caller's <c>NestedCallDoc</c> stores and a nested <c>tool_execution_end</c> event carries.
/// </summary>
public sealed record NestedToolExecutionResult
{
    /// <summary>The nested call's tool task.</summary>
    public required TaskId TaskId { get; init; }
    /// <summary>
    /// The tool's value for programs. A tool with <see cref="ToolRegistration.StructuredOutputSchema"/>: its validated structured output,
    /// also on an error result that has one; null otherwise, and an error's text is then in an error diagnostic. A tool without: its bounded
    /// output, success or error, as a string for one text item, an <c>ImageContent</c> for one image, <c>""</c> for none, and the content
    /// list otherwise. Null on an error result the Harness wrote itself (unavailable, invalid, blocked, interrupted, aborted);
    /// <see cref="Diagnostics"/> say what went wrong. A JSON null is <see cref="JsonValue.Null"/>.
    /// </summary>
    public JsonValue? StructuredOutput { get; init; }
    /// <summary>Whether the result is an error.</summary>
    public required bool IsError { get; init; }
    /// <summary>The details; null when absent.</summary>
    public JsonValue? Details { get; init; }
    /// <summary>The diagnostics.</summary>
    public required ImmutableArray<ToolDiagnostic> Diagnostics { get; init; }
    /// <summary>pi-ai usage (wire JSON); null when absent.</summary>
    public JsonObjectValue? Usage { get; init; }
    /// <summary>Execution time of the attempt that produced the result; null when it never executed.</summary>
    public double? DurationMs { get; init; }

    /// <summary><c>{taskId, structuredOutput?, isError, details?, diagnostics, usage?, durationMs?}</c>, upstream's key order.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer()
        .Add("taskId", TaskId.ToJson())
        .Add("structuredOutput", StructuredOutput)
        .Add("isError", IsError)
        .Add("details", Details)
        .Add("diagnostics", JsonValue.Array(Diagnostics.Select(ToolResults.DiagnosticJson)))
        .Add("usage", Usage)
        .Number("durationMs", DurationMs)
        .Build();

    /// <summary>Reads <see cref="ToJson"/>'s shape.</summary>
    public static NestedToolExecutionResult FromJson(JsonValue json)
    {
        const string what = "nested tool execution result";
        var value = RecordJson.Object(json, what);
        return new NestedToolExecutionResult
        {
            TaskId = RecordJson.Id<TaskId>(value, "taskId", what),
            StructuredOutput = RecordJson.Optional(value, "structuredOutput"),
            IsError = RecordJson.Boolean(value, "isError", what),
            Details = RecordJson.Optional(value, "details"),
            Diagnostics = [.. RecordJson.Array(RecordJson.Required(value, "diagnostics", what), "diagnostics", what).Select(ToolResults.ParseDiagnostic)],
            Usage = RecordJson.Optional(value, "usage") is { } usage ? RecordJson.Object(usage, what) : null,
            DurationMs = RecordJson.OptionalNumber(value, "durationMs", what),
        };
    }
}

/// <summary>Options of a nested call (upstream <c>executeTool()</c>'s <c>{ key?, progress? }</c>).</summary>
public sealed record NestedCallOptions
{
    /// <summary>The nested call's key within its caller; default its position among the invocation's nested calls, <c>1</c>, <c>2</c>, ...
    /// An explicit key must be non-empty, without <c>/</c>, not <c>__proto__</c>, and not a positive integer.</summary>
    public string? Key { get; init; }
    /// <summary><c>false</c>: commit no running output, details, or diagnostics to the nested call's slot, only its status.</summary>
    public bool? Progress { get; init; }
}

/// <summary>The output retained so far, as the model will see it when the result omits its output, and whether earlier output was
/// dropped (upstream <c>retainedOutput()</c>'s <c>{ text, truncated }</c>).</summary>
public readonly record struct RetainedOutput(string Text, bool Truncated);

/// <summary>Who may call a tool (upstream <c>ToolRegistration.callers</c>' members).</summary>
public enum ToolCaller
{
    /// <summary><c>model</c>: the model, in a tool round.</summary>
    Model,
    /// <summary><c>tools</c>: other tools, through <see cref="IToolExecutionApi.ExecuteToolAsync"/>.</summary>
    Tools,
}

/// <summary>Which end of a tool's output is kept (upstream <c>"head" | "tail"</c>).</summary>
public enum ToolOutputRetention
{
    /// <summary><c>head</c>: the beginning (the default).</summary>
    Head,
    /// <summary><c>tail</c>: the end.</summary>
    Tail,
}

/// <summary>Output bounds of a tool (upstream <c>outputLimits</c>); a null field takes its default.</summary>
public sealed record ToolOutputLimits
{
    /// <summary>Maximum retained bytes; default 50 KiB (<see cref="Truncation.DefaultMaxBytes"/>).</summary>
    public long? MaxBytes { get; init; }
    /// <summary>Maximum retained lines; default 2000 (<see cref="Truncation.DefaultMaxLines"/>).</summary>
    public long? MaxLines { get; init; }
    /// <summary>Whether the head or the tail is kept; default <see cref="ToolOutputRetention.Head"/>.</summary>
    public ToolOutputRetention? Retain { get; init; }
}

/// <summary>
/// Operations available to one tool invocation (upstream <c>ToolExecutionApi</c>). Every operation rejects after the invocation ends. Upstream's
/// api is a plain object a wrapper spreads; here a wrapper derives from <see cref="DelegatingToolExecutionApi"/> and overrides what it changes.
/// </summary>
public interface IToolExecutionApi : IDocumentObserver, IDocumentReader
{
    /// <summary>The tool task.</summary>
    TaskId TaskId { get; }
    /// <summary>The calling conversation.</summary>
    ConversationId ConversationId { get; }
    /// <summary>The model's tool call ID.</summary>
    string CallId { get; }
    /// <summary>The tool task's phase snapshot.</summary>
    IRegistrySnapshot Registry { get; }
    /// <summary>The calling conversation's agent, as the tool task's phase resolved it.</summary>
    Task<Agent> AgentAsync(DurableContext context);
    /// <summary>The current <c>HarnessOptions.Models</c>, read at each access: the catalog, credentials and request transforms.</summary>
    IDurableModels Models { get; }
    /// <summary>Built by <c>HarnessOptions.Env</c> for this call; null without an environment.</summary>
    IExecutionEnv? Env { get; }
    /// <summary>The Harness clock (<see cref="HarnessOptions.Now"/>), in milliseconds, for times a rerun after a restart compares against.
    /// Throws once the tool task's invocation has ended.</summary>
    double Now();
    /// <summary>
    /// Appends running output; it becomes the result's <c>Output</c> when the result omits it. <paramref name="skipped"/> counts output
    /// omitted before the chunk, as reported by an environment given <see cref="OutputWindow"/> (<c>ShellOutputInfo.skipped</c>). Throws once
    /// the call has settled.
    /// </summary>
    void Output(string chunk, ShellOutputSkip? skipped = null);
    /// <summary>Appends running output as UTF-8 bytes; a character split across chunks survives. See
    /// <see cref="Output(string, ShellOutputSkip?)"/>.</summary>
    void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null);
    /// <summary>
    /// The tail this call's output keeps and the pace of its progress commits, for <c>ShellExecOptions.Window</c>; null when the tool keeps the
    /// head of its output, which cannot accept skips. A wrapper that replaces <c>Output</c> and transforms text must also replace this with
    /// null, so skipped text cannot bypass its transform.
    /// </summary>
    ShellOutputWindow? OutputWindow { get; }
    /// <summary>The output retained so far, as the model will see it when the result omits <c>Output</c>, and whether earlier output was
    /// dropped; for a tool that also returns that text in its <c>StructuredOutput</c>. Throws once the call has settled.</summary>
    RetainedOutput RetainedOutput();
    /// <summary>Records a model-visible remark about this call. Throws once the call has settled.</summary>
    void Diagnostic(ToolDiagnostic diagnostic);
    /// <summary>Replaces the running details; the last value becomes the result details when the result omits <c>Details</c>. Resolves with
    /// the progress commit that includes it; cancelling <paramref name="context"/> stops the wait, not the update.</summary>
    Task DetailsAsync(JsonValue value, DurableContext context);
    /// <summary>Commits on the Session line in the tool task's scope; appended entries carry the task as <c>byTaskId</c>.
    /// <paramref name="options"/> may defer storing it (<see cref="CommitPersistence"/>).</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null);
    /// <summary>Commits on the Session line in the tool task's scope.</summary>
    Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null);
    /// <summary>Reads a memo of the tool task.</summary>
    Task<JsonValue?> MemoAsync(string name, DurableContext context);
    /// <summary>Stores a memo of the tool task unless one exists; returns the winner.</summary>
    Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context);
    /// <summary>Creates a task in its own commit; <see cref="TaskOptions.ConversationId"/> defaults to the calling conversation (upstream
    /// takes the options without it). A child task of a tool that is not replay-safe defaults to <see cref="TaskOptions.AbandonOnRestart"/>:
    /// the tool never resumes after a restart, so nothing awaits the child then.</summary>
    Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context);
    /// <summary>The committed task record.</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context);
    /// <summary>Resolves with the task's terminal receipt; rejects when the invocation ends.</summary>
    Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context);
    /// <summary>Invocation-bound handle of an existing conversation, such as one this tool created in a commit.</summary>
    Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context);
    /// <summary>
    /// Runs tool <paramref name="name"/> as a nested call of this one and waits for its result (upstream <c>executeTool</c>). The nested call
    /// is its own <c>pi.tool</c> task, owned by this call: it resolves the tool among the conversation's tools (<see cref="Agent.Callable"/>),
    /// validates, runs the tool task's hooks (whose call carries <c>parent</c>), applies output limits and the tool's replay policy, and reports
    /// progress in <c>pi.live.nestedTools</c>. Its result returns here instead of entering the transcript. A call that is blocked, invalid,
    /// throws, is interrupted, or is aborted returns an <see cref="NestedToolExecutionResult.IsError"/> result.
    /// <para>The nested call's key names it within this call; its call ID is <c>&lt;callId&gt;/&lt;key&gt;</c>. By default the key is the
    /// call's position among this invocation's nested calls, <c>1</c>, <c>2</c>, ... An explicit key must be non-empty, without <c>/</c>, not
    /// <c>__proto__</c>, and not a positive integer. A rerun of a replay-safe tool that makes a call with a used key gets that nested call back,
    /// finished or still running, instead of starting another. Reusing a key with another tool or other arguments throws. A tool that is not
    /// replay-safe never reruns: after a restart, its unfinished nested calls are abandoned (<see cref="TaskOptions.AbandonOnRestart"/>).</para>
    /// <para>Nested calls run as soon as they are made; cancelling <paramref name="context"/> stops only the wait. When this call settles,
    /// nested calls it left running are aborted first. Rejects once <c>Execute</c> has returned, when this call is aborted, or when the Harness
    /// closes.</para>
    /// </summary>
    Task<NestedToolExecutionResult> ExecuteToolAsync(string name, JsonObjectValue arguments, DurableContext context, NestedCallOptions? options = null);
}

/// <summary>Runs a tool with its validated arguments.</summary>
public delegate Task<ToolExecutionResult> ToolExecute(JsonValue arguments, IToolExecutionApi api, DurableContext context);

/// <summary>
/// An executable tool registered in a registry (upstream <c>ToolRegistration</c>). Only the pi-ai <c>Tool</c> fields (name, description,
/// parameters) enter the transcript. A host may derive its own record type with extra fields; <c>with</c> keeps the derived type.
/// </summary>
public record ToolRegistration
{
    /// <summary><c>{"type":"object","properties":{}}</c>, the schema of a tool without parameters.</summary>
    public static JsonObjectValue EmptyParameters { get; } = (JsonObjectValue)JsonValue.Parse("""{"type":"object","properties":{}}""");

    /// <summary>The tool name.</summary>
    public required string Name { get; init; }
    /// <summary>The description shown to the model.</summary>
    public required string Description { get; init; }
    /// <summary>JSON Schema of the arguments: any JSON Schema object, which the Harness validates and coerces them against (pi-schema)
    /// before <see cref="Execute"/>.</summary>
    public JsonObjectValue Parameters { get; init; } = EmptyParameters;
    /// <summary>pi-ai <c>Tool.constrainedSampling</c> (wire JSON: <c>false</c> or a <c>ConstrainedSamplingConfig</c> such as
    /// <c>{type: "grammar", variants: {...}}</c>): how providers that support it constrain the tool's input. Enters the transcript with the
    /// declaration; null leaves it out.</summary>
    public JsonValue? ConstrainedSampling { get; init; }
    /// <summary>Whether an interrupted execution may rerun on recovery. Default <see cref="ToolReplay.Unsafe"/>.</summary>
    public ToolReplay? Replay { get; init; }
    /// <summary>Default: the settings' <c>ToolExecution</c>. One sequential call makes its whole round sequential.</summary>
    public ToolExecutionMode? ExecutionMode { get; init; }
    /// <summary>How <see cref="Parameters"/> was built. Up to Pi v1.2.0 it decided how arguments were converted and coerced before
    /// validation; since Pi v1.5.0 every schema is validated and coerced the same way (pi-schema), so it no longer changes a result. Default
    /// <see cref="PiSharp.Contracts.ToolSchemaOrigin.TypeBox"/>.</summary>
    public PiSharp.Contracts.ToolSchemaOrigin ParametersOrigin { get; init; } = PiSharp.Contracts.ToolSchemaOrigin.TypeBox;
    /// <summary>
    /// Repairs arguments models commonly get wrong before validation, such as a JSON string where an array belongs. Must be pure: it runs
    /// again when a call is retried before its intent is recorded. Its result is still validated against <see cref="Parameters"/>; a throw
    /// makes the arguments invalid.
    /// </summary>
    public Func<JsonValue, JsonValue>? PrepareArguments { get; init; }
    /// <summary>JSON Schema (any JSON Schema object, checked with pi-schema) of <see cref="ToolExecutionResult.StructuredOutput"/>, which
    /// programs that call the tool receive.
    /// Null: they receive the result's bounded output, success or error, as <see cref="NestedToolExecutionResult.StructuredOutput"/>
    /// describes.</summary>
    public JsonObjectValue? StructuredOutputSchema { get; init; }
    /// <summary>Who may call the tool: the model, other tools through <see cref="IToolExecutionApi.ExecuteToolAsync"/>, or both (null, the
    /// default).</summary>
    public ImmutableArray<ToolCaller>? Callers { get; init; }
    /// <summary>Not offered to the model until loaded: the model gets the tool only when <c>modelTools</c> names it, in its array or its
    /// <c>add</c>, which <c>control.addTools</c> writes. Other tools can call it any time.</summary>
    public bool Deferred { get; init; }
    /// <summary>Output bounds.</summary>
    public ToolOutputLimits? OutputLimits { get; init; }
    /// <summary>Runs the tool.</summary>
    public required ToolExecute Execute { get; init; }
}

// ─── Prompt sections, hooks, wraps, extensions, registries ──────────────

/// <summary>Input to system prompt section rendering for one request preparation (upstream <c>PromptInput</c>).</summary>
public sealed record PromptInput
{
    /// <summary>The conversation.</summary>
    public required ConversationId ConversationId { get; init; }
    /// <summary>The request's resolution; its tools are the tools offered in this request.</summary>
    public required Agent Agent { get; init; }
    /// <summary>Built by <c>HarnessOptions.Env</c> for this preparation; null without an environment.</summary>
    public IExecutionEnv? Env { get; init; }
    /// <summary>Sections already in effect after replaying the active transcript.</summary>
    public required IReadOnlyDictionary<string, string> Shown { get; init; }
    /// <summary>Committed document reads.</summary>
    public required IDocumentReader Read { get; init; }
}

/// <summary>Renders one section; null renders nothing.</summary>
public delegate ValueTask<string?> PromptRender(PromptInput input, DurableContext context);

/// <summary>One system prompt section; the agent's sections render in order before each request (upstream <c>PromptSection</c>).</summary>
public sealed record PromptSection
{
    /// <summary>The section key.</summary>
    public required string Key { get; init; }
    /// <summary>Renders the text.</summary>
    public required PromptRender Render { get; init; }
    /// <summary>Default true (null): wrap the text as <c>&lt;key&gt;\n...\n&lt;/key&gt;</c>.</summary>
    public bool? Tag { get; init; }
}

/// <summary>Hook handlers for tasks with one name (upstream <c>HookRegistration</c>, built by <c>Define.Hook</c>). The handlers object is
/// the task's hook map type, such as <see cref="ToolHooks"/>.</summary>
public sealed record HookRegistration(string Task, object Handlers);

/// <summary>Targets a tool name or a section key wherever the wrapping extension is selected; wrappers are pure (upstream <c>Wrap</c>).</summary>
public abstract record Wrap
{
    private protected Wrap() { }
}

/// <summary>Wraps the tool named <paramref name="Tool"/> (upstream <c>wrapTool</c>).</summary>
public sealed record ToolWrap(string Tool, Func<ToolRegistration, ToolRegistration> Apply) : Wrap;

/// <summary>Wraps the section <paramref name="Section"/> (upstream <c>wrapSection</c>).</summary>
public sealed record SectionWrap(string Section, Func<PromptSection, PromptSection> Apply) : Wrap;

/// <summary>A named bundle of code, installed in a registry and selected by conversations by name (upstream <c>Extension</c>). Compared
/// by reference, as upstream compares objects.</summary>
public sealed class Extension
{
    /// <summary>The name; unique within a registry.</summary>
    public required string Name { get; init; }
    /// <summary>Tools; names are unique within the extension.</summary>
    public IReadOnlyList<ToolRegistration>? Tools { get; init; }
    /// <summary>Prompt sections; keys are unique within the extension.</summary>
    public IReadOnlyList<PromptSection>? Sections { get; init; }
    /// <summary>Hook registrations.</summary>
    public IReadOnlyList<HookRegistration>? Hooks { get; init; }
    /// <summary>Applied where this extension is selected, in order.</summary>
    public IReadOnlyList<Wrap>? Wraps { get; init; }
    /// <summary>Resolved by name for every task, whichever conversations select this extension.</summary>
    public IReadOnlyList<DurableTask>? Tasks { get; init; }
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>An installed tool with its extension.</summary>
public readonly record struct RegisteredTool(Extension Extension, ToolRegistration Tool);

/// <summary>An installed section with its extension.</summary>
public readonly record struct RegisteredSection(Extension Extension, PromptSection Section);

/// <summary>Immutable view of one published registry state (upstream <c>RegistrySnapshot</c>).</summary>
public interface IRegistrySnapshot
{
    /// <summary>Installed extensions, in install order.</summary>
    IReadOnlyList<Extension> Installed();
    /// <summary>The installed extension with this name.</summary>
    Extension? GetExtension(string name);
    /// <summary>Every installed tool with its extension, in install order. Names may repeat across extensions.</summary>
    IReadOnlyList<RegisteredTool> Tools();
    /// <summary>Every installed section with its extension, in install order.</summary>
    IReadOnlyList<RegisteredSection> Sections();
    /// <summary>Built-in and installed task definitions.</summary>
    IReadOnlyList<DurableTask> Tasks();
    /// <summary>The task definition registered under this name (upstream <c>task(name)</c>).</summary>
    DurableTask? GetTask(string name);
}

/// <summary>Read side of a registry consumed by a Harness (upstream <c>RegistryReader</c>).</summary>
public interface IRegistryReader
{
    /// <summary>Immutable view of the whole current registry.</summary>
    IRegistrySnapshot Snapshot();
    /// <summary>Called synchronously after every publication; wakes the scheduler to reconsider blocked tasks. Dispose to unsubscribe.</summary>
    IDisposable Subscribe(Action listener);
}

/// <summary>Application-owned registry of extensions (upstream <c>Registry</c>).</summary>
public interface IRegistry : IRegistryReader
{
    /// <summary>Installs <paramref name="extension"/>, or replaces the installed extension with its name in place. Publishes at once.</summary>
    void Install(Extension extension);
    /// <summary>Removes the installed extension with <paramref name="extension"/>'s name, whichever object it is. A later install
    /// appends.</summary>
    void Uninstall(Extension extension);
}

// ─── Settings ───────────────────────────────────────────────────────────

/// <summary>Curated pi-ai request options; absent fields use pi-ai defaults (upstream <c>ConversationStreamOptions</c>).</summary>
public sealed record ConversationStreamOptions
{
    /// <summary>Preferred transport (pi-ai <c>Transport</c>).</summary>
    public string? Transport { get; init; }
    /// <summary>HTTP request timeout.</summary>
    public double? TimeoutMs { get; init; }
    /// <summary>Provider/SDK retries inside one request attempt.</summary>
    public int? MaxRetries { get; init; }
    /// <summary>Longest server-requested retry delay to wait for.</summary>
    public double? MaxRetryDelayMs { get; init; }
    /// <summary>Extra request headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>Request metadata.</summary>
    public JsonObjectValue? Metadata { get; init; }
    /// <summary>Prompt cache retention (pi-ai <c>CacheRetention</c>).</summary>
    public string? CacheRetention { get; init; }
    /// <summary>Deferred responses: <c>true</c>, or <c>{window}</c> (wire JSON).</summary>
    public JsonValue? Deferred { get; init; }
}

/// <summary>Durable generation attempt retries; the JSON shape of pi-ai <c>RetryPolicy</c> (upstream <c>ConversationRetryPolicy</c>).</summary>
public sealed record ConversationRetryPolicy(bool Enabled, int MaxRetries, double BaseDelayMs, double? MaxAgentDelayMs = null);

/// <summary>Automatic compaction thresholds (spec §8.7); manual compaction ignores <see cref="Enabled"/> (upstream
/// <c>CompactionPolicy</c>).</summary>
public sealed record CompactionPolicy(bool Enabled, double ReserveTokens, double KeepRecentTokens, double BackgroundTokens)
{
    /// <summary>
    /// In <see cref="Settings.Compaction"/>: the host's per-model entries, keyed by exact <c>"provider/modelId"</c> (upstream
    /// <c>Settings.compaction.modelOverrides</c>). The readers that know their model apply its entry over this policy, field by field; null
    /// in a policy resolved for one model.
    /// </summary>
    public IReadOnlyDictionary<string, CompactionOverride>? ModelOverrides { get; init; }
}

/// <summary>One model's compaction fields (upstream <c>Partial&lt;CompactionPolicy&gt;</c> in <c>modelOverrides</c>); each field it sets
/// replaces the harness-wide one for that model.</summary>
public sealed record CompactionOverride
{
    /// <summary>Threshold and overflow compaction.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Room kept free for the answer.</summary>
    public double? ReserveTokens { get; init; }
    /// <summary>Approximate size of the recent context a summary keeps verbatim.</summary>
    public double? KeepRecentTokens { get; init; }
    /// <summary>Background compaction starts this far below the blocking threshold; 0 disables it.</summary>
    public double? BackgroundTokens { get; init; }
}

/// <summary>
/// How often running progress is committed, and how long it may wait in memory before it is stored (upstream <c>ProgressPolicy</c>).
/// Progress commits are deferred (<see cref="CommitPersistence"/>): watches see each at once, and Storage gets them together, at the run's
/// next phase boundary or after <paramref name="PersistWithinMs"/>. A host can commit less often, so live answers and tool output appear in
/// larger steps.
/// </summary>
/// <param name="PartialIntervalMs">Minimum pause between commits of the answer being generated.</param>
/// <param name="OutputIntervalMs">Minimum pause between commits of running tool output; large commits also pause in proportion to their
/// size.</param>
/// <param name="PersistWithinMs">How long these commits, and other deferred commits without their own <see cref="CommitPersistence.WithinMs"/>,
/// may stay in memory before they are stored; a crash loses at most about this much of an answer or of tool output. Default 1,000;
/// <see cref="double.PositiveInfinity"/> stores them only at the next commit that reads or writes their document and at close.</param>
public sealed record ProgressPolicy(double PartialIntervalMs, double OutputIntervalMs, double PersistWithinMs);

/// <summary>Host overrides of <see cref="ConversationRetryPolicy"/> (upstream <c>Partial&lt;ConversationRetryPolicy&gt;</c>).</summary>
public sealed record RetrySettings
{
    /// <summary>Whether retries are enabled.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Maximum retries.</summary>
    public int? MaxRetries { get; init; }
    /// <summary>Base delay.</summary>
    public double? BaseDelayMs { get; init; }
    /// <summary>Longest delay.</summary>
    public double? MaxAgentDelayMs { get; init; }
}

/// <summary>Harness-wide host overrides of <see cref="CompactionPolicy"/> (upstream <c>CompactionSettings</c>).</summary>
public sealed record CompactionSettings
{
    /// <summary>Threshold and overflow compaction.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Room kept free for the answer.</summary>
    public double? ReserveTokens { get; init; }
    /// <summary>Approximate size of the recent context a summary keeps verbatim.</summary>
    public double? KeepRecentTokens { get; init; }
    /// <summary>Background compaction starts this far below the blocking threshold; 0 disables it.</summary>
    public double? BackgroundTokens { get; init; }
    /// <summary>
    /// Per-model entries keyed by exact <c>"provider/modelId"</c>; each field an entry sets replaces the one above for that model, so small-
    /// and large-window models can share a harness. The conversation's current model picks the entry at the threshold check and when a
    /// compaction selects what to summarize, and the request's model after a context overflow.
    /// </summary>
    public IReadOnlyDictionary<string, CompactionOverride>? ModelOverrides { get; init; }
}

/// <summary>Host overrides of <see cref="ProgressPolicy"/>.</summary>
public sealed record ProgressSettings
{
    /// <summary>Minimum pause between commits of the answer being generated.</summary>
    public double? PartialIntervalMs { get; init; }
    /// <summary>Minimum pause between commits of running tool output.</summary>
    public double? OutputIntervalMs { get; init; }
    /// <summary>How long progress commits may stay in memory before they are stored (<see cref="ProgressPolicy.PersistWithinMs"/>); 0 stores
    /// each at once. Anything that is not a number of milliseconds, 0 or more (NaN, a negative value) means the default, 1,000.</summary>
    public double? PersistWithinMs { get; init; }
}

/// <summary>
/// Harness-wide run policy (upstream <c>HarnessSettings</c>). Read at every resolution and never copied: a host may derive from this class
/// and override a property's getter. Synchronous, because some readers run on the Session line.
/// </summary>
public class HarnessSettings
{
    /// <summary>Default extension selection; null: every installed extension, in install order.</summary>
    public virtual IReadOnlyList<Extension>? Extensions { get; init; }
    /// <summary>Request options.</summary>
    public virtual ConversationStreamOptions? Stream { get; init; }
    /// <summary>Retry overrides.</summary>
    public virtual RetrySettings? Retry { get; init; }
    /// <summary>Compaction overrides.</summary>
    public virtual CompactionSettings? Compaction { get; init; }
    /// <summary>Progress overrides.</summary>
    public virtual ProgressSettings? Progress { get; init; }
    /// <summary>Default tool execution mode.</summary>
    public virtual ToolExecutionMode? ToolExecution { get; init; }
    /// <summary>How many steers a boundary places.</summary>
    public virtual QueueMode? SteeringMode { get; init; }
    /// <summary>How many follow-ups a final boundary places.</summary>
    public virtual QueueMode? FollowUpMode { get; init; }
    /// <summary>How long an idle conversation keeps its last context read in memory; 0 drops it once the conversation is idle.</summary>
    public virtual double? ContextRetentionMs { get; init; }
    /// <summary>
    /// How long a loaded document that no watch observes stays in memory after its last read or write; the next access
    /// loads it from Storage. Observed documents always stay. Default ten minutes; 0 drops one right after use;
    /// <see cref="double.PositiveInfinity"/> keeps them. Anything else that is not a number of milliseconds (NaN, a negative value) means the
    /// default.
    /// </summary>
    public virtual double? DocumentRetentionMs { get; init; }
    /// <summary>
    /// What a run that fails (<c>model_error</c>, <c>no_model</c>, <c>interrupted</c>, <c>restart_limit</c>, <c>nothing_to_answer</c>,
    /// <c>turn_limit</c>) does with the inputs queued behind it: <see cref="Durable.AfterFailedRun.Keep"/> leaves them queued until the next input or queued write,
    /// <see cref="Durable.AfterFailedRun.Continue"/> starts the next run with them, as an answer would, and <see cref="Durable.AfterFailedRun.Withdraw"/> settles them <c>unanswered</c> with
    /// reason <c>run_failed</c>. Default <see cref="Durable.AfterFailedRun.Keep"/>.
    /// </summary>
    public virtual AfterFailedRun? AfterFailedRun { get; init; }
    /// <summary>
    /// How many times in a row a task may be cut off by a crash, eviction or reset (a close does not count) without changing its state before
    /// it ends instead of running again: a tool call with an error result, a compaction failed into <c>pi.live.compactionFailure</c>, a run
    /// with <c>pi.live.runFailure</c> reason <c>restart_limit</c>, any other task <c>faulted</c> with detail <c>{reason: "restart_limit"}</c>.
    /// A generation's request or poll the host died during is a failed attempt under <see cref="Retry"/> first; the limit bounds it only when
    /// charging that attempt keeps being cut off. A number of restarts, 1 or more, rounded down; anything else means the default, 5.
    /// <see cref="double.PositiveInfinity"/> never ends a task for it.
    /// </summary>
    public virtual double? RestartLimit { get; init; }
}

/// <summary>What a failed run does with the inputs queued behind it (<see cref="HarnessSettings.AfterFailedRun"/>; upstream
/// <c>AfterFailedRun</c>): a run that ends with <c>model_error</c>, <c>no_model</c>, <c>interrupted</c>, <c>restart_limit</c>,
/// <c>nothing_to_answer</c>, or <c>turn_limit</c>.</summary>
public enum AfterFailedRun
{
    /// <summary><c>keep</c>: they stay queued until the next input or queued write.</summary>
    Keep,
    /// <summary><c>continue</c>: the next run starts with them, as after an answer.</summary>
    Continue,
    /// <summary><c>withdraw</c>: they settle <c>unanswered</c> with reason <c>run_failed</c>.</summary>
    Withdraw,
}

/// <summary>Resolved settings: every field over its built-in default, object fields merged (upstream <c>Settings</c>).</summary>
public sealed record Settings
{
    /// <summary>Null: every installed extension, in install order.</summary>
    public IReadOnlyList<Extension>? Extensions { get; init; }
    /// <summary>Request options.</summary>
    public required ConversationStreamOptions Stream { get; init; }
    /// <summary>Retry policy.</summary>
    public required ConversationRetryPolicy Retry { get; init; }
    /// <summary>Harness-wide compaction policy over the defaults, with the host's per-model overrides, which apply where the model is
    /// known.</summary>
    public required CompactionPolicy Compaction { get; init; }
    /// <summary>Progress policy.</summary>
    public required ProgressPolicy Progress { get; init; }
    /// <summary>Default tool execution mode.</summary>
    public required ToolExecutionMode ToolExecution { get; init; }
    /// <summary>Steering queue mode.</summary>
    public required QueueMode SteeringMode { get; init; }
    /// <summary>Follow-up queue mode.</summary>
    public required QueueMode FollowUpMode { get; init; }
    /// <summary>Idle context retention.</summary>
    public required double ContextRetentionMs { get; init; }
    /// <summary>Unobserved document retention.</summary>
    public required double DocumentRetentionMs { get; init; }
    /// <summary>What a failed run does with the inputs queued behind it.</summary>
    public AfterFailedRun AfterFailedRun { get; init; }
    /// <summary>How many times in a row a task may be cut off before it ends (<see cref="HarnessSettings.RestartLimit"/>): a whole number, 1
    /// or more, or <see cref="double.PositiveInfinity"/>.</summary>
    public double RestartLimit { get; init; } = Agents.DefaultRestartLimit;
}

/// <summary>What <c>HarnessOptions.Env</c> builds an environment for (upstream <c>EnvTarget</c>).</summary>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Cwd">The conversation's agent <c>cwd</c>.</param>
/// <param name="Read">Committed document reads.</param>
public sealed record EnvTarget(ConversationId ConversationId, string? Cwd, IDocumentReader Read);

/// <summary>Options of <see cref="Harness.OpenAsync"/> (upstream <c>HarnessOptions</c>).</summary>
public sealed record HarnessOptions
{
    /// <summary>
    /// The host-owned adapter of the content-free admission and phase telemetry (upstream <c>telemetry</c>; <see cref="DurableTelemetry.Schema"/>):
    /// public submissions start <c>pi.durable.submit</c> spans under it, and task phases <c>pi.durable.phase</c> spans under their stored
    /// trace, or under it. Its propagation and metrics are optional: without propagation no trace carrier is stored on a record. Null: the
    /// no-op context, which records and stores nothing. Spans, events and metrics never carry content, only record IDs and fixed categories.
    /// </summary>
    public PiSharp.Telemetry.ITelemetryContext? Telemetry { get; init; }
    /// <summary>pi-ai model access used by generation, compaction, hooks and tools. Set this or <see cref="CurrentModels"/>.</summary>
    public IDurableModels? Models { get; init; }
    /// <summary>
    /// A function returning the current model access, called at each use (generation and compaction requests,
    /// <c>ITaskRuntime.Models</c>, and <c>Models</c> of hooks and tools at each access), so a host can replace its model configuration
    /// without reopening the Harness (upstream's <c>models</c> as a function). Set this or <see cref="Models"/>.
    /// </summary>
    public Func<IDurableModels>? CurrentModels { get; init; }
    /// <summary>
    /// How model requests identify the app to providers (upstream <c>identity</c>), merged field by field over
    /// <see cref="Harness.DefaultIdentity"/>. It applies to every chat request through the Harness's models, including those of hooks and
    /// tools (<see cref="DurableStreamOptions.Identity"/>), and overrides the identity the models were created with; a request's own
    /// identity overrides it. PiSharp's default sends no attribution (see <see cref="Harness.DefaultIdentity"/>).
    /// </summary>
    public ClientIdentity? Identity { get; init; }
    /// <summary>The registry; it may keep changing while the Harness runs.</summary>
    public required IRegistryReader Registry { get; init; }
    /// <summary>Harness-wide run policy, read at each use. Set this or <see cref="CurrentSettings"/>, or neither.</summary>
    public HarnessSettings? Settings { get; init; }
    /// <summary>
    /// A function returning the current run policy, or null for the defaults, called at each use as <see cref="Settings"/> is read (upstream's
    /// <c>settings</c> as a function), so a host can replace its settings without reopening the Harness. Set this or <see cref="Settings"/>.
    /// </summary>
    public Func<HarnessSettings?>? CurrentSettings { get; init; }
    /// <summary>Builds a conversation's environment at each use. Never called on the Session line.</summary>
    public Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? Env { get; init; }
    /// <summary>
    /// Runs in every commit that creates or forks a conversation, raw <c>tx.CreateConversationAsync()</c> included, after the built-in
    /// <c>pi.*</c> documents and before the conveniences apply <c>agent</c> and run <c>init</c>. A fork already has its copies. Table reads
    /// throw <see cref="ReadAfterWriteException"/>; a throw fails the creating commit.
    /// </summary>
    public Func<ITx, ConversationRecord, Task>? ConversationCreated { get; init; }
    /// <summary>The Harness clock in milliseconds; default <see cref="TimeProvider"/>'s wall clock. Sleeping tasks store deadlines on it, so
    /// every process that opens the same storage must use the same clock.</summary>
    public Func<double>? Now { get; init; }
    /// <summary>The default clock, and the default timers of <see cref="SetTimer"/> (a .NET seam for upstream's <c>Date.now</c> and
    /// <c>setTimeout</c>); default <see cref="System.TimeProvider.System"/>. A fake one drives both, as fake timers do.</summary>
    public TimeProvider? TimeProvider { get; init; }
    /// <summary>
    /// Arms the timers of the Session, the scheduler, and its tasks: pacing progress commits, freeing idle memory (background), and, without
    /// <see cref="OnWake"/>, resuming sleeping tasks; and the timers of <see cref="ITaskRuntime.SetTimer"/>. The environment, file watcher
    /// and image worker keep their own timers. Default: <see cref="TimeProvider"/>'s timers, which never keep the process alive and never
    /// decline a background timer. Tests can pass fake timers; a host can keep timers off a process that should hibernate.
    /// </summary>
    public SetTimer? SetTimer { get; init; }
    /// <summary>
    /// Wakes the process for sleeping tasks instead of a <see cref="SetTimer"/> timer. Without it, while scheduling runs, a timer resumes
    /// sleeping tasks when they are due. With it, the Harness arms no timer for them: it calls <c>OnWake</c> after open and whenever
    /// <see cref="IHarness.Wake"/> changes, and the host arms its own wakeup and calls <see cref="IHarness.Resume"/> when it fires. Called on
    /// the thread pool, never on the Session line, after the commit that changed the value was adopted, so a host arms a recovery wakeup
    /// before calls that can start work; one call at a time. It may call <c>Wake</c> and <c>Resume</c>, and must not block. A throw is
    /// reported, and the value is offered again at the next change.
    /// </summary>
    public Action<Wake>? OnWake { get; init; }
    /// <summary>
    /// Receives failures that do not fail the calling operation, such as a throwing hook or listener, a task that faulted, and the error that
    /// fails the Session, once: one <see cref="Durable.Report"/>, the error and what happened (its <see cref="ReportInfo"/>, by kind). A host
    /// can tell a failed Session (<see cref="ReportKind.StorageFailed"/>, often a platform reset) from a faulted task
    /// (<see cref="ReportKind.TaskFaulted"/>). Its own throw is dropped. (An <c>async void</c> handler's exception escapes to the thread pool,
    /// which no caller can contain.)
    /// </summary>
    public Action<Report>? OnReport { get; init; }
    /// <summary>
    /// Opens for reading only, as a viewer of a session that another process drives, for example a synced copy on a device that only shows
    /// it (upstream <c>readOnly</c>). Open recovers nothing: a <c>running</c> task stays as committed. Every commit with writes throws
    /// <see cref="ReadOnlySessionException"/> before it reaches Storage, and so do <see cref="IHarness.Resume"/> and every call that enables
    /// scheduling; the Harness stays usable for reads and watches. <see cref="IHarness.Wake"/> is always inactive, and <see cref="OnWake"/> is
    /// never called. Reopen without it to write. Unrelated to the device host's read-only agent switch, which removes writing tools from an
    /// agent that still runs.
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// When a Session next needs a running process (<see cref="IHarness.Wake"/>; upstream <c>Wake</c>). <see cref="Active"/>: some task can run
/// now or is running, so a process that dies must be woken again soon, for example by a recovery alarm. <see cref="At"/>: the earliest
/// sleep not yet due, when there is one, on the <see cref="HarnessOptions.Now"/> clock. Neither: nothing needs a process until new work is
/// submitted. Tasks blocked on a missing or unmigratable definition, <c>completing</c> tasks, and waits on live tasks do not count; the
/// work they wait for does.
/// </summary>
public sealed record Wake(bool Active, double? At = null);

// ─── Inspection and context ─────────────────────────────────────────────

/// <summary>Scheduling state of a Harness.</summary>
public enum HarnessScheduling
{
    /// <summary>Not resumed yet.</summary>
    Paused,
    /// <summary>Scheduling tasks.</summary>
    Running,
    /// <summary>Closing.</summary>
    Closing,
}

/// <summary>What the scheduler would do with a live task (upstream <c>TaskInspection["state"]["kind"]</c>).</summary>
public enum TaskInspectionKind
{
    /// <summary>An invocation is active.</summary>
    Running,
    /// <summary>The next scheduling pass reserves it (upstream <c>pending</c>, TaskState's word; v1.2.0's <c>ready</c>).</summary>
    Pending,
    /// <summary>It waits for live tasks.</summary>
    Waiting,
    /// <summary>It sleeps until the Harness clock reaches <see cref="TaskInspectionState.Until"/>.</summary>
    Sleeping,
    /// <summary>Its outcome is held until its ordinary owned work drains.</summary>
    Completing,
    /// <summary>Cannot run here: no registered definition can take it (aborting it settles it as <c>orphaned</c>).</summary>
    Blocked,
}

/// <summary>Why no registered definition can take a task. Derived, never persisted.</summary>
public enum BlockedReason
{
    /// <summary><c>missing_task</c>: no definition has its kind.</summary>
    MissingTask,
    /// <summary><c>task_too_old</c>: the definition is older than the record.</summary>
    TaskTooOld,
    /// <summary><c>migration_failed</c>: the migration threw or does not exist.</summary>
    MigrationFailed,
}

/// <summary>The derived state of a live task (upstream <c>TaskInspection["state"]</c>).</summary>
public sealed record TaskInspectionState(TaskInspectionKind Kind)
{
    /// <summary>For <see cref="TaskInspectionKind.Pending"/>: its definition is newer and has a migration.</summary>
    public bool Migrates { get; init; }
    /// <summary>For <see cref="TaskInspectionKind.Waiting"/>: the live tasks it waits for.</summary>
    public ImmutableArray<TaskId> On { get; init; } = [];
    /// <summary>For <see cref="TaskInspectionKind.Sleeping"/>: when it is due, on the Harness clock.</summary>
    public double? Until { get; init; }
    /// <summary>For <see cref="TaskInspectionKind.Blocked"/>: why.</summary>
    public BlockedReason? Reason { get; init; }
    /// <summary>For a failed migration: the error.</summary>
    public Exception? Error { get; init; }

    /// <summary>The persisted spelling of <see cref="Reason"/> (<c>missing_task</c>, <c>task_too_old</c>, <c>migration_failed</c>).</summary>
    public static string ReasonName(BlockedReason reason) => reason switch
    {
        BlockedReason.MissingTask => "missing_task",
        BlockedReason.TaskTooOld => "task_too_old",
        _ => "migration_failed",
    };
}

/// <summary>A live task and what the scheduler would do with it under the current registry (upstream <c>TaskInspection</c>).</summary>
public sealed record TaskInspection(TaskRecord Record, TaskInspectionState State);

/// <summary>Point-in-time view of live work: unfinished tasks and submissions, read on the Session line (upstream
/// <c>HarnessInspection</c>).</summary>
public sealed record HarnessInspection(HarnessScheduling Scheduling, ImmutableArray<TaskInspection> Tasks, ImmutableArray<SubmissionRecord> Submissions);

/// <summary>Raw active transcript and derived model context (upstream <c>ContextView</c>). Messages are pi-ai wire JSON.</summary>
public sealed record ContextView
{
    /// <summary>An empty view.</summary>
    public static ContextView Empty { get; } = new() { Entries = [], Contributions = [], Messages = [] };
    /// <summary>Newest applicable head marker, if any.</summary>
    public EntryRecord? Head { get; init; }
    /// <summary>Raw active entries: the head marker followed by non-head entries from its head through the tail.</summary>
    public required ImmutableArray<EntryRecord> Entries { get; init; }
    /// <summary>Per entry of <see cref="Entries"/>, its model messages after edits and excluded stop reasons, before tool result ordering.</summary>
    public required ImmutableArray<ImmutableArray<JsonValue>> Contributions { get; init; }
    /// <summary>Model context for the next provider request.</summary>
    public required ImmutableArray<JsonValue> Messages { get; init; }
}

// ─── Conversations and the Harness ──────────────────────────────────────

/// <summary>Runs inside the creating commit, after the creation hook and the <c>agent</c> change (upstream <c>ConversationInit</c>). The
/// conversation creation is already a table write, so table reads throw <see cref="ReadAfterWriteException"/>.</summary>
public delegate Task ConversationInit(ITx tx, ConversationId conversationId);

/// <summary>Options of conversation creation and forks (upstream <c>ConversationCreateOptions</c>).</summary>
public sealed record ConversationCreateOptions(ConversationOwnership Ownership)
{
    /// <summary>Applied in the creating commit after the creation hook's copy, before <see cref="Init"/>.</summary>
    public AgentChange? Agent { get; init; }
    /// <summary>Runs last in the creating commit.</summary>
    public ConversationInit? Init { get; init; }
}

/// <summary>Options of conversation abort (upstream <c>ConversationAbortOptions</c>).</summary>
public sealed record ConversationAbortOptions
{
    /// <summary>
    /// Cross background boundaries: mark every live task reached ignoring the background flag when the abort is admitted, withdraw the
    /// queued inputs of every conversation reached, and wait until those tasks are terminal and the conversation is ordinarily idle.
    /// </summary>
    public bool Background { get; init; }
}

/// <summary>What an idle conversation (one without a run) does with user input (upstream <c>whenIdle</c>); without it, idle input starts a
/// run.</summary>
public enum WhenIdle
{
    /// <summary><c>reject</c>: rejected with <see cref="ConversationIdleException"/> and nothing is written, so the input never starts a run of
    /// its own. With <see cref="WhenBusy.Steer"/> it steers the current run or is refused, in one admission; a run that answered and waits for
    /// its <c>afterRun</c> tasks is still busy, and such an input queues and starts the next run.</summary>
    Reject,
}

/// <summary>When a busy conversation queues user input.</summary>
public enum WhenBusy
{
    /// <summary><c>steer</c>: placed at the next boundary.</summary>
    Steer,
    /// <summary><c>followUp</c>: placed at the final boundary.</summary>
    FollowUp,
    /// <summary><c>reject</c>: rejected with <see cref="ConversationBusyException"/>.</summary>
    Reject,
}

/// <summary>
/// The entry an input is placed as, instead of <c>pi.user</c> (upstream <c>InputEntry</c>): an application kind, and data the transcript
/// shows, such as its author. Its model message is still the input's user message, so it has no model, head or edits of its own.
/// </summary>
/// <param name="Kind">The entry kind; it must be non-empty and not start with <c>pi.</c>.</param>
/// <param name="Data">The entry data; null omits it (JSON <c>null</c> is <see cref="JsonValue.Null"/>).</param>
public sealed record InputEntry(string Kind, JsonValue? Data = null);

/// <summary>A host submission: user input that may start a run, a passive entry write, or a continue: a run without a new user message, for
/// an idle conversation whose context the model still owes an answer (it ends in a user message or tool results, for example after a run
/// that ended without an answer) (upstream <c>SubmissionDraft</c>). A request ID deduplicates within one conversation before any write;
/// reusing one for another submission type rejects.</summary>
public sealed record SubmissionDraft
{
    private SubmissionDraft() { }
    /// <summary>User input (pi-ai <c>UserMessage["content"]</c>, wire JSON), placed as <paramref name="entry"/> when given, else as
    /// <c>pi.user</c> without data. <paramref name="whenIdle"/> <see cref="Durable.WhenIdle.Reject"/> refuses it on an idle
    /// conversation.</summary>
    public static SubmissionDraft Input(JsonValue content, WhenBusy? whenBusy = null, string? requestId = null, InputEntry? entry = null,
        WhenIdle? whenIdle = null) =>
        new() { Type = SubmissionType.Input, Content = content, WhenBusy = whenBusy, WhenIdle = whenIdle, RequestId = requestId, InputEntry = entry };
    /// <summary>A passive entry write.</summary>
    public static SubmissionDraft Write(EntryDraft entry, string? requestId = null) =>
        new() { Type = SubmissionType.Write, Entry = entry, RequestId = requestId };
    /// <summary>
    /// Starts a run on the context as it is, without a user message (upstream <c>{ type: "continue" }</c>). Only when idle: a busy conversation
    /// rejects it with <see cref="ConversationBusyException"/>, and it never queues. Items kept in <c>pi.inbox</c> are placed first and join
    /// the run. A context with nothing to answer (empty, or ending in an answer, also a text answer cut off at its output limit) ends the run
    /// without a request, <c>unanswered</c> with <c>nothing_to_answer</c>. A cut-off answer with tool calls is continued: its calls never ran,
    /// and the context ends in their error results (<c>missing_result</c>).
    /// </summary>
    public static SubmissionDraft Continue(string? requestId = null) => new() { Type = SubmissionType.Continue, RequestId = requestId };
    /// <summary>Input, write or continue.</summary>
    public SubmissionType Type { get; private init; }
    /// <summary>Host deduplication key, scoped to the conversation.</summary>
    public string? RequestId { get; private init; }
    /// <summary>The input content.</summary>
    public JsonValue? Content { get; private init; }
    /// <summary>What a busy conversation does with the input.</summary>
    public WhenBusy? WhenBusy { get; private init; }
    /// <summary>What an idle conversation does with the input (upstream <c>whenIdle</c>); null starts a run.</summary>
    public WhenIdle? WhenIdle { get; private init; }
    /// <summary>The written entry.</summary>
    public EntryDraft? Entry { get; private init; }
    /// <summary>The entry an input is placed as (upstream <c>entry</c> of an input); null places it as <c>pi.user</c>.</summary>
    public InputEntry? InputEntry { get; private init; }
}

/// <summary>Result of aborting a submission.</summary>
public enum SubmissionAbortResult
{
    /// <summary><c>aborted</c>.</summary>
    Aborted,
    /// <summary><c>already_placed</c>.</summary>
    AlreadyPlaced,
    /// <summary><c>settled</c>.</summary>
    Settled,
    /// <summary><c>not_found</c>.</summary>
    NotFound,
}

/// <summary>Awaitable host object for one durably admitted submission (upstream <c>Submission</c>).</summary>
public interface ISubmission
{
    /// <summary>The submission.</summary>
    SubmissionId Id { get; }
    /// <summary>The committed record.</summary>
    Task<SubmissionRecord> StatusAsync(DurableContext context);
    /// <summary>Resolves with the settled record (status <c>done</c> or <c>unanswered</c>; upstream <c>SettledSubmissionRecord</c>); cancelling
    /// <paramref name="context"/> cancels only this wait, and close rejects it.</summary>
    Task<SubmissionRecord> WaitAsync(DurableContext context);
    /// <summary>Withdraws a queued submission: <c>aborted</c>; <c>already_placed</c> for a placed input or continue and <c>settled</c> for a
    /// settled submission.</summary>
    Task<SubmissionAbortResult> AbortAsync(DurableContext context);
}

/// <summary>
/// Invocation-bound conversation operations for tasks and tools (upstream <c>ConversationHandle</c>). Rejects after the invocation ends.
/// <c>SubmitAsync</c> takes any submission, in both forms of <see cref="IConversation.SubmitAsync(SubmissionDraft, DurableContext)"/>: a
/// subagent's conversation is an ordinary one, so a tool may also continue it, and a task may admit a submission in the same commit as its
/// own writes (<c>runtime.CommitAsync()</c>); entries the admission appends there carry the task's <c>byTaskId</c>. The transaction form also
/// rejects once the invocation is aborted.
/// </summary>
public interface IConversationHandle
{
    /// <summary>The conversation.</summary>
    ConversationId Id { get; }
    /// <summary>Admits a submission; the returned submission's operations are bound to the invocation too.</summary>
    Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context);
    /// <summary><see cref="IConversation.SubmitAsync(ITx, SubmissionDraft)"/> inside a commit of the task or tool (<c>runtime.CommitAsync()</c>,
    /// <c>api.CommitAsync()</c>), or any commit of the Harness; rejects once the invocation is aborted or ended.</summary>
    Task<SubmissionId> SubmitAsync(ITx tx, SubmissionDraft submission);
    /// <summary><c>Conversation.AbortAsync()</c>: withdraw queued inputs, abort the ordinary ownership scope, and wait until it is idle.</summary>
    Task AbortAsync(DurableContext context, ConversationAbortOptions? options = null);
    /// <summary>Resolves when the conversation's ordinary ownership scope has no live non-background task.</summary>
    Task WaitForIdleAsync(DurableContext context);
}

/// <summary>
/// What a compaction produced (upstream <c>CompactionResult</c>): the <see cref="EntryId"/> of a blocking compaction's summary, or the
/// <see cref="SubmissionId"/> of a conversation-owned compaction's summary write; both null when nothing was compacted. A completed
/// <c>pi.compaction</c> task's result is its JSON, <c>{entryId}</c>, <c>{submissionId}</c> or <c>{}</c>.
/// </summary>
public sealed record CompactionResult(EntryId? EntryId = null, SubmissionId? SubmissionId = null)
{
    /// <summary><c>{entryId?, submissionId?}</c>.</summary>
    public JsonObjectValue ToJson() => new RecordJson.Writer().Add("entryId", EntryId?.ToJson()).Add("submissionId", SubmissionId?.ToJson()).Build();

    /// <summary>Reads a completed compaction task's result.</summary>
    public static CompactionResult FromJson(JsonValue json)
    {
        var value = RecordJson.Object(json, "compaction result");
        RecordJson.OnlyKeys(value, "compaction result", "entryId", "submissionId");
        return new CompactionResult(
            RecordJson.OptionalInteger(value, "entryId", "compaction result") is { } entry ? new EntryId(entry) : null,
            RecordJson.OptionalInteger(value, "submissionId", "compaction result") is { } submission ? new SubmissionId(submission) : null);
    }
}

/// <summary>Stateless handle for one conversation, bound to the Harness that returned it. Compare handles by <see cref="Id"/> (upstream
/// <c>Conversation</c>).</summary>
public interface IConversation
{
    /// <summary>The conversation.</summary>
    ConversationId Id { get; }

    /// <summary>The agent, resolved with the current registry snapshot and settings.</summary>
    Task<Agent> AgentAsync(DurableContext context);
    /// <summary>Applies <paramref name="change"/> to <c>pi.agent</c> in its own commit.</summary>
    Task ConfigureAsync(AgentChange change, DurableContext context);

    /// <summary>
    /// Durably admits user input, a passive entry write, or a continue. A busy conversation, or one with queued items, queues input and
    /// writes that affect model context in <c>pi.inbox</c>; <see cref="WhenBusy.Reject"/> rejects input with
    /// <see cref="ConversationBusyException"/> instead and writes nothing. An idle conversation rejects <see cref="WhenIdle.Reject"/> input
    /// with <see cref="ConversationIdleException"/> and writes nothing. A write of only <c>Kind</c> and <c>Data</c> (no <c>Model</c>,
    /// <c>Head</c> or <c>Edits</c>) is appended at once, even mid-run, as it changes nothing the model sees. Idle input places its entry
    /// (<c>pi.user</c>, or its <see cref="InputEntry"/>) and starts a run; an idle write appends its entry. A continue starts a run on the
    /// context as it is, after placing queued items; a busy conversation rejects it with <see cref="ConversationBusyException"/>, as it
    /// never queues.
    /// </summary>
    Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context);
    /// <summary>
    /// Admits a submission inside the caller's commit <paramref name="tx"/> (the Harness's <c>CommitAsync()</c>, <see cref="CommitAsync{T}(Func{ITx,
    /// Task{T}}, DurableContext, CommitOptions?)"/>, a task's <c>runtime.CommitAsync()</c>), with the same rules, and returns its ID: the
    /// admission is stored with that commit, or not at all when the callback throws later or the commit fails (upstream
    /// <c>submit(tx, draft)</c>). Call it before the commit's first table write (<c>AppendEntryAsync()</c>, <c>CreateTaskAsync()</c>,
    /// <c>CreateSubmissionAsync()</c>, another admission): otherwise it throws <see cref="ReadAfterWriteException"/>, on every path. Documents
    /// may be read and written before and after it. A rejection (<see cref="ConversationBusyException"/>, <see cref="ConversationIdleException"/>)
    /// throws before any write, so the callback may catch it. Do not call <see cref="ISubmission"/> or other Harness reads inside the callback:
    /// they wait for the commit holding the line. Its <c>pi.durable.submit</c> span covers the admission only, so its <c>result</c> is
    /// provisional. Throws for a transaction of another Session.
    /// </summary>
    Task<SubmissionId> SubmitAsync(ITx tx, SubmissionDraft submission);
    /// <summary>Admits a write of a <c>pi.reset</c> entry that starts a new context, carrying <paramref name="handoff"/> as a user message
    /// when given.</summary>
    Task ResetAsync(string? handoff, DurableContext context);
    /// <summary>
    /// Creates a manual compaction task in its own commit and returns its ID; its result is a <see cref="CompactionResult"/>. The task is
    /// conversation-owned ordinary work: idle waits include it and <see cref="AbortAsync"/> stops it, but it never makes the conversation
    /// busy. Its summary is admitted as a write submission, placed at once when idle and otherwise at the next boundary. Enables scheduling.
    /// </summary>
    Task<TaskId> CompactAsync(string? instructions, DurableContext context);

    /// <summary>A Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation; <paramref name="options"/> as for
    /// <see cref="ISession.CommitAsync{T}(Func{ITx, Task{T}}, DurableContext, CommitOptions?)"/>.</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context, CommitOptions? options = null);
    /// <summary>A Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task CommitAsync(Func<ITx, Task> change, DurableContext context, CommitOptions? options = null);
    /// <summary>A synchronous Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context, CommitOptions? options = null);
    /// <summary>A synchronous Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task CommitAsync(Action<ITx> change, DurableContext context, CommitOptions? options = null);
    /// <summary>
    /// Committed raw active transcript and model context. With <paramref name="at"/>, the context as of that visible entry: the same view
    /// <c>ForkAsync(at)</c> would start with, without creating a conversation.
    /// </summary>
    Task<ContextView> ContextAsync(DurableContext context, EntryId? at = null);
    /// <summary>Fork-aware history of this conversation, newest first unless the query orders ascending. The query's conversation is
    /// ignored.</summary>
    Task<Page<EntryRecord>> EntriesAsync(EntryQueryBounds query, int limit, JsonObjectValue? cursor, DurableContext context);
    /// <summary>Forks this conversation at the visible entry <paramref name="at"/>.</summary>
    Task<IConversation> ForkAsync(EntryId at, ConversationCreateOptions options, DurableContext context);
    /// <summary>
    /// Withdraws queued inputs (queued writes stay), marks every live non-background task of the ordinary ownership scope, signals them,
    /// and resolves once the scope is idle. Background subtrees survive unless <see cref="ConversationAbortOptions.Background"/> is set.
    /// </summary>
    Task AbortAsync(DurableContext context, ConversationAbortOptions? options = null);
    /// <summary>
    /// Resolves when the ordinary ownership scope has no live non-background task: this conversation and the conversations owned,
    /// transitively, by its non-background tasks.
    /// </summary>
    Task WaitForIdleAsync(DurableContext context);
    /// <summary>The structural view (spec §9.3) as a serialized watch, Chord's <see cref="IWatchHandle{T}"/>; commits made while its
    /// listener runs arrive folded; cancelling <paramref name="context"/> ends it aborted. Its <see cref="ConversationView.Entries"/> are a
    /// window over the transcript, as <paramref name="window"/> says: the newest 50 by default, then appends, dropping the oldest past 100;
    /// bounds out of range fault the task with <see cref="ArgumentOutOfRangeException"/>. Built on the same mount as
    /// <see cref="IHarness.ObserveConversationAsync"/>.</summary>
    Task<IWatchHandle<ConversationView>> WatchAsync(DurableContext context, ConversationWindow? window = null);
}

/// <summary>Bounds of <see cref="IConversation.EntriesAsync"/> (upstream <c>Omit&lt;EntryQuery, "conversationId"&gt;</c>).</summary>
public sealed record EntryQueryBounds
{
    /// <summary>Oldest entry ID that may be returned.</summary>
    public EntryId? MinEntryId { get; init; }
    /// <summary>Newest entry ID that may be returned.</summary>
    public EntryId? MaxEntryId { get; init; }
    /// <summary>Default descending.</summary>
    public ScanOrder? Order { get; init; }
}

/// <summary>The durable agent harness over one Session (upstream <c>Harness</c>).</summary>
public interface IHarness : ISession
{
    /// <summary>
    /// Enables task scheduling. Idempotent; throws after close, and <see cref="ReadOnlySessionException"/> on a read-only Harness
    /// (<see cref="HarnessOptions.ReadOnly"/>). Calls that ask for progress enable it too: <c>Conversation.SubmitAsync</c>,
    /// <c>CompactAsync</c>, <c>AbortAsync</c>, <c>Submission.WaitAsync</c>, <see cref="WaitForTaskAsync"/>, <see cref="WaitForIdleAsync"/>
    /// and <c>Conversation.WaitForIdleAsync</c>. Read-only viewers never do. Sleeping tasks that are due become runnable, so a host wakeup,
    /// such as an alarm, calls this.
    /// </summary>
    void Resume();
    /// <summary>
    /// When this Session next needs a running process, from the scheduler's memory: <c>Active</c> while some task can run now or is running,
    /// <c>At</c> for the earliest sleep (upstream <c>wake()</c>). Answers also while scheduling is paused; throws after close. A host that
    /// wakes the process itself arms from it and calls <see cref="Resume"/> when woken; see <see cref="HarnessOptions.OnWake"/>.
    /// </summary>
    Wake Wake();

    /// <summary>Returns the reserved root conversation, creating it with <paramref name="agent"/> and <paramref name="init"/> in one commit
    /// when absent.</summary>
    Task<IConversation> RootAsync(DurableContext context, AgentChange? agent = null, ConversationInit? init = null);
    /// <summary>A handle of an existing conversation; null when absent.</summary>
    Task<IConversation?> ConversationAsync(ConversationId id, DurableContext context);
    /// <summary>Creates a conversation.</summary>
    Task<IConversation> CreateConversationAsync(ConversationCreateOptions options, DurableContext context);

    /// <summary>The committed task record.</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context);
    /// <summary>Live tasks and unsettled submissions. Writes nothing and runs no task code.</summary>
    Task<HarnessInspection> InspectAsync(DurableContext context);
    /// <summary>Reacquires a submission, for example after reopen; null when it does not exist.</summary>
    Task<ISubmission?> SubmissionAsync(SubmissionId id, DurableContext context);
    /// <summary>Aborts a submission; <see cref="SubmissionAbortResult.NotFound"/> for an unknown submission or one of another conversation
    /// than <paramref name="conversationId"/>.</summary>
    Task<SubmissionAbortResult> AbortSubmissionAsync(SubmissionId id, DurableContext context, ConversationId? conversationId = null);
    /// <summary>
    /// Commits the abort mark, signals and joins an active run invocation, and schedules the abort invocation. A task whose definition
    /// cannot take it settles as <c>orphaned</c> instead.
    /// </summary>
    Task<TaskAbortResult> AbortTaskAsync(TaskId id, DurableContext context);
    /// <summary>Resolves with the terminal receipt; cancelling <paramref name="context"/> cancels only this wait.</summary>
    Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context);
    /// <summary>Resolves when the ordinary ownership scope of every ownerless conversation has no live non-background task.</summary>
    Task WaitForIdleAsync(DurableContext context);
    /// <summary>Session total: every conversation's <c>pi.usage</c> summed (upstream <c>UsageState</c> JSON).</summary>
    Task<JsonObjectValue> UsageAsync(DurableContext context);
    /// <summary>Every live task with its owner edge, status, and owned conversations (spec §9.4), as a serialized watch, Chord's
    /// <see cref="IWatchHandle{T}"/>, whose operations address <see cref="TaskGraph.ToJson"/>; commits made while its listener runs arrive
    /// folded; cancelling <paramref name="context"/> ends it aborted. Reads only: it does not enable scheduling.</summary>
    Task<IWatchHandle<TaskGraph>> WatchTaskGraphAsync(DurableContext context);
    /// <summary>
    /// Attaches a synchronous <see cref="Observer{T}"/> to a conversation's structural view (spec §9.3), atomically on the Session line, for
    /// adapters: it gets each next revision with its operations as memory adopts the commit, at the publication once Storage stored a
    /// stored one and before it is stored for a deferred one, so it only records it. <paramref name="context"/> governs only the
    /// attachment. <paramref name="window"/> as for <see cref="IConversation.WatchAsync"/>; each observer has its own. A closed Harness, an
    /// unknown conversation or window bounds out of range fault the task; the call never throws.
    /// </summary>
    Task<Observation<ConversationView>> ObserveConversationAsync(ConversationId id, Observer<ConversationView> observer, DurableContext context,
        ConversationWindow? window = null);
    /// <summary>
    /// Attaches a synchronous <see cref="Observer{T}"/> to the task graph (spec §9.4), as <see cref="ObserveConversationAsync"/> does to a
    /// view. A <c>blocked</c> reason that changes without a commit (a registry change, a failed migration) advances it on the Session line
    /// outside any commit, with the background context. Reads only: it does not enable scheduling.
    /// </summary>
    Task<Observation<TaskGraph>> ObserveTaskGraphAsync(Observer<TaskGraph> observer, DurableContext context);
}

/// <summary>Result of <see cref="IHarness.AbortTaskAsync"/>.</summary>
public enum TaskAbortResult
{
    /// <summary><c>marked</c>: the abort mark is durable (or the task settled as orphaned).</summary>
    Marked,
    /// <summary><c>terminal</c>: the task was already terminal.</summary>
    Terminal,
}

// ─── Hooks ──────────────────────────────────────────────────────────────

/// <summary>What a hook may use: committed reads and the asking task's memos, which hooks and the task share (upstream <c>HookApi</c>). Hooks
/// run off the Session line, so reads see what is committed when they run; a hook rerun after a crash may see more. Entries never change and
/// compaction never removes them: a range bounded by entry IDs reads the same before and after a compaction and in a rerun. There is no
/// context read: the active transcript changes in the middle of a run, so a hook asking what a run did reads its history instead.</summary>
public interface IHookApi : IDocumentReader
{
    /// <summary>The asking task.</summary>
    TaskId TaskId { get; }
    /// <summary>Its conversation.</summary>
    ConversationId ConversationId { get; }
    /// <summary>The current <c>HarnessOptions.Models</c>, read at each access (a task runtime's too).</summary>
    IDurableModels Models { get; }
    /// <summary>Reads a memo of the asking task.</summary>
    Task<JsonValue?> MemoAsync(string name, DurableContext context);
    /// <summary>Stores a memo unless one exists; returns the winner.</summary>
    Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context);
    /// <summary>A committed entry visible from the task's conversation.</summary>
    Task<EntryRecord?> EntryAsync(EntryId id, DurableContext context);
    /// <summary>A committed entry visible from the task's conversation; null when absent, not visible, or of another kind.</summary>
    Task<EntryRecord?> EntryAsync(Entry token, EntryId id, DurableContext context);
    /// <summary>A committed typed entry visible from the task's conversation; null when absent, not visible, or of another kind.</summary>
    Task<TypedEntry<TData>?> EntryAsync<TData>(Entry<TData> token, EntryId id, DurableContext context) where TData : JsonValue;
    /// <summary>
    /// Committed raw history of the task's conversation, fork-aware, as <see cref="IConversation.EntriesAsync"/> (upstream <c>entries()</c>):
    /// every entry, also those a compaction summary or reset left behind, newest first unless the query orders ascending. A range also holds
    /// entries of others, such as display-only writes, compaction summaries and entries of other tasks; filter by kind.
    /// </summary>
    Task<Page<EntryRecord>> EntriesAsync(EntryQueryBounds query, int limit, JsonObjectValue? cursor, DurableContext context);
}

/// <summary>Hooks of the built-in generation task (upstream <c>GenerationHooks</c>); every handler is optional. Messages are pi-ai wire
/// JSON; hooks receive the generation's runtime as their <see cref="IHookApi"/>.</summary>
public sealed class GenerationHooks
{
    /// <summary>Before every request attempt, including recovery; a returned message list is used for that request only.</summary>
    public Func<ImmutableArray<JsonValue>, IHookApi, DurableContext, Task<ImmutableArray<JsonValue>?>>? BeforeRequest { get; init; }
    /// <summary>
    /// After the model adapter builds the provider request body, before it is sent (upstream <c>beforeProviderRequest</c>, Pi v1.5.0): it
    /// receives the model and the body, and a returned body replaces it for that request only; null keeps it. Runs again whenever the body
    /// is rebuilt, such as a retry within one attempt. The body is in the provider API's own format (<see cref="DurableModel.Api"/>), so a
    /// hook checks the API before changing it. It reaches the provider through <see cref="DurableStreamOptions.OnPayload"/>, which
    /// <see cref="NativeDurableModels"/> applies to JSON request bodies; compaction requests do not run it.
    /// </summary>
    public Func<DurableModel, JsonValue, IHookApi, DurableContext, Task<JsonValue?>>? BeforeProviderRequest { get; init; }
    /// <summary>Every terminal provider message, before classification.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task>? AfterResponse { get; init; }
    /// <summary>A final answer; the first returned user input continues the run.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task<JsonValue?>>? OnYield { get; init; }
    /// <summary>After every tool of the round is terminal; the round's result entries in call order.</summary>
    public Func<EntryId, ImmutableArray<EntryId>, IHookApi, DurableContext, Task>? AfterTools { get; init; }
    /// <summary>
    /// After a run answered, its answer committed and its inputs settled <c>done</c>: a final answer, or a <c>terminate</c> round; not when an
    /// <see cref="OnYield"/> continuation is taken, and never after a failure or an abort (upstream <c>afterRun</c>, spec §8.3). Returns the
    /// tasks to run (<see cref="Define.RunEndTask"/>), or null for none. The requested tasks of every hook are created together as children of
    /// the generation, which keeps the run, and the conversation busy, until they are terminal; then queued items are placed and the run
    /// ends. A crash before they are created asks again; once created, never again for this run. A request that cannot start (a built-in or
    /// uninstalled task, a throwing <c>Initial</c>, an unknown first phase) is skipped and reported against the hook's extension.
    /// </summary>
    public Func<RunSummary, IHookApi, DurableContext, Task<IReadOnlyList<RunEndTask>?>>? AfterRun { get; init; }
}

/// <summary>How a run ended, for <see cref="GenerationHooks.AfterRun"/> (upstream <c>RunSummary</c>). Read the run's history with
/// <c>api.EntriesAsync(new EntryQueryBounds { MinEntryId = First, MaxEntryId = Last }, ...)</c>.</summary>
/// <param name="Reason">A final answer, or a tool round whose every call asked to terminate.</param>
/// <param name="First">The run's first entry (<c>pi.live.run.first</c>): the first one the boundary that started it placed, for a continue the
/// failed run's it continues, otherwise its first tool-calling answer; the answer itself for a run that has none of these.</param>
/// <param name="Answer">The answer, or the terminating round's tool-calling answer, whose results follow it.</param>
/// <param name="Last">The run's last entry: the answer; for a terminate round its newest result, or the reset of its handoff.</param>
public sealed record RunSummary(RunSummaryReason Reason, EntryId First, EntryId Answer, EntryId Last)
{
    /// <summary>Upstream's JSON, as the <c>finish</c> checkpoint stores it: <c>{reason, answer, last, first}</c>.</summary>
    internal JsonObjectValue ToJson() => JsonValue.Object([
        KeyValuePair.Create("reason", (JsonValue)(Reason == RunSummaryReason.Terminate ? "terminate" : "answer")),
        KeyValuePair.Create("answer", (JsonValue)Answer.ToJson()),
        KeyValuePair.Create("last", (JsonValue)Last.ToJson()),
        KeyValuePair.Create("first", (JsonValue)First.ToJson()),
    ]);

    /// <summary>Reads <see cref="ToJson"/>.</summary>
    internal static RunSummary FromJson(JsonValue json)
    {
        var value = (JsonObjectValue)json;
        static EntryId Id(JsonObjectValue value, string key) => new((long)((JsonNumberValue)value[key]).Value);
        var reason = ((JsonStringValue)value["reason"]).Value == "terminate" ? RunSummaryReason.Terminate : RunSummaryReason.Answer;
        return new RunSummary(reason, Id(value, "first"), Id(value, "answer"), Id(value, "last"));
    }
}

/// <summary>Why a run answered (upstream <c>RunSummary["reason"]</c>).</summary>
public enum RunSummaryReason
{
    /// <summary><c>answer</c>: a final answer.</summary>
    Answer,
    /// <summary><c>terminate</c>: a tool round whose every call asked to terminate.</summary>
    Terminate,
}

/// <summary>A task a <see cref="GenerationHooks.AfterRun"/> hook asks for (upstream <c>RunEndTask</c>), built by <see cref="Define.RunEndTask"/>:
/// the task and its input.</summary>
/// <param name="Task">The task, which must be installed and not built-in; it is created from the installed definition.</param>
/// <param name="Input">Its input.</param>
public sealed record RunEndTask(DurableTask Task, JsonValue Input);

/// <summary>What <see cref="ToolHooks.BeforeTool"/> decides: replacement arguments or a block.</summary>
public sealed record BeforeToolDecision(JsonObjectValue? Arguments = null, string? Block = null);

/// <summary>Hooks of the built-in tool task, for model-issued and nested calls (upstream <c>ToolHooks</c>); every handler is optional. A
/// call is upstream's <c>ToolHookCall</c>: pi-ai <c>ToolCall</c> wire JSON with, for a nested call (one a running tool made through
/// <see cref="IToolExecutionApi.ExecuteToolAsync"/>), <c>parent: {taskId, callId}</c>. Hooks receive the tool task's runtime as their
/// <see cref="IHookApi"/>, so their memos are the call's.</summary>
public sealed class ToolHooks
{
    /// <summary>Before intent; the first block wins, otherwise arguments replace the call's arguments. A throw blocks.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task<BeforeToolDecision?>>? BeforeTool { get; init; }
    /// <summary>
    /// Before the result is committed; replaces the result. Runs on every result the tool task writes: an executed call's, and the error
    /// results the Harness writes itself, told apart by their diagnostic code: a tool that does not resolve (<c>tool_unavailable</c>, also
    /// every call to a tool its request did not offer), invalid arguments (<c>invalid_arguments</c>, with the call's <c>arguments</c> as the
    /// model sent them), a block (<c>blocked</c>), and, rebuilt from the call's partial output, an interruption (<c>interrupted</c>), the
    /// restart limit (<c>restart_limit</c>), and an abort (<c>aborted</c>, <c>abandoned</c>). Once the task was cut off more than
    /// <see cref="HarnessSettings.RestartLimit"/> times the hooks no longer run, since they may be what cuts it off, nor in the last attempt
    /// the scheduler gives an abort handler (restarts at the limit minus one). A result's <c>StructuredOutput</c> is dropped when the Harness
    /// wrote it (a nested call a hook turns into a success gets the bounded output as a schema-less tool's does), and its <c>Control</c>
    /// applies only to a call that completes (not to an interrupted, cut-off, or aborted one). An abort's hooks run inside the abort handler,
    /// which cannot be aborted: a slow hook delays <see cref="IConversation.AbortAsync"/>. Hooks that assume the tool ran (that cast the
    /// call's arguments or read <c>StructuredOutput</c>) check the result's diagnostics first.
    /// </summary>
    public Func<JsonObjectValue, ToolExecutionResult, IHookApi, DurableContext, Task<ToolExecutionResult?>>? AfterTool { get; init; }
}

/// <summary>What <see cref="CompactionHooks.BeforeCompact"/> sees (upstream's <c>compaction</c> argument).</summary>
public sealed record CompactionRequest
{
    /// <summary>Why the compaction runs: <c>manual</c>, <c>threshold</c> or <c>overflow</c> (upstream <c>CompactionReason</c>).</summary>
    public required string Reason { get; init; }
    /// <summary>The active entries the summary replaces, the head marker first.</summary>
    public required ImmutableArray<EntryRecord> Entries { get; init; }
    /// <summary>Their model context (pi-ai wire messages), the summarizer's source.</summary>
    public required ImmutableArray<JsonValue> Messages { get; init; }
    /// <summary>The first entry kept verbatim; the summary's <c>head</c>.</summary>
    public required EntryId FirstKept { get; init; }
    /// <summary>The instructions of <c>CompactAsync</c>, if any.</summary>
    public string? Instructions { get; init; }
}

/// <summary>What <see cref="CompactionHooks.BeforeCompact"/> decides (upstream <c>{decline: true} | {summary}</c>).</summary>
public sealed record CompactionDecision
{
    private CompactionDecision(bool decline, string? summary)
    {
        Decline = decline;
        Summary = summary;
    }

    /// <summary>Whether the compaction completes without a summary.</summary>
    public bool Decline { get; }
    /// <summary>The summary to place instead of summarizing; null when declining.</summary>
    public string? Summary { get; }

    /// <summary><c>{decline: true}</c>: complete with <c>{}</c>.</summary>
    public static CompactionDecision Declined { get; } = new(true, null);

    /// <summary><c>{summary}</c>: place <paramref name="summary"/> without a summarization request.</summary>
    public static CompactionDecision FromSummary(string summary) => new(false, summary ?? throw new ArgumentNullException(nameof(summary)));
}

/// <summary>Hooks of the built-in compaction task (upstream <c>CompactionHooks</c>); hooks receive the compaction's runtime as their
/// <see cref="IHookApi"/>.</summary>
public sealed class CompactionHooks
{
    /// <summary>
    /// After range selection, before summarizing; the first non-null decision wins, and a throw is reported and skipped. Null asks the next
    /// hook, and without a decision the task summarizes.
    /// </summary>
    public Func<CompactionRequest, IHookApi, DurableContext, Task<CompactionDecision?>>? BeforeCompact { get; init; }
}
