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

internal static class RpcSessionMetadataTests
{
    internal const string Prefix = "rpc.session-metadata.";
    private static readonly ModelDescriptor Model = new("metadata", "openai-responses", "fixture");
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "whole-session-accounting-and-selected-context", WholeHistory),
        (Prefix + "context-compaction-null-post-usage-and-window-omission", Context),
        (Prefix + "name-sanitization-append-reopen-and-stale-owner", Naming),
        (Prefix + "invalid-canceled-and-budget-commands-preserve-checkpoint", Rejections),
        (Prefix + "stats-during-held-provider-and-idle-name-fence", Active),
        (Prefix + "admitted-name-checkpoint-cancellation-and-original-disposal-join", Checkpoint),
        (Prefix + "stats-follow-published-attachment-before-dispatcher-rebind", ReplacementStats)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Success(JsonElement value) => Check(value.GetProperty("success").GetBoolean(), value.GetRawText());
    private static object Usage(int count) => new { input = count, output = count * 2, cacheRead = count * 3,
        cacheWrite = count * 4, totalTokens = 999, cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = .125 } };
    private static object Assistant(int usage, bool call = false) => new { role = "assistant", api = Model.Api,
        provider = Model.Provider, model = Model.Id, timestamp = 123, stopReason = "stop", usage = Usage(usage),
        content = call ? new object[] { new { type = "toolCall", id = "call", name = "inert", arguments = new { exact = true } } }
            : new object[] { new { type = "text", text = "done" } } };
    private static SessionEntry Entry(string id, string? parent, string type, object fields)
    {
        var extra = JsonSerializer.Serialize(fields)[1..^1];
        return new SessionEntryCodec().Parse("{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) +
            ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"2026-10-01T00:00:00.000Z\"," + extra + "}");
    }
    private static SessionEntry Message(string id, string? parent, object message) => Entry(id, parent, "message", new { message });
    private static SessionEntry User(string id = "u", string? parent = null) => Message(id, parent, new { role = "user", content = "four", timestamp = 123 });
    private static async Task WholeHistory()
    {
        await using var f = await Fixture.Create([
            Message("system", null, new { role = "system", content = "system", timestamp = 123 }), User(parent: "system"),
            Message("left", "u", Assistant(1, call: true)),
            Message("result", "left", new { role = "toolResult", toolCallId = "call", toolName = "inert", isError = false,
                content = new[] { new { type = "text", text = "actual tool usage" } }, timestamp = 123, usage = Usage(2) }),
            Entry("compact", "result", "compaction", new { summary = "summary", firstKeptEntryId = "absent", tokensBefore = 50, usage = Usage(3) }),
            Entry("usage", "compact", "usage", new { kind = "fixture", provider = Model.Provider, model = Model.Id, usage = Usage(4) }),
            Entry("branch", "usage", "branch_summary", new { summary = "other branch", fromId = "u", usage = Usage(5) }),
            Message("right", "u", Assistant(6))]);
        var before = await Bytes(f); var response = await f.Send(new { id = "stats", type = "get_session_stats" }); Success(response);
        var data = response.GetProperty("data");
        Check(data.EnumerateObject().Select(value => value.Name).SequenceEqual(new[] { "sessionFile", "sessionId", "userMessages",
            "assistantMessages", "toolCalls", "toolResults", "totalMessages", "tokens", "cost", "contextUsage" }), "Stats field inventory/order differs from pinned source.");
        Check(data.GetProperty("sessionFile").GetString() == f.Source && data.GetProperty("sessionId").GetString() == "source" &&
            data.GetProperty("userMessages").GetInt32() == 1 && data.GetProperty("assistantMessages").GetInt32() == 2 &&
            data.GetProperty("toolCalls").GetInt32() == 1 && data.GetProperty("toolResults").GetInt32() == 1 &&
            data.GetProperty("totalMessages").GetInt32() == 5, "Accounting lost sibling messages or counted synthesized summaries.");
        var tokens = data.GetProperty("tokens");
        Check(tokens.GetProperty("input").GetDouble() == 21 && tokens.GetProperty("output").GetDouble() == 42 &&
            tokens.GetProperty("cacheRead").GetDouble() == 63 && tokens.GetProperty("cacheWrite").GetDouble() == 84 &&
            tokens.GetProperty("total").GetDouble() == 210 && data.GetProperty("cost").GetDouble() == .75,
            "Assistant/tool/usage/summary contributions or recomputed total differ.");
        Check(data.GetProperty("contextUsage").GetProperty("tokens").GetDouble() == 999 &&
            f.Session.Snapshot.Context.Messages.Length == 3, "Context estimator did not use selected branch and provider totalTokens.");
        var after = await Bytes(f); Check(before.SequenceEqual(after) && f.Transport.Calls == 0, "Stats mutated durable state or sent.");
    }
    private static async Task Context()
    {
        var compact = Entry("compact", "a", "compaction", new { summary = "after summary", firstKeptEntryId = "absent", tokensBefore = 99 });
        await using (var f = await Fixture.Create([User(), Message("a", "u", Assistant(1)), compact]))
        {
            var response = await f.Send(new { id = "unknown", type = "get_session_stats" }); Success(response);
            var usage = response.GetProperty("data").GetProperty("contextUsage");
            Check(usage.GetProperty("tokens").ValueKind == JsonValueKind.Null && usage.GetProperty("percent").ValueKind == JsonValueKind.Null &&
                usage.GetProperty("contextWindow").GetDouble() == 32768, "Pre-compaction usage became an invented known context count.");
        }
        await using (var f = await Fixture.Create([User(), Message("a", "u", Assistant(1)), compact, Message("post", "compact", Assistant(2))]))
        {
            var response = await f.Send(new { id = "known", type = "get_session_stats" }); Success(response);
            var usage = response.GetProperty("data").GetProperty("contextUsage");
            Check(usage.GetProperty("tokens").GetDouble() == 999 && usage.GetProperty("percent").GetDouble() == 999d / 32768 * 100,
                "Post-compaction valid assistant did not reestablish context usage.");
        }
        await using (var f = await Fixture.Create(window: 0))
        {
            var response = await f.Send(new { id = "absent", type = "get_session_stats" }); Success(response);
            Check(!response.GetProperty("data").TryGetProperty("contextUsage", out _), "Undefined contextUsage serialized as null/object.");
        }
    }
    private static async Task Naming()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f); var initial = f.Session.Snapshot;
        var response = await f.Send(new { id = "name", type = "set_session_name", name = "\uFEFF  First\r\n\nSecond\uFEFF " }); Success(response);
        Check(!response.TryGetProperty("data", out _), "Name acknowledgement invented data.");
        var current = f.Session.Snapshot; var entry = current.Log.Entries[^1];
        Check(current.Log.Entries.Length == initial.Log.Entries.Length + 1 && entry.Type == "session_info" &&
            entry.ParentId == initial.Context.LeafId && current.Context.LeafId == entry.Id &&
            entry.WireBody.Value.GetProperty("name").GetString() == "First Second" && entry.Timestamp == "1970-01-01T00:00:00.123Z" &&
            current.Agent.Messages.SequenceEqual(initial.Agent.Messages), "Name checkpoint has wrong normalized payload, parent, time or model history.");
        var appended = await Bytes(f); Check(appended.Length > before.Length && appended.AsSpan(0, before.Length).SequenceEqual(before), "Naming rewrote prior bytes.");
        var lines = Encoding.UTF8.GetString(appended, before.Length, appended.Length - before.Length).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 1 && JsonData.Parse(lines[0]).Value.GetProperty("name").GetString() == "First Second", "Acknowledgement preceded exact JSONL record.");
        Success(await f.Send(new { id = "repeat", type = "set_session_name", name = "First Second" }));
        Check(f.Session.Snapshot.Log.Entries.Length == initial.Log.Entries.Length + 2, "Same name was incorrectly deduplicated.");
        var state = await f.Send(new { id = "state", type = "get_state" }); Success(state);
        Check(state.GetProperty("data").GetProperty("sessionName").GetString() == "First Second", "State did not expose acknowledged name.");
        var old = f.Owner.Current;
        Success(await f.Send(new { id = "switch", type = "switch_session", sessionPath = f.Other }));
        try { await f.Owner.SetSessionNameAsync(old, "stale"); throw new InvalidOperationException("Retired attachment renamed."); }
        catch (InvalidOperationException error) when (error.Message == "Session context is stale.") { }
        await using var reopened = await f.Open(f.Source);
        Check(reopened.Snapshot.Log.Entries.Last().WireBody.Value.GetProperty("name").GetString() == "First Second" &&
            f.Session.Snapshot.Log.Entries.All(value => value.Type != "session_info") && f.Transport.Calls == 0,
            "Name failed durable reopen or stale write leaked into replacement.");
    }
    private static async Task Rejections()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f);
        foreach (var command in new object[] { new { id = "missing", type = "set_session_name" }, new { id = "null", type = "set_session_name", name = (string?)null },
            new { id = "number", type = "set_session_name", name = 1 }, new { id = "empty", type = "set_session_name", name = " \uFEFF\r\n " },
            new { id = "large", type = "set_session_name", name = new string('x', 65_537) } })
        { var response = await f.Send(command); Check(!response.GetProperty("success").GetBoolean(), "Invalid name succeeded."); }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await f.Send(new { id = "cancel", type = "set_session_name", name = "valid" }, canceled.Token); throw new InvalidOperationException("Canceled submission was admitted."); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        try { await f.Session.SetSessionNameAsync("wrong-session", "valid"); throw new InvalidOperationException("Wrong identity named."); }
        catch (PersistentAgentSessionException error) when (error.Fault.Failure == PersistentAgentSessionFailure.StaleSession) { }
        f.Session.Steer(new("user", JsonData.Parse("""{"role":"user","content":"pending","timestamp":123}""")));
        var pending = await f.Send(new { id = "pending", type = "set_session_name", name = "valid" });
        Check(!pending.GetProperty("success").GetBoolean() && f.Session.Snapshot.Agent.SteeringCount == 1,
            "Name ignored or consumed pending input.");
        f.Session.ClearPendingInputQueues();
        var after = await Bytes(f); Check(before.SequenceEqual(after) && f.Session.Snapshot.Fault is null && f.Transport.Calls == 0, "Rejected naming changed/faulted/sent.");
        await using var limited = await Fixture.Create(options: new(MaximumOutputBytes: 256));
        var budgetBefore = await Bytes(limited); var budget = await limited.Send(new { id = "budget", type = "get_session_stats" });
        Check(!budget.GetProperty("success").GetBoolean(), "Oversize stats ignored response limit.");
        var budgetAfter = await Bytes(limited); Check(budgetBefore.SequenceEqual(budgetAfter) && limited.Session.Snapshot.Fault is null, "Stats budget changed state.");
        var unsupported = Entry("huge", null, "usage", new { kind = "fixture", provider = Model.Provider, model = Model.Id,
            usage = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0,
                cost = JsonData.Parse("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":1e309}""").Value } });
        await using var opaque = await Fixture.Create([unsupported]); var opaqueBefore = await Bytes(opaque);
        var unavailable = await opaque.Send(new { id = "unsupported", type = "get_session_stats" });
        Check(!unavailable.GetProperty("success").GetBoolean() && unavailable.GetProperty("error").GetString()!.Contains("finite native accounting", StringComparison.Ordinal),
            "Unsupported native cost fabricated partial numeric totals.");
        var opaqueAfter = await Bytes(opaque); Check(opaqueBefore.SequenceEqual(opaqueAfter) && opaque.Session.Snapshot.Fault is null && opaque.Transport.Calls == 0,
            "Unsupported accounting query mutated/faulted/sent.");
    }
    private static async Task Active()
    {
        await using var f = await Fixture.Create(); f.Transport.Hold = true;
        Success(await f.Send(new { id = "prompt", type = "prompt", message = "held" }));
        await f.Transport.Entered.Task.WaitAsync(Bound);
        var before = f.Session.Snapshot.Log;
        var stats = await f.Send(new { id = "stream-stats", type = "get_session_stats" }); Success(stats);
        var name = await f.Send(new { id = "stream-name", type = "set_session_name", name = "must wait" });
        Check(!name.GetProperty("success").GetBoolean() && f.Session.Snapshot.Log.Sequence == before.Sequence &&
            f.Session.Snapshot.Log.CommittedByteLength == before.CommittedByteLength && f.Transport.Calls == 1,
            "Streaming name mutated or stats sent an extra request.");
    }
    private static async Task Checkpoint()
    {
        await using var f = await Fixture.Create(); f.Storage.Hold = true; using var cancel = new CancellationTokenSource();
        var notifications = 0;
        f.Session.ConfigureSessionInfoObservation(observation =>
        {
            Check(observation.Name == "checkpoint" && f.Session.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("name").GetString() == "checkpoint",
                "Metadata notification preceded acknowledged publication or used the wrong name.");
            notifications++; return ValueTask.CompletedTask;
        });
        var original = f.Send(new { id = "held-name", type = "set_session_name", name = "checkpoint" }, cancel.Token); Task? disposal = null; JsonElement response = default;
        try
        {
            await f.Storage.Entered.Task.WaitAsync(Bound); cancel.Cancel();
            var state = await f.Send(new { id = "old-state", type = "get_state" }); Success(state);
            Check(!state.GetProperty("data").TryGetProperty("sessionName", out _) && !original.IsCompleted && notifications == 0,
                "Unacknowledged name leaked or cancellation abandoned original append.");
            disposal = f.Dispatcher.DisposeAsync().AsTask(); Check(!disposal.IsCompleted, "Dispatcher disposal abandoned admitted writer.");
        }
        finally { f.Storage.Release.TrySetResult(); response = await original; if (disposal is not null) await disposal; }
        Success(response);
        Check(f.Session.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("name").GetString() == "checkpoint" &&
            !f.Session.Snapshot.IsConfiguring && f.Session.Snapshot.Fault is null && notifications == 1,
            "Original checkpoint/metadata notification did not publish/join after admitted cancellation.");
    }
    private static async Task ReplacementStats()
    {
        await using var f = await Fixture.Create([User(), User("second", "u")]);
        var before = await f.Send(new { id = "before-publication", type = "get_session_stats" }); Success(before);
        Check(before.GetProperty("data").GetProperty("sessionId").GetString() == "source" &&
            before.GetProperty("data").GetProperty("sessionFile").GetString() == f.Source &&
            before.GetProperty("data").GetProperty("userMessages").GetInt32() == 2 &&
            before.GetProperty("data").GetProperty("totalMessages").GetInt32() == 2, "Initial stats did not observe the actual old attachment.");
        var original = f.Owner.AttachmentChanged ?? throw new InvalidOperationException("Actual dispatcher rebind callback is missing.");
        var published = new TaskCompletionSource<AgentSessionReplacement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Owner.AttachmentChanged = async replacement =>
        {
            published.TrySetResult(replacement); // ReplaceCore has already published Current and canceled the old lifetime.
            await release.Task;
            await original(replacement); // Preserve and directly join the real dispatcher callback.
            rebound.TrySetResult();
        };
        Task<JsonElement>? stats = null; JsonElement switched = default;
        var switching = f.Send(new { id = "gated-switch", type = "switch_session", sessionPath = f.Other });
        try
        {
            var actual = await published.Task.WaitAsync(Bound);
            Check(ReferenceEquals(f.Owner.Current, actual.Current) && actual.Current.Session.Path == f.Other &&
                actual.Previous.Session.Path == f.Source && actual.Previous.LifetimeToken.IsCancellationRequested &&
                !switching.IsCompleted && !rebound.Task.IsCompleted, "Gate did not capture published Current before actual dispatcher rebinding.");
            var checkpoint = await Bytes(f);
            stats = f.Send(new { id = "during-publication", type = "get_session_stats" });
            var during = await stats; Success(during); var data = during.GetProperty("data");
            Check(data.GetProperty("sessionId").GetString() == "other" && data.GetProperty("sessionFile").GetString() == f.Other &&
                data.GetProperty("userMessages").GetInt32() == 1 && data.GetProperty("totalMessages").GetInt32() == 1 &&
                data.GetProperty("contextUsage").GetProperty("tokens").GetDouble() == 1 &&
                !switching.IsCompleted && !rebound.Task.IsCompleted, "Stats mixed retired snapshot/path/accounting while dispatcher rebind was held.");
            var afterStats = await Bytes(f); Check(checkpoint.SequenceEqual(afterStats), "Publication-interval stats changed the new checkpoint.");
        }
        finally
        {
            release.TrySetResult();
            try { if (stats is not null) await stats; }
            finally
            {
                try { switched = await switching; }
                finally { f.Owner.AttachmentChanged = original; }
            }
        }
        Success(switched); Check(rebound.Task.IsCompletedSuccessfully, "Original replacement callback did not join.");
        var after = await f.Send(new { id = "after-rebind", type = "get_session_stats" }); Success(after);
        Check(after.GetProperty("data").GetProperty("sessionId").GetString() == "other" &&
            after.GetProperty("data").GetProperty("sessionFile").GetString() == f.Other &&
            after.GetProperty("data").GetProperty("userMessages").GetInt32() == 1 && f.Transport.Calls == 0,
            "Stats did not retain new ownership after the real callback and original switch settled.");
    }

    private static async Task<byte[]> Bytes(Fixture fixture)
    {
        var session = fixture.Session; var before = session.Snapshot;
        static bool Idle(PersistentAgentSessionSnapshot value) => !value.IsDisposed && !value.IsRetired && !value.IsAdmittingInput && !value.Agent.IsRunning && !value.IsProcessingOperation &&
            !value.IsConfiguring && !value.IsCompacting && !value.IsEditingContext && !value.IsAppendingExtensionEntry && value.Fault is null;
        Check(Idle(before) && before.Log.StorageDurability == SessionLogStorageDurability.LocalFileFlush &&
            before.Log.CommittedByteLength is >= 0 and <= 8_388_608, "Read lacks bounded acknowledged idle checkpoint.");
        await using var reader = new FileStream(session.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length == before.Log.CommittedByteLength, "Physical checkpoint length changed.");
        var bytes = new byte[checked((int)before.Log.CommittedByteLength)]; await reader.ReadExactlyAsync(bytes);
        var after = session.Snapshot;
        Check(Idle(after) && after.Log.Sequence == before.Log.Sequence && after.Log.LeafId == before.Log.LeafId &&
            after.Log.CommittedByteLength == before.Log.CommittedByteLength && reader.Length == bytes.Length && reader.Position == bytes.Length,
            "Read crossed a mutation or abandoned the owned reader."); return bytes;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root = Path.Combine(Path.GetTempPath(), "PiSharp-rpc-session-metadata-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal readonly HeldTransport Transport = new(); internal readonly StorageFactory Storage = new(); private readonly Capture output = new();
        internal SessionRuntimeRegistry Registry = null!; internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!;
        internal PersistentAgentSession Session => Owner.Current.Session; private int ids;
        internal Task<PersistentAgentSession> Open(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, Registry, () => 123,
            () => "metadata-" + Interlocked.Increment(ref ids), new(SessionLogStoreOptions: new(StorageFactory: Storage)), fallbackModel: Model);
        internal static async Task<Fixture> Create(ImmutableArray<SessionEntry> entries = default, int window = 32768, RpcDispatchOptions? options = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.Registry = new([new(Model, f.Transport)], [], new DenyPolicy());
            foreach (var path in new[] { f.Source, f.Other })
            {
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path),
                    timestamp = "2026-10-01T00:00:00.000Z", cwd = f.Root }));
                await using var store = await SessionLogStore.CreateNewAsync(path, header);
                await store.AppendAsync(path == f.Source && !entries.IsDefault ? entries : [User()]);
            }
            var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path));
            var wire = JsonData.Parse(JsonSerializer.Serialize(new { id = Model.Id, api = Model.Api, provider = Model.Provider, name = Model.Id,
                baseUrl = "https://offline.invalid", reasoning = false, input = new[] { "text" }, contextWindow = window, maxTokens = 1024,
                cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 } }));
            try { f.Dispatcher = new(session, new JsonlWriter(f.output, ownership: JsonlStreamOwnership.Borrowed), () => 123,
                [new(Model, wire)], options, RpcSessionOwnership.Borrowed, sessionOwner: f.Owner); return f; }
            catch { await f.Owner.DisposeAsync(); f.output.Dispose(); throw; }
        }
        internal async Task<JsonElement> Send(object command, CancellationToken token = default)
        {
            var raw = JsonData.Parse(JsonSerializer.Serialize(command)); await Dispatcher.SubmitAsync(raw, token);
            var id = raw.Value.GetProperty("id").GetString();
            return Encoding.UTF8.GetString(output.Bytes()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonData.Parse(line).Value)
                .Single(value => value.GetProperty("type").GetString() == "response" && value.TryGetProperty("id", out var identity) && identity.GetString() == id);
        }
        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult(); Storage.Release.TrySetResult();
            try { await Dispatcher.DisposeAsync(); } finally { await Owner.DisposeAsync(); output.Dispose(); }
            if (Transport.Entered.Task.IsCompletedSuccessfully) Check(Transport.Joined.Task.IsCompletedSuccessfully, "Original provider enumerator did not join.");
            // Keep small authored durable fixtures for coordinator review; no recursive cleanup or detached task.
        }
    }
    private sealed class Capture : MemoryStream
    {
        private readonly object gate = new(); internal byte[] Bytes() { lock (gate) return ToArray(); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); lock (gate) Write(buffer.Span); return ValueTask.CompletedTask; }
    }
    private sealed class HeldTransport : IChatTransport
    {
        internal bool Hold; internal int Calls; internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously), Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Calls++; var message = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [], TokenUsage.Zero, StopReason.Stop);
            try
            {
                yield return new StreamStarted(message with { StopReason = StopReason.Pending }); Entered.TrySetResult();
                if (Hold) await Release.Task.WaitAsync(token); yield return new StreamDone(StopReason.Stop, message);
            }
            finally { Joined.TrySetResult(); }
        }
    }
    private sealed class DenyPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        internal bool Hold; internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this);
        private sealed class Storage(ISessionLogStorage inner, StorageFactory owner) : ISessionLogStorage
        {
            public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
            public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
            public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
            public async ValueTask BeforeCheckpointAsync() { if (owner.Hold) { owner.Entered.TrySetResult(); await owner.Release.Task; } await inner.BeforeCheckpointAsync(); }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
