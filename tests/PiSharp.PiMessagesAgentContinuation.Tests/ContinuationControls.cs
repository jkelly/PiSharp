using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

namespace PiSharp.PiMessagesAgentContinuation.Tests;

// NEW source-only controls. The caller supplies its own bounded report owner; no file output here.
public static class ContinuationControls
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> Cases(Action<Observation>? observe = null) =>
    [ ("pi-messages.public-direct-agent-two-turn-final-identity", () => Run(false, observe)),
      ("pi-messages.public-simple-agent-two-turn-final-identity", () => Run(true, observe)) ];

    public sealed record Original(string Name, Task Task)
    {
        public bool Joined { get; internal set; }
        public AggregateException? OriginalException { get; internal set; }
        public Exception? DirectException { get; internal set; }
    }
    public sealed record Observation(bool Simple, IReadOnlyList<JsonData> Requests,
        ToolCallContent StartedCall, ToolCallContent FinalCall, IReadOnlyList<Original> Originals,
        IReadOnlyList<FaultNode> FaultGraph, int FirstResponseDisposeCount);
    public sealed record FaultNode(int Id, string Type, string Message, string? StackTrace, int[] Children);
    public sealed class ControlFailure : Exception
    {
        public IReadOnlyList<Original> Originals { get; }
        public IReadOnlyList<Exception> FaultRoots { get; }
        internal ControlFailure(IReadOnlyList<Original> originals, Exception[] faults)
            : base("Public Pi Messages Agent continuation control failed; raw originals retained.", new AggregateException(faults)) { Originals = originals; FaultRoots = faults; }
    }
    private sealed class Inventory
    {
        private readonly List<Original> _records = [];
        private readonly object _gate = new();
        public Original Add(string name, Task task)
        {
            lock (_gate)
            {
                var found = _records.FirstOrDefault(row => ReferenceEquals(row.Task, task));
                if (found is not null) return found;
                var record = new Original(name, task); _records.Add(record); return record;
            }
        }
        public Original[] Snapshot() { lock (_gate) return _records.ToArray(); }
        public async Task<T> Join<T>(string name, Task<T> task)
        {
            var record = Add(name, task); record.Joined = true;
            try { return await task.ConfigureAwait(false); }
            catch (Exception error) { record.OriginalException = task.Exception; record.DirectException = error; throw; }
        }
        public async Task Join(Original record)
        {
            record.Joined = true;
            try { await record.Task.ConfigureAwait(false); }
            catch (Exception error) { record.OriginalException = record.Task.Exception; record.DirectException = error; }
        }
        public async Task JoinPending()
        {
            while (true)
            {
                var pending = Snapshot().Where(row => !row.Joined).ToArray();
                if (pending.Length == 0) return;
                foreach (var record in pending) await Join(record).ConfigureAwait(false);
            }
        }
    }
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string anchor)
    { if (!value) throw new InvalidOperationException(anchor); }

    private static async Task Run(bool simple, Action<Observation>? observe)
    {
        var inventory = new Inventory(); var faults = new List<Exception>();
        var sourceFaults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var tool = new HeldTool(inventory); var resultMessageEntered = Gate<bool>(); var resultMessageRelease = Gate<bool>();
        var requests = new List<JsonData>(); var bodies = new List<TrackedContent>();
        ToolCallContent? started = null; ToolCallContent? ended = null; bool firstTerminal = false;
        var handler = new Handler(inventory, requests, bodies);
        var client = new HttpClient(handler, disposeHandler: false); NativeAgent? agent = null;
        Task<AgentLoopResult>? run = null;
        try
        {
            var catalog = FrozenModelCatalog.ReadProviderJson("inert-provider", Encoding.UTF8.GetBytes(Catalog));
            var options = new PiMessagesProviderOptions("inert-key") { EnvironmentLookup = _ => null, SessionId = "inert-session",
                Hooks = new PiMessagesLifecycleHooks { OnEventPublished = item =>
                {
                    if (item is ToolCallStarted begin) started = begin.ToolCall;
                    if (item is ToolCallEnded end) ended = end.ToolCall;
                } } };
            var provider = simple ? PiMessagesModelProvider.CreateSimple(catalog, client, options) : PiMessagesModelProvider.CreateDirect(catalog, client, options);
            var model = provider.Models.Single();
            Check(provider.GetCatalogModel(model).Raw.Value.GetProperty("baseUrl").GetString() == "https://pi-messages.invalid/base/", "catalog-bound metadata");
            agent = new NativeAgent(new(model, provider.Transport, [new ToolDefinition("other", tool)]), () => 123,
                new Sink((item, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (item is TurnStreamObserved { Event: StreamError error })
                    {
                        if (error.NativeSourceTask is { } source) inventory.Add("stream-error-source-original", source);
                        if (error.NativeSourceException is { } direct) sourceFaults.Enqueue(direct);
                        if (error.NativeCleanupTasks is { } cleanupTasks)
                            foreach (var cleanup in cleanupTasks) inventory.Add("stream-error-cleanup-original", cleanup);
                        if (error.NativeCleanupExceptions is { } cleanupErrors) foreach (var cleanupError in cleanupErrors) sourceFaults.Enqueue(cleanupError);
                    }
                    if (item is TurnStreamObserved { Event: StreamDone { Reason: StopReason.ToolUse } })
                    { Check(bodies[0].DisposeCount > 0, "first HTTP content cleanup before terminal"); firstTerminal = true; }
                    if (item is ToolResultMessageEnded)
                    {
                        inventory.Add("agent.tool-result-ended-callback", resultMessageRelease.Task);
                        resultMessageEntered.TrySetResult(true); return new ValueTask(resultMessageRelease.Task);
                    }
                    return ValueTask.CompletedTask;
                }), new AgentOptions(StreamCapacity: 1));
            run = agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"continue\",\"timestamp\":123}")));
            inventory.Add("agent.prompt-two-turn-original", run);
            await SignalOrOriginal(tool.Entered.Task, run, "executor entry").ConfigureAwait(false);
            Check(firstTerminal && requests.Count == 1 && !run.IsCompleted && !tool.Result.Task.IsCompleted, "held tool blocks second HTTP turn");
            var invocation = tool.Invocation ?? throw new InvalidOperationException("missing genuine tool invocation");
            Check(invocation.Call.Id == "final-call" && invocation.Call.Name == "other" && invocation.Call.Arguments.Value.GetProperty("value").GetInt32() == 7, "finalized replacement invocation");
            Check(invocation.AssistantMessage.StopReason == StopReason.ToolUse, "execution only after toolUse terminal");
            tool.Result.TrySetResult(ToolResult.Success("tool-result-seven"));
            await SignalOrOriginal(resultMessageEntered.Task, run, "tool-result callback entry").ConfigureAwait(false);
            Check(requests.Count == 1 && !run.IsCompleted && !resultMessageRelease.Task.IsCompleted, "held genuine Agent callback blocks second HTTP turn");
            resultMessageRelease.TrySetResult(true);
            var result = await inventory.Join("agent.prompt-two-turn-original", run).ConfigureAwait(false);
            Check(result.Turns.Length == 2 && result.Turns.All(turn => turn.Result.Chat.Failure is null), "genuine two successful Agent turns");
            Check(requests.Count == 2 && tool.Calls == 1, "exact physical sends and tool invocation");
            Check(result.Turns[1].Result.Chat.Message.Content.OfType<TextContent>().Single().Text == "continued", "second assistant continuation");
            var next = requests[1].Value.GetProperty("context").GetProperty("messages");
            var assistant = next.EnumerateArray().Single(row => row.GetProperty("role").GetString() == "assistant");
            var call = assistant.GetProperty("content")[0];
            Check(call.GetProperty("id").GetString() == "final-call" && call.GetProperty("name").GetString() == "other" && call.GetProperty("arguments").GetProperty("value").GetInt32() == 7, "actual second request assistant uses final identity/arguments");
            var toolResult = next.EnumerateArray().Single(row => row.GetProperty("role").GetString() == "toolResult");
            Check(toolResult.GetProperty("toolCallId").GetString() == "final-call" && toolResult.GetProperty("toolName").GetString() == "other", "actual second request matched tool result");
            Check(requests.All(row => row.Value.GetProperty("model").GetString() == model.Id && row.Value.GetProperty("options").GetProperty("sessionId").GetString() == "inert-session"), "public provider options reach both HTTP turns");
            Check(started is { Id: "provisional-call", Name: "inspect" } && !started.Arguments.Value.EnumerateObject().Any(), "native earlier immutable call remains unchanged");
            Check(ended is { Id: "final-call", Name: "other" } && !ReferenceEquals(started, ended), "native final owned value differs from JS mutable alias");
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            tool.Result.TrySetResult(ToolResult.Success("tool-result-seven")); resultMessageRelease.TrySetResult(true);
            // Release all test holds before stopping and join every captured actual, even completed Tasks.
            try { agent?.Abort(); } catch (Exception error) { faults.Add(error); }
            await inventory.JoinPending().ConfigureAwait(false);
            if (agent is not null)
            {
                try { inventory.Add("agent.dispose-original", agent.DisposeAsync().AsTask()); }
                catch (Exception error) { faults.Add(error); }
            }
            await inventory.JoinPending().ConfigureAwait(false);
            try { client.Dispose(); } catch (Exception error) { faults.Add(error); }
            try { handler.Dispose(); } catch (Exception error) { faults.Add(error); }
        }
        faults.AddRange(sourceFaults);
        var originals = inventory.Snapshot();
        foreach (var row in originals)
        {
            if (row.OriginalException is not null) faults.Add(row.OriginalException);
            if (row.DirectException is not null) faults.Add(row.DirectException);
        }
        try
        {
            Check(originals.All(row => row.Joined && row.Task.IsCompletedSuccessfully), "all captured originals joined successfully");
            if (faults.Count == 0) observe?.Invoke(new(simple, requests.ToArray(), started ?? throw new InvalidOperationException("missing start"),
                ended ?? throw new InvalidOperationException("missing final call"), originals, Graph(faults), bodies[0].DisposeCount));
        }
        catch (Exception error) { faults.Add(error); }
        if (faults.Count != 0) throw new ControlFailure(originals, faults.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray());
    }
    private static async Task SignalOrOriginal(Task signal, Task original, string anchor)
    {
        var selected = await Task.WhenAny(signal, original).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (!ReferenceEquals(selected, signal)) throw new InvalidOperationException("actual Agent finished before " + anchor);
        await signal.ConfigureAwait(false);
    }
    public static IReadOnlyList<FaultNode> Graph(IEnumerable<Exception> roots)
    {
        var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance); var queue = new Queue<Exception>();
        int Add(Exception error)
        {
            if (ids.TryGetValue(error, out var id)) return id;
            if (ids.Count >= 1024) throw new InvalidOperationException("fault graph node bound; raw references remain in ControlFailure");
            id = ids.Count; ids.Add(error, id); queue.Enqueue(error); return id;
        }
        foreach (var error in roots) Add(error);
        var nodes = new List<FaultNode>(); var edges = 0;
        while (queue.TryDequeue(out var error))
        {
            var children = error is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : error.InnerException is { } inner ? [inner] : Array.Empty<Exception>();
            edges += children.Length;
            if (edges > 4096) throw new InvalidOperationException("fault graph edge bound; raw references remain in ControlFailure");
            nodes.Add(new(ids[error], error.GetType().FullName ?? error.GetType().Name, error.Message, error.StackTrace, children.Select(Add).ToArray()));
        }
        return nodes;
    }
    private sealed class HeldTool(Inventory inventory) : IToolExecutor
    {
        public readonly TaskCompletionSource<bool> Entered = Gate<bool>();
        public readonly TaskCompletionSource<ToolResult> Result = Gate<ToolResult>();
        public ToolInvocation? Invocation; public int Calls;
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Invocation = invocation;
            inventory.Add("tool.executor-original", Result.Task); Entered.TrySetResult(true); return new(Result.Task);
        }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent item, CancellationToken token) => emit(item, token); }
    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public int DisposeCount;
        protected override void Dispose(bool disposing) { if (disposing) DisposeCount++; base.Dispose(disposing); }
    }
    private sealed class Handler(Inventory inventory, List<JsonData> requests, List<TrackedContent> bodies) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var ordinal = ++_calls; var original = Send(request, token); inventory.Add("http.send-" + ordinal, original); return original;
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            var read = request.Content?.ReadAsStringAsync(token) ?? throw new InvalidOperationException("missing request body");
            var body = await inventory.Join("http.request-body-read-" + (requests.Count + 1), read).ConfigureAwait(false);
            requests.Add(JsonData.Parse(body)); Check(requests.Count <= 2, "no third physical HTTP request");
            var content = new TrackedContent(Encoding.UTF8.GetBytes(requests.Count == 1 ? FirstWire : SecondWire)); bodies.Add(content);
            content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private const string Catalog = """
    {"pi-messages":{"chat:inert-model":{"type":"chat","id":"inert-model","provider":"inert-provider","api":"pi-messages","name":"Inert","baseUrl":"https://pi-messages.invalid/base/","reasoning":true,"input":["text"],"contextWindow":65536,"maxTokens":8192,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}}}
    """;
    private const string Usage = "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";
    private static readonly string FirstWire = string.Join("\n\n", new[] {
        "data: {\"type\":\"start\"}",
        "data: {\"type\":\"toolcall_start\",\"contentIndex\":0,\"id\":\"provisional-call\",\"toolName\":\"inspect\"}",
        "data: {\"type\":\"toolcall_delta\",\"contentIndex\":0,\"delta\":\"{\\\"value\\\":1}\"}",
        "data: {\"type\":\"toolcall_end\",\"contentIndex\":0,\"toolCall\":{\"type\":\"toolCall\",\"id\":\"final-call\",\"name\":\"other\",\"arguments\":{\"value\":7}}}",
        "data: {\"type\":\"done\",\"reason\":\"toolUse\"," + Usage + "}" }) + "\n\n";
    private static readonly string SecondWire = string.Join("\n\n", new[] {
        "data: {\"type\":\"start\"}", "data: {\"type\":\"text_start\",\"contentIndex\":0}",
        "data: {\"type\":\"text_delta\",\"contentIndex\":0,\"delta\":\"continued\"}",
        "data: {\"type\":\"text_end\",\"contentIndex\":0,\"content\":\"continued\"}",
        "data: {\"type\":\"done\",\"reason\":\"stop\"," + Usage + "}" }) + "\n\n";
}
