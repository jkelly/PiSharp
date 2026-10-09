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
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcUpstreamCompactionTests
{
    internal const string Prefix = "rpc.upstream-compaction.";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("alias-summary", "openai-responses", "fixture");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "manual-result-events-durable-append-and-focus", Manual),
        (Prefix + "strict-grammar-no-plan-and-explicit-host-prerequisite", Grammar),
        (Prefix + "toggle-drives-actual-automatic-threshold-and-replacement-reset", Automatic),
        (Prefix + "prospective-result-budget-refuses-before-checkpoint", Budget),
        (Prefix + "abort-joins-original-summary-and-emits-aborted-end", Abort),
        (Prefix + "admitted-checkpoint-wins-late-abort-and-end-observes-idle", Checkpoint),
        (Prefix + "EOF-joins-original-summary-and-borrowed-owner-survives", Eof),
        (Prefix + "active-run-is-aborted-before-manual-compaction", Active)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Success(JsonElement value) => Check(value.GetProperty("success").GetBoolean(), value.GetRawText());
    private static SessionEntry User(string id, string? parent, string text) => new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2026-10-01T00:00:00.000Z", message = new { role = "user", content = text, timestamp = 123 } }));
    private static async Task Manual()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f); var log = f.Session.Snapshot.Log;
        var response = await f.Send(new { id = "manual", type = "compact", customInstructions = "focus exact" }); Success(response);
        var data = response.GetProperty("data");
        Check(data.EnumerateObject().Select(value => value.Name).SequenceEqual(new[] { "summary", "firstKeptEntryId", "tokensBefore", "estimatedTokensAfter", "usage", "details" }), "Manual result field inventory/order differs from pinned result.");
        Check(data.GetProperty("summary").GetString() == "offline summary" && data.GetProperty("firstKeptEntryId").GetString() == "keep" &&
            data.GetProperty("tokensBefore").GetDouble() == 45001 && data.GetProperty("estimatedTokensAfter").GetDouble() == 22505 &&
            data.GetProperty("usage").GetProperty("totalTokens").GetInt64() == 7 &&
            data.GetProperty("details").GetProperty("readFiles").GetArrayLength() == 0 && f.Summary.LastRequest!.Prompt.Contains("focus exact", StringComparison.Ordinal),
            "Manual result lost actual summary/boundary/estimates/usage/details/custom instructions.");
        var frames = f.Records; var start = Array.FindIndex(frames, value => value.GetProperty("type").GetString() == "compaction_start");
        var end = Array.FindIndex(frames, value => value.GetProperty("type").GetString() == "compaction_end");
        var ack = Array.FindIndex(frames, value => value.TryGetProperty("id", out var id) && id.GetString() == "manual");
        Check(start >= 0 && start < end && end < ack && frames[start].GetProperty("reason").GetString() == "manual" &&
            !frames[end].GetProperty("aborted").GetBoolean() && !frames[end].GetProperty("willRetry").GetBoolean() &&
            frames[end].GetProperty("result").GetRawText() == data.GetRawText() && !frames[end].TryGetProperty("errorMessage", out _) && f.StartActive.Single() && f.EndIdle.Single(),
            "Manual lifecycle frames are unordered, invented or emitted while the original reservation remained active.");
        var after = await Bytes(f); var checkpoint = f.Session.Snapshot.Log;
        Check(after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before) && checkpoint.Entries.Length == log.Entries.Length + 1 &&
            checkpoint.Entries[^1].Kind == SessionEntryKind.Compaction && checkpoint.Entries[^1].ParentId == log.LeafId && f.Observed.Single().Reason == SessionCompactionReason.Manual,
            "Manual acknowledgement preceded actual append/rebuild/native observation.");
        var again = await f.Send(new { id = "again", type = "compact" });
        Check(!again.GetProperty("success").GetBoolean() && again.GetProperty("error").GetString() == "Already compacted" && f.Summary.Calls == 1,
            "Already compacted returned a fabricated skipped success or invoked summary again.");
        var repeated = await Bytes(f); Check(after.SequenceEqual(repeated) && f.Transport.Calls == 0, "Repeated manual command wrote or ran a normal provider.");
    }
    private static async Task Grammar()
    {
        await using var f = await Fixture.Create(compactable: false); var before = await Bytes(f);
        foreach (var command in new object[] { new { id = "missing", type = "set_auto_compaction" }, new { id = "wrong", type = "set_auto_compaction", enabled = "true" },
            new { id = "null", type = "set_auto_compaction", enabled = (bool?)null }, new { id = "instructions", type = "compact", customInstructions = 1 } })
        { var result = await f.Send(command); Check(!result.GetProperty("success").GetBoolean(), "Malformed alias was admitted."); }
        var small = await f.Send(new { id = "small", type = "compact" });
        Check(!small.GetProperty("success").GetBoolean() && small.GetProperty("error").GetString() == "Nothing to compact (session too small)", "Small session returned a successful empty result.");
        var end = f.Records.Single(value => value.GetProperty("type").GetString() == "compaction_end");
        Check(!end.GetProperty("aborted").GetBoolean() && !end.TryGetProperty("result", out _) &&
            end.GetProperty("errorMessage").GetString() == "Compaction failed: Nothing to compact (session too small)", "Failure end shape differs.");
        var after = await Bytes(f); Check(before.SequenceEqual(after) && f.Summary.Calls == 0 && f.Session.Snapshot.Fault is null, "Rejected aliases changed the checkpoint or inferred.");
        await using var unbound = await Fixture.Create(hasGenerator: false);
        Check(!(await unbound.Send(new { id = "enable", type = "set_auto_compaction", enabled = true })).GetProperty("success").GetBoolean(), "Missing summary host was reported as enabled.");
        Success(await unbound.Send(new { id = "disable", type = "set_auto_compaction", enabled = false }));
        Check(!(await unbound.Send(new { id = "manual", type = "compact" })).GetProperty("success").GetBoolean() && unbound.Summary.Calls == 0,
            "Missing summary transport inferred or fabricated a compact result.");
    }
    private static async Task Automatic()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f);
        var on = await f.Send(new { id = "on", type = "set_auto_compaction", enabled = true }); Success(on);
        Check(!on.TryGetProperty("data", out _) && f.Session.Snapshot.AutoCompactionEnabled && f.Summary.Calls == 0, "Toggle shape/state or side effects differ.");
        var configured = await Bytes(f); Check(before.SequenceEqual(configured), "Runtime toggle appended unrelated persistent settings.");
        var state = await f.Send(new { id = "state", type = "get_state" }); Success(state);
        Check(state.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean(), "State did not expose toggle.");
        Success(await f.Send(new { id = "prompt", type = "prompt", message = "auto" }));
        await f.Session.WaitForIdleAsync().WaitAsync(Bound); await f.JoinRun();
        Check(f.Summary.Calls == 1 && f.Transport.Calls == 1 && f.Session.Snapshot.Log.Entries[^1].Kind == SessionEntryKind.Compaction &&
            f.Observed.Single().Reason == SessionCompactionReason.Threshold && !f.Observed.Single().WillRetry,
            "Enabled alias did not drive actual model-window threshold transaction through offline normal-turn and summary seams.");
        var start = f.Records.Single(value => value.GetProperty("type").GetString() == "compaction_start");
        var end = f.Records.Single(value => value.GetProperty("type").GetString() == "compaction_end");
        Check(start.GetProperty("reason").GetString() == "threshold" && end.GetProperty("reason").GetString() == "threshold" &&
            !end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() && end.TryGetProperty("result", out _) &&
            !f.Records.Any(value => value.GetProperty("type").GetString() is "auto_compaction_start" or "auto_compaction_end") &&
            f.StartActive.Single() && f.EndIdle.Single(), "Actual threshold lifecycle differs from pinned compaction_start/end contract.");
        var off = await f.Send(new { id = "off", type = "set_auto_compaction", enabled = false }); Success(off);
        Check(!off.TryGetProperty("data", out _) && !f.Session.Snapshot.AutoCompactionEnabled, "Disable failed.");
        Success(await f.Send(new { id = "on-again", type = "set_auto_compaction", enabled = true }));
        Success(await f.Send(new { id = "switch", type = "switch_session", sessionPath = f.Other }));
        Check(!f.Session.Snapshot.AutoCompactionEnabled, "Runtime toggle was falsely persisted across replacement.");
    }
    private static async Task Budget()
    {
        await using var f = await Fixture.Create(options: new(MaximumOutputBytes: 512)); f.Summary.Text = new string('x', 8192);
        var before = await Bytes(f); var result = await f.Send(new { id = "budget", type = "compact" });
        Check(!result.GetProperty("success").GetBoolean() && f.Summary.Calls == 1 && f.Session.Snapshot.Fault is null && f.Observed.Count == 0,
            "Prospective oversized result was accepted, poisoned or observed after an append.");
        var after = await Bytes(f); Check(before.SequenceEqual(after), "Result/end frame budget failed after durable effects.");
        f.Summary.Text = "offline summary"; Success(await f.Send(new { id = "continued", type = "compact" }));
    }
    private static async Task Abort()
    {
        await using var f = await Fixture.Create(); f.Summary.Hold = true; var before = await Bytes(f);
        var original = f.Send(new { id = "held", type = "compact" }); Task<JsonElement>? abort = null;
        try
        {
            await f.Summary.Entered.Task.WaitAsync(Bound);
            var state = await f.Send(new { id = "state", type = "get_state" }); Success(state); Check(state.GetProperty("data").GetProperty("isCompacting").GetBoolean(), "Held generator was not visible.");
            abort = f.Send(new { id = "abort", type = "abort" }); await f.Summary.Canceled.Task.WaitAsync(Bound);
            Check(!original.IsCompleted && !abort.IsCompleted && f.Summary.Active == 1, "Abort abandoned original generator cleanup.");
        }
        finally { f.Summary.Release.TrySetResult(); try { await original; } finally { if (abort is not null) await abort; } }
        if (abort is not null) Success(await abort);
        var response = await original; Check(!response.GetProperty("success").GetBoolean() && response.GetProperty("error").GetString() == "Compaction cancelled", "Canceled command was reported as successful.");
        var end = f.Records.Single(value => value.GetProperty("type").GetString() == "compaction_end");
        Check(end.GetProperty("aborted").GetBoolean() && !end.GetProperty("willRetry").GetBoolean() && !end.TryGetProperty("result", out _) && !end.TryGetProperty("errorMessage", out _) && f.StartActive.Single() && f.EndIdle.Single(), "Aborted end shape or original reservation settlement differs.");
        var after = await Bytes(f); Check(before.SequenceEqual(after) && f.Summary.Active == 0 && f.Summary.Joined.Task.IsCompletedSuccessfully && f.Observed.Count == 0,
            "Canceled summary wrote or did not join.");
    }
    private static async Task Checkpoint()
    {
        await using var f = await Fixture.Create(); f.Storage.Hold = true; var before = f.Session.Snapshot.Log;
        var original = f.Send(new { id = "checkpoint", type = "compact" }); Task<JsonElement>? abort = null;
        try
        {
            await f.Storage.Entered.Task.WaitAsync(Bound); abort = f.Send(new { id = "late", type = "abort" });
            Check(!original.IsCompleted && !abort.IsCompleted && f.Session.Snapshot.Log.Sequence == before.Sequence &&
                !f.Records.Any(value => value.GetProperty("type").GetString() == "compaction_end"), "Admitted physical checkpoint was detached or prematurely published.");
        }
        finally { f.Storage.Release.TrySetResult(); try { await original; } finally { if (abort is not null) await abort; } }
        Success(await original); if (abort is not null) Success(await abort);
        var end = f.Records.Single(value => value.GetProperty("type").GetString() == "compaction_end");
        Check(!end.GetProperty("aborted").GetBoolean() && end.TryGetProperty("result", out _) && f.EndIdle.Single() &&
            f.Session.Snapshot.Log.Entries.Length == before.Entries.Length + 1 && f.Observed.Count == 1 && f.Storage.Joined.Task.IsCompletedSuccessfully,
            "Late abort denied/duplicated durable success or end preceded original checkpoint settlement.");
        _ = await Bytes(f);
    }
    private static async Task Eof()
    {
        await using var f = await Fixture.Create(); f.Summary.Hold = true; var before = await Bytes(f);
        await using var reader = new JsonlReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"eof\",\"type\":\"compact\"}\n")), ownership: JsonlStreamOwnership.Owned);
        var original = f.Dispatcher.RunAsync(reader);
        try { await f.Summary.Entered.Task.WaitAsync(Bound); await f.Summary.Canceled.Task.WaitAsync(Bound); Check(!original.IsCompleted, "EOF abandoned original summary."); }
        finally { f.Summary.Release.TrySetResult(); await original; }
        var after = await Bytes(f); Check(before.SequenceEqual(after) && f.Summary.Active == 0 && f.Summary.Joined.Task.IsCompletedSuccessfully &&
            !f.Session.Snapshot.IsCompacting && f.Session.Snapshot.Fault is null, "EOF did not settle borrowed session without append.");
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "after-eof", 1, JsonData.EmptyObject));
    }
    // agent-session.ts compact(): `await this.abort()` aborts the running turn and waits for idle, then compacts.
    private static async Task Active()
    {
        await using var f = await Fixture.Create(); f.Transport.Hold = true;
        Success(await f.Send(new { id = "prompt", type = "prompt", message = "held" })); await f.Transport.Entered.Task.WaitAsync(Bound);
        var compact = f.Send(new { id = "busy", type = "compact" });
        var deadline = DateTime.UtcNow + Bound;
        while (!f.Transport.LastToken.IsCancellationRequested && DateTime.UtcNow < deadline) await Task.Delay(10);
        Check(f.Transport.LastToken.IsCancellationRequested && !compact.IsCompleted && f.Summary.Calls == 0, "compact did not abort the running turn first.");
        f.Transport.Release.TrySetResult();
        var response = await compact.WaitAsync(Bound); Success(response);
        Check(f.Transport.Joined.Task.IsCompleted && f.Summary.Calls == 1 &&
            f.Records.Any(value => value.GetProperty("type").GetString() == "compaction_start"), "The aborted run was not joined before compaction.");
    }
    private static async Task<byte[]> Bytes(Fixture f)
    {
        var session = f.Session; var before = session.Snapshot;
        static bool Idle(PersistentAgentSessionSnapshot s) => !s.Agent.IsRunning && !s.IsProcessingOperation && !s.IsCompacting && !s.IsConfiguring && !s.IsAdmittingInput && !s.IsEditingContext && !s.IsAppendingExtensionEntry && !s.IsDisposed && !s.IsRetired && s.Fault is null;
        Check(Idle(before) && before.Log.StorageDurability == SessionLogStorageDurability.LocalFileFlush && before.Log.CommittedByteLength is >= 0 and <= 8_388_608, "Read lacks bounded idle acknowledged checkpoint.");
        await using var reader = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length == before.Log.CommittedByteLength, "Physical checkpoint length differs."); var bytes = new byte[checked((int)before.Log.CommittedByteLength)]; await reader.ReadExactlyAsync(bytes);
        var after = session.Snapshot; Check(Idle(after) && after.Log.Sequence == before.Log.Sequence && after.Log.LeafId == before.Log.LeafId &&
            after.Log.CommittedByteLength == before.Log.CommittedByteLength && reader.Length == bytes.Length && reader.Position == bytes.Length, "Read crossed mutation or abandoned reader."); return bytes;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "PiSharp-rpc-alias-compaction-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal readonly Summary Summary = new(); internal readonly Transport Transport = new(); internal readonly StorageFactory Storage = new();
        internal readonly List<SessionCompactionObservation> Observed = []; internal readonly List<bool> StartActive = [], EndIdle = [];
        private readonly Capture output = new(); private SessionRuntimeRegistry registry = null!; private int ids;
        internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!; internal PersistentAgentSession Session => Owner.Current.Session;
        internal JsonElement[] Records => Encoding.UTF8.GetString(output.Bytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonData.Parse(line).Value).ToArray();
        private async Task<PersistentAgentSession> Open(string path)
        {
            var session = await PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 123, () => "alias-" + Interlocked.Increment(ref ids),
                new(SessionLogStoreOptions: new(StorageFactory: Storage)), fallbackModel: Model);
            session.ConfigureCompactionObservation(value => { Check(session.Snapshot.Log.ById.ContainsKey(value.CompactionEntry.Id), "Native observation preceded checkpoint."); Observed.Add(value); return ValueTask.CompletedTask; });
            return session;
        }
        internal static async Task<Fixture> Create(bool compactable = true, bool hasGenerator = true, RpcDispatchOptions? options = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.registry = new([new(Model, f.Transport)], [], new DenyPolicy());
            foreach (var path in new[] { f.Source, f.Other })
            {
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-01T00:00:00.000Z", cwd = f.Root }));
                await using var store = await SessionLogStore.CreateNewAsync(path, header);
                await store.AppendAsync(path == f.Source && compactable ? [User("old", null, new string('o', 90000)), User("keep", "old", new string('k', 90000)), User("recent", "keep", "four")] : [User("u", null, "four")]);
            }
            var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path));
            f.output.BeforeWrite = record => { if (record.GetProperty("type").GetString() == "compaction_start") f.StartActive.Add(f.Session.Snapshot.IsCompacting);
                if (record.GetProperty("type").GetString() == "compaction_end") f.EndIdle.Add(!f.Session.Snapshot.IsCompacting); };
            var wire = JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, api = Model.Api, provider = Model.Provider, name = Model.Id, baseUrl = "https://offline.invalid", reasoning = false,
                input = new[] { "text" }, contextWindow = 32768, maxTokens = 16384, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } }));
            try { f.Dispatcher = new(session, new JsonlWriter(f.output, ownership: JsonlStreamOwnership.Borrowed), () => 123, [new(Model, wire)], options,
                RpcSessionOwnership.Borrowed, sessionOwner: f.Owner, summaryGenerator: hasGenerator ? f.Summary : null); return f; }
            catch { await f.Owner.DisposeAsync(); f.output.Dispose(); throw; }
        }
        internal async Task<JsonElement> Send(object command)
        {
            var raw = JsonData.Parse(JsonSerializer.Serialize(command)); await Dispatcher.SubmitAsync(raw); var id = raw.Value.GetProperty("id").GetString();
            return Records.Single(value => value.GetProperty("type").GetString() == "response" && value.TryGetProperty("id", out var identity) && identity.GetString() == id);
        }
        internal async Task JoinRun()
        {
            using var deadline = new CancellationTokenSource(Bound);
            for (var attempt = 0; ; attempt++) { deadline.Token.ThrowIfCancellationRequested(); var state = await Send(new { id = "join-" + attempt, type = "get_state" }); Success(state);
                if (state.GetProperty("data").GetProperty("pisharpRunOwnerSettled").GetBoolean()) return; await Task.Delay(1, deadline.Token); }
        }
        public async ValueTask DisposeAsync()
        {
            Summary.Release.TrySetResult(); Storage.Release.TrySetResult(); Transport.Release.TrySetResult();
            try { await Dispatcher.DisposeAsync(); } finally { await Owner.DisposeAsync(); output.Dispose(); }
            Check(Summary.Active == 0 && Transport.Active == 0, "Original offline summary/turn did not join.");
            // Retain bounded authored durable fixtures; no recursive cleanup or detached original task.
        }
    }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new(); internal Action<JsonElement>? BeforeWrite; internal byte[] Bytes() { lock (gate) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); BeforeWrite?.Invoke(JsonData.Parse(Encoding.UTF8.GetString(buffer.Span).TrimEnd('\n', '\r')).Value); lock (gate) Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    private sealed class Summary : ISessionSummaryGenerator
    {
        internal bool Hold; internal string Text = "offline summary"; internal int Calls, Active; internal SessionSummaryRequest? LastRequest;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        {
            Calls++; Active++; LastRequest = request;
            try { Check(request.Kind == SessionSummaryKind.History && request.Model == Model, "Unexpected native summary request.");
                if (Hold) { using var cancellation = token.UnsafeRegister(_ => Canceled.TrySetResult(), null); Entered.TrySetResult(); await Release.Task; }
                token.ThrowIfCancellationRequested(); return new(Text, new(4, 3, 0, 0, 7, new(0, 0, 0, 0, 0))); }
            finally { Active--; Joined.TrySetResult(); }
        }
    }
    private sealed class Transport : IChatTransport
    {
        internal bool Hold; internal int Calls, Active; internal CancellationToken LastToken; internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Calls++; Active++; LastToken = token; var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [], new(40000, 0, 0, 0, 40000, new(0, 0, 0, 0, 0)), StopReason.Stop);
            try { yield return new StreamStarted(final with { StopReason = StopReason.Pending }); Entered.TrySetResult(); if (Hold) await Release.Task; token.ThrowIfCancellationRequested(); yield return new StreamDone(StopReason.Stop, final); }
            finally { Active--; Joined.TrySetResult(); }
        }
    }
    private sealed class DenyPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        internal bool Hold; internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this);
        private sealed class Storage(ISessionLogStorage inner, StorageFactory owner) : ISessionLogStorage
        {
            public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
            public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
            public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
            public async ValueTask BeforeCheckpointAsync() { try { if (owner.Hold) { owner.Entered.TrySetResult(); await owner.Release.Task; } await inner.BeforeCheckpointAsync(); } finally { owner.Joined.TrySetResult(); } }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
