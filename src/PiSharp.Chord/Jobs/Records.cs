// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/jobs/records.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Jobs;

internal sealed class JobRecords
{
    internal IJobOwner Owner { get; private set; }
    internal JobDefinition Definition { get; private set; }
    internal Running Running { get; }
    internal RunStates? States { get; private set; }
    private readonly Persistence? statePersistence;
    private readonly PersistedRuns? persisted;
    private readonly Dictionary<string, JobRun> runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobRun> keys = new(StringComparer.Ordinal);
    private readonly HashSet<JobRun> waiting = [];
    private readonly HashSet<JobRun> stopped = [];
    private HostedJob? job;
    private int active;
    private bool loaded;
    private bool declared = true;
    private ChordError? ended;
    private ITimer? flushTimer;
    private ChordContext context;
    internal string Name => $"{Owner.Documents.Label} job {Definition.Name}";
    internal bool Persisted => persisted is not null;
    internal ChordContext Context => context;
    internal JobRecords(JobDefinition definition, IJobOwner owner)
    {
        Owner = owner; Definition = definition; context = ChordContext.WithoutAbortSignal(owner.Context);
        if (definition.Persist is not null)
        {
            var store = owner.Store ?? throw new ArgumentException($"{definition.Name}: persisted jobs need a local empty-param object and a store");
            if (owner is Self self)
            {
                store.Store.Claims.Documents(store.Owner, "chord.jobs", self.Attempt.Owner);
                store.Store.Claims.Documents(store.Owner, "chord.job-states", self.Attempt.Owner);
            }
            statePersistence = new(store.Store, store.Owner, "chord.job-states", definition.Name);
            persisted = new(new(store.Store, store.Owner, "chord.jobs", definition.Name), statePersistence);
        }
        Running = new(owner.Documents.Batch, Name);
        if (definition.State is { } state) States = CreateStates(state);
    }
    private RunStates CreateStates(StateDefinition state) => new(state, Owner.Documents, statePersistence, StateChanged, Held, Owner.Host.Options.TimeProvider);
    internal void Check(JobDefinition definition)
    {
        if (definition.Version == Definition.Version && definition.Fingerprint != Definition.Fingerprint)
            throw new InvalidOperationException($"{Name}: its schema changed without a version bump (version {definition.Version})");
        if ((definition.Persist is null) != (Definition.Persist is null)) throw new InvalidOperationException($"{Name}: persistence cannot change while its object exists");
        if (definition.State is { } state) States?.Definition.Check(state);
    }
    internal void Redefine(JobDefinition definition, IJobOwner owner)
    {
        Check(definition); declared = true; Owner = owner; Definition = definition; context = ChordContext.WithoutAbortSignal(owner.Context);
        if (definition.State is { } state) { if (States is null) States = CreateStates(state); else States.Redefine(state); }
        if (runs.Count > 0) Owner.Documents.Batch.Within(() => { foreach (var run in runs.Values) ReplaceEntry(run); }, Context);
    }
    internal bool Takes(JobRun run) => run.Version == Definition.Version ? run.Fingerprint == Definition.Fingerprint
        : run.Version < Definition.Version && Definition.Accepts.ContainsKey(run.Version);
    internal JsonNode? ArgsOf(JobRun run, JobDefinition definition)
    {
        if (run.Version == definition.Version) return run.Args;
        if (run.MappedVersion == definition.Version) return run.MappedArgs;
        var args = definition.Accepts[run.Version].Args(run.Args);
        run.MappedVersion = definition.Version; run.MappedArgs = args; run.MappedOutcome = null; return args;
    }
    internal JobOutcome OutcomeOf(JobRun run)
    {
        var outcome = run.Outcome!;
        if (run.Version == Definition.Version) return outcome;
        var args = ArgsOf(run, Definition);
        return run.MappedOutcome ??= outcome with { Args = args, State = States?.Migrated(run.Id, Context) ?? outcome.State,
            Result = outcome.Kind == "done" ? Definition.Accepts[run.Version].Result(outcome.Result) : null };
    }
    internal IJobRun Stage(Transaction tx, JsonNode? args, ChordContext context, StartOptions options)
    {
        DataInternal.Check(DataInternal.ValidatorFor(Definition.ArgumentSnapshot, context), args, "args", context);
        // Serializing validates JSON leaves as well as the schema. Keep a snapshot across caller mutations.
        var snapshot = args is null ? null : JsonNode.Parse(ChordJson.Stringify(args));
        var prior = tx.Staged<Staging>(this);
        if (options.Key is { } key)
        {
            var known = prior?.Started.Find(x => x.Key == key) ?? keys.GetValueOrDefault(key);
            if (known is not null && !ReferenceEquals(known, prior?.Finishing)) return known;
        }
        if (Definition.Concurrency is { } limit && active + (prior?.Started.Count ?? 0) - (prior?.Finishing is null ? 0 : 1) >= limit)
            throw new ChordError("unavailable", $"{Name}: at its concurrency limit ({limit})", "busy");
        var run = new JobRun(Guid.CreateVersion7().ToString(), Definition.Version, Definition.Fingerprint, options.Key, snapshot, Persisted ? "pending" : "running");
        if (Persisted && context.Telemetry.Propagation?.Inject(context.Telemetry) is { } trace)
        {
            run.Trace = new JsonObject();
            foreach (var (name, value) in trace) run.Trace[name] = value;
        }
        var staging = StageOf(tx); staging.Started.Add(run);
        Change(tx, new("create", run));
        if (Definition.State is { } state) staging.Adoptions.Add(States!.StageCreate(run.Id, state.Initial(context), () => Store(tx)));
        return run;
    }
    internal sealed record ChangeRecord(string Kind, JobRun Run, string? Phase = null, int? Restarts = null, int? FinishFailures = null,
        bool? AbortMarked = null, string? Blocked = null, JobOutcome? Outcome = null);
    internal sealed class Staging(JobRecords records) : IStaged
    {
        internal List<JobRun> Started { get; } = [];
        internal List<ChangeRecord> Changes { get; } = [];
        internal List<Action> Adoptions { get; } = [];
        internal JobRun? Finishing;
        public void Adopt() => records.Owner.Host.Dispatcher.Invoke(() =>
        {
            if (records.ended is not null) return;
            foreach (var adopt in Adoptions) adopt();
            foreach (var change in Changes) records.Adopt(change);
        });
        public void Committed() => records.Owner.Host.Dispatcher.Invoke(() =>
        {
            foreach (var change in Changes)
            {
                if (records.ended is { } ended) { if (change.Kind == "create") change.Run.Reject(ended); }
                else records.Committed(change);
            }
        });
        public void Discarded()
        {
            foreach (var run in Started) run.Reject(new ChordError("released", $"{records.Name}: the start's transaction was discarded", "discarded"));
        }
    }
    internal Staging StageOf(Transaction tx) => tx.Stage(this, () => new Staging(this));
    private List<IStagedWrite> Store(Transaction tx) => tx.StageStore(Owner.Store!.Store);
    internal void Change(Transaction tx, ChangeRecord change)
    {
        var staging = StageOf(tx); staging.Changes.Add(change); var run = change.Run;
        if (change.Kind is "delete" or "drop")
        {
            if (States?.Current(run.Id) is not null) tx.RetireSlot(States, run.Id);
            if (persisted is not null) staging.Adoptions.Add(persisted.Retire(run.Id, Store(tx)));
            return;
        }
        if (persisted is not null && change.Kind != "restore") staging.Adoptions.Add(persisted.Stage(run,
            PersistedRuns.Encode(run, change.Phase, change.Restarts, change.FinishFailures, change.AbortMarked,
                change.Blocked, change.Outcome, change.Kind == "unblock"), Store(tx)));
    }
    private void Adopt(ChangeRecord change)
    {
        var run = change.Run;
        switch (change.Kind)
        {
            case "create": case "restore":
                runs.Add(run.Id, run); if (run.Key is not null) keys[run.Key] = run;
                if (run.Phase != "blocked") { active++; Owner.CountRuns(1); }
                run.Entry = Entry(run); Running.Add(run.Entry); break;
            case "update":
                if (change.Phase is { } phase) run.Phase = phase;
                if (change.Restarts is { } restarts) run.Restarts = restarts;
                if (change.FinishFailures is { } failures) run.FinishFailures = failures;
                if (change.AbortMarked is { } marked) run.AbortMarked = marked;
                ReplaceEntry(run); break;
            case "outcome": run.Outcome = change.Outcome; run.Phase = "finishing"; ReplaceEntry(run); break;
            case "block":
                if (run.Phase != "blocked") active--;
                if (change.FinishFailures is { } failed) run.FinishFailures = failed;
                run.Phase = "blocked"; run.Blocked = change.Blocked; ReplaceEntry(run); break;
            case "unblock":
                run.Phase = change.Phase!; run.Blocked = null; run.BlockedPhase = null; run.FinishFailures = 0;
                if (run.Phase == "pending") run.Resuming = true;
                active++; Owner.CountRuns(1); ReplaceEntry(run); break;
            case "delete": case "drop":
                runs.Remove(run.Id); waiting.Remove(run); stopped.Remove(run);
                if (run.Key is { } key && ReferenceEquals(keys.GetValueOrDefault(key), run)) keys.Remove(key);
                if (change.Kind == "delete") active--;
                Running.Remove(run.Entry!); run.Entry = null; break;
        }
    }
    private void Committed(ChangeRecord change)
    {
        var run = change.Run;
        switch (change.Kind)
        {
            case "create": Owner.Host.Dispatcher.Post(() => Need(run)); break;
            case "delete": Owner.CountRuns(-1); run.Resolve(run.Outcome!); break;
            case "block": Owner.CountRuns(-1); break;
            case "unblock": Need(run); break;
            case "drop": Report("job_dropped", run); run.Reject(new ChordError("aborted", $"{Name}: run {run.Id} dropped")); break;
        }
    }
    private JsonObject Entry(JobRun run)
    {
        var args = run.Args;
        if (run.Phase != "blocked" && Takes(run)) { try { args = ArgsOf(run, Definition); } catch { } }
        return run.EntryFor(States?.Value(run.Id), args);
    }
    private void ReplaceEntry(JobRun run)
    {
        if (run.Entry is not { } before) return;
        run.Entry = Entry(run); Running.Replace(before, run.Entry);
    }
    private void StateChanged(string id, System.Collections.Immutable.ImmutableArray<Delta.Op> ops)
    {
        if (runs.GetValueOrDefault(id) is not { Entry: { } before } run) return;
        run.Entry = Entry(run); Running.StateChanged(before, run.Entry, ops);
    }
    internal Task<JobOutcome> OutcomeAsync(string id, ChordContext context) => runs.TryGetValue(id, out var run)
        ? run.OutcomeAsync(context) : Task.FromException<JobOutcome>(new ChordError("gone", $"{Name}: no unsettled run {id}"));
    internal async Task<AbortResult> AbortAsync(string id, ChordContext context)
    {
        if (ended is not null || !runs.TryGetValue(id, out var run)) return AbortResult.Terminal;
        if (run.Phase == "blocked") { await RecordAsync(new("drop", run)); return AbortResult.Marked; }
        if (!run.AbortMarked) await RecordAsync(new("update", run, AbortMarked: true));
        if (run.Phase == "finishing") return AbortResult.Marked;
        Cancel(run);
        await run.OverAsync(context); return AbortResult.Marked;
    }
    internal async Task RecordAsync(ChangeRecord change)
    {
        try
        {
            await Owner.Documents.TxAsync(tx =>
            {
                Owner.Host.Dispatcher.Invoke(() => { if (runs.GetValueOrDefault(change.Run.Id) == change.Run) Change(tx, change); });
                return Task.FromResult(true);
            }, Owner.Documents.Own, Context);
        }
        catch when (ended is not null || Owner.Documents.Own.Sealed) { }
    }
    internal void Attach(HostedJob next)
    {
        if (ended is not null) return; job = next;
        foreach (var run in waiting.ToArray()) { waiting.Remove(run); Dispatch(run, next); }
        _ = RetryBlockedAsync();
    }
    internal void Detach(HostedJob old, IEnumerable<JobRun> finishing)
    { if (ReferenceEquals(job, old)) job = null; foreach (var run in finishing) Need(run); }
    internal void Need(JobRun run)
    {
        if (ended is not null || runs.GetValueOrDefault(run.Id) != run) return;
        if (!declared)
        {
            if (run.Phase != "blocked") Observe(RecordAsync(new("block", run, Phase: "blocked", Blocked: "version")));
            return;
        }
        if (job is { } current) Dispatch(run, current); else waiting.Add(run);
    }
    internal void Undeclare()
    {
        if (!declared || ended is not null) return;
        declared = false; job = null;
        foreach (var run in waiting.ToArray()) { waiting.Remove(run); Need(run); }
    }
    private void Dispatch(JobRun run, HostedJob current)
    {
        if (run.Phase == "blocked" || runs.GetValueOrDefault(run.Id) != run) return;
        if (!Takes(run)) { Observe(RecordAsync(new("block", run, Phase: "blocked", Blocked: "version"))); return; }
        if (run.Phase == "finishing") { current.Finish(run); return; }
        if (run.Invoked) return;
        if (run.AbortMarked) { Observe(RecordOutcomeAsync(run, "failed", null, new("aborted", Reason: "abort"))); return; }
        if (run.Resuming && Definition.Persist?.Resume == JobResume.Abandon)
        { Observe(RecordOutcomeAsync(run, "failed", null, new("aborted", Reason: "restart"))); return; }
        run.Resuming = false; current.Invoke(run);
    }
    internal async Task RecordOutcomeAsync(JobRun run, string kind, object? result, JobError? error)
    {
        try
        {
            await Owner.Documents.TxAsync(tx =>
            {
                Owner.Host.Dispatcher.Invoke(() =>
                {
                    if (runs.GetValueOrDefault(run.Id) != run) return;
                    var outcome = new JobOutcome(run.Id, run.Args, States?.Value(run.Id), kind, result, error);
                    Change(tx, new("outcome", run, Phase: "finishing", Outcome: outcome));
                    if (Persisted && States?.StageHeld(run.Id, Store(tx)) is { } adopt) StageOf(tx).Adoptions.Add(adopt);
                });
                return Task.FromResult(true);
            }, Owner.Documents.Own, Context);
        }
        catch when (ended is not null || Owner.Documents.Own.Sealed) { return; }
        finally { run.Invocation?.Ended.TrySetResult(); }
        Need(run);
    }
    internal void Cut(JobRun run, ChordError error, string reason)
    {
        if (run.Invocation is not { } invocation) return;
        invocation.Settle(); Owner.Documents.Fence(invocation.Token); Cancel(run);
        if (Persisted) { run.Phase = "stopped"; run.Resuming = true; stopped.Add(run); if (ended is null) Need(run); }
        else if (ended is null) Observe(RecordOutcomeAsync(run, "failed", null, new("aborted", Reason: reason == "reload" ? "reload" : "restart")));
    }
    internal async Task LoadAsync(ChordContext context)
    {
        if (persisted is null || loaded) return;
        var restored = new List<(JobRun Run, ChangeRecord? Change)>();
        try
        {
            foreach (var run in await persisted.LoadAsync(context))
            {
                if (!Takes(run))
                {
                    run.BlockedPhase = run.Phase; run.Phase = "blocked"; run.Blocked = "version";
                    await persisted.FindBlockedStateAsync(run.Id, context); restored.Add((run, null)); continue;
                }
                if (States is { } states && await statePersistence!.ReadAsync(run.Id, context) is { } document)
                { var decoded = statePersistence.Decode(document, states.Definition.Current, context); states.Create(run.Id, decoded.Value, decoded.Stored); }
                if (run.Outcome is { } outcome) run.Outcome = outcome with { State = States?.Value(run.Id) };
                ChangeRecord? change = null;
                if (run.Phase == "running")
                {
                    run.Resuming = true; var restarts = run.Restarts + 1;
                    change = restarts < 5 ? new("update", run, Phase: "pending", Restarts: restarts)
                        : new("outcome", run, Phase: "finishing", Outcome: new(run.Id, run.Args, States?.Value(run.Id), "failed", Error: new("restart_limit", Restarts: restarts)));
                    if (restarts >= 5) run.Restarts = restarts;
                }
                else if (run.Phase == "stopped") { run.Resuming = true; change = new("update", run, Phase: "pending"); }
                else if (run.Phase == "pending") run.Resuming = true;
                restored.Add((run, change));
            }
            if (restored.Count > 0) await Owner.Documents.TxAsync(tx =>
            {
                Owner.Host.Dispatcher.Invoke(() =>
                {
                    foreach (var (run, _) in restored) StageOf(tx).Changes.Add(new("restore", run));
                    foreach (var (_, change) in restored) if (change is not null) Change(tx, change);
                });
                return Task.FromResult(true);
            }, Owner.Documents.Own, context);
        }
        catch { persisted.Drop(); States?.Drop(); throw; }
        loaded = true;
        foreach (var (run, change) in restored)
        {
            if (change?.Outcome?.Error?.Kind == "restart_limit") Report("job_interrupted", run, restarts: run.Restarts);
            Need(run);
        }
    }
    private async Task RetryBlockedAsync()
    {
        try
        {
            foreach (var run in runs.Values.ToArray().Where(x => x.Phase == "blocked" && (x.Blocked == "finish_failed" || Takes(x))))
            {
                if (run.BlockedPhase is not null && States is { } states && statePersistence is not null)
                {
                    if (await statePersistence.ReadAsync(run.Id, Context) is { } document)
                    { var decoded = statePersistence.Decode(document, states.Definition.Current, Context); states.Create(run.Id, decoded.Value, decoded.Stored); }
                    persisted!.ForgetBlockedState(run.Id);
                    if (run.Outcome is { } outcome) run.Outcome = outcome with { State = States.Value(run.Id) };
                }
                await RecordAsync(new("unblock", run, Phase: run.Outcome is not null ? "finishing" : Persisted ? "pending" : "running", FinishFailures: 0));
            }
        }
        catch (Exception error) { Report("job_resume_failed", null, error); }
    }
    private void Held(double deadline)
    {
        deadline = States?.HeldIds.Select(States.Deadline).DefaultIfEmpty(deadline).Min() ?? deadline;
        var delay = Math.Max(0, deadline - Owner.Host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        flushTimer?.Dispose();
        flushTimer = Owner.Host.Options.TimeProvider.CreateTimer(_ => Owner.Host.Dispatcher.Post(() => Observe(FlushHeldAsync())), null,
            TimeSpan.FromMilliseconds(Math.Min(delay, uint.MaxValue - 1)), Timeout.InfiniteTimeSpan);
    }
    private async Task FlushHeldAsync()
    {
        if (ended is not null || States is null) return;
        try
        {
            await Owner.Documents.TxAsync(tx =>
            {
                Owner.Host.Dispatcher.Invoke(() =>
                {
                    var now = Owner.Host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                    foreach (var id in States.HeldIds)
                        if (States.Deadline(id) <= now && States.StageHeld(id, Store(tx)) is { } adopt) StageOf(tx).Adoptions.Add(adopt);
                });
                return Task.FromResult(true);
            }, Owner.Documents.Own, Context);
            var next = States.HeldIds.Select(States.Deadline).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (double.IsFinite(next)) Held(next);
        }
        catch when (ended is not null || Owner.Documents.Own.Sealed) { }
    }
    internal async Task WriteAtCloseAsync(ChordContext context)
    {
        if (persisted is null) return;
        var writes = new List<IStagedWrite>(); var adopts = new List<Action>();
        foreach (var run in stopped) adopts.Add(persisted.Stage(run, PersistedRuns.Encode(run), writes));
        foreach (var id in States?.HeldIds ?? []) if (States!.StageHeld(id, writes) is { } adopt) adopts.Add(adopt);
        if (writes.Count > 0) await Owner.Store!.Store.CommitAsync(writes, new CommitFence(() => true, (_, _) => Owner.Host.Dispatcher.Invoke(() =>
        { foreach (var adopt in adopts) adopt(); stopped.Clear(); })), ChordContext.WithoutAbortSignal(context));
    }
    internal void End(ChordError error)
    {
        if (ended is not null) return; ended = error; job = null; waiting.Clear(); flushTimer?.Dispose(); flushTimer = null;
        foreach (var run in runs.Values)
        {
            if (!Persisted && run.Phase == "blocked") Report("job_dropped", run);
            Cancel(run); run.Reject(error);
            if (run.Invocation is { Running: false } invocation) invocation.Ended.TrySetResult();
        }
        if (active > 0) Owner.CountRuns(-active); active = 0;
    }
    internal void Drop()
    {
        runs.Clear(); keys.Clear(); waiting.Clear(); stopped.Clear(); States?.Drop(); persisted?.Drop(); Running.End();
    }
    private void Cancel(JobRun run)
    {
        // A linked run context can contain plugin cancellation registrations. Cancellation still reaches every
        // registration when one throws; the aggregate is diagnostic, never a reason to skip the remaining teardown.
        try { run.Invocation?.Abort.Cancel(); }
        catch (Exception error) { Report("cancellation_failed", run, error); }
    }
    internal void Report(string kind, JobRun? run, Exception? error = null, int? restarts = null) =>
        Owner.Report(new(kind, error, Owner.Documents.Label, Job: Definition.Name, Id: run?.Id, Restarts: restarts));
    internal void Observe(Task task)
    {
        _ = task.ContinueWith(t => Owner.Host.Dispatcher.Post(() => Report("job_record_failed", null, t.Exception!.GetBaseException())),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }
}
