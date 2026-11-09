using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;
using PiSharp.Durable;
using PiSharp.Durable.Chord.Examples;
using PiSharp.Durable.Store;
using ChordJson = System.Text.Json.Nodes.JsonValue;
using DurableJson = PiSharp.Durable.Chord.JsonValue;
using static PiSharp.Durable.Chord.Examples.ChordExamples;

var cases = new (string Name, Func<Task> Run)[]
{
    ("real-host.example35.shared-sequence-archive-reopen-and-store-ownership", async () =>
    {
        var result = await StoreAsync(TextWriter.Null);
        Equal(2L, result.Sequences, "one Chord and one Durable commit share the sequence");
        Check(result.NextId > 0, "the shared allocator advanced");
        Check(result.LastSeq >= result.Sequences, "the shared archive carries its committed sequence");
        Check(result.Parts.ContainsKey("example.chat table") && result.Parts.ContainsKey("example.chat put"), "archive contains the owner's tables and rows");
        Check(result.Parts.ContainsKey("example.chat document.create"), "archive also contains the Durable documents");
        Check(result.ReopenedEntries.Contains("example.said"), "Durable entry persisted after host/store close");
        Equal("hello", result.ReopenedNote, "Chord table persisted beside Durable records");
    }),
    ("real-host.example36.follows-live-durable-views-and-detaches", async () =>
    {
        var result = await ViewsAsync(TextWriter.Null);
        Equal(2, result.Frames, "one public frame per write");
        Equal(2, result.Computes, "one derive compute per write");
        Equal(2, result.Detached, "host close detaches both real Durable observations");
        Equal("[\"example.first\",\"example.second\"]", result.Summary["kinds"]!.ToJsonString(), "transcript view");
    }),
    ("real-host.example37.session-failure-retry-reopens-native-and-rebinds-follower", async () =>
    {
        var result = await RegistryAsync(TextWriter.Null);
        Check(result.Greeting.Contains("hello", StringComparison.Ordinal), "contributed Durable task runs");
        Equal("failed", result.FailedState, "actual Durable session close fails the follower");
        Equal("live", result.RecoveredState, "retry recovers the follower");
        Check(result.Kinds.Contains("example.greeted"), "reopened store retains transcript");
        Check(result.Conflicts > 0, "duplicate task contribution is reported and excluded");
        Equal(2, result.Opens, "retry opens a new Harness");
        Equal(2, result.Closes, "each native closes once");
    }),
    ("real-host.store-publication.adopts-two-durable-views-in-one-batch", AtomicViews),
    ("real-host.registry-reader.reload-wakes-blocked-task-and-removal-is-visible", RegistryReload),
    ("real-host.storage-failure.closes-once-and-detaches-owned-harness", StorageFailure),
    ("real-host.follow.close-during-real-durable-attachment-detaches-late-result", CloseDuringAttach),
};
var failed = 0;
foreach (var (name, run) in cases)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(45)); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} real-host Durable integration cases passed.");
return failed == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }

static async Task AtomicViews()
{
    await using var store = await OpenStoreAsync();
    var errors = new ConcurrentQueue<HostError>();
    var app = new ViewsApplication();
    await using var host = Hosts.Create(new() { Store = store, OnError = errors.Enqueue });
    await host.LoadAsync([new("@example/core", app.Facet(store))], Context);
    using var reference = await host.GetAsync(ViewsApplication.Chat, new(), Context);
    var frames = new ConcurrentQueue<JsonNode>();
    var watch = await reference.Member("summary").WatchAsync(Context);
    watch.Start((call, _) => { if (call is WatchValueCall<JsonNode?> value) frames.Enqueue(value.Value!.DeepClone()); return Task.CompletedTask; });
    var before = app.Computes;
    var seq = store.LastSeq;
    await reference.CallAsync("writeAndTask", ChordJson.Create("example.atomic"), Context);
    await EventuallyAsync(() => frames.Count > 0, "atomic frame");
    await Task.Delay(40);
    Equal(1L, store.LastSeq - seq, "entry and task share a single commit");
    Equal(1, frames.Count, "both changed Durable views publish a single Chord frame");
    Equal(1, app.Computes - before, "one derive after both adoptions");
    var frame = frames.Single();
    Equal(1, frame["tasks"]!.GetValue<int>(), "same frame contains the created task");
    Equal("[\"example.atomic\"]", frame["kinds"]!.ToJsonString(), "same frame contains the appended entry");
    await watch.StopAsync();
    await host.CloseAsync(Context);
    Equal(2, app.Detached, "close detaches actual conversation and graph observers");
    Check(errors.IsEmpty, "no host publication or listener errors");
}

