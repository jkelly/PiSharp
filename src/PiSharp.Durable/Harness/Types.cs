// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/types.ts. The parts the openable Harness (P6) and the
// task runtime (P7) need are complete; later-phase shapes keep upstream's names and carry TODO markers where their final C# signatures
// join (compaction and the task graph, P11). Submissions and the generation hooks are final (P8); tools are final (P9); views are final (P10).
//
// Mappings: upstream's `Tool` generic (a host's own ToolRegistration subtype) becomes record inheritance, so `{ ...tool, x }` is
// `tool with { X = ... }` and keeps the subtype; pi-ai messages and tool calls are wire JSON (JsonValue); a field that may be given,
// cleared with `null` or left `undefined` is a nullable Patch<T>; a stored agent state is the pi.agent document's JsonObjectValue.
using System.Collections.Immutable;
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
    /// <summary>The tool filter.</summary>
    public Patch<ToolSelection>? Tools { get; init; }
    /// <summary>Rendered after every extension section, as the section <c>instructions</c>.</summary>
    public Patch<string>? Instructions { get; init; }
    /// <summary>Directory within the environment's file system, passed to <c>HarnessOptions.Env</c>.</summary>
    public Patch<string>? Cwd { get; init; }
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
    /// <summary>The tools a request offers, in order.</summary>
    public required ImmutableArray<ToolRegistration> Tools { get; init; }
    /// <summary>Extension sections, then <c>instructions</c> when set.</summary>
    public required ImmutableArray<PromptSection> Sections { get; init; }
    /// <summary>The agent's instructions.</summary>
    public string? Instructions { get; init; }
    /// <summary>The environment directory.</summary>
    public string? Cwd { get; init; }
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
    /// <summary>End the run after this round.</summary>
    public bool Terminate { get; init; }
    /// <summary>Hand off to a new context with this text.</summary>
    public string? Handoff { get; init; }
}

/// <summary>The result of one tool execution (upstream <c>ToolExecutionResult</c>).</summary>
public sealed record ToolExecutionResult
{
    /// <summary>pi-ai tool result content (wire JSON); null: the retained running output becomes the content.</summary>
    public JsonArrayValue? Content { get; init; }
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
    /// <summary><c>HarnessOptions.Models</c>: the catalog, credentials and request transforms generation uses.</summary>
    IDurableModels Models { get; }
    /// <summary>Built by <c>HarnessOptions.Env</c> for this call; null without an environment.</summary>
    IExecutionEnv? Env { get; }
    /// <summary>
    /// Appends running output; it becomes the result content when the result omits <c>Content</c>. <paramref name="skipped"/> counts output
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
    /// <summary>Records a model-visible remark about this call. Throws once the call has settled.</summary>
    void Diagnostic(ToolDiagnostic diagnostic);
    /// <summary>Replaces the running details; the last value becomes the result details when the result omits <c>Details</c>. Resolves with
    /// the progress commit that includes it; cancelling <paramref name="context"/> stops the wait, not the update.</summary>
    Task DetailsAsync(JsonValue value, DurableContext context);
    /// <summary>Commits on the Session line in the tool task's scope; appended entries carry the task as <c>byTaskId</c>.</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context);
    /// <summary>Commits on the Session line in the tool task's scope.</summary>
    Task CommitAsync(Func<ITx, Task> change, DurableContext context);
    /// <summary>Reads a memo of the tool task.</summary>
    Task<JsonValue?> MemoAsync(string name, DurableContext context);
    /// <summary>Stores a memo of the tool task unless one exists; returns the winner.</summary>
    Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context);
    /// <summary>Creates a task in its own commit; <see cref="TaskOptions.ConversationId"/> defaults to the calling conversation (upstream
    /// takes the options without it).</summary>
    Task<TaskId> CreateTaskAsync(DurableTask task, JsonValue input, TaskOptions options, DurableContext context);
    /// <summary>The committed task record.</summary>
    Task<TaskRecord?> GetTaskAsync(TaskId id, DurableContext context);
    /// <summary>Resolves with the task's terminal receipt; rejects when the invocation ends.</summary>
    Task<TaskRecord> WaitForTaskAsync(TaskId id, DurableContext context);
    /// <summary>Invocation-bound handle of an existing conversation, such as one this tool created in a commit.</summary>
    Task<IConversationHandle?> ConversationAsync(ConversationId id, DurableContext context);
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
    /// <summary>JSON Schema of the arguments.</summary>
    public JsonObjectValue Parameters { get; init; } = EmptyParameters;
    /// <summary>Whether an interrupted execution may rerun on recovery. Default <see cref="ToolReplay.Unsafe"/>.</summary>
    public ToolReplay? Replay { get; init; }
    /// <summary>Default: the settings' <c>ToolExecution</c>. One sequential call makes its whole round sequential.</summary>
    public ToolExecutionMode? ExecutionMode { get; init; }
    /// <summary>How <see cref="Parameters"/> was built, which decides how arguments are converted and coerced before validation (pi-ai
    /// <c>validateToolArguments</c>). Default <see cref="PiSharp.Contracts.ToolSchemaOrigin.TypeBox"/>: upstream tools declare TypeBox schemas.</summary>
    public PiSharp.Contracts.ToolSchemaOrigin ParametersOrigin { get; init; } = PiSharp.Contracts.ToolSchemaOrigin.TypeBox;
    /// <summary>
    /// Repairs arguments models commonly get wrong before validation, such as a JSON string where an array belongs. Must be pure: it runs
    /// again when a call is retried before its intent is recorded. Its result is still validated against <see cref="Parameters"/>; a throw
    /// makes the arguments invalid.
    /// </summary>
    public Func<JsonValue, JsonValue>? PrepareArguments { get; init; }
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
public sealed record CompactionPolicy(bool Enabled, double ReserveTokens, double KeepRecentTokens, double BackgroundTokens);

