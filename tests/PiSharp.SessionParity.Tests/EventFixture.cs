using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

/// <summary>A durable session in a temp directory over an authored scripted transport, its replaceable owner, an extension
/// registry over the owner's snapshots, and (on request) an RPC dispatcher writing to a capture stream.</summary>
internal sealed class EventFixture : IAsyncDisposable
{
    internal static readonly ModelDescriptor Model = new("parity-events", "openai-responses", "fixture");
    internal static readonly ModelDescriptor Model2 = new("parity-events-2", "openai-responses", "fixture");
    internal static readonly JsonData Model2Wire = JsonData.Parse("""{"id":"parity-events-2","api":"openai-responses","provider":"fixture","name":"Parity events 2","baseUrl":"https://offline.invalid","reasoning":true,"input":["text"],"contextWindow":128000,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    internal static readonly JsonData ProbeDeclaration = JsonData.Parse("""{"name":"probe","description":"Authored probe tool","parameters":{"type":"object","properties":{}}}""");
    internal static readonly JsonData ModelWire = JsonData.Parse("""{"id":"parity-events","api":"openai-responses","provider":"fixture","name":"Parity events","baseUrl":"https://offline.invalid","reasoning":true,"input":["text"],"contextWindow":128000,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    private static readonly SessionEntryCodec Codec = new();
    internal string Root { get; } = Program.Temp("events");
    internal string Source => Path.Combine(Root, "source.jsonl");
    internal ScriptTransport Transport { get; } = new();
    internal ProbeAdapter Probe { get; } = new();
    internal ReplaceableAgentSession Owner { get; private set; } = null!;
    internal PersistentAgentSession Session => Owner.Current.Session;
    internal ExtensionRegistry Registry { get; private set; } = null!;
    internal Capture Output { get; } = new();
    internal RpcSessionDispatcher? Rpc { get; private set; }
    internal List<ExtensionEventDiagnostic> Diagnostics { get; } = [];
    private long ticks = 1_800_000_000_000; private int ids;
    internal long Clock() => Interlocked.Increment(ref ticks);

    internal static Task<EventFixture> CreateAsync(params AssistantMessage[] responses) => CreateAsync(false, responses);
    internal static Task<EventFixture> CreateAsync(bool withTool, params AssistantMessage[] responses) => CreateWithOptionsAsync(withTool, null, responses);
    internal static async Task<EventFixture> CreateWithOptionsAsync(bool withTool, SessionRuntimeRegistryOptions? options, params AssistantMessage[] responses)
    {
        var f = new EventFixture(); foreach (var response in responses) f.Transport.Responses.Enqueue(response);
        f.Transport.Clock = f.Clock;
        var runtime = new SessionRuntimeRegistry([new(Model, f.Transport), new(Model2, f.Transport)],
            withTool ? [new(ProbeDeclaration, f.Probe)] : [], new NoPolicy(), options);
        var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "parity-source", timestamp = "2026-10-08T00:00:00.000Z", cwd = f.Root }));
        await using (var store = await SessionLogStore.CreateNewAsync(f.Source, header))
            await store.AppendAsync([Entry("u0", null, User(new string('x', 8000))), Entry("a0", "u0", Assistant("first")),
                Entry("u1", "a0", User(new string('y', 8000))), Entry("a1", "u1", Assistant("second"))]);
        var lifecycle = new PersistentSessionLifecycle(runtime, f.Clock, () => "entry-" + Interlocked.Increment(ref f.ids),
            new PersistentAgentSessionOptions(AgentOptions: new() { TimeProvider = TimeProvider.System }), catalog: new SessionCatalog([new("fixtures", f.Root)]));
        f.Owner = lifecycle.Attach(await lifecycle.OpenAsync(new(f.Source), Model));
        var views = new NativeSessionSnapshotProvider(); views.Attach(f.Owner); f.Registry = new(null, null, views);
        return f;
    }

    internal ValueTask Report(ExtensionEventDiagnostic diagnostic, CancellationToken token) { lock (Diagnostics) Diagnostics.Add(diagnostic); return ValueTask.CompletedTask; }
    internal Task Activate(Action<IExtensionRegistry> initialize) => Registry.ActivateAsync("parity-owner", new Plugin(initialize));
    internal RpcSessionDispatcher StartRpc(ISessionSummaryGenerator? summary = null) =>
        Rpc = new RpcSessionDispatcher(Session, new JsonlWriter(Output, ownership: JsonlStreamOwnership.Borrowed), Clock,
            [new(Model, ModelWire), new(Model2, Model2Wire)], new(), RpcSessionOwnership.Borrowed, sessionOwner: Owner, summaryGenerator: summary);
    internal async Task PromptAsync(string id = "go", string message = "go")
    {
        await Rpc!.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(new { type = "prompt", id, message })));
        await Rpc.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
    internal JsonData[] Frames() => Output.Frames();
    internal static string Type(JsonData frame) => frame.Value.GetProperty("type").GetString()!;

    internal static AssistantMessage Response(StopReason reason = StopReason.Stop, string text = "ok", string? error = null) =>
        new(Model.Api, Model.Provider, Model.Id, 0, reason == StopReason.Error ? [] : [new TextContent(text)], TokenUsage.Zero, reason,
            error is null ? null : JsonFields.Empty.Set("errorMessage", JsonData.FromElement(JsonSerializer.SerializeToElement(error))));
    private static SessionEntry Entry(string id, string? parent, TranscriptEntry message) => Codec.Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2026-10-08T00:00:00.000Z", message = message.WireBody.Value }));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 10 })));
    private static TranscriptEntry Assistant(string text) => new("assistant", PiWireJson.WriteMessage(Response(text: text) with { Timestamp = 10 }));

    public async ValueTask DisposeAsync()
    {
        try { if (Rpc is not null) await Rpc.DisposeAsync(); }
        finally
        {
            try { if (Registry is not null) await Registry.DisposeAsync(); }
            finally { if (Owner is not null) await Owner.DisposeAsync(); }
        }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    internal sealed class ProbeAdapter : IPreparedToolAdapter
    {
        internal int Executions; internal ToolResult Result = ToolResult.Success("probe output"); public string Name => "probe";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(Name, "probe",
            PreparedToolActionKind.Path, "/authored", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) { Executions++; return ValueTask.FromResult(Result); }
    }
    internal static AssistantMessage ToolCall(string id = "call-1") =>
        new(Model.Api, Model.Provider, Model.Id, 0, [new ToolCallContent(id, "probe", JsonData.EmptyObject)], TokenUsage.Zero, StopReason.ToolUse);

    /// <summary>Authored responses in order: a successful response streams one text block; an error response has no content.</summary>
    internal sealed class ScriptTransport : IChatTransport, IThinkingLevelTransport
    {
        internal readonly Queue<AssistantMessage> Responses = new(); internal Func<long> Clock = () => 0; internal int Calls;
        internal NativeChatDiagnostic? ErrorDiagnostic; internal readonly List<ChatRequest> Requests = [];
        /// <summary>Runs while a request is in its provider phase (before the response streams).</summary>
        internal Func<Task>? DuringStream;
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) => ["off", "minimal", "low", "medium", "high"];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; lock (Requests) Requests.Add(request);
            var final = (Responses.Count == 0 ? Response() : Responses.Dequeue()) with { Timestamp = Clock() };
            if (DuringStream is { } during) await during();
            await Task.CompletedTask;
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (final.StopReason == StopReason.Error) { yield return new StreamError(StopReason.Error, final) { NativeDiagnostic = ErrorDiagnostic }; yield break; }
            if (final.Content[0] is ToolCallContent call)
            { yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); yield return new StreamDone(final.StopReason, final); yield break; }
            var text = ((TextContent)final.Content[0]).Text;
            yield return new TextStarted(0, new("")); yield return new TextDelta(0, text); yield return new TextEnded(0, text);
            yield return new StreamDone(final.StopReason, final);
        }
    }

    internal sealed class Capture : Stream
    {
        private readonly List<JsonData> records = []; private JsonData? written;
        internal JsonData[] Frames() { lock (records) return records.ToArray(); }
        internal string[] Lines() { lock (records) return records.Select(record => record.ToString()).ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        { written = JsonData.Parse(Encoding.UTF8.GetString(bytes.Span)); return ValueTask.CompletedTask; }
        public override Task FlushAsync(CancellationToken token) { lock (records) if (written is not null) records.Add(written); written = null; return Task.CompletedTask; }
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
}
