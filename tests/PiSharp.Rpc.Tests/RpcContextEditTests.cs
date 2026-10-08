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
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class RpcContextEditTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ModelDescriptor Model = new("rpc-edit", "openai-responses", "fixture");
    private static readonly JsonData ModelWire = JsonData.Parse("""{"id":"rpc-edit","api":"openai-responses","provider":"fixture","name":"Offline editor","baseUrl":"https://offline.invalid","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":1024,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("rpc.context-edit exact durable response and strict required null generation grammar preserve raw history", AppendAndGrammar),
        ("rpc.context-edit staged response limit rejects before effects and source continues", ResponseBound),
        ("rpc.context-edit state and abort remain responsive while acknowledged writer checkpoint joins", CheckpointAbort),
        ("rpc.context-edit accepted old attachment cannot write after held real switch commits", StaleSwitch),
        ("rpc.context-edit EOF cancels pending edit joins actual manager and borrowed source continues", EofJoin)
    ];
    private static async Task AppendAndGrammar()
    {
        await using var f = await Fixture.Create(); var original = await Bytes(f.Source);
        foreach (var command in new object[] {
            new { id = "missing", type = "pisharp_context_edit", generation = 1, targetId = "u" },
            new { id = "generation", type = "pisharp_context_edit", targetId = "u", replacement = (object?)null },
            new { id = "wrong", type = "pisharp_context_edit", generation = 2, targetId = "u", replacement = (object?)null },
            new { id = "shape", type = "pisharp_context_edit", generation = 1, targetId = "u", replacement = new { prose = "invalid" } } })
        {
            await f.Send(command); Check(!f.Records[^1].Value.GetProperty("success").GetBoolean(), "Invalid editor command succeeded.");
            Check((await Bytes(f.Source)).SequenceEqual(original), "RPC admission failure changed bytes.");
        }
        await f.Send(new { id = "replace", type = "pisharp_context_edit", generation = 1, targetId = "u", replacement = new { content = "changed", extra = new { exact = true } } });
        var response = f.Response("replace").Value;
        Check(response.GetProperty("success").GetBoolean() && response.GetProperty("data").GetProperty("checkpointAcknowledged").GetBoolean(), "Actual editor acknowledgment missing.");
        await f.Send(new { id = "read", type = "get_messages" });
        Check(f.Response("read").Value.GetProperty("data").GetProperty("messages")[0].GetProperty("content").GetString() == "changed", "RPC model history was not refreshed.");
        await f.Send(new { id = "omit", type = "pisharp_context_edit", generation = 1, targetId = "u", replacement = (object?)null });
        Check(f.Response("omit").Value.GetProperty("success").GetBoolean() && f.Owner.Current.Session.Snapshot.Context.LlmMessages.IsEmpty, "Explicit null did not omit contribution.");
        var actual = await Bytes(f.Source); Check(actual.AsSpan(0, original.Length).SequenceEqual(original), "Editor rewrote original records.");
        await f.Dispatcher.DisposeAsync(); await f.Owner.DisposeAsync();
        await using var reopened = await f.Open(f.Source);
        Check(reopened.Snapshot.Log.Entries.Length == 3 && reopened.Snapshot.Context.LlmMessages.IsEmpty, "Durable RPC edits lost on reopen.");
    }
    private static async Task ResponseBound()
    {
        await using var f = await Fixture.Create(new(MaximumOutputBytes: 512), new string('e', 220)); var before = await Bytes(f.Source);
        await f.Send(new { id = "budget", type = "pisharp_context_edit", generation = 1, targetId = "u", replacement = new { content = "valid" } });
        var response = f.Response("budget").Value;
        Check(!response.GetProperty("success").GetBoolean() && response.GetProperty("error").GetString()!.Contains("limits", StringComparison.Ordinal) &&
            (await Bytes(f.Source)).SequenceEqual(before) && f.Owner.Current.Session.Snapshot.Fault is null, "Response budget failed after writing or poisoned source.");
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "continued", 1, JsonData.EmptyObject));
    }
    private static async Task CheckpointAbort()
    {
        await using var f = await Fixture.Create(); f.Storage.Hold = true;
        var edit = f.Send(new { id = "edit", type = "pisharp_context_edit", generation = 1, targetId = "u", replacement = new { content = "after checkpoint" } });
        Task? abort = null;
        try
        {
            await f.Storage.Entered.Task.WaitAsync(Bound);
            await f.Send(new { id = "state", type = "get_state" });
            Check(f.Response("state").Value.GetProperty("data").GetProperty("pisharpEditingContext").GetBoolean(), "State did not observe held editor.");
            abort = f.Send(new { id = "abort", type = "abort" }); Check(!abort.IsCompleted, "Abort abandoned an admitted checkpoint.");
            f.Storage.Release.TrySetResult(); await Task.WhenAll(edit, abort).WaitAsync(Bound);
            Check(f.Response("edit").Value.GetProperty("success").GetBoolean() && f.Response("abort").Value.GetProperty("success").GetBoolean(), "Late abort hid an actual checkpoint.");
            Check(!f.Owner.Current.Session.Snapshot.IsEditingContext && f.Owner.Current.Session.Snapshot.Log.Entries.Length == 2, "Held checkpoint did not settle exactly one editor.");
        }
        finally { f.Storage.Release.TrySetResult(); await edit; if (abort is not null) await abort; }
    }
    private static async Task StaleSwitch()
    {
        await using var f = await Fixture.Create(); var before = await Bytes(f.Source); var entered = Gate(); var release = Gate(); var previous = f.Owner.Current;
        var switched = f.Owner.SwitchAsync(previous, new(f.Other), async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return true; });
        Task? edit = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            edit = f.Send(new { id = "stale", type = "pisharp_context_edit", generation = 1, targetId = "u", replacement = (object?)null });
            Check(!edit.IsCompleted, "Old accepted editor did not wait behind actual host switch.");
            release.TrySetResult(); await switched.WaitAsync(Bound); await edit.WaitAsync(Bound);
            Check(!f.Response("stale").Value.GetProperty("success").GetBoolean() && f.Owner.Current.Generation == 2 &&
                (await Bytes(f.Source)).SequenceEqual(before) && f.Owner.Current.Session.Snapshot.Log.Entries.Length == 1,
                "Old accepted attachment crossed committed generation.");
            await f.Send(new { id = "fresh", type = "pisharp_context_edit", generation = 2, targetId = "u", replacement = (object?)null });
            Check(f.Response("fresh").Value.GetProperty("success").GetBoolean(), "Fresh RPC generation could not edit.");
        }
        finally { release.TrySetResult(); await switched; if (edit is not null) await edit; }
    }
    private static async Task EofJoin()
    {
        await using var f = await Fixture.Create(); var entered = Gate(); var release = Gate(); var before = await Bytes(f.Source);
        var held = f.Owner.AppendContextEditAsync(f.Owner.Current, new("u", JsonData.Null), preflight: async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        try
        {
            await entered.Task.WaitAsync(Bound);
            var bytes = Encoding.UTF8.GetBytes("{\"id\":\"eof-edit\",\"type\":\"pisharp_context_edit\",\"generation\":1,\"targetId\":\"u\",\"replacement\":null}\n{\"id\":\"state\",\"type\":\"get_state\"}\n");
            await using var input = new JsonlReader(new MemoryStream(bytes), ownership: JsonlStreamOwnership.Owned);
            await f.Dispatcher.RunAsync(input).WaitAsync(Bound); await Throws<OperationCanceledException>(() => held);
            Check((await Bytes(f.Source)).SequenceEqual(before) && f.Owner.Current.Session.Snapshot.Fault is null &&
                !f.Owner.Current.Session.Snapshot.IsEditingContext, "EOF failed to cancel/join pending editor without effects.");
            await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "after-eof", 1, JsonData.EmptyObject));
        }
        finally { release.TrySetResult(); try { await held; } catch (OperationCanceledException) { } }
    }
    private static async Task<byte[]> Bytes(string path)
    { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var bytes = new MemoryStream(); await file.CopyToAsync(bytes); return bytes.ToArray(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-rpc-context-edit-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal RpcSessionDispatcher Dispatcher = null!; internal StorageFactory Storage = new();
        private readonly MemoryStream output = new(); private SessionRuntimeRegistry runtime = null!; private string? generated; private int ids;
        internal JsonData[] Records => Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JsonData.Parse).ToArray();
        internal JsonData Response(string id) => Records.Single(record => record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        internal Task Send(object value) => Dispatcher.SubmitAsync(JsonData.Parse(JsonSerializer.Serialize(value)));
        internal Task<PersistentAgentSession> Open(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0,
            () => generated ?? "edit-" + Interlocked.Increment(ref ids), new(SessionLogStoreOptions: new(StorageFactory: Storage)), fallbackModel: Model);
        internal static async Task<Fixture> Create(RpcDispatchOptions? options = null, string? generatedId = null)
        {
            var f = new Fixture { generated = generatedId }; Directory.CreateDirectory(f.Root); f.runtime = new([new(Model, new NoTransport())], [], new NoPolicy());
            foreach (var path in new[] { f.Source, f.Other })
            {
                var codec = new SessionEntryCodec();
                var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2026-10-02T00:00:00.000Z", cwd = f.Root }));
                var user = codec.Parse("""{"type":"message","id":"u","parentId":null,"timestamp":"2026-10-02T00:00:00.000Z","message":{"role":"user","content":"original","timestamp":0}}""");
                await using var store = await SessionLogStore.CreateNewAsync(path, header); await store.AppendAsync([user]);
            }
            var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path));
            f.Dispatcher = new(session, new JsonlWriter(f.output, ownership: JsonlStreamOwnership.Borrowed), () => 0,
                [new(Model, ModelWire)], options, RpcSessionOwnership.Borrowed, sessionOwner: f.Owner); return f;
        }
        public async ValueTask DisposeAsync()
        {
            Storage.Release.TrySetResult(); await Dispatcher.DisposeAsync(); await Owner.DisposeAsync(); output.Dispose();
            var root = Path.GetFullPath(Root); Check(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("pisharp-rpc-context-edit-", StringComparison.Ordinal) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unowned RPC fixture cleanup root.");
            foreach (var path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root, false);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        internal bool Hold; internal readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this);
        private sealed class Storage(ISessionLogStorage inner, StorageFactory owner) : ISessionLogStorage
        {
            public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
            public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength);
            public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes); public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
            public async ValueTask BeforeCheckpointAsync() { if (owner.Hold) { owner.Entered.TrySetResult(); await owner.Release.Task; } await inner.BeforeCheckpointAsync(); }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
    private sealed class NoTransport : IChatTransport
    { public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default) { await Task.FromException(new InvalidOperationException("Editor invoked a provider.")); yield break; } }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Editor invoked a tool."); }
}
