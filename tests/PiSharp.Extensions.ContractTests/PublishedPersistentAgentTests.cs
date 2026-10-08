using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class PublishedPersistentAgentTests
{
    private static readonly ModelDescriptor Model = new("published-extension-model", "openai-responses", "authored-provider");
    private static readonly JsonData Complete = JsonData.Parse("""{"echo":true,"probeHostAction":true,"content":[{"type":"text","text":"published answer\u0000\u03c0"}],"details":{"number":7,"nil":null},"usage":{"tokens":3,"ordered":[2,1,null]},"structuredContent":{"programOnly":true},"opaqueResult":{"unknown":null,"ordered":[3,1]},"isError":false}""");

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("published native extension executes through HTTP Agent durable acknowledgement and reopen without replay", DurableReopen);
        yield return ("published native extension final policy and complete schema admission prevent callback effects", FinalAdmission);
        yield return ("published native callback cancellation and concurrent session/loader close join execution and shutdown", CancellationAndClose);
    }

    private static async Task DurableReopen()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var files = await PublishedFiles.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(files.LoaderOptions);
        var loaded = await files.LoadAsync(loader, registry);
        var effects = 0;
        AppContext.SetData("PiSharp.PublishedFixture.HostAction", (Action)(() => effects++));
        using var policy = new Policy();
        var binding = new ExtensionAgentBinding(registry, policy, ValidateEmptySchema);
        Equal(loaded.OwnerGeneration, binding.Registrations.Single().OwnerGeneration);
        Equal("fixture.one.value", binding.Tools.Single().Name);
        using var wire = new Wire([ToolSse(Complete), TextSse("first done")]);
        var storage = new AuditedStorageFactory();
        var session = await Create(files, "durable", binding, wire, storage);
        var entered = Gate(); var release = Gate(); ToolExecutionEnded? execution = null;
        ImmutableArray<TranscriptEntry> retained = []; string[] records = []; long acknowledged = 0;
        var path = session.Path;
        using var listener = session.Subscribe(new Sink(async (observation, _) =>
        {
            if (observation is ToolExecutionEnded end) execution = end;
            if (observation is ToolResultMessageEnded)
            {
                await Acknowledged(session, storage, deadline.Token);
                entered.TrySetResult(); await release.Task;
            }
        }));
        wire.BeforeSend = async (index, token) =>
        {
            await Acknowledged(session, storage, token);
            if (index == 1) Equal("toolResult", session.Snapshot.Context.LlmMessages[^1].Role);
        };
        var run = session.PromptAsync([binding.CreateDeclarationMessage("approved published fixture", 123), User("first")], deadline.Token);
        try
        {
            await Stage(entered.Task, run, "acknowledged published tool result", deadline.Token);
            Check(!run.IsCompleted && !session.WaitForIdleAsync().IsCompleted, "Next provider or idle overtook held durable message-end delivery.");
            Check(execution is not null && !execution.Outcome.IsError,
                "Published callback did not produce a successful owned outcome; policy=" + policy.Calls + "; effects=" + effects +
                "; failure=" + execution?.Outcome.Result.Failure?.Kind + "; call=" + execution?.Outcome.Invocation.Call.Name + ".");
            Equal(1, wire.Bodies.Count); Equal(1, effects); Equal(1, policy.Calls);
            Same(Complete.Value, execution!.Outcome.Result.ToJson().Value);
            var authorized = policy.Action ?? throw new InvalidOperationException("Final policy did not observe execution.");
            Check(ReferenceEquals(authorized.Arguments, execution.Outcome.Invocation.Call.Arguments), "Final policy lost the identical admitted arguments.");
            Equal(PreparedToolActionKind.Extension, authorized.Kind);
            Equal("fixture.one/" + loaded.OwnerGeneration + "/value", authorized.Target);
            Check(authorized.CommandArguments.IsEmpty && authorized.WorkingDirectory is null && authorized.Environment.IsEmpty,
                "Virtual extension invocation acquired shell or path authority.");
            Check(registry.CaptureSnapshot().Tools.Single().Parameters.Value.EnumerateObject().Count() == 0, "Fixture schema changed.");
            var tool = session.Snapshot.Context.LlmMessages[^1].WireBody.Value;
            AssertCanonicalResult(tool); release.TrySetResult();
            var result = await run.WaitAsync(deadline.Token);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(2, result.Turns.Length);
            Equal(2, wire.Bodies.Count); Equal(1, effects);
            AssertPayload(wire.Bodies[0], hasResult: false); AssertPayload(wire.Bodies[1], hasResult: true);
            retained = session.Snapshot.Context.LlmMessages;
            records = session.Snapshot.Log.Entries.Select(entry => entry.WireBody.ToString()).ToArray();
            acknowledged = session.Snapshot.Log.CommittedByteLength;
            await Acknowledged(session, storage, deadline.Token);
            Check(wire.Streams.All(body => body.AsyncDisposals == 1), "HTTP/SSE body cleanup was not awaited.");
            await wire.RequestsDisposed();
        }
        finally
        {
            release.TrySetResult(); session.Abort(); await Ignore(run); await session.DisposeAsync();
        }
        Equal(1, storage.Storage!.Disposals);
        using var resumedWire = new Wire([TextSse("resumed done")]);
        await using (var reopened = await PersistentAgentSession.OpenAsync(path, Configuration(binding, resumedWire), () => 123, files.NextId,
            cancellationToken: deadline.Token))
        {
            Equal(acknowledged, reopened.Snapshot.Log.CommittedByteLength);
            Equal(0, resumedWire.Bodies.Count); Equal(1, effects);
            Equal(records.Length, reopened.Snapshot.Log.Entries.Length);
            for (var index = 0; index < records.Length; index++) Equal(records[index], reopened.Snapshot.Log.Entries[index].WireBody.ToString());
            Prefix(retained, reopened.Snapshot.Agent.Messages);
            AssertCanonicalResult(reopened.Snapshot.Agent.Messages.Single(message => message.Role == "toolResult").WireBody.Value);
            Same(Complete.Value, PiWireJson.ReadMessage(reopened.Snapshot.Agent.Messages.Single(message => message.Role == "assistant" &&
                PiWireJson.ReadMessage(message.WireBody.Value).Content.OfType<ToolCallContent>().Any()).WireBody.Value).Content.OfType<ToolCallContent>().Single().Arguments.Value);
            await reopened.PromptAsync(User("resume"), deadline.Token);
            Prefix(retained, resumedWire.ChatRequests.Single().Messages);
            AssertPayload(resumedWire.Bodies.Single(), hasResult: true); Equal(1, effects);
            Equal("resumed done", reopened.Snapshot.Agent.Messages[^1].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        }
        await using (var durable = await SessionLogStore.OpenAsync(path, cancellationToken: deadline.Token))
            Equal(new FileInfo(path).Length, durable.Snapshot.CommittedByteLength);
        await loaded.DisposeAsync();
        Equal(1, files.MarkerCount("constructor")); Equal(1, files.MarkerCount("initialize")); Equal(1, files.MarkerCount("dispose"));
        Equal(0, registry.CaptureSnapshot().Tools.Length); Check(loaded.UnloadRequested, "Published package did not request cooperative unload.");
    }

    private static async Task FinalAdmission()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var files = await PublishedFiles.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(files.LoaderOptions);
        await files.LoadAsync(loader, registry);
        var effects = 0; AppContext.SetData("PiSharp.PublishedFixture.HostAction", (Action)(() => effects++));
        Throws<ArgumentNullException>(() => new ExtensionAgentBinding(registry, new Policy(), null!));
        foreach (var invalidSchemaAdmission in new[] { false, true })
        {
            using var policy = new Policy { Allow = false };
            var binding = new ExtensionAgentBinding(registry, policy,
                invalidSchemaAdmission ? (_, _, _) => ValueTask.FromResult(false) : ValidateEmptySchema);
            using var wire = new Wire([ToolSse(Complete), TextSse("denied done")]);
            var storage = new AuditedStorageFactory();
            await using var session = await Create(files, invalidSchemaAdmission ? "schema" : "policy", binding, wire, storage);
            ToolExecutionEnded? ended = null;
            using var subscription = session.Subscribe(new Sink((observation, _) =>
            { if (observation is ToolExecutionEnded end) ended = end; return ValueTask.CompletedTask; }));
            var result = await session.PromptAsync([binding.CreateDeclarationMessage("approved declaration", 123), User("deny")], deadline.Token);
            Equal(AgentLoopStopReason.Completed, result.Reason); Equal(2, wire.Bodies.Count); Equal(0, effects);
            Equal(invalidSchemaAdmission ? ToolFailureKind.InvalidArguments : ToolFailureKind.Blocked, ended!.Outcome.Result.Failure!.Kind);
            Equal(invalidSchemaAdmission ? 0 : 1, policy.Calls);
            Check(ended.Outcome.IsError && session.Snapshot.Context.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value.GetProperty("isError").GetBoolean(),
                "Admission denial became a successful canonical result.");
            await Acknowledged(session, storage, deadline.Token);
        }
        Equal(0, effects); Equal(1, files.MarkerCount("constructor"));
    }

    private static async Task CancellationAndClose()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var files = await PublishedFiles.CreateAsync();
        await using var registry = new ExtensionRegistry();
        var loader = new PluginAssemblyLoader(files.LoaderOptions);
        var loaded = await files.LoadAsync(loader, registry);
        var callbackEntered = Gate(); var callbackRelease = Gate(); var workCanceled = Gate();
        var shutdownEntered = files.AddGate("fixture.one.dispose-entered"); var shutdownRelease = files.AddGate("fixture.one.dispose-release");
        using var policy = new Policy(); var callbackCleanup = 0; var effects = 0; var canceledAtCleanup = false;
        AppContext.SetData("PiSharp.PublishedFixture.HostAction", (Action)(() =>
        {
            callbackEntered.TrySetResult();
            try { callbackRelease.Task.GetAwaiter().GetResult(); policy.ExecutionToken.ThrowIfCancellationRequested(); effects++; }
            finally { canceledAtCleanup = policy.ExecutionToken.IsCancellationRequested; Interlocked.Increment(ref callbackCleanup); }
        }));
        policy.ObserveCancellation = workCanceled;
        var binding = new ExtensionAgentBinding(registry, policy, ValidateEmptySchema);
        using var wire = new Wire([ToolSse(Complete), TextSse("must not run")]);
        var storage = new AuditedStorageFactory(); var session = await Create(files, "cancel", binding, wire, storage);
        var toolEnds = 0; var toolMessageEnds = 0; ToolFailureKind? failure = null;
        using var subscription = session.Subscribe(new Sink((observation, _) =>
        {
            if (observation is ToolExecutionEnded end) { failure = end.Outcome.Result.Failure?.Kind; Interlocked.Increment(ref toolEnds); }
            if (observation is ToolResultMessageEnded) Interlocked.Increment(ref toolMessageEnds);
            return ValueTask.CompletedTask;
        }));
        // The approved published callback is synchronous; a worker enters its real HostAction probe.
        var run = Task.Run(() => session.PromptAsync([binding.CreateDeclarationMessage("owned fixture", 123), User("cancel")], deadline.Token));
        Task? sessionClose = null; Task? loaderClose = null;
        try
        {
            await Stage(callbackEntered.Task, run, "actual published callback", deadline.Token,
                () => "; policy=" + policy.Calls + "; toolEnds=" + toolEnds + "; failure=" + failure + "; requests=" + wire.Bodies.Count);
            Equal(1, wire.Bodies.Count); Equal(1, policy.Calls); Equal(0, callbackCleanup);
            await Acknowledged(session, storage, deadline.Token);
            sessionClose = session.DisposeAsync().AsTask(); var sameSessionClose = session.DisposeAsync().AsTask();
            Check(ReferenceEquals(sessionClose, sameSessionClose), "Concurrent durable-session disposal did not share settlement.");
            await Stage(workCanceled.Task, run, "actual admitted execution cancellation", deadline.Token);
            // Package revocation is synchronous admission; loader shutdown joins that same owned package close.
            var packageClose = loaded.DisposeAsync().AsTask();
            loaderClose = loader.DisposeAsync().AsTask(); var sameLoaderClose = loader.DisposeAsync().AsTask();
            Check(ReferenceEquals(loaderClose, sameLoaderClose), "Concurrent loader disposal did not share settlement.");
            Equal(0, registry.CaptureSnapshot().Tools.Length);
            Check(!run.IsCompleted && !sessionClose.IsCompleted && !loaderClose.IsCompleted && !shutdownEntered.Task.IsCompleted,
                "Close bypassed the admitted published callback or started shutdown early.");
            Equal(0, toolEnds); Equal(0, toolMessageEnds); Equal(0, storage.Storage!.Disposals);
            Check(Directory.Exists(loaded.SnapshotDirectory), "Owned artifact snapshot vanished while executing.");
            callbackRelease.TrySetResult();
            await run.WaitAsync(deadline.Token); await sessionClose.WaitAsync(deadline.Token);
            await Stage(shutdownEntered.Task, loaderClose, "published shutdown after callback join", deadline.Token);
            Check(canceledAtCleanup && callbackCleanup == 1 && effects == 0, "Actual callback cancellation/cleanup/effect witnesses changed.");
            Equal(ToolFailureKind.Canceled, failure); Equal(1, toolEnds); Equal(1, toolMessageEnds); Equal(1, wire.Bodies.Count);
            Equal(1, storage.Storage!.Disposals); Check(!loaderClose.IsCompleted, "Loader close skipped held actual published shutdown.");
            shutdownRelease.TrySetResult(); await Task.WhenAll(packageClose, loaderClose, sameLoaderClose, sameSessionClose).WaitAsync(deadline.Token);
            Check(loaded.UnloadRequested && !Directory.Exists(loaded.SnapshotDirectory), "Loader failed to release captured artifacts after actual shutdown.");
            var read = await ReadAcknowledged(session.Path, deadline.Token);
            Check(read.ValidatedPrefix.Any(record => record.Entry.Type == "message" && record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult"),
                "Joined cancellation lost its durable tool-result checkpoint.");
            await using var reopened = await SessionLogStore.OpenAsync(session.Path, cancellationToken: deadline.Token);
            Equal(new FileInfo(session.Path).Length, reopened.Snapshot.CommittedByteLength);
        }
        finally
        {
            callbackRelease.TrySetResult(); shutdownRelease.TrySetResult(); session.Abort();
            await Ignore(run); await session.DisposeAsync(); await loader.DisposeAsync();
        }
    }

    // {} is the complete unconstrained JSON Schema. The prepared-tool contract separately requires an object.
    private static ValueTask<bool> ValidateEmptySchema(ExtensionToolRegistrationInfo tool, JsonData input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(tool.Parameters.Value.ValueKind == JsonValueKind.Object && !tool.Parameters.Value.EnumerateObject().Any() &&
            input.Value.ValueKind == JsonValueKind.Object);
    }
    private static AgentConfiguration Configuration(ExtensionAgentBinding binding, Wire wire) => new(Model, wire.Transport, binding.Tools);
    private static Task<PersistentAgentSession> Create(PublishedFiles files, string name, ExtensionAgentBinding binding, Wire wire, AuditedStorageFactory storage) =>
        PersistentAgentSession.CreateAsync(Path.Combine(files.Root, name + ".jsonl"), new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "published-header", timestamp = "2026-10-01T00:00:00.000Z", cwd = files.Root })),
            Configuration(binding, wire), () => 123, files.NextId, new(SessionLogStoreOptions: new(StorageFactory: storage)));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 123 })));
    private static void AssertCanonicalResult(JsonElement tool)
    {
        Equal("toolResult", tool.GetProperty("role").GetString()); Equal("fixture.one.value", tool.GetProperty("toolName").GetString());
        Same(Complete.Value.GetProperty("content"), tool.GetProperty("content")); Same(Complete.Value.GetProperty("details"), tool.GetProperty("details"));
        Same(Complete.Value.GetProperty("usage"), tool.GetProperty("usage")); Check(!tool.GetProperty("isError").GetBoolean(), "Successful outcome became an error.");
        Check(!tool.TryGetProperty("opaqueResult", out _) && !tool.TryGetProperty("structuredContent", out _) && !tool.TryGetProperty("echo", out _),
            "Canonical tool message invented complete-result properties outside the source message shape.");
    }
    private static void AssertPayload(JsonData payload, bool hasResult)
    {
        Equal(Model.Id, payload.Value.GetProperty("model").GetString()); Check(payload.Value.GetProperty("stream").GetBoolean(), "Request disabled SSE.");
        var declaration = payload.Value.GetProperty("tools").EnumerateArray().Single();
        Equal("function", declaration.GetProperty("type").GetString()); Equal("fixture.one.value", declaration.GetProperty("name").GetString());
        Check(!declaration.GetProperty("parameters").EnumerateObject().Any(), "Empty schema changed on the provider boundary.");
        var input = payload.Value.GetProperty("input").EnumerateArray().ToArray();
        Equal(hasResult ? 1 : 0, input.Count(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"));
        if (!hasResult) return;
        var call = input.Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
        var result = input.Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
        Same(Complete.Value, JsonData.Parse(call.GetProperty("arguments").GetString()!).Value);
        Equal(call.GetProperty("call_id").GetString(), result.GetProperty("call_id").GetString());
        Equal("published answer\0\u03c0", result.GetProperty("output").GetString());
        Check(result.EnumerateObject().Count() == 3, "Programmatic result metadata leaked into provider function output.");
    }
    private static async Task Acknowledged(PersistentAgentSession session, AuditedStorageFactory storage, CancellationToken token)
    {
        var snapshot = session.Snapshot; var read = await ReadAcknowledged(session.Path, token);
        Equal(snapshot.Log.CommittedByteLength, new FileInfo(session.Path).Length);
        Equal(snapshot.Log.Entries.Length + 1, read.ValidatedPrefix.Length);
        Equal(snapshot.Log.CommittedByteLength, (long)read.ValidatedPrefixByteLength);
        var audit = storage.Storage ?? throw new InvalidOperationException("Actual storage was not acquired.");
        Check(audit.DurableFlushes == snapshot.Log.Sequence + 1 && audit.Checkpoints == audit.DurableFlushes,
            "Acknowledged snapshot overtook actual flush-to-disk/checkpoint completion.");
        for (var index = 0; index < snapshot.Log.Entries.Length; index++)
            Equal(snapshot.Log.Entries[index].WireBody.ToString(), read.ValidatedPrefix[index + 1].Entry.WireBody.ToString());
    }
    private static async Task<SessionLogReadResult> ReadAcknowledged(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        var read = await new SessionLogReader().ReadAsync(input, leaveOpen: true, cancellationToken: token);
        Check(read.SourceComplete && read.Status == SessionLogReadStatus.Complete, "Durable live-reader checkpoint is incomplete."); return read;
    }
    private static string ToolSse(JsonData arguments) => Sse(
        JsonSerializer.Serialize(new { type = "response.output_item.added", output_index = 4, item = new { type = "function_call", id = "fc_published", call_id = "call-published", name = "fixture.one.value", arguments = "" } }),
        JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 4, item = new { type = "function_call", id = "fc_published", call_id = "call-published", name = "fixture.one.value", arguments = arguments.ToString() } }),
        """{"type":"response.completed","response":{"id":"published-tool","status":"completed","output":[]}}""");
    private static string TextSse(string text) => Sse(
        """{"type":"response.output_item.added","output_index":4,"item":{"type":"message","id":"msg_published","content":[]}}""",
        JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 4, item = new { type = "message", id = "msg_published", content = new[] { new { type = "output_text", text } } } }),
        """{"type":"response.completed","response":{"id":"published-text","status":"completed","output":[]}}""");
    private static string Sse(params string[] values) => string.Concat(values.Select(value => "data: " + value + "\n\n"));

    private sealed class Wire : IDisposable
    {
        private readonly Handler handler; private readonly HttpClient client;
        internal readonly List<JsonData> Bodies = []; internal readonly List<ChatRequest> ChatRequests = [];
        internal readonly List<HttpRequestMessage> Requests = []; internal readonly List<Body> Streams = [];
        internal Func<int, CancellationToken, Task>? BeforeSend;
        internal IChatTransport Transport { get; }
        internal Wire(string[] replies)
        {
            handler = new Handler(async (request, token) =>
            {
                var index = Bodies.Count; Check(index < replies.Length, "Unexpected provider execution.");
                Equal("https://published-fixture.invalid/v1/responses", request.RequestUri!.AbsoluteUri);
                Equal("Bearer authored-inert-key", request.Headers.Authorization!.ToString());
                if (BeforeSend is { } before) await before(index, token);
                var bytes = await request.Content!.ReadAsByteArrayAsync(token); Check(bytes.Length <= 65_536, "Authored request exceeded its bound.");
                Bodies.Add(JsonData.Parse(Encoding.UTF8.GetString(bytes))); Requests.Add(request);
                var body = new Body(Encoding.UTF8.GetBytes(replies[index])); Streams.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            });
            client = new HttpClient(handler);
            var factory = new ResponsesKeyAuthRequestFactory(new("https://published-fixture.invalid/v1/responses"), Model, new(false), new(MaximumPayloadBytes: 65_536));
            Transport = new ResponsesHttpSseTransport(client, request => { ChatRequests.Add(request); return factory.Create(request, "authored-inert-key"); });
        }
        internal async Task RequestsDisposed()
        {
            foreach (var request in Requests)
            {
                try { await request.Content!.ReadAsByteArrayAsync(); }
                catch (ObjectDisposedException) { continue; }
                throw new InvalidOperationException("Owned HTTP request content survived final cleanup.");
            }
        }
        public void Dispose() => client.Dispose();
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
        internal sealed class Body(byte[] bytes) : MemoryStream(bytes, writable: false)
        {
            internal int AsyncDisposals;
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
            public override ValueTask DisposeAsync() { Interlocked.Increment(ref AsyncDisposals); return base.DisposeAsync(); }
        }
    }
    private sealed class AuditedStorageFactory : ISessionLogStorageFactory
    {
        internal AuditedStorage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class AuditedStorage(ISessionLogStorage inner) : ISessionLogStorage
    {
        internal int DurableFlushes, Checkpoints, Disposals;
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() { inner.FlushToDisk(); Interlocked.Increment(ref DurableFlushes); }
        public async ValueTask BeforeCheckpointAsync() { await inner.BeforeCheckpointAsync(); Interlocked.Increment(ref Checkpoints); }
        public async ValueTask DisposeAsync() { await inner.DisposeAsync(); Interlocked.Increment(ref Disposals); }
    }
    private sealed class Policy : IToolActionPolicy, IDisposable
    {
        internal bool Allow = true; internal int Calls; internal PreparedToolAction? Action; internal CancellationToken ExecutionToken;
        internal TaskCompletionSource? ObserveCancellation; private CancellationTokenRegistration registration;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Action = action; ExecutionToken = token;
            Check(ReferenceEquals(invocation.Call.Arguments, action.Arguments), "Final policy lost original owned argument identity.");
            if (ObserveCancellation is { } observer) registration = token.Register(() => observer.TrySetResult());
            return ValueTask.FromResult(new ToolActionAuthorization(Allow));
        }
        public void Dispose() => registration.Dispose();
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) => emit(observation, cancellationToken); }

    private sealed class PublishedFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-Published-Persistent-" + Guid.NewGuid().ToString("N"));
        private readonly string? previousMarkers = Environment.GetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS");
        private readonly object? previousGates = AppContext.GetData("PiSharp.PublishedFixture.Gates"), previousAction = AppContext.GetData("PiSharp.PublishedFixture.HostAction");
        private readonly Dictionary<string, TaskCompletionSource> gates = new(StringComparer.Ordinal); private int id;
        private JsonData metadata = JsonData.EmptyObject;
        private string PackageRoot => Path.Combine(Root, "one"); private string Markers => Path.Combine(Root, "markers");
        internal PluginAssemblyLoaderOptions LoaderOptions => new() { SnapshotParentDirectory = Path.Combine(Root, "snapshots") };
        internal string NextId() => "published-entry-" + Interlocked.Increment(ref id);
        internal TaskCompletionSource AddGate(string name) { var gate = Gate(); gates.Add(name, gate); return gate; }
        internal int MarkerCount(string stage) { var path = Path.Combine(Markers, "PublishedFixture.One.markers"); return File.Exists(path) ? File.ReadAllLines(path).Count(line => line == stage) : 0; }
        internal async Task<LoadedExtension> LoadAsync(PluginAssemblyLoader loader, ExtensionRegistry registry)
        {
            var read = new ExtensionManifestReader().Read(metadata);
            Check(read.IsValid, "Authored fixture metadata was not valid.");
            var inspection = new ExtensionTrustDecision(ExtensionTrustDisposition.ApproveMetadataInspection, PackageRoot, read.ManifestValueSha256!,
                ExtensionSourceScope.Explicit, "approved-persistent-fixture", "experimental-policy-0");
            var execution = new PluginExecutionDecision(PluginExecutionDisposition.ApprovePublishedFixtureExecution, PackageRoot, read.ManifestValueSha256!, read.Manifest!.ArtifactHashes,
                ExtensionSourceScope.Explicit, "approved-persistent-fixture", "experimental-policy-0", 1);
            return await loader.LoadAsync(metadata, PackageRoot, ExtensionSourceScope.Explicit, "approved-persistent-fixture", inspection, execution, registry);
        }
        internal static async Task<PublishedFiles> CreateAsync()
        {
            if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                throw new PlatformNotSupportedException("Published fixture qualification requires Windows win-x64; other runtime qualification remains pending.");
            var files = new PublishedFiles();
            try
            {
                Directory.CreateDirectory(files.PackageRoot); Directory.CreateDirectory(files.Markers); Directory.CreateDirectory(files.LoaderOptions.SnapshotParentDirectory);
                Environment.SetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS", files.Markers); AppContext.SetData("PiSharp.PublishedFixture.Gates", files.gates);
                var source = Environment.GetEnvironmentVariable("PISHARP_PUBLISHED_EXTENSION_FIXTURES");
                if (source is null)
                {
                    var directory = new DirectoryInfo(AppContext.BaseDirectory);
                    while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent ?? throw new InvalidOperationException("Cannot locate approved published fixture outputs.");
                    source = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures");
                }
                var sourcePackage = Path.Combine(source, "one");
                Check(File.Exists(Path.Combine(sourcePackage, "PublishedFixture.One.dll")), "Root must publish the approved fixture.one project before this gate.");
                var paths = Directory.GetFiles(sourcePackage, "*", SearchOption.AllDirectories);
                Check(paths.Length <= 128, "Approved fixture file-count bound changed."); var hashes = new Dictionary<string, string>(StringComparer.Ordinal); long total = 0;
                foreach (var path in paths.Order(StringComparer.Ordinal))
                {
                    Check((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Fixture copy requires regular owned files.");
                    var relative = Path.GetRelativePath(sourcePackage, path); var size = new FileInfo(path).Length;
                    Check(size <= 16 * 1024 * 1024 - total, "Approved fixture byte bound changed."); total += size;
                    var destination = Path.Combine(files.PackageRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
                    hashes.Add(relative.Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(destination))).ToLowerInvariant());
                }
                Check(!hashes.ContainsKey("PiSharp.Extensions.Abstractions.dll") && !hashes.ContainsKey("PiSharp.Contracts.dll"), "Published fixture copied a private host ABI.");
                files.metadata = JsonData.Parse(JsonSerializer.Serialize(new
                {
                    schemaVersion = 0, id = "fixture.one", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" },
                    runtimeKind = "native", assembly = "PublishedFixture.One.dll", entryType = "PublishedFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "tools", "observations" },
                    resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.One.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes
                }));
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var gate in gates.Values) gate.TrySetResult();
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_FIXTURE_MARKERS", previousMarkers);
            AppContext.SetData("PiSharp.PublishedFixture.Gates", previousGates); AppContext.SetData("PiSharp.PublishedFixture.HostAction", previousAction);
            var full = Path.GetFullPath(Root); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporary, StringComparison.Ordinal) || !Path.GetFileName(full).StartsWith("PiSharp-Published-Persistent-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its owned temporary directory.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Stage(Task witness, Task run, string name, CancellationToken token, Func<string>? diagnostic = null)
    { await Task.WhenAny(witness, run).WaitAsync(token); if (!witness.IsCompleted) { await run; throw new InvalidOperationException("Actual operation settled before " + name + diagnostic?.Invoke()); } await witness; }
    private static async Task Ignore(Task operation) { try { await operation; } catch { } }
    private static void Prefix(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual)
    { Check(actual.Length >= expected.Length, "Restored history lost entries."); for (var index = 0; index < expected.Length; index++) { Equal(expected[index].Role, actual[index].Role); Equal(expected[index].WireBody.ToString(), actual[index].WireBody.ToString()); } }
    private static void Same(JsonElement expected, JsonElement actual)
    {
        Equal(expected.ValueKind, actual.ValueKind);
        if (expected.ValueKind == JsonValueKind.Object)
        { Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count()); foreach (var property in expected.EnumerateObject()) { Check(actual.TryGetProperty(property.Name, out var field), "Missing owned field: " + property.Name); Same(property.Value, field); } }
        else if (expected.ValueKind == JsonValueKind.Array)
        { Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (var index = 0; index < expected.GetArrayLength(); index++) Same(expected[index], actual[index]); }
        else if (expected.ValueKind == JsonValueKind.String) Equal(expected.GetString(), actual.GetString());
        else Equal(expected.GetRawText(), actual.GetRawText());
    }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
