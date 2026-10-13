// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/chat-support.ts, and the faux provider of
// packages/ai/src/providers/faux.ts (fauxProvider, fauxAssistantMessage, fauxText, fauxThinking, fauxToolCall) as an IDurableModels.
//
// Adaptations:
// - Faux messages are pi-ai wire JSON (JsonObjectValue); the provider converts them to PiSharp.Contracts values with PiWireJson as it
//   streams. Its events are PiSharp.Contracts' compact frames (start with an empty partial, then *_start/*_delta/*_end, then done or error),
//   carrying the same content upstream's events snapshot in `partial`.
// - A response factory receives a FauxRequest: the messages, the request options, the request's cancellation token (upstream's
//   options.signal), the provider state and the model. Upstream's `FauxResponseStep = AssistantMessage | factory` is FauxStep, with implicit
//   conversions from both.
// - Upstream's queueMicrotask is Task.Yield; its token-paced setTimeout is Task.Delay. Like upstream, an abort is noticed after the chunk
//   it delays.
// - chatSetup's settings object, whose fields tests assign between decisions, is ChatSetup's mutable properties (Stream, Retry, ...) behind a
//   HarnessSettings whose getters read them at every resolution.
// - `new Proxy(models, { get })` overriding streamSimple or cancelDeferred is ModelsOverride, a decorator.
// - waitFor (a 5 ms poll for at most 5 s) is Eventually (a bounded poll of about 20 s): only how long a slow machine may take differs.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static HarnessTests;
using static SessionTests;

/// <summary>The faux provider and chat helpers of the harness.generation, harness.submissions and harness.prompt case groups.</summary>
internal static class ChatTests
{
    internal static readonly DurableContext Context = DurableContext.Background;

    // ─── pi-ai faux provider ────────────────────────────────────────────────

    internal static readonly JsonObjectValue FauxDefaultUsage =
        O("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""");

    /// <summary>Upstream <c>fauxText</c>.</summary>
    internal static JsonObjectValue FauxText(string text) => Obj(("type", "text"), ("text", text));

    /// <summary>Upstream <c>fauxThinking</c>.</summary>
    internal static JsonObjectValue FauxThinking(string thinking) => Obj(("type", "thinking"), ("thinking", thinking));

    /// <summary>Upstream <c>fauxToolCall</c>.</summary>
    internal static JsonObjectValue FauxToolCall(string name, JsonObjectValue arguments, string? id = null) =>
        Obj(("type", "toolCall"), ("id", id ?? FauxModels.RandomId("tool")), ("name", name), ("arguments", arguments));

    /// <summary>Upstream <c>fauxAssistantMessage(content, options)</c> with string content.</summary>
    internal static JsonObjectValue FauxAssistantMessage(string text, string? stopReason = null, JsonObjectValue? deferred = null,
        string? errorMessage = null, string? responseId = null, double? timestamp = null) =>
        FauxAssistantMessage([FauxText(text)], stopReason, deferred, errorMessage, responseId, timestamp);

