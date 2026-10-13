// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/faux.ts, its paced streaming (fauxProvider({ tokensPerSecond,
// tokenSize })) reduced to what 21-late-join and 23-subagent-background use: each text or tool-call argument string is split into chunks of
// 4 x token size characters, and each chunk waits ceil(length / 4) / tokensPerSecond seconds before it streams. The waits are on an
// ExampleClock instead of setTimeout, and the token size is fixed (upstream draws it at random between min and max). A reply is the next
// scripted one, or one a route picks from the request's messages (upstream's FauxResponseStep function).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

/// <summary>Model access that streams scripted replies at a paced rate on an <see cref="ExampleClock"/>.</summary>
public sealed class PacedModels : IDurableModels
{
    private readonly ExampleClock clock;
    private readonly double tokensPerSecond;
    private readonly int tokenSize;
    private readonly ConcurrentQueue<ScriptedReply> replies = new();
    private readonly Func<ImmutableArray<JsonValue>, ScriptedReply>? route;

    /// <summary>Replies in order.</summary>
    public PacedModels(ExampleClock clock, double tokensPerSecond, int tokenSize, params ScriptedReply[] replies)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.tokensPerSecond = tokensPerSecond;
        this.tokenSize = tokenSize;
        foreach (var reply in replies) this.replies.Enqueue(reply);
    }

    /// <summary>A route answers every request.</summary>
    public PacedModels(ExampleClock clock, double tokensPerSecond, int tokenSize, Func<ImmutableArray<JsonValue>, ScriptedReply> route)
        : this(clock, tokensPerSecond, tokenSize) => this.route = route ?? throw new ArgumentNullException(nameof(route));

    private DurableModel Model { get; } = new()
    {
        Id = "faux-1", Name = "Faux Model", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text", "image"],
        ContextWindow = 128000, MaxTokens = 16384,
    };

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => provider == Model.Provider && modelId == Model.Id ? Model : null;

    private IEnumerable<string> Chunks(string text)
    {
        var size = Math.Max(1, tokenSize * 4);
        for (var index = 0; index < text.Length; index += size) yield return text.Substring(index, Math.Min(size, text.Length - index));
        if (text.Length == 0) yield return "";
    }

    private Task Pace(string chunk, CancellationToken cancellationToken) =>
        tokensPerSecond <= 0 ? Task.CompletedTask : clock.Delay(Math.Ceiling(chunk.Length / 4.0) / tokensPerSecond * 1000, cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, (long)clock.NowMs(), [], TokenUsage.Zero, StopReason.Pending);
        ScriptedReply? reply = route?.Invoke(messages);
        if (reply is null && !replies.TryDequeue(out reply))
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
            foreach (var chunk in Chunks(text))
            {
                await Pace(chunk, cancellationToken);
                yield return new TextDelta(0, chunk);
            }
            yield return new TextEnded(0, text);
            yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(text)], StopReason = StopReason.Stop, Usage = Usage(messages, text) });
            yield break;
        }
        for (var index = 0; index < reply.Calls.Length; index++)
        {
            var call = reply.Calls[index];
            yield return new ToolCallStarted(index, call with { Arguments = JsonData.Parse("{}") });
            foreach (var chunk in Chunks(call.Arguments.ToString()))
            {
                await Pace(chunk, cancellationToken);
                yield return new ToolCallDelta(index, chunk);
            }
            yield return new ToolCallEnded(index, call);
        }
        yield return new StreamDone(StopReason.ToolUse, partial with
        {
            Content = [.. reply.Calls], StopReason = StopReason.ToolUse,
            Usage = Usage(messages, string.Join('\n', reply.Calls.Select(call => $"{call.Name}:{call.Arguments}"))),
        });
    }

    // A rough form of the faux provider's estimate (a token per four characters of the prompt and the answer, without its prompt cache).
    // The examples print no usage; a reply with usage changes pi.usage, as upstream's replies do.
    private static TokenUsage Usage(ImmutableArray<JsonValue> messages, string answer)
    {
        var input = Math.Ceiling(messages.Sum(message => message.ToJson().Length) / 4.0);
        var output = Math.Ceiling(answer.Length / 4.0);
        return new TokenUsage(input, output, 0, 0, input + output, new(0, 0, 0, 0, 0));
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
        throw new NotSupportedException("The paced model does not defer responses.");

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException("The paced model does not defer responses."));

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}
