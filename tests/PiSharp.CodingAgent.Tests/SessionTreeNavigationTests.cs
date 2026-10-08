using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionTreeNavigationTests
{
    public const string Prefix = "session-tree-navigation ";
    private static readonly ModelDescriptor A = new("a", "openai-responses", "navigation-fixture");
    private static readonly ModelDescriptor B = new("b", "openai-responses", "navigation-fixture");
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "same file selected context native messages model and tools without append", SameFile),
        (Prefix + "current user leaf no-op precedes parent resolution and veto", CurrentLeaf),
        (Prefix + "canonical user custom-message text parent and null root", EditorSemantics),
        (Prefix + "opaque revision rejects ABA changed log foreign session and old attachment", StaleAuthority),
        (Prefix + "veto caller cancellation owned abort and foreign failures retain original state", Cancellation),
        (Prefix + "held callback joins original work through owner close and rejects self-waits", CloseAndReentrancy),
        (Prefix + "next durable append uses selected parent and storage failure retains facts", DurableContinuation),
        (Prefix + "memory and lazy navigation never materialize files", VirtualStorage),
        (Prefix + "pending queues and active input reject selection publication", Admission),
        (Prefix + "steering and followup admission preserve queues durable state and revision", QueueAdmissionPreservation),
        (Prefix + "late steering and followup prevent navigation publication without mutation", LateQueuePublicationPreservation),
        (Prefix + "unknown branch loadout rejects before preflight and canonical editor ignores context edits", ProjectionAdmission)
    ];

    private static async Task SameFile()
    {
        await using var f = await Fixture.Open();
        var expected = f.Owner.Current; var view = f.Owner.CaptureTree(expected); var bytes = await f.Bytes();
        var log = f.Session.Snapshot.Log;
        Equal("right-tail", view.LeafId); Equal(B, f.Session.Snapshot.Agent.Model);
        Equal("write", string.Join(",", f.Session.GetActiveTools()));
        var result = await f.Owner.NavigateTreeAsync(expected, new("left-system", view.Revision), beforeTree: (preview, _) =>
        {
            Equal("base-model", preview.CommonAncestorId);
            Check(preview.AbandonedEntries.Select(entry => entry.Id).SequenceEqual(
                new[] { "right-model", "right-system", "right-user", "right-assistant", "right-tail" }), "Abandoned raw branch differs.");
            Equal("left-system", preview.NewLeafId); Equal(A, preview.Configuration.Model);
            return ValueTask.FromResult(true);
        });
        Equal(SessionTreeNavigationDisposition.Selected, result.Disposition);
        Equal("left-system", result.LeafId); Equal(A, result.Agent.Model);
        Equal("read", string.Join(",", f.Session.GetActiveTools()));
        Check(ReferenceEquals(expected, result.View.Attachment) && ReferenceEquals(expected, f.Owner.Current), "Navigation replaced attachment authority.");
        Equal(1L, result.View.Generation); Equal(f.Path, result.View.Path); Equal(null, result.EditorText);
        SameMessages(SessionContextProjector.AgentMessages(result.Context), result.Agent.Messages);
        Check(ReferenceEquals(view.Tree, result.View.Tree) && view.LeafId == "right-tail", "Captured tree/view was mutated.");
        Check(ReferenceEquals(log, f.Session.Snapshot.Log), "Navigation fabricated a log checkpoint.");
        var afterBytes = await f.Bytes();
        Check(bytes.AsSpan().SequenceEqual(afterBytes), "No-summary navigation changed durable bytes.");
        Equal(0, f.Transport.Calls); Equal(0, f.Ids); Equal(1, Directory.GetFiles(f.Root).Length);
    }

    private static async Task CurrentLeaf()
    {
        await using var f = await Fixture.Open(selected: "left-user");
        var before = f.Session.Snapshot; var view = f.Owner.CaptureTree(f.Owner.Current); var calls = 0;
        var result = await f.Owner.NavigateTreeAsync(view.Attachment, new("left-user", view.Revision), beforeTree: (_, _) =>
        { calls++; return ValueTask.FromResult(false); });
        Check(result.NoOp && !result.Cancelled && result.EditorText is null, "Current user leaf was resolved to parent or editor text.");
        Equal(0, calls); Equal("left-user", result.LeafId);
        Check(ReferenceEquals(before.Context, f.Session.Snapshot.Context) && ReferenceEquals(before.Log, f.Session.Snapshot.Log), "No-op changed state.");
        SameMessages(before.Agent.Messages, result.Agent.Messages);
    }

    private static async Task EditorSemantics()
    {
        await using var f = await Fixture.Open();
        var result = await Select(f, "left-user");
        Equal("left-system", result.LeafId); Equal("leftdraft", result.EditorText);
        result = await Select(f, "left-custom");
        Equal("left-user", result.LeafId); Equal("hiddenprompt", result.EditorText);
        result = await Select(f, "left-empty");
        Equal("left-user", result.LeafId); Equal("", result.EditorText);
        Check(!result.NoOp, "Different target with the same resolved parent incorrectly short-circuited.");
        result = await Select(f, "right-assistant");
        Equal("right-assistant", result.LeafId); Equal(null, result.EditorText);
        result = await Select(f, "root-user");
        Equal(null, result.LeafId); Equal("first", result.EditorText); Equal(0, result.Context.Ancestry.Length);
        Equal(0, result.Agent.Messages.Length); Equal(0, f.Session.GetActiveTools().Length);
        var root = await Select(f, null); Check(root.NoOp && root.EditorText is null, "Root no-op guessed a physical leaf.");
        result = await Select(f, "right-tail");
        Equal("right-tail", result.LeafId); Equal(null, result.EditorText); Equal(B, result.Agent.Model);
        Equal(0, f.Transport.Calls); Equal(0, f.Ids);
    }

    private static async Task StaleAuthority()
    {
        await using var f = await Fixture.Open(); var original = f.Owner.CaptureTree(f.Owner.Current);
        var forged = original.Attachment with { Generation = original.Generation + 1 };
        await Throws<InvalidOperationException>(() => f.Owner.NavigateTreeAsync(forged, new("left-system", original.Revision)));
        await Select(f, "left-system"); await Select(f, "right-tail");
        Equal(SessionTreeNavigationFailure.StaleSelection, (await Throws<SessionTreeNavigationException>(() =>
            f.Owner.NavigateTreeAsync(original.Attachment, new("left-system", original.Revision)))).Failure);
        var changed = f.Owner.CaptureTree(f.Owner.Current);
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("navigation", "state", 1, JsonData.EmptyObject));
        Equal(SessionTreeNavigationFailure.StaleSelection, (await Throws<SessionTreeNavigationException>(() =>
            f.Owner.NavigateTreeAsync(changed.Attachment, new("left-system", changed.Revision)))).Failure);
        await using var foreign = await Fixture.Open(); var other = foreign.Owner.CaptureTree(foreign.Owner.Current);
        Equal(SessionTreeNavigationFailure.StaleSelection, (await Throws<SessionTreeNavigationException>(() =>
            f.Owner.NavigateTreeAsync(f.Owner.Current, new("left-system", other.Revision)))).Failure);
        var current = f.Owner.CaptureTree(f.Owner.Current);
        Equal(SessionTreeNavigationFailure.UnknownTarget, (await Throws<SessionTreeNavigationException>(() =>
            f.Owner.NavigateTreeAsync(current.Attachment, new("missing", current.Revision)))).Failure);
        Equal(SessionTreeNavigationFailure.InvalidRequest, (await Throws<SessionTreeNavigationException>(() =>
            f.Owner.NavigateTreeAsync(current.Attachment, new("", current.Revision)))).Failure);
        await foreign.Owner.DisposeAsync();
        var target = global::System.IO.Path.Combine(f.Root, "replacement.jsonl");
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
            id = "navigation-replacement", timestamp = "2026-10-03T00:00:00.000Z", cwd = f.Root }));
        await using (var store = await SessionLogStore.CreateNewAsync(target, header)) await store.AppendAsync(Seed());
        await f.Owner.SwitchAsync(current.Attachment, new(target));
        await Throws<InvalidOperationException>(() => f.Owner.NavigateTreeAsync(current.Attachment, new("left-system", current.Revision)));
        Check(f.Owner.Current.Generation == 2 && !ReferenceEquals(f.Owner.Current, current.Attachment), "Switch did not retire old attachment.");
        await f.Owner.DisposeAsync(); // Release the target writer before its owning fixture deletes the workspace.
    }

    private static async Task Cancellation()
    {
        await using var f = await Fixture.Open(); var before = f.Session.Snapshot; var bytes = await f.Bytes();
        var cancellationFailure = new IOException("navigation cancellation callback failed");
        var view = f.Owner.CaptureTree(f.Owner.Current);
        var veto = await f.Owner.NavigateTreeAsync(view.Attachment, new("left-user", view.Revision), beforeTree: (_, _) => ValueTask.FromResult(false));
        Equal(SessionTreeNavigationDisposition.Vetoed, veto.Disposition); Equal(null, veto.EditorText);
        foreach (var kind in new[] { "caller", "abort", "foreign", "late-io", "abort-callback-failure" })
        {
            using var caller = new CancellationTokenSource(); using var foreign = new CancellationTokenSource(); foreign.Cancel();
            var entered = Gate(); var release = Gate(); CancellationToken callbackToken = default;
            var run = f.Owner.NavigateTreeAsync(view.Attachment, new("left-user", view.Revision), caller.Token, async (_, token) =>
            {
                using var registration = kind == "abort-callback-failure"
                    ? token.Register(() => throw cancellationFailure) : default;
                if (kind == "abort-callback-failure") f.ExpectedCleanupFailure = cancellationFailure;
                callbackToken = token; entered.TrySetResult(); await release.Task;
                if (kind == "foreign") throw new OperationCanceledException(foreign.Token);
                if (kind == "late-io") throw new IOException("navigation held callback failed");
                token.ThrowIfCancellationRequested(); return true;
            });
            try
            {
                await entered.Task;
                if (kind == "caller" || kind == "late-io") caller.Cancel(); else Check(f.Owner.Current.Session.Abort(), "Navigation Abort was not owned.");
                Check(callbackToken.IsCancellationRequested && !run.IsCompleted, "Cancellation detached original callback.");
                Check(ReferenceEquals(before.Context, f.Session.Snapshot.Context), "Prepublication cancellation changed context.");
                release.TrySetResult();
                if (kind is "abort" or "abort-callback-failure")
                {
                    var receipt = await run; Check(receipt.Aborted, "Owned abort did not retain explicit disposition.");
                    Equal(kind == "abort-callback-failure", receipt.CancellationCallbackFailed);
                }
                else if (kind == "late-io") await Throws<IOException>(() => run);
                else
                {
                    var error = await Throws<OperationCanceledException>(() => run);
                    if (kind == "foreign") Equal(foreign.Token, error.CancellationToken);
                }
            }
            finally { release.TrySetResult(); await Join(run); }
        }
        Check(ReferenceEquals(before.Context, f.Session.Snapshot.Context) && ReferenceEquals(before.Log, f.Session.Snapshot.Log), "Cancellation/veto changed authoritative state.");
        var afterBytes = await f.Bytes();
        Check(bytes.AsSpan().SequenceEqual(afterBytes), "Cancellation changed durable bytes.");
        await Select(f, "left-system"); // The old writer/context remains usable.
    }

    private static async Task CloseAndReentrancy()
    {
        await using var f = await Fixture.Open(); var view = f.Owner.CaptureTree(f.Owner.Current);
        await f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision), beforeTree: async (_, _) =>
        {
            await Throws<InvalidOperationException>(() => f.Owner.NavigateTreeAsync(view.Attachment, new("right-tail", view.Revision)));
            await Throws<InvalidOperationException>(() => f.Session.WaitForIdleAsync());
            await Throws<InvalidOperationException>(() => f.Owner.DisposeAsync().AsTask());
            return false;
        });
        var entered = Gate(); var release = Gate(); var cancellationSeen = Gate();
        var run = f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision), beforeTree: async (_, token) =>
        {
            using var registration = token.Register(() => cancellationSeen.TrySetResult());
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return true;
        });
        Task? close = null;
        try
        {
            await entered.Task; close = f.Owner.DisposeAsync().AsTask(); await cancellationSeen.Task;
            Check(!close.IsCompleted && !run.IsCompleted, "Owner close detached reserved navigation callback.");
            release.TrySetResult(); await Throws<OperationCanceledException>(() => run); await close;
            Check(f.Session.Snapshot.IsDisposed, "Owner close did not join session disposal.");
        }
        finally { release.TrySetResult(); await Join(run); if (close is not null) await Join(close); }
    }

    private static async Task DurableContinuation()
    {
        await using var f = await Fixture.Open(); var before = f.Session.Snapshot.Log;
        await Select(f, "left-system");
        var receipt = await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("navigation", "state", 1, JsonData.EmptyObject));
        Equal("left-system", receipt.Entry.ParentId); Check(receipt.Append.DurableCheckpointAcknowledged, "Continuation did not acknowledge actual disk append.");
        Equal(before.Entries.Length + 1, receipt.Append.Snapshot.Entries.Length);
        Check(receipt.Append.Snapshot.ById.ContainsKey("right-tail"), "Navigation discarded physical sibling records.");
        var root = await Select(f, "root-user"); Equal(null, root.LeafId);
        receipt = await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, new("navigation", "state", 1, JsonData.EmptyObject));
        Equal(null, receipt.Entry.ParentId);
        await f.Owner.DisposeAsync();
        await using var disk = await SessionLogStore.OpenAsync(f.Path);
        Equal(receipt.Entry.Id, disk.Snapshot.LeafId); Equal(null, disk.Snapshot.ById[receipt.Entry.Id].ParentId);
        Check(disk.Snapshot.ById.ContainsKey("right-tail"), "Reopen lost abandoned durable branch.");

        await using var failed = await Fixture.Open(); await Select(failed, "left-system");
        failed.Storage!.FailWrite = true;
        var failure = await Throws<PersistentAgentSessionException>(() => failed.Owner.AppendExtensionEntryAsync(failed.Owner.Current,
            new("navigation", "state", 1, JsonData.EmptyObject)));
        Check(failure.Fault.Failure == PersistentAgentSessionFailure.AppendFailed && failure.Fault.MayHaveWritten,
            "Continuation storage failure lost uncertain-write facts.");
        Equal("left-system", failed.Session.Snapshot.Context.LeafId);
    }

    private static async Task VirtualStorage()
    {
        foreach (var mode in new[] { SessionStorageMode.InMemory, SessionStorageMode.LazyLocal })
        {
            await using var f = await Fixture.Open(mode: mode, setupOnly: true);
            Check(!File.Exists(f.Path), "Fixture unexpectedly materialized setup-only storage.");
            var result = await Select(f, "base-model"); Equal("base-model", result.LeafId);
            result = await Select(f, null); Equal(null, result.LeafId);
            Check(!File.Exists(f.Path) && f.Ids == 0 && f.Transport.Calls == 0, "Navigation materialized virtual storage or consumed effects.");
            await f.Owner.DisposeAsync(); Equal(0, f.Backend!.ActiveWriterCount);
        }
    }

    private static async Task Admission()
    {
        await using var f = await Fixture.Open(); var view = f.Owner.CaptureTree(f.Owner.Current);
        f.Session.FollowUp(User("queued"));
        await Throws<PersistentAgentSessionException>(() => f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision)));
        f.Session.ClearPendingInputQueues();
        var entered = Gate(); var release = Gate();
        var run = f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision), beforeTree: async (_, _) =>
        { entered.TrySetResult(); await release.Task; return true; });
        try
        {
            await entered.Task;
            await Throws<InvalidOperationException>(() => f.Session.PromptAsync(User("busy")));
            await Throws<InvalidOperationException>(() => f.Session.ConfigureAsync(new() { Model = A }));
            f.Session.FollowUp(User("late queued")); release.TrySetResult();
            await Throws<InvalidOperationException>(() => run);
            Equal("right-tail", f.Session.Snapshot.Context.LeafId);
            Equal(1, f.Session.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
        }
        finally { release.TrySetResult(); await Join(run); f.Session.ClearPendingInputQueues(); }
        await Select(f, "left-system"); Equal(0, f.Transport.Calls);
    }

    private static async Task QueueAdmissionPreservation()
    {
        foreach (var steering in new[] { true, false })
        {
            await using var f = await Fixture.Open(); var view = f.Owner.CaptureTree(f.Owner.Current);
            if (steering) { f.Session.Steer(User("first")); f.Session.Steer(User("second")); }
            else { f.Session.FollowUp(User("first")); f.Session.FollowUp(User("second")); }
            var queues = f.Session.GetPendingInputQueueSnapshot();
            var before = f.Session.Snapshot; var selection = f.Session.GetToolActivationSelection(); var bytes = await f.Bytes();
            await Throws<PersistentAgentSessionException>(() => f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision)));
            await QueueNavigationUnchanged(f, before, selection, queues, bytes);
            Check(f.Session.TryClearPendingInputQueues(queues, out var removed) && removed is not null,
                "Rejected admission mutated the authoritative queue revision.");
            var result = await f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision));
            Equal("left-system", result.LeafId); Equal(0, f.Transport.Calls);
        }
    }

    private static async Task LateQueuePublicationPreservation()
    {
        foreach (var steering in new[] { true, false })
        {
            await using var f = await Fixture.Open(); var view = f.Owner.CaptureTree(f.Owner.Current);
            var before = f.Session.Snapshot; var selection = f.Session.GetToolActivationSelection(); var bytes = await f.Bytes();
            var entered = Gate(); var release = Gate();
            var run = f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision), beforeTree: async (_, _) =>
            { entered.TrySetResult(); await release.Task; return true; });
            AgentPendingInputQueueSnapshot? queues = null;
            try
            {
                if (await Task.WhenAny(entered.Task, run) == run && !entered.Task.IsCompleted)
                { await run; throw new InvalidOperationException("Navigation completed before held callback."); }
                await entered.Task;
                if (steering) { f.Session.Steer(User("late first")); f.Session.Steer(User("late second")); }
                else { f.Session.FollowUp(User("late first")); f.Session.FollowUp(User("late second")); }
                queues = f.Session.GetPendingInputQueueSnapshot();
                Check(!run.IsCompleted, "Late queue admission detached the reserved navigation.");
                release.TrySetResult(); await Throws<InvalidOperationException>(() => run);
                await QueueNavigationUnchanged(f, before, selection, queues, bytes);
            }
            finally { release.TrySetResult(); await Join(run); }
            var retained = queues ?? throw new InvalidOperationException("Late queue snapshot was not captured.");
            Check(f.Session.TryClearPendingInputQueues(retained, out var removed) && removed is not null,
                "Rejected publication mutated the authoritative queue revision.");
            var result = await f.Owner.NavigateTreeAsync(view.Attachment, new("left-system", view.Revision));
            Equal("left-system", result.LeafId); Equal(0, f.Transport.Calls);
        }
    }

    private static async Task QueueNavigationUnchanged(Fixture f, PersistentAgentSessionSnapshot before,
        SessionToolActivationSelection selection, AgentPendingInputQueueSnapshot expected, byte[] bytes)
    {
        var after = f.Session.Snapshot; var queues = f.Session.GetPendingInputQueueSnapshot();
        Check(expected.SteeringMessages.SequenceEqual(queues.SteeringMessages) && expected.FollowUpMessages.SequenceEqual(queues.FollowUpMessages) &&
            expected.SteeringMode == queues.SteeringMode && expected.FollowUpMode == queues.FollowUpMode,
            "Navigation changed queued contents, FIFO order or drain modes.");
        Check(ReferenceEquals(before.Context, after.Context) && ReferenceEquals(before.Log, after.Log) &&
            before.Agent.Generation == after.Agent.Generation && !after.IsCompacting,
            "Rejected navigation changed durable leaf/context/history or retained reservation.");
        Equal(before.Context.LeafId, after.Context.LeafId);
        var currentSelection = f.Session.GetToolActivationSelection();
        Check(selection.Revision == currentSelection.Revision && selection.Names.SequenceEqual(currentSelection.Names) &&
            before.Agent.Tools.Select(tool => tool.Name).SequenceEqual(after.Agent.Tools.Select(tool => tool.Name)),
            "Navigation changed loadout or activation revision.");
        var durableBytesAfterRejection = await f.Bytes();
        Check(bytes.SequenceEqual(durableBytesAfterRejection), "Rejected navigation changed durable bytes.");
    }

    private static async Task ProjectionAdmission()
    {
        await using var unavailable = await Fixture.Open(registerRead: false);
        var before = unavailable.Session.Snapshot; var view = unavailable.Owner.CaptureTree(unavailable.Owner.Current); var callbacks = 0;
        var error = await Throws<SessionRuntimeRegistryException>(() => unavailable.Owner.NavigateTreeAsync(view.Attachment,
            new("left-system", view.Revision), beforeTree: (_, _) => { callbacks++; return ValueTask.FromResult(true); }));
        Equal(SessionRuntimeRegistryFailure.UnknownTool, error.Failure); Equal(0, callbacks);
        Check(ReferenceEquals(before.Context, unavailable.Session.Snapshot.Context) && ReferenceEquals(before.Log, unavailable.Session.Snapshot.Log),
            "Unsupported selected loadout changed state.");
        await using var f = await Fixture.Open();
        await Select(f, "left-custom");
        var edit = await f.Owner.AppendContextEditAsync(f.Owner.Current,
            new("left-user", JsonData.Parse("{\"content\":\"edited projected text\"}")));
        Check(edit.Append.DurableCheckpointAcknowledged, "Context edit was not real durable state.");
        await Select(f, "right-tail");
        var result = await Select(f, "left-user");
        Equal("leftdraft", result.EditorText); Equal("left-system", result.LeafId);
        Equal(0, f.Transport.Calls);
    }

    private static Task<SessionTreeNavigationReceipt> Select(Fixture fixture, string? target)
    {
        var view = fixture.Owner.CaptureTree(fixture.Owner.Current);
        return fixture.Owner.NavigateTreeAsync(view.Attachment, new(target, view.Revision));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(), "pisharp-navigation-" + Guid.NewGuid().ToString("N"));
        public string Path => global::System.IO.Path.Combine(Root, "session.jsonl");
        public readonly NoTransport Transport = new();
        public PersistentAgentSession Session = null!;
        public ReplaceableAgentSession Owner = null!;
        public Exception? ExpectedCleanupFailure;
        public SessionStorageBackend? Backend;
        public ObservedStorage? Storage;
        public int Ids;
        public static async Task<Fixture> Open(string selected = "right-tail", SessionStorageMode? mode = null, bool setupOnly = false,
            bool registerRead = true)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            try
            {
                f.Backend = mode is { } m ? new(f.Root, m) : null;
                var factory = new ObservedFactory(f, (ISessionLogStorageFactory?)f.Backend ?? SessionLogStore.DefaultStorageFactory);
                var options = new SessionLogStoreOptions(StorageFactory: factory);
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                    id = "navigation-session", timestamp = "2026-10-03T00:00:00.000Z", cwd = f.Root }));
                var entries = setupOnly ? ImmutableArray.Create(Record("model_change", "base-model", null, new { provider = A.Provider, modelId = A.Id })) : Seed();
                await using (var store = await SessionLogStore.CreateNewAsync(f.Path, header, options)) await store.AppendAsync(entries);
                ImmutableArray<SessionRegisteredTool> tools = registerRead
                    ? [new(Declaration("read"), new Adapter("read")), new(Declaration("write"), new Adapter("write"))]
                    : [new(Declaration("write"), new Adapter("write"))];
                var registry = new SessionRuntimeRegistry([new(A, f.Transport), new(B, f.Transport)], tools, new Policy());
                f.Session = await PersistentAgentSession.OpenWithRegistryAsync(f.Path, registry, () => 123,
                    () => "navigation-append-" + ++f.Ids,
                    new(UseLatestLeaf: false, SelectedLeafId: setupOnly ? "base-model" : selected, SessionLogStoreOptions: options), A);
                f.Owner = new(f.Session, (request, token) => PersistentAgentSession.OpenWithRegistryAsync(request.Path, registry,
                    () => 123, () => "replacement-" + Guid.NewGuid().ToString("N"),
                    new(UseLatestLeaf: request.UseLatestLeaf, SelectedLeafId: request.SelectedLeafId), A, token));
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<byte[]> Bytes()
        {
            await using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var buffer = new MemoryStream(); await file.CopyToAsync(buffer); return buffer.ToArray();
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Owner is not null) await Owner.DisposeAsync(); else if (Session is not null) await Session.DisposeAsync();
                Check(ExpectedCleanupFailure is null, "Expected cancellation cleanup failure became success.");
            }
            catch (AggregateException retained) when (ExpectedCleanupFailure is { } original)
            {
                Check(OnlyOriginalCancellationCause(retained, original) && Session.Snapshot.IsDisposed,
                    "Joined navigation disposal lost or masked its original cancellation callback failure.");
            }
            finally { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        }
    }

    private static ImmutableArray<SessionEntry> Seed() =>
    [
        Record("message", "root-user", null, new { message = User("first").WireBody.Value }),
        Record("model_change", "base-model", "root-user", new { provider = A.Provider, modelId = A.Id }),
        Record("message", "left-system", "base-model", new { message = System("read").WireBody.Value }),
        Record("message", "left-user", "left-system", new { message = new { role = "user", content = new[] {
            new { type = "text", text = "left" }, new { type = "text", text = "draft" } }, timestamp = 123 } }),
        Record("custom_message", "left-custom", "left-user", new { customType = "navigation-hidden", display = false,
            content = new[] { new { type = "text", text = "hidden" }, new { type = "text", text = "prompt" } } }),
        Record("custom_message", "left-empty", "left-user", new { customType = "navigation-empty", display = false, content = "" }),
        Record("model_change", "right-model", "base-model", new { provider = B.Provider, modelId = B.Id }),
        Record("message", "right-system", "right-model", new { message = System("write").WireBody.Value }),
        Record("message", "right-user", "right-system", new { message = User("right").WireBody.Value }),
        Record("message", "right-assistant", "right-user", new { message = PiWireJson.WriteMessage(new(
            B.Api, B.Provider, B.Id, 123, [new TextContent("answer")], TokenUsage.Zero, StopReason.Stop)).Value }),
        Record("custom", "right-tail", "right-assistant", new { customType = "navigation-state", data = new { value = "right" } })
    ];
    private static SessionEntry Record(string type, string id, string? parent, object fields)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject(); writer.WriteString("type", type); writer.WriteString("id", id);
            writer.WriteString("parentId", parent); writer.WriteString("timestamp", "2026-10-03T00:00:00.000Z");
            foreach (var field in JsonSerializer.SerializeToElement(fields).EnumerateObject()) field.WriteTo(writer);
            writer.WriteEndObject();
        }
        return new SessionEntryCodec().ParseUtf8(bytes.ToArray());
    }
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 123 })));
    private static TranscriptEntry System(string tool) => new("system", JsonData.Parse(JsonSerializer.Serialize(new
        { role = "system", content = "", timestamp = 123, toolsAdded = new[] { Declaration(tool).Value } })));
    private static JsonData Declaration(string tool) => JsonData.Parse(JsonSerializer.Serialize(new { name = tool, description = tool, parameters = new { type = "object" } }));
    internal sealed class NoTransport : IChatTransport
    {
        public int Calls;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { Calls++; await Task.FromException(new InvalidOperationException("Navigation must not invoke a provider.")); yield break; }
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Navigation cannot authorize tools."); }
    private sealed class Adapter(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class ObservedFactory(Fixture fixture, ISessionLogStorageFactory inner) : ISessionLogStorageFactory
    {
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) =>
            fixture.Storage = new ObservedStorage(await inner.OpenAsync(path, createNew, token));
    }
    internal sealed class ObservedStorage(ISessionLogStorage inner) : ISessionLogStorage
    {
        public bool FailWrite;
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => FailWrite ? ValueTask.FromException(new IOException("navigation continuation failure")) : inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk();
        public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool OnlyOriginalCancellationCause(Exception error, Exception original) => ReferenceEquals(error, original) ||
        error is AggregateException { InnerExceptions.Count: > 0 } aggregate && aggregate.InnerExceptions.All(inner => OnlyOriginalCancellationCause(inner, original)) ||
        error is PersistentAgentSessionException { InnerException: { } cause } && OnlyOriginalCancellationCause(cause, original);
    private static async Task Join(Task original) { try { await original; } catch (Exception) { } }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void SameMessages(ImmutableArray<TranscriptEntry> expected, ImmutableArray<TranscriptEntry> actual) =>
        Check(expected.Select(entry => entry.WireBody.ToString()).SequenceEqual(actual.Select(entry => entry.WireBody.ToString())), "Native/projected messages differ.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Values differ: " + expected + " / " + actual);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
