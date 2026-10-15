// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/providers/faux.ts, its paced streaming (fauxProvider({ tokensPerSecond,
// tokenSize })) and usage estimate (withUsageEstimate, with the prompt cache per session), reduced to what 19-json, 21-late-join and
// 23-subagent-background use: each text or tool-call argument string is split into chunks of 4 x token size characters, and each chunk waits
// ceil(length / 4) / tokensPerSecond seconds before it streams. The waits are on an ExampleClock instead of setTimeout, and the token size is
// fixed (upstream draws it at random between min and max). A reply is the next scripted one, or one a route picks from the request's
// messages (upstream's FauxResponseStep function). As upstream, every partial carries the final usage estimate.
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
    private readonly Lock gate = new();
    private readonly Dictionary<string, string[]> promptCache = new(StringComparer.Ordinal);
    private DurableModel model;

    /// <summary>Replies in order.</summary>
    public PacedModels(ExampleClock clock, double tokensPerSecond, int tokenSize, params ScriptedReply[] replies)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.tokensPerSecond = tokensPerSecond;
        this.tokenSize = tokenSize;
        foreach (var reply in replies) this.replies.Enqueue(reply);
        model = new()
        {
            Id = "faux-1", Name = "Faux Model", Api = "faux", Provider = "faux", BaseUrl = "http://localhost:0", Input = ["text", "image"],
            ContextWindow = 128000, MaxTokens = 16384,
        };
    }

    /// <summary>A route answers every request.</summary>
    public PacedModels(ExampleClock clock, double tokensPerSecond, int tokenSize, Func<ImmutableArray<JsonValue>, ScriptedReply> route)
        : this(clock, tokensPerSecond, tokenSize) => this.route = route ?? throw new ArgumentNullException(nameof(route));

    /// <summary>The model's API name; upstream's faux provider registers a fresh <c>faux:&lt;time&gt;:&lt;random&gt;</c> one.</summary>
    public string Api
    {
        get => model.Api;
        init => model = model with { Api = value };
    }

    /// <summary>A fresh API name as upstream's faux provider makes one (<c>randomId("faux")</c>).</summary>
    public static string RandomApi(ExampleClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var base36 = "";
        for (var value = Random.Shared.NextInt64(1L << 40, 1L << 52); value > 0; value /= 36) base36 = "0123456789abcdefghijklmnopqrstuvwxyz"[(int)(value % 36)] + base36;
        return $"faux:{(long)clock.NowMs()}:{base36}";
    }

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => provider == model.Provider && modelId == model.Id ? model : null;

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
        // The estimate is made before streaming, so every partial carries it.
        var answer = reply.Text ?? string.Join('\n', reply.Calls.Select(call => $"{call.Name}:{call.Arguments}"));
        partial = partial with { Usage = Estimate(messages, answer, options) };
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
            yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(text)], StopReason = StopReason.Stop });
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
        yield return new StreamDone(StopReason.ToolUse, partial with { Content = [.. reply.Calls], StopReason = StopReason.ToolUse });
    }

    // ─── The faux provider's usage estimate (withUsageEstimate) ───

    private TokenUsage Estimate(ImmutableArray<JsonValue> messages, string answer, DurableStreamOptions? options)
    {
        // One text per message; the whole prompt joins them with blank lines.
        var prompt = messages.OfType<JsonObjectValue>().Select(message => $"{Role(message)}:{MessageText(message)}").ToArray();
        var promptLength = JoinedLength(prompt, prompt.Length);
        var promptTokens = Math.Ceiling(promptLength / 4.0);
        var output = Math.Ceiling(answer.Length / 4.0);
        double input = promptTokens, cacheRead = 0, cacheWrite = 0;
        if (options?.SessionId is { Length: > 0 } sessionId && options.CacheRetention != "none")
        {
            lock (gate)
            {
                if (promptCache.TryGetValue(sessionId, out var previous))
                {
                    var cached = CommonPromptPrefixLength(previous, prompt);
                    cacheRead = Math.Ceiling(cached / 4.0);
                    cacheWrite = Math.Ceiling((promptLength - cached) / 4.0);
                    input = Math.Max(0, promptTokens - cacheRead);
                }
                else cacheWrite = promptTokens;
                promptCache[sessionId] = prompt;
            }
        }
        return new TokenUsage(input, output, cacheRead, cacheWrite, input + output + cacheRead + cacheWrite, new(0, 0, 0, 0, 0));
    }

    private static string Role(JsonObjectValue message) => message["role"] is JsonStringValue role ? role.Value : "";

    private static string MessageText(JsonObjectValue message)
    {
        switch (Role(message))
        {
            case "system":
            {
                var parts = new List<string> { ContentText(message["content"]) };
                if (message.TryGetValue("sections", out var sections))
                    parts.AddRange(((JsonObjectValue)sections).Values.OfType<JsonStringValue>().Select(text => text.Value));
                var lines = new List<string> { string.Join("\n\n", parts.Where(part => part.Length > 0)) };
                if (message.TryGetValue("toolsRemoved", out var removed)) lines.AddRange(((JsonArrayValue)removed).Select(tool => $"tool-:{tool.ToJson()}"));
                if (message.TryGetValue("toolsAdded", out var added)) lines.AddRange(((JsonArrayValue)added).Select(tool => $"tool+:{tool.ToJson()}"));
                return string.Join("\n", lines.Where(part => part.Length > 0));
            }
            case "user": return ContentText(message["content"]);
            case "assistant":
                return string.Join("\n", ((JsonArrayValue)message["content"]).OfType<JsonObjectValue>().Select(block =>
                    ((JsonStringValue)block["type"]).Value switch
                    {
                        "text" => ((JsonStringValue)block["text"]).Value,
                        "thinking" => ((JsonStringValue)block["thinking"]).Value,
                        _ => $"{((JsonStringValue)block["name"]).Value}:{block["arguments"].ToJson()}",
                    }));
            default:
                // A tool result: its tool name, then each content block.
                return string.Join("\n", new[] { message["toolName"] is JsonStringValue name ? name.Value : "" }
                    .Concat(((JsonArrayValue)message["content"]).Select(block => ContentText(JsonValue.Array([block])))));
        }
    }

    private static string ContentText(JsonValue content) => content is JsonStringValue text ? text.Value
        : string.Join("\n", ((JsonArrayValue)content).OfType<JsonObjectValue>().Select(block => block["type"] is JsonStringValue { Value: "text" }
            ? ((JsonStringValue)block["text"]).Value
            : $"[image:{(block.TryGetValue("mimeType", out var mime) && mime is JsonStringValue type ? type.Value : "")}:" +
              $"{(block.TryGetValue("data", out var data) && data is JsonStringValue bytes ? bytes.Value.Length : 0)}]"));

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
