using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;

internal static class CompletionsNativeDiagnosticTests
{
    private static readonly ModelDescriptor Model = new("native-diagnostic", "openai-completions", "authored-offline");
    private const string Finish = "{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}";
    private const string Tool = "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"owned-call\",\"function\":{\"name\":\"probe\",\"arguments\":\"{}\"}}]}}]}";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("completions-native-diagnostics.all-eight-real-terminal-result-codes", AllCodes);
        yield return ("completions-native-diagnostics.primary-cleanup-precedence", CleanupPrecedence);
        yield return ("completions-native-diagnostics.joined-physical-failure-and-source-success", PhysicalOwnership);
        yield return ("completions-native-diagnostics.closed-enum-admission", InvalidAdmission);
        yield return ("completions-native-diagnostics.unknown-source-fields-and-old-constructors", UnknownFieldsAndConsumers);
        yield return ("completions-native-diagnostics.agent-loop-propagation-no-tool-effects", AgentPropagation);
    }

    private static async Task AllCodes()
    {
        foreach (var code in Enum.GetValues<OpenAICompletionsWireFailure>())
        foreach (var wrapped in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var probe = code switch
            {
                OpenAICompletionsWireFailure.SourceFailed => new Probe([]) { ReadFailure = true },
                OpenAICompletionsWireFailure.MalformedStream => new Probe(["{\"choices\":\"invalid\"}"]),
                OpenAICompletionsWireFailure.UnexpectedEof => new Probe(["{\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}"]),
                OpenAICompletionsWireFailure.ResourceLimit => new Probe(["{\"choices\":[]}", Finish]),
                OpenAICompletionsWireFailure.ProviderError => new Probe(["{\"error\":{\"message\":\"PRIVATE_PROVIDER_BODY\"}}"]),
                OpenAICompletionsWireFailure.Cancelled => new Probe([]) { CancelOnRead = cancellation.Cancel },
                OpenAICompletionsWireFailure.CleanupFailed => new Probe([Finish]) { FailDisposal = true },
                OpenAICompletionsWireFailure.UnsupportedFeature => new Probe(["{\"choices\":[{\"delta\":{\"audio\":{\"opaque\":true}}}]}" ]),
                _ => throw new InvalidOperationException("Missing native-code test input.")
            };
            var mapper = new OpenAICompletionsWireSource((_, _) => probe,
                code == OpenAICompletionsWireFailure.ResourceLimit ? new(MaximumChunks: 1) : null);
            var frames = new List<StreamEvent>(); ChatResult? result = null;
            if (wrapped)
            {
                await using var run = await new ChatClient(mapper).StartAsync(Request(), cancellation.Token);
                await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
                result = await run.Completion.WaitAsync(Deadline);
            }
            else await foreach (var frame in mapper.StreamAsync(Request(), cancellation.Token)) frames.Add(frame);
            var terminal = frames.OfType<StreamTerminalEvent>().Single();
            Check(terminal is StreamError && terminal.NativeDiagnostic?.Adapter == NativeChatAdapter.OpenAICompletions &&
                terminal.NativeDiagnostic.Code.ToString() == code.ToString(), "Actual native terminal lost fine code: " + code);
            Check(terminal.Reason == (code == OpenAICompletionsWireFailure.Cancelled ? StopReason.Aborted : StopReason.Error), "Native stop reason changed.");
            Clean(terminal.Message);
            if (result is not null)
            {
                Check(result.NativeDiagnostic == terminal.NativeDiagnostic && result.Failure?.NativeDiagnostic == terminal.NativeDiagnostic &&
                    result.NativeCleanupDiagnostic == terminal.NativeCleanupDiagnostic &&
                    result.Failure!.Kind == (code == OpenAICompletionsWireFailure.Cancelled ? ChatFailureKind.Cancelled : ChatFailureKind.Provider),
                    "Actual terminal/result/coarse failure diverged: " + code);
                Check(PiWireJson.WriteMessage(result.Message).ToString() == PiWireJson.WriteMessage(terminal.Message).ToString(), "Terminal/result messages differ.");
            }
            Check(probe.Disposals == 1, "Failure did not join its one owned source disposal.");
        }
    }

    private static async Task CleanupPrecedence()
    {
        foreach (var cancelled in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var probe = new Probe(["{\"choices\":\"invalid\"}"]) { FailDisposal = true, CancelOnRead = cancelled ? cancellation.Cancel : null };
            await using var run = await new ChatClient(new OpenAICompletionsWireSource((_, _) => probe)).StartAsync(Request(), cancellation.Token);
            var frames = new List<StreamEvent>(); await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
            var result = await run.Completion.WaitAsync(Deadline); var terminal = frames.OfType<StreamTerminalEvent>().Single();
            Check(result.NativeDiagnostic?.Code == (cancelled ? NativeChatFailureCode.Cancelled : NativeChatFailureCode.MalformedStream) &&
                result.Failure?.NativeDiagnostic == result.NativeDiagnostic && terminal.NativeDiagnostic == result.NativeDiagnostic &&
                result.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed && terminal.NativeCleanupDiagnostic == result.NativeCleanupDiagnostic,
                "Owned cleanup replaced or hid the primary semantic failure.");
            Clean(result.Message); Check(probe.Disposals == 1, "Secondary cleanup was not actually joined.");
        }
    }

    private static async Task PhysicalOwnership()
    {
        foreach (var sourceView in new[] { false, true })
        {
            var body = new HeldBody(Encoding.UTF8.GetBytes("data: " + Finish + "\n\ndata: [DONE]\n\n"));
            using var handler = new Handler(body); using var client = new HttpClient(handler);
            var transport = new CompletionsHttpSseTransport(client, (_, _) => new(HttpMethod.Post, "https://diagnostic.invalid/completions"));
            ChatResult result;
            if (sourceView)
            {
                await using var run = await transport.StartAsync(Request());
                var drain = DrainSource(run);
                try
                {
                    await body.CloseEntered.Task.WaitAsync(Deadline); var semantic = await run.SourceResult.WaitAsync(Deadline);
                    Check(semantic.Snapshot.Raw.Value.GetProperty("value").GetProperty("stopReason").GetString() == "stop" &&
                        !run.CanonicalCompletion.IsCompleted && !run.CleanupCompletion.IsCompleted, "Source success acquired held native authority.");
                    body.ReleaseClose.TrySetResult(); await drain.WaitAsync(Deadline);
                    result = await run.CanonicalCompletion.WaitAsync(Deadline); var cleanup = await run.CleanupCompletion.WaitAsync(Deadline);
                    Check(!cleanup.Succeeded && cleanup.Failure?.NativeDiagnostic?.Code == NativeChatFailureCode.CleanupFailed,
                        "Actual cleanup outcome lost its separate bounded diagnostic.");
                }
                finally { body.ReleaseClose.TrySetResult(); await Observe(drain); }
            }
            else
            {
                await using var run = await new ChatClient(transport).StartAsync(Request());
                var frames = new ConcurrentQueue<StreamEvent>(); var drain = Drain(run, frames);
                try
                {
                    await body.CloseEntered.Task.WaitAsync(Deadline);
                    Check(!run.Completion.IsCompleted && !frames.OfType<StreamTerminalEvent>().Any(), "Native terminal escaped held physical cleanup.");
                    body.ReleaseClose.TrySetResult(); await drain.WaitAsync(Deadline); result = await run.Completion.WaitAsync(Deadline);
                    var terminal = frames.OfType<StreamTerminalEvent>().Single();
                    Check(terminal.NativeDiagnostic == result.NativeDiagnostic && terminal.NativeCleanupDiagnostic == result.NativeCleanupDiagnostic,
                        "Default native terminal/result diagnostics differ after joined cleanup.");
                }
                finally { body.ReleaseClose.TrySetResult(); await Observe(drain); }
            }
            Check(result.NativeDiagnostic?.Code == NativeChatFailureCode.SourceFailed && result.Failure?.Kind == ChatFailureKind.Provider &&
                result.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed && result.Message.StopReason == StopReason.Error && body.AsyncCloses == 1,
                "Physical cleanup fault changed existing primary classification or acquired success.");
            Clean(result.Message);
        }
    }

    private static async Task InvalidAdmission()
    {
        foreach (var invalid in new[] { new NativeChatDiagnostic(0, NativeChatFailureCode.SourceFailed),
            new NativeChatDiagnostic((NativeChatAdapter)99, NativeChatFailureCode.SourceFailed),
            new NativeChatDiagnostic(NativeChatAdapter.OpenAICompletions, 0), new NativeChatDiagnostic(NativeChatAdapter.OpenAICompletions, (NativeChatFailureCode)99) })
        foreach (var cleanup in new[] { false, true })
        {
            var message = Message(StopReason.Error);
            var terminal = new StreamError(StopReason.Error, message)
            { NativeDiagnostic = cleanup ? null : invalid, NativeCleanupDiagnostic = cleanup ? invalid : null };
            await using var run = await new ChatClient(new Frames([terminal])).StartAsync(Request());
            var frames = new List<StreamEvent>(); await foreach (var frame in run.ReadEventsAsync()) frames.Add(frame);
            var result = await run.Completion.WaitAsync(Deadline);
            Check(result.Failure?.Kind == ChatFailureKind.MalformedStream && result.NativeDiagnostic is null && result.NativeCleanupDiagnostic is null &&
                result.Message.StopReason == StopReason.Error && frames.OfType<StreamTerminalEvent>().Count() == 1,
                "Undefined native enum data crossed producer admission.");
        }
    }

    private static async Task UnknownFieldsAndConsumers()
    {
        var message = Message(StopReason.Error) with { ExtraProperties = JsonFields.Empty
            .Set("openAICompletionsFailure", JsonData.Parse("\"extension-owned\""))
            .Set("opaque", JsonData.Parse("{\"number\":1.00,\"keep\":null,\"ordered\":[2,1]}")) };
        var failure = new ChatFailure(ChatFailureKind.Provider, "safe"); var (kind, text) = failure;
        var result = new ChatResult(message, failure); var (oldMessage, oldFailure) = result;
        var diagnostic = new NativeChatDiagnostic(NativeChatAdapter.OpenAICompletions, NativeChatFailureCode.SourceFailed);
        var (adapter, code) = diagnostic;
        var terminal = new StreamError(StopReason.Error, message) { NativeDiagnostic = diagnostic };
        var (reason, oldTerminalMessage, properties) = terminal;
        Check(kind == ChatFailureKind.Provider && text == "safe" && oldMessage == message && oldFailure == failure &&
            reason == StopReason.Error && oldTerminalMessage == message && properties is null && adapter == NativeChatAdapter.OpenAICompletions && code == NativeChatFailureCode.SourceFailed,
            "Existing positional constructor/deconstruction changed.");
        foreach (var actualDiagnostic in new NativeChatDiagnostic?[] { null, diagnostic })
        {
            var originalTerminal = terminal with { NativeDiagnostic = actualDiagnostic };
            await using var run = await new ChatClient(new Frames([originalTerminal])).StartAsync(Request());
            await foreach (var frame in run.ReadEventsAsync())
                Check(ReferenceEquals(originalTerminal, frame), "Unchanged immutable native terminal identity was replaced.");
            var actual = await run.Completion;
            var json = PiWireJson.WriteMessage(actual.Message);
            Check(json.Value.GetProperty("openAICompletionsFailure").GetString() == "extension-owned" &&
                json.Value.GetProperty("opaque").GetRawText() == "{\"number\":1.00,\"keep\":null,\"ordered\":[2,1]}" &&
                actual.NativeDiagnostic == actualDiagnostic && actual.Failure?.NativeDiagnostic == actualDiagnostic,
                "Unknown/imported source fields were erased or trusted as native diagnostics.");
            Check(!PiWireJson.WriteEvent(terminal).ToString().Contains("NativeDiagnostic", StringComparison.OrdinalIgnoreCase), "Typed native metadata entered source event wire.");
        }
    }

    private static async Task AgentPropagation()
    {
        foreach (var malformed in new[] { false, true })
        {
            var probe = new Probe(malformed ? [Tool, "{\"choices\":\"invalid\"}"] : [Tool, Finish]) { FailDisposal = true };
            var adapter = new NeverTool(); var policy = new NeverPolicy();
            var scheduler = new ToolBatchScheduler([new("probe", new ToolInvoker([adapter], policy))]);
            var runner = new AgentLoopRunner(new TurnRunner(new ChatClient(new OpenAICompletionsWireSource((_, _) => probe)), scheduler), Model, () => 123);
            var events = new List<AgentEvent>();
            var result = await runner.RunAsync([], new((snapshot, _) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript, 123))),
                new Sink(events)).WaitAsync(Deadline);
            var turn = result.Turns.Single().Result;
            var observed = events.OfType<AgentLoopTurnEnded>().Single().Turn.Result;
            Check(result.Reason == AgentLoopStopReason.ChatFailure && adapter.Preparations == 0 && adapter.Executions == 0 && policy.Authorizations == 0 &&
                turn.Chat.NativeDiagnostic?.Code == (malformed ? NativeChatFailureCode.MalformedStream : NativeChatFailureCode.CleanupFailed) &&
                turn.Chat.NativeCleanupDiagnostic?.Code == NativeChatFailureCode.CleanupFailed &&
                observed.Chat.NativeDiagnostic == turn.Chat.NativeDiagnostic && observed.Chat.NativeCleanupDiagnostic == turn.Chat.NativeCleanupDiagnostic &&
                turn.Chat.Failure?.NativeDiagnostic == turn.Chat.NativeDiagnostic && probe.Disposals == 1,
                "Native diagnostics were lost through agent turns or authorized tool effects.");
            Clean(turn.Chat.Message);
            Check(!result.Transcript.Any(entry => entry.WireBody.Value.TryGetProperty("openAICompletionsFailure", out _)), "Agent transcript retained port-origin diagnostic data.");
        }
    }

    private static ChatRequest Request() => new(Model, [], 123);
    private static AssistantMessage Message(StopReason reason) => new(Model.Api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, reason);
    private static void Clean(AssistantMessage message)
    {
        var json = PiWireJson.WriteMessage(message);
        Check(!json.Value.TryGetProperty("openAICompletionsFailure", out _) && !json.ToString().Contains("PRIVATE", StringComparison.Ordinal),
            "Native diagnostic or private failure text entered the source message.");
    }
    private static async Task Drain(ChatRun run, ConcurrentQueue<StreamEvent> frames) { await foreach (var frame in run.ReadEventsAsync()) frames.Enqueue(frame); }
    private static async Task DrainSource(CompletionsRun run) { while (!(await run.NextAsync()).Done) { } }
    private static async Task Observe(Task task) { try { await task; } catch { } }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Probe(string[] values) : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>
    {
        private int _index = -1;
        public bool ReadFailure, FailDisposal; public Action? CancelOnRead; public int Disposals;
        public JsonData Current => JsonData.Parse(values[_index]);
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken token = default) => this;
        public ValueTask<bool> MoveNextAsync()
        { CancelOnRead?.Invoke(); if (ReadFailure) throw new IOException("PRIVATE_SOURCE_READ"); return ValueTask.FromResult(++_index < values.Length); }
        public ValueTask DisposeAsync()
        { Disposals++; return FailDisposal ? ValueTask.FromException(new IOException("PRIVATE_SOURCE_CLEANUP")) : ValueTask.CompletedTask; }
    }
    private sealed class Frames(StreamEvent[] frames) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.CompletedTask; foreach (var frame in frames) yield return frame; }
    }
    private sealed class Handler(HeldBody body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }
    private sealed class HeldBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AsyncCloses;
        public override async ValueTask DisposeAsync()
        { AsyncCloses++; CloseEntered.TrySetResult(); await ReleaseClose.Task; base.Dispose(true); throw new IOException("PRIVATE_PHYSICAL_CLEANUP"); }
    }
    private sealed class NeverTool : IPreparedToolAdapter
    {
        public string Name => "probe"; public int Preparations, Executions;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        { Preparations++; throw new InvalidOperationException("Failed provider authorized preparation."); }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { Executions++; throw new InvalidOperationException("Failed provider authorized execution."); }
    }
    private sealed class NeverPolicy : IToolActionPolicy
    {
        public int Authorizations;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Authorizations++; throw new InvalidOperationException("Failed provider authorized policy admission."); }
    }
    private sealed class Sink(List<AgentEvent> events) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { events.Add(observation); return ValueTask.CompletedTask; } }
}
