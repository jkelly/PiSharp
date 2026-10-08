using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionCatalogResumeTests
{
    private static readonly ModelDescriptor Model = new("catalog", "openai-responses", "fixture");
    private static readonly ModelDescriptor OtherModel = new("catalog-other", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const string Time = "2026-10-02T00:00:00.000Z";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-catalog-resume same IDs in explicit stores restore selected ancestry and fresh physical authority", SelectedBranch),
        ("session-catalog-resume root and return attachments preserve original branch state across generations", RootAndReturn),
        ("session-catalog-resume listing a header never bypasses full log graph and runtime validation", FullValidation),
        ("session-catalog-resume changed opened header leaves both existing files and source authority intact", HeaderRace),
        ("session-catalog-resume veto and canceled target close join without deleting imported target", VetoAndCanceledClose),
        ("session-catalog-resume abort and disposal join actual held discovery reader cleanup", DiscoveryShutdown),
        ("session-catalog-resume lifecycle callbacks can list with fresh read-only attachment authority", CallbackListing),
        ("session-catalog-resume actual header cwd chooses explicit borrowed bindings before publication", WorkingDirectoryBindings)
    ];
    private static async Task SelectedBranch()
    {
        await using var f = await Fixture.Open(); var old = f.Owner.Current; var original = await Bytes(f.A);
        var page = await f.Owner.ListSessionsAsync(old, new()); Equal(2, page.Items.Length);
        Check(page.Items.All(item => item.SessionId == "same") && page.Items[0].Key != page.Items[1].Key, "Same session ID aliased stores.");
        var result = await f.Owner.ResumeAsync(old, new(f.Key(page), false, "left"));
        Equal("resume", result!.Reason); Equal(2L, result.Current.Generation); Equal(f.B, result.Current.Session.Path);
        Equal("left", result.Current.Session.Snapshot.Context.LeafId); Equal("right", result.Current.Session.Snapshot.Log.LeafId);
        Check(States(result.Current.Session).SequenceEqual(new[] { "base", "left" }), "Selected state leaked the physical sibling.");
        Check(result.Current.Session.Snapshot.Context.LlmMessages.IsEmpty, "State-only records leaked into model input.");
        await Fails<InvalidOperationException>(() => f.Owner.AppendExtensionEntryAsync(old, new("fixture", "state", 1, JsonData.EmptyObject)));
        await f.Owner.AppendExtensionEntryAsync(result.Current, new("fixture", "state", 1, JsonData.Parse("{\"value\":\"fresh\"}")));
        Check((await Bytes(f.A)).SequenceEqual(original), "Resume changed old source bytes.");
        await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(f.B);
        Check(States(reopened).SequenceEqual(new[] { "base", "left", "fresh" }), "Independent reopen lost fresh branch state.");
        Equal(0, f.Transport.Calls);
    }
    private static async Task RootAndReturn()
    {
        await using var f = await Fixture.Open(); var page = await f.Owner.ListSessionsAsync(f.Owner.Current, new());
        var old = f.Owner.Current; await f.Owner.ResumeAsync(old, new(f.Key(page), false, null));
        Check(f.Owner.Current.Session.Snapshot.Context.Ancestry.IsEmpty, "Explicit root selected the physical tail.");
        var next = await f.Owner.ListSessionsAsync(f.Owner.Current, new()); var sourceKey = next.Items.Single(item => item.Path == f.A).Key;
        await f.Owner.ResumeAsync(f.Owner.Current, new(sourceKey)); Equal(3L, f.Owner.Current.Generation); Equal(f.A, f.Owner.Current.Session.Path);
        await Fails<InvalidOperationException>(() => f.Owner.ListSessionsAsync(old, new()));
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "returned", 1, JsonData.EmptyObject));
        await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(f.A);
        Equal("returned", reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("entryKind").GetString());
    }
    private static async Task FullValidation()
    {
        foreach (var corruption in new[] { "tail", "parent", "duplicate", "model" })
        {
            await using var f = await Fixture.Open(); var header = Header(f.Root);
            var content = corruption switch
            {
                "tail" => header + "\n{broken",
                "parent" => header + "\n" + Record("bad", "missing", "future", new { payload = 0 }).WireBody + "\n",
                "duplicate" => header + "\n" + Record("dup", null, "future", new { }).WireBody + "\n" + Record("dup", "dup", "future", new { }).WireBody + "\n",
                _ => header + "\n" + Record("model", null, "model_change", new { provider = "absent", modelId = "absent" }).WireBody + "\n"
            };
            await File.WriteAllTextAsync(f.B, content); var target = await Bytes(f.B); var source = await Bytes(f.A); var old = f.Owner.Current;
            var page = await f.Owner.ListSessionsAsync(old, new()); Equal(2, page.Items.Length);
            await Fails<Exception>(() => f.Owner.ResumeAsync(old, new(f.Key(page))));
            Check(ReferenceEquals(old, f.Owner.Current) && !old.Session.Snapshot.IsRetired, "Header listing admitted an invalid target.");
            Check((await Bytes(f.A)).SequenceEqual(source) && (await Bytes(f.B)).SequenceEqual(target), "Rejected resume changed an existing file.");
            await f.Owner.AppendExtensionEntryAsync(old, new("fixture", "after-invalid", 1, JsonData.EmptyObject));
        }
    }
    private static async Task HeaderRace()
    {
        await using var f = await Fixture.Open(); var page = await f.Owner.ListSessionsAsync(f.Owner.Current, new()); var key = f.Key(page);
        var source = await Bytes(f.A); var old = f.Owner.Current;
        f.Readers.AfterTargetClose = () =>
        {
            var bytes = File.ReadAllText(f.B); var line = bytes.IndexOf('\n');
            File.WriteAllText(f.B, Header(f.Root).Replace("\"version\":3", "\"version\":3,\"revision\":\"changed\"") + bytes[line..]);
        };
        var error = await Fails<SessionCatalogException>(() => f.Owner.ResumeAsync(old, new(key)));
        Equal(SessionCatalogFailure.SessionChanged, error.Failure);
        Check(ReferenceEquals(old, f.Owner.Current) && !old.Session.Snapshot.IsRetired && (await Bytes(f.A)).SequenceEqual(source), "Header race retired source.");
        using (var exclusive = new FileStream(f.B, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        await f.Owner.AppendExtensionEntryAsync(old, new("fixture", "after-change", 1, JsonData.EmptyObject));
    }
    private static async Task VetoAndCanceledClose()
    {
        await using var f = await Fixture.Open(); var page = await f.Owner.ListSessionsAsync(f.Owner.Current, new()); var key = f.Key(page);
        var old = f.Owner.Current; var source = await Bytes(f.A); var target = await Bytes(f.B);
        f.Owner.BeforeReplacement = (_, _, _) => ValueTask.FromResult(false);
        Check(await f.Owner.ResumeAsync(old, new(key)) is null, "Vetoed resume committed.");
        using var cancellation = new CancellationTokenSource(); var entered = Gate(); var release = Gate();
        f.Storage.ArmTargetClose();
        f.Owner.BeforeReplacement = async (_, staged, _) =>
        {
            entered.TrySetResult();
            await Fails<InvalidOperationException>(() => staged.AppendExtensionEntryAsync("same", new("fixture", "busy", 1, JsonData.EmptyObject)));
            await release.Task; return true;
        };
        var resume = f.Owner.ResumeAsync(old, new(key), cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Bound); cancellation.Cancel(); release.TrySetResult();
            await f.Storage.TargetCloseEntered.Task.WaitAsync(Bound); Check(!resume.IsCompleted, "Canceled resume escaped target close.");
            Check(ReferenceEquals(old, f.Owner.Current) && !old.Session.Snapshot.IsRetired, "Canceled target crossed retirement.");
            f.Storage.ReleaseTargetClose.TrySetResult(); await Fails<OperationCanceledException>(() => resume.WaitAsync(Bound));
            using (var exclusive = new FileStream(f.B, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Check((await Bytes(f.A)).SequenceEqual(source) && (await Bytes(f.B)).SequenceEqual(target), "Veto/cancellation rewrote or deleted imports.");
            await f.Owner.AppendExtensionEntryAsync(old, new("fixture", "after-cancel", 1, JsonData.EmptyObject));
        }
        finally { release.TrySetResult(); f.Storage.ReleaseTargetClose.TrySetResult(); try { await resume; } catch (Exception) { } }
    }
    private static async Task DiscoveryShutdown()
    {
        foreach (var dispose in new[] { false, true })
        {
            await using var f = await Fixture.Open(); var old = f.Owner.Current; f.Readers.HoldTargetRead = true; f.Readers.HoldTargetClose = true;
            var listing = f.Owner.ListSessionsAsync(old, new()); await f.Readers.TargetReadEntered.Task.WaitAsync(Bound);
            Task? closing = dispose ? f.Owner.DisposeAsync().AsTask() : null; if (!dispose) f.Owner.Abort();
            try
            {
                await f.Readers.TargetCloseEntered.Task.WaitAsync(Bound);
                Check(!listing.IsCompleted && (closing is null || !closing.IsCompleted), "Shutdown escaped held discovery cleanup.");
                f.Readers.ReleaseTargetClose.TrySetResult(); await Fails<OperationCanceledException>(() => listing.WaitAsync(Bound));
                if (closing is not null) await closing.WaitAsync(Bound);
                else await f.Owner.AppendExtensionEntryAsync(old, new("fixture", "after-abort", 1, JsonData.EmptyObject));
                Check(f.Readers.TargetClosed, "Discovery retained its physical reader.");
                using (var exclusive = new FileStream(f.B, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            finally { f.Readers.ReleaseTargetClose.TrySetResult(); try { await listing; } catch (Exception) { } if (closing is not null) await closing; }
        }
    }
    private static async Task CallbackListing()
    {
        await using var f = await Fixture.Open(); var initial = f.Owner.Current; var page = await f.Owner.ListSessionsAsync(initial, new()); var before = 0; var after = 0;
        f.Owner.BeforeReplacement = async (old, _, token) => { Equal(2, (await f.Owner.ListSessionsAsync(old, new(), token)).Items.Length); before++; return true; };
        f.Owner.AfterReplacement = async replacement =>
        {
            Equal("resume", replacement.Reason); Equal(2, (await f.Owner.ListSessionsAsync(replacement.Current, new())).Items.Length);
            await Fails<InvalidOperationException>(() => f.Owner.ListSessionsAsync(replacement.Previous, new())); after++;
        };
        await f.Owner.ResumeAsync(initial, new(f.Key(page))); Equal(1, before); Equal(1, after);
    }
    private static async Task WorkingDirectoryBindings()
    {
        await using var f = await Fixture.Open(rebind: true); var page = await f.Owner.ListSessionsAsync(f.Owner.Current, new());
        await f.Owner.ResumeAsync(f.Owner.Current, new(f.Key(page), false, "left"));
        Equal(f.OtherCwd, f.Owner.Current.Session.WorkingDirectory); Equal(OtherModel, f.Owner.Current.Session.Snapshot.Agent.Model);
        Check(f.FactoryCwds.SequenceEqual(new[] { f.OtherCwd }), "Bindings used catalog metadata or inherited source cwd.");
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("fixture", "rebound", 1, JsonData.EmptyObject));
        Equal(0, f.Transport.Calls);
        await using var denied = await Fixture.Open(foreignCwd: true); var old = denied.Owner.Current;
        var deniedPage = await denied.Owner.ListSessionsAsync(old, new()); await Fails<Exception>(() => denied.Owner.ResumeAsync(old, new(denied.Key(deniedPage))));
        Check(ReferenceEquals(old, denied.Owner.Current) && !old.Session.Snapshot.IsRetired, "Implicit host expanded cwd bindings.");
    }
    private static string Header(string cwd) => JsonSerializer.Serialize(new { type = "session", version = 3, id = "same", timestamp = Time, cwd });
    private static SessionEntry Record(string id, string? parent, string type, object data)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(data))!.AsObject();
        body["type"] = type; body["id"] = id; body["parentId"] = parent; body["timestamp"] = Time; return Codec.Parse(body.ToJsonString());
    }
    private static SessionEntry State(string id, string? parent, string value) => Record(id, parent, "custom", new
        { customType = "pisharp.extension-state", data = new { extensionId = "fixture", entryKind = "state", schemaVersion = 1, data = new { value } } });
    private static IEnumerable<string> States(PersistentAgentSession session) => session.Snapshot.Context.Ancestry.Where(entry => entry.Type == "custom")
        .Select(entry => entry.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("value").GetString()!);
    private static async Task<byte[]> Bytes(string path)
    { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var copy = new MemoryStream(); await file.CopyToAsync(copy); return copy.ToArray(); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Fails<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-catalog-resume-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(Root, "a", "same.jsonl"); public string B => Path.Combine(Root, "b", "same.jsonl");
        public string OtherCwd => Path.Combine(Root, "other-cwd");
        public readonly NoTransport Transport = new(); public readonly List<string> FactoryCwds = [];
        public ReaderFiles Readers = null!; public StoreFactory Storage = null!;
        public ReplaceableAgentSession Owner = null!; private SessionRuntimeRegistry registry = null!;
        public string Key(SessionCatalogPage page) => page.Items.Single(item => item.Path == B).Key;
        public Task<PersistentAgentSession> Reopen(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 0,
            () => Guid.NewGuid().ToString("N"), fallbackModel: Model);
        public static async Task<Fixture> Open(bool rebind = false, bool foreignCwd = false)
        {
            var f = new Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(f.A)!); Directory.CreateDirectory(Path.GetDirectoryName(f.B)!); Directory.CreateDirectory(f.OtherCwd);
            await using (var store = await SessionLogStore.CreateNewAsync(f.A, Codec.Parse(Header(f.Root)))) { }
            var cwd = rebind || foreignCwd ? f.OtherCwd : f.Root;
            await using (var store = await SessionLogStore.CreateNewAsync(f.B, Codec.Parse(Header(cwd))))
            {
                var model = rebind ? OtherModel : Model;
                await store.AppendAsync([Record("model", null, "model_change", new { provider = model.Provider, modelId = model.Id }),
                    State("base", "model", "base"), State("left", "base", "left"), State("right", "base", "right")]);
            }
            f.registry = new([new(Model, f.Transport)], [], new NoPolicy());
            f.Readers = new(f.B); f.Storage = new(f.B);
            var options = new PersistentAgentSessionOptions(SessionLogStoreOptions: new(StorageFactory: f.Storage));
            var source = await PersistentAgentSession.OpenWithRegistryAsync(f.A, f.registry, () => 0, () => Guid.NewGuid().ToString("N"), options, Model);
            SessionRuntimeRegistry Bind(string actualCwd)
            {
                f.FactoryCwds.Add(actualCwd);
                if (actualCwd != f.OtherCwd && actualCwd != f.Root) throw new InvalidOperationException("Unconfigured fixture cwd.");
                return new([new(Model, f.Transport), new(OtherModel, f.Transport)], [], new NoPolicy());
            }
            var catalog = new SessionCatalog([new("a", Path.GetDirectoryName(f.A)!), new("b", Path.GetDirectoryName(f.B)!)], fileSystem: f.Readers);
            f.Owner = ReplaceableAgentSession.WithLifecycle(source, new(f.registry, () => 0, () => Guid.NewGuid().ToString("N"), options,
                catalog: catalog, registryForWorkingDirectory: rebind ? Bind : null));
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            Readers.ReleaseTargetClose.TrySetResult(); Storage.ReleaseTargetClose.TrySetResult(); await Owner.DisposeAsync();
            File.Delete(A); File.Delete(B); Directory.Delete(Path.GetDirectoryName(A)!); Directory.Delete(Path.GetDirectoryName(B)!); Directory.Delete(OtherCwd); Directory.Delete(Root);
        }
    }
    private sealed class ReaderFiles(string target) : ISessionCatalogFileSystem
    {
        public bool HoldTargetRead, HoldTargetClose, TargetClosed; public Action? AfterTargetClose;
        public readonly TaskCompletionSource TargetReadEntered = Gate(), TargetCloseEntered = Gate(), ReleaseTargetClose = Gate();
        public bool IsDirectoryLink(string directory) => false;
        public IEnumerable<string> EnumerateFileNames(string directory) => Directory.EnumerateFiles(directory).Select(Path.GetFileName).Select(name => name!);
        public SessionCatalogFileMetadata GetMetadata(string path) { var file = new FileInfo(path); return new(file.Length, file.LastWriteTimeUtc.Ticks, false); }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Stream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 512, FileOptions.Asynchronous);
            return ValueTask.FromResult(path == target ? new Reader(file, this) : file);
        }
    }
    private sealed class Reader(Stream inner, ReaderFiles owner) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { owner.TargetReadEntered.TrySetResult(); if (owner.HoldTargetRead) await Task.Delay(Timeout.Infinite, token); return await inner.ReadAsync(buffer, token); }
        public override async ValueTask DisposeAsync()
        {
            owner.TargetCloseEntered.TrySetResult(); if (owner.HoldTargetClose) await owner.ReleaseTargetClose.Task;
            await inner.DisposeAsync(); owner.TargetClosed = true; var changed = owner.AfterTargetClose; owner.AfterTargetClose = null; changed?.Invoke(); GC.SuppressFinalize(this);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StoreFactory(string target) : ISessionLogStorageFactory
    {
        public bool HoldTargetClose;
        public TaskCompletionSource TargetCloseEntered = Gate(), ReleaseTargetClose = Gate();
        public void ArmTargetClose() { TargetCloseEntered = Gate(); ReleaseTargetClose = Gate(); HoldTargetClose = true; }
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        {
            var store = await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token);
            return path == target ? new HeldStore(store, this) : store;
        }
    }
    private sealed class HeldStore(ISessionLogStorage inner, StoreFactory owner) : ISessionLogStorage
    {
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long expected) => inner.PositionForAppend(expected); public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk(); public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync() { owner.TargetCloseEntered.TrySetResult(); if (owner.HoldTargetClose) await owner.ReleaseTargetClose.Task; await inner.DisposeAsync(); }
    }
    private sealed class NoTransport : IChatTransport
    {
        public int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("Catalog/resume must not acquire a provider turn.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Catalog/resume must not invoke a tool."); }
}
