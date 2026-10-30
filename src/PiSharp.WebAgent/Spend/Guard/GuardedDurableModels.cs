// Decision 0011 (run W3, S3): the Spend Guard as a decorator over the app's final IDurableModels (faux, priced faux or routed), registered
// by AddGuard after every other registration, so every durable model call of a chat (generation through StreamSimple, compaction summaries
// through CompleteSimpleAsync) is reserved before it is sent and settled from how it ended (GuardedRunExecutor's rules). The call is read
// from the request: provider and model from the DurableModel, metadata.pisharp's account, op, source and run (SpendMetadata; a request
// without them is a chat call under a fresh op), the messages' UTF-8 size and options.maxTokens. The request goes to the inner models
// unchanged (the same model, messages, options and metadata; the route strips metadata.pisharp), except that maxTokens is lowered when
// the user chose "cap the output". A refused call is not sent: its stream is one StreamError (stop reason error) whose errorMessage is
// the spend error, and CompleteSimpleAsync returns that message, so the durable run fails with the code in its error (S5 shows the
// confirm modal from it). If the guard itself fails (the ledger cannot be read), the call is refused the same way: never sent unchecked.
//
// The answer names its ledger row (W3 fix): the final message of a reserved call (StreamDone or StreamError, CompleteSimpleAsync's message,
// a deferred answer's) carries `pisharp: {spendOp}`, the op of the ledger row it settles (op, or op~n for a later send), which the durable
// engine stores with the entry, so the chat's message footers link to their row exactly. It is PiSharp's own field: every request's messages
// lose it again before anything else reads them (the size the guard counts, and what the inner models and the provider see), so the
// provider gets the conversation exactly as it would without the guard.
//
// Deferred responses keep their reservation until the deferred answer arrives through StreamDeferred or FetchDeferredAsync of the same
// process (the handle id maps to the ledger op in memory); a cancelled one becomes uncertain; after a restart, startup recovery has
// already made it uncertain. Model lookups, catalog listings, classification and image generation pass through unguarded (W3 prices chat
// requests only).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.WebAgent.Spend;

/// <summary>The Spend Guard over <see cref="Inner"/>: every chat request is reserved, then sent, then settled.</summary>
public sealed class GuardedDurableModels : IDurableModels
{
    private readonly Lazy<GuardedRunExecutor> executor;
    private readonly ConcurrentDictionary<string, string> deferred = new(StringComparer.Ordinal);

