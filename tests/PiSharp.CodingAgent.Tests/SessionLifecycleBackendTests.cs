using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionLifecycleBackendTests
{
    private static readonly ModelDescriptor Model = new("backend", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private const string Timestamp = "2026-10-02T00:00:00.000Z";
    private static readonly SessionStorageMode[] Modes = [SessionStorageMode.InMemory, SessionStorageMode.LazyLocal];
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session-lifecycle-backend initial setup and actual new retain explicit deferred or volatile checkpoints", NewAndReopen),
        ("session-lifecycle-backend actual queues defer files until admitted user checkpoint and preserve replacement admission", QueuesAndFirstConversation),
        ("session-lifecycle-backend selected in-file siblings root fork and clone retain opaque state and physical source", SelectedBranches),
        ("session-lifecycle-backend repeated actual-header cwd recreation releases fresh model tool policy services and transfers subscriptions", WorkingDirectoryRebinding),
        ("session-lifecycle-backend owned cwd services join release after veto canceled staging failed open and failed cleanup", OwnedRuntimeFailures),
        ("session-lifecycle-backend staged veto and cancellation close targets delete only owned branches and retain source usability", Rollback)
    ];

    private static async Task NewAndReopen()
    {
        foreach (var mode in Modes)
        {
            using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids(); var script = new Script();
            var lifecycle = Lifecycle(backend, ids, Registry(Model, script));
            await using var initial = await lifecycle.CreateAsync(files.A, Header("source", files.Root), Model);
            await using var owner = lifecycle.Attach(initial); var previous = owner.Current;
            await initial.ConfigureAsync(new(SystemMessage: SystemMessage("setup only")));
            var sourceState = await owner.AppendExtensionEntryAsync(previous, Draft("source"));
            Checkpoint(sourceState, Pending(mode)); Check(!File.Exists(files.A), "Header, setup or state materialized a session file.");
            var before = await Bytes(backend, files.A); var oldContext = Context(owner);
            Equal("source", oldContext.ReadLatest("state")!.Data.Value.GetProperty("value").GetString());
            Equal(Persistence(mode, false), oldContext.SessionSnapshot!.Persistence);
            var replacement = await owner.CreateAsync(previous, new(AgentSessionCreationKind.New, ParentSession: "explicit-parent.jsonl"));
            Check(replacement is not null && ReferenceEquals(replacement.Current, owner.Current) && previous.Session.Snapshot.IsRetired, "New did not attach a fresh coordinator and retire the actual source.");
            Equal("new", replacement!.Reason); Equal(2L, replacement.Current.Generation); Equal("session-1", replacement.Current.Session.Snapshot.Log.Header.Id);
            var current = replacement.Current; var header = current.Session.Snapshot.Log.Header;
            Equal("explicit-parent.jsonl", header.WireBody.Value.GetProperty("parentSession").GetString());
            Check(!header.WireBody.Value.TryGetProperty("opaque", out _) && current.Session.Snapshot.Log.Entries.IsEmpty && current.Session.Snapshot.Context.Ancestry.IsEmpty, "New inherited source data or selected state.");
            await Throws<OperationCanceledException>(() => Task.FromResult(oldContext.ReadLatest("state")));
            await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(previous, Draft("stale")));
            var receipt = await owner.AppendExtensionEntryAsync(current, Draft("fresh")); Checkpoint(receipt, Pending(mode));
            Equal(Persistence(mode, false), Context(owner).SessionSnapshot!.Persistence);
            await using (var scope = Context(owner).OpenScope())
            {
                var acknowledgment = await scope.AppendAsync("receipt", 1, JsonData.Parse("{\"value\":\"sdk acknowledgment\"}"), CancellationToken.None);
                Equal(Persistence(mode, false), acknowledgment.Persistence); Check(acknowledgment.ByteLength > 0, "Actual SDK scope omitted its acknowledged backend checkpoint.");
            }
            Check(!File.Exists(current.Session.Path) && backend.FileExists(current.Session.Path), "Fresh state checkpoint confused file durability with backend retention.");
            var page = await lifecycle.ReadOnly.ListAsync(new()); Equal(2, page.Items.Length); Equal(0, page.UnavailableStores);
            SameBytes(before, await Bytes(backend, files.A)); Equal(0, script.Requests.Count);
            var path = current.Session.Path; await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
            await using var reopened = await lifecycle.OpenAsync(new(path), Model);
            Equal("fresh", StateValue(reopened)); Equal(Pending(mode), reopened.Snapshot.Log.StorageDurability);
            await reopened.AppendExtensionEntryAsync("session-1", Draft("reopened")); Equal("reopened", StateValue(reopened));
            SameBytes(before, await Bytes(backend, files.A)); NoTemporaries(backend); Check(!Directory.EnumerateFiles(files.Root).Any(), "Setup-only lifecycle created a physical file.");
        }
    }

    private static async Task QueuesAndFirstConversation()
    {
        foreach (var mode in Modes)
        {
            using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids(); var script = new Script(holdFirst: true);
            var lifecycle = Lifecycle(backend, ids, Registry(Model, script));
            await using var initial = await lifecycle.CreateAsync(files.A, Header("source", files.Root), Model);
            await using var owner = lifecycle.Attach(initial);
            await initial.ConfigureAsync(new(SystemMessage: SystemMessage("inert setup")));
            await owner.AppendExtensionEntryAsync(owner.Current, Draft("queued state")); var setup = await Bytes(backend, files.A);
            var queued = await initial.SubmitInputAsync(new("queued idle", PromptInputSource.Rpc, StreamingBehavior: PromptInputStreamingBehavior.FollowUp),
                options: new() { QueueOnly = true });
            Equal(SubmittedInputDisposition.Queued, queued.Disposition); Equal(1, initial.Snapshot.Agent.FollowUpCount);
            await Throws<InvalidOperationException>(() => owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New)));
            Equal(0, ids.Sessions); SameBytes(setup, await Bytes(backend, files.A)); Check(!File.Exists(files.A), "Queued admission wrote a deferred file.");
            var removed = initial.ClearPendingInputQueues(); Equal(1, removed.FollowUpMessages.Length); Equal("queued idle", Text(removed.FollowUpMessages[0]));
            var running = initial.SubmitInputAsync(new("first user", PromptInputSource.Rpc));
            try
            {
                await script.Entered.Task.WaitAsync(Bound);
                Check(!running.IsCompleted && !initial.WaitForIdleAsync().IsCompleted, "Actual provider hold failed to retain the run.");
                Equal(mode == SessionStorageMode.LazyLocal, File.Exists(files.A));
                Equal(mode == SessionStorageMode.LazyLocal ? SessionLogStorageDurability.LocalFileFlush : SessionLogStorageDurability.VolatileMemory, initial.Snapshot.Log.StorageDurability);
                Equal(Persistence(mode, true), Context(owner).SessionSnapshot!.Persistence);
                Check(initial.Snapshot.Context.LlmMessages.Any(message => message.Role == "user" && Text(message) == "first user"), "Provider began before the acknowledged user checkpoint.");
                if (mode == SessionStorageMode.LazyLocal)
                {
                    SameBytes(await Bytes(backend, files.A), await FileBytes(files.A));
                    var disk = await new SessionLogReader().ReadAsync(new MemoryStream(await FileBytes(files.A), writable: false), leaveOpen: false); Equal("source", disk.Header!.Id);
                    Check(disk.ValidatedPrefix.Any(record => record.Entry.Kind == SessionEntryKind.Message && record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user"), "Lazy publication omitted the first actual user record.");
                }
                var checkpoint = await Bytes(backend, files.A);
                var followUp = await initial.SubmitInputAsync(new("queued later", PromptInputSource.Rpc, StreamingBehavior: PromptInputStreamingBehavior.FollowUp));
                Equal(SubmittedInputDisposition.Queued, followUp.Disposition); Equal(1, initial.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
                SameBytes(checkpoint, await Bytes(backend, files.A));
                await Throws<InvalidOperationException>(() => owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New))); Equal(0, ids.Sessions);
                script.Release.TrySetResult(); var result = await running.WaitAsync(Bound);
                Equal(SubmittedInputDisposition.Started, result.Disposition); Equal(AgentLoopStopReason.Completed, result.Run!.Reason);
                Equal(2, script.Requests.Count); Equal(0, initial.GetPendingInputQueueSnapshot().FollowUpMessages.Length);
                Check(initial.Snapshot.Context.LlmMessages.Where(message => message.Role == "user").Select(Text).SequenceEqual(new[] { "first user", "queued later" }), "Follow-up queue was not durably drained through the existing run.");
                var committed = await Bytes(backend, files.A); var source = owner.Current;
                await owner.CreateAsync(source, new(AgentSessionCreationKind.New)); Equal(2L, owner.Current.Generation);
                SameBytes(committed, await Bytes(backend, files.A)); Check(!File.Exists(owner.Current.Session.Path), "Empty replacement materialized without conversation.");
                await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
                await using var reopened = await lifecycle.OpenAsync(new(files.A), Model);
                Check(reopened.Snapshot.Context.LlmMessages.Where(message => message.Role == "user").Select(Text).SequenceEqual(new[] { "first user", "queued later" }), "Independent reopen lost actual queued input checkpoints."); NoTemporaries(backend);
            }
            finally { script.Release.TrySetResult(); await Drain(running); }
        }
    }

    private static async Task SelectedBranches()
    {
        foreach (var mode in Modes)
        {
            foreach (var kind in new[] { AgentSessionCreationKind.ForkBefore, AgentSessionCreationKind.ForkAt, AgentSessionCreationKind.Clone })
            {
                using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids(); var script = new Script();
                var lifecycle = Lifecycle(backend, ids, Registry(Model, script)); await Seed(backend, files.A, files.Root);
                await using var initial = await lifecycle.OpenAsync(new(files.A, false, "left-state"), Model);
                await using var owner = lifecycle.Attach(initial); var previous = owner.Current; var before = await Bytes(backend, files.A);
                Equal("left-state", initial.Snapshot.Context.LeafId); Equal("right-state", initial.Snapshot.Log.LeafId); Equal("left", Context(owner).ReadLatest("state")!.Data.Value.GetProperty("value").GetString());
                var entry = kind == AgentSessionCreationKind.ForkBefore ? "left-user" : kind == AgentSessionCreationKind.ForkAt ? "left-state" : null;
                var replacement = await owner.CreateAsync(previous, new(kind, entry)); Check(replacement is not null, "Backend fork or clone did not attach.");
                var current = owner.Current; Equal("session-1", current.Session.Snapshot.Log.Header.Id);
                if (mode == SessionStorageMode.InMemory)
                {
                    Check(previous.Session.SessionFile is null && current.Session.SessionFile is null && !current.Session.Snapshot.Log.Header.WireBody.Value.TryGetProperty("parentSession", out _), "Memory branch invented a parent file or disk identity.");
                    Equal(files.A, previous.Session.Path);
                }
                else Equal(files.A, current.Session.Snapshot.Log.Header.WireBody.Value.GetProperty("parentSession").GetString());
                Equal(kind == AgentSessionCreationKind.ForkBefore ? "selected left" : null, replacement!.SelectedText);
                var selected = Context(owner); Equal(kind == AgentSessionCreationKind.ForkBefore ? "base" : "left", selected.ReadLatest("state")!.Data.Value.GetProperty("value").GetString());
                Check(!current.Session.Snapshot.Log.Entries.Any(record => record.Id.StartsWith("right", StringComparison.Ordinal)) && !current.Session.Snapshot.Context.LlmMessages.Any(message => message.WireBody.ToString().Contains("physical sibling", StringComparison.Ordinal)), "Backend creation copied a physical sibling.");
                Opaque(current.Session); var disabled = Context(owner, "disabled");
                Equal(ExtensionSessionStateFailure.IncompatibleVersion, ThrowsSync<ExtensionSessionStateException>(() => disabled.ReadLatest("state")).Failure);
                Equal("1.00e400", disabled.ReadLatest("state", 99)!.Data.Value.GetProperty("opaque").GetRawText());
                var write = await owner.AppendExtensionEntryAsync(current, Draft("fresh branch")); Check(write.Append.CheckpointAcknowledged, "Fresh branch writer did not acknowledge its own checkpoint.");
                await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(previous, Draft("stale owner")));
                await Throws<PersistentAgentSessionException>(() => previous.Session.AppendExtensionEntryAsync("source", Draft("stale coordinator")));
                SameBytes(before, await Bytes(backend, files.A)); Equal(0, script.Requests.Count);
                var path = current.Session.Path; await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
                await using var reopened = await lifecycle.OpenAsync(new(path), Model); Equal("fresh branch", StateValue(reopened)); Opaque(reopened);
                SameBytes(before, await Bytes(backend, files.A)); Equal(mode == SessionStorageMode.LazyLocal && kind != AgentSessionCreationKind.ForkBefore, File.Exists(path)); NoTemporaries(backend);
            }
            await InFileBranches(mode);
        }
    }

    private static async Task InFileBranches(SessionStorageMode mode)
    {
        using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids(); var script = new Script();
        var lifecycle = Lifecycle(backend, ids, Registry(Model, script)); await Seed(backend, files.A, files.Root);
        await using (var peer = await lifecycle.CreateAsync(files.B, Header("peer", files.Root), Model)) { }
        await using var initial = await lifecycle.OpenAsync(new(files.A, false, "left-state"), Model);
        await using var owner = lifecycle.Attach(initial); var original = owner.Current; var prefix = await Bytes(backend, files.A);
        await owner.SwitchAsync(original, new(files.B));
        await owner.SwitchAsync(owner.Current, new(files.A, false, "right-state")); Equal(3L, owner.Current.Generation);
        Equal("right", Context(owner).ReadLatest("state")!.Data.Value.GetProperty("value").GetString());
        var right = await owner.AppendExtensionEntryAsync(owner.Current, Draft("fresh right")); Equal("right-state", right.Entry.ParentId); Prefix(prefix, await Bytes(backend, files.A));
        await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(original, Draft("stale matching ID")));
        await owner.SwitchAsync(owner.Current, new(files.B)); await owner.SwitchAsync(owner.Current, new(files.A, false, null));
        Equal(5L, owner.Current.Generation); Check(Context(owner).ReadLatest("state") is null && owner.Current.Session.Snapshot.Context.Ancestry.IsEmpty, "Explicit empty root restored sibling extension state.");
        var root = await owner.AppendExtensionEntryAsync(owner.Current, Draft("fresh root")); Check(root.Entry.ParentId is null, "Root append inherited a physical parent.");
        Prefix(prefix, await Bytes(backend, files.A)); await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
        await using var reopened = await lifecycle.OpenAsync(new(files.A), Model); Equal("fresh root", StateValue(reopened));
        Check(reopened.Snapshot.Log.Entries.Any(entry => entry.Id == "left-state") && reopened.Snapshot.Log.Entries.Any(entry => entry.Id == "right-state"), "In-file branch selection rewrote physical siblings.");
        Opaque(reopened); Equal(0, script.Requests.Count); NoTemporaries(backend);
    }

    private static async Task WorkingDirectoryRebinding()
    {
        foreach (var mode in Modes)
        {
            using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids();
            var cwdA = files.Path("cwd-alpha"); var cwdB = files.Path("cwd-beta");
            var modelA = Model with { Id = "alpha-model" }; var modelB = Model with { Id = "beta-model" };
            var bindings = new List<string>(); var services = new List<OwnedServices>();
            ValueTask<SessionRuntimeLease> Bind(string cwd, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); bindings.Add(cwd);
                var fresh = cwd == cwdA ? new OwnedServices(cwd, modelA, "alpha") : cwd == cwdB ? new OwnedServices(cwd, modelB, "beta") : throw new InvalidOperationException("Unconfigured fixture cwd.");
                services.Add(fresh); return ValueTask.FromResult(new SessionRuntimeLease(fresh.Registry, fresh));
            }
            var lifecycle = new PersistentSessionLifecycle(Registry(modelA, new Script()), () => 0, ids.Entry, nextSessionId: ids.Session, runtimeForWorkingDirectory: Bind, backend: backend);
            await using var initial = await lifecycle.CreateAsync(files.A, Header("alpha-session", cwdA), modelA);
            await initial.ConfigureAsync(new(SystemMessage: SystemMessage("alpha setup", Declaration("alpha"))));
            await using (var target = await lifecycle.CreateAsync(files.B, Header("beta-session", cwdB), modelB))
                await target.ConfigureAsync(new(SystemMessage: SystemMessage("beta setup", Declaration("beta"))));
            Equal(1, services[1].Releases); Equal(0, services[0].Releases);
            Check(!File.Exists(files.A) && !File.Exists(files.B), "Factory setup materialized files before conversation.");
            await using var owner = lifecycle.Attach(initial); using var tracker = new Subscriptions(owner); var notifications = new List<long>();
            owner.AttachmentChanged = tracker.Changed; owner.AfterReplacement = replacement => { notifications.Add(replacement.Current.Generation); return ValueTask.CompletedTask; };
            await initial.PromptAsync(User("alpha initial"));
            for (var index = 0; index < 4; index++)
            {
                var previous = owner.Current; var priorService = services[index == 0 ? 0 : index + 1]; var toB = index % 2 == 0;
                var result = await owner.SwitchAsync(previous, new(toB ? files.B : files.A)); Check(result is not null, "Actual-header rebound switch was vetoed.");
                var current = owner.Current; Equal(index + 2L, current.Generation); Equal(toB ? cwdB : cwdA, current.Session.WorkingDirectory);
                Equal(toB ? modelB : modelA, current.Session.Snapshot.Agent.Model);
                Check(current.Session.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(new[] { toB ? "beta" : "alpha" }), "Actual target header retained the source tool binding.");
                await Throws<InvalidOperationException>(() => owner.AppendExtensionEntryAsync(previous, Draft("stale rebound")));
                await current.Session.PromptAsync(User(toB ? "beta return" : "alpha return"));
                await priorService.Released.Task.WaitAsync(Bound); Equal(1, priorService.Releases); Equal(0, services[^1].Releases);
            }
            Check(bindings.SequenceEqual(new[] { cwdA, cwdB, cwdB, cwdA, cwdB, cwdA }), "Registry factory used inherited cwd or skipped repeated actual-header reopening.");
            Check(tracker.Completed.SequenceEqual(new long[] { 1, 2, 3, 4, 5 }) && tracker.Changes.SequenceEqual(new long[] { 2, 3, 4, 5 }) && notifications.SequenceEqual(tracker.Changes), "Replacement lost, duplicated or misbound actual event subscriptions and notifications.");
            Equal(5, services.Sum(service => service.Adapter.Effects)); Equal(5, services.Sum(service => service.Policy.Targets.Count));
            Check(services.All(service => service.Policy.Targets.All(target => target == service.Adapter.Target)), "Final tool actions used another header's cwd binding.");
            Check(services.All(service => service.Script.Requests.All(request => request.Model == service.Model)), "Provider requests crossed explicit model bindings.");
            Equal(6, services.Select(service => service.Registry).Distinct().Count()); Equal(6, services.Select(service => service.Script).Distinct().Count());
            Equal(6, services.Select(service => service.Adapter).Distinct().Count()); Equal(6, services.Select(service => service.Policy).Distinct().Count());
            Equal(mode == SessionStorageMode.LazyLocal, File.Exists(files.A)); Equal(mode == SessionStorageMode.LazyLocal, File.Exists(files.B));
            tracker.Dispose(); await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
            Check(services.All(service => service.Releases == 1), "Replacement or owner disposal leaked cwd services.");
            await using var reopenedA = await lifecycle.OpenAsync(new(files.A), modelB); await using var reopenedB = await lifecycle.OpenAsync(new(files.B), modelA);
            Equal(modelA, reopenedA.Snapshot.Agent.Model); Equal(modelB, reopenedB.Snapshot.Agent.Model);
            Check(reopenedA.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(new[] { "alpha" }) && reopenedB.Snapshot.Agent.Tools.Select(tool => tool.Name).SequenceEqual(new[] { "beta" }), "Independent reopen did not restore header-owned loadouts.");
            Equal(8, services.Count); Equal(0, services[^1].Releases); Equal(0, services[^2].Releases);
            await reopenedA.DisposeAsync(); await reopenedB.DisposeAsync();
            Check(services.All(service => service.Releases == 1), "Independent reopen did not own and join its distinct service release."); NoTemporaries(backend);
        }
    }

    private static async Task OwnedRuntimeFailures()
    {
        foreach (var mode in Modes)
        {
            using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids();
            var services = new List<OwnedServices>(); var wrongModel = false; var failRelease = false; CancellationTokenSource? cancelAfterAcquire = null;
            ValueTask<SessionRuntimeLease> Bind(string cwd, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var fresh = new OwnedServices(cwd, wrongModel ? Model with { Id = "unregistered-restored-model" } : Model, "fixture-tool", failRelease);
                services.Add(fresh); cancelAfterAcquire?.Cancel(); return ValueTask.FromResult(new SessionRuntimeLease(fresh.Registry, fresh));
            }
            var lifecycle = new PersistentSessionLifecycle(Registry(Model, new Script()), () => 0, ids.Entry, backend: backend, nextSessionId: ids.Session, runtimeForWorkingDirectory: Bind);
            await using var initial = await lifecycle.CreateAsync(files.A, Header("source", files.Root), Model);
            await using (var target = await lifecycle.CreateAsync(files.B, Header("target", files.Root), Model)) { }
            await using var owner = lifecycle.Attach(initial); var source = owner.Current;
            Check(await owner.SwitchAsync(source, new(files.B), (_, _, _) => ValueTask.FromResult(false)) is null, "Owned target veto attached.");
            Equal(1, services[^1].Releases); Equal(0, services[0].Releases); Equal(1, backend.ActiveWriterCount);
            using (var canceled = new CancellationTokenSource())
            {
                await Throws<OperationCanceledException>(() => owner.SwitchAsync(source, new(files.B), (_, _, token) =>
                { canceled.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }, cancellationToken: canceled.Token));
            }
            Equal(1, services[^1].Releases); Equal(0, services[0].Releases);
            string? createdPath = null;
            await Throws<PreflightFailure>(() => owner.CreateAsync(source, new(AgentSessionCreationKind.New), (target, _, _) =>
            { createdPath = target.Path; throw new PreflightFailure(); }));
            Equal(1, services[^1].Releases); Check(createdPath is not null && !backend.FileExists(createdPath), "Owned creation rollback leaked a branch.");
            wrongModel = true;
            await Throws<SessionRuntimeRegistryException>(() => owner.SwitchAsync(source, new(files.B)));
            Equal(1, services[^1].Releases); Equal(1, backend.ActiveWriterCount); wrongModel = false;
            using (var canceled = new CancellationTokenSource())
            {
                cancelAfterAcquire = canceled;
                await Throws<OperationCanceledException>(() => lifecycle.OpenAsync(new(files.B), Model, canceled.Token));
                Equal(1, services[^1].Releases); cancelAfterAcquire = null;
            }
            using (var canceled = new CancellationTokenSource())
            {
                cancelAfterAcquire = canceled;
                await Throws<OperationCanceledException>(() => lifecycle.CreateAsync(files.Path("canceled-create.jsonl"), Header("canceled", files.Root), Model, canceled.Token));
                Equal(1, services[^1].Releases); Check(!backend.FileExists(files.Path("canceled-create.jsonl")), "Canceled factory created a checkpoint."); cancelAfterAcquire = null;
            }
            wrongModel = true; failRelease = true;
            var failed = await Throws<AggregateException>(() => lifecycle.OpenAsync(new(files.B), Model));
            Check(failed.InnerExceptions[0] is SessionRuntimeRegistryException && failed.InnerExceptions[1] is PreflightFailure, "Open lost primary admission or owned cleanup failure.");
            Equal(1, services[^1].Releases); wrongModel = false; failRelease = false;
            Check(ReferenceEquals(source, owner.Current) && !source.Session.Snapshot.IsRetired && backend.FileExists(files.B), "Failed owned services retired source or deleted existing target.");
            await owner.AppendExtensionEntryAsync(source, Draft("usable after service failures"));
            await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
            Check(services.All(service => service.Releases == 1), "Failed admission, cancellation or veto leaked owned services.");
        }
    }

    private static async Task Rollback()
    {
        foreach (var mode in Modes)
        {
            using var files = new Files(); var backend = new SessionStorageBackend(files.Root, mode); var ids = new Ids(); var script = new Script();
            var lifecycle = Lifecycle(backend, ids, Registry(Model, script));
            await using var initial = await lifecycle.CreateAsync(files.A, Header("source", files.Root), Model);
            await initial.PromptAsync(User("original conversation"));
            await using (var target = await lifecycle.CreateAsync(files.B, Header("existing", files.Root), Model))
                await target.AppendExtensionEntryAsync("existing", Draft("existing target"));
            await using var owner = lifecycle.Attach(initial); var source = owner.Current; var original = await Bytes(backend, files.A); var imported = await Bytes(backend, files.B);
            owner.BeforeCreation = (_, _, _) => ValueTask.FromResult(false);
            Check(await owner.CreateAsync(source, new(AgentSessionCreationKind.New)) is null, "Pre-effect backend veto attached a session."); Equal(0, ids.Sessions);
            owner.BeforeCreation = null; string? stagedPath = null; PersistentAgentSession? staged = null;
            await Throws<PreflightFailure>(() => owner.CreateAsync(source, new(AgentSessionCreationKind.Clone), (target, _, _) =>
            { stagedPath = target.Path; staged = target; throw new PreflightFailure(); }));
            Check(staged is not null && staged.Snapshot.IsDisposed && stagedPath is not null && !backend.FileExists(stagedPath) && !File.Exists(stagedPath), "Failed backend preflight leaked its target writer or known created branch.");
            Equal(1, backend.ActiveWriterCount); Check(ReferenceEquals(source, owner.Current) && !source.Session.Snapshot.IsRetired, "Rejected backend branch retired A.");
            SameBytes(original, await Bytes(backend, files.A)); SameBytes(imported, await Bytes(backend, files.B));
            using (var canceled = new CancellationTokenSource())
            {
                await Throws<OperationCanceledException>(() => owner.CreateAsync(source, new(AgentSessionCreationKind.Clone), (target, _, token) =>
                { stagedPath = target.Path; staged = target; canceled.Cancel(); token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }, canceled.Token));
            }
            Check(staged!.Snapshot.IsDisposed && !backend.FileExists(stagedPath!), "Canceled staged validator returned before owned rollback."); Equal(1, backend.ActiveWriterCount);
            Check(await owner.SwitchAsync(source, new(files.B), (_, _, _) => ValueTask.FromResult(false)) is null, "Existing backend target veto attached a session.");
            Equal(1, backend.ActiveWriterCount); Check(backend.FileExists(files.B), "Target veto deleted an imported session.");
            SameBytes(imported, await Bytes(backend, files.B)); SameBytes(original, await Bytes(backend, files.A));
            var appended = await owner.AppendExtensionEntryAsync(source, Draft("available after rollback")); Check(appended.Append.CheckpointAcknowledged, "Rollback left the source writer unusable.");
            Prefix(original, await Bytes(backend, files.A)); NoTemporaries(backend);
            Check(backend.EnumerateFileNames(files.Root).Order().SequenceEqual(new[] { "a.jsonl", "b.jsonl" }), "Rollback removed an unowned file or retained a created branch.");
            await owner.DisposeAsync(); Equal(0, backend.ActiveWriterCount);
            await using var reopened = await lifecycle.OpenAsync(new(files.A), Model); Equal("available after rollback", StateValue(reopened));
        }
    }

    private static PersistentSessionLifecycle Lifecycle(SessionStorageBackend backend, Ids ids, SessionRuntimeRegistry registry) =>
        new(registry, () => 0, ids.Entry, nextSessionId: ids.Session, backend: backend);
    private static SessionRuntimeRegistry Registry(ModelDescriptor model, Script script, Adapter? adapter = null, Policy? policy = null) =>
        new([new(model, script)], adapter is null ? [] : [new(Declaration(adapter.Name), adapter)], policy ?? new Policy());
    private static SessionEntry Header(string id, string cwd) => Codec.Parse("{\"type\":\"session\",\"version\":3,\"id\":" + JsonSerializer.Serialize(id) + ",\"timestamp\":\"" + Timestamp + "\",\"cwd\":" + JsonSerializer.Serialize(cwd) + ",\"opaque\":1.00e400}");
    private static SessionEntry Entry(string type, string id, string? parent, string fields) => Codec.Parse("{\"type\":" + JsonSerializer.Serialize(type) + ",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) + ",\"timestamp\":\"" + Timestamp + "\"," + fields + "}");
    private static SessionEntry State(string id, string parent, string value, string owner = "fixture", int version = 1) => Entry("custom", id, parent,
        "\"customType\":\"pisharp.extension-state\",\"data\":{\"extensionId\":" + JsonSerializer.Serialize(owner) + ",\"entryKind\":\"state\",\"schemaVersion\":" + version + ",\"data\":{\"value\":" + JsonSerializer.Serialize(value) + ",\"opaque\":1.00e400}}");
    private static async Task Seed(SessionStorageBackend backend, string path, string cwd)
    {
        await using var store = await SessionLogStore.CreateNewAsync(path, Header("source", cwd), new(StorageFactory: backend));
        await store.AppendAsync([
            Entry("model_change", "model", null, "\"provider\":\"fixture\",\"modelId\":\"backend\""),
            Entry("future", "opaque", "model", "\"data\":{\"number\":1.00e400,\"nil\":null,\"native\":{\"$type\":\"inert\"}}"),
            State("base-state", "opaque", "base"), State("disabled-state", "base-state", "disabled", "disabled", 99),
            Entry("message", "left-user", "disabled-state", "\"message\":{\"role\":\"user\",\"timestamp\":0,\"content\":\"selected left\"}"), State("left-state", "left-user", "left"),
            Entry("message", "right-user", "disabled-state", "\"message\":{\"role\":\"user\",\"timestamp\":0,\"content\":\"physical sibling\"}"), State("right-state", "right-user", "right")]);
    }
    private static SessionExtensionEntryDraft Draft(string value) => new("fixture", "state", 1, JsonData.Parse(JsonSerializer.Serialize(new { value })));
    private static string? StateValue(PersistentAgentSession session) => session.Snapshot.Context.Ancestry.Last(entry => entry.Kind == SessionEntryKind.Custom && entry.WireBody.Value.GetProperty("data").GetProperty("extensionId").GetString() == "fixture" && entry.WireBody.Value.GetProperty("data").GetProperty("entryKind").GetString() == "state")
        .WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("value").GetString();
    private static void Opaque(PersistentAgentSession session)
    {
        var entry = session.Snapshot.Log.Entries.Single(record => record.Id == "opaque"); Equal("1.00e400", entry.WireBody.Value.GetProperty("data").GetProperty("number").GetRawText());
        Equal("inert", entry.WireBody.Value.GetProperty("data").GetProperty("native").GetProperty("$type").GetString());
        var disabled = session.Snapshot.Log.Entries.Single(record => record.Id == "disabled-state"); Equal(99, disabled.WireBody.Value.GetProperty("data").GetProperty("schemaVersion").GetInt32());
        Equal("1.00e400", disabled.WireBody.Value.GetProperty("data").GetProperty("data").GetProperty("opaque").GetRawText());
        Check(session.Snapshot.Context.LlmMessages.All(message => !message.WireBody.ToString().Contains("1.00e400", StringComparison.Ordinal)), "Opaque or disabled state acquired executable model authority.");
    }
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 0 })));
    private static JsonData Declaration(string name) => JsonData.Parse(JsonSerializer.Serialize(new { name, description = name, parameters = new { type = "object" } }));
    private static TranscriptEntry SystemMessage(string content, JsonData? declaration = null) => new("system", JsonData.Parse(JsonSerializer.Serialize(new
    { role = "system", content, timestamp = 0, toolsAdded = declaration is null ? Array.Empty<JsonElement>() : new[] { declaration.Value } })));
    private static string Text(TranscriptEntry message)
    { var content = message.WireBody.Value.GetProperty("content"); return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Where(item => item.GetProperty("type").GetString() == "text").Select(item => item.GetProperty("text").GetString())); }
    private static SessionLogStorageDurability Pending(SessionStorageMode mode) => mode == SessionStorageMode.InMemory ? SessionLogStorageDurability.VolatileMemory : SessionLogStorageDurability.DeferredLocalFile;
    private static ExtensionSessionPersistence Persistence(SessionStorageMode mode, bool conversation) => mode == SessionStorageMode.InMemory ? ExtensionSessionPersistence.VolatileMemory : conversation ? ExtensionSessionPersistence.DurableLocalFile : ExtensionSessionPersistence.DeferredLocalFile;
    private static void Checkpoint(SessionExtensionEntryReceipt receipt, SessionLogStorageDurability durability)
    { Check(receipt.Append.CheckpointAcknowledged && receipt.Append.Accepted && receipt.Append.Flushed && !receipt.Append.DurableCheckpointAcknowledged, "Pending backend checkpoint pretended disk durability or lacked acknowledgment."); Equal(durability, receipt.Append.Snapshot.StorageDurability); }
    private static StateContext Context(ReplaceableAgentSession owner, string extension = "fixture")
    { var provider = new NativeSessionSnapshotProvider(); provider.Attach(owner); return new(provider, owner.Current, extension); }
    private static async Task<byte[]> Bytes(SessionStorageBackend backend, string path)
    { await using var stream = await backend.OpenReadAsync(path, CancellationToken.None); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); return bytes.ToArray(); }
    private static async Task<byte[]> FileBytes(string path)
    { await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes); return bytes.ToArray(); }
    private static void NoTemporaries(SessionStorageBackend backend) => Check(!backend.EnumerateFileNames(backend.Directory).Any(name => name.StartsWith(".pisharp-", StringComparison.Ordinal)), "Backend retained an owned publication temporary.");
    private static void SameBytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Lifecycle changed original physical or checkpoint bytes.");
    private static void Prefix(byte[] expected, byte[] actual) => Check(actual.AsSpan().StartsWith(expected), "Append or branch selection rewrote existing physical history.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Throws<T>(Func<Task> work) where T : Exception { try { await work().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static T ThrowsSync<T>(Action work) where T : Exception { try { work(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Drain(Task work) { try { await work.WaitAsync(Bound); } catch (Exception) when (work.IsCompleted) { } }
    private sealed class PreflightFailure : Exception { }
    private sealed class OwnedServices : IAsyncDisposable
    {
        public readonly ModelDescriptor Model; public readonly Script Script; public readonly Adapter Adapter;
        public readonly Policy Policy = new(); public readonly SessionRuntimeRegistry Registry;
        public readonly TaskCompletionSource Released = Gate(); public int Releases; private readonly bool failRelease;
        public OwnedServices(string cwd, ModelDescriptor model, string tool, bool failRelease = false)
        { Model = model; Script = new(tool); Adapter = new(tool, cwd); Registry = SessionLifecycleBackendTests.Registry(model, Script, Adapter, Policy); this.failRelease = failRelease; }
        public ValueTask DisposeAsync()
        {
            if (++Releases != 1) throw new InvalidOperationException("Owned services released twice.");
            Released.TrySetResult(); if (failRelease) throw new PreflightFailure(); return ValueTask.CompletedTask;
        }
    }
    private sealed class Ids { private int entries; public int Sessions; public string Entry() => "entry-" + Interlocked.Increment(ref entries); public string Session() => "session-" + ++Sessions; }
    private sealed class StateContext(NativeSessionSnapshotProvider provider, AgentSessionAttachment attachment, string extension) : IExtensionSessionContext
    {
        public string OwnerId => extension; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => CancellationToken.None;
        public CancellationToken SessionCancellationToken => attachment.LifetimeToken;
        public CancellationToken ExtensionLifetimeCancellationToken => CancellationToken.None;
        public ExtensionSessionSnapshot? SessionSnapshot => provider.Capture(this);
        public IExtensionSessionActionScope OpenScope() => provider.OpenScope(this, SessionSnapshot!);
    }
    private sealed class Script(string? tool = null, bool holdFirst = false) : IChatTransport
    {
        public readonly List<ChatRequest> Requests = []; public readonly TaskCompletionSource Entered = Gate(), Release = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request); var number = Requests.Count; var callTool = tool is not null && request.Messages[^1].Role != "toolResult";
            var final = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 0,
                callTool ? [new ToolCallContent("call-" + number, tool!, JsonData.EmptyObject)] : [new TextContent("done-" + number)], TokenUsage.Zero, callTool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending }); Entered.TrySetResult();
            if (holdFirst && number == 1) await Release.Task.WaitAsync(Bound, token);
            if (callTool) { var call = (ToolCallContent)final.Content[0]; yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done-" + number); }
            yield return new StreamDone(final.StopReason, final);
        }
    }
    private sealed class Adapter(string name, string cwd) : IPreparedToolAdapter
    {
        public string Name => name; public string Target { get; } = Path.Combine(cwd, "owned-binding"); public int Effects;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) => ValueTask.FromResult(new PreparedToolAction(name, "fixture", PreparedToolActionKind.Path, Target, invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(action.Target == Target);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) { token.ThrowIfCancellationRequested(); Effects++; return ValueTask.FromResult(ToolResult.Success(name + " effect")); }
    }
    private sealed class Policy : IToolActionPolicy
    {
        public readonly List<string> Targets = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Targets.Add(action.Target); return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Subscriptions : IDisposable
    {
        private readonly ReplaceableAgentSession owner; private IDisposable? lease; public readonly List<long> Completed = [], Changes = [];
        public Subscriptions(ReplaceableAgentSession owner) { this.owner = owner; lease = owner.Current.Session.Subscribe(new Sink(owner.Current.Generation, Completed)); }
        public ValueTask Changed(AgentSessionReplacement replacement)
        {
            Check(ReferenceEquals(owner.Current, replacement.Current), "Notification did not expose the actual committed attachment.");
            lease?.Dispose(); lease = replacement.Current.Session.Subscribe(new Sink(replacement.Current.Generation, Completed)); Changes.Add(replacement.Current.Generation); return ValueTask.CompletedTask;
        }
        public void Dispose() { lease?.Dispose(); lease = null; }
    }
    private sealed class Sink(long generation, List<long> completed) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) { if (observation is AgentLoopEnded) completed.Add(generation); return ValueTask.CompletedTask; } }
    private sealed class Files : IDisposable
    {
        private const string Prefix = "PiSharp-backend-lifecycle-"; private readonly string temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        public string Root { get; } public string A => Path("a.jsonl"); public string B => Path("b.jsonl");
        public Files() { Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N"))); ValidateRoot(); Directory.CreateDirectory(Root); }
        public string Path(string name) { var path = System.IO.Path.Combine(Root, name); Validate(path); return path; }
        private void Validate(string path) => Check(System.IO.Path.IsPathFullyQualified(path) && string.Equals(path, System.IO.Path.GetFullPath(path), Comparison) && string.Equals(System.IO.Path.GetDirectoryName(path), Root, Comparison), "Backend fixture path escaped its canonical owned root.");
        private void ValidateRoot() { var name = System.IO.Path.GetFileName(Root); Check(System.IO.Path.IsPathFullyQualified(Root) && string.Equals(System.IO.Path.GetDirectoryName(Root), temp, Comparison) && name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Refusing cleanup outside owned backend fixture."); }
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return; Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Backend fixture root became a link.");
            foreach (var path in Directory.EnumerateFileSystemEntries(Root)) { Validate(path); Check((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Backend cleanup encountered an unowned directory or link."); File.Delete(path); }
            Directory.Delete(Root, recursive: false);
        }
    }
}
