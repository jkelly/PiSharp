using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Context;

internal static class CompletionsSourceEventProjectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("completions-rpc-model", "openai-completions", "offline-authored");
    private const string First = """{"choices":[{"delta":{"tool_calls":[{"index":9,"function":{"arguments":"{\"value\":null,\"n\":"}}]}}]}""";
    private const string Last = """{"choices":[{"delta":{"tool_calls":[{"index":9,"id":"real-call","function":{"name":"read","arguments":"1}"}}]},"finish_reason":"tool_calls"}]}""";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("rpc.completions-default-provisional-start-header-suppression-and-final-policy", DefaultProjection);
        yield return ("rpc.completions-unfilled-and-bounded-source-deny-final-authority", FailedProjection);
        yield return ("rpc.completions-truncated-final-arguments-finalize-through-parse-streaming-json", TruncatedFinalProjection);
    }

    private static async Task DefaultProjection()
    {
        await using var fixture = await Fixture.Create([First, Last]);
        await fixture.Run();
        Equal(1, fixture.Probe.Provisional); Equal(1, fixture.Probe.Headers); Equal(1, fixture.Probe.Ended);
        var records = fixture.Output.Records();
        var updates = records.Where(record => Type(record) == "message_update")
            .Select(record => record.Value.GetProperty("assistantMessageEvent")).ToArray();
        Check(updates.Select(value => value.GetProperty("type").GetString()).SequenceEqual(
            new[] { "toolcall_start", "toolcall_delta", "toolcall_delta", "toolcall_end" }),
            "An identity fill invented a public update or lost the original source push order.");
        Equal("", updates[0].GetProperty("id").GetString()); Equal("", updates[0].GetProperty("toolName").GetString());
        Equal(0, updates[0].GetProperty("contentIndex").GetInt32());
        Check(updates.All(value => !value.TryGetProperty("partial", out _) && !value.TryGetProperty("sourceEmissionSnapshot", out _)),
            "Owned source comparison metadata bypassed compact RPC output.");
        var final = updates[^1].GetProperty("toolCall");
        Equal("real-call", final.GetProperty("id").GetString()); Equal("read", final.GetProperty("name").GetString());
        Equal("{\"value\":null,\"n\":1}", final.GetProperty("arguments").GetRawText());
        Equal(1, fixture.Adapter.Preparations); Equal(1, fixture.Policy.Decisions); Equal(0, fixture.Adapter.Executions);
        var prepared = fixture.Adapter.Prepared ?? throw new InvalidOperationException("Strict final invocation was not prepared.");
        Equal("real-call", prepared.Call.Id);
        Equal("{\"value\":null,\"n\":1}", prepared.Call.Arguments.ToString());
        Check(records.Single(record => Type(record) == "tool_execution_end").Value.GetProperty("isError").GetBoolean(),
            "Completed provider progress bypassed the selected final tool policy.");
        Equal(1, records.Count(record => Type(record) == "agent_settled"));
        Check(!fixture.Dispatcher.Completion.IsCompleted, "A supported source operation failed the default RPC session.");
    }

    private static async Task FailedProjection()
    {
        foreach (var scenario in new[] { "unfilled", "bounded" })
        {
            var chunks = scenario == "unfilled" ? new[] { First,
                """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" } : new[] { First, Last };
            await using var fixture = await Fixture.Create(chunks,
                scenario == "bounded" ? new(MaximumContentCharacters: 64) : null);
            await fixture.Run();
            Equal(0, fixture.Probe.Ended); Equal(0, fixture.Adapter.Preparations); Equal(0, fixture.Policy.Decisions); Equal(0, fixture.Adapter.Executions);
            var records = fixture.Output.Records();
            Check(!records.Any(record => Type(record) is "tool_execution_start" or "tool_execution_end"),
                "Failed provisional input reached tool execution admission: " + scenario);
            Check(!records.Where(record => Type(record) == "message_update").Any(record =>
                record.Value.GetProperty("assistantMessageEvent").GetProperty("type").GetString() == "toolcall_end"),
                "Failed source input acquired a successful tool end: " + scenario);
            var assistant = fixture.Session.Snapshot.Context.Messages.Single(message => message.Role == "assistant").WireBody.Value;
            Equal("error", assistant.GetProperty("stopReason").GetString());
            Check(!assistant.TryGetProperty("openAICompletionsFailure",out _),"Native diagnostic leaked into the Pi assistant body.");
            var diagnostic=records.Single(record=>Type(record)=="pisharp_chat_diagnostic").Value.GetProperty("data");
            Equal(scenario == "bounded" ? "ResourceLimit" : "MalformedStream", diagnostic.GetProperty("diagnostic").GetProperty("code").GetString());
            Equal("declared-native-record",diagnostic.GetProperty("provenance").GetString());
            var acknowledged=fixture.Session.GetNativeDiagnostics().Entries.Single();
            Equal(acknowledged.AssistantEntryId,diagnostic.GetProperty("assistantEntryId").GetString());
            Equal(acknowledged.RecordEntryId,diagnostic.GetProperty("recordEntryId").GetString());
            Check(Array.FindIndex(records,record=>Type(record)=="turn_end")<Array.FindIndex(records,record=>Type(record)=="pisharp_chat_diagnostic")&&
                Array.FindIndex(records,record=>Type(record)=="pisharp_chat_diagnostic")<Array.FindIndex(records,record=>Type(record)=="agent_settled"),"Native diagnostic bypassed acknowledged turn/final settlement boundaries.");
            Equal(1, records.Count(record => Type(record) == "agent_settled"));
            Check(!fixture.Dispatcher.Completion.IsCompleted, "A sanitized provider failure became a fatal RPC route: " + scenario);
        }
    }

    // openai-completions.ts finalizes block.arguments = parseStreamingJson(block.partialArgs): the unterminated {"value":null,"n":1
    // becomes {"value":null,"n":1} and the tool call ends normally, reaching the final tool policy like a complete one.
    private static async Task TruncatedFinalProjection()
    {
        await using var fixture = await Fixture.Create([First,
            """{"choices":[{"delta":{"tool_calls":[{"index":9,"id":"real-call","function":{"name":"read","arguments":"1"}}]},"finish_reason":"tool_calls"}]}"""]);
        await fixture.Run();
        Equal(1, fixture.Probe.Ended); Equal(1, fixture.Adapter.Preparations);
        var prepared = fixture.Adapter.Prepared ?? throw new InvalidOperationException("Final invocation was not prepared.");
        Equal("{\"value\":null,\"n\":1}", prepared.Call.Arguments.ToString());
        Check(!fixture.Dispatcher.Completion.IsCompleted, "A repaired final argument failed the default RPC session.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory; private readonly IDisposable _subscription;
        public PersistentAgentSession Session { get; } public RpcSessionDispatcher Dispatcher { get; }
        public Capture Output { get; } public Adapter Adapter { get; } public DenyPolicy Policy { get; } public Probe Probe { get; }
        private Fixture(string directory, PersistentAgentSession session, RpcSessionDispatcher dispatcher, Capture output,
            Adapter adapter, DenyPolicy policy, Probe probe, IDisposable subscription)
        { _directory = directory; Session = session; Dispatcher = dispatcher; Output = output; Adapter = adapter; Policy = policy; Probe = probe; _subscription = subscription; }
        public static async Task<Fixture> Create(string[] chunks, OpenAICompletionsWireOptions? options = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PiSharp-completions-rpc-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
                { type = "session", version = 3, id = "completions-header", timestamp = "2026-10-01T00:00:00.000Z", cwd = directory }));
            var adapter = new Adapter(); var policy = new DenyPolicy(); var invoker = new ToolInvoker([adapter], policy); var ids = 0;
            var mapper = new OpenAICompletionsWireSource((_, token) => Chunks(chunks, token), options);
            var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header,
                new(Model, mapper, [new("read", invoker)], Hooks: new(FinishTurnDecision: (_, _) => ValueTask.FromResult(AgentLoopFinishAction.End))),
                () => 123, () => "completions-entry-" + Interlocked.Increment(ref ids));
            var probe = new Probe(adapter); var subscription = session.Subscribe(probe); var output = new Capture();
            var model = JsonData.Parse("""{"id":"completions-rpc-model","api":"openai-completions","provider":"offline-authored","name":"Authored Completions RPC model","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":4096,"maxTokens":128,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
            var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(output, ownership: JsonlStreamOwnership.Borrowed), () => 123, [new(Model, model)]);
            return new(directory, session, dispatcher, output, adapter, policy, probe, subscription);
        }
        public async Task Run()
        {
            await Dispatcher.SubmitAsync(JsonData.Parse("""{"type":"prompt","id":"completions-prompt","message":"offline tool progress"}"""));
            await Dispatcher.WaitForIdleAsync().WaitAsync(Deadline);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Dispatcher.DisposeAsync().AsTask().WaitAsync(Deadline); } finally { await Session.DisposeAsync(); _subscription.Dispose(); }
            var target = Path.GetFullPath(_directory); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(target) != temporary || !Path.GetFileName(target).StartsWith("PiSharp-completions-rpc-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside owned Completions RPC fixture.");
            Directory.Delete(target, recursive: true);
        }
    }

    private static async IAsyncEnumerable<JsonData> Chunks(string[] chunks, [EnumeratorCancellation] CancellationToken token)
    { foreach (var chunk in chunks) { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(chunk); } await Task.CompletedTask; }
    private sealed class Probe(Adapter adapter) : IAgentEventSink
    {
        public int Provisional, Headers, Ended;
        public ValueTask EmitAsync(AgentEvent observation, CancellationToken token)
        {
            if (observation is TurnStreamObserved stream)
                switch (stream.Event)
                {
                    case ToolCallProvisionalStarted start:
                        Provisional++; Equal("", start.ToolCall.Id); Equal("", start.ToolCall.Name); Equal(0, adapter.Preparations); break;
                    case ToolCallHeaderUpdated: Headers++; Equal(0, adapter.Preparations); break;
                    case ToolCallEnded: Ended++; Equal(0, adapter.Preparations); break;
                }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "read"; public int Preparations, Executions; public ToolInvocation? Prepared;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        { Preparations++; Prepared = invocation; return ValueTask.FromResult(new PreparedToolAction(Name, "read", PreparedToolActionKind.Path,
            "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty)); }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { Executions++; return ValueTask.FromResult(ToolResult.Success("must never execute")); }
    }
    private sealed class DenyPolicy : IToolActionPolicy
    {
        public int Decisions;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Decisions++; return ValueTask.FromResult(new ToolActionAuthorization(false)); }
    }
    private sealed class Capture : Stream
    {
        private readonly List<JsonData> _records = []; private JsonData? _written;
        public JsonData[] Records() { lock (_records) return _records.ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); var text = Encoding.UTF8.GetString(bytes.Span);
            Check(text.EndsWith('\n') && text.Count(character => character == '\n') == 1, "Invalid complete RPC JSONL frame.");
            _written = JsonData.Parse(text); return ValueTask.CompletedTask; }
        public override Task FlushAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); lock (_records) _records.Add(_written!); _written = null; return Task.CompletedTask; }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private static string Type(JsonData value) => value.Value.GetProperty("type").GetString()!;
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ: " + expected + " / " + actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
