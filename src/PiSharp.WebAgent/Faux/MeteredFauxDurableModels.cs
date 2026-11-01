// Decision 0011 (run W3, S0): the faux models of --faux-metered. They answer exactly as FauxDurableModels does (the same grammar, the same
// events), but each finished answer (StreamDone) reports usage at a list price, with tokens from text length (FauxPricing.Tokens), and the
// models' catalog JSON carries that price. Errors, cancellations and dropped streams report nothing, as a real provider would not, so the
// Spend Guard's release and uncertain paths can be exercised too. Nothing here calls a provider.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Faux;

/// <summary>Faux models that report usage at <see cref="Price"/>.</summary>
/// <param name="inner">The faux models that answer.</param>
/// <param name="price">The list price; default <see cref="FauxPricing.Price"/>.</param>
public sealed class MeteredFauxDurableModels(FauxDurableModels inner, CatalogCost? price = null) : IDurableModels
{
    /// <summary>The faux models that answer.</summary>
    public FauxDurableModels Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The list price every answer is charged at, per million tokens.</summary>
    public CatalogCost Price { get; } = price ?? FauxPricing.Price;

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) =>
        Inner.GetModel(provider, modelId) is { } model ? model with { Wire = model.Wire is { } wire ? Priced(wire) : null } : null;

    /// <inheritdoc/>
    public ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) =>
        type == "chat" ? [.. Inner.GetModelsOfType(type, provider).Select(Priced)] : Inner.GetModelsOfType(type, provider);

    /// <inheritdoc/>
    public Task<ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
        Task.FromResult(GetModelsOfType(type, provider));

    /// <inheritdoc/>
    public JsonObjectValue? GetModelOfType(string type, string provider, string id) =>
        GetModelsOfType(type, provider).FirstOrDefault(model => model.TryGetValue("id", out var value) && value is JsonStringValue text && text.Value == id);

    /// <inheritdoc/>
    public Task<JsonObjectValue> ClassifyAsync(JsonObjectValue model, JsonObjectValue context, CancellationToken cancellationToken) =>
        Inner.ClassifyAsync(model, context, cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = InputTokens(messages);
        await foreach (var item in Inner.StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false))
            yield return item is StreamDone done ? done with { Message = Priced(done.Message, input) } : item;
    }

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false))
            if (item is StreamTerminalEvent end) terminal = end;
        return terminal?.Message ?? throw new InvalidOperationException("The faux stream ended without a final message.");
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

    /// <summary>The input tokens of a request: its messages as JSON (<see cref="FauxPricing.Tokens"/>).</summary>
    public static int InputTokens(ImmutableArray<JsonValue> messages) => FauxPricing.Tokens(JsonValue.Array(messages.IsDefault ? [] : messages).ToJson());

    /// <summary>The output tokens of a message: its text, thinking and tool calls (name and arguments).</summary>
    public static int OutputTokens(AssistantMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var tokens = 0L;
        foreach (var content in message.Content.IsDefault ? [] : message.Content)
            tokens += content switch
            {
                TextContent text => FauxPricing.Tokens(text.Text),
                ThinkingContent thinking => FauxPricing.Tokens(thinking.Thinking),
                ToolCallContent call => FauxPricing.Tokens(call.Name + call.Arguments),
                _ => 0,
            };
        return (int)Math.Min(int.MaxValue, tokens);
    }

    private AssistantMessage Priced(AssistantMessage message, int input) => message with { Usage = FauxPricing.Usage(input, OutputTokens(message), Price) };

    private JsonObjectValue Priced(JsonObjectValue wire) => wire.SetItem("cost", JsonValue.Object(
    [
        new("input", Price.Input), new("output", Price.Output), new("cacheRead", Price.CacheRead), new("cacheWrite", Price.CacheWrite),
    ]));
}
