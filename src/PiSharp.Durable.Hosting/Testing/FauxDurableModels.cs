// PiSharp-only (decision 0008). A model that needs no provider, for tests and UI work at no cost. Streaming follows the durable examples'
// ScriptedModels (samples/Durable.Examples/ScriptedModels.cs, after pi-ai's faux provider); the prompt grammar adapts durable-pi's faux model
// (apps/dpi-backend/src/faux.ts, MIT): "!command" runs bash, "slow:" streams slowly, a tool result is answered "Tool finished: …", anything
// else is echoed. "tool: <name> <json>" calls any tool.
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting.Testing;

/// <summary>One faux chat model.</summary>
public sealed record FauxModel(string Id, string Name, double ContextWindow, double MaxTokens = 8192);

/// <summary>
/// Model access without a provider. Each request answers the newest user message or tool result:
/// <list type="bullet">
/// <item><c>!command</c> calls <c>bash</c> with the command.</item>
/// <item><c>tool: name {json}</c> calls any tool with the JSON object as its arguments (default <c>{}</c>).</item>
/// <item><c>slow: text</c> answers the text a word at a time, <see cref="SlowDelay"/> apart.</item>
/// <item>A tool result is answered <c>Tool finished: &lt;tool&gt;: &lt;its first 200 characters&gt;</c>.</item>
/// <item>Anything else is answered <c>Echo: &lt;text&gt;</c>.</item>
/// </list>
/// Usage is zero.
/// </summary>
public sealed class FauxDurableModels : IDurableModels
{
    /// <summary>The faux provider's ID.</summary>
    public const string ProviderId = "faux";

    /// <summary>The default model, <c>faux/echo</c>, with a 128000-token window.</summary>
    public static FauxModel Echo { get; } = new("echo", "Echo", 128_000);

    /// <summary><c>faux/echo-small</c>, with a 40000-token window.</summary>
    public static FauxModel EchoSmall { get; } = new("echo-small", "Echo small", 40_000);

    /// <summary>A provider with <see cref="Echo"/> and <see cref="EchoSmall"/>.</summary>
    public FauxDurableModels() : this([Echo, EchoSmall]) { }

    /// <summary>A provider with these models.</summary>
    public FauxDurableModels(IEnumerable<FauxModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        Models = [.. models];
    }

    /// <summary>The provider's models.</summary>
    public ImmutableArray<FauxModel> Models { get; }

