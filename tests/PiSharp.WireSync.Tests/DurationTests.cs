using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using static Assert;

// wire.duration (1.1.0): ai event-stream.ts and agent-loop.ts durationMs, with an injected monotonic clock.
internal static class DurationTests
{
    internal static readonly ModelDescriptor Model = new("wire-model", "openai-responses", "authored-provider");
    private const string Usage = """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""";

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("wire.duration.assistant-message-json-placement-and-legacy-bytes", AssistantJson);
        yield return ("wire.duration.chat-run-times-final-message-with-injected-clock", ChatRunTiming);
        yield return ("wire.duration.scheduler-times-plain-execute-and-tool-result-json", SchedulerTiming);
        yield return ("wire.duration.invoker-times-adapter-execute-not-blocked-calls", InvokerTiming);
    }

    private static AssistantMessage Text(long timestamp, StopReason reason = StopReason.Stop) =>
        new(Model.Api, Model.Provider, Model.Id, timestamp, [new TextContent("hi")], TokenUsage.Zero, reason);

    private static Task AssistantJson()
    {
        // pi-ai builds { role, content, api, provider, model, usage, stopReason, timestamp } and JSON.stringify keeps that order.
        var legacy = "{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"api\":\"openai-responses\",\"provider\":\"authored-provider\"," +
            "\"model\":\"wire-model\",\"usage\":" + Usage + ",\"stopReason\":\"stop\",\"timestamp\":1000}";
        Equal(legacy, PiWireJson.WriteMessage(Text(1000)).ToString(), "untimed message in pi-ai key order");
        var timed = legacy[..^1] + ",\"durationMs\":1234}";
        Equal(timed, PiWireJson.WriteMessage(Text(1000) with { DurationMs = 1234 }).ToString(), "durationMs follows the finished fields");
        var read = PiWireJson.ReadMessage(JsonData.Parse(timed).Value);
        Equal(1234L, read.DurationMs); Check(read.ExtraProperties?.TryGet("durationMs", out _) != true, "typed duration stayed an extra field");
        Equal(timed, PiWireJson.WriteMessage(read).ToString(), "round trip");
        // A non-integral legacy value is not a source duration; it stays opaque and byte-preserved.
        var opaque = PiWireJson.ReadMessage(JsonData.Parse(legacy[..^1] + ",\"durationMs\":1.5}").Value);
        Check(opaque.DurationMs is null && opaque.ExtraProperties!.TryGet("durationMs", out var kept) && kept!.Value.GetRawText() == "1.5", "opaque duration lost");
        // A stream terminal frame carries the same message body.
        var done = PiWireJson.WriteEvent(new StreamDone(StopReason.Stop, Text(1000) with { DurationMs = 7 })).ToString();
        Equal("{\"type\":\"done\",\"reason\":\"stop\",\"message\":" + legacy[..^1] + ",\"durationMs\":7}}", done);
        return Task.CompletedTask;
    }

    private sealed class Stream(ManualClock clock, Func<ChatRequest, AssistantMessage> final, double elapsed) : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var message = final(request);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending, DurationMs = null });
            await Task.Yield();
            clock.Advance(elapsed);
            yield return new TextStarted(0, new("")); yield return new TextDelta(0, "hi"); yield return new TextEnded(0, "hi");
            yield return new StreamDone(message.StopReason, message);
        }
    }

    private static async Task<ChatResult> Run(ManualClock clock, Func<ChatRequest, AssistantMessage> final, double elapsed, bool timed = true,
        long requestTimestamp = 1000)
    {
        var client = new ChatClient(new Stream(clock, final, elapsed)) { TimeProvider = timed ? clock : null };
        await using var run = await client.StartAsync(new(Model, [new("user", JsonData.Parse("""{"role":"user","content":"go","timestamp":1}"""))], requestTimestamp));
        StreamTerminalEvent? terminal = null;
        await foreach (var frame in run.ReadEventsAsync()) if (frame is StreamTerminalEvent end) terminal = end;
        var result = await run.Completion;
        Check(terminal is not null && terminal.Message == result.Message, "terminal frame and completion disagree");
        return result;
    }

    private static async Task ChatRunTiming()
    {
        var clock = new ManualClock();
        // JavaScript Math.round: 1234.5 ms rounds up, 1234.4 ms rounds down.
        Equal(1235L, (await Run(clock, request => Text(request.Timestamp), 1234.5)).Message.DurationMs);
        Equal(1234L, (await Run(clock, request => Text(request.Timestamp), 1234.4)).Message.DurationMs);
        // Error terminals are timed like done terminals.
        var failed = await Run(clock, request => Text(request.Timestamp, StopReason.Error) with
            { ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"authored\"")) }, 30);
        Equal(30L, failed.Message.DurationMs);
        // Untimed: no clock; a message that already has a duration; a response that started before this request.
        Check((await Run(clock, request => Text(request.Timestamp), 50, timed: false)).Message.DurationMs is null, "timed without a clock");
        Equal(9L, (await Run(clock, request => Text(request.Timestamp) with { DurationMs = 9 }, 50)).Message.DurationMs);
        Check((await Run(clock, _ => Text(999), 50)).Message.DurationMs is null, "forwarded earlier response was timed");
        // The native profile's default request timestamp (0) is still this run's own response.
        Equal(50L, (await Run(clock, request => Text(request.Timestamp), 50, requestTimestamp: 0)).Message.DurationMs);
    }

    internal sealed class Probe(ManualClock clock, double elapsed, bool fail = false) : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken token)
        {
            clock.Advance(elapsed);
            if (fail) throw new InvalidOperationException("authored failure");
            return ValueTask.FromResult(ToolResult.Success("probed"));
        }
    }

    internal sealed class Collect : IAgentEventSink
    {
        public readonly List<AgentEvent> Events = [];
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { lock (Events) Events.Add(observation); return ValueTask.CompletedTask; }
    }

    private static AssistantMessage Calls(params string[] names) => new(Model.Api, Model.Provider, Model.Id, 1000,
        names.Select((name, index) => (AssistantContent)new ToolCallContent("call-" + index, name, JsonData.EmptyObject)).ToImmutableArray(),
        TokenUsage.Zero, StopReason.ToolUse);

    private static async Task SchedulerTiming()
    {
        foreach (var mode in new[] { ToolExecutionMode.Parallel, ToolExecutionMode.Sequential })
        {
            var clock = new ManualClock(); var sink = new Collect();
            var scheduler = new ToolBatchScheduler([new("probe", new Probe(clock, 250)), new("broken", new Probe(clock, 5, fail: true))], executionMode: mode)
                { TimeProvider = clock };
            var batch = await scheduler.RunAsync(Calls("probe", "missing", "broken"), sink);
            var ends = sink.Events.OfType<ToolExecutionEnded>().ToDictionary(end => end.Outcome.Invocation.Call.Name, end => end.Outcome);
            Equal(250L, ends["probe"].DurationMs, mode + " probe");
            Check(ends["missing"].DurationMs is null, "a tool that did not run was timed");
            Equal(5L, ends["broken"].DurationMs, "thrown execute keeps its time");
            Check(ends["broken"].IsError, "thrown execute was not an error");
            var messages = batch.Messages.ToDictionary(message => message.ToolName);
            Equal("""{"role":"toolResult","toolCallId":"call-0","toolName":"probe","content":[{"type":"text","text":"probed"}],"details":{},"isError":false,"durationMs":250,"timestamp":77}""",
                ToolResultMessageMaterializer.ToTranscript(messages["probe"], 77).WireBody.ToString());
            Equal("""{"role":"toolResult","toolCallId":"call-1","toolName":"missing","content":[{"type":"text","text":"Tool missing not found"}],"details":{},"isError":true,"timestamp":77}""",
                ToolResultMessageMaterializer.ToTranscript(messages["missing"], 77).WireBody.ToString());
        }
        // Without a clock nothing is recorded, so existing transcripts keep their bytes.
        var plain = await new ToolBatchScheduler([new("probe", new Probe(new ManualClock(), 250))]).RunAsync(Calls("probe"), new Collect());
        Check(plain.Outcomes.Single().DurationMs is null && !plain.Messages.Single().DurationMs.HasValue, "timed without a clock");
        Equal("""{"role":"toolResult","toolCallId":"call-0","toolName":"probe","content":[{"type":"text","text":"probed"}],"details":{},"isError":false,"timestamp":77}""",
            ToolResultMessageMaterializer.ToTranscript(plain.Messages.Single(), 77).WireBody.ToString());
    }

    internal sealed class Adapter(ManualClock clock, double elapsed) : IPreparedToolAdapter
    {
        public string Name => "probe";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(
            new PreparedToolAction("probe", "probe", PreparedToolActionKind.Extension, "probe", invocation.Call.Arguments, [], null,
                ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { clock.Advance(elapsed); return ValueTask.FromResult(ToolResult.Success("adapted")); }
    }

    internal sealed class Policy(bool allow, ManualClock? clock = null) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token)
        { clock?.Advance(1000); return ValueTask.FromResult(new ToolActionAuthorization(allow)); }
    }

    private static async Task InvokerTiming()
    {
        // Authorization time (1000 ms) is outside the measured execute() (40 ms), as in the source.
        var clock = new ManualClock();
        var allowed = await new ToolBatchScheduler([new("probe", new ToolInvoker([new Adapter(clock, 40)], new Policy(true, clock)))]) { TimeProvider = clock }
            .RunAsync(Calls("probe"), new Collect());
        Equal(40L, allowed.Outcomes.Single().DurationMs); Equal(40L, allowed.Messages.Single().DurationMs);
        var denied = await new ToolBatchScheduler([new("probe", new ToolInvoker([new Adapter(clock, 40)], new Policy(false, clock)))]) { TimeProvider = clock }
            .RunAsync(Calls("probe"), new Collect());
        Check(denied.Outcomes.Single().IsError && denied.Outcomes.Single().DurationMs is null, "a denied call that did not run was timed");
    }
}
