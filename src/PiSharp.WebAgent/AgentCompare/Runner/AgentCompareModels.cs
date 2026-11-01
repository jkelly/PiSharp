// Decision 0011 (run W4, R5): the models the app's durable device streams through. AgentCompareModule gives DeviceHostService this
// decorator over the app's IDurableModels (the runs.db recorder over the Spend Guard over the faux, priced faux or routed models) through
// the chat area's device models hook (AddDeviceModelsDecorator, W4 fix 2); the DI registration itself is not changed, so the run engine and
// everything else keep theirs. A request of an agent compare model's session goes through AgentCompareGate (rewritten to the model's run
// key and account, admitted against its limits, and observed for its tool calls); every other request (every chat's, and a model's once it
// was opened as a chat) passes through unchanged.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.WebAgent.AgentCompare.Runner;

/// <summary>The durable device's models: agent compare's gate over <see cref="Inner"/>.</summary>
public sealed class AgentCompareModels(IDurableModels inner, AgentCompareGate gate) : IDurableModels
{
    /// <summary>The models underneath.</summary>
    public IDurableModels Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => Inner.GetModel(provider, modelId);

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return gate.Find(options) is { } found ? Stream(found.Run, found.Model, model, messages, options, cancellationToken)
            : Inner.StreamSimple(model, messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (gate.Find(options) is not { } found) return await Inner.CompleteSimpleAsync(model, messages, options, cancellationToken).ConfigureAwait(false);
        var rewritten = AgentCompareGate.Rewrite(options, found.Run, found.Model);
        if (await gate.AdmitAsync(found.Run, found.Model, model, messages, rewritten, turn: false, cancellationToken).ConfigureAwait(false) is { } refusal)
            return Refused(model, refusal);
        return await Inner.CompleteSimpleAsync(model, messages, rewritten, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The final message of a refused request: stop reason <c>aborted</c> with <paramref name="reason"/> as its errorMessage (the
    /// durable engine never retries it, and nothing was sent).</summary>
    public static AssistantMessage Refused(DurableModel model, string reason)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new AssistantMessage(model.Descriptor.Api, model.Descriptor.Provider, model.Descriptor.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
            TokenUsage.Zero, StopReason.Aborted, JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(reason))));
    }

    private async IAsyncEnumerable<StreamEvent> Stream(AgentCompareRun run, AgentCompareModelState state, DurableModel model, ImmutableArray<JsonValue> messages,
        DurableStreamOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rewritten = AgentCompareGate.Rewrite(options, run, state);
        if (await gate.AdmitAsync(run, state, model, messages, rewritten, turn: true, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            yield return new StreamError(StopReason.Aborted, Refused(model, refusal));
            yield break;
        }
        await foreach (var item in Inner.StreamSimple(model, messages, rewritten, cancellationToken).ConfigureAwait(false))
        {
            if (item is StreamTerminalEvent terminal) gate.Observe(run, state, terminal.Message);
            yield return item;
        }
    }
}