/// <summary>How often running progress is committed (upstream <c>ProgressPolicy</c>).</summary>
public sealed record ProgressPolicy(double PartialIntervalMs, double OutputIntervalMs);

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

/// <summary>Host overrides of <see cref="CompactionPolicy"/>.</summary>
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
}

/// <summary>Host overrides of <see cref="ProgressPolicy"/>.</summary>
public sealed record ProgressSettings
{
    /// <summary>Minimum pause between commits of the answer being generated.</summary>
    public double? PartialIntervalMs { get; init; }
    /// <summary>Minimum pause between commits of running tool output.</summary>
    public double? OutputIntervalMs { get; init; }
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
    /// <summary>Compaction policy.</summary>
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
}

/// <summary>What <c>HarnessOptions.Env</c> builds an environment for (upstream <c>EnvTarget</c>).</summary>
/// <param name="ConversationId">The conversation.</param>
/// <param name="Cwd">The conversation's agent <c>cwd</c>.</param>
/// <param name="Read">Committed document reads.</param>
public sealed record EnvTarget(ConversationId ConversationId, string? Cwd, IDocumentReader Read);

/// <summary>Options of <see cref="Harness.OpenAsync"/> (upstream <c>HarnessOptions</c>).</summary>
public sealed record HarnessOptions
{
    /// <summary>pi-ai model access used by generation.</summary>
    public required IDurableModels Models { get; init; }
    /// <summary>The registry; it may keep changing while the Harness runs.</summary>
    public required IRegistryReader Registry { get; init; }
    /// <summary>Harness-wide run policy.</summary>
    public HarnessSettings? Settings { get; init; }
    /// <summary>Builds a conversation's environment at each use. Never called on the Session line.</summary>
    public Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? Env { get; init; }
    /// <summary>
    /// Runs in every commit that creates or forks a conversation, raw <c>tx.CreateConversationAsync()</c> included, after the built-in
    /// <c>pi.*</c> documents and before the conveniences apply <c>agent</c> and run <c>init</c>. A fork already has its copies. Table reads
    /// throw <see cref="ReadAfterWriteException"/>; a throw fails the creating commit.
    /// </summary>
    public Func<ITx, ConversationRecord, Task>? ConversationCreated { get; init; }
    /// <summary>The Harness clock in milliseconds; default <see cref="TimeProvider"/>'s wall clock.</summary>
    public Func<double>? Now { get; init; }
    /// <summary>Timers for sleeps and idle context expiry (a .NET seam for upstream's <c>setTimeout</c>); default
    /// <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
    /// <summary>Receives extension failures that do not fail the calling operation. Must not throw.</summary>
    public Action<Exception>? OnReport { get; init; }
}

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

