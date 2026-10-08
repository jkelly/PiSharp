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
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class NativeSdkSessionResumeTests
{
    private static readonly ModelDescriptor Model = new("sdk-resume", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new(new(MaximumJsonDepth: 64));
    private static readonly SessionLogReaderOptions ReaderOptions = new(CodecOptions: new(MaximumJsonDepth: 64));
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Timestamp = "2026-10-02T00:00:00.000Z";
    private const string Command = "sdk-resume-check";

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("native-sdk-session-resume listing separates identical IDs in explicit stores without file effects", ListExplicitStores),
        ("native-sdk-session-resume selected siblings and root restore namespaced state and fresh physical authority", SelectedStateAndFreshAuthority),
        ("native-sdk-session-resume resume and switch validate actual target count characters UTF8 depth and IDs before retirement", TargetSnapshotPolicy),
        ("native-sdk-session-resume opaque raw numbers survive while executable command input remains finite", OpaqueViewAndFiniteInput),
        ("native-sdk-session-resume canceled staged validator joins target close and keeps source usable", CanceledTargetClose),
        ("native-sdk-session-resume typed veto closes target and preserves both existing files", TypedVeto),
        ("native-sdk-session-resume committed resume and switch errors report identity after scope cleanup joins", CommittedReceiptAndScopeJoin)
    ];

    private static async Task ListExplicitStores()
    {
        await using var f = await Fixture.Open(); var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
        await f.Register(async (context, _) =>
        {
            var catalog = (IExtensionSessionCatalogCommandContext)context;
            var first = await catalog.ListSessionsAsync(new(1, WorkingDirectory: f.Files.Root));
            Equal(1, first.Items.Length); Check(first.NextCursor is not null, "Catalog did not expose the second explicit store.");
            var second = await catalog.ListSessionsAsync(new(1, first.NextCursor, f.Files.Root));
            Equal(1, second.Items.Length); Check(second.NextCursor is null, "Catalog did not end after the two owned files.");
            var items = first.Items.AddRange(second.Items);
            Equal(2, items.Select(item => item.Key).Distinct().Count()); Equal(1, items.Select(item => item.SessionId).Distinct().Count());
            Equal(1, items.Select(item => item.FileName).Distinct().Count());
            Check(items.Select(item => item.StoreId).Order().SequenceEqual(new[] { "one", "two" }), "Duplicate session IDs collapsed distinct explicit stores.");
            foreach (var item in items)
            {
                Equal("same", item.SessionId); Equal(f.Files.Root, item.WorkingDirectory); Equal(64, item.Key.Length);
                Equal(item.Path == f.Files.Source, item.IsCurrent);
                Equal((long)(item.IsCurrent ? original.Length : target.Length), item.FileBytes);
            }
            Equal(0, first.SkippedFiles + second.SkippedFiles); Equal(0, first.UnavailableStores + second.UnavailableStores);
            var repeated = await catalog.ListSessionsAsync(new());
            Check(repeated.Items.Select(item => item.Key).Order().SequenceEqual(items.Select(item => item.Key).Order()), "Unchanged catalog scan changed stable keys.");
            Equal(1L, catalog.SessionSnapshot!.Generation); Equal("a", catalog.SessionSnapshot.SelectedLeafId);
        });
        await f.Invoke(); Equal(1, f.Admitted); Equal(0, f.Storage.Opened.Count); Equal(0, f.AttachmentChanges);
        SameBytes(original, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target)); f.AssertOwnedFiles();
    }

    private static async Task SelectedStateAndFreshAuthority()
    {
        foreach (var leaf in new string?[] { "left-state", "right-state", "root-state", null })
        {
            await using var f = await Fixture.Open(); var previous = f.Owner.Current;
            var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target); string? appendedId = null;
            await f.Register(async (context, _) =>
            {
                var fresh = await Replace(context, f, resume: true, latest: false, leaf: leaf);
                Check(fresh is not null, "Valid explicit target selection was vetoed.");
                var snapshot = fresh!.SessionSnapshot!; Equal("same", snapshot.SessionId); Equal(2L, snapshot.Generation); Equal(leaf, snapshot.SelectedLeafId);
                if (leaf is null) { Equal(0, snapshot.BranchEntries.Length); Check(fresh.ReadLatest("state") is null, "Empty root inherited a physical sibling's state."); }
                else
                {
                    var expected = leaf == "left-state" ? "left" : leaf == "right-state" ? "right" : "root";
                    Equal(expected, fresh.ReadLatest("state")!.Data.Value.GetProperty("value").GetString());
                    Check(fresh.ReadLatest("missing") is null, "Foreign or absent extension state escaped its namespace.");
                    var version = ThrowsSync<ExtensionSessionStateException>(() => fresh.ReadLatest("state", expectedSchemaVersion: 2));
                    Equal(ExtensionSessionStateFailure.IncompatibleVersion, version.Failure);
                    Check(!snapshot.BranchEntries.Any(entry => entry.Value.GetProperty("id").GetString() == (leaf == "left-state" ? "right-state" : "left-state")),
                        "Selected ancestry included the physical sibling.");
                }
                var receipt = await fresh.AppendSessionEntryAsync("fresh", 1, JsonData.Parse("{\"fresh\":true}"));
                Equal(2L, receipt.Generation); Equal("same", receipt.SessionId); Check(receipt.ByteLength > 0, "Fresh context did not durably append target.");
                Equal(leaf, receipt.Entry.Value.GetProperty("parentId").GetString()); appendedId = receipt.Entry.Value.GetProperty("id").GetString();
                await Throws<InvalidOperationException>(() => ((IExtensionSessionActionsContext)context).AppendSessionEntryAsync("stale", 1, JsonData.Null).AsTask());
                await Throws<OperationCanceledException>(() => ((IExtensionSessionCatalogContext)context).ListSessionsAsync(new()).AsTask());
            });
            await f.Invoke(); Equal(1, f.Admitted); Equal(1, f.AttachmentChanges);
            Check(previous.Session.Snapshot.IsRetired && f.Owner.Current.Generation == 2 && f.Owner.Current.Session.Path == f.Files.Target,
                "Resume did not retire A and attach the actual listed B.");
            SameBytes(original, await Bytes(f.Files.Source)); Prefix(target, await Bytes(f.Files.Target)); f.AssertOwnedFiles();
            await f.Owner.DisposeAsync();
            await using var source = await f.Reopen(f.Files.Source); Equal("a", source.Snapshot.Context.LeafId); Equal(1, source.Snapshot.Log.Entries.Length);
            await using var reopened = await f.Reopen(f.Files.Target); Equal(appendedId, reopened.Snapshot.Context.LeafId);
            Equal(7, reopened.Snapshot.Log.Entries.Length); Equal(leaf, reopened.Snapshot.Log.Entries[^1].ParentId);
            var disabled = reopened.Snapshot.Log.Entries.Single(entry => entry.Id == "disabled-state").WireBody.Value;
            Equal("1.00e400", disabled.GetProperty("data").GetProperty("data").GetProperty("opaque").GetRawText());
            Equal(99, disabled.GetProperty("data").GetProperty("schemaVersion").GetInt32());
            Equal(2, reopened.Snapshot.Log.Entries.Single(entry => entry.Id == "absent-state").WireBody.Value.GetProperty("data").GetProperty("schemaVersion").GetInt32());
        }
    }

    private static async Task TargetSnapshotPolicy()
    {
        var specs = new[]
        {
            new Spec("count", Count: ExtensionSessionSnapshotLimits.MaximumBranchEntries + 1),
            new Spec("record", Padding: new string('x', 65_536)),
            new Spec("characters", Count: 17, Padding: new string('x', 63_000)),
            new Spec("utf8", Count: 11, Padding: new string('\u03c0', 50_000)),
            new Spec("depth", Depth: 32), // The record object adds one level to the 32 nested arrays.
            new Spec("session-id", SessionId: "bad/session"),
            new Spec("leaf-id", LeafId: "bad/leaf")
        };
        foreach (var resume in new[] { true, false })
        foreach (var spec in specs)
        {
            await using var f = await Fixture.Open(spec); var previous = f.Owner.Current;
            var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
            await f.Register(async (context, _) =>
            {
                Equal("a", ((IExtensionSessionContext)context).SessionSnapshot!.SelectedLeafId);
                var error = await Throws<ExtensionRegistrationException>(() => Replace(context, f, resume));
                Equal(spec.Kind is "session-id" or "leaf-id" or "depth" ? ExtensionRegistrationFailure.InvalidDescriptor : ExtensionRegistrationFailure.LimitExceeded, error.Failure);
                Equal("capture-session-snapshot", error.Operation);
                await f.AssertRollback(previous, original, target);
                var staged = f.Provider.Staged!; Equal(2L, staged.Generation); Equal(spec.SessionId, staged.SessionId); Equal(spec.Count, staged.BranchEntries.Length);
                var sizes = Size(staged);
                if (spec.Kind == "count") Check(staged.BranchEntries.Length > 4096 && sizes.Characters < 1_048_576, "Count fixture crossed an unrelated budget.");
                if (spec.Kind == "record") Check(sizes.MaximumRecord > 65_536 && sizes.Characters < 1_048_576, "Record fixture did not isolate the per-record budget.");
                if (spec.Kind == "characters") Check(sizes.MaximumRecord <= 65_536 && sizes.Characters > 1_048_576, "Character fixture did not cross only the aggregate character boundary.");
                if (spec.Kind == "utf8") Check(sizes.MaximumRecord <= 65_536 && sizes.Characters < 1_048_576 && sizes.Utf8 > 1_048_576, "UTF8 fixture did not isolate aggregate bytes.");
                if (spec.Kind == "leaf-id") Equal("bad/leaf", staged.SelectedLeafId);
                if (spec.Kind == "depth") Equal(JsonValueKind.Array, staged.BranchEntries[0].Value.GetProperty("data").ValueKind);
                var receipt = await ((IExtensionSessionActionsContext)context).AppendSessionEntryAsync("continued", 1, JsonData.Parse("{\"continued\":true}"));
                Equal(1L, receipt.Generation); Equal("a", receipt.Entry.Value.GetProperty("parentId").GetString()); Check(receipt.ByteLength > 0, "Rejected target left A unable to append.");
            });
            await f.Invoke(); Equal(1, f.Admitted); Prefix(original, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
            await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(f.Files.Source);
            Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("continued").GetBoolean(), "Reopen lost A's post-rejection checkpoint.");
        }
    }

    private static async Task OpaqueViewAndFiniteInput()
    {
        await using var f = await Fixture.Open(); var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
        await f.Register(async (context, _) =>
        {
            Equal(1.25, f.LastArguments!.Value.GetProperty("n").GetDouble());
            var fresh = await Replace(context, f, resume: true); Check(fresh is not null, "Under-limit opaque target was rejected.");
            Equal("right", fresh!.ReadLatest("state")!.Data.Value.GetProperty("value").GetString());
            var raw = fresh!.SessionSnapshot!.BranchEntries;
            Equal("1.00e400", raw[0].Value.GetProperty("data").GetProperty("opaque").GetRawText());
            Equal("1.00e400", raw.Single(entry => entry.Value.GetProperty("id").GetString() == "disabled-state").Value.GetProperty("data").GetProperty("data").GetProperty("opaque").GetRawText());
            var receipt = await fresh.AppendSessionEntryAsync("finite", 1, JsonData.Parse("{\"n\":1.25}")); Equal(2L, receipt.Generation);
        });
        var rejected = await Throws<ExtensionRegistrationException>(() => f.Invoke(arguments: JsonData.Parse("{\"n\":1.00e400}")));
        Equal(ExtensionRegistrationFailure.InvalidDescriptor, rejected.Failure); Equal(0, f.Admitted); Equal(0, f.Storage.Opened.Count);
        SameBytes(original, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
        await f.Invoke(arguments: JsonData.Parse("{\"n\":1.25}")); Equal(1, f.Admitted); Equal(2L, f.Owner.Current.Generation);
        SameBytes(original, await Bytes(f.Files.Source)); Prefix(target, await Bytes(f.Files.Target)); await f.Owner.DisposeAsync();
        await using var reopened = await f.Reopen(f.Files.Target);
        Equal("1.00e400", reopened.Snapshot.Log.Entries[0].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText()); f.AssertOwnedFiles();
    }

    private static async Task CanceledTargetClose()
    {
        await using var f = await Fixture.Open(); using var caller = new CancellationTokenSource();
        var previous = f.Owner.Current; var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
        f.Provider.HoldValidation = true; f.Storage.HoldClose = true;
        await f.Register(async (context, _) => { await Replace(context, f, resume: true); throw new InvalidOperationException("Canceled resume returned success."); });
        var invocation = f.Invoke(caller.Token);
        try
        {
            await f.Provider.ValidationEntered.Task.WaitAsync(Bound); Equal(2L, f.Provider.Staged!.Generation);
            Check(ReferenceEquals(previous, f.Owner.Current) && !previous.Session.Snapshot.IsRetired, "Held actual target validation retired A.");
            caller.Cancel(); await f.Storage.CloseEntered.Task.WaitAsync(Bound);
            Check(!invocation.IsCompleted && f.Storage.Opened.Single().Closed, "Cancellation returned before actual target cleanup was joined.");
            SameBytes(original, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target)); f.AssertOwnedFiles();
            f.Storage.CloseRelease.TrySetResult(); await Throws<OperationCanceledException>(() => invocation);
            await f.AssertRollback(previous, original, target);
            await f.Owner.AppendExtensionEntryAsync(previous, new("sdk", "after_cancel", 1, JsonData.Parse("{\"afterCancel\":true}")));
            Prefix(original, await Bytes(f.Files.Source)); await f.Owner.DisposeAsync();
            await using var reopened = await f.Reopen(f.Files.Source);
            Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("afterCancel").GetBoolean(), "A's writer was unusable after joined cancellation.");
            await using var reopenedTarget = await f.Reopen(f.Files.Target); Equal("right-state", reopenedTarget.Snapshot.Context.LeafId);
        }
        finally { caller.Cancel(); f.Provider.ValidationRelease.TrySetResult(); f.Storage.CloseRelease.TrySetResult(); await Drain(invocation); }
    }

    private static async Task TypedVeto()
    {
        foreach (var resume in new[] { true, false })
        {
            await using var f = await Fixture.Open(); var previous = f.Owner.Current;
            var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target); var vetoes = 0;
            f.Native.BeforeSwitch = (attached, staged, token) => f.Registry.BeforeSessionSwitchAsync(f.Registry.CaptureSnapshot(),
                new(attached.Session.Snapshot.Log.Header.Id, staged.Snapshot.Log.Header.Id, staged.Path, staged.Snapshot.Context.LeafId), token);
            await f.Register(async (context, _) =>
            {
                Check(await Replace(context, f, resume) is null, "Typed Cancel decision did not veto replacement.");
                await f.AssertRollback(previous, original, target);
                var receipt = await ((IExtensionSessionActionsContext)context).AppendSessionEntryAsync("after_veto", 1, JsonData.Parse("{\"afterVeto\":true}")); Equal(1L, receipt.Generation);
            }, (proposal, context, _) =>
            {
                vetoes++; Equal("same", proposal.PreviousSessionId); Equal("same", proposal.TargetSessionId); Equal(f.Files.Target, proposal.TargetPath);
                Equal("right-state", proposal.SelectedLeafId); Equal(1L, ((IExtensionSessionContext)context).SessionSnapshot!.Generation);
                return ValueTask.FromResult(ExtensionSessionSwitchDecision.Cancel);
            });
            await f.Invoke(); Equal(1, vetoes); Equal(1, f.Admitted); Prefix(original, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
            await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(f.Files.Source);
            Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("afterVeto").GetBoolean(), "Typed veto did not preserve source writer authority."); f.AssertOwnedFiles();
        }
    }

    private static async Task CommittedReceiptAndScopeJoin()
    {
        foreach (var resume in new[] { true, false })
        {
            await using var f = await Fixture.Open(); using var caller = new CancellationTokenSource();
            var previous = f.Owner.Current; var original = await Bytes(f.Files.Source); var target = await Bytes(f.Files.Target);
            f.Provider.HoldTargetScopeClose = true;
            f.Owner.AttachmentChanged = replacement => { f.AttachmentChanges++; Equal(f.Files.Target, replacement.Current.Session.Path); caller.Cancel(); return ValueTask.CompletedTask; };
            await f.Register(async (context, _) => { await Replace(context, f, resume); throw new InvalidOperationException("Postcommit canceled fresh context returned success."); });
            var invocation = f.Invoke(caller.Token);
            try
            {
                await f.Provider.TargetScopeClosed.Task.WaitAsync(Bound);
                Check(!invocation.IsCompleted && f.Owner.Current.Generation == 2 && previous.Session.Snapshot.IsRetired && !f.Owner.Current.Session.Snapshot.IsDisposed,
                    "Fresh-context error lost the committed identity or escaped its scope cleanup join.");
                Equal(1, f.Provider.TargetCloseCalls); Equal(1, f.AttachmentChanges);
                SameBytes(original, await Bytes(f.Files.Source)); SameBytes(target, await Bytes(f.Files.Target));
                f.Provider.TargetScopeRelease.TrySetResult();
                if (resume)
                {
                    var error = await Throws<ExtensionSessionResumeCommittedException>(() => invocation);
                    Equal("same", error.Receipt.SessionId); Equal(2L, error.Receipt.Generation); Equal("right-state", error.Receipt.SelectedLeafId);
                    Check(error.InnerException is OperationCanceledException, "Resume receipt did not retain the fresh-context cancellation cause.");
                }
                else
                {
                    var error = await Throws<ExtensionSessionSwitchCommittedException>(() => invocation);
                    Equal("same", error.Receipt.SessionId); Equal(2L, error.Receipt.Generation); Equal("right-state", error.Receipt.SelectedLeafId);
                    Check(error.InnerException is OperationCanceledException, "Switch receipt did not retain the fresh-context cancellation cause.");
                }
                Equal(1, f.Provider.TargetCloseCalls); Check(!f.Storage.Opened.Single().Closed, "Closing returned SDK authority closed the attached writer.");
                var receipt = await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("sdk", "after_commit", 1, JsonData.Parse("{\"afterCommit\":true}")));
                Check(receipt.Append.ByteLength > 0, "Committed target could not physically append after its returned-context error.");
                await Throws<InvalidOperationException>(() => f.Owner.AppendExtensionEntryAsync(previous, new("sdk", "stale", 1, JsonData.Null)));
                Prefix(target, await Bytes(f.Files.Target)); SameBytes(original, await Bytes(f.Files.Source)); await f.Owner.DisposeAsync();
                await using var reopened = await f.Reopen(f.Files.Target);
                Check(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("afterCommit").GetBoolean(), "Committed target identity was reported without a durable usable writer."); f.AssertOwnedFiles();
            }
            finally { f.Provider.TargetScopeRelease.TrySetResult(); await Drain(invocation); }
        }
    }

    private static async Task<IExtensionSessionCommandContext?> Replace(IExtensionCommandContext context, Fixture f, bool resume, bool latest = true, string? leaf = null)
    {
        if (!resume) return await ((IExtensionSessionCommandContext)context).SwitchSessionAsync(f.Files.Target, latest, leaf);
        var catalog = (IExtensionSessionCatalogCommandContext)context;
        var item = (await catalog.ListSessionsAsync(new())).Items.Single(item => item.StoreId == "two");
        return await catalog.ResumeSessionAsync(item.Key, latest, leaf);
    }
    private static (long Characters, long Utf8, int MaximumRecord) Size(ExtensionSessionSnapshot snapshot)
    {
        var raw = snapshot.BranchEntries.Select(entry => entry.ToString()).ToArray();
        return (snapshot.SessionId.Length + (snapshot.SelectedLeafId?.Length ?? 0) + raw.Sum(text => (long)text.Length),
            Encoding.UTF8.GetByteCount(snapshot.SessionId) + (long)(snapshot.SelectedLeafId is null ? 0 : Encoding.UTF8.GetByteCount(snapshot.SelectedLeafId)) + raw.Sum(text => (long)Encoding.UTF8.GetByteCount(text)),
            raw.Length == 0 ? 0 : raw.Max(text => text.Length));
    }
    private static async Task<byte[]> Bytes(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
        using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); return bytes.ToArray();
    }
    private static void SameBytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Existing session bytes changed before an authorized append.");
    private static void Prefix(byte[] before, byte[] after) => Check(after.Length > before.Length && after.AsSpan(0, before.Length).SequenceEqual(before), "Checkpoint did not physically extend the intact existing log.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T ThrowsSync<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task task) { try { await task.WaitAsync(Bound); } catch (Exception) when (task.IsCompleted) { } }

    private sealed record Spec(string Kind = "state", int Count = 1, string Padding = "", int Depth = 0, string SessionId = "same", string? LeafId = null);
    private sealed class Fixture : IAsyncDisposable
    {
        public Files Files { get; } = new();
        public StorageFactory Storage { get; } = new();
        public NoBranchEffects Branches { get; } = new();
        public ReplaceableAgentSession Owner { get; private set; } = null!;
        public NativeSessionSnapshotProvider Native { get; private set; } = null!;
        public ObservedProvider Provider { get; private set; } = null!;
        public ExtensionRegistry Registry { get; private set; } = null!;
        public int Admitted { get; private set; }
        public int AttachmentChanges { get; set; }
        public JsonData? LastArguments { get; private set; }
        private readonly NoTransport transport = new();
        private readonly SessionRuntimeRegistry runtime;
        private int ids;
        private Fixture() { runtime = new([new(Model, transport)], [], new NoPolicy()); }
        private string NextId() => "append-" + Interlocked.Increment(ref ids);
        public static async Task<Fixture> Open(Spec? requested = null)
        {
            var spec = requested ?? new(); var f = new Fixture(); PersistentAgentSession? source = null;
            try
            {
                await Seed(f.Files.Source, "same", f.Files.Root, [Future("a", null, "{}")]);
                var entries = ImmutableArray.CreateBuilder<SessionEntry>();
                if (spec.Kind == "state") entries.AddRange(StateEntries());
                else
                {
                    var data = spec.Depth == 0 ? "{\"opaque\":1.00e400,\"text\":\"" + spec.Padding + "\"}" : new string('[', spec.Depth) + "0" + new string(']', spec.Depth);
                    for (var index = 0; index < spec.Count; index++)
                        entries.Add(Future(index == spec.Count - 1 && spec.LeafId is not null ? spec.LeafId : "e" + index, index == 0 ? null : "e" + (index - 1), data));
                }
                await Seed(f.Files.Target, spec.SessionId, f.Files.Root, entries.ToImmutable());
                source = await PersistentAgentSession.OpenWithRegistryAsync(f.Files.Source, f.runtime, () => 0, f.NextId,
                    new(SessionLogStoreOptions: new(ReaderOptions: ReaderOptions)), Model);
                var catalog = new SessionCatalog([new("one", f.Files.One), new("two", f.Files.Two)]);
                var lifecycle = new PersistentSessionLifecycle(f.runtime, () => 0, f.NextId,
                    new(SessionLogStoreOptions: new(ReaderOptions: ReaderOptions, StorageFactory: f.Storage)), fileSystem: f.Branches, catalog: catalog);
                f.Owner = ReplaceableAgentSession.WithLifecycle(source, lifecycle); source = null;
                f.Owner.AttachmentChanged = _ => { f.AttachmentChanges++; return ValueTask.CompletedTask; };
                f.Native = new(); f.Native.Attach(f.Owner); f.Provider = new(f.Native); f.Registry = new(null, null, f.Provider); return f;
            }
            catch
            {
                if (source is not null) await source.DisposeAsync(); if (f.Registry is not null) await f.Registry.DisposeAsync();
                if (f.Owner is not null) await f.Owner.DisposeAsync(); f.Files.Dispose(); throw;
            }
        }
        public Task Register(Func<IExtensionCommandContext, CancellationToken, ValueTask> callback, ExtensionSessionSwitchCallback? veto = null) =>
            Registry.ActivateAsync("sdk", new Plugin((registry, _) =>
            {
                registry.RegisterCommand(new("resume-registration", Command, "", (arguments, context, token) => { Admitted++; LastArguments = arguments; return callback(context, token); }));
                if (veto is not null) ((IExtensionSessionLifecycleRegistry)registry).RegisterSessionSwitchHandler(new("typed-veto", veto));
                return ValueTask.CompletedTask;
            }));
        public Task Invoke(CancellationToken token = default, JsonData? arguments = null) => Registry.InvokeCommandAsync(Registry.CaptureSnapshot(), Command, arguments ?? JsonData.Null, operationToken: token).AsTask();
        public Task<PersistentAgentSession> Reopen(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0, NextId,
            new(SessionLogStoreOptions: new(ReaderOptions: ReaderOptions)), Model);
        public void AssertOwnedFiles() { Equal(0, Branches.Calls); Check(Files.OnlyKnown(), "Resume/switch created, deleted or moved an existing file."); }
        public async Task AssertRollback(AgentSessionAttachment previous, byte[] source, byte[] target)
        {
            Check(ReferenceEquals(Owner.Current, previous) && previous.Generation == 1 && !previous.Session.Snapshot.IsRetired &&
                !previous.Session.Snapshot.IsDisposed && previous.Session.Snapshot.Fault is null, "Target rejection crossed source retirement.");
            Equal(0, AttachmentChanges); Equal(1, Storage.Opened.Count);
            Check(Storage.Opened.Single().Closed && Storage.Opened.Single().DisposeCalls == 1, "Target cleanup did not join exactly one actual writer close.");
            using (var exclusive = new FileStream(Files.Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            SameBytes(source, await Bytes(Files.Source)); SameBytes(target, await Bytes(Files.Target)); AssertOwnedFiles();
        }
        public async ValueTask DisposeAsync()
        {
            Provider.ValidationRelease.TrySetResult(); Provider.TargetScopeRelease.TrySetResult(); Storage.CloseRelease.TrySetResult();
            try { await Registry.DisposeAsync(); await Owner.DisposeAsync(); Equal(0, transport.Calls); }
            finally { Files.Dispose(); }
        }
    }

    private static SessionEntry Future(string id, string? parent, string data) => Codec.Parse("{\"type\":\"future\",\"id\":" + JsonSerializer.Serialize(id) +
        ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Timestamp + "\",\"data\":" + data + "}");
    private static SessionEntry State(string id, string parent, string owner, int version, string data) => Codec.Parse("{\"type\":\"custom\",\"customType\":\"pisharp.extension-state\",\"id\":\"" + id +
        "\",\"parentId\":\"" + parent + "\",\"timestamp\":\"" + Timestamp + "\",\"data\":{\"extensionId\":\"" + owner + "\",\"entryKind\":\"state\",\"schemaVersion\":" + version + ",\"data\":" + data + "}}");
    private static ImmutableArray<SessionEntry> StateEntries() =>
    [
        Future("root", null, "{\"opaque\":1.00e400,\"tool\":\"must-stay-data\"}"),
        State("root-state", "root", "sdk", 1, "{\"value\":\"root\"}"),
        State("disabled-state", "root-state", "disabled", 99, "{\"opaque\":1.00e400}"),
        State("absent-state", "disabled-state", "absent", 2, "{\"value\":\"absent\"}"),
        State("left-state", "absent-state", "sdk", 1, "{\"value\":\"left\"}"),
        State("right-state", "absent-state", "sdk", 1, "{\"value\":\"right\"}")
    ];
    private static async Task Seed(string path, string id, string cwd, ImmutableArray<SessionEntry> entries)
    {
        var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id, timestamp = Timestamp, cwd }));
        await using var store = await SessionLogStore.CreateNewAsync(path, header, new(ReaderOptions: ReaderOptions));
        foreach (var batch in entries.Chunk(128)) await store.AppendAsync(batch.ToImmutableArray());
    }
    private sealed class Plugin(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class NoTransport : IChatTransport
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("SDK resume fixtures must not call a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("SDK resume fixtures must not acquire a tool."); }
    private sealed class NoBranchEffects : ISessionBranchFileSystem
    {
        public int Calls { get; private set; }
        public ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path) { Calls++; throw new InvalidOperationException("Existing resume/switch attempted branch creation."); }
        public ValueTask PublishNewAsync(string temporary, string destination) { Calls++; throw new InvalidOperationException("Existing resume/switch attempted branch publication."); }
        public ValueTask DeleteOwnedAsync(string path) { Calls++; throw new InvalidOperationException("Existing resume/switch attempted file deletion."); }
    }
    private sealed class Files : IDisposable
    {
        private const string Prefix = "PiSharp-sdk-resume-";
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string One => Path.Combine(Root, "one"); public string Two => Path.Combine(Root, "two");
        public string Source => Path.Combine(One, "same.jsonl"); public string Target => Path.Combine(Two, "same.jsonl");
        public Files() { Root = Path.GetFullPath(Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"))); ValidateRoot(); Directory.CreateDirectory(One); Directory.CreateDirectory(Two); }
        public bool OnlyKnown() => Directory.EnumerateFileSystemEntries(Root).Order().SequenceEqual(new[] { One, Two }.Order()) &&
            Directory.EnumerateFileSystemEntries(One).SequenceEqual(new[] { Source }) && Directory.EnumerateFileSystemEntries(Two).SequenceEqual(new[] { Target });
        private void ValidateRoot()
        {
            var name = Path.GetFileName(Root);
            Check(Path.IsPathFullyQualified(Root) && string.Equals(Root, Path.GetFullPath(Root), Comparison) &&
                string.Equals(Path.GetDirectoryName(Root), temp, Comparison) && name.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Refusing cleanup outside the owned SDK resume fixture.");
        }
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return;
            Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "SDK fixture root became a reparse point.");
            foreach (var store in new[] { One, Two })
            {
                if (!Directory.Exists(store)) continue;
                Check(Path.IsPathFullyQualified(store) && string.Equals(store, Path.GetFullPath(store), Comparison) &&
                    string.Equals(Path.GetDirectoryName(store), Root, Comparison) && (File.GetAttributes(store) & FileAttributes.ReparsePoint) == 0, "SDK store escaped its owned parent.");
                foreach (var path in Directory.EnumerateFileSystemEntries(store))
                {
                    Check(Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), Comparison) && string.Equals(Path.GetDirectoryName(path), store, Comparison) &&
                        (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "SDK cleanup encountered an unowned directory, path or reparse point.");
                    File.Delete(path);
                }
                Directory.Delete(store, recursive: false);
            }
            Directory.Delete(Root, recursive: false);
        }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    {
        public bool HoldClose { get; set; }
        public List<Storage> Opened { get; } = [];
        public readonly TaskCompletionSource CloseEntered = Gate(), CloseRelease = Gate();
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        { var storage = new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), this); Opened.Add(storage); return storage; }
    }
    private sealed class Storage(ISessionLogStorage inner, StorageFactory owner) : ISessionLogStorage
    {
        public int DisposeCalls { get; private set; } public bool Closed { get; private set; }
        public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes); public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk(); public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync()
        { DisposeCalls++; await inner.DisposeAsync(); Closed = true; owner.CloseEntered.TrySetResult(); if (owner.HoldClose) await owner.CloseRelease.Task.WaitAsync(Bound); }
    }
    // Only schedules callbacks and scope cleanup. All captures, action admission, validation and file
    // operations go through the actual native broker and the unchanged registry-supplied validator.
    private sealed class ObservedProvider(NativeSessionSnapshotProvider inner) : IExtensionSessionCatalogProvider, IExtensionSessionOpaqueViewProvider
    {
        public ExtensionSessionSnapshot? Staged { get; private set; }
        public bool HoldValidation { get; set; } public bool HoldTargetScopeClose { get; set; }
        public int TargetCloseCalls { get; private set; }
        public readonly TaskCompletionSource ValidationEntered = Gate(), ValidationRelease = Gate(), TargetScopeClosed = Gate(), TargetScopeRelease = Gate();
        public ExtensionSessionSnapshot? Capture(IExtensionContext context) => inner.Capture(context);
        public IExtensionSessionActionScope OpenScope(IExtensionContext context, ExtensionSessionSnapshot snapshot) => new Scope((IExtensionSessionCatalogScope)inner.OpenScope(context, snapshot), this);
        private async ValueTask Validate(ExtensionSessionSnapshot snapshot, CancellationToken token, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> policy)
        { Staged = snapshot; await policy(snapshot, token); ValidationEntered.TrySetResult(); if (HoldValidation) await ValidationRelease.Task.WaitAsync(Bound, token); }
        private sealed class Scope(IExtensionSessionCatalogScope actual, ObservedProvider owner) : IExtensionSessionCatalogScope
        {
            public ExtensionSessionSnapshot Snapshot => actual.Snapshot; public CancellationToken SessionCancellationToken => actual.SessionCancellationToken;
            public ValueTask<ExtensionSessionEntryAcknowledgment> AppendAsync(string kind, int version, JsonData data, CancellationToken token) => actual.AppendAsync(kind, version, data, token);
            public ValueTask<ExtensionSessionCatalogPage> ListAsync(ExtensionSessionCatalogQuery query, CancellationToken token) => actual.ListAsync(query, token);
            public ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, CancellationToken token) => actual.SwitchAsync(path, latest, leaf, token);
            public async ValueTask<IExtensionSessionActionScope?> SwitchAsync(string path, bool latest, string? leaf, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> policy, CancellationToken token)
            { var result = await actual.SwitchAsync(path, latest, leaf, (snapshot, stagedToken) => owner.Validate(snapshot, stagedToken, policy), token); return result is null ? null : new Scope((IExtensionSessionCatalogScope)result, owner); }
            public ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string key, bool latest, string? leaf, CancellationToken token) => actual.ResumeAsync(key, latest, leaf, token);
            public async ValueTask<IExtensionSessionCatalogScope?> ResumeAsync(string key, bool latest, string? leaf, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> policy, CancellationToken token)
            { var result = await actual.ResumeAsync(key, latest, leaf, (snapshot, stagedToken) => owner.Validate(snapshot, stagedToken, policy), token); return result is null ? null : new Scope(result, owner); }
            public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request, CancellationToken token) => actual.CreateAsync(request, token);
            public ValueTask<ExtensionSessionCreationScopeResult?> CreateAsync(ExtensionSessionCreationRequest request, Func<ExtensionSessionSnapshot, CancellationToken, ValueTask> policy, CancellationToken token) => actual.CreateAsync(request, policy, token);
            public async ValueTask DisposeAsync()
            {
                await actual.DisposeAsync();
                if (Snapshot.Generation > 1)
                { owner.TargetCloseCalls++; owner.TargetScopeClosed.TrySetResult(); if (owner.HoldTargetScopeClose) await owner.TargetScopeRelease.Task.WaitAsync(Bound); }
            }
        }
    }
}
