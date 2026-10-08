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

internal static class SessionInfoChangedTests
{
    internal const string Prefix = "native session-info-changed ";
    private static readonly ModelDescriptor Model = new("metadata-event", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "actual RPC durable event then extension then response with repeated and undefined names", Payloads),
        (Prefix + "invalid stale precanceled and overbudget naming never publishes", Exclusions),
        (Prefix + "held original callback joins caller cancellation and owner registry disposal", Held),
        (Prefix + "snapshot failures continue while throwing reporter skips later handlers without undoing commit", Failures),
        (Prefix + "replacement installs fresh generation and rejects stale publisher", Replacement),
        (Prefix + "session observer error retains acknowledged name original cause and healthy writer", ObserverFailure)
    ];

    private static async Task Payloads()
    {
        await using var f = await Fixture.Create(); var seen = new List<JsonData>(); var callbackErrors = new List<Exception>();
        using var output = new Capture(); await using var rpc = Dispatcher(f, output);
        await f.Register((value, context, token) =>
        {
            try
            {
                var snapshot = ((IExtensionSessionContext)context).SessionSnapshot!;
                Check(!token.CanBeCanceled && snapshot.Generation == f.Owner.Current.Generation && snapshot.SessionId == "source" &&
                    snapshot.SelectedLeafId == f.Session.Snapshot.Context.LeafId && snapshot.BranchEntries[^1].Value.GetProperty("type").GetString() == "session_info" &&
                    snapshot.Persistence == ExtensionSessionPersistence.DurableLocalFile && f.Session.Snapshot.IsConfiguring,
                    "Notification lacks actual acknowledged context or original reservation.");
                var beforeResponse = Frames(output);
                Check(beforeResponse[^1].Value.GetProperty("type").GetString() == "session_info_changed" &&
                    JsonElement.DeepEquals(beforeResponse[^1].Value, value.Value), "Session observer did not precede extension delivery with exact source event.");
                Check(ReadAcknowledgedFile(f.Source).Contains(snapshot.SelectedLeafId!, StringComparison.Ordinal), "Event preceded actual file acknowledgment.");
                seen.Add(value); return ValueTask.CompletedTask;
            }
            catch (Exception error)
            {
                callbackErrors.Add(error);
                throw;
            }
        }); f.Install();
        for (var i = 0; i < 2; i++)
        {
            await rpc.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(new { id = "name-" + i, type = "set_session_name", name = "\uFEFF  First\r\nSecond  \uFEFF" })));
            var last = Frames(output)[^1].Value;
            Check(last.GetProperty("type").GetString() == "response" && last.GetProperty("success").GetBoolean(), "Naming response changed.");
        }
        Check(seen.Count == 2 && seen.All(v => v.Value.EnumerateObject().Select(p => p.Name).SequenceEqual(["type", "name"]) &&
            v.Value.GetProperty("name").GetString() == "First Second"), "Normalized payload or repeated-name notification differs. Seen: " + JsonSerializer.Serialize(seen.Select(value => value.Value)) +
            "; Diagnostics: " + JsonSerializer.Serialize(f.Diagnostics) + "; Callback errors: " + string.Join(" | ", callbackErrors.Select(error => error.ToString())));
        // The existing native writer accepts nonempty whitespace; source getSessionName returns undefined.
        await f.Owner.SetSessionNameAsync(f.Owner.Current, " \uFEFF ");
        Check(seen.Count == 3 && seen[^1].Value.EnumerateObject().Select(p => p.Name).SequenceEqual(["type"]) &&
            Frames(output)[^1].Value.EnumerateObject().Select(p => p.Name).SequenceEqual(["type"]) &&
            f.Session.Snapshot.Log.Entries.Count(e => e.Type == "session_info") == 3 && f.Script.Calls == 0 && f.Diagnostics.Count == 0,
            "Undefined name was serialized as null, repeated append suppressed or transport used.");
    }

    private static async Task Exclusions()
    {
        await using var f = await Fixture.Create(); var calls = 0; var original = f.Session.Snapshot.Log.Entries.Length;
        await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
        using var output = new Capture(); await using var rpc = Dispatcher(f, output, new(MaximumOutputBytes: 256));
        foreach (var name in new[] { "", " \r\n ", new string('x', 300) })
        {
            await rpc.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(new { id = "reject-" + name.Length, type = "set_session_name", name })));
            Check(!Frames(output)[^1].Value.GetProperty("success").GetBoolean(), "Invalid/overbudget naming accepted.");
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => f.Owner.SetSessionNameAsync(f.Owner.Current, "canceled", canceled.Token));
        await Throws<PersistentAgentSessionException>(() => f.Session.SetSessionNameAsync("wrong-id", "wrong"));
        await Throws<ExtensionRegistrationException>(() => f.Registry.DispatchSessionInfoChangedAsync(f.Registry.CaptureSnapshot(),
            JsonData.Parse("{\"type\":\"session_info_changed\",\"name\":null}")).AsTask());
        Check(calls == 0 && Frames(output).All(v => v.Value.GetProperty("type").GetString() != "session_info_changed") &&
            f.Session.Snapshot.Log.Entries.Length == original && f.Session.Snapshot.Fault is null, "Excluded naming emitted or changed checkpoint.");
    }

    private static async Task Held()
    {
        await using var f = await Fixture.Create(); using var caller = new CancellationTokenSource();
        var entered = Gate(); var release = Gate(); var joined = false; var old = f.Owner.Current;
        await f.Register(async (_, _, token) =>
        { Check(!token.CanBeCanceled && f.Session.Snapshot.Log.Entries[^1].Type == "session_info", "Held notification preceded commit or borrowed cancellation."); entered.TrySetResult(); await release.Task; joined = true; });
        f.Install(); Task<SessionEntry>? naming = null; Task? close = null; Task? registryClose = null;
        try
        {
            naming = f.Owner.SetSessionNameAsync(old, "held", caller.Token);
            Check(await Task.WhenAny(entered.Task, naming) == entered.Task && !naming.IsCompleted, "Notification detached original task.");
            caller.Cancel(); close = f.Owner.DisposeAsync().AsTask(); registryClose = f.Registry.DisposeAsync().AsTask();
            Check(!naming.IsCompleted && !close.IsCompleted && !registryClose.IsCompleted && !old.Session.Snapshot.IsDisposed,
                "Original resources closed before held callback joined.");
            release.TrySetResult(); var entry = await naming; await close; await registryClose;
            Check(joined && entry.WireBody.Value.GetProperty("name").GetString() == "held" && old.Session.Snapshot.IsDisposed,
                "Late cancellation lost acknowledged receipt or cleanup.");
        }
        finally
        {
            release.TrySetResult();
            try { if (naming is not null) await naming; }
            finally { try { if (close is not null) await close; } finally { if (registryClose is not null) await registryClose; } }
        }
    }

    private static async Task Failures()
    {
        await using var f = await Fixture.Create(); var order = new List<int>(); IExtensionRegistration? second = null;
        await f.Registry.ActivateAsync("errors", new Plugin(api =>
        {
            api.Observe(new("first", "session_info_changed", (_, _, _) => { order.Add(1); second!.Dispose(); throw new IOException("handler failure"); }));
            second = api.Observe(new("second", "session_info_changed", (_, _, _) => { order.Add(2); using var foreign = new CancellationTokenSource(); foreign.Cancel(); throw new OperationCanceledException(foreign.Token); }));
            api.Observe(new("third", "session_info_changed", (_, _, _) => { order.Add(3); return ValueTask.CompletedTask; }));
        })); f.Install(); await f.Owner.SetSessionNameAsync(f.Owner.Current, "committed despite plugins");
        Check(order.SequenceEqual([1, 2, 3]) && f.Diagnostics.Count == 2 && f.Session.Snapshot.Fault is null,
            "Captured registration order or handler failure continuation changed.");
        // Fresh captured snapshot excludes the removed registration. The trusted reporter throws;
        // source emission stops later handlers, while host admission preserves naming success.
        order.Clear(); f.Report = (_, _) => throw new IOException("trusted reporter failure"); f.Install();
        await f.Owner.SetSessionNameAsync(f.Owner.Current, "committed despite reporter");
        Check(order.SequenceEqual([1]) && f.Session.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("name").GetString() == "committed despite reporter" &&
            f.Session.Snapshot.Fault is null, "Reporter failure continued callbacks or undid/poisoned commit.");
        await f.Registry.DisposeAsync(); await f.Owner.SetSessionNameAsync(f.Owner.Current, "committed despite admission refusal");
        Check(f.Session.Snapshot.Fault is null, "Postcommit registry refusal poisoned writer.");
    }

    private static async Task Replacement()
    {
        await using var f = await Fixture.Create(); var generations = new List<long>(); var names = new List<string?>();
        await f.Register((value, context, _) => { generations.Add(((IExtensionSessionContext)context).SessionSnapshot!.Generation); names.Add(value.Value.GetProperty("name").GetString()); return ValueTask.CompletedTask; });
        f.Install(); var old = f.Owner.Current; await f.Owner.SetSessionNameAsync(old, "source name");
        await f.Replace("switch"); await f.Owner.SetSessionNameAsync(f.Owner.Current, "target name");
        await Throws<InvalidOperationException>(() => f.Owner.SetSessionNameAsync(old, "stale"));
        Check(generations.SequenceEqual([1L, 2L]) && names.SequenceEqual(["source name", "target name"]) &&
            f.Session.Snapshot.Log.Header.Id == "other" && f.Diagnostics.Count == 0, "Notification retargeted stale source or missed fresh binding.");
    }

    private static async Task ObserverFailure()
    {
        await using var f = await Fixture.Create(); var calls = 0; var cause = new IOException("original observer failure");
        await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
        using var subscription = f.Session.SubscribeOperationEvents(new Sink(value =>
        { if (value is SessionInfoChanged) throw cause; return ValueTask.CompletedTask; }));
        var actual = await Throws<IOException>(() => f.Owner.SetSessionNameAsync(f.Owner.Current, "observer already committed"));
        Check(ReferenceEquals(actual, cause) && calls == 0 && f.Session.Snapshot.Fault is null &&
            f.Session.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("name").GetString() == "observer already committed",
            "Observer failure lost cause, invoked extensions after failed session emission or poisoned acknowledged writer.");
        subscription.Dispose(); await f.Owner.SetSessionNameAsync(f.Owner.Current, "healthy after observer failure");
        Check(calls == 1 && f.Session.Snapshot.Fault is null, "Original naming reservation leaked after observer failure.");
    }

    private sealed class Sink(Func<SessionOperationEvent, ValueTask> emit) : ISessionOperationEventSink
    { public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken token) => emit(observation); }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new(); internal byte[] Bytes() { lock (gate) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    private static string ReadAcknowledgedFile(string path)
    {
        // The live store owns a read/write handle. This reader must share that existing write access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static JsonData[] Frames(Capture output) => Encoding.UTF8.GetString(output.Bytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonData.Parse(line)).ToArray();
    private static RpcSessionDispatcher Dispatcher(Fixture f, Capture output, RpcDispatchOptions? options = null) => new(f.Session,
        new JsonlWriter(output, ownership: JsonlStreamOwnership.Borrowed), f.Clock,
        [new(Model, JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, api = Model.Api, provider = Model.Provider, name = Model.Id,
            baseUrl = "https://offline.invalid", reasoning = false, input = new[] { "text" }, contextWindow = 128000, maxTokens = 1024,
            cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } })))], options, RpcSessionOwnership.Borrowed, sessionOwner: f.Owner);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-session-info-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal PersistentAgentSession Session => Owner.Current.Session;
        internal ExtensionRegistry Registry = null!; internal Script Script = new();
        internal List<ExtensionEventDiagnostic> Diagnostics = []; internal Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? Report;
        internal NativeSessionInfoChangedBinding? Metadata;
        private SessionRuntimeRegistry runtime = null!; private int id, sessionId; private long ticks = 1711929600000;
        internal long Clock() => Interlocked.Increment(ref ticks);
        internal static async Task<Fixture> Create()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.Script.Clock = f.Clock;
            f.runtime = new([new(Model, f.Script)], [], new NoPolicy());
            try
            {
                foreach (var path in new[] { f.Source, f.Other })
                {
                    var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2024-04-01T00:00:00.000Z", cwd = f.Root }));
                    await using var store = await SessionLogStore.CreateNewAsync(path, header);
                    await store.AppendAsync([Entry("u0", null, User(new string('x', 8000))), Entry("a0", "u0", Assistant()),
                        Entry("u1", "a0", User(new string('y', 8000))), Entry("a1", "u1", Assistant())]);
                }
                var lifecycle = new PersistentSessionLifecycle(f.runtime, f.Clock, () => "entry-" + Interlocked.Increment(ref f.id),
                    nextSessionId: () => "created-" + Interlocked.Increment(ref f.sessionId), catalog: new SessionCatalog([new("fixtures", f.Root)]));
                var initial = await lifecycle.OpenAsync(new(f.Source), Model); f.Owner = lifecycle.Attach(initial);
                var views = new NativeSessionSnapshotProvider(); views.Attach(f.Owner); f.Registry = new(null, null, views);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task Register(ExtensionObservationCallback callback) => Registry.ActivateAsync("observations", new Plugin(api =>
        { api.Observe(new("observe-info", "session_info_changed", callback)); }));
        internal void Install()
        {
            var snapshot = Registry.CaptureSnapshot(); var report = Report ?? ((diagnostic, _) => { Diagnostics.Add(diagnostic); return ValueTask.CompletedTask; });
            var binding = new NativeSessionInfoChangedBinding(Registry, snapshot, report); Metadata = binding;
            binding.Attach(Owner, Owner.Current);
            Owner.AfterReplacement = replacement => { binding.Attach(Owner, replacement.Current); return ValueTask.CompletedTask; };
        }
        internal async Task<AgentSessionReplacement?> Replace(string kind)
        {
            var attached = Owner.Current;
            if (kind == "resume")
            {
                var page = await Owner.ListSessionsAsync(attached, new());
                return await Owner.ResumeAsync(attached, new(page.Items.Single(item => item.Path == Other).Key));
            }
            return kind switch
            {
                "new" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.New)),
                "before" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.ForkBefore, "u1")),
                "at" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.ForkAt, "u1")),
                "clone" => await Owner.CreateAsync(attached, new(AgentSessionCreationKind.Clone)),
                _ => await Owner.SwitchAsync(attached, new(kind == "switch-source" ? Source : Other))
            };
        }
        public async ValueTask DisposeAsync()
        {
            // File deletion follows successful ownership closure, never a failed/unsettled close.
            try { if (Registry is not null) await Registry.DisposeAsync(); }
            finally { if (Owner is not null) await Owner.DisposeAsync(); }
            Check(Path.GetDirectoryName(Path.GetFullPath(Root)) == parent && Path.GetFileName(Root).StartsWith("PiSharp-session-info-", StringComparison.Ordinal), "Invalid owned fixture root.");
            if (Directory.Exists(Root))
            {
                Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Linked fixture root rejected.");
                Directory.Delete(Root, recursive: true);
            }
        }
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Script : IChatTransport
    {
        internal Func<long> Clock = () => 0; internal int Calls, Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls++; var final = Message() with { Timestamp = Clock() };
            try
            {
                await Task.CompletedTask; yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new("")); yield return new TextEnded(0, ((TextContent)final.Content.Single()).Text); yield return new StreamDone(final.StopReason, final);
            }
            finally { Cleanups++; }
        }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Observation acquired a tool"); }
    private static SessionEntry Entry(string id, string? parent, TranscriptEntry message) => Codec.Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2024-04-01T00:00:00.000Z", message = message.WireBody.Value }));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 10 })));
    private static TranscriptEntry Assistant() => new("assistant", PiWireJson.WriteMessage(Message() with { Timestamp = 10 }));
    private static AssistantMessage Message() => new(Model.Api, Model.Provider, Model.Id, 0, [new TextContent("fake completed text")], TokenUsage.Zero, StopReason.Stop);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
