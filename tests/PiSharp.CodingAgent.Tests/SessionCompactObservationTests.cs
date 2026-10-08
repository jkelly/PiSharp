using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SessionCompactObservationTests
{
    internal const string Prefix = "session-compact observation ";
    private static readonly ModelDescriptor Model = new("compact-observe", "openai-responses", "fixture");
    private static readonly SessionEntryCodec Codec = new();
    private static readonly SessionCompactionSettings Settings = new(true, 128, 0);
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "manual payload is exact immutable checkpoint after acknowledged context installation", Manual),
        (Prefix + "actual configured threshold publishes explicit threshold false metadata", Threshold),
        (Prefix + "actual overflow retry waits held callback before fresh provider and final settlement", Overflow),
        (Prefix + "branch skipped vetoed and failed actual appends never publish", Exclusions),
        (Prefix + "captured handler order survives removal addition failures foreign cancellation and reporter failure", HandlerSnapshot),
        (Prefix + "repeated summaries select first stored compaction while context exposes newest checkpoint", RepeatedSummary),
        (Prefix + "held committed callback joins original completion caller cancellation owner and registry disposal", HeldCompletion),
        (Prefix + "rebuilt context is visible and reentrant or retained callback mutations reject", ContextAndReentrancy),
        (Prefix + "initial replacement generations bind exact captured handlers and retired mutation rejects", Replacement),
        (Prefix + "trusted host publisher fault occurs outside committed write fault classification", HostPublisherFault),
        (Prefix + "immutable native payload bound refusal reports without veto or poisoned writer", PayloadBounds)
    ];
    private static async Task Manual()
    {
        await using var f = await Fixture.Create(); JsonData? seen = null; ExtensionSessionSnapshot? view = null;
        await f.Register((value, context, token) =>
        {
            Check(!token.CanBeCanceled, "Observation received an event abort signal."); seen = value;
            view = ((IExtensionSessionContext)context).SessionSnapshot;
            Check(f.Session.Snapshot.IsCompacting && f.Session.Snapshot.Fault is null, "Publication escaped reservation or poisoned context.");
            return ValueTask.CompletedTask;
        });
        f.Install(); var receipt = (await f.Compact("manual exact"))!;
        var payload = seen ?? throw new InvalidOperationException("No manual observation");
        Check(payload.Value.EnumerateObject().Select(p => p.Name).SequenceEqual(["type", "compactionEntry", "fromExtension", "reason", "willRetry"]), "Payload shape differs.");
        CheckPayload(payload, "manual", false, true); Check(JsonElement.DeepEquals(payload.Value.GetProperty("compactionEntry"), receipt.Entry.WireBody.Value), "Payload is not exact acknowledged entry.");
        Check(view is not null && view.SelectedLeafId == receipt.Entry.Id && view.BranchEntries[^1].Value.GetProperty("id").GetString() == receipt.Entry.Id && f.Script.Calls == 0 && f.Diagnostics.Count == 0, "Callback did not see rebuilt durable context or invoked transport.");
    }
    private static async Task Threshold()
    {
        await using var f = await Fixture.Create(); var observations = new List<JsonData>();
        await f.Register((value, _, _) => { observations.Add(value); return ValueTask.CompletedTask; }); f.Install();
        f.Script.Schedule.Add(Message(StopReason.Stop, 10000));
        f.Session.ConfigureAutomaticCompaction(f.Generator, Settings, 5000);
        await f.Session.PromptAsync(User("threshold input"));
        Check(observations.Count == 1 && f.Session.Snapshot.LastAutomaticCompaction?.Disposition == "committed", "Actual threshold did not publish exactly once.");
        CheckPayload(observations.Single(), "threshold", false, false);
        Check(f.Script.Calls == 1 && f.Script.Cleanups == 1 && !f.Session.Snapshot.IsProcessingOperation, "Threshold original run did not settle.");
    }
    private static async Task Overflow()
    {
        await using var f = await Fixture.Create(); var entered = Gate(); var release = Gate(); var observations = new List<JsonData>();
        await f.Register(async (value, _, token) =>
        {
            Check(!token.CanBeCanceled, "Recovery observation got abort signal."); observations.Add(value); entered.TrySetResult(); await release.Task;
        }); f.Install();
        f.Script.Schedule.Add(Message(StopReason.Error) with { ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"input exceeds the context window\"")) });
        f.Script.Schedule.Add(Message(StopReason.Stop));
        f.Session.ConfigureAutomaticCompaction(f.Generator, Settings, 50000, recoveryDesiredMaxOutput: 1000);
        Task<AgentLoopResult>? original = null;
        try
        {
            original = f.Session.PromptAsync(User("recover actual input"));
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Recovery settled before held callback entry.");
            Check(f.Script.Calls == 1 && f.Script.Cleanups == 1 && f.Session.Snapshot.IsProcessingOperation && f.Session.Snapshot.IsCompacting, "Retry or settlement overtook observation/process join.");
            CheckPayload(observations.Single(), "overflow", true, false);
            release.TrySetResult(); await original;
            Check(f.Diagnostics.Count == 0, "Recovery observation reported a swallowed callback assertion or cleanup failure.");
            Check(f.Script.Calls == 2 && f.Script.Cleanups == 2 && observations.Count == 1 && !f.Session.Snapshot.IsProcessingOperation, "Original retry or final settlement did not join.");
            Check(f.Script.Requests[1].Messages.Any(m => m.WireBody.ToString().Contains("generated summary", StringComparison.Ordinal)), "Retried provider did not receive rebuilt context.");
        }
        finally { release.TrySetResult(); if (original is not null) await original; }
    }
    private static async Task Exclusions()
    {
        await using (var f = await Fixture.Create())
        {
            var calls = 0; await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
            await Throws<ArgumentException>(() => f.Owner.CompactAsync(f.Owner.Current, new(Settings) { Reason = (SessionCompactionReason)(-1) }, f.Generator));
            await Throws<ArgumentException>(() => f.Owner.CompactAsync(f.Owner.Current, new(Settings) { WillRetry = true }, f.Generator));
            Check(calls == 0 && f.Session.Snapshot.Log.Entries.Length == 4, "Invalid origin/retry metadata admitted effects.");
            var skip = await f.Owner.CompactAsync(f.Owner.Current, new(Settings, Automatic: true, ContextWindow: 1_000_000_000), f.Generator);
            Check(skip is null && calls == 0, "Skipped compaction published.");
            await f.Owner.SummarizeBranchAsync(f.Owner.Current, new("u0", ExtensionSummary: new("branch only")), f.Generator);
            Check(calls == 0, "Branch summary published session_compact.");
        }
        await using (var f = await Fixture.Create())
        {
            var calls = 0; await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
            await Throws<IOException>(() => f.Owner.CompactAsync(f.Owner.Current, new(Settings, ExtensionSummary: new("veto")), f.Generator,
                preflight: (_, _) => throw new IOException("preflight veto")));
            Check(calls == 0 && f.Session.Snapshot.Log.Entries.Length == 4 && f.Session.Snapshot.Fault is null, "Veto published/wrote/poisoned.");
        }
        var factory = new FailingFactory();
        await using (var f = await Fixture.Create(factory))
        {
            var calls = 0; await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install(); factory.Storage!.Fail = true;
            await Throws<PersistentAgentSessionException>(() => f.Compact("failed append"));
            Check(calls == 0 && f.Session.Snapshot.Log.Entries.Length == 4, "Failed actual append published or acknowledged a checkpoint.");
        }
    }
    private static async Task HandlerSnapshot()
    {
        await using var f = await Fixture.Create(); var order = new List<string>(); IExtensionRegistration? second = null;
        await f.Registry.ActivateAsync("ordered", new Plugin(api =>
        {
            f.Api = api;
            api.Observe(new("first", "session_compact", (_, _, _) =>
            {
                order.Add("first"); second!.Dispose();
                api.Observe(new("late", "session_compact", (_, _, _) => { order.Add("late"); return ValueTask.CompletedTask; }));
                throw new IOException("first original failure");
            }));
            second = api.Observe(new("second", "session_compact", (_, _, _) =>
            { order.Add("second"); using var foreign = new CancellationTokenSource(); foreign.Cancel(); throw new OperationCanceledException(foreign.Token); }));

        }));
        await f.Registry.ActivateAsync("other-owner", new Plugin(api => api.Observe(new("third", "session_compact", (_, _, _) => { order.Add("third"); return ValueTask.CompletedTask; }))));
        f.Report = (diagnostic, token) => { Check(!token.CanBeCanceled, "Diagnostic got abort signal."); f.Diagnostics.Add(diagnostic); throw new IOException("reporter original failure"); };
        f.Install(); var receipt = await f.Compact("order");
        Check(receipt is not null && order.SequenceEqual(["first", "second", "third"]) && f.Diagnostics.Select(d => d.RegistrationId).SequenceEqual(["first", "second"]), "Snapshot/order/failure continuation changed.");
        Check(f.Session.Snapshot.Fault is null, "Handler or reporter poisoned committed writer.");
        f.Api!.Observe(new("generic", "generic", (_, _, _) => throw new IOException("generic still fails")));
        await Throws<IOException>(() => f.Registry.DispatchObservationsAsync(f.Registry.CaptureSnapshot(), "generic", JsonData.EmptyObject).AsTask());
    }
    private static async Task RepeatedSummary()
    {
        await using var f = await Fixture.Create(); var events = new List<JsonData>(); var leaves = new List<string?>();
        await f.Register((value, context, _) => { events.Add(value); leaves.Add(((IExtensionSessionContext)context).SessionSnapshot!.SelectedLeafId); return ValueTask.CompletedTask; }); f.Install();
        var first = (await f.Compact("repeat identical"))!;
        f.Script.Schedule.Add(Message(StopReason.Stop)); await f.Session.PromptAsync(User("new history between repeated summaries"));
        var second = (await f.Compact("repeat identical"))!;
        Check(first.Entry.Id != second.Entry.Id && events.Count == 2 &&
            events.All(e => e.Value.GetProperty("compactionEntry").GetProperty("id").GetString() == first.Entry.Id) &&
            leaves.SequenceEqual([first.Entry.Id, second.Entry.Id]), "Repeated summary selected newest entry or exposed stale rebuilt context.");
    }
    private static async Task HeldCompletion()
    {
        await using var f = await Fixture.Create(); using var cancel = new CancellationTokenSource(); var entered = Gate(); var release = Gate(); var joined = false;
        await f.Register(async (_, _, token) => { Check(!token.CanBeCanceled, "Committed callback got signal."); entered.TrySetResult(); await release.Task; joined = true; }); f.Install();
        Task<SessionSummaryCheckpointReceipt?>? original = null; Task? ownerClose = null; Task? registryClose = null;
        try
        {
            original = f.Compact("held committed", cancel.Token);
            Check(await Task.WhenAny(entered.Task, original) == entered.Task && !original.IsCompleted, "Original compaction settled before callback entry.");
            Check(f.Session.Snapshot.Log.Entries.Any(e => e.Kind == SessionEntryKind.Compaction), "Callback preceded acknowledgment.");
            cancel.Cancel(); ownerClose = f.Owner.DisposeAsync().AsTask(); registryClose = f.Registry.DisposeAsync().AsTask();
            Check(!original.IsCompleted && !ownerClose.IsCompleted && !registryClose.IsCompleted, "Cancellation/disposal abandoned original callback or reservation lease.");
            release.TrySetResult(); var receipt = await original; await ownerClose; await registryClose;
            Check(f.Diagnostics.Count == 0, "Committed observation reported a swallowed callback assertion or cleanup failure.");
            Check(receipt is not null && joined && f.Session.Snapshot.IsDisposed, "Committed receipt/physical disposal did not join callback.");
        }
        finally
        {
            release.TrySetResult();
            try { if (original is not null) await original; }
            finally
            {
                try { if (ownerClose is not null) await ownerClose; }
                finally { if (registryClose is not null) await registryClose; }
            }
        }
    }
    private static async Task ContextAndReentrancy()
    {
        await using var f = await Fixture.Create(); IExtensionSessionContextEditContext? retained = null; Task? unexpectedDispose = null;
        CancellationToken scopeToken = default;
        await f.Register(async (value, context, _) =>
        {
            var editable = (IExtensionSessionContextEditContext)context; retained = editable; var entry = value.Value.GetProperty("compactionEntry").GetProperty("id").GetString();
            scopeToken = context.SessionCancellationToken;
            Check(scopeToken.CanBeCanceled && !scopeToken.IsCancellationRequested && !context.OperationCancellationToken.CanBeCanceled &&
                !context.ExtensionLifetimeCancellationToken.IsCancellationRequested, "Active callback lifetime facts differ.");
            Check(f.Session.Snapshot.Context.LeafId == entry && SameMessages(f.Session.Snapshot.Agent.Messages, PiSharp.Sessions.Context.SessionContextProjector.AgentMessages(f.Session.Snapshot.Context)), "Agent context was not installed before callback.");
            var before = f.Session.Snapshot.Log.Entries.Length;
            await Throws<InvalidOperationException>(() => editable.AppendContextEditAsync("u0", JsonData.Null).AsTask());
            await Throws<InvalidOperationException>(() => f.Compact("reentrant"));
            await Throws<InvalidOperationException>(() => f.Owner.SwitchAsync(f.Owner.Current, new(f.Other)));
            try { unexpectedDispose = f.Session.DisposeAsync().AsTask(); throw new InvalidOperationException("Self-disposal was incorrectly admitted."); }
            catch (InvalidOperationException) when (unexpectedDispose is null) { }
            Check(f.Session.Snapshot.Log.Entries.Length == before && ReferenceEquals(f.Owner.Current.Session, f.Session), "Reentrant observer mutated or replaced reserved context.");
        }); f.Install();
        try { await f.Compact("context visible"); }
        finally { if (unexpectedDispose is not null) await unexpectedDispose; }
        Check(unexpectedDispose is null, "Observer admitted self-disposal.");
        var captured = retained ?? throw new InvalidOperationException("No retained context");
        var before = await ReadAcknowledgedBytes(f.Source, f.Session);
        Check(!f.Owner.Current.LifetimeToken.IsCancellationRequested, "Live attachment was canceled by callback disposal.");
        await CheckRetainedCancellation("same-attachment", captured, scopeToken);
        var after = await ReadAcknowledgedBytes(f.Source, f.Session);
        Check(before.SequenceEqual(after), "Retained callback changed durable session bytes.");
        Check(f.Diagnostics.Count == 0 && f.Session.Snapshot.Fault is null && !f.Session.Snapshot.IsCompacting, "Retained scope poisoned writer or reservation leaked.");
    }
    private static async Task Replacement()
    {
        await using var f = await Fixture.Create(); var generations = new List<long>(); var seenSessions = new List<string>(); IExtensionSessionContextEditContext? oldContext = null;
        CancellationToken oldScopeToken = default;
        await f.Register((_, context, _) =>
        {
            var view = ((IExtensionSessionContext)context).SessionSnapshot!; generations.Add(view.Generation); seenSessions.Add(view.SessionId);
            if (oldContext is null)
            {
                oldContext = (IExtensionSessionContextEditContext)context; oldScopeToken = context.SessionCancellationToken;
                Check(oldScopeToken.CanBeCanceled && !oldScopeToken.IsCancellationRequested, "Initial callback scope was canceled before delivery.");
            }
            return ValueTask.CompletedTask;
        }); f.Install(); var old = f.Owner.Current; await f.Compact("initial attachment");
        Check(f.Diagnostics.Count == 0, "Initial attachment observation reported a swallowed callback assertion or cleanup failure.");
        Check(oldScopeToken.IsCancellationRequested && !old.LifetimeToken.IsCancellationRequested,
            "Retained scope cancellation was incorrectly attributed to replacement retirement.");
        var replacement = await f.Owner.SwitchAsync(f.Owner.Current, new(f.Other)) ?? throw new InvalidOperationException("Replacement skipped");
        await f.Compact("replacement attachment");
        Check(f.Diagnostics.Count == 0, "Replacement attachment observation reported a swallowed callback assertion or cleanup failure.");
        Check(generations.SequenceEqual([1L, 2L]) && seenSessions.SequenceEqual(["source", "other"]) && old.Session.Snapshot.IsRetired, "Actual replacement did not install generation-bound observation.");
        var oldBytes = await ReadAcknowledgedBytes(f.Source, old.Session); var freshBytes = await ReadAcknowledgedBytes(f.Other, replacement.Current.Session);
        var fresh = replacement.Current.Session.Snapshot;
        await CheckRetainedCancellation("retired-attachment", oldContext ?? throw new InvalidOperationException("No old callback context"), oldScopeToken);
        await Throws<InvalidOperationException>(() => f.Owner.CompactAsync(old, new(Settings, ExtensionSummary: new("retired")), f.Generator));
        await Throws<PersistentAgentSessionException>(() => old.Session.CompactAsync("source", new(Settings, ExtensionSummary: new("retired")), f.Generator));
        var oldBytesAfter = await ReadAcknowledgedBytes(f.Source, old.Session);
        var freshBytesAfter = await ReadAcknowledgedBytes(f.Other, replacement.Current.Session);
        Check(oldBytes.SequenceEqual(oldBytesAfter) && freshBytes.SequenceEqual(freshBytesAfter), "Stale calls changed old or fresh durable bytes.");
        Check(ReferenceEquals(f.Owner.Current, replacement.Current) && !replacement.Current.LifetimeToken.IsCancellationRequested &&
            replacement.Current.Session.Snapshot.Log.LeafId == fresh.Log.LeafId &&
            SameMessages(fresh.Agent.Messages, replacement.Current.Session.Snapshot.Agent.Messages) &&
            replacement.Current.Session.Snapshot.Fault is null && generations.Count == 2, "Retired observer mutation reached fresh writer.");
        f.Script.Schedule.Add(Message(StopReason.Stop)); await f.Session.PromptAsync(User("fresh writer after stale refusals"));
        Check(f.Script.Calls == 1 && f.Script.Cleanups == 1 && f.Session.Snapshot.Fault is null, "Fresh writer did not remain usable after stale refusals.");
    }
    private static async Task<byte[]> ReadAcknowledgedBytes(string path, PersistentAgentSession session)
    {
        // Callers have awaited the original compaction/refusal; no new fixture work is
        // admitted until this read and its handle disposal settle. Read sharing must
        // permit the existing writable owner without changing its write exclusion.
        var acknowledged = session.Snapshot;
        Check(!acknowledged.IsProcessingOperation && !acknowledged.IsCompacting && acknowledged.Fault is null &&
            acknowledged.Log.StorageDurability == SessionLogStorageDurability.LocalFileFlush, "Fixture read has no stable durable acknowledgment.");
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Check(reader.Length == acknowledged.Log.CommittedByteLength, "Fixture file length differs from acknowledged bytes.");
        var bytes = new byte[checked((int)acknowledged.Log.CommittedByteLength)];
        await reader.ReadExactlyAsync(bytes);
        var after = session.Snapshot;
        Check(reader.Length == bytes.Length && after.Log.Sequence == acknowledged.Log.Sequence &&
            after.Log.CommittedByteLength == acknowledged.Log.CommittedByteLength && !after.IsProcessingOperation && !after.IsCompacting,
            "Fixture checkpoint changed during owned read.");
        return bytes;
    }
    private static async Task CheckRetainedCancellation(string schedule, IExtensionSessionContextEditContext context, CancellationToken scopeToken)
    {
        // Public context token belongs to the disposed callback action scope, not the
        // event signal or an inferred current attachment. Require exact refusal ownership.
        Check(scopeToken.CanBeCanceled && scopeToken.IsCancellationRequested && context.SessionCancellationToken == scopeToken &&
            !context.OperationCancellationToken.CanBeCanceled && !context.ExtensionLifetimeCancellationToken.IsCancellationRequested,
            "Retained callback cancellation ownership differs.");
        var error = await Throws<OperationCanceledException>(() => context.AppendContextEditAsync("u0", JsonData.Null).AsTask());
        Check(error.GetType() == typeof(OperationCanceledException) && error.CancellationToken == scopeToken && error.InnerException is null,
            "Retained context did not return the exact canceled callback scope token.");
        using var caller = new CancellationTokenSource(); caller.Cancel();
        var callerError = await Throws<OperationCanceledException>(() => context.AppendContextEditAsync("u0", JsonData.Null, caller.Token).AsTask());
        Check(callerError.GetType() == typeof(OperationCanceledException) && callerError.CancellationToken == caller.Token &&
            callerError.CancellationToken != scopeToken && callerError.InnerException is null, "Explicit caller cancellation lost its first-check token precedence.");
        Console.WriteLine("SESSION-COMPACT-CONTEXT " + JsonSerializer.Serialize(new
        {
            schedule, cause = "callback-action-scope-disposal", exceptionType = error.GetType().FullName,
            exactScopeToken = error.CancellationToken == scopeToken, scopeCancellationRequested = scopeToken.IsCancellationRequested,
            operationCanBeCanceled = context.OperationCancellationToken.CanBeCanceled,
            extensionCancellationRequested = context.ExtensionLifetimeCancellationToken.IsCancellationRequested,
            innerCauseCount = error.InnerException is null ? 0 : 1, explicitCallerTokenPrecedence = callerError.CancellationToken == caller.Token
        }));
    }
    private static async Task HostPublisherFault()
    {
        var failure = new IOException("trusted publisher fault"); var bound = false;
        await using var f = await Fixture.Create(bind: (owner, attachment) =>
        {
            owner.ConfigureCompactionObservationForBinding(attachment, _ => throw failure);
            owner.ConfigureSessionInfoObservationForBinding(attachment, _ => ValueTask.CompletedTask);
            bound = true;
        });
        Check(bound, "Compaction publisher was not installed through the actual reserved runtime binder.");
        var error = await Throws<IOException>(() => f.Compact("committed despite host publisher"));
        Check(ReferenceEquals(error, failure) && f.Session.Snapshot.Fault is null && f.Session.Snapshot.Context.LeafId == f.Session.Snapshot.Log.LeafId &&
            f.Session.Snapshot.Log.Entries.Any(e => e.Kind == SessionEntryKind.Compaction) && !f.Session.Snapshot.IsCompacting, "Observer failure entered write-fault catch or lost installed context/reservation cleanup.");
        f.Session.ConfigureCompactionObservation(null); f.Script.Schedule.Add(Message(StopReason.Stop)); await f.Session.PromptAsync(User("writer remains usable"));
    }
    private static async Task PayloadBounds()
    {
        await using var f = await Fixture.Create(); var calls = 0; await f.Register((_, _, _) => { calls++; return ValueTask.CompletedTask; }); f.Install();
        var receipt = await f.Compact(new string('s', 66000));
        Check(receipt is not null && calls == 0 && f.Diagnostics.Single().OwnerId == "native-host" && f.Session.Snapshot.Fault is null, "Native JSON admission bound vetoed/poisoned acknowledged compaction or silently delivered oversize data.");
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-compact-observation-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source.jsonl"); internal string Other => Path.Combine(Root, "other.jsonl");
        internal ReplaceableAgentSession Owner = null!; internal PersistentAgentSession Session => Owner.Current.Session;
        internal ExtensionRegistry Registry = null!; internal IExtensionRegistry? Api; internal Script Script = new(); internal Generator Generator = new();
        internal List<ExtensionEventDiagnostic> Diagnostics = []; internal Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? Report;
        private SessionRuntimeRegistry runtime = null!; private int id; private long ticks = 1711929600000;
        private ISessionLogStorageFactory? storage;
        internal long Clock() => Interlocked.Increment(ref ticks);
        internal Task<PersistentAgentSession> Open(string path) => PersistentAgentSession.OpenWithRegistryAsync(path, runtime, Clock,
            () => "checkpoint-" + Interlocked.Increment(ref id), new(SessionLogStoreOptions: new(StorageFactory: storage)), fallbackModel: Model);
        internal static async Task<Fixture> Create(ISessionLogStorageFactory? storage = null,
            Action<ReplaceableAgentSession, AgentSessionAttachment>? bind = null)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); f.Script.Clock = f.Clock;
            f.runtime = new([new(Model, f.Script)], [], new NoPolicy());
            try
            {
                foreach (var path in new[] { f.Source, f.Other })
                {
                    var header = Codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = Path.GetFileNameWithoutExtension(path), timestamp = "2024-04-01T00:00:00.000Z", cwd = f.Root }));
                    await using var store = await SessionLogStore.CreateNewAsync(path, header);
                    await store.AppendAsync([Entry("u0", null, User(new string('x', 8000))), Entry("a0", "u0", Assistant()),
                        Entry("u1", "a0", User(new string('y', 8000))), Entry("a1", "u1", Assistant())]);
                }
                f.storage = storage;
                if (bind is null)
                { var session = await f.Open(f.Source); f.Owner = new(session, (request, _) => f.Open(request.Path)); }
                else
                {
                    var lifecycle = new PersistentSessionLifecycle(f.runtime, f.Clock, () => "checkpoint-" + Interlocked.Increment(ref f.id),
                        new(SessionLogStoreOptions: new(StorageFactory: storage)),
                        runtimeForAttachment: (_, _, _) => ValueTask.FromResult(new SessionRuntimeLease(f.runtime, bindOwner: bind)));
                    var session = await lifecycle.OpenAsync(new(f.Source), Model);
                    try { f.Owner = await lifecycle.AttachAsync(session); }
                    catch (Exception bindingError)
                    {
                        var close = session.DisposeAsync().AsTask();
                        try { await close; }
                        catch (Exception cleanupError) { throw new AggregateException(bindingError, close.Exception ?? cleanupError); }
                        throw;
                    }
                }
                var views = new NativeSessionSnapshotProvider(); views.Attach(f.Owner); f.Registry = new(null, null, views);
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal Task Register(ExtensionObservationCallback callback) => Registry.ActivateAsync("observations", new Plugin(api =>
        { Api = api; api.Observe(new("observe-compact", "session_compact", callback)); }));
        internal void Install()
        {
            var binding = new NativeSessionCompactionObservationBinding(Registry, Registry.CaptureSnapshot(), Report ?? ((diagnostic, _) => { Diagnostics.Add(diagnostic); return ValueTask.CompletedTask; }));
            binding.Attach(Owner, Owner.Current);
            Owner.AfterReplacement = replacement => { binding.Attach(Owner, replacement.Current); return ValueTask.CompletedTask; };
        }
        internal Task<SessionSummaryCheckpointReceipt?> Compact(string summary, CancellationToken token = default) =>
            Owner.CompactAsync(Owner.Current, new(Settings, ExtensionSummary: new(summary)), Generator, token);
        public async ValueTask DisposeAsync()
        {
            try { if (Registry is not null) await Registry.DisposeAsync(); }
            finally
            {
                try { if (Owner is not null) await Owner.DisposeAsync(); }
                finally
                {
                    Check(Path.GetDirectoryName(Path.GetFullPath(Root)) == parent && Path.GetFileName(Root).StartsWith("PiSharp-compact-observation-", StringComparison.Ordinal), "Invalid owned fixture root.");
                    if (Directory.Exists(Root))
                    {
                        Check((File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Linked fixture cleanup root rejected.");
                        Directory.Delete(Root, recursive: true);
                    }
                }
            }
        }
    }
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { token.ThrowIfCancellationRequested(); initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Generator : ISessionSummaryGenerator
    {
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new SessionGeneratedSummary("generated summary", TokenUsage.Zero)); }
    }
    private sealed class Script : IChatTransport
    {
        internal Func<long> Clock = () => 0; internal List<AssistantMessage> Schedule = []; internal List<ChatRequest> Requests = []; internal int Calls, Cleanups;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); var final = (Calls < Schedule.Count ? Schedule[Calls] : Message(StopReason.Stop)) with { Timestamp = Clock() }; Calls++; Requests.Add(request);
            try
            {
                await Task.CompletedTask; yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
                yield return new TextStarted(0, new("")); yield return new TextEnded(0, ((TextContent)final.Content.Single()).Text);
                if (final.StopReason == StopReason.Error) yield return new StreamError(final.StopReason, final); else yield return new StreamDone(final.StopReason, final);
            }
            finally { Cleanups++; }
        }
    }
    private sealed class NoPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Observer acquired a tool"); }
    private sealed class FailingFactory : ISessionLogStorageFactory
    {
        internal Storage? Storage;
        public async ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => Storage = new(await SessionLogStore.DefaultStorageFactory.OpenAsync(path, createNew, token));
    }
    private sealed class Storage(ISessionLogStorage inner) : ISessionLogStorage
    {
        internal bool Fail; public Stream ReadStream => inner.ReadStream; public SessionLogStorageDurability Durability => inner.Durability; public long Length => inner.Length;
        public void PositionForAppend(long length) => inner.PositionForAppend(length);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes) => Fail ? ValueTask.FromException(new IOException("authored actual append refusal")) : inner.WriteAsync(bytes);
        public ValueTask FlushAsync() => inner.FlushAsync(); public void FlushToDisk() => inner.FlushToDisk(); public ValueTask BeforeCheckpointAsync() => inner.BeforeCheckpointAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private static SessionEntry Entry(string id, string? parent, TranscriptEntry message) => Codec.Parse(JsonSerializer.Serialize(new
    { type = "message", id, parentId = parent, timestamp = "2024-04-01T00:00:00.000Z", message = message.WireBody.Value }));
    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 10 })));
    private static TranscriptEntry Assistant() => new("assistant", PiWireJson.WriteMessage(Message(StopReason.Stop) with { Timestamp = 10 }));
    private static AssistantMessage Message(StopReason stop, long input = 100) => new(Model.Api, Model.Provider, Model.Id, 0, [new TextContent("fake completed text")], new(input, 1, 0, 0, input + 1, new(0, 0, 0, 0, 0)), stop);
    private static void CheckPayload(JsonData value, string reason, bool retry, bool extension) => Check(value.Value.GetProperty("type").GetString() == "session_compact" &&
        value.Value.GetProperty("reason").GetString() == reason && value.Value.GetProperty("willRetry").GetBoolean() == retry && value.Value.GetProperty("fromExtension").GetBoolean() == extension, "Reason/retry/extension payload differs.");
    private static bool SameMessages(ImmutableArray<TranscriptEntry> first, ImmutableArray<TranscriptEntry> second) =>
        first.Length == second.Length && first.Zip(second).All(pair => pair.First.Role == pair.Second.Role && JsonElement.DeepEquals(pair.First.WireBody.Value, pair.Second.WireBody.Value));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