static async Task RegistryReload()
{
    await using var store = await OpenStoreAsync();
    var errors = new ConcurrentQueue<HostError>();
    var app = new RegistryApplication();
    var core = new PluginFacet("@example/core", app.CoreFacet(store));
    await using var host = Hosts.Create(new() { Store = store, OnError = errors.Enqueue });
    var valueBeforeLoad = host.RegistryAsync(RegistryApplication.Extensions, Context);
    var getBeforeLoad = host.GetAsync(RegistryApplication.Core, new(), Context);
    Check(!valueBeforeLoad.IsCompleted && !getBeforeLoad.IsCompleted, "real host waiters park before load");
    await host.LoadAsync([core], Context);
    using var reference = await getBeforeLoad;
    var value = await valueBeforeLoad;
    var (readerContext, cancelReader) = ChordContext.WithCancel(Context);
    var reader = RegistryReader.Create(value, readerContext);
    Check(reader.Snapshot().GetTask(RegistryApplication.Greet.Definition.Name) is null, "initial real registry is empty");
    var notifications = 0;
    using var subscription = reader.Subscribe(() => Interlocked.Increment(ref notifications));
    Equal("no greeter installed", ((JsonNode)(await reference.CallAsync("greet", null, Context))!).GetValue<string>(), "native reader uses empty registry");
    var harness = app.Harness!;
    var root = await harness.ConversationAsync(ConversationId.Root, DurableContext);
    var id = await root!.CommitAsync(tx => tx.CreateTaskAsync(RegistryApplication.Greet, DurableJson.Null, new TaskOptions(TaskOwnership.Conversation)), DurableContext);
    harness.Resume();
    var waiting = harness.WaitForTaskAsync(id, DurableContext);
    await Task.Delay(40);
    Check(!waiting.IsCompleted, "task is blocked without its contributed definition");
    var greeter = RegistryApplication.Greeter();
    var report = await host.ReloadAsync([core, greeter], Context);
    Equal(0, report.Failed.Count, "extension reload accepted");
    var completed = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    Check(completed.State is SettlingTaskState, "real RegistryReader notification woke Durable scheduler");
    Check(reader.Snapshot().GetTask(RegistryApplication.Greet.Definition.Name) is not null, "host reader sees contribution");
    Check(Volatile.Read(ref notifications) > 0, "subscription receives new composition");
    cancelReader();
    var notificationsAtCancel = Volatile.Read(ref notifications);
    await host.ReloadAsync([core], Context);
    Equal(notificationsAtCancel, Volatile.Read(ref notifications), "cancelling the real registry reader detaches its subscription");
    Equal("no greeter installed", ((JsonNode)(await reference.CallAsync("greet", null, Context))!).GetValue<string>(), "native reader sees removed plugin");
    Equal(1, app.Opens, "unrelated plugin reload preserves the real native Harness");
    Check(!errors.Any(e => e.Kind is "runtime_failed" or "storage_failed" or "listener_error"), "reload does not cause runtime errors");
}