/// <summary>What the scheduler would do with a live task.</summary>
public enum TaskInspectionKind
{
    /// <summary>An invocation is active.</summary>
    Running,
    /// <summary>The next scheduling pass reserves it.</summary>
    Ready,
    /// <summary>It waits for live tasks.</summary>
    Waiting,
    /// <summary>Its outcome is held until its ordinary owned work drains.</summary>
    Completing,
    /// <summary>No registered definition can take it.</summary>
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
    /// <summary>For <see cref="TaskInspectionKind.Ready"/>: its definition is newer and has a migration.</summary>
    public bool Migrates { get; init; }
    /// <summary>For <see cref="TaskInspectionKind.Waiting"/>: the live tasks it waits for.</summary>
    public ImmutableArray<TaskId> On { get; init; } = [];
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

/// <summary>A host submission: user input that may start a run, or a passive entry write (upstream <c>SubmissionDraft</c>). A request ID
/// deduplicates within one conversation before any write; reusing one for the other submission type rejects.</summary>
public sealed record SubmissionDraft
{
    private SubmissionDraft() { }
    /// <summary>User input (pi-ai <c>UserMessage["content"]</c>, wire JSON).</summary>
    public static SubmissionDraft Input(JsonValue content, WhenBusy? whenBusy = null, string? requestId = null) =>
        new() { Type = SubmissionType.Input, Content = content, WhenBusy = whenBusy, RequestId = requestId };
    /// <summary>A passive entry write.</summary>
    public static SubmissionDraft Write(EntryDraft entry, string? requestId = null) =>
        new() { Type = SubmissionType.Write, Entry = entry, RequestId = requestId };
    /// <summary>Input or write.</summary>
    public SubmissionType Type { get; private init; }
    /// <summary>Host deduplication key, scoped to the conversation.</summary>
    public string? RequestId { get; private init; }
    /// <summary>The input content.</summary>
    public JsonValue? Content { get; private init; }
    /// <summary>What a busy conversation does with the input.</summary>
    public WhenBusy? WhenBusy { get; private init; }
    /// <summary>The written entry.</summary>
    public EntryDraft? Entry { get; private init; }
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
    /// <summary>Withdraws a queued submission: <c>aborted</c>; <c>already_placed</c> for a placed input and <c>settled</c> for a settled
    /// submission.</summary>
    Task<SubmissionAbortResult> AbortAsync(DurableContext context);
}

/// <summary>
/// Invocation-bound conversation operations for tasks and tools (upstream <c>ConversationHandle</c>). Rejects after the invocation ends;
/// passive entries are written with ordinary transaction writes instead.
/// </summary>
public interface IConversationHandle
{
    /// <summary>The conversation.</summary>
    ConversationId Id { get; }
    /// <summary>Admits user input; the returned submission's operations are bound to the invocation too.</summary>
    Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context);
    /// <summary><c>Conversation.AbortAsync()</c>: withdraw queued inputs, abort the ordinary ownership scope, and wait until it is idle.</summary>
    Task AbortAsync(DurableContext context, ConversationAbortOptions? options = null);
    /// <summary>Resolves when the conversation's ordinary ownership scope has no live non-background task.</summary>
    Task WaitForIdleAsync(DurableContext context);
}

/// <summary>What a compaction produced (upstream <c>CompactionResult</c>). TODO(P11).</summary>
public sealed record CompactionResult(EntryId? EntryId = null, SubmissionId? SubmissionId = null);

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
    /// Durably admits user input or a passive entry write. A busy conversation queues it in <c>pi.inbox</c>, or rejects
    /// <see cref="WhenBusy.Reject"/> input with <see cref="ConversationBusyException"/> and writes nothing. Idle input places a user entry
    /// and starts a run; an idle write appends its entry.
    /// </summary>
    Task<ISubmission> SubmitAsync(SubmissionDraft submission, DurableContext context);
    /// <summary>Admits a write of a <c>pi.reset</c> entry that starts a new context, carrying <paramref name="handoff"/> as a user message
    /// when given.</summary>
    Task ResetAsync(string? handoff, DurableContext context);
    /// <summary>Admits a manual compaction task and returns its ID. TODO(P11).</summary>
    Task<TaskId> CompactAsync(string? instructions, DurableContext context);

    /// <summary>A Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, DurableContext context);
    /// <summary>A Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task CommitAsync(Func<ITx, Task> change, DurableContext context);
    /// <summary>A synchronous Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task<T> CommitAsync<T>(Func<ITx, T> change, DurableContext context);
    /// <summary>A synchronous Session commit whose <c>tx.CreateTaskAsync()</c> defaults to this conversation.</summary>
    Task CommitAsync(Action<ITx> change, DurableContext context);
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
    /// <summary>The structural view (spec §9.3) as a disposable read-only Chord state.</summary>
    Task<AttachedReplicatedState<ConversationView>> ViewStateAsync(DurableContext context);
    /// <summary>The structural view as a serialized exact-frame watch with bounded pending frames; cancelling <paramref name="context"/> stops
    /// it.</summary>
    Task<IWatchHandle<ConversationView>> WatchAsync(DurableContext context);
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
    /// Enables task scheduling. Idempotent; throws after close. Calls that ask for progress enable it too: <c>Conversation.SubmitAsync</c>,
    /// <c>CompactAsync</c>, <c>AbortAsync</c>, <c>Submission.WaitAsync</c>, <see cref="WaitForTaskAsync"/>, <see cref="WaitForIdleAsync"/>
    /// and <c>Conversation.WaitForIdleAsync</c>. Read-only viewers never do.
    /// </summary>
    void Resume();

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
    /// <summary>Every live task with its owner edge, status, and owned conversations, as a disposable read-only Chord state (spec §9.5).
    /// Reads only: it does not enable scheduling.</summary>
    Task<AttachedReplicatedState<TaskGraph>> TaskGraphAsync(DurableContext context);
    /// <summary>The task graph as a serialized exact-frame watch whose operations address <see cref="TaskGraph.ToJson"/>; cancelling
    /// <paramref name="context"/> stops it (spec §9.5).</summary>
    Task<IWatchHandle<TaskGraph>> WatchTaskGraphAsync(DurableContext context);
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

