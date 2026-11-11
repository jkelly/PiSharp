// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/jobs/job.ts
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Jobs;

internal sealed class HostedJob : IHostedJob, IJobHandle
{
    private static readonly ConditionalWeakTable<Documents.Documents, Dictionary<string, JobRecords>> ByObject = new();
    private readonly JobRecords records;
    private readonly JobDefinition definition;
    private readonly JobImplementation implementation;
    private readonly IJobOwner owner;
    private readonly ChordContext context;
    private readonly HashSet<JobRun> invocations = [];
    private readonly HashSet<JobRun> finishing = [];
    private string state = "declared";
    private ChordError? sealedError;
    private string cutReason = "restart";

    private HostedJob(JobRecords records, JobDefinition definition, JobImplementation implementation, IJobOwner owner)
    { this.records = records; this.definition = definition; this.implementation = implementation; this.owner = owner; context = ChordContext.WithoutAbortSignal(owner.Context); }
    internal static HostedJob Create(JobDefinition definition, JobImplementation implementation, IJobOwner owner)
    {
        var all = ByObject.GetOrCreateValue(owner.Documents);
        if (!all.TryGetValue(definition.Name, out var records)) all.Add(definition.Name, records = new(definition, owner));
        else records.Redefine(definition, owner);
        return new(records, definition, implementation, owner);
    }
    internal static void Check(Documents.Documents documents, JobDefinition definition)
    {
        if (ByObject.TryGetValue(documents, out var all) && all.TryGetValue(definition.Name, out var records)) records.Check(definition);
    }
    internal string Name => definition.Name;
    internal static void Reconcile(Documents.Documents documents, IEnumerable<string> names)
    {
        if (!ByObject.TryGetValue(documents, out var all)) return;
        var declared = names.ToHashSet(StringComparer.Ordinal);
        foreach (var (name, records) in all) if (!declared.Contains(name)) records.Undeclare();
    }
    public IWatchable<JsonNode?> Running => records.Running;
    public Task<IJobRun> StartAsync(JsonNode? args, ChordContext context, StartOptions? options = null)
    {
        try { owner.Host.Dispatcher.Invoke(() => owner.Documents.Batch.AssertOutside("job.start")); }
        catch (Exception error) { return Task.FromException<IJobRun>(error); }
        var attributes = owner.Host.Attributes(obj: owner.Documents.Label); attributes["job"] = definition.Name; attributes["phase"] = "intent";
        return owner.Host.TraceOperation("chord.job", attributes, context, traced => owner.Host.Dispatcher.RunAsync(() =>
            owner.Documents.TxAsync(tx => Task.FromResult(Stage(tx, args, traced, options ?? new())), owner.Token, traced)));
    }
    internal IJobRun Stage(Transaction tx, JsonNode? args, ChordContext context, StartOptions options) => owner.Host.Dispatcher.Invoke(() =>
    {
        if (!ReferenceEquals(tx.Documents, owner.Documents)) throw new ArgumentException($"{owner.Documents.Label}: a transaction starts only its own object's jobs");
        if (state == "sealed") throw new ChordError("released", $"{definition.Name}: its attempt ended", "ended");
        if (state == "declared") throw new ChordError("not_ready", $"{definition.Name}: its object is not live yet");
        var run = records.Stage(tx, args, context, options);
        if (run is JobRun job && job.Trace is null && ChordTelemetry.CarrierOf(context.Telemetry) is { } carrier)
            job.Trace = new JsonObject(carrier.Select(each => KeyValuePair.Create(each.Key, (JsonNode?)JsonValue.Create(each.Value))));
        return run;
    });
    public Task<JobOutcome> OutcomeAsync(string id, ChordContext context) => owner.Host.Dispatcher.RunAsync(() => records.OutcomeAsync(id, context));
    public Task<AbortResult> AbortAsync(string id, ChordContext context) => owner.Host.Dispatcher.RunAsync(() => records.AbortAsync(id, context));
    public Task LoadAsync(ChordContext context) => records.LoadAsync(context);
    public async Task WriteAtCloseAsync(ChordContext context)
    {
        await records.WriteAtCloseAsync(context);
        if (owner.Documents.Own.Sealed) records.Drop();
    }
    public void Live() { if (state != "declared") return; state = "live"; records.Attach(this); }
    public void HandOver()
    {
        Seal(new ChordError("released", "job attempt reloaded", "ended"), "reload");
    }
    public void Seal(ChordError error, string reason)
    {
        if (state == "sealed") return;
        state = "sealed"; sealedError = error; cutReason = reason;
        if (owner.Documents.Own.Sealed) records.End(error);
        records.Detach(this, finishing); finishing.Clear();
        foreach (var run in invocations.ToArray()) records.Cut(run, error, reason);
        invocations.Clear();
    }
    internal void Invoke(JobRun run)
    {
        var invocation = new Invocation(); run.Invocation = invocation;
        if (!records.Persisted || run.Phase == "running") { Start(run, invocation); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Admit(completion.Task);
        _ = Reserve();
        async Task Reserve()
        {
            try
            {
                await owner.Documents.TxAsync(tx =>
                {
                    owner.Host.Dispatcher.Invoke(() => records.Change(tx, new("update", run, Phase: "running"))); return Task.FromResult(true);
                }, owner.Token, context);
                if (state != "sealed") Start(run, invocation);
                else { records.Cut(run, sealedError!, cutReason); invocation.Ended.TrySetResult(); }
            }
            catch (Exception error)
            {
                invocation.Settle(); invocation.Ended.TrySetResult();
                if (state == "sealed") records.Need(run);
                else records.Report("job_record_failed", run, error);
            }
            finally { completion.TrySetResult(); }
        }
    }
    private void Start(JobRun run, Invocation invocation)
    {
        var trace = PhaseSpan(run, "run");
        var parent = trace is null ? context : ChordContext.WithTelemetry(trace.Span, context);
        var call = new CallContext(owner.Host, owner.Caller, ChordContext.WithAbortSignal(invocation.Abort.Token, parent));
        var ctx = new JobContext(call, run.Id, StateOf(run, invocation));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Drain admission precedes plugin invocation, including its synchronous pre-await code.
        owner.AdmitRun(completion.Task, ctx.Dispose); invocations.Add(run);
        _ = Execute();
        async Task Execute()
        {
            string kind; object? result = null; JobError? failure = null;
            try { result = await owner.Host.InvokeCallbackAsync(implementation.Run, () => implementation.Run(records.ArgsOf(run, definition), ctx), owner.DeclaringFacet); kind = "done"; trace?.Span.SetAttributes(new SpanAttributes { ["outcome"] = "ok" }); }
            catch (Exception error)
            {
                if (trace is not null) trace.Span.SetAttributes(new SpanAttributes { ["outcome"] = ChordTelemetry.Ended(trace.Span, "job_failed", error, invocation.Abort.Token) });
                kind = "failed";
                if (invocation.Abort.IsCancellationRequested) failure = new("aborted", Reason: "abort");
                else
                {
                    records.Report("job_failed", run, error);
                    failure = new("threw", error is ChordError or PluginError ? error : new PluginError("internal", error.Message));
                }
            }
            finally { invocation.Settle(); trace?.End(); }
            try
            {
                ctx.Dispose(); invocations.Remove(run);
                if (!invocation.Token.Sealed && ReferenceEquals(run.Invocation, invocation))
                    await records.RecordOutcomeAsync(run, kind, result, failure);
            }
            catch (Exception error) { records.Report("job_record_failed", run, error); }
            finally { invocation.Ended.TrySetResult(); completion.TrySetResult(); }
        }
    }
    private RunState? StateOf(JobRun run, Invocation invocation)
    {
        if (definition.State is null || records.States is not { } states) return null;
        ChordError Refused() => new("released", $"{definition.Name}: run {run.Id} settled; its state is closed", "settled");
        return new RunState(() => owner.Host.Dispatcher.Invoke(() => states.Value(run.Id) ?? throw Refused()),
            (update, callerContext, persistence) => owner.Host.Dispatcher.RunAsync(async () =>
            {
                if (!invocation.Running) throw Refused();
                if (persistence is { Kind: "deferred", WithinMs: { } delay } && (double.IsNaN(delay) || delay < 0))
                    throw DataInternal.Invalid("args", "persistence is immediate or deferred with withinMs >= 0");
                var policy = run.Restarts == 0 ? persistence : CommitPersistence.Immediate;
                return await owner.Documents.TxAsync(tx => owner.Host.Dispatcher.RunAsync(async () =>
                {
                    var draft = await tx.DraftSlotAsync(states, run.Id);
                    var result = update(draft);
                    if (run.Restarts != 0) records.Change(tx, new("update", run, Restarts: 0));
                    return result;
                }), invocation.Token, ChordContext.WithoutAbortSignal(callerContext), policy);
            }));
    }
    internal void Finish(JobRun run)
    {
        if (!finishing.Add(run)) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Admit(completion.Task);
        _ = Execute();
        async Task Execute()
        {
            var trace = PhaseSpan(run, "finish");
            var phaseContext = trace is null ? context : ChordContext.WithTelemetry(trace.Span, context);
            var delay = 1000;
            try
            {
                while (state != "sealed")
                {
                    try
                    {
                        await owner.Documents.TxAsync(tx => owner.Host.Dispatcher.RunAsync(async () =>
                        {
                            records.StageOf(tx).Finishing = run;
                            if (implementation.Finish is { } finish) await owner.Host.InvokeCallbackAsync(finish, () => finish(records.OutcomeOf(run), tx), owner.DeclaringFacet);
                            records.Change(tx, new("delete", run));
                            return true;
                        }), owner.Token, phaseContext);
                        trace?.Span.SetAttributes(new SpanAttributes { ["outcome"] = "ok" });
                        return;
                    }
                    catch (Exception error)
                    {
                        if (state == "sealed") return;
                        records.Report("job_finish_failed", run, error);
                        var failures = run.FinishFailures + 1;
                        if (failures >= 5)
                        { if (trace is not null) { ChordTelemetry.SpanFailed(trace.Span, "job_failed", error); trace.Span.SetAttributes(new SpanAttributes { ["outcome"] = "error" }); } await records.RecordAsync(new("block", run, Phase: "blocked", FinishFailures: failures, Blocked: "finish_failed")); return; }
                        trace?.Span.AddEvent("chord.retry", new SpanAttributes { ["backoff"] = (double)delay });
                        await records.RecordAsync(new("update", run, FinishFailures: failures));
                        try { await Task.Delay(TimeSpan.FromMilliseconds(delay), owner.Host.Options.TimeProvider, owner.Signal); }
                        catch (OperationCanceledException) { return; }
                        delay = Math.Min(delay * 2, 60000);
                    }
                }
            }
            catch (Exception error) { records.Report("job_record_failed", run, error); }
            finally { trace?.End(); finishing.Remove(run); completion.TrySetResult(); }
        }
    }
    private OpenSpan? PhaseSpan(JobRun run, string phase)
    {
        if (ChordTelemetry.IsUntraced(owner.Host.Telemetry)) return null;
        var carrier = run.Trace?.ToDictionary(each => each.Key, each => each.Value!.GetValue<string>(), StringComparer.Ordinal);
        var attributes = owner.Host.Attributes(obj: owner.Documents.Label); attributes["job"] = definition.Name;
        attributes["phase"] = phase; attributes["job.id"] = run.Id;
        return new OpenSpan(ChordTelemetry.ParentOf(carrier, owner.Host.Telemetry), "chord.job", attributes);
    }
}
