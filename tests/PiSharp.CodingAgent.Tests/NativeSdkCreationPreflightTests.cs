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
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class NativeSdkCreationPreflightTests
{
    private static readonly ModelDescriptor Model = new("sdk-preflight", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Time = "2026-10-02T00:00:00.000Z";
    private const string Command = "sdk-clone-check";

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("native-sdk-creation-preflight count overflow rolls back and original SDK context physically appends A", CountBoundaryRollback),
        ("native-sdk-creation-preflight global sibling label obeys record character and aggregate UTF8 policy", GlobalLabelBudgets),
        ("native-sdk-creation-preflight generated selected leaf obeys eventual SDK identifier policy", GeneratedLeafPolicy),
        ("native-sdk-creation-preflight exact count control retains opaque tokens and writable fresh B", UnderLimitOpaqueControl),
        ("native-sdk-creation-preflight canceled staged validator joins actual writer close and file deletion", StagedCancellationCleanup)
    ];

    private const int SnapshotEntries = 4_096;
    private const long SnapshotCharacters = 1_048_576;
    private const long SnapshotUtf8Bytes = 1_048_576;

    private static async Task CountBoundaryRollback()
    {
        await using var f = await Fixture.Open(new(SnapshotEntries));
        await RejectedClone(f, ExtensionRegistrationFailure.LimitExceeded, async snapshot =>
        {
            Equal(SnapshotEntries + 1, snapshot.BranchEntries.Length);
            var sizes = Size(snapshot); Check(sizes.Characters < SnapshotCharacters &&
                sizes.Utf8Bytes < SnapshotUtf8Bytes, "Count fixture also exceeded a byte/character budget.");
            await Task.CompletedTask;
        });
    }

    private static async Task GlobalLabelBudgets()
    {
        // Session entries have no per-entry bound of their own (runner.ts hands handlers the whole session): only the aggregates.
        foreach (var budget in new[] { "characters", "utf8" })
        {
            var spec = budget switch
            {
                "record" => new Spec(2, Label: new string('x', 65_536)),
                "characters" => new Spec(17, Padding: new string('x', 60_000), Label: new string('x', 30_000)),
                _ => new Spec(10, Padding: new string('\u03c0', 50_000), Label: new string('x', 50_000))
            };
            await using var f = await Fixture.Open(spec);
            await RejectedClone(f, ExtensionRegistrationFailure.LimitExceeded, snapshot =>
            {
                var sizes = Size(snapshot);
                Equal(spec.Count + 1, snapshot.BranchEntries.Length);
                if (budget == "record")
                    Check(sizes.MaximumRecordCharacters > f.Options.MaximumJsonCharacters &&
                        sizes.Characters < SnapshotCharacters, "Per-record fixture did not isolate the record budget.");
                else if (budget == "characters")
                    Check(sizes.MaximumRecordCharacters <= f.Options.MaximumJsonCharacters &&
                        sizes.Characters > SnapshotCharacters, "Aggregate character fixture failed to cross the actual staged budget.");
                else
                    Check(sizes.MaximumRecordCharacters <= f.Options.MaximumJsonCharacters &&
                        sizes.Characters < SnapshotCharacters && sizes.Utf8Bytes > SnapshotUtf8Bytes,
                        "UTF8 fixture did not isolate aggregate bytes from characters and per-record limits.");
                return Task.CompletedTask;
            });
        }
    }

    private static async Task GeneratedLeafPolicy()
    {
        foreach (var id in new[] { "created/label", "\u03c0-label" })
        {
            await using var f = await Fixture.Open(new(2, GeneratedLabelId: id));
            await RejectedClone(f, ExtensionRegistrationFailure.InvalidDescriptor, snapshot =>
            {
                Equal(id, snapshot.SelectedLeafId); Equal(id, snapshot.BranchEntries[^1].Value.GetProperty("id").GetString());
                Equal("e0", snapshot.BranchEntries[^1].Value.GetProperty("targetId").GetString());
                return Task.CompletedTask;
            });
        }
    }

    private static async Task UnderLimitOpaqueControl()
    {
        var count = SnapshotEntries - 1;
        await using var f = await Fixture.Open(new(count)); var previous = f.Owner.Current; var original = await Bytes(f.Files.Source);
        var returned = false;
        await f.Register(async (context, _) =>
        {
            var initial = ((IExtensionSessionContext)context).SessionSnapshot!;
            Equal(count, initial.BranchEntries.Length); Equal("1.00e400", initial.BranchEntries[0].Value.GetProperty("data").GetProperty("opaque").GetRawText());
            var created = await ((IExtensionSessionCreationCommandContext)context).CreateSessionAsync(new(ExtensionSessionCreationKind.Clone));
            Check(created is not null, "Exact-boundary clone was vetoed."); returned = true;
            var fresh = created!.Context.SessionSnapshot!;
            Equal(SnapshotEntries, fresh.BranchEntries.Length); Equal(2L, fresh.Generation);
            Equal("1.00e400", fresh.BranchEntries[0].Value.GetProperty("data").GetProperty("opaque").GetRawText());
            Equal("global label", fresh.BranchEntries[^1].Value.GetProperty("label").GetString());
            var receipt = await created.Context.AppendSessionEntryAsync("fresh", 1, JsonData.Parse("{\"fresh\":true}"));
            Equal(2L, receipt.Generation); Check(receipt.ByteLength > 0, "Fresh SDK context did not physically append B.");
            await Throws<InvalidOperationException>(() => ((IExtensionSessionActionsContext)context)
                .AppendSessionEntryAsync("stale", 1, JsonData.Null).AsTask());
        });
        await f.Invoke(); Equal(1, f.Admitted); Check(returned && f.Owner.Current.Generation == 2 && previous.Session.Snapshot.IsRetired,
            "Under-limit SDK clone did not return a committed fresh context.");
        SameBytes(original, await Bytes(f.Files.Source)); Equal(0, f.IO.Deleted.Count); Equal(1, f.AttachmentChanges);
        var path = f.Owner.Current.Session.Path; await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(path);
        Equal(count + 2, reopened.Snapshot.Log.Entries.Length); Equal("custom", reopened.Snapshot.Log.Entries[^1].Type);
        Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("fresh").GetBoolean(),
            "Independent reopen lost the fresh SDK append."); NoTemps(f.Files);
    }

    private static async Task RejectedClone(Fixture f, ExtensionRegistrationFailure failure,
        Func<ExtensionSessionSnapshot, Task> assertStaged)
    {
        var previous = f.Owner.Current; var original = await Bytes(f.Files.Source); var continued = false;
        await f.Register(async (context, _) =>
        {
            var captured = ((IExtensionSessionContext)context).SessionSnapshot!;
            Equal(f.Spec.Count, captured.BranchEntries.Length); Equal("e" + (f.Spec.Count - 1), captured.SelectedLeafId);
            var acceptedSize = Size(captured);
            Check(acceptedSize.Characters <= SnapshotCharacters &&
                acceptedSize.Utf8Bytes <= SnapshotUtf8Bytes && acceptedSize.MaximumRecordCharacters <= f.Options.MaximumJsonCharacters,
                "Original callback did not satisfy the eventual SDK snapshot budgets.");
            var error = await Throws<ExtensionRegistrationException>(() => ((IExtensionSessionCreationCommandContext)context)
                .CreateSessionAsync(new(ExtensionSessionCreationKind.Clone)).AsTask());
            Equal(failure, error.Failure); Equal("capture-session-snapshot", error.Operation);
            await AssertRollback(f, previous, original); await assertStaged(await f.PublishedSnapshot());
            var acknowledgment = await ((IExtensionSessionActionsContext)context).AppendSessionEntryAsync("continued", 1,
                JsonData.Parse("{\"continued\":true}"));
            Equal(1L, acknowledgment.Generation); Equal("source", acknowledgment.SessionId);
            Check(acknowledgment.ByteLength > 0 && acknowledgment.Entry.Value.GetProperty("parentId").GetString() == captured.SelectedLeafId,
                "Failed clone did not leave the original SDK writer on its logical leaf.");
            var after = await Bytes(f.Files.Source);
            Check(after.Length > original.Length && after.AsSpan(0, original.Length).SequenceEqual(original), "Original SDK append did not physically extend intact A bytes.");
            continued = true;
        });
        await f.Invoke(); Equal(1, f.Admitted); Check(continued && f.Files.OnlySource(), "Rejected SDK clone left a target or could not continue A.");
        await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(f.Files.Source);
        Equal("custom", reopened.Snapshot.Log.Entries[^1].Type);
        Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("continued").GetBoolean(),
            "Independent A reopen lost the post-rejection SDK checkpoint."); NoTemps(f.Files);
    }

    private static async Task StagedCancellationCleanup()
    {
        await using var f = await Fixture.Open(new(2), heldValidator: true); using var caller = new CancellationTokenSource();
        var previous = f.Owner.Current; var original = await Bytes(f.Files.Source);
        f.Storage.HoldClose = true; f.IO.HoldDelete = true;
        await f.Register(async (context, token) =>
        {
            await ((IExtensionSessionCreationCommandContext)context).CreateSessionAsync(new(ExtensionSessionCreationKind.Clone), token);
            throw new InvalidOperationException("Canceled staged SDK creation returned success.");
        });
        var invocation = f.Invoke(caller.Token);
        try
        {
            await f.Held!.Entered.Task.WaitAsync(Bound);
            Equal(2L, f.Held.Staged!.Generation); Equal("created", f.Held.Staged.SessionId);
            Check(ReferenceEquals(f.Owner.Current, previous) && !previous.Session.Snapshot.IsRetired && File.Exists(f.IO.FinalPath),
                "Staged validator crossed source retirement before cancellation.");
            caller.Cancel(); await f.Storage.CloseEntered.Task.WaitAsync(Bound);
            Check(!invocation.IsCompleted && File.Exists(f.IO.FinalPath) && f.IO.Deleted.Count == 0 && f.Storage.Opened.Single().Closed,
                "Cancellation returned before the held actual target close settled.");
            f.Storage.CloseRelease.TrySetResult(); await f.IO.DeleteEntered.Task.WaitAsync(Bound);
            Check(!invocation.IsCompleted && File.Exists(f.IO.FinalPath) && f.IO.ClosedWriterProofs == 1,
                "Cancellation returned before known unattached-file deletion settled.");
            f.IO.DeleteRelease.TrySetResult(); await Throws<OperationCanceledException>(() => invocation);
            await AssertRollback(f, previous, original); Equal(1, f.Admitted);
            await f.Owner.AppendExtensionEntryAsync(previous, new("sdk", "after_cancel", 1, JsonData.Parse("{\"afterCancel\":true}")));
            var after = await Bytes(f.Files.Source); Check(after.Length > original.Length && after.AsSpan(0, original.Length).SequenceEqual(original),
                "Source was not physically usable after joined staged cancellation.");
            await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(f.Files.Source); Equal("custom", reopened.Snapshot.Log.Entries[^1].Type);
        }
        finally
        {
            caller.Cancel(); f.Held!.Release.TrySetResult(); f.Storage.CloseRelease.TrySetResult(); f.IO.DeleteRelease.TrySetResult(); await Drain(invocation);
        }
    }

    private static async Task AssertRollback(Fixture f, AgentSessionAttachment previous, byte[] original)
    {
        Check(ReferenceEquals(f.Owner.Current, previous) && previous.Generation == 1 && !previous.Session.Snapshot.IsRetired &&
            !previous.Session.Snapshot.IsDisposed && previous.Session.Snapshot.Fault is null, "SDK preflight rejection crossed source attachment retirement.");
        Equal(0, f.AttachmentChanges); SameBytes(original, await Bytes(f.Files.Source));
        Equal(1, f.IO.Created); Equal(1, f.IO.Published); Check(f.IO.Deleted.SequenceEqual(new[] { f.IO.FinalPath! }) && f.Files.OnlySource(),
            "SDK rollback retained its known branch or deleted outside its owned target.");
        Equal(1, f.IO.ClosedWriterProofs); Equal(1, f.Storage.Opened.Count);
        Check(f.Storage.Opened.Single().Closed && f.Storage.Opened.Single().DisposeCalls == 1, "SDK rollback did not join exactly one actual target writer close."); NoTemps(f.Files);
    }

    private static (long Characters, long Utf8Bytes, int MaximumRecordCharacters) Size(ExtensionSessionSnapshot snapshot)
    {
        var raw = snapshot.BranchEntries.Select(entry => entry.ToString()).ToArray();
        return ((long)snapshot.SessionId.Length + (snapshot.SelectedLeafId?.Length ?? 0) + raw.Sum(text => (long)text.Length),
            Encoding.UTF8.GetByteCount(snapshot.SessionId) + (long)(snapshot.SelectedLeafId is null ? 0 : Encoding.UTF8.GetByteCount(snapshot.SelectedLeafId)) +
            raw.Sum(text => (long)Encoding.UTF8.GetByteCount(text)), raw.Length == 0 ? 0 : raw.Max(text => text.Length));
    }
    private static async Task<byte[]> Bytes(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); return bytes.ToArray();
    }
    private static void SameBytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Physical source bytes changed before continuation.");
    private static void NoTemps(Files files) => Check(!Directory.EnumerateFiles(files.Root, ".pisharp-branch-*.tmp").Any(), "SDK preflight left an owned temporary file.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task task)
    { try { await task.WaitAsync(Bound); } catch (Exception) when (task.IsCompleted) { } }

    private sealed record Spec(int Count, string Padding = "", string Label = "global label", string GeneratedLabelId = "created-label");
    private sealed class Fixture : IAsyncDisposable
    {
        public Files Files { get; } = new();
        public Spec Spec { get; }
        // The SDK host bounds its callback views explicitly here, so the boundary fixtures stay small; the defaults
        // (ExtensionSessionSnapshotLimits) grow with the session as the request budgets do.
        public ExtensionRegistryOptions Options { get; } = new()
        { MaximumSessionBranchEntries = SnapshotEntries, MaximumSessionCharacters = SnapshotCharacters, MaximumSessionUtf8Bytes = SnapshotUtf8Bytes };
        public StorageFactory Storage { get; } = new();
        public LocalFiles IO { get; }
        public ReplaceableAgentSession Owner { get; private set; } = null!;
        public ExtensionRegistry Registry { get; private set; } = null!;
        public HeldProvider? Held { get; private set; }
        public int Admitted { get; private set; }
        public int AttachmentChanges { get; private set; }
        private readonly NoTransport transport = new();
        private readonly SessionRuntimeRegistry runtime;
        private int ids;
        private Fixture(Spec spec) { Spec = spec; IO = new(Files); runtime = new([new(Model, transport)], [], new NoPolicy()); }
        private string NextEntry() => ++ids == 1 ? Spec.GeneratedLabelId : "append-" + ids;
        public static async Task<Fixture> Open(Spec spec, bool heldValidator = false)
        {
            var f = new Fixture(spec); PersistentAgentSession? source = null;
            try
            {
                var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "source", timestamp = Time, cwd = f.Files.Root }));
                var entries = ImmutableArray.CreateBuilder<SessionEntry>();
                for (var index = 0; index < spec.Count; index++)
                    entries.Add(Codec.Parse("{\"type\":\"future\",\"id\":\"e" + index + "\",\"parentId\":" + (index == 0 ? "null" : "\"e" + (index - 1) + "\"") +
                        ",\"timestamp\":\"" + Time + "\",\"data\":{" + (index == 0 ? "\"opaque\":1.00e400," : "") + "\"text\":\"" + spec.Padding + "\"}}"));
                entries.Add(Codec.Parse(JsonSerializer.Serialize(new { type = "label", id = "sibling-label", parentId = (string?)null,
                    timestamp = Time, targetId = "e0", label = spec.Label })));
                await using (var store = await SessionLogStore.CreateNewAsync(f.Files.Source, header))
                    foreach (var batch in entries.ToImmutable().Chunk(128)) await store.AppendAsync(batch.ToImmutableArray());
                source = await PersistentAgentSession.OpenWithRegistryAsync(f.Files.Source, f.runtime, () => 0, f.NextEntry,
                    new(UseLatestLeaf: false, SelectedLeafId: "e" + (spec.Count - 1)), Model);
                var lifecycle = new PersistentSessionLifecycle(f.runtime, () => 0, f.NextEntry,
                    new(SessionLogStoreOptions: new(StorageFactory: f.Storage)), () => "created", f.IO);
                f.Owner = new(source, (_, _) => throw new InvalidOperationException("SDK clone fixtures do not switch files."), lifecycle); source = null;
                f.Owner.AttachmentChanged = _ => { f.AttachmentChanges++; return ValueTask.CompletedTask; };
                var native = new NativeSessionSnapshotProvider(); native.Attach(f.Owner);
                IExtensionSessionViewProvider provider = native;
                if (heldValidator) { f.Held = new(native); provider = f.Held; }
                f.Registry = new(f.Options, null, provider); return f;
            }
            catch
            {
                if (source is not null) await source.DisposeAsync(); if (f.Registry is not null) await f.Registry.DisposeAsync();
                if (f.Owner is not null) await f.Owner.DisposeAsync(); f.Files.Dispose(); throw;
            }
        }
        public Task Register(Func<IExtensionCommandContext, CancellationToken, ValueTask> callback) => Registry.ActivateAsync("sdk", new Plugin((registry, _) =>
        {
            registry.RegisterCommand(new("clone-registration", Command, "", (arguments, context, token) => { Admitted++; return callback(context, token); }));
            return ValueTask.CompletedTask;
        }));
        public Task Invoke(CancellationToken token = default) => Registry.InvokeCommandAsync(Registry.CaptureSnapshot(), Command, JsonData.Null, operationToken: token).AsTask();
        public Task<PersistentAgentSession> Reopen(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0, NextEntry, fallbackModel: Model);
        public async Task<ExtensionSessionSnapshot> PublishedSnapshot()
        {
            var log = await new SessionLogReader().ReadAsync(new MemoryStream(IO.PublishedBytes!, writable: false), leaveOpen: false);
            Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete, "Actual published staging bytes were incomplete.");
            var entries = log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToArray();
            return new(log.Header!.Id, 2, entries[^1].Id, entries.Select(entry => entry.WireBody).ToImmutableArray());
        }
        public async ValueTask DisposeAsync()
        {
            try { await Registry.DisposeAsync(); await Owner.DisposeAsync(); Equal(0, transport.Calls); }
            finally { Files.Dispose(); }
        }
    }

    private sealed class Plugin(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class NoTransport : IChatTransport
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("SDK preflight fixtures must not call a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            throw new InvalidOperationException("SDK preflight fixtures must not acquire a tool.");
    }
    private sealed class Files : IDisposable
    {
        private const string Prefix = "PiSharp-sdk-preflight-";
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Source => Path.Combine(Root, "source.jsonl");
        public Files() { Root = Path.GetFullPath(Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"))); ValidateRoot(); Directory.CreateDirectory(Root); }
        public void ValidatePath(string path) => Check(Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), Comparison) &&
            string.Equals(Path.GetDirectoryName(path), Root, Comparison), "SDK fixture path escaped its canonical owned root.");
        public bool OnlySource() => Directory.EnumerateFileSystemEntries(Root).SequenceEqual(new[] { Source });
        private void ValidateRoot()
        {
            var name = Path.GetFileName(Root);
            Check(Path.IsPathFullyQualified(Root) && string.Equals(Root, Path.GetFullPath(Root), Comparison) &&
                string.Equals(Path.GetDirectoryName(Root), temp, Comparison) && name.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Refusing cleanup outside the owned SDK preflight fixture.");
        }
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return;
            Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "SDK fixture root became a reparse point.");
            foreach (var path in Directory.EnumerateFileSystemEntries(Root))
            {
                ValidatePath(path); Check((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "SDK cleanup encountered an unowned directory or reparse point.");
                File.Delete(path);
            }
            Directory.Delete(Root, recursive: false);
        }
    }
    private sealed class LocalFiles(Files files) : ISessionBranchFileSystem
    {
        public int Created { get; private set; }
        public int Published { get; private set; }
        public int ClosedWriterProofs { get; private set; }
        public string? FinalPath { get; private set; }
        public byte[]? PublishedBytes { get; private set; }
        private string? temporary;
        public List<string> Deleted { get; } = [];
        public bool HoldDelete { get; set; }
        public readonly TaskCompletionSource DeleteEntered = Gate(), DeleteRelease = Gate();
        public ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path)
        {
            files.ValidatePath(path); Check(Path.GetFileName(path).StartsWith(".pisharp-branch-", StringComparison.Ordinal), "SDK fixture did not receive an owned temporary path.");
            Created++; temporary = path; return SessionBranchPublisher.LocalFileSystem.CreateNewTemporaryAsync(path);
        }
        public async ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        {
            files.ValidatePath(temporaryPath); files.ValidatePath(destinationPath); Equal(temporary, temporaryPath);
            PublishedBytes = await File.ReadAllBytesAsync(temporaryPath); FinalPath = destinationPath; Published++;
            await SessionBranchPublisher.LocalFileSystem.PublishNewAsync(temporaryPath, destinationPath);
        }
        public async ValueTask DeleteOwnedAsync(string path)
        {
            files.ValidatePath(path); Check(path == temporary || path == FinalPath, "SDK rollback deleted outside actual staged/final ownership."); Deleted.Add(path);
            if (path == FinalPath && File.Exists(path))
            {
                using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } ClosedWriterProofs++;
                DeleteEntered.TrySetResult(); if (HoldDelete) await DeleteRelease.Task.WaitAsync(Bound);
            }
            await SessionBranchPublisher.LocalFileSystem.DeleteOwnedAsync(path);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public bool HoldClose { get; set; }
        public List<Storage> Opened { get; } = [];
        public readonly TaskCompletionSource CloseEntered = Gate(), CloseRelease = Gate();
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        {
            var storage = new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this); Opened.Add(storage); return storage;
        }
    }
    private sealed class Storage(ISessionLogStorage inner, StorageFactory owner) : ISessionLogStorage
    {
        public int DisposeCalls { get; private set; }
        public bool Closed { get; private set; }
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk();
        public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync()
        {
            DisposeCalls++; await inner.DisposeAsync(); Closed = true; owner.CloseEntered.TrySetResult();
            if (owner.HoldClose) await owner.CloseRelease.Task.WaitAsync(Bound);
        }
    }
    // This scheduling wrapper forwards capture/actions to the actual production broker and invokes the
    // registry-supplied policy unchanged before holding the trusted staged callback for cancellation.
    private sealed class HeldProvider(NativeSessionSnapshotProvider inner) : IExtensionSessionCreationProvider, IExtensionSessionOpaqueViewProvider
    {
        public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public ExtensionSessionSnapshot? Staged { get; private set; }
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => inner.Capture(context);
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) =>
            new HeldScope((IExtensionSessionCreationScope)inner.OpenScope(context, snapshot), this);
        private sealed class HeldScope(IExtensionSessionCreationScope innerScope, HeldProvider owner) : IExtensionSessionCreationScope
        {
            public ExtensionSessionSnapshot Snapshot => innerScope.Snapshot;
            public CancellationToken SessionCancellationToken => innerScope.SessionCancellationToken;
            public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int version, JsonData data, CancellationToken token) => innerScope.AppendAsync(kind, version, data, token);
            public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) => innerScope.SwitchAsync(path, latest, leaf, token);
            public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request, CancellationToken token) => innerScope.CreateAsync(request, token);
            public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request,
                Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> validate, CancellationToken token) =>
                innerScope.CreateAsync(request, async (snapshot, stagedToken) =>
                {
                    await validate(snapshot, stagedToken); owner.Staged = snapshot; owner.Entered.TrySetResult();
                    await owner.Release.Task.WaitAsync(Bound, stagedToken);
                }, token);
            public ValueTask DisposeAsync() => innerScope.DisposeAsync();
        }
    }
}
