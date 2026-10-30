// Decision 0011 (run W4, R0): chats (and any other durable session: agent compare's workspaces) in History. RunsModule registers this
// decorator over the app's final IDurableModels after AddSpendArea, so it wraps the Spend Guard: every generation request (StreamSimple) and
// compaction summary (CompleteSimpleAsync) of a durable session is recorded as one runs.db row, keyed by its op (metadata.pisharp.op, the
// host's operation id: new for each prepared request, the same when a request is recovered after a restart, so a recovered call updates its
// row instead of adding one). The row has the source and run from metadata.pisharp (a chat: source chat, session = the chat id, the
// messages by reference "chat:<id>", not copied), the model, the account, the request's options and size, and, when the call ends, the
// output, usage, the cost of the ledger row the guard settled (its answer names it: pisharp.spendOp), the timings and the events. The
// request and its stream pass through unchanged; the routing path's capture hook is told which row a provider request belongs to
// (RunHttpScope, set again before every step of the inner stream), so raw payloads are kept for chats too when the user turned them on.
// Recording never fails a call: a runs.db failure is logged and the call goes on. Deferred responses, model listings, classification and
// image generation pass through unrecorded.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace PiSharp.WebAgent.Runs;

/// <summary>Finds the models a run streams through.</summary>
public static class RunModels
{
    /// <summary><paramref name="models"/> without the recorder and the Spend Guard around it.</summary>
    public static IDurableModels Unguarded(IDurableModels models)
    {
        ArgumentNullException.ThrowIfNull(models);
        while (true)
        {
            switch (models)
            {
                case RecordingDurableModels recording:
                    models = recording.Inner;
                    continue;
                case GuardedDurableModels guarded:
                    models = guarded.Inner;
                    continue;
                default:
                    return models;
            }
        }
    }
}

/// <summary>Records every durable model call in runs.db (see the file comment).</summary>
public sealed class RecordingDurableModels : IDurableModels
{
    private readonly RunStore store;
    private readonly IServiceProvider? services;
    private readonly ILogger log;

