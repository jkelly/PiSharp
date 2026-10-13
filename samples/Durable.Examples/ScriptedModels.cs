// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/faux.ts, reduced to what examples 14 and 15 use: the model
// faux/faux-1 answering each request with the next scripted text, streamed as one text block. A real host passes its IDurableModels over
// PiSharp.AI instead.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

/// <summary>Model access that answers each request with the next scripted text (pi-ai's faux provider, reduced).</summary>
public sealed class ScriptedModels(params string[] replies) : IDurableModels
{
    private readonly ConcurrentQueue<string> replies = new(replies);

    /// <summary>The scripted model.</summary>
    public DurableModel Model { get; } = new()
    {
        Id = "faux-1", Name = "Faux Model", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text", "image"],
        ContextWindow = 128000, MaxTokens = 16384,
    };

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => provider == Model.Provider && modelId == Model.Id ? Model : null;

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, timestamp, [], TokenUsage.Zero, StopReason.Pending);
        if (!replies.TryDequeue(out var text))
        {
            var error = partial with
            {
                StopReason = StopReason.Error,
                ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonValue.String("No more scripted replies").ToJson())),
            };
            yield return new StreamError(StopReason.Error, error);
            yield break;
        }
        yield return new StreamStarted(partial);
        yield return new TextStarted(0, new TextContent(""));
        yield return new TextDelta(0, text);
        yield return new TextEnded(0, text);
        yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(text)], StopReason = StopReason.Stop });
    }

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in StreamSimple(model, messages, options, cancellationToken)) if (item is StreamTerminalEvent end) terminal = end;
        return terminal!.Message;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The scripted model does not defer responses.");

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException("The scripted model does not defer responses."));

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}
