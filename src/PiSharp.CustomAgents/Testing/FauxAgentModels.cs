// Decision 0012: a scripted IDurableModels for tests and offline demos of custom agents, grown from samples/Durable.Examples/ScriptedModels.cs
// (itself pi-ai's faux provider, reduced). A request is answered by the first matching route, then by the next queued reply. Routes see the
// request's model, the active tool names and system sections (folded from the transcript's incremental system messages), the last user
// text and the last tool result, so one instance can serve an orchestrator and its subagents at once.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Testing;

/// <summary>One model request as a faux route sees it.</summary>
public sealed class FauxRequest
{
    internal FauxRequest(int index, string modelId, ImmutableArray<JsonValue> messages)
    {
        Index = index;
        ModelId = modelId;
        Messages = messages;
        var tools = new List<string>();
        var results = new List<FauxToolResult>();
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages.OfType<JsonObjectValue>())
        {
            var role = Text(message, "role");
            if (role == "system")
            {
                if (message.TryGetValue("sections", out var patch) && patch is JsonObjectValue changed)
                    foreach (var (key, value) in changed)
                        if (value is JsonStringValue text) sections[key] = text.Value; else sections.Remove(key);
                if (message.TryGetValue("toolsRemoved", out var removed) && removed is JsonArrayValue gone)
                    foreach (var tool in gone.OfType<JsonObjectValue>()) tools.Remove(Text(tool, "name") ?? "");
                if (message.TryGetValue("toolsAdded", out var added) && added is JsonArrayValue fresh)
                    foreach (var tool in fresh.OfType<JsonObjectValue>())
                        if (Text(tool, "name") is { } name && !tools.Contains(name)) tools.Add(name);
            }
            else if (role == "user")
            {
                LastUserText = ContentText(message);
                LastToolResult = null;
                LastToolName = null;
                results.Clear();
            }
            else if (role == "toolResult")
            {
                LastToolResult = ContentText(message);
                LastToolName = Text(message, "toolName");
                results.Add(new FauxToolResult(LastToolName ?? "", LastToolResult, message.TryGetValue("isError", out var error) && error is JsonBooleanValue { Value: true }));
            }
            else if (role == "assistant")
            {
                LastToolResult = null;
                LastToolName = null;
                results.Clear();
            }
        }
        Tools = tools;
        ToolResults = results;
        Sections = sections;
    }

    /// <summary>Zero-based order of the request among every request this instance answered.</summary>
    public int Index { get; }
    /// <summary>The requested model ID.</summary>
    public string ModelId { get; }
    /// <summary>The pi-ai wire messages.</summary>
    public ImmutableArray<JsonValue> Messages { get; }
    /// <summary>The tools offered to the model, in offer order.</summary>
    public IReadOnlyList<string> Tools { get; }
    /// <summary>The system prompt sections by key.</summary>
    public IReadOnlyDictionary<string, string> Sections { get; }
    /// <summary>All section texts, joined.</summary>
    public string SystemText => string.Join("\n", Sections.Values);
    /// <summary>The text of the last user message; null when none.</summary>
    public string? LastUserText { get; }
    /// <summary>The text of the trailing tool result, when the request follows a tool round; else null.</summary>
    public string? LastToolResult { get; }
    /// <summary>The tool name of <see cref="LastToolResult"/>.</summary>
    public string? LastToolName { get; }
    /// <summary>Every result of the trailing tool round, in order; empty when the request does not follow one.</summary>
    public IReadOnlyList<FauxToolResult> ToolResults { get; }
    /// <summary>True when the request follows a tool round.</summary>
    public bool AfterTools => LastToolResult is not null;

    private static string? Text(JsonObjectValue value, string key) => value.TryGetValue(key, out var item) && item is JsonStringValue text ? text.Value : null;

    private static string ContentText(JsonObjectValue message) => message.TryGetValue("content", out var content) ? content switch
    {
        JsonStringValue text => text.Value,
        JsonArrayValue blocks => string.Concat(blocks.OfType<JsonObjectValue>().Where(block => Text(block, "type") == "text").Select(block => Text(block, "text"))),
        _ => "",
    } : "";
}

/// <summary>One tool result a request carries.</summary>
public sealed record FauxToolResult(string Name, string Text, bool IsError);

/// <summary>One scripted answer: a text, tool calls, or a provider error.</summary>
public sealed record FauxReply
{
    private FauxReply() { }

    /// <summary>The text of a text answer.</summary>
    public string? Text { get; private init; }
    /// <summary>The tool calls of a tool-calling answer; empty otherwise.</summary>
    public ImmutableArray<ToolCallContent> Calls { get; private init; } = [];
    /// <summary>The provider error of a failed answer.</summary>
    public string? Error { get; private init; }
    /// <summary>What the reply waits for before it streams; null streams at once.</summary>
    public Task? Release { get; private init; }

