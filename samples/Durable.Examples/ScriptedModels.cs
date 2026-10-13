// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/faux.ts, reduced to what the examples use: the model faux/faux-1
// answering each request with the next scripted reply, a text streamed as one text block or tool calls streamed as tool-call blocks with
// stop reason toolUse (upstream fauxAssistantMessage([fauxToolCall(...)], { stopReason: "toolUse" })). A real host passes its IDurableModels
// over PiSharp.AI instead.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

/// <summary>One scripted answer: a text, or tool calls.</summary>
public sealed record ScriptedReply
{
    private ScriptedReply(string? text, ImmutableArray<ToolCallContent> calls) { Text = text; Calls = calls; }

    /// <summary>The text of a text answer.</summary>
    public string? Text { get; }

    /// <summary>The tool calls of a tool-calling answer; default for a text answer.</summary>
    public ImmutableArray<ToolCallContent> Calls { get; }

    /// <summary>A text answer.</summary>
    public static ScriptedReply Answer(string text) => new(text ?? throw new ArgumentNullException(nameof(text)), default);

    /// <summary>An answer that calls one tool (upstream <c>fauxToolCall(name, args, { id })</c>).</summary>
    public static ScriptedReply ToolCall(string name, JsonObjectValue arguments, string? id = null) =>
        new(null, [new ToolCallContent(id ?? "tool:" + Guid.NewGuid().ToString("N")[..12], name, JsonData.Parse(arguments.ToJson()))]);

    /// <summary>A text answer.</summary>
    public static implicit operator ScriptedReply(string text) => Answer(text);
}

/// <summary>Model access that answers each request with the next scripted reply (pi-ai's faux provider, reduced).</summary>
public sealed class ScriptedModels : IDurableModels
{
    private readonly ConcurrentQueue<ScriptedReply> replies;

    /// <summary>Text answers.</summary>
    public ScriptedModels(params string[] replies) : this(replies.Select(ScriptedReply.Answer).ToArray()) { }

    /// <summary>Text and tool-calling answers.</summary>
    public ScriptedModels(params ScriptedReply[] replies) => this.replies = new(replies);

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
        if (!replies.TryDequeue(out var reply))
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