    /// <summary>Upstream <c>fauxAssistantMessage(content, options)</c>: <c>{role, content, api, provider, model, usage, stopReason,
    /// deferred?, errorMessage?, responseId?, timestamp}</c>.</summary>
    internal static JsonObjectValue FauxAssistantMessage(IEnumerable<JsonObjectValue> content, string? stopReason = null, JsonObjectValue? deferred = null,
        string? errorMessage = null, string? responseId = null, double? timestamp = null)
    {
        var properties = new List<(string, JsonValue)>
        {
            ("role", "assistant"), ("content", JsonValue.Array(content)), ("api", "faux"), ("provider", "faux"), ("model", "faux-1"),
            ("usage", FauxDefaultUsage), ("stopReason", stopReason ?? "stop"),
        };
        if (deferred is not null) properties.Add(("deferred", deferred));
        if (errorMessage is not null) properties.Add(("errorMessage", errorMessage));
        if (responseId is not null) properties.Add(("responseId", responseId));
        properties.Add(("timestamp", timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return Obj([.. properties]);
    }

    // ─── chat-support.ts ────────────────────────────────────────────────────

    /// <summary>A Harness and its root conversation.</summary>
    internal sealed record OpenedChat(IHarness Harness, IConversation Root);

    /// <summary>Upstream <c>chatSetup(options)</c>.</summary>
    internal static ChatSetup Setup(FauxOptions? options = null) => new(new FauxModels(options));

    /// <summary>Opens a Harness over <paramref name="storage"/> and returns its root, configured with the faux model on first creation
    /// (upstream <c>openChat</c>).</summary>
    internal static async Task<OpenedChat> OpenChat(IStorage storage, ChatSetup setup,
        Func<EnvTarget, DurableContext, Task<PiSharp.Durable.Env.IExecutionEnv?>>? env = null, TimeProvider? timeProvider = null)
    {
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            TimeProvider = timeProvider,
            Models = setup.Models,
            Registry = setup.Registry,
            Settings = setup.Settings,
            Env = env,
            Now = () => setup.Now(),
            OnReport = setup.Reports.Add,
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "faux-1") });
        return new OpenedChat(harness, root);
    }

    /// <summary>Raw entries of a conversation, oldest first (upstream <c>allEntries</c>).</summary>
    internal static async Task<List<EntryRecord>> AllEntries(IConversation conversation)
    {
        var page = await conversation.EntriesAsync(new EntryQueryBounds(), 1000, null, Context);
        return [.. page.Items.Reverse()];
    }

    /// <summary>Kinds of a conversation's raw entries, oldest first.</summary>
    internal static async Task<List<string>> Kinds(IConversation conversation) => [.. (await AllEntries(conversation)).Select(entry => entry.Kind)];

    /// <summary>Text of the first text content of a message (upstream <c>textOf</c>).</summary>
    internal static string? TextOf(JsonValue? message)
    {
        if (message is not JsonObjectValue value || S(value["role"]) == "system") return null;
        if (value["content"] is JsonStringValue text) return text.Value;
        return value["content"] is JsonArrayValue blocks &&
            blocks.OfType<JsonObjectValue>().FirstOrDefault(block => block["type"] is JsonStringValue { Value: "text" }) is { } found
            ? S(found["text"]) : null;
    }

    /// <summary>
    /// A faux response that never answers; the run stays busy until its generation is cancelled. <c>Reached</c> completes once the request
    /// was sent, after the generation's preparation and request commits (upstream <c>unanswered</c>).
    /// </summary>
    internal static (FauxStep Step, Task Reached) Unanswered()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FauxStep step = new FauxResponseFactory(async request =>
        {
            reached.TrySetResult();
            await Aborted(request.Signal);
            throw new InvalidOperationException("unreachable");
        });
        return (step, reached.Task);
    }

    /// <summary>The installed tools with these names, as <c>ConfigureAsync</c> takes them (upstream <c>toolsNamed</c>).</summary>
    internal static List<ToolRegistration> ToolsNamed(ChatSetup setup, params string[] names)
    {
        var installed = setup.Registry.Snapshot().Tools();
        return [.. names.Select(name => installed.FirstOrDefault(found => found.Tool.Name == name).Tool ??
            throw new InvalidOperationException($"Tool {name} is not installed"))];
    }

    /// <summary>
    /// A scripted stream for a ModelsOverride: <paramref name="partialText"/>, when given, streams as one text block (start, text_start,
    /// text_delta), then the stream pauses <paramref name="pauseMs"/> and ends with <paramref name="final"/>. Upstream yields one
    /// <c>start</c> event whose partial already holds the text; PiSharp.Contracts' start frame has empty content, so the text follows it.
    /// </summary>
    internal static async IAsyncEnumerable<StreamEvent> ScriptedStream(string? partialText, int pauseMs, AssistantMessage final,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var start = final with { Content = [], StopReason = StopReason.Pending, Usage = TokenUsage.Zero };
        yield return new StreamStarted(start);
        if (partialText is not null)
        {
            yield return new TextStarted(0, new TextContent(""));
            yield return new TextDelta(0, partialText);
        }
        await Task.Delay(pauseMs, CancellationToken.None);
        yield return final.StopReason is StopReason.Error or StopReason.Aborted
            ? new StreamError(final.StopReason, final)
            : new StreamDone(final.StopReason, final);
    }

    // ─── Assertions ─────────────────────────────────────────────────────────

    /// <summary>vitest's <c>toMatchObject</c>: every property <paramref name="expected"/> names matches, recursively; arrays match
    /// element-wise with equal length. The strings <c>"&lt;number&gt;"</c> and <c>"&lt;string&gt;"</c> stand for <c>expect.any(Number)</c>
    /// and <c>expect.any(String)</c>.</summary>
    internal static void MatchJson(string expected, JsonValue? actual, string what) =>
        Program.Check(actual is not null && Matches(J(expected), actual, partial: true),
            $"{what}: expected a match of {expected}, actual {actual?.ToJson() ?? "undefined"}");

    /// <summary>vitest's <c>toEqual</c> with the <c>"&lt;number&gt;"</c> and <c>"&lt;string&gt;"</c> placeholders of
    /// <see cref="MatchJson"/>.</summary>
    internal static void MatchExactly(string expected, JsonValue? actual, string what) =>
        Program.Check(actual is not null && Matches(J(expected), actual, partial: false),
            $"{what}: expected {expected}, actual {actual?.ToJson() ?? "undefined"}");

    private static bool Matches(JsonValue expected, JsonValue actual, bool partial) => (expected, actual) switch
    {
        (JsonStringValue { Value: "<number>" }, JsonNumberValue) => true,
        (JsonStringValue { Value: "<string>" }, JsonStringValue) => true,
        (JsonObjectValue subset, JsonObjectValue value) => (partial || subset.Count == value.Count) &&
            subset.All(property => value.TryGetValue(property.Key, out var item) && Matches(property.Value, item, partial)),
        (JsonArrayValue items, JsonArrayValue values) =>
            items.Count == values.Count && Enumerable.Range(0, items.Count).All(index => Matches(items[index], values[index], partial)),
        _ => JsonValue.DeepEquals(expected, actual),
    };

    /// <summary>The terminal outcome of a settled task record, as JSON.</summary>
    internal static JsonValue OutcomeJson(TaskRecord record) => ((SettlingTaskState)record.State).Outcome.ToJson();

    /// <summary>A wire message as a PiSharp.Contracts value.</summary>
    internal static AssistantMessage Native(JsonObjectValue message) => PiWireJson.ReadMessage(message.ToData().Value);

    /// <summary>A PiSharp.Contracts message as wire JSON.</summary>
    internal static JsonObjectValue Wire(AssistantMessage message) => (JsonObjectValue)JsonValue.FromData(PiWireJson.WriteMessage(message));
}

