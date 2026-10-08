using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionCreationTests
{
    private static readonly ModelDescriptor Model = new("creation", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const string Time = "2026-10-02T00:00:00.000Z";
    private const string PrivateCause = "private creation fixture cause";
    private static readonly byte[] Competitor = Encoding.UTF8.GetBytes("unproven competitor bytes\n");

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-creation durable new preserves source and attaches a fresh writable header", DurableNew),
        ("session-creation fork at and clone copy logical leaf rather than physical sibling", SelectedLeafForkAndClone),
        ("session-creation preserves opaque tokens global labels and repaired compaction references", CompactionAndLabels),
        ("session-creation fork before returns selected text including an empty root branch", ForkBeforeAndEmptyRoot),
        ("session-creation own input callback receives usable B and rejects stale A writes", OwnInputCallback),
        ("session-creation request veto and cancellation precede IDs files and source retirement", PreEffectAdmission),
        ("session-creation malformed generated identities and existing filename collisions roll back", IdentityAndCollision),
        ("session-creation target open and preflight failures close writers before known branch deletion", OpenAndPreflightRollback),
        ("session-creation per-operation preflight reserves source and actual staged target", TargetReservation),
        ("session-creation held publication cancellation and uncertainty preserve honest file ownership", PublicationFailures),
        ("session-creation postcommit notification failure retains usable committed identity", NotificationFailure),
        ("session-creation postcommit abort and disposal join held notification cleanup", PostcommitShutdown)
    ];

    private static async Task DurableNew()
    {
        await using var f = await Fixture.Open(); var source = await Bytes(f.Files.A); var original = f.Owner.Current;
        var result = await f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New, ParentSession: "explicit-parent.jsonl"));
        Check(result is not null && result.Reason == "new" && result.SelectedText is null && result.Current.Generation == 2, "New attachment receipt changed.");
        var current = f.Owner.Current; Check(ReferenceEquals(result!.Current, current) && original.Session.Snapshot.IsRetired, "New did not retire the actual source writer.");
        var log = await Inspect(current.Session.Path);
        Equal("fresh", log.Header!.Id); Equal("1970-01-01T00:00:00.000Z", log.Header.Timestamp);
        Equal(f.Files.Directory, log.Header.WireBody.Value.GetProperty("cwd").GetString());
        Equal("explicit-parent.jsonl", log.Header.WireBody.Value.GetProperty("parentSession").GetString());
        Check(log.ValidatedPrefix.Length == 1 && current.Session.Snapshot.Context.Ancestry.IsEmpty &&
            !log.Header.WireBody.Value.TryGetProperty("opaqueHeader", out _), "New copied historical records or header fields.");
        Equal(1, f.SessionIds); Equal(0, f.EntryIds); Equal(0, f.Transport.Calls);
        await f.Owner.AppendExtensionEntryAsync(current, Draft("new B checkpoint"));
        var path = current.Session.Path; await f.Owner.DisposeAsync();
        await using var reopened = await f.Reopen(path);
        Check(Customs(reopened).SequenceEqual(new[] { "new B checkpoint" }), "Independent reopen lost new-session checkpoint.");
        SameBytes(source, await Bytes(f.Files.A)); NoTemps(f.Files);
    }

    private static async Task SelectedLeafForkAndClone()
    {
        foreach (var kind in new[] { AgentSessionCreationKind.ForkAt, AgentSessionCreationKind.Clone })
        {
            await using var f = await Fixture.Open(); var original = f.Owner.Current; var source = await Bytes(f.Files.A);
            Equal("left", original.Session.Snapshot.Context.LeafId); Equal("right-tail", original.Session.Snapshot.Log.LeafId);
            var result = await f.Owner.CreateAsync(original, new(kind, kind == AgentSessionCreationKind.ForkAt ? "left" : null));
            Check(result is not null && result.Reason == "fork" && result.SelectedText is null, "Fork/clone receipt changed.");
            var current = f.Owner.Current; var log = await Inspect(current.Session.Path); var entries = Entries(log);
            Ids(["model", "thinking", "root", "opaque", "left", "generated-1"], entries);
            Equal(f.Files.A, log.Header!.WireBody.Value.GetProperty("parentSession").GetString()); Equal("fresh", log.Header.Id);
            Equal("root", entries.Single(entry => entry.Id == "opaque").ParentId);
            Equal("1.00e400", entries.Single(entry => entry.Id == "opaque").WireBody.Value.GetProperty("opaque").GetProperty("number").GetRawText());
            Equal("updated root", entries[^1].WireBody.Value.GetProperty("label").GetString());
            Check(!entries.Any(entry => entry.Id.StartsWith("right", StringComparison.Ordinal)) &&
                current.Session.Snapshot.Context.LlmMessages.All(message => !message.WireBody.ToString().Contains("physical sibling", StringComparison.Ordinal)),
                "Fork/clone copied the physical sibling instead of the selected branch.");
            await Throws<InvalidOperationException>(() => f.Owner.AppendExtensionEntryAsync(original, Draft("stale owner A")));
            await Throws<PersistentAgentSessionException>(() => original.Session.AppendExtensionEntryAsync("source", Draft("stale direct A")));
            await f.Owner.AppendExtensionEntryAsync(current, Draft("fresh branch B"));
            var path = current.Session.Path; await f.Owner.DisposeAsync();
            await using var reopened = await f.Reopen(path);
            Check(Customs(reopened).SequenceEqual(new[] { "fresh branch B" }), "Fresh branch writer or independent reopen lost B checkpoint.");
            SameBytes(source, await Bytes(f.Files.A)); Equal(0, f.Transport.Calls); NoTemps(f.Files);
        }
    }

    private static async Task CompactionAndLabels()
    {
        await using var f = await Fixture.Open(SeedKind.Compacted); var original = f.Owner.Current; var source = await Bytes(f.Files.A);
        Equal("compact", original.Session.Snapshot.Context.LeafId); Equal("right-tail", original.Session.Snapshot.Log.LeafId);
        await f.Owner.CreateAsync(original, new(AgentSessionCreationKind.Clone));
        var entries = Entries(await Inspect(f.Owner.Current.Session.Path));
        Ids(["model", "thinking", "root", "opaque", "kept", "compact", "generated-1", "generated-2"], entries);
        Equal("kept", entries.Single(entry => entry.Id == "compact").WireBody.Value.GetProperty("firstKeptEntryId").GetString());
        Equal("root", entries[^2].WireBody.Value.GetProperty("targetId").GetString());
        Equal("opaque", entries[^1].WireBody.Value.GetProperty("targetId").GetString());
        Equal("updated root", entries[^2].WireBody.Value.GetProperty("label").GetString());
        Equal("opaque title", entries[^1].WireBody.Value.GetProperty("label").GetString());
        Equal("opaque", entries.Single(entry => entry.Id == "kept").ParentId);
        var opaque = entries.Single(entry => entry.Id == "opaque").WireBody.Value.GetProperty("opaque");
        Equal("1.00e400", opaque.GetProperty("number").GetRawText()); Equal(JsonValueKind.Null, opaque.GetProperty("nil").ValueKind);
        Check(!opaque.TryGetProperty("missing", out _) && !entries.Any(entry => entry.Id is "root-label" or "opaque-label"), "Historical label identity or absent data was copied incorrectly.");
        var path = f.Owner.Current.Session.Path; await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(path);
        Check(reopened.Snapshot.Context.LlmMessages.Any(message => message.WireBody.ToString().Contains("compact summary", StringComparison.Ordinal)), "Repaired compaction did not reopen into its actual selected context.");
        SameBytes(source, await Bytes(f.Files.A)); Equal(2, f.EntryIds); NoTemps(f.Files);
    }

    private static async Task ForkBeforeAndEmptyRoot()
    {
        foreach (var root in new[] { false, true })
        {
            await using var f = await Fixture.Open(root ? SeedKind.RootOnly : SeedKind.Branched);
            var source = await Bytes(f.Files.A); var old = f.Owner.Current;
            var result = await f.Owner.CreateAsync(old, new(AgentSessionCreationKind.ForkBefore, root ? "root" : "left"));
            Equal("firstsecond", result!.SelectedText); var log = await Inspect(result.Current.Session.Path); var entries = Entries(log);
            if (root)
            {
                Check(entries.IsEmpty && result.Current.Session.Snapshot.Context.LeafId is null && result.Current.Session.Snapshot.Agent.Messages.IsEmpty,
                    "Fork before root retained source conversation or metadata.");
                Equal(0, f.EntryIds);
            }
            else
            {
                Ids(["model", "thinking", "root", "opaque", "generated-1"], entries);
                Check(entries.All(entry => entry.Id != "left"), "Before fork retained the selected user message.");
            }
            Equal(f.Files.A, log.Header!.WireBody.Value.GetProperty("parentSession").GetString());
            var path = result.Current.Session.Path; await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(path);
            Equal(entries.Length, reopened.Snapshot.Log.Entries.Length); SameBytes(source, await Bytes(f.Files.A)); NoTemps(f.Files);
        }
    }

    private static async Task OwnInputCallback()
    {
        await using var f = await Fixture.Open(); var original = f.Owner.Current; var source = await Bytes(f.Files.A);
        var input = original.Session.SubmitInputAsync(new("/fork", PromptInputSource.Rpc), new Admission(async token =>
        {
            var created = await f.Owner.CreateAsync(original, new(AgentSessionCreationKind.ForkBefore, "left"), cancellationToken: token);
            Equal("firstsecond", created!.SelectedText);
            await f.Owner.AppendExtensionEntryAsync(created.Current, Draft("own callback B"), token);
            await Throws<InvalidOperationException>(() => f.Owner.AppendExtensionEntryAsync(original, Draft("own callback stale A"), token));
            await Throws<PersistentAgentSessionException>(() => original.Session.AppendExtensionEntryAsync("source", Draft("direct callback stale A"), token));
            Check(original.Session.Snapshot.IsRetired && !created.Current.Session.Snapshot.IsDisposed, "Own callback did not receive a usable B.");
        }));
        Equal(SubmittedInputDisposition.Handled, (await input.WaitAsync(Bound)).Disposition);
        var path = f.Owner.Current.Session.Path; await f.Owner.DisposeAsync();
        Check(original.Session.Snapshot.IsDisposed, "Owner did not join the retired originating callback.");
        await using var reopened = await f.Reopen(path); Check(Customs(reopened).Contains("own callback B"), "Own callback checkpoint did not survive reopen.");
        SameBytes(source, await Bytes(f.Files.A)); Equal(0, f.Transport.Calls);
    }

    private static async Task PreEffectAdmission()
    {
        await using var f = await Fixture.Open(); var source = await Bytes(f.Files.A); var original = f.Owner.Current;
        foreach (var request in new[]
        {
            new AgentSessionCreationRequest((AgentSessionCreationKind)99), new(AgentSessionCreationKind.New, "left"),
            new(AgentSessionCreationKind.Clone, "left"), new(AgentSessionCreationKind.ForkAt),
            new(AgentSessionCreationKind.ForkBefore, "left", "forbidden-parent")
        }) await Throws<ArgumentException>(() => f.Owner.CreateAsync(original, request));
        foreach (var parent in new[] { "", " \t", new string('x', 4097), "parent\ud800", "\udfff", "\ud800x" })
            await Throws<ArgumentException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New, ParentSession: parent)));
        Equal(SessionBranchPlanFailure.MissingEntry, (await Throws<SessionBranchPlanException>(() =>
            f.Owner.CreateAsync(original, new(AgentSessionCreationKind.ForkAt, "absent")))).Failure);
        f.Owner.BeforeCreation = (_, _, _) => ValueTask.FromResult(false);
        Check(await f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New)) is null, "Pre-effect veto created an attachment.");
        f.Owner.BeforeCreation = null; using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Throws<OperationCanceledException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New), canceled.Token));
        using var during = new CancellationTokenSource();
        f.Owner.BeforeCreation = (_, _, _) => { during.Cancel(); return ValueTask.FromResult(true); };
        await Throws<OperationCanceledException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.Clone), during.Token));
        f.Owner.BeforeCreation = null;
        Equal(0, f.SessionIds); Equal(0, f.EntryIds); Equal(0, f.ClockReads); Equal(0, f.IO.Created); Equal(0, f.IO.Published);
        Check(ReferenceEquals(original, f.Owner.Current) && !original.Session.Snapshot.IsRetired && f.Files.OnlySource(), "Pre-effect admission changed source identity or files.");
        SameBytes(source, await Bytes(f.Files.A)); await f.Owner.AppendExtensionEntryAsync(original, Draft("source after admission"));
        await using var empty = await Fixture.Open(SeedKind.Empty);
        await Throws<InvalidOperationException>(() => empty.Owner.CreateAsync(empty.Owner.Current, new(AgentSessionCreationKind.Clone)));
        Equal(0, empty.SessionIds); Equal(0, empty.EntryIds); Check(empty.Files.OnlySource(), "Empty clone admission created a file.");
    }

    private static async Task IdentityAndCollision()
    {
        foreach (var id in new[] { "", "../escaped", "bad/id", "white space", "\ud800", new string('x', 129) })
        {
            await using var f = await Fixture.Open(); var source = await Bytes(f.Files.A); var original = f.Owner.Current; f.SessionIdentity = id;
            await Throws<InvalidOperationException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New)));
            Equal(1, f.SessionIds); Equal(0, f.ClockReads); Equal(0, f.IO.Created);
            Check(f.Files.OnlySource() && ReferenceEquals(f.Owner.Current, original) && !original.Session.Snapshot.IsRetired, "Malformed identity published or retired the source.");
            SameBytes(source, await Bytes(f.Files.A)); await f.Owner.AppendExtensionEntryAsync(original, Draft("source after bad ID"));
        }
        await using (var f = await Fixture.Open())
        {
            var source = await Bytes(f.Files.A); var original = f.Owner.Current;
            f.SessionIdentity = original.Session.Snapshot.Log.Header.Id;
            foreach (var kind in new[] { AgentSessionCreationKind.New, AgentSessionCreationKind.Clone })
            {
                await Throws<InvalidOperationException>(() => f.Owner.CreateAsync(original, new(kind)));
                Equal(0, f.ClockReads); Equal(0, f.EntryIds); Equal(0, f.IO.Created); Equal(0, f.IO.Published);
                Check(ReferenceEquals(f.Owner.Current, original) && !original.Session.Snapshot.IsRetired && f.Files.OnlySource(),
                    "Reused source session identity reached clock, files or source retirement.");
                SameBytes(source, await Bytes(f.Files.A));
            }
            Equal(2, f.SessionIds); await f.Owner.AppendExtensionEntryAsync(original, Draft("source after duplicate session ID"));
        }
        await using (var f = await Fixture.Open())
        {
            var original = f.Owner.Current; f.EntryIdentity = () => "\udfff";
            Equal(SessionBranchPlanFailure.InvalidRequest, (await Throws<SessionBranchPlanException>(() =>
                f.Owner.CreateAsync(original, new(AgentSessionCreationKind.Clone)))).Failure);
            Equal(1, f.SessionIds); Equal(1, f.EntryIds); Equal(0, f.IO.Created); Check(f.Files.OnlySource() && !original.Session.Snapshot.IsRetired, "Invalid label ID reached publication.");
            f.EntryIdentity = null; await f.Owner.AppendExtensionEntryAsync(original, Draft("source after bad label ID"));
        }
        await using (var f = await Fixture.Open())
        {
            var source = await Bytes(f.Files.A); var original = f.Owner.Current;
            await File.WriteAllBytesAsync(f.NewPath, Competitor);
            var error = await Throws<SessionBranchPublishException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New)));
            Equal(SessionBranchPublishFailure.InvalidRequest, error.Failure); Equal(SessionBranchPublication.NotAttempted, error.Publication);
            SameBytes(Competitor, await Bytes(f.NewPath)); SameBytes(source, await Bytes(f.Files.A)); Equal(0, f.IO.Created); Equal(0, f.IO.DeletedPaths.Count);
            Check(ReferenceEquals(f.Owner.Current, original) && !original.Session.Snapshot.IsRetired, "Collision retired the source.");
            await f.Owner.AppendExtensionEntryAsync(original, Draft("source after collision")); NoTemps(f.Files);
        }
    }

    private static async Task OpenAndPreflightRollback()
    {
        foreach (var failOpen in new[] { true, false })
        {
            var factory = new Factory { RejectDurability = failOpen };
            await using var f = await Fixture.Open(targetFactory: factory); var source = await Bytes(f.Files.A); var original = f.Owner.Current;
            PersistentAgentSession? target = null;
            if (failOpen)
            {
                var error = await Throws<SessionLogStoreException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New)));
                Equal(SessionLogStoreFailure.OpenFailed, error.Failure);
            }
            else await Throws<IOException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.ForkBefore, "left"), (staged, selectedText, _) =>
            { target = staged; Equal("firstsecond", selectedText); throw new IOException(PrivateCause); }));
            Equal(1, factory.Opened.Count); Equal(1, factory.Opened.Single().DisposeCalls);
            Check(factory.Opened.Single().Closed && (target is null || target.Snapshot.IsDisposed), "Staged rollback did not close the actual acquired writer.");
            Check(f.IO.DeletedPaths.SequenceEqual(new[] { f.NewPath }) && f.IO.ClosedWriterProofs == 1 && f.Files.OnlySource(), "Rollback deleted the wrong file or deleted before writer close.");
            Check(ReferenceEquals(f.Owner.Current, original) && !original.Session.Snapshot.IsRetired, "Staged failure retired A.");
            SameBytes(source, await Bytes(f.Files.A)); await f.Owner.AppendExtensionEntryAsync(original, Draft("source after staged failure")); NoTemps(f.Files);
        }
    }

    private static async Task TargetReservation()
    {
        await using var f = await Fixture.Open(); var original = f.Owner.Current; var source = await Bytes(f.Files.A);
        var entered = Gate(); var release = Gate(); PersistentAgentSession? target = null; var notificationWrite = false;
        f.Owner.AttachmentChanged = async replacement =>
        {
            await replacement.Current.Session.AppendExtensionEntryAsync("fresh", Draft("published target callback")); notificationWrite = true;
        };
        var creating = f.Owner.CreateAsync(original, new(AgentSessionCreationKind.ForkBefore, "left"), async (staged, text, _) =>
        { target = staged; Equal("firstsecond", text); entered.TrySetResult(); await release.Task.WaitAsync(Bound); });
        try
        {
            await entered.Task.WaitAsync(Bound); var staged = target!; var before = await Bytes(staged.Path);
            await Throws<InvalidOperationException>(() => staged.AppendExtensionEntryAsync("fresh", Draft("retained target")));
            await Throws<InvalidOperationException>(() => staged.SubmitInputAsync(new("retained input", PromptInputSource.Rpc)));
            await Throws<InvalidOperationException>(() => staged.ConfigureAsync(new()));
            await Throws<InvalidOperationException>(() => staged.DrainLoadoutDiagnosticsAsync());
            await Throws<InvalidOperationException>(() => { staged.ClearPendingInputQueues(); return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => { staged.SteeringMode = AgentPendingInputMode.All; return Task.CompletedTask; });
            await Throws<InvalidOperationException>(() => staged.DisposeAsync().AsTask());
            await Throws<InvalidOperationException>(() => original.Session.AppendExtensionEntryAsync("source", Draft("retained source")));
            await Throws<InvalidOperationException>(() => original.Session.DisposeAsync().AsTask());
            SameBytes(before, await Bytes(staged.Path)); SameBytes(source, await Bytes(f.Files.A));
            Check(!creating.IsCompleted && !staged.Snapshot.IsDisposed && !original.Session.Snapshot.IsRetired && ReferenceEquals(f.Owner.Current, original), "Preflight reservation leaked target/source authority.");
            release.TrySetResult(); var result = await creating.WaitAsync(Bound);
            Check(result is not null && result.Current.Session == staged && notificationWrite, "Target reservation did not release before the publication callback.");
            await f.Owner.AppendExtensionEntryAsync(result!.Current, Draft("fresh owner target"));
            var path = staged.Path; await f.Owner.DisposeAsync(); await using var reopened = await f.Reopen(path);
            Check(Customs(reopened).SequenceEqual(new[] { "published target callback", "fresh owner target" }), "Committed target did not own its actual writable branch."); NoTemps(f.Files);
        }
        finally { release.TrySetResult(); await Drain(creating); }
    }

    private static async Task PublicationFailures()
    {
        foreach (var mode in new[] { MoveMode.CancelHeld, MoveMode.Before, MoveMode.After })
        {
            await using var f = await Fixture.Open(); using var caller = new CancellationTokenSource();
            var original = f.Owner.Current; var source = await Bytes(f.Files.A); f.IO.Mode = mode;
            var creating = f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New), caller.Token);
            try
            {
                if (mode == MoveMode.CancelHeld)
                {
                    await f.IO.PublishEntered.Task.WaitAsync(Bound); caller.Cancel();
                    Check(!creating.IsCompleted && !File.Exists(f.NewPath) && !original.Session.Snapshot.IsRetired, "Held move cancellation detached cleanup or retired A.");
                    f.IO.PublishRelease.TrySetResult(); await Throws<OperationCanceledException>(() => creating);
                    Check(f.IO.Moved && f.IO.DeletedPaths.SequenceEqual(new[] { f.NewPath }) && f.Files.OnlySource(), "Canceled known publication lost rollback authority.");
                }
                else
                {
                    var error = await Throws<SessionBranchPublishException>(() => creating);
                    Equal(SessionBranchPublishFailure.PublishFailed, error.Failure); Equal(SessionBranchPublication.Uncertain, error.Publication);
                    Check(error.InnerException is null && !error.ToString().Contains(PrivateCause, StringComparison.Ordinal), "Publication exception exposed its I/O cause.");
                    Check(f.IO.DeletedPaths.SequenceEqual(new[] { f.IO.TemporaryPath! }) && File.Exists(f.NewPath), "Uncertain publication deleted an unproven final file.");
                    if (mode == MoveMode.Before) SameBytes(Competitor, await Bytes(f.NewPath));
                    else { Equal("fresh", (await Inspect(f.NewPath)).Header!.Id); Check(f.IO.Moved, "After-effect fixture did not actually move the local file."); }
                }
                Check(ReferenceEquals(f.Owner.Current, original) && !original.Session.Snapshot.IsRetired, "Publication failure changed committed attachment identity.");
                SameBytes(source, await Bytes(f.Files.A)); await f.Owner.AppendExtensionEntryAsync(original, Draft("source after publish failure")); NoTemps(f.Files);
            }
            finally { f.IO.PublishRelease.TrySetResult(); await Drain(creating); }
        }
    }

    private static async Task NotificationFailure()
    {
        await using var f = await Fixture.Open(); var original = f.Owner.Current; var source = await Bytes(f.Files.A);
        f.Owner.AfterReplacement = _ => throw new IOException(PrivateCause);
        var error = await Throws<AgentSessionReplacementNotificationException>(() => f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New)));
        Check(error.InnerException is IOException && ReferenceEquals(error.Replacement.Current, f.Owner.Current) &&
            error.Replacement.Reason == "new" && f.Owner.Current.Generation == 2 && original.Session.Snapshot.IsRetired, "Postcommit notification failure lost its committed receipt.");
        await f.Owner.AppendExtensionEntryAsync(f.Owner.Current, Draft("committed after notification fault"));
        var path = f.Owner.Current.Session.Path; await f.Owner.DisposeAsync();
        Check(File.Exists(path) && f.IO.DeletedPaths.Count == 0, "Notification failure rolled back a committed file.");
        await using var reopened = await f.Reopen(path); Check(Customs(reopened).Contains("committed after notification fault"), "Committed notification-fault branch could not reopen.");
        SameBytes(source, await Bytes(f.Files.A));
    }

    private static async Task PostcommitShutdown()
    {
        foreach (var disposing in new[] { false, true })
        {
            await using var f = await Fixture.Open(); var original = f.Owner.Current;
            var entered = Gate(); var cleanupEntered = Gate(); var cleanupRelease = Gate(); var notificationRelease = Gate();
            f.Owner.AfterReplacement = async replacement =>
            {
                entered.TrySetResult();
                try
                {
                    if (disposing) await notificationRelease.Task.WaitAsync(Bound, replacement.Current.LifetimeToken);
                    else await notificationRelease.Task.WaitAsync(Bound);
                }
                finally { cleanupEntered.TrySetResult(); await cleanupRelease.Task.WaitAsync(Bound); }
            };
            var creating = f.Owner.CreateAsync(original, new(AgentSessionCreationKind.New));
            Task? firstClose = null, secondClose = null;
            try
            {
                await entered.Task.WaitAsync(Bound); var committed = f.Owner.Current; var path = committed.Session.Path;
                if (disposing)
                { firstClose = f.Owner.DisposeAsync().AsTask(); secondClose = f.Owner.DisposeAsync().AsTask(); Check(ReferenceEquals(firstClose, secondClose), "Owner disposal did not share completion."); }
                else
                {
                    f.Owner.Abort();
                    Check(!creating.IsCompleted && !committed.LifetimeToken.IsCancellationRequested && ReferenceEquals(f.Owner.Current, committed),
                        "Postcommit abort detached its notification or canceled committed attachment authority.");
                    notificationRelease.TrySetResult();
                }
                await cleanupEntered.Task.WaitAsync(Bound);
                Check(!creating.IsCompleted && (firstClose is null || !firstClose.IsCompleted) && ReferenceEquals(f.Owner.Current, committed) &&
                    committed.Generation == 2 && File.Exists(path), "Shutdown detached notification cleanup or lost committed B.");
                cleanupRelease.TrySetResult();
                if (firstClose is not null)
                {
                    var error = await Throws<AgentSessionReplacementNotificationException>(() => creating);
                    Check(error.InnerException is OperationCanceledException && ReferenceEquals(error.Replacement.Current, committed), "Shutdown lost the committed cancellation receipt.");
                    await Task.WhenAll(firstClose, secondClose!).WaitAsync(Bound);
                }
                else
                {
                    var result = await creating.WaitAsync(Bound); Check(result is not null && ReferenceEquals(result.Current, committed), "Postcommit abort lost the committed success receipt.");
                    await f.Owner.AppendExtensionEntryAsync(committed, Draft("B after abort")); await f.Owner.DisposeAsync();
                }
                Check(committed.Session.Snapshot.IsDisposed && original.Session.Snapshot.IsDisposed && f.IO.DeletedPaths.Count == 0, "Owner shutdown failed to join attached sessions or deleted committed B.");
                await using var reopened = await f.Reopen(path); Equal("fresh", reopened.Snapshot.Log.Header.Id); NoTemps(f.Files);
            }
            finally
            {
                notificationRelease.TrySetResult(); cleanupRelease.TrySetResult(); await Drain(creating);
                if (firstClose is not null) await Drain(firstClose);
            }
        }
    }

    private static SessionExtensionEntryDraft Draft(string text) => new("creation.checkpoint", "checkpoint", 1,
        JsonData.Parse(JsonSerializer.Serialize(new { text, optional = (object?)null })));
    private static IEnumerable<string> Customs(PersistentAgentSession session) => session.Snapshot.Context.Ancestry
        .Where(entry => entry.Type == "custom").Select(entry => entry.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("text").GetString()!);
    private static ImmutableArray<SessionEntry> Entries(SessionLogReadResult log) => log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
    private static async Task<SessionLogReadResult> Inspect(string path)
    {
        var log = await new SessionLogReader().ReadAsync(new MemoryStream(await Bytes(path), writable: false), leaveOpen: false);
        Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete, "Independent disk inspection found an incomplete branch."); return log;
    }
    private static async Task<byte[]> Bytes(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); return bytes.ToArray();
    }
    private static void SameBytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Physical source or branch bytes changed.");
    private static void Ids(string[] expected, IEnumerable<SessionEntry> entries) => Check(expected.SequenceEqual(entries.Select(entry => entry.Id)), "Copied branch identities or order changed.");
    private static void NoTemps(Files files) => Check(!Directory.EnumerateFiles(files.Directory, ".pisharp-branch-*.tmp").Any(), "Owned creation temporary file remained.");
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task task)
    { try { await task.WaitAsync(Bound); } catch (Exception) when (task.IsCompleted) { } }

    private enum SeedKind { Branched, Compacted, RootOnly, Empty }
    private sealed class Fixture : IAsyncDisposable
    {
        public Files Files { get; } = new();
        public NoTransport Transport { get; } = new();
        public SessionRuntimeRegistry Registry { get; }
        public FaultFiles IO { get; }
        public ReplaceableAgentSession Owner { get; private set; } = null!;
        public int SessionIds { get; private set; }
        public int EntryIds { get; private set; }
        public int ClockReads { get; private set; }
        public string SessionIdentity { get; set; } = "fresh";
        public Func<string>? EntryIdentity { get; set; }
        public string NewPath => Files.File("1970-01-01T00-00-00-000Z_fresh.jsonl");
        private Fixture()
        {
            Registry = new([new(Model, Transport)], [], new NeverPolicy()); IO = new(Files);
        }
        private long Clock() { ClockReads++; return 0; }
        private string NextEntry() { EntryIds++; return EntryIdentity?.Invoke() ?? "generated-" + EntryIds; }
        private string NextSession() { SessionIds++; return SessionIdentity; }
        public static async Task<Fixture> Open(SeedKind kind = SeedKind.Branched, Factory? targetFactory = null)
        {
            var f = new Fixture(); PersistentAgentSession? source = null;
            try
            {
                var header = Codec.Parse("{\"type\":\"session\",\"version\":3,\"id\":\"source\",\"timestamp\":\"" + Time +
                    "\",\"cwd\":" + JsonSerializer.Serialize(f.Files.Directory) + ",\"parentSession\":\"older.jsonl\",\"opaqueHeader\":1e400}");
                await using (var store = await SessionLogStore.CreateNewAsync(f.Files.A, header))
                {
                    var entries = Seed(kind); if (!entries.IsEmpty) await store.AppendAsync(entries);
                }
                var leaf = kind switch { SeedKind.Empty => null, SeedKind.RootOnly => "root", SeedKind.Compacted => "compact", _ => "left" };
                source = await PersistentAgentSession.OpenWithRegistryAsync(f.Files.A, f.Registry, f.Clock, f.NextEntry,
                    new(UseLatestLeaf: false, SelectedLeafId: leaf), Model);
                var lifecycle = new PersistentSessionLifecycle(f.Registry, f.Clock, f.NextEntry,
                    targetFactory is null ? null : new(SessionLogStoreOptions: new(StorageFactory: targetFactory)), f.NextSession, f.IO);
                f.Owner = new(source, (request, token) => f.Reopen(request.Path, token: token), lifecycle);
                source = null;
                return f;
            }
            catch { if (source is not null) await source.DisposeAsync(); if (f.Owner is not null) await f.Owner.DisposeAsync(); f.Files.Dispose(); throw; }
        }
        public Task<PersistentAgentSession> Reopen(string path, CancellationToken token = default) =>
            PersistentAgentSession.OpenWithRegistryAsync(path, Registry, Clock, NextEntry, fallbackModel: Model, cancellationToken: token);
        public async ValueTask DisposeAsync()
        { try { await Owner.DisposeAsync(); Check(Transport.Calls == 0, "Creation workflow called a provider."); } finally { Files.Dispose(); } }
        private static SessionEntry Record(string type, string id, string? parent, string fields) => Codec.Parse(
            "{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
            ",\"timestamp\":\"" + Time + "\"," + fields + "}");
        private static SessionEntry User(string id, string? parent, string content) => Record("message", id, parent,
            "\"message\":{\"role\":\"user\",\"timestamp\":0,\"content\":" + content + "}");
        private static SessionEntry Label(string id, string parent, string target, string label) => Record("label", id, parent,
            "\"targetId\":" + JsonSerializer.Serialize(target) + ",\"label\":" + JsonSerializer.Serialize(label));
        private static ImmutableArray<SessionEntry> Seed(SeedKind kind)
        {
            const string selected = "[{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"text\",\"text\":\"second\"}]";
            if (kind == SeedKind.Empty) return [];
            if (kind == SeedKind.RootOnly) return [User("root", null, selected)];
            var entries = ImmutableArray.CreateBuilder<SessionEntry>();
            entries.Add(Record("model_change", "model", null, "\"provider\":\"fixture\",\"modelId\":\"creation\""));
            entries.Add(Record("thinking_level_change", "thinking", "model", "\"thinkingLevel\":\"off\""));
            entries.Add(User("root", "thinking", "\"root input\"")); entries.Add(Label("root-label", "root", "root", "old root"));
            entries.Add(Record("future_state", "opaque", "root-label", "\"opaque\":{\"number\":1.00e400,\"nil\":null,\"text\":\"π🙂\"}"));
            if (kind == SeedKind.Compacted)
            {
                entries.Add(Label("opaque-label", "opaque", "opaque", "opaque title")); entries.Add(User("kept", "opaque-label", "\"kept input\""));
                entries.Add(Record("compaction", "compact", "kept", "\"summary\":\"compact summary\",\"firstKeptEntryId\":\"opaque-label\",\"tokensBefore\":9"));
            }
            else entries.Add(User("left", "opaque", selected));
            entries.Add(User("right", "root", "\"physical sibling\"")); entries.Add(Label("updated-root", "right", "root", "updated root"));
            entries.Add(Record("future_state", "right-tail", "updated-root", "\"opaque\":{\"physical\":true}"));
            return entries.ToImmutable();
        }
    }

    private sealed class Admission(Func<CancellationToken, Task> callback) : IPromptInputAdmission
    { public async ValueTask<PromptInputDecision> ReduceAsync(PromptInput input, CancellationToken token) { await callback(token); return new(PromptInputAction.Handled); } }
    private sealed class NoTransport : IChatTransport
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; await Task.FromException(new InvalidOperationException("Creation fixtures must not call a provider.")); yield break; }
    }
    private sealed class NeverPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            throw new InvalidOperationException("Creation fixtures must not execute a tool.");
    }
    private sealed class Files : IDisposable
    {
        private const string Prefix = "PiSharp-creation-";
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Directory { get; }
        public string A => File("A.jsonl");
        public Files() { Directory = Path.GetFullPath(Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"))); ValidateRoot(); System.IO.Directory.CreateDirectory(Directory); }
        public string File(string name) { var path = Path.GetFullPath(Path.Combine(Directory, name)); ValidatePath(path); return path; }
        public void ValidatePath(string path) => Check(Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), Comparison) &&
            string.Equals(Path.GetDirectoryName(path), Directory, Comparison), "Creation fixture path escaped its absolute owned directory.");
        public bool OnlySource() => System.IO.Directory.EnumerateFileSystemEntries(Directory).SequenceEqual(new[] { A });
        private void ValidateRoot()
        {
            var name = Path.GetFileName(Directory);
            Check(Path.IsPathFullyQualified(Directory) && string.Equals(Directory, Path.GetFullPath(Directory), Comparison) &&
                string.Equals(Path.GetDirectoryName(Directory), temp, Comparison) && name.StartsWith(Prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Refusing cleanup outside the owned creation fixture.");
        }
        public void Dispose()
        {
            ValidateRoot(); if (!System.IO.Directory.Exists(Directory)) return;
            Check((System.IO.File.GetAttributes(Directory) & FileAttributes.ReparsePoint) == 0, "Creation root became a reparse point.");
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(Directory))
            {
                ValidatePath(path); Check((System.IO.File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Creation cleanup encountered an unowned directory or reparse point.");
                System.IO.File.Delete(path);
            }
            System.IO.Directory.Delete(Directory, recursive: false);
        }
    }
    private enum MoveMode { None, CancelHeld, Before, After }
    private sealed class FaultFiles(Files files) : ISessionBranchFileSystem
    {
        public MoveMode Mode { get; set; }
        public readonly TaskCompletionSource PublishEntered = Gate(), PublishRelease = Gate();
        public int Created { get; private set; }
        public int Published { get; private set; }
        public int ClosedWriterProofs { get; private set; }
        public bool Moved { get; private set; }
        public string? TemporaryPath { get; private set; }
        private string? finalPath;
        public List<string> DeletedPaths { get; } = [];
        public ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path)
        {
            files.ValidatePath(path); Check(Path.GetFileName(path).StartsWith(".pisharp-branch-", StringComparison.Ordinal) && path.EndsWith(".tmp", StringComparison.Ordinal), "Creation staged an unowned temporary filename.");
            TemporaryPath = path; Created++; return SessionBranchPublisher.LocalFileSystem.CreateNewTemporaryAsync(path);
        }
        public async ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        {
            files.ValidatePath(temporaryPath); files.ValidatePath(destinationPath); Equal(TemporaryPath, temporaryPath);
            finalPath = destinationPath; Published++; PublishEntered.TrySetResult();
            if (Mode == MoveMode.CancelHeld) await PublishRelease.Task.WaitAsync(Bound);
            if (Mode == MoveMode.Before) { await System.IO.File.WriteAllBytesAsync(destinationPath, Competitor); throw new IOException(PrivateCause); }
            await SessionBranchPublisher.LocalFileSystem.PublishNewAsync(temporaryPath, destinationPath); Moved = true;
            if (Mode == MoveMode.After) throw new IOException(PrivateCause);
        }
        public async ValueTask DeleteOwnedAsync(string path)
        {
            files.ValidatePath(path); Check(path == TemporaryPath || path == finalPath, "Creation deleted a file outside its actual staged/final authority."); DeletedPaths.Add(path);
            if (path == finalPath && System.IO.File.Exists(path))
            { using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } ClosedWriterProofs++; }
            await SessionBranchPublisher.LocalFileSystem.DeleteOwnedAsync(path);
        }
    }
    private sealed class Factory : ISessionLogStorageFactory
    {
        public bool RejectDurability { get; init; }
        public List<Storage> Opened { get; } = [];
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token)
        {
            var storage = new Storage(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token), RejectDurability);
            Opened.Add(storage); return storage;
        }
    }
    private sealed class Storage(ISessionLogStorage inner, bool rejectDurability) : ISessionLogStorage
    {
        public int DisposeCalls { get; private set; }
        public bool Closed { get; private set; }
        public Stream ReadStream => inner.ReadStream;
        public SessionLogStorageDurability Durability => rejectDurability ? SessionLogStorageDurability.Unsupported : inner.Durability;
        public long Length => inner.Length;
        public void PositionForAppend(long expectedLength) => inner.PositionForAppend(expectedLength);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync();
        public void FlushToDisk() => inner.FlushToDisk();
        public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public async ValueTask DisposeAsync() { DisposeCalls++; await inner.DisposeAsync(); Closed = true; }
    }
}
