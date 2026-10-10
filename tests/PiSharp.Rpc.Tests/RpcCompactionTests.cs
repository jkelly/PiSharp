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

internal static class RpcCompactionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("rpc-summary", "openai-responses", "fixture");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"rpc-summary","api":"openai-responses","provider":"fixture","name":"Offline summary","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("rpc.compaction actual durable commands strict grammar captured generation and auto configuration", DurableGrammar),
        ("rpc.compaction prospective response budget rejects before append and writer remains usable", ResponseBound),
        ("rpc.compaction held generator state and Abort join actual cleanup before returning", GeneratorAbort),
        ("rpc.compaction admitted durable checkpoint wins late Abort and state remains responsive", CheckpointAbort),
        ("rpc.compaction EOF cancels held generator and joins borrowed owner without writes", EofJoin),
        ("rpc.errors a command that throws answers the thrown error's message, not a fixed text", FailureMessage)
    ];
    private static async Task DurableGrammar()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f.Source);
        foreach (var command in new object[]
        {
            new { id = "missing-gen", type = "pisharp_compact", firstKeptEntryId = (string?)null },
            new { id = "stale", type = "pisharp_compact", generation = 2, firstKeptEntryId = (string?)null },
            new { id = "number", type = "pisharp_compact", generation = 1, reserveTokens = -1 },
            new { id = "flag", type = "pisharp_set_auto_compaction", generation = 1, enabled = "true" },
            new { id = "target", type = "pisharp_branch_summary", generation = 1 }
        }) { await f.Send(command); Check(!f.Records[^1].Value.GetProperty("success").GetBoolean(), "Invalid native summary RPC succeeded."); }
        Check(f.Summary.Calls == 0 && (await Bytes(f.Source)).SequenceEqual(before), "Grammar/generation rejection consumed provider or wrote bytes.");
        await f.Send(new { id = "auto-on", type = "pisharp_set_auto_compaction", generation = 1, enabled = true });
        await f.Send(new { id = "state-on", type = "get_state" }); Check(f.Response("state-on").Value.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean(), "Native auto state did not publish.");
        await f.Send(new { id = "auto-off", type = "pisharp_set_auto_compaction", generation = 1, enabled = false });
        await f.Send(new { id = "compact", type = "pisharp_compact", generation = 1, firstKeptEntryId = (string?)null });
        Check(f.Response("compact").Value.GetProperty("success").GetBoolean() && f.Response("compact").Value.GetProperty("data").GetProperty("checkpointAcknowledged").GetBoolean() &&
            f.Owner.Current.Session.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("summary").GetString() == "native summary" && f.Summary.Calls == 1, "Actual RPC compact did not append acknowledged summary.");
        var old = f.Owner.Current; await f.Owner.SwitchAsync(old, new(f.Other)); var priorCalls = f.Summary.Calls;
        await f.Send(new { id = "retired", type = "pisharp_compact", generation = 1, firstKeptEntryId = (string?)null });
        Check(!f.Response("retired").Value.GetProperty("success").GetBoolean() && f.Summary.Calls == priorCalls && !f.Owner.Current.Session.Snapshot.AutoCompactionEnabled, "Replacement retained summary authority or automatic settings.");
        await f.Send(new { id = "branch", type = "pisharp_branch_summary", generation = 2, targetId = (string?)null });
        Check(f.Response("branch").Value.GetProperty("success").GetBoolean() && f.Owner.Current.Session.Snapshot.Log.Entries[^1].Kind == SessionEntryKind.BranchSummary, "Fresh captured generation could not summarize branch.");
    }
    private static async Task ResponseBound()
    {
        await using var f = await Fixture.Create(new(MaximumOutputBytes: 512), new string('e', 220)); var before = await Bytes(f.Source);
        await f.Send(new { id = "bound", type = "pisharp_compact", generation = 1, firstKeptEntryId = (string?)null });
        Check(!f.Response("bound").Value.GetProperty("success").GetBoolean() && (await Bytes(f.Source)).SequenceEqual(before) && f.Owner.Current.Session.Snapshot.Fault is null,
            "Prospective summary response failed after effects or poisoned writer.");
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "continued", 1, JsonData.EmptyObject));
    }
    private static async Task GeneratorAbort()
    {
        await using var f = await Fixture.Create(); f.Summary.Hold = true; var before = await Bytes(f.Source);
        var summary = f.Send(new { id = "held", type = "pisharp_compact", generation = 1, firstKeptEntryId = (string?)null }); Task? abort = null;
        try
        {
            await f.Summary.Entered.Task.WaitAsync(Bound); await f.Send(new { id = "state", type = "get_state" });
            Check(f.Response("state").Value.GetProperty("data").GetProperty("isCompacting").GetBoolean(), "Held summary state did not remain responsive.");
            abort = f.Send(new { id = "abort", type = "abort" }); await f.Summary.Canceled.Task.WaitAsync(Bound);
            Check(!summary.IsCompleted && !abort.IsCompleted && (await Bytes(f.Source)).SequenceEqual(before), "Abort detached summary cleanup or appended before release.");
            f.Summary.Release.TrySetResult(); await Task.WhenAll(summary, abort).WaitAsync(Bound);
            Check(!f.Response("held").Value.GetProperty("success").GetBoolean() && f.Response("abort").Value.GetProperty("success").GetBoolean() &&
                !f.Owner.Current.Session.Snapshot.IsCompacting && f.Summary.Active == 0 && (await Bytes(f.Source)).SequenceEqual(before), "Abort did not settle actual generator without writes.");
            f.Summary.Hold = false; await f.Send(new { id = "fresh", type = "pisharp_compact", generation = 1, firstKeptEntryId = (string?)null }); Check(f.Response("fresh").Value.GetProperty("success").GetBoolean(), "Joined canceled summary left writer unusable.");
        }
        finally { f.Summary.Release.TrySetResult(); await summary; if (abort is not null) await abort; }
    }
    private static async Task CheckpointAbort()
    {
        await using var f = await Fixture.Create(); f.Storage.Hold = true;
        var summary = f.Send(new { id = "checkpoint", type = "pisharp_compact", generation = 1, firstKeptEntryId = (string?)null }); Task? abort = null;
        try
        {
            await f.Storage.Entered.Task.WaitAsync(Bound); await f.Send(new { id = "state", type = "get_state" });
            Check(f.Response("state").Value.GetProperty("data").GetProperty("isCompacting").GetBoolean(), "Checkpoint state became unavailable.");
            abort = f.Send(new { id = "abort", type = "abort" }); Check(!abort.IsCompleted, "Late Abort abandoned admitted durable checkpoint.");
            f.Storage.Release.TrySetResult(); await Task.WhenAll(summary, abort).WaitAsync(Bound);
            Check(f.Response("checkpoint").Value.GetProperty("success").GetBoolean() && f.Response("abort").Value.GetProperty("success").GetBoolean() &&
                f.Owner.Current.Session.Snapshot.Log.Entries.Length == 2 && !f.Owner.Current.Session.Snapshot.IsCompacting, "Actual admitted summary checkpoint was denied or duplicated.");
        }
        finally { f.Storage.Release.TrySetResult(); await summary; if (abort is not null) await abort; }
    }
    private static async Task EofJoin()
    {
        await using var f = await Fixture.Create(); f.Summary.Hold = true; var before = await Bytes(f.Source);
        var bytes = Encoding.UTF8.GetBytes("{\"id\":\"eof\",\"type\":\"pisharp_compact\",\"generation\":1,\"firstKeptEntryId\":null}\n{\"id\":\"state\",\"type\":\"get_state\"}\n");
        await using var reader = new JsonlReader(new MemoryStream(bytes), ownership: JsonlStreamOwnership.Owned);
        var run = f.Dispatcher.RunAsync(reader);
        try
        {
            await f.Summary.Entered.Task.WaitAsync(Bound); await f.Summary.Canceled.Task.WaitAsync(Bound); Check(!run.IsCompleted, "EOF detached actual summary cleanup.");
            f.Summary.Release.TrySetResult(); await run.WaitAsync(Bound);
            Check(f.Summary.Active == 0 && (await Bytes(f.Source)).SequenceEqual(before) && !f.Owner.Current.Session.Snapshot.IsCompacting && f.Owner.Current.Session.Snapshot.Fault is null, "EOF did not join held summary without effects.");
            await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "after-eof", 1, JsonData.EmptyObject));
        }
        finally { f.Summary.Release.TrySetResult(); await run; }
    }
    // rpc-mode.ts handleInputLine: a command that throws answers error(id, command.type, commandError.message). An error PiSharp has
    // no specific mapping for (here a user bash executor that cannot start its shell) travels as its message; it was the fixed
    // "RPC command failed." (issue #5). The message never carries the .NET stack.
    private static async Task FailureMessage()
    {
        await using var f = await Fixture.Create(userBash: new FailingBash());
        await f.Send(new { id = "bash", type = "bash", command = "echo hi" });
        var response = f.Response("bash").Value;
        Check(!response.GetProperty("success").GetBoolean() && response.GetProperty("error").GetString() == "spawn /missing/bash ENOENT",
            "A failing command did not answer the thrown error's message: " + response.GetRawText());
        await f.Send(new { id = "after", type = "get_state" });
        Check(f.Response("after").Value.GetProperty("success").GetBoolean() && f.Owner.Current.Session.Snapshot.Fault is null, "The failure poisoned the session.");
    }
    private sealed class FailingBash : PiSharp.CodingAgent.Execution.IUserBashExecutor
    {
        public Task<PiSharp.CodingAgent.Execution.UserBashResult> ExecuteAsync(PiSharp.CodingAgent.Execution.UserBashExecutionRequest request,
            PiSharp.CodingAgent.Execution.UserBashProgress progress, CancellationToken cancellationToken) =>
            throw new IOException("spawn /missing/bash ENOENT");
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<byte[]> Bytes(string path) { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var bytes = new MemoryStream(); await file.CopyToAsync(bytes); return bytes.ToArray(); }
    private sealed class Summary : ISessionSummaryGenerator
    {
        internal bool Hold; internal int Calls, Active; internal readonly TaskCompletionSource Entered = Gate(), Canceled = Gate(), Release = Gate();
        public async ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        {
            Calls++; Active++; try { Check(request.Kind is SessionSummaryKind.History or SessionSummaryKind.Branch, "Unexpected summary request kind."); if (Hold) { using var canceled = token.UnsafeRegister(_ => Canceled.TrySetResult(), null); Entered.TrySetResult(); await Release.Task; } token.ThrowIfCancellationRequested(); return new("native summary", new(4, 3, 0, 0, 7, new(0, 0, 0, 0, 0))); }
            finally { Active--; }
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-rpc-summary-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!; internal readonly Summary Summary = new(); internal readonly StorageFactory Storage = new();
        private readonly MemoryStream output = new(); private SessionRuntimeRegistry runtime = null!; private string? generated; private int ids;
        internal JsonData[] Records => Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
        internal JsonData Response(string id) => Records.Single(record => record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        internal Task Send(object command) => Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(command)));
        private Task<PersistentAgentSession> Open(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0, () => generated ?? "summary-" + Interlocked.Increment(ref ids), new(SessionLogStoreOptions: new(StorageFactory: Storage)), fallbackModel: Model);
        internal static async Task<Fixture> Create(RpcDispatchOptions? options = null, string? generatedId = null,
            PiSharp.CodingAgent.Execution.IUserBashExecutor? userBash = null)
        {
            var f = new Fixture { generated = generatedId }; Directory.CreateDirectory(f.Root); f.runtime = new([new(Model, new NoTransport())], [], new NoPolicy()); var codec = new SessionEntryCodec();
            foreach (var path in new[] { f.Source, f.Other })
            {
                var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-02T00:00:00.000Z", cwd = f.Root }));
                var user = codec.Parse("""{"type":"message","id":"u","parentId":null,"timestamp":"2026-10-02T00:00:00.000Z","message":{"role":"user","content":"original input","timestamp":0}}""");
                await using var store = await SessionLogStore.CreateNewAsync(path, header); await store.AppendAsync([user]);
            }
            var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path));
            f.Dispatcher = new(session, new JsonlWriter(f.output, ownership: JsonlStreamOwnership.Borrowed), () => 0, [new(Model, ModelWire)], options,
                RpcSessionOwnership.Borrowed, sessionOwner: f.Owner, summaryGenerator: f.Summary, userBash: userBash); return f;
        }
        public async ValueTask DisposeAsync()
        {
            Storage.Release.TrySetResult(); Summary.Release.TrySetResult(); await Dispatcher.DisposeAsync(); await Owner.DisposeAsync(); output.Dispose(); Check(Summary.Active == 0, "Summary fixture leaked generator.");
            var root = Path.GetFullPath(Root); Check(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) && Path.GetFileName(root).StartsWith("pisharp-rpc-summary-", StringComparison.Ordinal) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unowned summary RPC cleanup root.");
            foreach (var path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root, false);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        internal bool Hold; internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
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
    private sealed class NoTransport : IChatTransport
    { public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default) { await Task.FromException(new InvalidOperationException("Summary invoked agent/tool transport.")); yield break; } }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Summary invoked tool policy."); }
}