/// <summary>Models and registry that survive a close/reopen, like a host process's own objects (upstream <c>ChatSetup</c>).</summary>
internal sealed class ChatSetup
{
    private readonly Values values;

    public ChatSetup(FauxModels faux) : this(faux, PiSharp.Durable.Registry.Create(), new SyncList<Exception>(), new Values()) { }

    private ChatSetup(FauxModels faux, IRegistry registry, SyncList<Exception> reports, Values values)
    {
        Faux = faux;
        Models = faux;
        Registry = registry;
        Reports = reports;
        this.values = values;
        Settings = new LiveSettings(values);
    }

    public FauxModels Faux { get; }
    /// <summary>The models the Harness opens with: the faux provider, or a decorator over it.</summary>
    public IDurableModels Models { get; set; }
    public IRegistry Registry { get; }
    public SyncList<Exception> Reports { get; }
    public Func<double> Now { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>The HarnessSettings the Harness reads; its getters read the mutable fields below at every resolution.</summary>
    public HarnessSettings Settings { get; }

    // Upstream's `setup.settings.<field> = ...`.
    public IReadOnlyList<Extension>? Extensions { get => values.Extensions; set => values.Extensions = value; }
    public ConversationStreamOptions? Stream { get => values.Stream; set => values.Stream = value; }
    public RetrySettings? Retry { get => values.Retry; set => values.Retry = value; }
    public CompactionSettings? Compaction { get => values.Compaction; set => values.Compaction = value; }
    public ProgressSettings? Progress { get => values.Progress; set => values.Progress = value; }
    public ToolExecutionMode? ToolExecution { get => values.ToolExecution; set => values.ToolExecution = value; }
    public QueueMode? SteeringMode { get => values.SteeringMode; set => values.SteeringMode = value; }
    public QueueMode? FollowUpMode { get => values.FollowUpMode; set => values.FollowUpMode = value; }
    public double? ContextRetentionMs { get => values.ContextRetentionMs; set => values.ContextRetentionMs = value; }

    /// <summary>A copy sharing the faux provider, registry, reports and settings object, with other models (upstream
    /// <c>{ ...base, models }</c>).</summary>
    public ChatSetup WithModels(IDurableModels models) => new(Faux, Registry, Reports, values) { Models = models, Now = Now };

    /// <summary>A copy with a fresh settings object (upstream <c>{ ...base, settings }</c>).</summary>
    public ChatSetup WithFreshSettings() => new(Faux, Registry, Reports, new Values()) { Models = Models, Now = Now };

    private sealed class Values
    {
        public volatile IReadOnlyList<Extension>? Extensions;
        public volatile ConversationStreamOptions? Stream;
        public volatile RetrySettings? Retry;
        public volatile CompactionSettings? Compaction;
        public volatile ProgressSettings? Progress;
        public ToolExecutionMode? ToolExecution;
        public QueueMode? SteeringMode;
        public QueueMode? FollowUpMode;
        public double? ContextRetentionMs;
    }

    private sealed class LiveSettings(Values values) : HarnessSettings
    {
        public override IReadOnlyList<Extension>? Extensions { get => values.Extensions; init { } }
        public override ConversationStreamOptions? Stream { get => values.Stream; init { } }
        public override RetrySettings? Retry { get => values.Retry; init { } }
        public override CompactionSettings? Compaction { get => values.Compaction; init { } }
        public override ProgressSettings? Progress { get => values.Progress; init { } }
        public override ToolExecutionMode? ToolExecution { get => values.ToolExecution; init { } }
        public override QueueMode? SteeringMode { get => values.SteeringMode; init { } }
        public override QueueMode? FollowUpMode { get => values.FollowUpMode; init { } }
        public override double? ContextRetentionMs { get => values.ContextRetentionMs; init { } }
    }
}

/// <summary>Options of the faux provider (upstream <c>RegisterFauxProviderOptions</c>).</summary>
internal sealed record FauxOptions
{
    public string? Api { get; init; }
    public string? Provider { get; init; }
    /// <summary>Fetches that return the original handle before the scripted response becomes ready.</summary>
    public int? PendingFetches { get; init; }
    public double? PollAfterMs { get; init; }
    public double? TokensPerSecond { get; init; }
    public int? MinTokenSize { get; init; }
    public int? MaxTokenSize { get; init; }
    /// <summary>Context window of faux-1 (default 128000).</summary>
    public double? ContextWindow { get; init; }
    /// <summary>The provider's models (upstream <c>models</c>); absent: one faux-1 model.</summary>
    public IReadOnlyList<FauxModelOptions>? Models { get; init; }
}

/// <summary>One faux model (upstream <c>FauxModelDefinition</c>): context window default 128000, output limit default 16384.</summary>
internal sealed record FauxModelOptions(string Id, double? ContextWindow = null, double? MaxTokens = null);

/// <summary>What a response factory sees: the request (upstream's factory arguments).</summary>
internal sealed record FauxRequest(ImmutableArray<JsonValue> Messages, DurableStreamOptions? Options, CancellationToken Signal, FauxState State,
    DurableModel Model);

/// <summary>A scripted response computed from its request (upstream <c>FauxResponseFactory</c>).</summary>
internal delegate Task<JsonObjectValue> FauxResponseFactory(FauxRequest request);

/// <summary>A scripted response: a message, or a factory (upstream <c>FauxResponseStep</c>).</summary>
internal sealed class FauxStep
{
    private FauxStep(JsonObjectValue? message, FauxResponseFactory? factory) { Message = message; Factory = factory; }
    public JsonObjectValue? Message { get; }
    public FauxResponseFactory? Factory { get; }
    public static implicit operator FauxStep(JsonObjectValue message) => new(message, null);
    public static implicit operator FauxStep(FauxResponseFactory factory) => new(null, factory);
    /// <summary>A synchronous factory.</summary>
    public static FauxStep From(Func<FauxRequest, JsonObjectValue> factory) => new(null, request => Task.FromResult(factory(request)));
}

/// <summary>Upstream <c>FauxProviderState</c>.</summary>
internal sealed class FauxState
{
    private int callCount;
    private int deferredFetchCount;
    public int CallCount => Volatile.Read(ref callCount);
    public int DeferredFetchCount => Volatile.Read(ref deferredFetchCount);
    public SyncList<JsonObjectValue> CancelledDeferred { get; } = new();
    internal void Called() => Interlocked.Increment(ref callCount);
    internal void Fetched() => Interlocked.Increment(ref deferredFetchCount);
}

/// <summary>pi-ai's faux provider registered in a Models collection (upstream <c>fauxProvider</c> with <c>createModels</c>).</summary>
internal sealed class FauxModels : IDurableModels
{
    private const int DefaultMinTokenSize = 3;
    private const int DefaultMaxTokenSize = 5;