    /// <summary>The pause between the words of a <c>slow:</c> answer.</summary>
    public TimeSpan SlowDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary><c>faux/echo</c>.</summary>
    public static ModelRef EchoRef { get; } = new(ProviderId, Echo.Id);

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) =>
        provider == ProviderId && Models.FirstOrDefault(model => model.Id == modelId) is { } found ? ToDurable(found) : null;

    /// <inheritdoc/>
    public ImmutableArray<JsonObjectValue> GetModelsOfType(string type, string? provider) =>
        type == "chat" && provider is null or ProviderId ? [.. Models.Select(Json)] : [];

    /// <inheritdoc/>
    public Task<ImmutableArray<JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider, CancellationToken cancellationToken) =>
        Task.FromResult(GetModelsOfType(type, provider));

    /// <inheritdoc/>
    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], TokenUsage.Zero,
            StopReason.Pending);
        var reply = Reply(messages);
        yield return new StreamStarted(partial);
        if (reply.Call is { } call)
        {
            yield return new ToolCallStarted(0, call with { Arguments = JsonData.Parse("{}") });
            yield return new ToolCallDelta(0, call.Arguments.ToString());
            yield return new ToolCallEnded(0, call);
            yield return new StreamDone(StopReason.ToolUse, partial with { Content = [call], StopReason = StopReason.ToolUse });
            yield break;
        }
        yield return new TextStarted(0, new TextContent(""));
        if (reply.Slow)
        {
            var words = reply.Text.Split(' ');
            for (var index = 0; index < words.Length; index++)
            {
                await Task.Delay(SlowDelay, cancellationToken);
                yield return new TextDelta(0, index == 0 ? words[index] : " " + words[index]);
            }
        }
        else
        {
            yield return new TextDelta(0, reply.Text);
        }
        yield return new TextEnded(0, reply.Text);
        yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(reply.Text)], StopReason = StopReason.Stop });
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
        throw new NotSupportedException("The faux model does not defer responses.");

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException("The faux model does not defer responses."));

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>What the model answers to <paramref name="messages"/> (pi-ai wire messages).</summary>
    internal static FauxReply Reply(ImmutableArray<JsonValue> messages)
    {
        var last = messages.OfType<JsonObjectValue>().LastOrDefault(message => Role(message) is "user" or "toolResult");
        if (last is null) return FauxReply.Answer("Echo: ");
        if (Role(last) == "toolResult")
        {
            var output = TextOf(last).Trim();
            return FauxReply.Answer($"Tool finished: {String(last, "toolName") ?? "tool"}: {(output.Length > 200 ? output[..200] : output)}");
        }
        var input = TextOf(last).Trim();
        if (input.StartsWith('!')) return FauxReply.Calling("bash", JsonValue.Object([new("command", input[1..].Trim())]));
        if (input.StartsWith("tool:", StringComparison.Ordinal))
        {
            var rest = input[5..].Trim();
            var space = rest.IndexOfAny([' ', '\n', '\t']);
            var toolName = space < 0 ? rest : rest[..space];
            JsonObjectValue arguments;
            try
            {
                arguments = space < 0 ? JsonObjectValue.Empty : JsonValue.Parse(rest[space..].Trim()) as JsonObjectValue ?? JsonObjectValue.Empty;
            }
            catch (Exception)
            {
                arguments = JsonObjectValue.Empty;
            }
            return FauxReply.Calling(toolName, arguments);
        }
        if (input.StartsWith("slow:", StringComparison.Ordinal)) return FauxReply.Answer(input[5..].Trim(), slow: true);
        return FauxReply.Answer("Echo: " + input);
    }

    private static DurableModel ToDurable(FauxModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Api = ProviderId,
        Provider = ProviderId,
        BaseUrl = "http://localhost:0",
        Input = ["text"],
        ContextWindow = model.ContextWindow,
        MaxTokens = model.MaxTokens,
        Wire = Json(model),
    };

    private static JsonObjectValue Json(FauxModel model) => JsonValue.Object(
    [
        new("id", model.Id),
        new("name", model.Name),
        new("api", ProviderId),
        new("provider", ProviderId),
        new("baseUrl", "http://localhost:0"),
        new("reasoning", false),
        new("input", JsonValue.Array(["text"])),
        new("cost", JsonValue.Object([new("input", 0.0), new("output", 0.0), new("cacheRead", 0.0), new("cacheWrite", 0.0)])),
        new("contextWindow", model.ContextWindow),
        new("maxTokens", model.MaxTokens),
    ]);

    private static string? Role(JsonObjectValue message) => String(message, "role");

    private static string? String(JsonObjectValue json, string key) => json.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    /// <summary>A message's text: its string content, or its text blocks joined.</summary>
    private static string TextOf(JsonObjectValue message) => message.TryGetValue("content", out var content) ? content switch
    {
        JsonStringValue text => text.Value,
        JsonArrayValue blocks => string.Join("\n", blocks.OfType<JsonObjectValue>()
            .Where(block => String(block, "type") == "text").Select(block => String(block, "text") ?? "")),
        _ => "",
    } : "";
}

/// <summary>What <see cref="FauxDurableModels"/> answers: a text, or one tool call.</summary>
internal sealed record FauxReply(string Text, ToolCallContent? Call, bool Slow)
{
    public static FauxReply Answer(string text, bool slow = false) => new(text, null, slow);

    public static FauxReply Calling(string tool, JsonObjectValue arguments) =>
        new("", new ToolCallContent("faux:" + Guid.NewGuid().ToString("N")[..12], tool, JsonData.Parse(arguments.ToJson())), false);
}
