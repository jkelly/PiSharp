// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/providers/faux.ts, reduced to what tool-output-bench.ts uses: the model
// faux/faux-1 answering each request with the next scripted reply, tool calls (stop reason toolUse) or a text. It is the scripted model of
// samples/Durable.Examples (ScriptedModels.cs), kept internal here.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Model access that answers each request with the next scripted reply: tool calls, or a text when <c>Calls</c> is empty.</summary>
internal sealed class FauxModels(params (ImmutableArray<ToolCallContent> Calls, string? Text)[] replies) : IDurableModels
{
    private readonly ConcurrentQueue<(ImmutableArray<ToolCallContent> Calls, string? Text)> replies = new(replies);

    /// <summary>The faux model reference.</summary>
    public static ModelRef Ref { get; } = new("faux", "faux-1");

    /// <summary>A reply calling <paramref name="name"/> once per ID.</summary>
    public static (ImmutableArray<ToolCallContent>, string?) Calls(string name, JsonObjectValue arguments, params string[] ids) =>
        ([.. ids.Select(id => new ToolCallContent(id, name, JsonData.Parse(arguments.ToJson())))], null);

    /// <summary>A text reply.</summary>
    public static (ImmutableArray<ToolCallContent>, string?) Text(string text) => ([], text);

    private DurableModel Model { get; } = new()
    {
        Id = "faux-1", Name = "Faux Model", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text", "image"],
        ContextWindow = 128000, MaxTokens = 16384,
    };

    public DurableModel? GetModel(string provider, string modelId) => provider == Model.Provider && modelId == Model.Id ? Model : null;

    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], TokenUsage.Zero,
            StopReason.Pending);
        if (!replies.TryDequeue(out var reply))
        {
            yield return new StreamError(StopReason.Error, partial with
            {
                StopReason = StopReason.Error,
                ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonValue.String("No more faux responses queued").ToJson())),
            });
            yield break;
        }
        yield return new StreamStarted(partial);
        if (reply.Text is { } text)
        {
            yield return new TextStarted(0, new TextContent(""));
            yield return new TextDelta(0, text);
            yield return new TextEnded(0, text);
            yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(text)], StopReason = StopReason.Stop });
            yield break;
        }
        for (var index = 0; index < reply.Calls.Length; index++)
        {
            var call = reply.Calls[index];
            yield return new ToolCallStarted(index, call with { Arguments = JsonData.Parse("{}") });
            yield return new ToolCallDelta(index, call.Arguments.ToString());
            yield return new ToolCallEnded(index, call);
        }
        yield return new StreamDone(StopReason.ToolUse, partial with { Content = [.. reply.Calls], StopReason = StopReason.ToolUse });
    }

    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in StreamSimple(model, messages, options, cancellationToken)) if (item is StreamTerminalEvent end) terminal = end;
        return terminal!.Message;
    }

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The faux model does not defer responses.");

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException("The faux model does not defer responses."));

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}