/// <summary>What a hook may use: committed reads and the asking task's memos, which hooks and the task share (upstream <c>HookApi</c>).</summary>
public interface IHookApi : IDocumentReader
{
    /// <summary>The asking task.</summary>
    TaskId TaskId { get; }
    /// <summary>Its conversation.</summary>
    ConversationId ConversationId { get; }
    /// <summary><c>HarnessOptions.Models</c>.</summary>
    IDurableModels Models { get; }
    /// <summary>Reads a memo of the asking task.</summary>
    Task<JsonValue?> MemoAsync(string name, DurableContext context);
    /// <summary>Stores a memo unless one exists; returns the winner.</summary>
    Task<JsonValue> MemoAsync(string name, JsonValue candidate, DurableContext context);
}

/// <summary>Hooks of the built-in generation task (upstream <c>GenerationHooks</c>); every handler is optional. Messages are pi-ai wire
/// JSON; hooks receive the generation's runtime as their <see cref="IHookApi"/>.</summary>
public sealed class GenerationHooks
{
    /// <summary>Before every request attempt, including recovery; a returned message list is used for that request only.</summary>
    public Func<ImmutableArray<JsonValue>, IHookApi, DurableContext, Task<ImmutableArray<JsonValue>?>>? BeforeRequest { get; init; }
    /// <summary>Every terminal provider message, before classification.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task>? AfterResponse { get; init; }
    /// <summary>A final answer; the first returned user input continues the run.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task<JsonValue?>>? OnYield { get; init; }
    /// <summary>After every tool of the round is terminal; the round's result entries in call order.</summary>
    public Func<EntryId, ImmutableArray<EntryId>, IHookApi, DurableContext, Task>? AfterTools { get; init; }
}

/// <summary>What <see cref="ToolHooks.BeforeTool"/> decides: replacement arguments or a block.</summary>
public sealed record BeforeToolDecision(JsonObjectValue? Arguments = null, string? Block = null);

/// <summary>Hooks of the built-in tool task (upstream <c>ToolHooks</c>); every handler is optional. Calls are pi-ai <c>ToolCall</c> wire
/// JSON; hooks receive the tool task's runtime as their <see cref="IHookApi"/>, so their memos are the call's.</summary>
public sealed class ToolHooks
{
    /// <summary>Before intent; the first block wins, otherwise arguments replace the call's arguments. A throw blocks.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task<BeforeToolDecision?>>? BeforeTool { get; init; }
    /// <summary>After execution, before the result entry; replaces the result.</summary>
    public Func<JsonObjectValue, ToolExecutionResult, IHookApi, DurableContext, Task<ToolExecutionResult?>>? AfterTool { get; init; }
}

/// <summary>What <see cref="CompactionHooks.BeforeCompact"/> decides: decline, or a summary.</summary>
public sealed record CompactionDecision(bool Decline = false, string? Summary = null);

/// <summary>Hooks of the built-in compaction task (upstream <c>CompactionHooks</c>). TODO(P11): signatures are confirmed when compaction is
/// ported.</summary>
public sealed class CompactionHooks
{
    /// <summary>After range selection, before summarizing; the first decision wins. The request is <c>{reason, entries, messages,
    /// firstKept, instructions?}</c>.</summary>
    public Func<JsonObjectValue, IHookApi, DurableContext, Task<CompactionDecision?>>? BeforeCompact { get; init; }
}