    private readonly Lock gate = new();
    private readonly List<FauxStep> pending = [];
    private readonly ConcurrentDictionary<string, string[]> promptCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeferredResponse> deferredResponses = new(StringComparer.Ordinal);
    private readonly FauxOptions options;
    private readonly int minTokenSize;
    private readonly int maxTokenSize;

    public FauxModels(FauxOptions? options = null)
    {
        this.options = options ?? new FauxOptions();
        Api = this.options.Api ?? RandomId("faux");
        Provider = this.options.Provider ?? "faux";
        minTokenSize = Math.Max(1, Math.Min(this.options.MinTokenSize ?? DefaultMinTokenSize, this.options.MaxTokenSize ?? DefaultMaxTokenSize));
        maxTokenSize = Math.Max(minTokenSize, this.options.MaxTokenSize ?? DefaultMaxTokenSize);
        var definitions = this.options.Models is { Count: > 0 } listed ? listed : [new FauxModelOptions("faux-1", this.options.ContextWindow)];
        Models = [.. definitions.Select(definition => new DurableModel
        {
            Id = definition.Id, Name = definition.Id == "faux-1" && this.options.Models is null ? "Faux Model" : definition.Id, Api = Api,
            Provider = Provider, BaseUrl = "http://localhost:0", Reasoning = false, Input = ["text", "image"],
            ContextWindow = definition.ContextWindow ?? 128000, MaxTokens = definition.MaxTokens ?? 16384,
        })];
        Model = Models[0];
    }

