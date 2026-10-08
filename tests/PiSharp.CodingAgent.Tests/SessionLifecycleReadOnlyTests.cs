using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;
using PiSharp.Extensions;
using PiSharp.Cli.Extensions;

internal static class SessionLifecycleReadOnlyTests
{
    private static readonly ModelDescriptor Model = new("read-only", "openai-responses", "fixture");
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Timestamp = "2026-10-02T00:00:00.000Z";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-lifecycle-read-only borrowed active capture and snapshot export preserve authority and branch state", BorrowedCapture),
        ("session-lifecycle-read-only future damaged and unavailable projections retain explicit inspection data", InspectionDistinctions),
        ("session-lifecycle-read-only import and whole export disclose formats and preserve all fields and source bytes", WholeCopies),
        ("session-lifecycle-read-only selected export preserves original identity and reports exclusions separately from clone", SelectedExport),
        ("session-lifecycle-read-only selected legacy export retains migration receipts without pretending exact conversion", LegacyExport),
        ("session-lifecycle-read-only canceled readers and exports join physical cleanup and late publication keeps receipt", CancellationAndPublication),
        ("session-lifecycle-read-only disabled extension export import reload preserves sibling state and hidden context", DisabledStateAndHiddenContext)
    ];

    private static async Task BorrowedCapture()
    {
        using var f = new Fixture(); var source = await Bytes(f.Files.Source); var provider = new NoTransport(); var ids = 0;
        var runtime = new SessionRuntimeRegistry([new(Model, provider)], [], new NoPolicy());
        await using var session = await PersistentAgentSession.OpenWithRegistryAsync(f.Files.Source, runtime, () => 0,
            () => "append-" + ++ids, new(UseLatestLeaf: false, SelectedLeafId: "left-state"), Model);
        await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("Read-only capture cannot replace a session."));
        var before = owner.Current; var view = f.Facade.Capture(owner);
        Equal(1L, view.AttachmentGeneration); Equal("left-state", view.SelectedLeafId); Equal("name-info", view.PhysicalLeafId);
        Equal(6, view.Entries.Length); Equal("Left label", view.Tree.GetLabel("left-state")!.Label); Equal("Name", view.Tree.SessionName);
        Check(StateValues(view).SequenceEqual(new[] { "base", "left" }) && view.Context.LlmMessages.IsEmpty, "State-only capture leaked physical siblings or executable model input.");
        Equal("original", view.SessionId); Equal("../missing parent.jsonl", view.Header.WireBody.Value.GetProperty("parentSession").GetString());
        var right = f.Facade.SelectBranch(view, "right-state"); Check(StateValues(right).SequenceEqual(new[] { "base", "right" }), "Pure branch selection failed to restore sibling state.");
        Check(f.Facade.SelectBranch(view, null).BranchEntries.IsEmpty, "Pure empty-root selection chose a physical tail.");
        Equal(0, ids); Equal(0, f.IO.OpenedReads); SameBytes(source, await Bytes(f.Files.Source));
        var exported = await f.Facade.ExportSelectedBranchAsync(view, f.Files.Path("captured.jsonl"));
        Check(exported.Copy.Published && ReferenceEquals(view, exported.SourceView), "Borrowed snapshot export did not return its captured source view.");
        Equal(3, exported.OmittedRecords); Equal(0, exported.OmittedFields); Equal(0, f.IO.OpenedReads); Equal(0, ids);
        var receipt = await owner.AppendExtensionEntryAsync(before, new("fixture", "state", 1, JsonData.Parse("{\"value\":\"continued\"}")));
        Check(receipt.Append.ByteLength > 0 && !session.Snapshot.IsRetired && !session.Snapshot.IsDisposed, "Facade borrowed or retired live writer authority.");
        Equal(6, view.Entries.Length); Equal("left-state", view.SelectedLeafId); Equal(1L, owner.Current.Generation);
        await owner.DisposeAsync();
        await using var reopened = await SessionLogStore.OpenAsync(f.Files.Path("captured.jsonl"));
        Equal("original", reopened.Snapshot.Header.Id); Equal(3, reopened.Snapshot.Entries.Length);
        Equal("1.00e400", reopened.Snapshot.Entries[0].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText());
        Equal(0, provider.Calls); f.NoTemps();
        await MemoryCapture(f, view, runtime, provider);
    }

    private static async Task MemoryCapture(Fixture fixture, SessionLifecycleReadOnlyView seed,
        SessionRuntimeRegistry runtime, NoTransport provider)
    {
        var directory = fixture.Files.Path("uncreated-memory");
        var backend = new SessionStorageBackend(directory, SessionStorageMode.InMemory);
        var path = Path.Combine(directory, "source.jsonl"); var exportedPath = Path.Combine(directory, "selected.jsonl");
        var options = new SessionLogStoreOptions(StorageFactory: backend);
        await using (var store = await SessionLogStore.CreateNewAsync(path, seed.Header, options))
            await store.AppendAsync(seed.Entries);
        await using var session = await PersistentAgentSession.OpenWithRegistryAsync(path, runtime, () => 0,
            () => "memory-append", new(UseLatestLeaf: false, SelectedLeafId: "left-state", SessionLogStoreOptions: options), Model);
        await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("Memory capture cannot replace a session."));
        var catalog = new SessionCatalog([new("memory", directory)], fileSystem: backend);
        var facade = new SessionLifecycleReadOnly(fileSystem: backend, catalog: catalog); var view = facade.Capture(owner);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); await Throws<OperationCanceledException>(() => facade.ListAsync(new(), canceled.Token));
        }
        var page = await facade.ListAsync(new()); Equal(1, page.Items.Length); Equal(path, page.Items[0].Path);
        Equal("original", page.Items[0].SessionId); Equal("../missing parent.jsonl", page.Items[0].ParentSessionPath);
        Equal(1, (await catalog.ListAsync(new())).Items.Length);
        await Throws<InvalidOperationException>(() => fixture.Facade.ListAsync(new()));
        var exported = await facade.ExportSelectedBranchAsync(view, exportedPath);
        Check(exported.Copy.Published && backend.FileExists(exportedPath) && !File.Exists(exportedPath) && !Directory.Exists(directory), "Memory selected export required a physical source or destination directory.");
        Equal(SessionLogStorageDurability.VolatileMemory, exported.Copy.StorageDurability); Equal(3, exported.OmittedRecords);
        var inspected = await facade.InspectAsync(exportedPath);
        Check(inspected.View is not null && StateValues(inspected.View).SequenceEqual(new[] { "base", "left" }), "Memory publication failed to retain captured selected state.");
        var wholePath = Path.Combine(directory, "whole.jsonl");
        var whole = await facade.ExportAsync(new(path, wholePath));
        Check(whole.Published && backend.FileExists(wholePath), "Whole memory export bypassed its configured namespace.");
        Equal(SessionLogStorageDurability.VolatileMemory, whole.StorageDurability);
        var collision = await Throws<SessionCopyException>(() => facade.ExportSelectedBranchAsync(view, exportedPath));
        Equal(SessionCopyFailure.InvalidRequest, collision.Failure);
        await owner.AppendExtensionEntryAsync(owner.Current, new("fixture", "state", 1, JsonData.Parse("{\"value\":\"memory-continued\"}")));
        await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
        await using var reopened = await SessionLogStore.OpenAsync(exportedPath, options);
        Equal(3, reopened.Snapshot.Entries.Length); Equal("1.00e400", reopened.Snapshot.Entries[0].WireBody.Value.GetProperty("data").GetProperty("opaque").GetRawText());
        Equal(0, provider.Calls); Check(!Directory.Exists(directory), "Memory lifecycle capture or cleanup materialized a namespace.");
    }

    private static async Task InspectionDistinctions()
    {
        using var f = new Fixture(); var original = await Bytes(f.Files.Source);
        var ready = await f.Facade.InspectAsync(f.Files.Source, useLatestLeaf: false, selectedLeafId: "left-state");
        Equal(SessionLifecycleInspectionStatus.Available, ready.Status); Check(ready.View is not null && ready.Tree is not null, "Complete selected inspection lost its immutable tree or context.");
        Equal(Hash(original), ready.SourceSha256); Check(ready.OriginalBytes.AsSpan().SequenceEqual(original), "Inspection did not own original physical bytes.");
        var missing = await f.Facade.InspectAsync(f.Files.Source, useLatestLeaf: false, selectedLeafId: "missing");
        Equal(SessionLifecycleInspectionStatus.ProjectionUnavailable, missing.Status); Equal(PiSharp.Sessions.Context.SessionContextProjectionFailure.MissingLeaf, missing.ContextFailure);
        Check(missing.View is null && missing.Tree is not null && missing.CopyInspection.CanPublishCurrent, "Unavailable leaf guessed a runnable context or discarded the complete forest.");
        foreach (var text in new[]
        {
            Header(f.Files.Root).Replace("\"version\":3", "\"version\":4") + "\n" + Future("r", null) + "\n",
            Header(f.Files.Root) + "\n" + Future("r", null) + "\n{broken}\n" + Future("child", "lost") + "\n",
            Header(f.Files.Root) + "\n" + Future("r", "missing") + "\n"
        })
        {
            await File.WriteAllTextAsync(f.Files.Source, text); var bytes = await Bytes(f.Files.Source);
            var inspected = await f.Facade.InspectAsync(f.Files.Source);
            Equal(SessionLifecycleInspectionStatus.InspectionOnly, inspected.Status); Check(inspected.View is null && inspected.Tree is null && !inspected.CopyInspection.CanPublishCurrent, "Future/damaged data became an available active view.");
            Check(inspected.OriginalBytes.AsSpan().SequenceEqual(bytes) && inspected.CopyInspection.Diagnostics.Length != 0, "Unsafe inspection lost raw bytes or its blocking diagnostics.");
            var blocked = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("blocked.jsonl")));
            Equal(SessionCopyStatus.Blocked, blocked.Copy.Status); Check(!blocked.SelectionApplied && !File.Exists(blocked.Copy.DestinationPath), "Blocked inspection published selected data.");
            SameBytes(bytes, await Bytes(f.Files.Source));
        }
        var unsupported = Header(f.Files.Root) + "\n{\"type\":\"compaction\",\"id\":\"alien\",\"parentId\":null,\"timestamp\":\"" + Timestamp + "\",\"summary\":\"retained\",\"firstKeptEntryId\":\"\",\"tokensBefore\":0,\"systemMessage\":{\"role\":\"alien\",\"payload\":1.00e400}}\n";
        await File.WriteAllTextAsync(f.Files.Source, unsupported);
        var unavailable = await f.Facade.InspectAsync(f.Files.Source);
        Equal(SessionLifecycleInspectionStatus.ProjectionUnavailable, unavailable.Status); Check(unavailable.View is null && unavailable.Tree is not null, "Unsupported selected compaction message meaning was guessed.");
        var root = await f.Facade.InspectAsync(f.Files.Source, useLatestLeaf: false); Equal(SessionLifecycleInspectionStatus.Available, root.Status); Check(root.View!.Context.Messages.IsEmpty, "Empty root projected unsupported sibling data.");
        var raw = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("unsupported-data.jsonl")));
        Check(raw.Copy.Published && raw.OmittedFields == 0, "Unsupported executable projection prevented safe raw record export."); f.NoTemps();
    }

    private static async Task WholeCopies()
    {
        using var f = new Fixture(); var original = await Bytes(f.Files.Source);
        var imported = await f.Facade.ImportAsync(new(f.Files.Source, f.Files.Path("imported.jsonl")));
        Equal(SessionCopyFormat.NativeExact, imported.Format); Check(imported.Published, "Native import did not publish a fresh copy.");
        Equal(Hash(original), imported.Inspection.SourceSha256); Equal(Hash(original), imported.OutputSha256); SameBytes(original, await Bytes(imported.DestinationPath));
        var compatible = await f.Facade.ExportAsync(new(f.Files.Source, f.Files.Path("compatible.jsonl"), SessionCopyFormat.CompatibleCurrentJsonl));
        Check(compatible.Published && compatible.Diagnostics.Any(item => item.Code == SessionCopyDiagnosticCode.SemanticCompatibilityUnverified), "Compatible export hid its unverified semantic boundary.");
        Equal(0, compatible.OmittedRecords); Equal(0, compatible.OmittedFields);
        var log = await new SessionLogReader().ReadFileAsync(compatible.DestinationPath);
        Equal(7, log.ValidatedPrefix.Length); Equal("../missing parent.jsonl", log.Header!.WireBody.Value.GetProperty("parentSession").GetString());
        Equal("1.00e400", log.Header!.WireBody.Value.GetProperty("metadata").GetProperty("opaque").GetRawText());
        var data = log.ValidatedPrefix[1].Entry.WireBody.Value.GetProperty("data");
        Equal("Ignored.Type", data.GetProperty("native").GetProperty("$type").GetString()); Equal("unopened:/secret", data.GetProperty("image").GetString());
        Equal("1.00e400", data.GetProperty("opaque").GetRawText()); Equal("1.2300e+2", log.ValidatedPrefix[3].Entry.WireBody.Value.GetProperty("scaled").GetRawText());
        await Throws<SessionCopyException>(() => f.Facade.ExportAsync(new(f.Files.Source, imported.DestinationPath)));
        SameBytes(original, await Bytes(imported.DestinationPath)); SameBytes(original, await Bytes(f.Files.Source)); f.NoTemps();
    }

    private static async Task DisabledStateAndHiddenContext()
    {
        foreach (var mode in new SessionStorageMode?[] { null, SessionStorageMode.InMemory, SessionStorageMode.LazyLocal })
        {
            using var f = new Fixture();
            var backend = mode is null ? null : new SessionStorageBackend(f.Files.Root, mode.Value);
            var source = f.Files.Path("disabled-source.jsonl");
            var provider = new NoTransport(); var runtime = new SessionRuntimeRegistry([new(Model, provider)], [], new NoPolicy());
            var lifecycle = new PersistentSessionLifecycle(runtime, () => 0, () => "unused-generated-id", backend: backend);
            var codec = new SessionEntryCodec();
            string Entry(string type, string id, string? parent, string body) => "{\"type\":" + JsonSerializer.Serialize(type) +
                ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
                ",\"timestamp\":\"" + Timestamp + "\"," + body + "}";
            string StateBody(string value) => "\"customType\":\"pisharp.extension-state\",\"unrecognized\":null,\"data\":{\"extensionId\":\"disabled\",\"entryKind\":\"state\",\"schemaVersion\":2,\"data\":{\"value\":" +
                JsonSerializer.Serialize(value) + ",\"opaque\":1.00e400,\"marker\":\"private-state-only\",\"nil\":null}}";
            var entries = new[]
            {
                Entry("custom", "base", null, StateBody("base")),
                Entry("custom_message", "hidden", "base", "\"customType\":\"hidden\",\"content\":\"hidden context\",\"display\":false,\"details\":{\"opaque\":1.00e400}"),
                Entry("message", "left-user", "hidden", "\"message\":{\"role\":\"user\",\"content\":\"left branch\",\"timestamp\":0}"),
                Entry("custom", "left-state", "left-user", StateBody("left")),
                Entry("message", "right-user", "hidden", "\"message\":{\"role\":\"user\",\"content\":\"right branch\",\"timestamp\":0}"),
                Entry("custom", "right-state", "right-user", StateBody("right"))
            }.Select(codec.Parse).ToImmutableArray();
            await using (var store = await SessionLogStore.CreateNewAsync(source, codec.Parse(Header(f.Files.Root)), lifecycle.Options.SessionLogStoreOptions))
                await store.AppendAsync(entries);
            var selectedPath = f.Files.Path("disabled-selected.jsonl"); var importedPath = f.Files.Path("disabled-imported.jsonl");
            var selected = await lifecycle.ReadOnly.ExportSelectedBranchAsync(new(source, selectedPath, UseLatestLeaf: false, SelectedLeafId: "left-state"));
            Check(selected.Copy.Published && selected.ExcludedEntryIds.SequenceEqual(new[] { "right-user", "right-state" }) && selected.OmittedFields == 0,
                "Disabled extension selected export changed retained data or hid sibling exclusions.");
            var imported = await lifecycle.ReadOnly.ImportAsync(new(selectedPath, importedPath));
            Check(imported.Published && imported.OutputSha256 == selected.Copy.OutputSha256, "Disabled selected export/import changed complete record bytes.");
            await using var restored = await lifecycle.OpenAsync(new(importedPath), Model);
            await using var owner = lifecycle.Attach(restored); var snapshots = new NativeSessionSnapshotProvider(); snapshots.Attach(owner);
            var disabled = new ReadStateContext(snapshots, owner.Current);
            Equal(ExtensionSessionStateFailure.IncompatibleVersion,
                (await Throws<ExtensionSessionStateException>(() => Task.FromResult(disabled.ReadLatest("state")))).Failure);
            var state = disabled.ReadLatest("state", 2)!; Equal("left", state.Data.Value.GetProperty("value").GetString());
            Equal("1.00e400", state.Data.Value.GetProperty("opaque").GetRawText()); Equal("left-state", state.EntryId);
            var context = restored.Snapshot.Context;
            Check(context.Messages.Any(message => message.Role == "custom" && !message.WireBody.Value.GetProperty("display").GetBoolean()), "Hidden custom message was lost on disabled reload.");
            Check(context.LlmMessages.Length == 2 && context.LlmMessages[0].WireBody.ToString().Contains("hidden context", StringComparison.Ordinal) &&
                !context.LlmMessages.Any(message => message.WireBody.ToString().Contains("private-state-only", StringComparison.Ordinal) ||
                    message.WireBody.ToString().Contains("right branch", StringComparison.Ordinal)), "Reload leaked state or sibling data or omitted hidden context.");
            var right = await lifecycle.ReadOnly.InspectAsync(source, useLatestLeaf: false, selectedLeafId: "right-state");
            Check(right.View is not null && right.View.Context.LlmMessages.Any(message => message.WireBody.ToString().Contains("right branch", StringComparison.Ordinal)), "Export changed the source sibling branch.");
            Equal(0, provider.Calls); await owner.DisposeAsync();
            if (backend is not null) Equal(0, backend.ActiveWriterCount);
            if (mode == SessionStorageMode.InMemory) Check(!File.Exists(source) && !File.Exists(selectedPath) && !File.Exists(importedPath), "Disabled memory export materialized a log.");
        }
    }
    private sealed class ReadStateContext(NativeSessionSnapshotProvider provider, AgentSessionAttachment attachment) : IExtensionSessionContext
    {
        public string OwnerId => "disabled"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => CancellationToken.None;
        public CancellationToken SessionCancellationToken => attachment.LifetimeToken;
        public CancellationToken ExtensionLifetimeCancellationToken => CancellationToken.None;
        public ExtensionSessionSnapshot? SessionSnapshot => provider.Capture(this);
    }

    private static async Task SelectedExport()
    {
        using var f = new Fixture(); var original = await Bytes(f.Files.Source);
        var selected = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("branch.jsonl"), UseLatestLeaf: false, SelectedLeafId: "left-state"));
        Check(selected.Copy.Published && selected.SelectionApplied && !selected.WholeSourceBytesPreserved, "Selected export pretended to be a whole-source exact copy.");
        Equal(SessionCopyFormat.NativeExact, selected.Copy.Format); Equal("left-state", selected.SelectedLeafId); Equal(3, selected.OmittedRecords); Equal(3, selected.Copy.OmittedRecords); Equal(0, selected.OmittedFields);
        Check(selected.ExcludedEntryIds.SequenceEqual(new[] { "right-state", "global-label", "name-info" }), "Selected export silently excluded records or resolved metadata.");
        var expected = new MemoryStream();
        foreach (var record in selected.SourceInspection.CopyInspection.Log.ValidatedPrefix.Where(record => record.Entry.IsHeader || new[] { "root", "base-state", "left-state" }.Contains(record.Entry.Id)))
        {
            var start = record.Entry.IsHeader ? 0 : record.ByteOffset; var length = record.ByteOffset + record.PhysicalByteLength - start;
            expected.Write(original.AsSpan(start, length));
        }
        SameBytes(expected.ToArray(), await Bytes(selected.Copy.DestinationPath)); expected.Dispose();
        await using (var exported = await SessionLogStore.OpenAsync(selected.Copy.DestinationPath))
        {
            Equal("original", exported.Snapshot.Header.Id); Equal("left-state", exported.Snapshot.LeafId); Equal(3, exported.Snapshot.Entries.Length);
            Equal("../missing parent.jsonl", exported.Snapshot.Header.WireBody.Value.GetProperty("parentSession").GetString());
            var source = selected.SourceInspection.View!;
            var clone = new SessionBranchPlanner().Fork(new(source.Header, source.Entries, "left-state", SessionForkPosition.At,
                "fresh-clone", Timestamp, f.Files.Source, ["new-label"]));
            Equal("fresh-clone", clone.Header.Id); Equal(f.Files.Source, clone.Header.WireBody.Value.GetProperty("parentSession").GetString());
            Check(clone.Entries.Any(entry => entry.Id == "new-label") && !exported.Snapshot.Entries.Any(entry => entry.Id == "new-label"), "Selected export acquired clone identity or recreated label entries.");
        }
        var compatible = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("branch-compatible.jsonl"), SessionCopyFormat.CompatibleCurrentJsonl, false, "left-state"));
        Check(compatible.Copy.Published && compatible.ExcludedEntryIds.SequenceEqual(selected.ExcludedEntryIds), "Compatible selected export changed its scope or hid exclusions.");
        var root = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("root.jsonl"), UseLatestLeaf: false));
        Equal(6, root.OmittedRecords); var rootLog = await new SessionLogReader().ReadFileAsync(root.Copy.DestinationPath); Equal(1, rootLog.ValidatedPrefix.Length);
        await Throws<SessionTreeQueryException>(() => f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("missing.jsonl"), UseLatestLeaf: false, SelectedLeafId: "missing")));
        Check(!File.Exists(f.Files.Path("missing.jsonl")), "Unknown selected entry acquired file effects."); SameBytes(original, await Bytes(f.Files.Source)); f.NoTemps();
    }

    private static async Task LegacyExport()
    {
        using var f = new Fixture();
        var legacy = Header(f.Files.Root).Replace("\"version\":3", "\"version\":2") + "\n{\"type\":\"message\",\"id\":\"hook\",\"parentId\":null,\"timestamp\":\"" + Timestamp + "\",\"message\":{\"role\":\"hookMessage\",\"customType\":\"state\",\"timestamp\":0,\"content\":\"retained\",\"display\":false,\"details\":{\"opaque\":1.00e400}}}\n";
        await File.WriteAllTextAsync(f.Files.Source, legacy); var original = await Bytes(f.Files.Source);
        var exported = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("legacy-compatible.jsonl"), SessionCopyFormat.CompatibleCurrentJsonl));
        Check(exported.Copy.Published, "Explicit compatible legacy branch did not publish."); Equal(2L, exported.SourceInspection.CopyInspection.SourceVersion);
        Check(exported.SourceInspection.CopyInspection.Migration!.Receipts.Any(receipt => receipt.Transform == SessionEntryMigrationTransform.HookRole), "Selected export lost original migration receipts.");
        var output = await new SessionLogReader().ReadFileAsync(exported.Copy.DestinationPath); var message = output.ValidatedPrefix[1].Entry.WireBody.Value.GetProperty("message");
        Equal("custom", message.GetProperty("role").GetString()); Equal("1.00e400", message.GetProperty("details").GetProperty("opaque").GetRawText()); Equal(0, exported.OmittedFields);
        var exact = await f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("legacy-exact.jsonl")));
        Equal(SessionCopyStatus.Blocked, exact.Copy.Status); Check(exact.Copy.Diagnostics.Any(item => item.Code == SessionCopyDiagnosticCode.ExactCopyRequiresCurrentVersion) && !exact.SelectionApplied && !File.Exists(exact.Copy.DestinationPath), "Legacy conversion pretended to retain original current-version bytes.");
        SameBytes(original, await Bytes(f.Files.Source)); f.NoTemps();
    }

    private static async Task CancellationAndPublication()
    {
        using (var f = new Fixture())
        using (var caller = new CancellationTokenSource())
        {
            var original = await Bytes(f.Files.Source); f.IO.HoldRead = true; f.IO.HoldClose = true;
            var read = f.Facade.InspectAsync(f.Files.Source, cancellationToken: caller.Token);
            try
            {
                await f.IO.ReadEntered.Task.WaitAsync(Bound); caller.Cancel(); await f.IO.CloseEntered.Task.WaitAsync(Bound);
                Check(!read.IsCompleted && !f.IO.LastClosed, "Canceled inspection escaped its actual held reader close.");
                f.IO.CloseRelease.TrySetResult(); await Throws<SessionCopyCanceledException>(() => read); Equal(0, f.IO.Created);
                using (var exclusive = new FileStream(f.Files.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                SameBytes(original, await Bytes(f.Files.Source));
            }
            finally { f.IO.ReleaseAll(); await Drain(read); }
        }
        using (var f = new Fixture())
        using (var caller = new CancellationTokenSource())
        {
            var original = await Bytes(f.Files.Source); f.IO.HoldFlush = true; f.IO.HoldClose = true; f.IO.HoldDelete = true;
            var copy = f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("canceled.jsonl"), UseLatestLeaf: false, SelectedLeafId: "left-state"), caller.Token);
            try
            {
                await f.IO.FlushEntered.Task.WaitAsync(Bound); caller.Cancel(); await f.IO.CloseEntered.Task.WaitAsync(Bound);
                Check(!copy.IsCompleted && File.Exists(f.IO.Temporary), "Canceled branch export escaped its actual temporary close.");
                f.IO.CloseRelease.TrySetResult(); await f.IO.DeleteEntered.Task.WaitAsync(Bound);
                Check(!copy.IsCompleted && File.Exists(f.IO.Temporary) && f.IO.LastClosed, "Canceled branch export escaped owned temporary deletion.");
                f.IO.DeleteRelease.TrySetResult(); var error = await Throws<SessionCopyCanceledException>(() => copy);
                Check(!error.TemporaryMayRemain && error.CleanupFailures.IsEmpty && !File.Exists(f.Files.Path("canceled.jsonl")), "Canceled branch export lost its cleanup disposition.");
                SameBytes(original, await Bytes(f.Files.Source)); f.NoTemps();
            }
            finally { f.IO.ReleaseAll(); await Drain(copy); }
        }
        using (var f = new Fixture())
        using (var caller = new CancellationTokenSource())
        {
            var original = await Bytes(f.Files.Source); f.IO.HoldPublish = true;
            var copy = f.Facade.ExportSelectedBranchAsync(new(f.Files.Source, f.Files.Path("late.jsonl"), UseLatestLeaf: false, SelectedLeafId: "left-state"), caller.Token);
            try
            {
                await f.IO.PublishEntered.Task.WaitAsync(Bound); caller.Cancel(); Check(!copy.IsCompleted, "Late cancellation detached an admitted publication.");
                f.IO.PublishRelease.TrySetResult(); var result = await copy.WaitAsync(Bound);
                Check(result.Copy.Published && File.Exists(result.Copy.DestinationPath) && result.Copy.OutputSha256 == Hash(await Bytes(result.Copy.DestinationPath)), "Late publication cancellation discarded its known receipt.");
                SameBytes(original, await Bytes(f.Files.Source)); f.NoTemps();
            }
            finally { f.IO.ReleaseAll(); await Drain(copy); }
        }
    }

    private static IEnumerable<string> StateValues(SessionLifecycleReadOnlyView view) => view.BranchEntries.Where(entry => entry.Kind == SessionEntryKind.Custom)
        .Select(entry => entry.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("value").GetString()!);
    private static string Header(string cwd) => "{\"type\":\"session\",\"version\":3,\"id\":\"original\",\"timestamp\":\"" + Timestamp + "\",\"cwd\":" + JsonSerializer.Serialize(cwd) + ",\"parentSession\":\"../missing parent.jsonl\",\"metadata\":{\"opaque\":1.00e400,\"nil\":null}}";
    private static string Future(string id, string? parent) => "{\"type\":\"future\",\"id\":\"" + id + "\",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Timestamp + "\",\"data\":{\"opaque\":1.00e400,\"native\":{\"$type\":\"Ignored.Type\"},\"image\":\"unopened:/secret\"}}";
    private static string State(string id, string parent, string value) => "{\"type\":\"custom\",\"customType\":\"pisharp.extension-state\",\"id\":\"" + id + "\",\"parentId\":\"" + parent + "\",\"timestamp\":\"" + Timestamp + "\",\"scaled\":1.2300e+2,\"data\":{\"extensionId\":\"fixture\",\"entryKind\":\"state\",\"schemaVersion\":1,\"data\":{\"value\":\"" + value + "\",\"nil\":null}}}";
    private static async Task<byte[]> Bytes(string path) { await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var bytes = new MemoryStream(); await file.CopyToAsync(bytes); return bytes.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void SameBytes(byte[] before, byte[] after) => Check(before.AsSpan().SequenceEqual(after), "Facade changed original physical bytes.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task task) { try { await task.WaitAsync(Bound); } catch (Exception) when (task.IsCompleted) { } }
    private sealed class Fixture : IDisposable
    {
        public Files Files { get; } = new(); public GuardFiles IO { get; } public SessionLifecycleReadOnly Facade { get; }
        public Fixture()
        {
            IO = new(Files); Facade = new(fileSystem: IO);
            var label = "{\"type\":\"label\",\"id\":\"global-label\",\"parentId\":\"right-state\",\"timestamp\":\"" + Timestamp + "\",\"targetId\":\"left-state\",\"label\":\"Left label\",\"native\":null}";
            var name = "{\"type\":\"session_info\",\"id\":\"name-info\",\"parentId\":\"global-label\",\"timestamp\":\"" + Timestamp + "\",\"name\":\" Name \"}";
            File.WriteAllText(Files.Source, "\n \t\n" + Header(Files.Root) + "\r\n" + Future("root", null) + "\n" + State("base-state", "root", "base") + "\r\n" + State("left-state", "base-state", "left") + "\n" + State("right-state", "base-state", "right") + "\r\n" + label + "\n" + name);
        }
        public void NoTemps() => Check(!Directory.EnumerateFiles(Files.Root, ".pisharp-copy-*.tmp").Any(), "Facade retained an owned copy temporary.");
        public void Dispose() { IO.ReleaseAll(); Files.Dispose(); }
    }
    private sealed class Files : IDisposable
    {
        private const string Prefix = "PiSharp-readonly-lifecycle-"; private readonly string temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public string Root { get; } public string Source => Path("source.jsonl");
        public Files() { Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"))); ValidateRoot(); Directory.CreateDirectory(Root); }
        public string Path(string name) { var path = System.IO.Path.Combine(Root, name); Validate(path); return path; }
        public void Validate(string path) => Check(System.IO.Path.IsPathFullyQualified(path) && string.Equals(path, System.IO.Path.GetFullPath(path), Comparison) && string.Equals(System.IO.Path.GetDirectoryName(path), Root, Comparison), "Facade fixture path escaped its canonical owned root.");
        private void ValidateRoot() { var name = System.IO.Path.GetFileName(Root); Check(System.IO.Path.IsPathFullyQualified(Root) && string.Equals(System.IO.Path.GetDirectoryName(Root), temp, Comparison) && name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Refusing cleanup outside owned lifecycle fixture."); }
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return; Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Lifecycle fixture root became a link.");
            foreach (var path in Directory.EnumerateFileSystemEntries(Root)) { Validate(path); Check((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Lifecycle cleanup encountered an unowned directory or link."); File.Delete(path); }
            Directory.Delete(Root, recursive: false);
        }
    }
    private sealed class GuardFiles(Files files) : ISessionCopyFileSystem
    {
        public int OpenedReads { get; private set; } public int Created { get; private set; } public string? Temporary { get; private set; }
        public bool HoldRead, HoldFlush, HoldClose, HoldDelete, HoldPublish, LastClosed;
        public readonly TaskCompletionSource ReadEntered = Gate(), ReadRelease = Gate(), FlushEntered = Gate(), FlushRelease = Gate(), CloseEntered = Gate(), CloseRelease = Gate(),
            DeleteEntered = Gate(), DeleteRelease = Gate(), PublishEntered = Gate(), PublishRelease = Gate();
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken token)
        {
            files.Validate(path); Equal(files.Source, path); token.ThrowIfCancellationRequested(); OpenedReads++;
            Stream actual = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
            return ValueTask.FromResult(HoldRead ? (Stream)new HeldStream(actual, this, false) : actual);
        }
        public async ValueTask<Stream> CreateNewTemporaryAsync(string path)
        {
            files.Validate(path); Check(System.IO.Path.GetFileName(path).StartsWith(".pisharp-copy-", StringComparison.Ordinal), "Facade did not use the existing copy publisher's temporary ownership.");
            Created++; Temporary = path; var actual = await SessionCopyService.LocalFileSystem.CreateNewTemporaryAsync(path);
            return HoldFlush ? new HeldStream(actual, this, true) : actual;
        }
        public async ValueTask PublishNewAsync(string temporary, string destination)
        {
            files.Validate(temporary); files.Validate(destination); Equal(Temporary, temporary); PublishEntered.TrySetResult();
            if (HoldPublish) await PublishRelease.Task.WaitAsync(Bound); await SessionCopyService.LocalFileSystem.PublishNewAsync(temporary, destination);
        }
        public async ValueTask DeleteTemporaryAsync(string path)
        {
            files.Validate(path); Equal(Temporary, path); using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            DeleteEntered.TrySetResult(); if (HoldDelete) await DeleteRelease.Task.WaitAsync(Bound); await SessionCopyService.LocalFileSystem.DeleteTemporaryAsync(path);
        }
        public void ReleaseAll() { ReadRelease.TrySetResult(); FlushRelease.TrySetResult(); CloseRelease.TrySetResult(); DeleteRelease.TrySetResult(); PublishRelease.TrySetResult(); }
    }
    private sealed class HeldStream(Stream actual, GuardFiles owner, bool temporary) : Stream
    {
        public override bool CanRead => actual.CanRead; public override bool CanWrite => actual.CanWrite; public override bool CanSeek => false;
        public override long Length => actual.Length; public override long Position { get => actual.Position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { owner.ReadEntered.TrySetResult(); if (owner.HoldRead) await owner.ReadRelease.Task.WaitAsync(Bound, token); return await actual.ReadAsync(buffer, token); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => actual.WriteAsync(buffer, token);
        public override async Task FlushAsync(CancellationToken token) { owner.FlushEntered.TrySetResult(); if (owner.HoldFlush) await owner.FlushRelease.Task.WaitAsync(Bound, token); await actual.FlushAsync(token); }
        public override async ValueTask DisposeAsync() { owner.CloseEntered.TrySetResult(); if (owner.HoldClose && (temporary || owner.HoldRead)) await owner.CloseRelease.Task.WaitAsync(Bound); await actual.DisposeAsync(); owner.LastClosed = true; GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class NoTransport : IChatTransport
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default) { Calls++; await Task.FromException(new InvalidOperationException("Read-only lifecycle must not acquire a provider.")); yield break; }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Read-only lifecycle must not acquire a tool."); }
}