    /// <summary>The guard over <paramref name="inner"/>, with <paramref name="executor"/> resolved on the first request (so resolving the
    /// models never fails because a spend service is missing; the first request then fails closed).</summary>
    public GuardedDurableModels(IDurableModels inner, Func<GuardedRunExecutor> executor)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(executor);
        Inner = inner;
        this.executor = new Lazy<GuardedRunExecutor>(executor, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The guard over <paramref name="inner"/> with <paramref name="executor"/>.</summary>
    public GuardedDurableModels(IDurableModels inner, GuardedRunExecutor executor) : this(inner, () => executor) =>
        ArgumentNullException.ThrowIfNull(executor);

    /// <summary>The models the guard sends to.</summary>
    public IDurableModels Inner { get; }

    /// <summary>The executor, resolved on first use.</summary>
    public GuardedRunExecutor Executor => executor.Value;

    /// <summary>The field of a guarded answer that names its ledger row: <c>pisharp</c>, an object whose <c>spendOp</c> is the row's op.</summary>
    public const string AnswerField = "pisharp";

    /// <summary>The name of the ledger op inside <see cref="AnswerField"/>: <c>spendOp</c>.</summary>
    public const string AnswerOpField = "spendOp";

    /// <summary><paramref name="message"/> naming its ledger row <paramref name="ledgerOp"/> (<see cref="AnswerField"/>); unchanged when the op
    /// is null.</summary>
    public static AssistantMessage Tag(AssistantMessage message, string? ledgerOp)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrEmpty(ledgerOp)) return message;
        var field = JsonData.Parse(new System.Text.Json.Nodes.JsonObject { [AnswerOpField] = ledgerOp }.ToJsonString());
        return message with { ExtraProperties = (message.ExtraProperties ?? JsonFields.Empty).Set(AnswerField, field) };
    }

    /// <summary><paramref name="messages"/> without <see cref="AnswerField"/> on any message (the same array when none has it).</summary>
    public static ImmutableArray<JsonValue> Untag(ImmutableArray<JsonValue> messages)
    {
        if (messages.IsDefaultOrEmpty || !messages.Any(message => message is JsonObjectValue item && item.ContainsKey(AnswerField))) return messages;
        return [.. messages.Select(message => message is JsonObjectValue item && item.ContainsKey(AnswerField) ? item.Remove(AnswerField) : message)];
    }

    /// <summary>The ledger op a stored answer names (<see cref="AnswerField"/>), or null.</summary>
    public static string? LedgerOpOf(System.Text.Json.Nodes.JsonObject? message) =>
        message?[AnswerField] is System.Text.Json.Nodes.JsonObject field && field[AnswerOpField] is System.Text.Json.Nodes.JsonValue value &&
        value.TryGetValue<string>(out var op) && op.Length > 0 ? op : null;

    /// <summary>The call the guard reserves for one request: the model, <c>metadata.pisharp</c>'s fields (source <c>chat</c> and a fresh op
    /// <c>call-…</c> when missing), the messages' size and <c>maxTokens</c>.</summary>
    public static SpendCall Call(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options)
    {
        ArgumentNullException.ThrowIfNull(model);
        var fields = SpendMetadata.Read(options);
        return new SpendCall
        {
            Provider = model.Provider,
            Model = model.Id,
            Account = fields.Account,
            Source = fields.Source ?? SpendSource.Chat,
            Run = fields.Run,
            Op = fields.Op ?? "call-" + Guid.NewGuid().ToString("N"),
            InputBytes = CostEstimator.InputBytes(messages.IsDefault ? [] : messages),
            MaxTokens = MaxTokens(options?.MaxTokens),
        };
    }

    /// <summary><paramref name="options"/> as sent for <paramref name="send"/>: unchanged, except <c>maxTokens</c> lowered to the user's chosen
    /// cap when it is lower.</summary>
    public static DurableStreamOptions? Options(DurableStreamOptions? options, GuardedSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var lowered = send.MaxTokens is { } chosen && (send.Call.MaxTokens is not { } own || chosen < own);
        return lowered ? (options ?? new DurableStreamOptions()) with { MaxTokens = send.MaxTokens } : options;
    }

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => Inner.GetModel(provider, modelId);

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Stream(model, Untag(messages), options, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        messages = Untag(messages);
        var (lease, refusal) = await ReserveAsync(model, messages, options, cancellationToken).ConfigureAwait(false);
        if (lease is null) return GuardedRunExecutor.StoppedMessage(model.Descriptor, refusal!, Now());
        await using (lease.ConfigureAwait(false))
        {
            var message = await lease.CompleteAsync(model.Descriptor,
                (send, token) => Inner.CompleteSimpleAsync(model, messages, Options(options, send), token), cancellationToken).ConfigureAwait(false);
            Remember(lease);
            return lease.Reserved ? Tag(message, lease.LedgerOp) : message;
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(handle);
        return deferred.TryGetValue(handle.Id, out var op) ? Deferred(model, handle, op, cancellationToken) : Inner.StreamDeferred(model, handle, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var message = await Inner.FetchDeferredAsync(model, handle, cancellationToken).ConfigureAwait(false);
        if (message.StopReason != StopReason.Deferred && deferred.TryRemove(handle.Id, out var op))
        {
            await Executor.SettleDeferredAsync(op, message).ConfigureAwait(false);
            return Tag(message, op);
        }
        return message;
    }

    /// <inheritdoc/>
    public async Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            await Inner.CancelDeferredAsync(model, handle, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The provider may have answered (and charged) before the cancel reached it: the row counts until the user resolves it.
            if (deferred.TryRemove(handle.Id, out var op))
                await Executor.SettleAsync(op, SpendOutcome.EndedWithoutUsage(0, "the deferred answer was cancelled; usage unknown")).ConfigureAwait(false);
        }
    }

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
        var (lease, refusal) = await ReserveAsync(model, messages, options, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            yield return new StreamError(StopReason.Error, GuardedRunExecutor.StoppedMessage(model.Descriptor, refusal!, Now()));
            yield break;
        }
        await using (lease.ConfigureAwait(false))
        {
            await foreach (var item in lease.StreamAsync(model.Descriptor,
                (send, token) => Inner.StreamSimple(model, messages, Options(options, send), token), cancellationToken).ConfigureAwait(false))
                yield return lease.Reserved ? Tagged(item, lease.LedgerOp) : item;
            Remember(lease);
        }
    }

    private async IAsyncEnumerable<StreamEvent> Deferred(DurableModel model, DurableDeferredHandle handle, string op,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var watch = new SpendStreamWatch();
        await foreach (var item in Inner.StreamDeferred(model, handle, cancellationToken).ConfigureAwait(false))
        {
            watch.Observe(item);
            yield return item is StreamTerminalEvent { Message.StopReason: not StopReason.Deferred } ? Tagged(item, op) : item;
        }
        // Only a final answer settles; a stream that stopped early leaves the reservation for the next fetch (or recovery).
        if (watch.Terminal is { Message.StopReason: not StopReason.Deferred } terminal && deferred.TryRemove(handle.Id, out _))
            await Executor.SettleDeferredAsync(op, terminal.Message).ConfigureAwait(false);
    }

    /// <summary>The lease of one request, or null and the message of a guard that could not check it (nothing is sent).</summary>
    private async Task<(GuardedLease? Lease, string? Unchecked)> ReserveAsync(DurableModel model, ImmutableArray<JsonValue> messages,
        DurableStreamOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            return (await Executor.ReserveAsync(Call(model, messages, options), cancellationToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return (null, SpendGuard.UncheckedMessage(error));
        }
    }

    /// <summary>A final event (<see cref="StreamDone"/> or <see cref="StreamError"/>) whose message names <paramref name="ledgerOp"/>; any
    /// other event unchanged.</summary>
    private static StreamEvent Tagged(StreamEvent item, string? ledgerOp) => item switch
    {
        StreamDone done => done with { Message = Tag(done.Message, ledgerOp) },
        StreamError error => error with { Message = Tag(error.Message, ledgerOp) },
        _ => item,
    };

    private void Remember(GuardedLease lease)
    {
        if (lease.DeferredHandle is { } id && lease.LedgerOp is { } op) deferred[id] = op;
    }

    private static int? MaxTokens(double? value) =>
        value is { } tokens && double.IsFinite(tokens) && tokens >= 1 ? (int)Math.Min(int.MaxValue, Math.Ceiling(tokens)) : null;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