    public string Api { get; }
    public string Provider { get; }
    /// <summary>The first model.</summary>
    public DurableModel Model { get; }
    /// <summary>Every model of the provider.</summary>
    public ImmutableArray<DurableModel> Models { get; }
    public FauxState State { get; } = new();

    public void SetResponses(params FauxStep[] responses)
    {
        lock (gate)
        {
            pending.Clear();
            pending.AddRange(responses);
        }
    }

    public void AppendResponses(params FauxStep[] responses)
    {
        lock (gate) pending.AddRange(responses);
    }

    public int PendingResponseCount
    {
        get { lock (gate) return pending.Count; }
    }

    internal static string RandomId(string prefix) =>
        $"{prefix}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Convert.ToString(Random.Shared.NextInt64(long.MaxValue), 16)}";

    public DurableModel? GetModel(string provider, string modelId) =>
        provider == Provider ? Models.FirstOrDefault(model => model.Id == modelId) : null;

    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        // Taken when the request starts, as upstream's stream() does before its microtask.
        FauxStep? step;
        lock (gate)
        {
            step = pending.Count > 0 ? pending[0] : null;
            if (step is not null) pending.RemoveAt(0);
        }
        State.Called();
        return StreamAsync(model, messages, options, step, cancellationToken);
    }

    private async IAsyncEnumerable<StreamEvent> StreamAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        FauxStep? step, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        JsonObjectValue? message = null;
        var deferred = options?.Deferred is { } flag && flag is not JsonBooleanValue { Value: false } && flag is not JsonNullValue;
        if (step is null)
        {
            message = WithUsageEstimate(ErrorMessage("No more faux responses queued", model.Id), messages, options);
            yield return Terminal(message);
            yield break;
        }
        if (deferred)
        {
            var handle = new List<(string, JsonValue)> { ("provider", model.Provider), ("modelId", model.Id), ("api", model.Api), ("id", RandomId("deferred")) };
            if (this.options.PollAfterMs is { } pollAfter) handle.Add(("pollAfterMs", pollAfter));
            var wire = Obj([.. handle]);
            deferredResponses[S(wire["id"])] = new DeferredResponse(wire, step, messages, options, model, Math.Max(0, this.options.PendingFetches ?? 0));
            message = DeferredMessage(model, wire);
        }
        else
        {
            string? failure = null;
            try
            {
                message = await ResolveAsync(step, messages, options, model, cancellationToken);
            }
            catch (Exception error)
            {
                failure = error.Message;
            }
            if (failure is not null)
            {
                yield return Terminal(ErrorMessage(failure, model.Id));
                yield break;
            }
        }
        await foreach (var item in StreamWithDeltas(message!, cancellationToken)) yield return item;
    }

    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => await TerminalOf(StreamSimple(model, messages, options, cancellationToken));

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        State.Fetched();
        return FetchAsync(model, handle, cancellationToken);
    }

    public async Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        await TerminalOf(StreamDeferred(model, handle, cancellationToken));

    private async IAsyncEnumerable<StreamEvent> FetchAsync(DurableModel model, DurableDeferredHandle handle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var id = handle.Id;
        if (!deferredResponses.TryGetValue(id, out var entry) || Str(entry.Handle, "provider") != Str(handle.Wire, "provider") ||
            Str(entry.Handle, "modelId") != Str(handle.Wire, "modelId") || Str(entry.Handle, "api") != Str(handle.Wire, "api"))
        {
            yield return Terminal(ErrorMessage($"Unknown faux deferred response: {id}", model.Id));
            yield break;
        }
        if (entry.Cancelled)
        {
            yield return Terminal(ErrorMessage($"Faux deferred response was cancelled: {id}", model.Id));
            yield break;
        }
        JsonObjectValue message;
        if (entry.TakePendingFetch()) message = DeferredMessage(model, entry.Handle);
        else
        {
            if (entry.Final is null)
            {
                // The submission's options without the deferred flag; the fetch has its own signal.
                var submission = entry.Options is null ? null : entry.Options with { Deferred = null };
                try
                {
                    entry.Final = await ResolveAsync(entry.Step, entry.Messages, submission, entry.Model, cancellationToken);
                }
                catch (Exception error)
                {
                    entry.Final = ErrorMessage(error.Message, entry.Model.Id);
                }
            }
            message = entry.Final;
        }
        await foreach (var item in StreamWithDeltas(message, cancellationToken)) yield return item;
    }

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        State.CancelledDeferred.Add(handle.Wire);
        if (deferredResponses.TryGetValue(handle.Id, out var entry)) entry.Cancelled = true;
        return Task.CompletedTask;
    }

    private async Task<JsonObjectValue> ResolveAsync(FauxStep step, ImmutableArray<JsonValue> messages, DurableStreamOptions? options, DurableModel model,
        CancellationToken signal)
    {
        var resolved = step.Factory is { } factory ? await factory(new FauxRequest(messages, options, signal, State, model)) : step.Message!;
        // cloneMessage: provider identity of the request's model; a missing timestamp or usage gets its default.
        var cloned = resolved.SetItem("api", Api).SetItem("provider", Provider).SetItem("model", model.Id);
        if (!cloned.ContainsKey("timestamp")) cloned = cloned.SetItem("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!cloned.ContainsKey("usage")) cloned = cloned.SetItem("usage", ChatTests.FauxDefaultUsage);
        return WithUsageEstimate(cloned, messages, options);
    }

    /// <summary>Upstream <c>streamWithDeltas</c>, as compact PiSharp.Contracts frames.</summary>
    private async IAsyncEnumerable<StreamEvent> StreamWithDeltas(JsonObjectValue message, [EnumeratorCancellation] CancellationToken signal)
    {
        var partial = message.SetItem("content", JsonArrayValue.Empty).SetItem("stopReason", "pending");
        var content = new List<JsonValue>();
        StreamEvent AbortedEvent() => Terminal(partial.SetItem("content", JsonValue.Array(content)).SetItem("stopReason", "aborted")
            .SetItem("errorMessage", "Request was aborted").SetItem("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        if (signal.IsCancellationRequested)
        {
            yield return AbortedEvent();
            yield break;
        }
        yield return new StreamStarted(ChatTests.Native(partial));
        var blocks = (JsonArrayValue)message["content"];
        for (var index = 0; index < blocks.Count; index++)
        {
            if (signal.IsCancellationRequested)
            {
                yield return AbortedEvent();
                yield break;
            }
            var block = (JsonObjectValue)blocks[index];
            var type = S(block["type"]);
            if (type is "thinking" or "text")
            {
                var key = type;
                var text = S(block[key]);
                content.Add(Obj(("type", type), (key, "")));
                yield return type == "text" ? new TextStarted(index, new TextContent("")) : new ThinkingStarted(index, new ThinkingContent(""));
                var sofar = "";
                foreach (var chunk in Split(text))
                {
                    await ScheduleChunk(chunk);
                    if (signal.IsCancellationRequested)
                    {
                        yield return AbortedEvent();
                        yield break;
                    }
                    sofar += chunk;
                    content[index] = Obj(("type", type), (key, sofar));
                    yield return type == "text" ? new TextDelta(index, chunk) : new ThinkingDelta(index, chunk);
                }
                content[index] = block;
                yield return type == "text" ? new TextEnded(index, text) : new ThinkingEnded(index, text);
                continue;
            }
            content.Add(block.SetItem("arguments", JsonObjectValue.Empty));
            var call = (ToolCallContent)PiWireJson.ReadContent(block.ToData().Value);
            yield return new ToolCallStarted(index, call with { Arguments = JsonData.Parse("{}") });
            foreach (var chunk in Split(block["arguments"].ToJson()))
            {
                await ScheduleChunk(chunk);
                if (signal.IsCancellationRequested)
                {
                    yield return AbortedEvent();
                    yield break;
                }
                yield return new ToolCallDelta(index, chunk);
            }
            content[index] = block;
            yield return new ToolCallEnded(index, call);
        }
        var stopReason = S(message["stopReason"]);
        if (stopReason == "pending") throw new InvalidOperationException("Faux response ended without a stop reason");
        yield return Terminal(message);
    }

    private static StreamEvent Terminal(JsonObjectValue message)
    {
        var native = ChatTests.Native(message);
        return native.StopReason is StopReason.Error or StopReason.Aborted ? new StreamError(native.StopReason, native) : new StreamDone(native.StopReason, native);
    }

    private static async Task<AssistantMessage> TerminalOf(IAsyncEnumerable<StreamEvent> events)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in events) if (item is StreamTerminalEvent end) terminal = end;
        return terminal?.Message ?? throw new InvalidOperationException("The faux stream has no terminal event");
    }

    private IEnumerable<string> Split(string text)
    {
        var chunks = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var tokenSize = minTokenSize + Random.Shared.Next(maxTokenSize - minTokenSize + 1);
            var charSize = Math.Max(1, tokenSize * 4);
            chunks.Add(text.Substring(index, Math.Min(charSize, text.Length - index)));
            index += charSize;
        }
        return chunks.Count > 0 ? chunks : [""];
    }

    private Task ScheduleChunk(string chunk)
    {
        if (options.TokensPerSecond is not { } rate || rate <= 0) return YieldOnce();
        var delayMs = EstimateTokens(chunk) / rate * 1000;
        return Task.Delay(TimeSpan.FromMilliseconds(delayMs));

        static async Task YieldOnce() => await Task.Yield();
    }

    private JsonObjectValue DeferredMessage(DurableModel model, JsonObjectValue handle) => Obj(("role", "assistant"), ("content", JsonArrayValue.Empty),
        ("api", model.Api), ("provider", model.Provider), ("model", model.Id), ("usage", ChatTests.FauxDefaultUsage), ("stopReason", "deferred"),
        ("deferred", handle), ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    private JsonObjectValue ErrorMessage(string error, string modelId) => Obj(("role", "assistant"), ("content", JsonArrayValue.Empty), ("api", Api),
        ("provider", Provider), ("model", modelId), ("usage", ChatTests.FauxDefaultUsage), ("stopReason", "error"), ("errorMessage", error),
        ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    // ─── Usage estimate (upstream withUsageEstimate) ───

    private static double EstimateTokens(string text) => Math.Ceiling(text.Length / 4.0);

    private JsonObjectValue WithUsageEstimate(JsonObjectValue message, ImmutableArray<JsonValue> messages, DurableStreamOptions? options)
    {
        // One text per message; the whole prompt joins them with blank lines.
        var prompt = messages.Select(item => $"{S(((JsonObjectValue)item)["role"])}:{MessageToText((JsonObjectValue)item)}").ToArray();
        var promptLength = JoinedLength(prompt, prompt.Length);
        var promptTokens = Math.Ceiling(promptLength / 4.0);
        var outputTokens = EstimateTokens(AssistantContentToText((JsonArrayValue)message["content"]));
        var input = promptTokens;
        double cacheRead = 0, cacheWrite = 0;
        if (options?.SessionId is { Length: > 0 } sessionId && options.CacheRetention != "none")
        {
            if (promptCache.TryGetValue(sessionId, out var previous))
            {
                var cachedChars = CommonPromptPrefixLength(previous, prompt);
                cacheRead = Math.Ceiling(cachedChars / 4.0);
                cacheWrite = Math.Ceiling((promptLength - cachedChars) / 4.0);
                input = Math.Max(0, promptTokens - cacheRead);
            }
            else cacheWrite = promptTokens;
            promptCache[sessionId] = prompt;
        }
        return message.SetItem("usage", Obj(("input", input), ("output", outputTokens), ("cacheRead", cacheRead), ("cacheWrite", cacheWrite),
            ("totalTokens", input + outputTokens + cacheRead + cacheWrite),
            ("cost", O("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}"""))));
    }

    private static double JoinedLength(string[] messages, int count)
    {
        double length = count > 0 ? (count - 1) * 2 : 0;
        for (var index = 0; index < count; index++) length += messages[index].Length;
        return length;
    }

    private static double CommonPromptPrefixLength(string[] previous, string[] current)
    {
        var index = 0;
        while (index < previous.Length && index < current.Length && previous[index] == current[index]) index++;
        string Rest(string[] messages) => index == messages.Length ? "" : (index > 0 ? "\n\n" : "") + string.Join("\n\n", messages.Skip(index));
        var a = Rest(previous);
        var b = Rest(current);
        var common = 0;
        while (common < Math.Min(a.Length, b.Length) && a[common] == b[common]) common++;
        return JoinedLength(previous, index) + common;
    }

    private static string ContentToText(JsonValue content)
    {
        if (content is JsonStringValue text) return text.Value;
        return string.Join("\n", ((JsonArrayValue)content).OfType<JsonObjectValue>().Select(block => S(block["type"]) == "text"
            ? S(block["text"]) : $"[image:{S(block["mimeType"])}:{S(block["data"]).Length}]"));
    }

    private static string AssistantContentToText(JsonArrayValue content) => string.Join("\n", content.OfType<JsonObjectValue>().Select(block =>
        S(block["type"]) switch
        {
            "text" => S(block["text"]),
            "thinking" => S(block["thinking"]),
            _ => $"{S(block["name"])}:{block["arguments"].ToJson()}",
        }));

    private static string MessageToText(JsonObjectValue message)
    {
        switch (S(message["role"]))
        {
            case "system":
            {
                var parts = new List<string> { SystemText(message) };
                if (message.TryGetValue("toolsRemoved", out var removed)) parts.AddRange(((JsonArrayValue)removed).Select(tool => $"tool-:{tool.ToJson()}"));
                if (message.TryGetValue("toolsAdded", out var added)) parts.AddRange(((JsonArrayValue)added).Select(tool => $"tool+:{tool.ToJson()}"));
                return string.Join("\n", parts.Where(part => part.Length > 0));
            }
            case "user": return ContentToText(message["content"]);
            case "assistant": return AssistantContentToText((JsonArrayValue)message["content"]);
            default:
                return string.Join("\n", new[] { S(message["toolName"]) }.Concat(((JsonArrayValue)message["content"]).Select(block => ContentToText(JsonValue.Array(block)))));
        }
    }

    /// <summary>pi-ai <c>getSystemMessageText</c>.</summary>
    private static string SystemText(JsonObjectValue message)
    {
        var parts = new List<string> { ContentToTextOnly(message["content"]) };
        if (message.TryGetValue("sections", out var sections))
            foreach (var value in ((JsonObjectValue)sections).Values) if (value is JsonStringValue text) parts.Add(text.Value);
        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    private static string ContentToTextOnly(JsonValue content) => content is JsonStringValue text ? text.Value
        : string.Join("\n", ((JsonArrayValue)content).OfType<JsonObjectValue>().Where(block => S(block["type"]) == "text").Select(block => S(block["text"])));

    private static string? Str(JsonObjectValue value, string key) => value.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : null;

    private sealed class DeferredResponse(JsonObjectValue handle, FauxStep step, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        DurableModel model, int pendingFetches)
    {
        private int pendingFetches = pendingFetches;
        public JsonObjectValue Handle { get; } = handle;
        public FauxStep Step { get; } = step;
        public ImmutableArray<JsonValue> Messages { get; } = messages;
        public DurableStreamOptions? Options { get; } = options;
        public DurableModel Model { get; } = model;
        public volatile bool Cancelled;
        public JsonObjectValue? Final { get; set; }

        public bool TakePendingFetch()
        {
            while (true)
            {
                var current = Volatile.Read(ref pendingFetches);
                if (current <= 0) return false;
                if (Interlocked.CompareExchange(ref pendingFetches, current - 1, current) == current) return true;
            }
        }
    }
}

/// <summary>Models with some operations replaced (upstream's <c>new Proxy(models, { get })</c>).</summary>
internal sealed class ModelsOverride(IDurableModels inner) : IDurableModels
{
    public Func<DurableModel, ImmutableArray<JsonValue>, DurableStreamOptions?, CancellationToken, IAsyncEnumerable<StreamEvent>>? StreamSimpleOverride { get; init; }
    public Func<DurableModel, DurableDeferredHandle, CancellationToken, Task>? CancelDeferredOverride { get; init; }

    public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) =>
        StreamSimpleOverride is { } stream ? stream(model, messages, options, cancellationToken) : inner.StreamSimple(model, messages, options, cancellationToken);

    public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.StreamDeferred(model, handle, cancellationToken);

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.FetchDeferredAsync(model, handle, cancellationToken);

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        CancelDeferredOverride is { } cancel ? cancel(model, handle, cancellationToken) : inner.CancelDeferredAsync(model, handle, cancellationToken);
}

/// <summary>Models without the faux provider (upstream <c>createModels()</c>): every lookup misses.</summary>
internal static class NoModels
{
    public static IDurableModels Instance => EmptyDurableModels.Instance;
}