    /// <summary>A recorder over <paramref name="inner"/> into <paramref name="store"/>; <paramref name="services"/> gives the settings and the
    /// ledger (resolved on use; null: defaults and no cost).</summary>
    public RecordingDurableModels(IDurableModels inner, RunStore store, IServiceProvider? services = null, ILogger<RecordingDurableModels>? log = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        Inner = inner;
        this.store = store;
        this.services = services;
        this.log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>The models it records (the Spend Guard in the app).</summary>
    public IDurableModels Inner { get; }

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => Inner.GetModel(provider, modelId);

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Stream(model, messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var call = Begin(model, messages, options, "complete");
        AssistantMessage? message = null;
        string? failure = null;
        var prior = RunHttpScope.Current;
        if (call is not null) RunHttpScope.Current = call.Http;
        try
        {
            message = await Inner.CompleteSimpleAsync(model, messages, options, cancellationToken).ConfigureAwait(false);
            return message;
        }
        catch (Exception error)
        {
            failure = error.Message;
            throw;
        }
        finally
        {
            RunHttpScope.Current = prior;
            if (call is not null)
            {
                StreamTerminalEvent? terminal = message is null ? null
                    : message.StopReason is StopReason.Error or StopReason.Aborted ? new StreamError(message.StopReason, message) : new StreamDone(message.StopReason, message);
                await FinishAsync(call, terminal, failure, cancellationToken.IsCancellationRequested, null, []).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Inner.StreamDeferred(model, handle, cancellationToken);

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Inner.FetchDeferredAsync(model, handle, cancellationToken);

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Inner.CancelDeferredAsync(model, handle, cancellationToken);

    /// <inheritdoc/>
    public ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) => Inner.GetModelsOfType(type, provider);

    /// <inheritdoc/>
    public Task<ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
        Inner.GetAvailableOfTypeAsync(type, provider, cancellationToken);

    /// <inheritdoc/>
    public JsonObjectValue? GetModelOfType(string type, string provider, string id) => Inner.GetModelOfType(type, provider, id);

    /// <inheritdoc/>
    public Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
        Inner.ClassifyAsync(model, context, cancellationToken);

    /// <inheritdoc/>
    public Task<JsonObjectValue> GenerateImagesAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
        Inner.GenerateImagesAsync(model, context, cancellationToken);

    private async IAsyncEnumerable<StreamEvent> Stream(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var call = Begin(model, messages, options, "stream");
        var events = new List<RunEventRecord>();
        StreamTerminalEvent? terminal = null;
        long? ttft = null;
        string? failure = null;
        long sequence = 0;
        IAsyncEnumerator<StreamEvent>? items = null;
        try
        {
            while (true)
            {
                var prior = RunHttpScope.Current;
                if (call is not null) RunHttpScope.Current = call.Http;
                bool next;
                try
                {
                    items ??= Inner.StreamSimple(model, messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    next = await items.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = error.Message;
                    throw;
                }
                finally
                {
                    RunHttpScope.Current = prior;
                }
                if (!next) break;
                var item = items.Current;
                if (call is not null)
                {
                    var at = call.Clock.ElapsedMilliseconds;
                    if (ttft is null && item is TextDelta or ThinkingDelta or ToolCallStarted or ToolCallProvisionalStarted or ToolCallDelta) ttft = at;
                    if (item is StreamTerminalEvent end) terminal = end;
                    sequence++;
                    if (call.StoreEvents && events.Count < RunEngine.MaxStoredEvents)
                    {
                        try { events.Add(new RunEventRecord(sequence, at, JsonNode.Parse(PiWireJson.WriteEvent(item).ToString())!)); }
                        catch (Exception error) when (error is not OutOfMemoryException) { /* An event the wire cannot write is not stored. */ }
                    }
                }
                yield return item;
            }
        }
        finally
        {
            if (items is not null)
            {
                var prior = RunHttpScope.Current;
                if (call is not null) RunHttpScope.Current = call.Http;
                try { await items.DisposeAsync().ConfigureAwait(false); }
                finally { RunHttpScope.Current = prior; }
            }
            if (call is not null) await FinishAsync(call, terminal, failure, cancellationToken.IsCancellationRequested, ttft, events).ConfigureAwait(false);
        }
    }

    /// <summary>The row of a call, started; null when it could not be recorded (the call goes on).</summary>
    private Call? Begin(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options, string kind)
    {
        try
        {
            var fields = SpendMetadata.Read(options);
            var source = fields.Source ?? SpendSource.Chat;
            var run = fields.Run;
            var settings = Settings();
            var now = DateTimeOffset.UtcNow;
            var request = new JsonObject
            {
                ["call"] = kind,
                ["inputBytes"] = CostEstimator.InputBytes(messages.IsDefault ? [] : messages),
                ["messages"] = messages.IsDefault ? 0 : messages.Length,
            };
            if (options?.MaxTokens is { } max && double.IsFinite(max)) request["maxTokens"] = max;
            if (options?.Reasoning is { } reasoning) request["thinking"] = reasoning;
            if (fields.Tier is { } tier) request["tier"] = tier;
            if (options?.CacheRetention is { } cache) request["cacheRetention"] = cache;
            var record = new RunRecord
            {
                Id = "c" + Guid.NewGuid().ToString("N"),
                Op = fields.Op ?? "call-" + Guid.NewGuid().ToString("N"),
                Source = source.Wire(),
                Provider = model.Provider,
                Model = model.Id,
                Api = model.Api,
                Account = fields.Account,
                Session = run,
                GroupId = source is SpendSource.Chat or SpendSource.AgentCompare ? null : run,
                ParentId = source == SpendSource.AgentCompare ? run : null,
                Title = RunText.Title(LastUserText(messages)),
                Options = request,
                MessagesRef = run is null ? null : (source == SpendSource.Chat ? "chat" : source.Wire()) + ":" + run,
                Created = now,
                Started = now,
                Updated = now,
            };
            var id = store.BeginCall(record);
            var clock = Stopwatch.StartNew();
            var capture = settings.StoreRawPayloads && model.Api != RunModelOptions.FauxApi ? new RunCapture(() => clock.ElapsedMilliseconds) : null;
            return new Call(id, clock, new RunHttpContext { RunId = id, Api = model.Api, Capture = capture }, settings.StoreSseEvents);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "A {Provider}/{Model} call could not be recorded in runs.db; it goes on unrecorded.", model.Provider, model.Id);
            return null;
        }
    }

    private async Task FinishAsync(Call call, StreamTerminalEvent? terminal, string? failure, bool cancelled, long? ttft, List<RunEventRecord> events)
    {
        try
        {
            var duration = call.Clock.ElapsedMilliseconds;
            var message = terminal?.Message;
            var status = message is null ? cancelled ? RunStatus.Aborted : RunStatus.Error
                : terminal is StreamDone ? RunStatus.Completed
                : message.StopReason == StopReason.Aborted ? RunStatus.Aborted : RunStatus.Error;
            var output = message is null ? null : JsonNode.Parse(PiWireJson.WriteMessage(message).ToString());
            var usage = message is null ? RunUsage.Zero
                : new RunUsage(message.Usage.Input, message.Usage.Output, message.Usage.CacheRead, message.Usage.CacheWrite, message.Usage.TotalTokens);
            var spendOp = output is JsonObject json ? GuardedDurableModels.LedgerOpOf(json) : null;
            Reservation? row = null;
            if (spendOp is not null && services?.GetService<ISpendLedger>() is { } ledger)
            {
                try { row = await ledger.GetAsync(spendOp, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { log.LogDebug(error, "The ledger row {Op} could not be read.", spendOp); }
            }
            var (cost, costKind, billing) = RunEngine.Cost(row, message);
            double? rate = usage.Output > 0 && ttft is { } first && duration > first ? Math.Round(usage.Output / ((duration - first) / 1000.0), 2) : null;
            store.Finish(call.Id, new RunOutcome
            {
                Status = status,
                Output = output,
                OutputText = message is null ? null : string.Concat(message.Content.OfType<TextContent>().Select(text => text.Text)),
                StopReason = output?["stopReason"] is System.Text.Json.Nodes.JsonValue stop && stop.TryGetValue<string>(out var reason) ? reason : null,
                Error = status == RunStatus.Error ? RunText.Cut(ErrorText(message) ?? failure ?? "The call ended without a final message.", 4000) : null,
                Usage = usage,
                CostMicros = cost,
                CostKind = costKind,
                Billing = billing,
                SpendOp = spendOp,
                TtftMs = ttft,
                DurationMs = duration,
                TokensPerSecond = rate is { } value && double.IsFinite(value) ? value : null,
                Finished = DateTimeOffset.UtcNow,
                Events = events,
                Raw = call.Http.Capture?.Items() ?? [],
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.LogWarning(error, "A call's end could not be recorded in runs.db (run {Run}).", call.Id);
        }
    }

    private RunHistorySettings Settings()
    {
        try { return services?.GetService<ISettingsStore>()?.Get<RunHistorySettings>() ?? RunHistorySettings.CreateDefault(); }
        catch (Exception error) when (error is not OperationCanceledException) { return RunHistorySettings.CreateDefault(); }
    }

    private static string? ErrorText(AssistantMessage? message) =>
        message?.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var value) && value is { Value.ValueKind: System.Text.Json.JsonValueKind.String }
            ? value.Value.GetString() : null;

    private static string? LastUserText(ImmutableArray<JsonValue> messages)
    {
        if (messages.IsDefaultOrEmpty) return null;
        for (var index = messages.Length - 1; index >= 0; index--)
        {
            if (messages[index] is not JsonObjectValue message || !message.TryGetValue("role", out var role) || role is not JsonStringValue { Value: "user" }) continue;
            if (!message.TryGetValue("content", out var content)) return null;
            if (content is JsonStringValue text) return text.Value;
            if (content is JsonArrayValue parts)
                return string.Concat(parts.OfType<JsonObjectValue>().Select(part => part.TryGetValue("text", out var piece) && piece is JsonStringValue words ? words.Value : ""));
            return null;
        }
        return null;
    }

    private sealed record Call(string Id, Stopwatch Clock, RunHttpContext Http, bool StoreEvents);
}
