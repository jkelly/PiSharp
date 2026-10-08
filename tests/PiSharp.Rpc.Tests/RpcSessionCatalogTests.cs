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
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcSessionCatalogTests
{
    private static readonly ModelDescriptor Model = new("catalog-rpc", "openai-responses", "fixture");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"catalog-rpc","api":"openai-responses","provider":"fixture","name":"Offline catalog","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Timestamp = "2026-10-02T00:00:00.000Z";

    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("rpc.catalog-resume-listing-keeps-identical-file-and-session-IDs-in-explicit-stores-distinct", Listing),
        ("rpc.catalog-resume-selected-sibling-and-root-return-fresh-durable-generation-authority", SelectedResume),
        ("rpc.catalog-resume-header-listing-cannot-admit-invalid-full-target-or-retire-source", FullTargetValidation),
        ("rpc.catalog-resume-and-switch-output-response-and-event-budgets-precede-source-retirement", OutputPreflight),
        ("rpc.catalog-resume-held-reader-allows-state-and-abort-while-listing-and-EOF-join-cleanup", HeldDiscovery)
    ];

    private static async Task Listing()
    {
        await using var f = await Fixture.Open(); var source = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
        await f.Send(new { id = "first", type = "pisharp_list_sessions", pageSize = 1, cwd = f.Files.Root });
        var first = Success(f.Response("first")); var cursor = first.GetProperty("nextCursor").GetString();
        Check(cursor is not null, "First RPC page lost the second configured store."); Equal(1L, first.GetProperty("generation").GetInt64());
        await f.Send(new { id = "second", type = "pisharp_list_sessions", pageSize = 1, cursor, cwd = f.Files.Root });
        var second = Success(f.Response("second")); Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        var items = first.GetProperty("sessions").EnumerateArray().Concat(second.GetProperty("sessions").EnumerateArray()).ToArray();
        Equal(2, items.Length); Equal(2, items.Select(item => item.GetProperty("key").GetString()).Distinct().Count());
        Equal(1, items.Select(item => item.GetProperty("sessionId").GetString()).Distinct().Count());
        Equal(1, items.Select(item => item.GetProperty("fileName").GetString()).Distinct().Count());
        Check(items.Select(item => item.GetProperty("storeId").GetString()).Order().SequenceEqual(new[] { "one", "two" }), "RPC listing aliased identical IDs across explicit stores.");
        foreach (var item in items)
        {
            var current = item.GetProperty("sessionFile").GetString() == f.Files.Source;
            Equal(current, item.GetProperty("isCurrent").GetBoolean()); Equal("same", item.GetProperty("sessionId").GetString());
            Equal((long)(current ? source.Length : target.Length), item.GetProperty("fileBytes").GetInt64());
            Equal(f.Files.Root, item.GetProperty("cwd").GetString()); Equal(64, item.GetProperty("key").GetString()!.Length);
        }
        Equal(0, first.GetProperty("skippedFiles").GetInt32() + second.GetProperty("skippedFiles").GetInt32());
        Equal(0, first.GetProperty("unavailableStores").GetInt32() + second.GetProperty("unavailableStores").GetInt32());
        await f.Send(new { id = "invalid", type = "pisharp_list_sessions", pageSize = 129 }); Failure(f.Response("invalid"));
        await f.Send(new { id = "state", type = "get_state" }); Equal(f.Files.Source, Success(f.Response("state")).GetProperty("sessionFile").GetString());
        Equal(0, f.Storage.Opened.Count); Equal(1L, f.Owner.Current.Generation);
        SameBytes(source, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target)); f.AssertOwnedFiles();
    }

    private static async Task SelectedResume()
    {
        foreach (var root in new[] { false, true })
        {
            await using var f = await Fixture.Open(); var previous = f.Owner.Current;
            var source = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
            await f.Send(new { id = "list", type = "pisharp_list_sessions" });
            var key = Success(f.Response("list")).GetProperty("sessions").EnumerateArray().Single(item => item.GetProperty("storeId").GetString() == "two").GetProperty("key").GetString()!;
            await f.Send(root ? new { id = "resume", type = "pisharp_resume_session", catalogKey = key, root = true } :
                (object)new { id = "resume", type = "pisharp_resume_session", catalogKey = key, leafId = "left" });
            var resumed = Success(f.Response("resume")); Equal(false, resumed.GetProperty("cancelled").GetBoolean());
            Equal("same", resumed.GetProperty("sessionId").GetString()); Equal(f.Files.Target, resumed.GetProperty("sessionFile").GetString()); Equal(2L, resumed.GetProperty("generation").GetInt64());
            var notification = f.Output.Records.Single(record => Type(record) == "session_switched").Value;
            Equal(f.Files.Target, notification.GetProperty("sessionFile").GetString()); Equal("same", notification.GetProperty("sessionId").GetString()); Equal(2L, notification.GetProperty("generation").GetInt64());
            Equal("right", f.Owner.Current.Session.Snapshot.Log.LeafId); Equal(root ? null : "left", f.Owner.Current.Session.Snapshot.Context.LeafId);
            Check(States(f.Owner.Current.Session).SequenceEqual(root ? Array.Empty<string>() : new[] { "base", "left" }), "Resume inherited a physical sibling's state.");
            await f.Send(new { id = "entries", type = "get_entries" }); var entries = Success(f.Response("entries"));
            Equal(root ? null : "left", entries.GetProperty("leafId").GetString()); Equal(4, entries.GetProperty("entries").GetArrayLength());
            Equal("1.00e400", entries.GetProperty("entries")[0].GetProperty("data").GetProperty("opaque").GetRawText());
            await f.Send(new { id = "state", type = "get_state" }); Equal(f.Files.Target, Success(f.Response("state")).GetProperty("sessionFile").GetString());
            await Throws<InvalidOperationException>(() => f.Owner.AppendExtensionEntryAsync(previous, new("fixture", "stale", 1, JsonData.EmptyObject)));
            await Throws<InvalidOperationException>(() => f.Owner.ListSessionsAsync(previous, new()));
            var receipt = await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "state", 1, JsonData.Parse("{\"value\":\"fresh\"}")));
            Equal(root ? null : "left", receipt.Entry.ParentId); Check(receipt.Append.ByteLength > 0, "Fresh RPC attachment could not physically append target.");
            SameBytes(source, await Bytes(f.Files.Source)); Prefix(target, await Bytes(f.Files.Target)); f.AssertOwnedFiles();
            await f.CloseRuntime();
            await using var reopenedSource = await f.Reopen(f.Files.Source); Equal("a", reopenedSource.Snapshot.Context.LeafId);
            await using var reopened = await f.Reopen(f.Files.Target);
            Check(States(reopened).SequenceEqual(root ? new[] { "fresh" } : new[] { "base", "left", "fresh" }), "Independent reopen lost the durable selected branch checkpoint.");
            Equal("1.00e400", reopened.Snapshot.Log.Entries[0].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText());
        }
    }

    private static async Task FullTargetValidation()
    {
        foreach (var corruption in new[] { "tail", "parent", "duplicate", "model" })
        {
            await using var f = await Fixture.Open();
            var header = Header("same", f.Files.Root);
            var text = corruption switch
            {
                "tail" => header + "\n{broken",
                "parent" => header + "\n" + Future("bad", "missing", "{}").WireBody + "\n",
                "duplicate" => header + "\n" + Future("dup", null, "{}").WireBody + "\n" + Future("dup", "dup", "{}").WireBody + "\n",
                _ => header + "\n{\"type\":\"model_change\",\"id\":\"model\",\"parentId\":null,\"timestamp\":\"" + Timestamp + "\",\"provider\":\"absent\",\"modelId\":\"absent\"}\n"
            };
            await File.WriteAllTextAsync(f.Files.Target, text); var previous = f.Owner.Current;
            var source = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
            await f.Send(new { id = "list", type = "pisharp_list_sessions" });
            var items = Success(f.Response("list")).GetProperty("sessions"); Equal(2, items.GetArrayLength());
            var key = items.EnumerateArray().Single(item => item.GetProperty("storeId").GetString() == "two").GetProperty("key").GetString()!;
            await f.Send(new { id = "invalid", type = "pisharp_resume_session", catalogKey = key }); Failure(f.Response("invalid"));
            await f.AssertRollback(previous, source, target);
            await f.Owner.AppendExtensionEntryAsync(previous, new("fixture", "after_invalid", 1, JsonData.Parse("{\"continued\":true}")));
            await f.Send(new { id = "state", type = "get_state" }); Equal(f.Files.Source, Success(f.Response("state")).GetProperty("sessionFile").GetString());
            Prefix(source, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
            await f.CloseRuntime(); await using var reopened = await f.Reopen(f.Files.Source);
            Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("continued").GetBoolean(), "Invalid imported target destroyed A's durable continuation.");
        }
    }

    private static async Task OutputPreflight()
    {
        foreach (var resume in new[] { true, false })
        {
            var targetId = new string('x', 1024); int responseBytes, eventBytes;
            await using (var control = await Fixture.Open(targetId: targetId))
            {
                await control.Replace("budget", resume); Success(control.Response("budget"));
                responseBytes = control.Output.JsonBytes(control.Response("budget"));
                eventBytes = control.Output.JsonBytes(control.Output.Records.Single(record => Type(record) == "session_switched"));
                Check(responseBytes > eventBytes && eventBytes > 256, "Actual successful wire frames did not qualify the separate response and combined overflow schedules.");
            }
            // The shared output bound makes the complete success response larger than the matching event.
            // Qualify response-only overflow and overflow of both actual frames without claiming an event-only case.
            foreach (var budget in new[] { responseBytes - 1, eventBytes - 1 })
            {
                await using var f = await Fixture.Open(new(MaximumOutputBytes: budget), targetId); var previous = f.Owner.Current;
                var source = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
                await f.Replace("budget", resume); var error = Failure(f.Response("budget"));
                Check(error.GetProperty("error").GetString()!.Contains("limits", StringComparison.Ordinal), "Output preflight failure was not reported as the configured resource limit.");
                Check(f.Output.JsonBytes(f.Response("budget")) <= budget, "Bounded failure response exceeded the configured frame budget.");
                await f.AssertRollback(previous, source, target);
                await f.Owner.AppendExtensionEntryAsync(previous, new("fixture", "after_budget", 1, JsonData.Parse("{\"continued\":true}")));
                await f.Send(new { id = "state", type = "get_state" }); Equal(f.Files.Source, Success(f.Response("state")).GetProperty("sessionFile").GetString());
                Prefix(source, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
            }
            await using (var exact = await Fixture.Open(new(MaximumOutputBytes: responseBytes), targetId))
            {
                var source = await Bytes(exact.Files.Source); await exact.Replace("budget", resume); Success(exact.Response("budget"));
                Equal(responseBytes, exact.Output.JsonBytes(exact.Response("budget"))); Equal(2L, exact.Owner.Current.Generation);
                Equal(eventBytes, exact.Output.JsonBytes(exact.Output.Records.Single(record => Type(record) == "session_switched")));
                var receipt = await exact.Owner.AppendExtensionEntryAsync(exact.Owner.Current, new("fixture", "exact", 1, JsonData.EmptyObject));
                Check(receipt.Append.ByteLength > 0, "Exact response limit admitted an unusable target."); SameBytes(source, await Bytes(exact.Files.Source)); exact.AssertOwnedFiles();
            }
        }
    }

    private static async Task HeldDiscovery()
    {
        await HeldListingAbort(); await HeldListingEof();
    }
    private static async Task HeldListingAbort()
    {
        await using var f = await Fixture.Open(); var previous = f.Owner.Current;
        var source = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target); f.Readers.Arm();
        var listing = f.Send(new { id = "list", type = "pisharp_list_sessions" });
        try
        {
            await f.Readers.ReadEntered.Task.WaitAsync(Bound);
            await f.Send(new { id = "state", type = "get_state" }); Equal(f.Files.Source, Success(f.Response("state")).GetProperty("sessionFile").GetString());
            Check(!listing.IsCompleted, "Polling bypassed the held physical catalog read.");
            var abort = f.Send(new { id = "abort", type = "abort" }); await f.Readers.CloseEntered.Task.WaitAsync(Bound);
            await abort.WaitAsync(Bound); Check(f.Response("abort").Value.GetProperty("success").GetBoolean(), "Concurrent RPC abort could not cancel discovery.");
            Check(!listing.IsCompleted && !f.Readers.TargetClosed && !f.Output.Records.Any(record => IsResponse(record, "list")), "Canceled listing returned before its actual held reader close.");
            SameBytes(source, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
            f.Readers.CloseRelease.TrySetResult(); await listing.WaitAsync(Bound); Failure(f.Response("list")); Equal(1, f.Readers.HeldCloseCalls);
            Check(f.Readers.TargetClosed && ReferenceEquals(previous, f.Owner.Current) && !previous.Session.Snapshot.IsRetired, "Canceled discovery changed source attachment or retained reader authority.");
            using (var exclusive = new FileStream(f.Files.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await f.Owner.AppendExtensionEntryAsync(previous, new("fixture", "after_abort", 1, JsonData.Parse("{\"continued\":true}")));
            Prefix(source, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target)); f.AssertOwnedFiles();
            await f.CloseRuntime(); await using var reopened = await f.Reopen(f.Files.Source);
            Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("continued").GetBoolean(), "Source writer did not remain durable after joined listing cancellation.");
        }
        finally { f.Readers.ReadRelease.TrySetResult(); f.Readers.CloseRelease.TrySetResult(); await Drain(listing); }
    }
    private static async Task HeldListingEof()
    {
        await using var f = await Fixture.Open(); var previous = f.Owner.Current;
        var source = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target); f.Readers.Arm();
        var frames = JsonSerializer.Serialize(new { id = "list", type = "pisharp_list_sessions" }) + "\n" + JsonSerializer.Serialize(new { id = "state", type = "get_state" }) + "\n";
        var input = new EofInput(Encoding.UTF8.GetBytes(frames)); var reader = new JsonlReader(input, ownership: JsonlStreamOwnership.Owned);
        var run = f.Dispatcher.RunAsync(reader);
        try
        {
            await f.Readers.ReadEntered.Task.WaitAsync(Bound); await f.Output.WaitResponse("state").WaitAsync(Bound);
            Equal(f.Files.Source, Success(f.Response("state")).GetProperty("sessionFile").GetString()); await input.EofEntered.Task.WaitAsync(Bound);
            input.EofRelease.TrySetResult(); await f.Readers.CloseEntered.Task.WaitAsync(Bound);
            Check(!run.IsCompleted && !f.Dispatcher.Completion.IsCompleted && !f.Readers.TargetClosed && !f.Output.Records.Any(record => IsResponse(record, "list")),
                "RPC EOF escaped admitted discovery's actual held cleanup.");
            f.Readers.CloseRelease.TrySetResult(); await run.WaitAsync(Bound); await f.Dispatcher.Completion.WaitAsync(Bound);
            Failure(f.Response("list")); Equal(1, f.Readers.HeldCloseCalls); Equal(1, input.DisposeCalls);
            Check(f.Readers.TargetClosed && ReferenceEquals(previous, f.Owner.Current) && !previous.Session.Snapshot.IsRetired && !previous.Session.Snapshot.IsDisposed,
                "Borrowed RPC EOF retired its source or retained physical discovery work.");
            SameBytes(source, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
            using (var exclusive = new FileStream(f.Files.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            await f.Owner.AppendExtensionEntryAsync(previous, new("fixture", "after_eof", 1, JsonData.EmptyObject)); Prefix(source, await Bytes(f.Files.Source)); f.AssertOwnedFiles();
        }
        finally { input.EofRelease.TrySetResult(); f.Readers.ReadRelease.TrySetResult(); f.Readers.CloseRelease.TrySetResult(); await Drain(run); await reader.DisposeAsync(); }
    }

    private static string Header(string id, string cwd) => JsonSerializer.Serialize(new { type = "session", version = 3, id, timestamp = Timestamp, cwd });
    private static SessionEntry Future(string id, string? parent, string data) => Codec.Parse("{\"type\":\"future\",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Timestamp + "\",\"data\":" + data + "}");
    private static SessionEntry State(string id, string parent, string value) => Codec.Parse(JsonSerializer.Serialize(new { type = "custom", customType = "pisharp.extension-state", id, parentId = parent,
        timestamp = Timestamp, data = new { extensionId = "fixture", entryKind = "state", schemaVersion = 1, data = new { value } } }));
    private static IEnumerable<string> States(PersistentAgentSession session) => session.Snapshot.Context.Ancestry.Where(entry => entry.Type == "custom")
        .Select(entry => entry.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("value").GetString()!);
    private static JsonElement Success(JsonData response) { Check(response.Value.GetProperty("success").GetBoolean(), "Expected successful RPC response: " + response); return response.Value.GetProperty("data"); }
    private static JsonElement Failure(JsonData response) { Check(!response.Value.GetProperty("success").GetBoolean(), "Expected rejected RPC response."); return response.Value; }
    private static string Type(JsonData record) => record.Value.GetProperty("type").GetString()!;
    private static bool IsResponse(JsonData record, string id) => Type(record) == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id;
    private static async Task<byte[]> Bytes(string path)
    { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var copy = new MemoryStream(); await file.CopyToAsync(copy); return copy.ToArray(); }
    private static void SameBytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Existing bytes changed before an authorized append.");
    private static void Prefix(byte[] before, byte[] after) => Check(after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before), "Checkpoint did not physically extend the intact source log.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; observed {actual}.");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task task) { try { await task.WaitAsync(Bound); } catch (Exception) when (task.IsCompleted) { } }

    private sealed class Fixture : IAsyncDisposable
    {
        public Files Files { get; } = new(); public Capture Output { get; } = new();
        public StorageFactory Storage { get; } = new(); public NoBranchEffects Branches { get; } = new();
        public CatalogFiles Readers { get; private set; } = null!;
        public ReplaceableAgentSession Owner { get; private set; } = null!;
        public RpcSessionDispatcher Dispatcher { get; private set; } = null!;
        private readonly NoTransport transport = new(); private readonly SessionRuntimeRegistry runtime; private int ids;
        private Fixture() { runtime = new([new(Model, transport)], [], new NoPolicy()); }
        private string NextId() => "append-" + Interlocked.Increment(ref ids);
        public static async Task<Fixture> Open(RpcDispatchOptions? options = null, string targetId = "same")
        {
            var f = new Fixture(); PersistentAgentSession? source = null;
            try
            {
                await Seed(f.Files.Source, "same", f.Files.Root, [Future("a", null, "{}")]);
                await Seed(f.Files.Target, targetId, f.Files.Root, [Future("root", null, "{\"opaque\":1.00e400}"), State("base", "root", "base"), State("left", "base", "left"), State("right", "base", "right")]);
                source = await f.Reopen(f.Files.Source); f.Readers = new(f.Files);
                var catalog = new SessionCatalog([new("one", f.Files.One), new("two", f.Files.Two)], fileSystem: f.Readers);
                var lifecycle = new PersistentSessionLifecycle(f.runtime, () => 0, f.NextId, new(SessionLogStoreOptions: new(StorageFactory: f.Storage)), fileSystem: f.Branches, catalog: catalog);
                f.Owner = ReplaceableAgentSession.WithLifecycle(source, lifecycle);
                f.Dispatcher = new(source, new JsonlWriter(f.Output, ownership: JsonlStreamOwnership.Borrowed), () => 0,
                    [new(Model, ModelWire)], options, RpcSessionOwnership.Borrowed, sessionOwner: f.Owner); source = null; return f;
            }
            catch
            {
                if (source is not null) await source.DisposeAsync(); if (f.Dispatcher is not null) await f.Dispatcher.DisposeAsync();
                if (f.Owner is not null) await f.Owner.DisposeAsync(); f.Files.Dispose(); throw;
            }
        }
        public Task Send(object record) => Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(record)));
        public JsonData Response(string id) => Output.Records.Single(record => IsResponse(record, id));
        public async Task Replace(string id, bool resume)
        {
            if (!resume) { await Send(new { id, type = "switch_session", sessionPath = Files.Target }); return; }
            var key = (await Owner.ListSessionsAsync(Owner.Current, new())).Items.Single(item => item.StoreId == "two").Key;
            await Send(new { id, type = "pisharp_resume_session", catalogKey = key });
        }
        public Task<PersistentAgentSession> Reopen(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0, NextId, fallbackModel: Model);
        public async Task CloseRuntime() { await Dispatcher.DisposeAsync(); await Owner.DisposeAsync(); }
        public void AssertOwnedFiles() { Equal(0, Branches.Calls); Check(Files.OnlyKnown(), "RPC catalog/resume created, deleted or moved an existing file."); Check(!Output.Records.Any(record => Type(record) == "agent_start"), "Lifecycle acquired provider execution."); }
        public async Task AssertRollback(AgentSessionAttachment previous, byte[] source, byte[] target)
        {
            Check(ReferenceEquals(previous, Owner.Current) && previous.Generation == 1 && !previous.Session.Snapshot.IsRetired && !previous.Session.Snapshot.IsDisposed && previous.Session.Snapshot.Fault is null,
                "RPC rejection crossed actual source retirement.");
            Check(!Output.Records.Any(record => Type(record) == "session_switched"), "Rejected target emitted a committed switch event.");
            Equal(1, Storage.Opened.Count); Check(Storage.Opened.Single().Closed && Storage.Opened.Single().DisposeCalls == 1, "RPC target rollback did not join its actual writer close.");
            using (var exclusive = new FileStream(Files.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            SameBytes(source, await Bytes(Files.Source)); SameBytes(target, await Bytes(Files.Target)); AssertOwnedFiles();
        }
        public async ValueTask DisposeAsync()
        {
            Readers.ReadRelease.TrySetResult(); Readers.CloseRelease.TrySetResult();
            try { await CloseRuntime(); Equal(0, transport.Calls); }
            finally { Output.Dispose(); Files.Dispose(); }
        }
    }
    private static async Task Seed(string path, string id, string cwd, ImmutableArray<SessionEntry> entries)
    { await using var store = await SessionLogStore.CreateNewAsync(path, Codec.Parse(Header(id, cwd))); await store.AppendAsync(entries); }
    private sealed class NoTransport : IChatTransport
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("RPC catalog/resume must not call a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("RPC catalog/resume must not acquire a tool."); }
    private sealed class NoBranchEffects : ISessionBranchFileSystem
    {
        public int Calls { get; private set; }
        public ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path) { Calls++; throw new InvalidOperationException("Imported session attempted branch creation."); }
        public ValueTask PublishNewAsync(string temporary, string destination) { Calls++; throw new InvalidOperationException("Imported session attempted publication."); }
        public ValueTask DeleteOwnedAsync(string path) { Calls++; throw new InvalidOperationException("Imported session attempted deletion."); }
    }
    private sealed class Files : IDisposable
    {
        private const string Prefix = "PiSharp-rpc-catalog-";
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } public string One => Path.Combine(Root, "one"); public string Two => Path.Combine(Root, "two");
        public string Source => Path.Combine(One, "same.jsonl"); public string Target => Path.Combine(Two, "same.jsonl");
        public Files() { Root = Path.GetFullPath(Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"))); ValidateRoot(); Directory.CreateDirectory(One); Directory.CreateDirectory(Two); }
        public bool OnlyKnown() => Directory.EnumerateFileSystemEntries(Root).Order().SequenceEqual(new[] { One, Two }.Order()) && Directory.EnumerateFileSystemEntries(One).SequenceEqual(new[] { Source }) && Directory.EnumerateFileSystemEntries(Two).SequenceEqual(new[] { Target });
        public void ValidatePath(string path) => Check(Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), Comparison) && (path == Source || path == Target), "RPC fixture received a path outside its owned files.");
        public void ValidateStore(string store) => Check(Path.IsPathFullyQualified(store) && string.Equals(store, Path.GetFullPath(store), Comparison) && (store == One || store == Two), "RPC fixture received an unowned store.");
        private void ValidateRoot()
        {
            var name = Path.GetFileName(Root);
            Check(Path.IsPathFullyQualified(Root) && string.Equals(Root, Path.GetFullPath(Root), Comparison) && string.Equals(Path.GetDirectoryName(Root), temp, Comparison) &&
                name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Refusing cleanup outside owned RPC catalog fixture.");
        }
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return; Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "RPC root became a reparse point.");
            foreach (var store in new[] { One, Two })
            {
                ValidateStore(store); if (!Directory.Exists(store)) continue; Check((File.GetAttributes(store) & FileAttributes.ReparsePoint) == 0, "RPC store became a reparse point.");
                foreach (var path in Directory.EnumerateFileSystemEntries(store))
                { ValidatePath(path); Check((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "RPC cleanup encountered an unowned directory or link."); File.Delete(path); }
                Directory.Delete(store, recursive: false);
            }
            Directory.Delete(Root, recursive: false);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public List<Storage> Opened { get; } = [];
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        { var storage = new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token)); Opened.Add(storage); return storage; }
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        public bool Closed { get; private set; } public int DisposeCalls { get; private set; }
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long expected) => inner.PositionForAppend(expected); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk(); public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync() { DisposeCalls++; await inner.DisposeAsync(); Closed = true; }
    }
    private sealed class CatalogFiles(Files files) : ISessionCatalogFileSystem
    {
        public bool Armed { get; private set; } public bool TargetClosed { get; set; } public int HeldCloseCalls { get; set; }
        public readonly TaskCompletionSource ReadEntered = Gate(), ReadRelease = Gate(), CloseEntered = Gate(), CloseRelease = Gate();
        public void Arm() { Armed = true; TargetClosed = false; }
        public bool IsDirectoryLink(string directory) { files.ValidateStore(directory); return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0; }
        public IEnumerable<string> EnumerateFileNames(string directory) { files.ValidateStore(directory); return Directory.EnumerateFileSystemEntries(directory).Select(path => Path.GetFileName(path)!); }
        public SessionCatalogFileMetadata GetMetadata(string path)
        { files.ValidatePath(path); var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc.Ticks, (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0); }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); files.ValidatePath(path);
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 512, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return ValueTask.FromResult(path == files.Target && Armed ? (Stream)new HeldReader(stream, this) : stream);
        }
    }
    private sealed class HeldReader(Stream actual, CatalogFiles owner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => actual.Length; public override long Position { get => actual.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { owner.ReadEntered.TrySetResult(); await owner.ReadRelease.Task.WaitAsync(Bound, token); return await actual.ReadAsync(buffer, token); }
        public override async ValueTask DisposeAsync()
        { owner.HeldCloseCalls++; owner.CloseEntered.TrySetResult(); await owner.CloseRelease.Task.WaitAsync(Bound); await actual.DisposeAsync(); owner.TargetClosed = true; GC.SuppressFinalize(this); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Capture : Stream
    {
        private readonly object gate = new(); private readonly List<(JsonData Record, int Bytes)> records = []; private readonly Dictionary<string, TaskCompletionSource> responses = [];
        private (JsonData Record, int Bytes)? written;
        public JsonData[] Records { get { lock (gate) return records.Select(item => item.Record).ToArray(); } }
        public int JsonBytes(JsonData record) { lock (gate) return records.Single(item => ReferenceEquals(item.Record, record)).Bytes; }
        public Task WaitResponse(string id)
        { lock (gate) { if (!responses.TryGetValue(id, out var signal)) { signal = Gate(); responses.Add(id, signal); } if (records.Any(item => IsResponse(item.Record, id))) signal.TrySetResult(); return signal.Task; } }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var text = Encoding.UTF8.GetString(buffer.Span);
            Check(text.EndsWith('\n') && text.Count(value => value == '\n') == 1, "RPC output interleaved frames or omitted LF.");
            lock (gate) { Check(written is null, "RPC wrote another frame before flushing its previous frame."); written = (JsonData.Parse(text), buffer.Length - 1); } return ValueTask.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); lock (gate)
            {
                var item = written ?? throw new InvalidOperationException("RPC flushed without a complete frame."); records.Add(item); written = null;
                if (Type(item.Record) == "response" && item.Record.Value.TryGetProperty("id", out var id) && responses.TryGetValue(id.GetString()!, out var signal)) signal.TrySetResult();
            }
            return Task.CompletedTask;
        }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class EofInput(byte[] bytes) : Stream
    {
        private int position; public int DisposeCalls { get; private set; } public readonly TaskCompletionSource EofEntered = Gate(), EofRelease = Gate();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); if (position < bytes.Length) { var length = Math.Min(buffer.Length, bytes.Length - position); bytes.AsMemory(position, length).CopyTo(buffer); position += length; return length; }
            EofEntered.TrySetResult(); await EofRelease.Task.WaitAsync(Bound, token); return 0;
        }
        public override ValueTask DisposeAsync() { DisposeCalls++; GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => bytes.Length; public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