static async Task StorageFailure()
{
    var backend = await StoreBackends.OpenSqliteAsync(":memory:");
    var fault = new AmbiguousCommitDatabase(backend.Db);
    await using var store = (ChordStore)await Tables.OpenStoreAsync(new Backend(fault), Context);
    var errors = new ConcurrentQueue<HostError>();
    var app = new ViewsApplication();
    await using var host = Hosts.Create(new() { Store = store, OnError = errors.Enqueue, CloseTimeout = TimeSpan.FromSeconds(2) });
    await host.LoadAsync([new("@example/core", app.Facet(store))], Context);
    using var reference = await host.GetAsync(ViewsApplication.Chat, new(), Context);
    fault.FailAfterNextCommit = true;
    try { await reference.CallAsync("write", ChordJson.Create("example.unknown-outcome"), Context); throw new InvalidOperationException("write unexpectedly succeeded"); }
    catch (Exception error) when (error.Message != "write unexpectedly succeeded") { }
    await EventuallyAsync(() => reference.State.Value.Kind == "ended" && app.Detached == 2, "store failure closes host and real followers");
    await host.CloseAsync(Context);
    Equal(1, errors.Count(e => e.Kind == "storage_failed"), "store failure delivered once");
    Check(store.Batch is null, "closed host detached from externally owned store");
    Equal(1, fault.Faults, "the actual SQLite transaction committed before its simulated acknowledgement loss");
}

static async Task CloseDuringAttach()
{
    await using var store = await OpenStoreAsync();
    var errors = new ConcurrentQueue<HostError>();
    var type = Contract.Local("example.late-attach", new()
    {
        Version = 1, Params = new(), Members = new() { ["kinds"] = Schemas.Arr(Schemas.Str()) },
    });
    var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var detached = 0;
    await using var host = Hosts.Create(new() { Store = store, OnError = errors.Enqueue, CloseTimeout = TimeSpan.FromSeconds(2) });
    var loading = host.LoadAsync([new("@example/core", Facet.Define((facet, _) => facet.Serve(type, (_, _, self) =>
    {
        var harness = OpenHarness(self, store, type.Name, Registry.Create());
        var view = self.Follow(async (observer, context) =>
        {
            var real = await ViewBridge.ConversationAsync(harness.Value, observer, context, () => Interlocked.Increment(ref detached));
            attached.TrySetResult();
            await release.Task;
            return real;
        });
        return new(new Dictionary<string, object?> { ["kinds"] = view });
    })))], Context);
    try
    {
        await attached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.CloseAsync(Context);
    }
    finally { release.TrySetResult(); }
    await loading.WaitAsync(TimeSpan.FromSeconds(10));
    await EventuallyAsync(() => Volatile.Read(ref detached) == 1, "late real observation is detached after close");
    Check(store.Batch is null, "late completion does not reclaim the host attachment");
    Check(!errors.Any(e => e.Kind is "listener_error" or "delivery_failed"), "no late publication escaped the closed host");
}

// Fault injection wraps real SQLite and changes only one post-commit acknowledgement; no host or storage operations are simulated.
sealed class AmbiguousCommitDatabase(ISqliteDatabase inner) : ISqliteDatabase
{
    public bool FailAfterNextCommit { get; set; }
    public int Faults { get; private set; }
    public Task ExecAsync(string sql) => inner.ExecAsync(sql);
    public Task RunAsync(string sql, params object?[] values) => inner.RunAsync(sql, values);
    public Task<long> RunCountedAsync(string sql, params object?[] values) => inner.RunCountedAsync(sql, values);
    public Task<IReadOnlyDictionary<string, object?>?> GetAsync(string sql, params object?[] values) => inner.GetAsync(sql, values);
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(string sql, params object?[] values) => inner.AllAsync(sql, values);
    public async Task<T> TransactionAsync<T>(Func<ISqliteExecutor, Task<T>> callback)
    {
        var value = await inner.TransactionAsync(callback);
        if (FailAfterNextCommit) { FailAfterNextCommit = false; Faults++; throw new IOException("lost SQLite commit acknowledgement"); }
        return value;
    }
    public Task CloseAsync() => inner.CloseAsync();
}