    /// <summary>A text answer.</summary>
    public static FauxReply Answer(string text) => new() { Text = text ?? throw new ArgumentNullException(nameof(text)) };
    /// <summary>An answer that calls one tool with JSON <paramref name="arguments"/>.</summary>
    public static FauxReply ToolCall(string name, string arguments = "{}", string? id = null) => new() { Calls = [Call(name, arguments, id)] };
    /// <summary>An answer that calls several tools, in order.</summary>
    public static FauxReply ToolCalls(params (string Name, string Arguments)[] calls) =>
        new() { Calls = [.. calls.Select(call => Call(call.Name, call.Arguments, null))] };
    /// <summary>A provider error (stop reason <c>error</c>).</summary>
    public static FauxReply Fail(string message) => new() { Error = message ?? throw new ArgumentNullException(nameof(message)) };
    /// <summary>This reply, streamed only once <paramref name="release"/> completes; the conversation stays busy until then.</summary>
    public FauxReply HeldUntil(Task release) => this with { Release = release ?? throw new ArgumentNullException(nameof(release)) };
    /// <summary>A text answer.</summary>
    public static implicit operator FauxReply(string text) => Answer(text);

    private static ToolCallContent Call(string name, string arguments, string? id) =>
        new(id ?? "call_" + Guid.NewGuid().ToString("N")[..12], name ?? throw new ArgumentNullException(nameof(name)), JsonData.Parse(arguments));
}

/// <summary>
/// Scripted model access for custom agents: provider <c>faux</c>, any listed model ID. Answers each request with the first route that
/// matches it (routes stay until removed, unless added with <c>once</c>), else the next queued reply, else a provider error. Thread-safe.
/// </summary>
public sealed class FauxAgentModels : IDurableModels
{
    private readonly Lock gate = new();
    private readonly List<Route> routes = [];
    private readonly ConcurrentQueue<FauxReply> queue = new();
    private readonly List<FauxRequest> requests = [];
    private readonly ImmutableArray<string> modelIds;

    /// <summary>The faux provider name.</summary>
    public const string ProviderName = "faux";

    /// <summary>Serves <paramref name="modelIds"/> (default <c>faux-1</c>).</summary>
    public FauxAgentModels(params string[] modelIds) => this.modelIds = modelIds.Length == 0 ? ["faux-1"] : [.. modelIds];

    /// <summary>A model reference to one of this instance's models (default: the first).</summary>
    public ModelRef Model(string? modelId = null) => new(ProviderName, modelId ?? modelIds[0]);

    /// <summary>Every request answered so far, in order.</summary>
    public IReadOnlyList<FauxRequest> Requests
    {
        get { lock (gate) return [.. requests]; }
    }

    /// <summary>How many requests were answered.</summary>
    public int RequestCount
    {
        get { lock (gate) return requests.Count; }
    }

    /// <summary>Answers requests matching <paramref name="match"/> with <paramref name="reply"/>; with <paramref name="once"/>, only the first.</summary>
    public FauxAgentModels On(Func<FauxRequest, bool> match, Func<FauxRequest, FauxReply> reply, bool once = false)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(reply);
        lock (gate) routes.Add(new Route(match, reply, once));
        return this;
    }

    /// <summary>Answers requests to <paramref name="modelId"/> with <paramref name="reply"/>.</summary>
    public FauxAgentModels OnModel(string modelId, Func<FauxRequest, FauxReply> reply) => On(request => request.ModelId == modelId, reply);

    /// <summary>Answers requests whose system sections contain <paramref name="text"/> with <paramref name="reply"/>.</summary>
    public FauxAgentModels OnSystem(string text, Func<FauxRequest, FauxReply> reply) =>
        On(request => request.SystemText.Contains(text, StringComparison.Ordinal), reply);

    /// <summary>Queues replies for requests no route matches.</summary>
    public FauxAgentModels Enqueue(params FauxReply[] replies)
    {
        foreach (var reply in replies) queue.Enqueue(reply);
        return this;
    }

    /// <inheritdoc/>
    public DurableModel? GetModel(string provider, string modelId) => provider == ProviderName && modelIds.Contains(modelId)
        ? new DurableModel
        {
            Id = modelId, Name = modelId, Api = "faux", Provider = ProviderName, BaseUrl = "http://localhost:0", Input = ["text", "image"],
            ContextWindow = 128000, MaxTokens = 16384,
        }
        : null;

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        FauxRequest request;
        FauxReply? reply = null;
        lock (gate)
        {
            request = new FauxRequest(requests.Count, model.Id, messages);
            requests.Add(request);
            foreach (var route in routes)
            {
                if (!route.Match(request)) continue;
                reply = route.Reply(request);
                if (route.Once) routes.Remove(route);
                break;
            }
        }
        if (reply is null && queue.TryDequeue(out var queued)) reply = queued;
        return StreamAsync(model, reply ?? FauxReply.Fail("No faux reply for request " + request.Index), cancellationToken);
    }

    private static async IAsyncEnumerable<StreamEvent> StreamAsync(DurableModel model, FauxReply reply,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], TokenUsage.Zero,
            StopReason.Pending);
        if (reply.Release is { } release) await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (reply.Error is { } error)
        {
            yield return new StreamError(StopReason.Error, partial with
            {
                StopReason = StopReason.Error,
                ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonValue.String(error).ToJson())),
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

    /// <inheritdoc/>
    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
        CancellationToken cancellationToken)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in StreamSimple(model, messages, options, cancellationToken).ConfigureAwait(false))
            if (item is StreamTerminalEvent end) terminal = end;
        return terminal!.Message;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Faux models do not defer responses.");

    /// <inheritdoc/>
    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException("Faux models do not defer responses."));

    /// <inheritdoc/>
    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed record Route(Func<FauxRequest, bool> Match, Func<FauxRequest, FauxReply> Reply, bool Once);
}
