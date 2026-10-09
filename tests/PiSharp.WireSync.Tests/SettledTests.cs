using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using static Assert;

// wire.duration through a real session/RPC run, and wire.agent-settled-aborted (1.1.0, agent-session.ts, docs/json.md).
internal static class SettledTests
{
    private static readonly ModelDescriptor Model = DurationTests.Model;
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"wire-model","api":"openai-responses","provider":"authored-provider","name":"Authored wire model","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":131072,"maxTokens":8192,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("wire.duration.session-rpc-events-jsonl-and-reopen-round-trip", SessionRoundTrip);
        yield return ("wire.agent-settled.rpc-and-session-aborted-flag", Aborted);
    }

    private sealed record Step(AssistantMessage Message, double Elapsed, bool Pause = false)
    { public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }

    private sealed class Script(ManualClock clock, params Step[] steps) : IChatTransport
    {
        private int _next;
        public Step[] Steps => steps;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var step = steps[Interlocked.Increment(ref _next) - 1];
            yield return new StreamStarted(step.Message with { Content = [], StopReason = StopReason.Pending });
            step.Entered.TrySetResult();
            if (step.Pause) await step.Release.Task.WaitAsync(token);
            clock.Advance(step.Elapsed);
            for (var index = 0; index < step.Message.Content.Length; index++)
                if (step.Message.Content[index] is ToolCallContent call)
                { yield return new ToolCallStarted(index, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallDelta(index, call.Arguments.ToString()); yield return new ToolCallEnded(index, call); }
                else if (step.Message.Content[index] is TextContent text)
                { yield return new TextStarted(index, new("")); yield return new TextDelta(index, text.Text); yield return new TextEnded(index, text.Text); }
            yield return new StreamDone(step.Message.StopReason, step.Message);
        }
    }

    private static AssistantMessage Message(bool tool) => new(Model.Api, Model.Provider, Model.Id, 456,
        tool ? [new ToolCallContent("call-1", "probe", JsonData.EmptyObject)] : [new TextContent("done")], TokenUsage.Zero,
        tool ? StopReason.ToolUse : StopReason.Stop);

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Directory { get; init; }
        public required string Path { get; init; }
        public required PersistentAgentSession Session { get; init; }
        public required RpcSessionDispatcher Dispatcher { get; init; }
        public required MemoryStream Output { get; init; }
        public readonly List<SessionOperationSettled> Settled = [];
        public static async Task<Fixture> Create(IChatTransport transport, ManualClock? clock, ImmutableArray<ToolDefinition> tools)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-wire-sync-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory); var path = System.IO.Path.Combine(directory, "session.jsonl"); var ids = 0;
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "wire-header",
                timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
            var session = await PersistentAgentSession.CreateAsync(path, header, new AgentConfiguration(Model, transport, tools), () => 123,
                () => "wire-entry-" + Interlocked.Increment(ref ids), new(AgentOptions: new() { TimeProvider = clock }));
            var output = new MemoryStream();
            var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output), () => 123, [new(Model, ModelWire)]);
            var fixture = new Fixture { Directory = directory, Path = path, Session = session, Dispatcher = dispatcher, Output = output };
            fixture._subscription = session.SubscribeOperationEvents(new OperationSink(fixture.Settled));
            return fixture;
        }
        private IDisposable? _subscription;
        public Task Send(string type, string id, string? message = null) => Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(
            message is null ? new Dictionary<string, object?> { ["type"] = type, ["id"] = id } : new() { ["type"] = type, ["id"] = id, ["message"] = message })));
        public JsonElement[] Records()
        {
            lock (Output) return Encoding.UTF8.GetString(Output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        }
        private bool _closed;
        public async ValueTask CloseAsync()
        {
            if (_closed) return; _closed = true;
            _subscription?.Dispose();
            await Dispatcher.DisposeAsync();
            await Session.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            if (System.IO.Path.GetFileName(Directory).StartsWith("PiSharp-wire-sync-", StringComparison.Ordinal)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class OperationSink(List<SessionOperationSettled> settled) : ISessionOperationEventSink
    {
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token)
        { if (observation is SessionOperationSettled done) lock (settled) settled.Add(done); return ValueTask.CompletedTask; }
    }

    private static string Type(JsonElement record) => record.GetProperty("type").GetString()!;
    private static string LastProperty(JsonElement value) => value.EnumerateObject().Last().Name;

    private static async Task SessionRoundTrip()
    {
        var clock = new ManualClock();
        var transport = new Script(clock, new Step(Message(tool: true), 1500), new Step(Message(tool: false), 20));
        await using var fixture = await Fixture.Create(transport, clock, [new("probe", new DurationTests.Probe(clock, 250))]);
        await fixture.Send("prompt", "run", "go"); await fixture.Dispatcher.WaitForIdleAsync();
        var records = fixture.Records();
        var end = records.Single(record => Type(record) == "tool_execution_end");
        Equal("durationMs", LastProperty(end), "tool_execution_end field placement"); Equal(250, end.GetProperty("durationMs").GetInt32());
        Check(!end.GetProperty("isError").GetBoolean(), "tool failed");
        var toolResult = records.Last(record => Type(record) == "message_end" && record.GetProperty("message").GetProperty("role").GetString() == "toolResult");
        Equal("""{"role":"toolResult","toolCallId":"call-1","toolName":"probe","content":[{"type":"text","text":"probed"}],"details":{},"isError":false,"durationMs":250,"timestamp":123}""",
            toolResult.GetProperty("message").GetRawText());
        var assistants = records.Where(record => Type(record) == "message_end" && record.GetProperty("message").GetProperty("role").GetString() == "assistant")
            .Select(record => record.GetProperty("message")).ToArray();
        Equal(2, assistants.Length);
        Equal(1500, assistants[0].GetProperty("durationMs").GetInt32()); Equal(20, assistants[1].GetProperty("durationMs").GetInt32());
        // event-stream.ts end() sets durationMs after the finished fields; agent-loop.ts then adds thinkingLevel (Object.assign).
        Check(assistants.All(message => message.EnumerateObject().Select(property => property.Name).TakeLast(2).SequenceEqual(["durationMs", "thinkingLevel"]) ||
            LastProperty(message) == "durationMs"), "assistant durationMs placement");
        Equal("""{"type":"agent_settled","aborted":false}""", records.Single(record => Type(record) == "agent_settled").GetRawText());
        Check(fixture.Settled.Count == 1 && !fixture.Settled[0].Aborted, "session settlement reported an abort");

        // The durable JSONL keeps the same bodies; a reopened store and the typed reader preserve them.
        await fixture.CloseAsync();
        await using var reopened = await SessionLogStore.OpenAsync(fixture.Path);
        var stored = reopened.Snapshot.Entries.Where(entry => entry.Type == "message").Select(entry => entry.WireBody.Value.GetProperty("message")).ToArray();
        Equal(toolResult.GetProperty("message").GetRawText(), stored.Single(message => message.GetProperty("role").GetString() == "toolResult").GetRawText());
        var typed = stored.Where(message => message.GetProperty("role").GetString() == "assistant").Select(message => PiWireJson.ReadMessage(message)).ToArray();
        Equal(1500L, typed[0].DurationMs); Equal(20L, typed[1].DurationMs);
        Equal(assistants[1].GetRawText(), PiWireJson.WriteMessage(typed[1]).ToString(), "typed assistant round trip");
    }

    private static async Task Aborted()
    {
        var clock = new ManualClock();
        var transport = new Script(clock, new Step(Message(tool: false), 10, Pause: true));
        await using var fixture = await Fixture.Create(transport, clock, []);
        await fixture.Send("prompt", "run", "go"); await transport.Steps[0].Entered.Task;
        await fixture.Send("abort", "stop"); await fixture.Dispatcher.WaitForIdleAsync();
        Equal("""{"type":"agent_settled","aborted":true}""", fixture.Records().Single(record => Type(record) == "agent_settled").GetRawText());
        Check(fixture.Settled.Count == 1 && fixture.Settled[0].Aborted && fixture.Settled[0].Status == "cancelled", "session settlement did not report the abort");
    }
}
