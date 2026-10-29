// Decision 0012: compiles a workflow onto one durable task definition, pisharp.workflow.<name>. Phases: "#start", one per action named
// after it, "<step>#join" for composite steps, and "#end" for a Sleep before the end. Every phase commits exactly one state change; the
// Data and the next step move together in that commit, so they are exactly once, while the action body that produced them is at least
// once. Every phase but "#start" first waits for the checkpoint's wake time, so a Sleep holds whatever step comes next.
using System.Collections.Immutable;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows.Engine;

/// <summary>A compiled workflow.</summary>
internal interface IWorkflowEngine
{
    WorkflowDefinition Workflow { get; }
    DurableTask Task { get; }
    /// <summary>Extensions the workflow needs installed besides its own (typed prompt result tools).</summary>
    IReadOnlyList<Extension> Extensions { get; }
    /// <summary>Task definitions installed with the workflow's own (Parallel branches).</summary>
    IReadOnlyList<DurableTask> ChildTasks { get; }
    /// <summary>Workflows this one runs as children; the catalog adds them too.</summary>
    IReadOnlyList<WorkflowDefinition> SubWorkflows { get; }
    /// <summary>The task input for a run starting with <paramref name="data"/> (a TData).</summary>
    JsonValue Input(object data);
    /// <summary>The Data codec (also used for signal values).</summary>
    DataCodec Codec { get; }
    /// <summary>Reads a terminal record's outcome as a result.</summary>
    object Result(TaskRecord record);
    /// <summary>Reads a child run's outcome as a result.</summary>
    object Result(TaskId id, TaskOutcome outcome);
}

/// <summary>What an <see cref="ActionContext"/> uses from its engine.</summary>
internal interface IStepScope
{
    string WorkflowName { get; }
    DataCodec Codec { get; }
    IServiceProvider? Services { get; }
    WorkflowCatalog Catalog { get; }
    WorkflowDefinition Definition { get; }
    /// <summary>The submit_result tool of a declared result type; null when undeclared.</summary>
    ResultTool? ResultFor(Type type);
}

internal sealed partial class WorkflowEngine<TData> : IWorkflowEngine, IStepScope where TData : class
{
    private const int MaxRouterHops = 64;
    private readonly Workflow<TData> workflow;

    public WorkflowEngine(Workflow<TData> workflow, WorkflowCatalog catalog)
    {
        this.workflow = workflow;
        Catalog = catalog;
        Codec = new DataCodec(workflow.JsonOptions, workflow.Limits.MaxDataBytes);
        var phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal) { ["#start"] = StartAsync };
        foreach (var step in workflow.Nodes) step.AddPhases(phases, this);
        phases[WorkflowEnd] = EndAsync;
        foreach (var name in phases.Keys.Where(name => name != "#start").ToList()) phases[name] = Waking(phases[name]);
        Extensions = [.. workflow.Nodes.SelectMany(step => step.Extensions), .. workflow.Results.Values.Select(result => result.Extension)];
        ChildTasks = [.. workflow.Nodes.SelectMany(step => step.ChildTasks(this))];
        SubWorkflows = [.. workflow.Nodes.SelectMany(step => step.SubWorkflows)];
        Task =Tasks.DefineTask(new TaskDefinition
        {
            Name = workflow.TaskKind,
            Version = workflow.Version,
            Initial = input => new Checkpoint("#start", 0, 0, 1, 0, ((JsonObjectValue)input)["data"], ImmutableDictionary<string, int>.Empty).ToJson(),
            Phases = new PhaseTable(phases, MissingPhaseAsync),
            Abort = AbortAsync,
            Migrate = workflow.Migration?.ToTaskMigration(),
        });
    }

    public Workflow<TData> Typed => workflow;
    public WorkflowDefinition Workflow => workflow;
    public WorkflowDefinition Definition => workflow;
    public DurableTask Task { get; }
    public IReadOnlyList<Extension> Extensions { get; }
    public IReadOnlyList<DurableTask> ChildTasks { get; }
    public IReadOnlyList<WorkflowDefinition> SubWorkflows { get; }
    public WorkflowCatalog Catalog { get; }
    public DataCodec Codec { get; }
    public string WorkflowName => workflow.Name;
    public IServiceProvider? Services => Catalog.Options.Services;
    private TimeProvider Time => Catalog.Options.TimeProvider ?? TimeProvider.System;

    public ResultTool? ResultFor(Type type) => workflow.Results.GetValueOrDefault(type);

    public JsonValue Input(object data) => RunDocs.Object(
        ("workflow", workflow.Name), ("version", workflow.Version), ("fingerprint", workflow.Fingerprint),
        ("data", Codec.Serialize((TData)data, "initial data")));

    public object Result(TaskRecord record) => WorkflowResult<TData>.From(record, Codec);
    public object Result(TaskId id, TaskOutcome outcome) => WorkflowResult<TData>.From(id, outcome, Codec);

    // ─── Phases ────────────────────────────────────────────────────────────

    private async Task StartAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = Checkpoint.Parse(task.Checkpoint);
        ReportDrift(task, runtime);
        TData data;
        try { data = Codec.Deserialize<TData>(checkpoint.Data); }
        catch (WorkflowDataException error)
        {
            await FailAsync(task, runtime, checkpoint, "#start", error.Message, "data", checkpoint.Data, context).ConfigureAwait(false);
            return;
        }
        var first = workflow.Nodes.Length > 0 ? workflow.Nodes[0].Name : WorkflowEnd;
        await TransitionAsync(task, runtime, checkpoint, "#start", "start", data, first, null, context).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="phase"/> once the checkpoint's wake time (a Sleep, a Repeat or a retry delay) has passed.</summary>
    private static TaskPhaseHandler Waking(TaskPhaseHandler phase) => async (task, runtime, context) =>
    {
        if (Checkpoint.Parse(task.Checkpoint).WakeAt is { } wake && wake > runtime.Now()) await runtime.SleepAsync(wake, context).ConfigureAwait(false);
        await phase(task, runtime, context).ConfigureAwait(false);
    };

    /// <summary>The end after a Sleep: completes the run, stopped at the action that slept.</summary>
    private Task EndAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = Checkpoint.Parse(task.Checkpoint);
        var from = checkpoint.State is { } state && state.TryGetValue("from", out var value) && value is JsonStringValue text ? text.Value : WorkflowEnd;
        return CompleteJsonAsync(task, runtime, checkpoint, from, checkpoint.Data, "end", context);
    }

    /// <summary>Runs one attempt of an action and commits its outcome.</summary>
    internal async Task RunActionAsync(ActionStep<TData> step, RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = Checkpoint.Parse(task.Checkpoint);
        var replay = await MarkStartedAsync(task, runtime, checkpoint, step.Name, context).ConfigureAwait(false);
        TData data;
        try { data = Codec.Deserialize<TData>(checkpoint.Data); }
        catch (WorkflowDataException error)
        {
            await FailAsync(task, runtime, checkpoint, step.Name, error.Message, "data", checkpoint.Data, context).ConfigureAwait(false);
            return;
        }
        var actionContext = new ActionContext(this, step.Name, task, runtime, context, checkpoint, replay);
        ActionResult? result = null;
        Exception? failure = null;
        using (var timeout = step.Options.Timeout is { } limit ? new CancellationTokenSource(limit, Time) : null)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, runtime.Signal, timeout?.Token ?? default))
        {
            try
            {
                result = await step.Action.ExecuteAsync(data, actionContext, linked.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Action '{step.Name}' returned no result.");
                if (result.ReplacedData is { } replaced) data = Codec.Deserialize<TData>(replaced, "data the action returned");
            }
            catch (Exception error) when (error is not OperationCanceledException || !Stopping(runtime, context))
            {
                failure = error is OperationCanceledException && timeout is { IsCancellationRequested: true }
                    ? new TimeoutException($"Action '{step.Name}' timed out after {step.Options.Timeout}.", error)
                    : error;
            }
        }
        if (failure is not null)
        {
            await RetryOrFailAsync(task, runtime, checkpoint, step.Name, step.Options.Retry, failure, context).ConfigureAwait(false);
            return;
        }
        await ApplyAsync(task, runtime, checkpoint, step, data, result!, context).ConfigureAwait(false);
    }

    /// <summary>Commits what <paramref name="result"/> decides for <paramref name="step"/>.</summary>
    internal Task ApplyAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, Step<TData> step, TData data, ActionResult result,
        DurableContext context)
    {
        var now = runtime.Now();
        return result.Kind switch
        {
            ActionResultKind.Stop => CompleteAsync(task, runtime, checkpoint, step.Name, data, "stop", context),
            ActionResultKind.Fail => FailWithDataAsync(task, runtime, checkpoint, step.Name, result.Reason!, data, context),
            ActionResultKind.GoTo => TransitionAsync(task, runtime, checkpoint, step.Name, "goto", data, result.Target!, null, context),
            ActionResultKind.Repeat => TransitionAsync(task, runtime, checkpoint, step.Name, "repeat", data, step.Name,
                now + result.Delay!.Value.TotalMilliseconds, context, route: false),
            ActionResultKind.Sleep => TransitionAsync(task, runtime, checkpoint, step.Name, "sleep", data, result.Target ?? NextOf(step),
                now + result.Delay!.Value.TotalMilliseconds, context),
            _ => TransitionAsync(task, runtime, checkpoint, step.Name, "next", data, NextOf(step), null, context),
        };
    }

    /// <summary>A failed attempt: retried as a new attempt of the same visit when the policy allows, else the run fails.</summary>
    internal Task RetryOrFailAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string action, RetryPolicy? policy, Exception failure,
        DurableContext context)
    {
        policy ??= RetryPolicy.None;
        var detail = failure is WorkflowDataException data && data.Path is { } path ? path : null;
        if (checkpoint.Attempt < policy.MaxAttempts && (policy.RetryOn?.Invoke(failure) ?? true) && failure is not WorkflowDataException)
        {
            var delay = policy.DelayAfter(checkpoint.Attempt);
            var next = checkpoint with
            {
                Seq = checkpoint.Seq + 1,
                Attempt = checkpoint.Attempt + 1,
                WakeAt = delay > TimeSpan.Zero ? runtime.Now() + delay.TotalMilliseconds : null,
            };
            return runtime.CommitAsync(async (tx, _) =>
            {
                var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
                RunDocs.Append(doc, "history", RunDocs.Object(("seq", checkpoint.Seq), ("action", action), ("attempt", checkpoint.Attempt),
                    ("outcome", "retry"), ("error", failure.Message), ("at", runtime.Now())), RunDocs.HistoryLimit);
                return (TaskState?)NextTaskState.Running(next.ToJson());
            }, context);
        }
        var attempts = checkpoint.Attempt == 1 ? "" : $" after {checkpoint.Attempt} attempts";
        return FailAsync(task, runtime, checkpoint, action, $"Action '{action}' failed{attempts}: {failure.Message}",
            failure is WorkflowDataException ? "data" : "action_failed", checkpoint.Data, context, failure.GetType().FullName, detail);
    }

    /// <summary>
    /// Moves the run to <paramref name="target"/>: serializes the Data, follows routers, checks the limits, and commits the new checkpoint
    /// with a history line, or completes the run at <see cref="Workflow.End"/>.
    /// </summary>
    internal async Task TransitionAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string from, string outcome, TData data,
        string target, double? wakeAt, DurableContext context, bool route = true, JsonObjectValue? state = null)
    {
        JsonValue json;
        try { json = Codec.Serialize(data); }
        catch (WorkflowDataException error)
        {
            await FailAsync(task, runtime, checkpoint, from, error.Message, "data", checkpoint.Data, context, null, error.Path).ConfigureAwait(false);
            return;
        }
        string resolved;
        try { resolved = route ? Resolve(target, data) : target; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await FailAsync(task, runtime, checkpoint, from, error.Message, "routing", json, context).ConfigureAwait(false);
            return;
        }
        var visits = checkpoint.Visits;
        var steps = checkpoint.Steps;
        if (resolved == WorkflowEnd)
        {
            if (wakeAt is null)
            {
                await CompleteJsonAsync(task, runtime, checkpoint, from, json, outcome == "next" ? "end" : outcome, context).ConfigureAwait(false);
                return;
            }
            // A Sleep before the end: the "#end" phase completes the run once it wakes.
            state = RunDocs.Object(("from", from));
        }
        else
        {
            visits = visits.SetItem(resolved, visits.GetValueOrDefault(resolved) + 1);
            steps++;
            if (steps > workflow.Limits.MaxSteps)
            {
                await FailAsync(task, runtime, checkpoint, from, $"The run entered {workflow.Limits.MaxSteps} actions without ending (MaxSteps).",
                    "limit", json, context).ConfigureAwait(false);
                return;
            }
            if (workflow.ByName[resolved].MaxVisits is { } most && visits[resolved] > most)
            {
                await FailAsync(task, runtime, checkpoint, from, $"Action '{resolved}' was entered more than {most} times (MaxVisits).", "limit", json,
                    context).ConfigureAwait(false);
                return;
            }
        }
        var next = new Checkpoint(resolved, checkpoint.Seq + 1, checkpoint.Seq + 1, 1, steps, json, visits, wakeAt, state);
        await runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            if (from != "#start")
                RunDocs.Append(doc, "history", RunDocs.Object(("seq", checkpoint.Seq), ("action", from), ("attempt", checkpoint.Attempt),
                    ("outcome", outcome), ("next", resolved), ("at", runtime.Now())), RunDocs.HistoryLimit);
            doc.Set("status", wakeAt is null ? "running" : "sleeping");
            doc.Set("current", RunDocs.Object(("action", resolved), ("step", next.Visit), ("wakeAt", wakeAt is { } at ? at : null)));
            return (TaskState?)NextTaskState.Running(next.ToJson());
        }, context).ConfigureAwait(false);
    }

    /// <summary>Follows routers from <paramref name="target"/> to an action, a composite step, or the end.</summary>
    internal string Resolve(string target, TData data)
    {
        for (var hops = 0; hops < MaxRouterHops; hops++)
        {
            if (target == WorkflowEnd) return target;
            if (!workflow.ByName.TryGetValue(target, out var step))
                throw new InvalidOperationException($"Workflow '{workflow.Name}' has no step '{target}'.");
            if (step is not RouterStep<TData> router) return target;
            target = router.Select(data) ?? throw new InvalidOperationException($"Router '{router.Name}' chose no step.");
        }
        throw new InvalidOperationException($"Routers of workflow '{workflow.Name}' did not reach an action within {MaxRouterHops} hops.");
    }

    /// <summary>The step after <paramref name="step"/>: its Next option, else the following step, else the end.</summary>
    internal string NextOf(Step<TData> step)
    {
        if (step.NextOverride is { } next) return next;
        var index = workflow.Nodes.IndexOf(step);
        return index + 1 < workflow.Nodes.Length ? workflow.Nodes[index + 1].Name : WorkflowEnd;
    }

    internal Task CompleteAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string action, TData data, string outcome,
        DurableContext context)
    {
        JsonValue json;
        try { json = Codec.Serialize(data); }
        catch (WorkflowDataException error)
        {
            return FailAsync(task, runtime, checkpoint, action, error.Message, "data", checkpoint.Data, context, null, error.Path);
        }
        return CompleteJsonAsync(task, runtime, checkpoint, action, json, outcome, context);
    }

    private Task CompleteJsonAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string action, JsonValue data, string outcome,
        DurableContext context) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            RunDocs.Append(doc, "history", RunDocs.Object(("seq", checkpoint.Seq), ("action", action), ("attempt", checkpoint.Attempt),
                ("outcome", outcome), ("at", runtime.Now())), RunDocs.HistoryLimit);
            doc.Set("status", "completed");
            doc.Delete("current");
            return (TaskState?)NextTaskState.Completed(RunDocs.Object(("data", data), ("stoppedAt", action), ("steps", checkpoint.Steps)));
        }, context);

    private Task FailWithDataAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string action, string reason, TData data,
        DurableContext context)
    {
        JsonValue json;
        try { json = Codec.Serialize(data); }
        catch (WorkflowDataException) { json = checkpoint.Data; }
        return FailAsync(task, runtime, checkpoint, action, reason, "failed", json, context);
    }

    /// <summary>Ends the run failed, with its Data in the outcome's result.</summary>
    internal Task FailAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string action, string message, string kind, JsonValue data,
        DurableContext context, string? errorType = null, string? path = null) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            RunDocs.Append(doc, "history", RunDocs.Object(("seq", checkpoint.Seq), ("action", action), ("attempt", checkpoint.Attempt),
                ("outcome", "fail"), ("error", message), ("at", runtime.Now())), RunDocs.HistoryLimit);
            doc.Set("status", "failed");
            doc.Set("error", message);
            doc.Delete("current");
            var detail = RunDocs.Object(("kind", kind), ("action", action), ("attempt", checkpoint.Attempt), ("type", RunDocs.Opt(errorType)), ("path", RunDocs.Opt(path)));
            return (TaskState?)NextTaskState.Terminal(new FailedTaskOutcome(new TaskOutcomeError(message, detail),
                RunDocs.Object(("data", data), ("failedAt", action), ("steps", checkpoint.Steps))));
        }, context);

    /// <summary>Records that an attempt at this checkpoint started; true when an earlier invocation already started it (a replay).</summary>
    internal static async Task<bool> MarkStartedAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string action, DurableContext context)
    {
        var replay = false;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            replay = doc.Get("started") is JsonNumberValue started && started.Value == checkpoint.Seq;
            if (replay) return;
            doc.Set("started", checkpoint.Seq);
            doc.Set("status", "running");
            doc.Set("current", RunDocs.Object(("action", action), ("step", checkpoint.Visit), ("attempt", checkpoint.Attempt),
                ("startedAt", runtime.Now())));
        }, context).ConfigureAwait(false);
        return replay;
    }

    /// <summary>The abort invocation: runs the OnAbort handler (at least once) and ends the run aborted, with its Data.</summary>
    private async Task AbortAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = Checkpoint.Parse(task.Checkpoint);
        var json = checkpoint.Data;
        if (workflow.AbortHandler is { } handler)
        {
            try
            {
                var data = Codec.Deserialize<TData>(checkpoint.Data);
                var actionContext = new ActionContext(this, "#abort", task, runtime, context, checkpoint, false);
                await handler(data, actionContext, runtime.Signal).ConfigureAwait(false);
                json = Codec.Serialize(data);
            }
            catch (Exception error) when (error is not OperationCanceledException || !Stopping(runtime, context))
            {
                runtime.Report(error);
            }
        }
        var reason = task.AbortReason is { } why ? why.ToString().ToLowerInvariant() : null;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            doc.Set("status", "aborted");
            doc.Delete("current");
            return (TaskState?)NextTaskState.Terminal(new AbortedTaskOutcome(reason, RunDocs.Object(("data", json),
                ("abortedAt", checkpoint.Phase), ("steps", checkpoint.Steps))));
        }, context).ConfigureAwait(false);
    }

    /// <summary>A checkpoint names a phase this definition lacks (an action renamed or removed without a version bump).</summary>
    private Task MissingPhaseAsync(RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = Checkpoint.Parse(task.Checkpoint);
        ReportDrift(task, runtime);
        return FailAsync(task, runtime, checkpoint, checkpoint.Phase,
            $"Workflow '{workflow.Name}' version {workflow.Version} has no action '{checkpoint.Phase}'; bump Version and add a migration when renaming or removing actions.",
            "missing_action", checkpoint.Data, context);
    }

    private void ReportDrift(RunningTask task, ITaskRuntime runtime)
    {
        if (task.Input is JsonObjectValue input && input.TryGetValue("fingerprint", out var stored) && stored is JsonStringValue fingerprint &&
            fingerprint.Value != workflow.Fingerprint && task.Version == workflow.Version)
            runtime.Report(new InvalidOperationException(
                $"Workflow '{workflow.Name}' changed shape at version {workflow.Version} while run {task.Id.Value} was live; bump Version when steps change."));
    }

    internal static bool Stopping(ITaskRuntime runtime, DurableContext context) =>
        runtime.Signal.IsCancellationRequested || context.IsCancellationRequested;

    private const string WorkflowEnd = Workflows.Workflow.End;
}

/// <summary>
/// The phase map of a workflow task. The scheduler looks phases up only with <see cref="TryGetValue"/>; an unknown phase gets a handler
/// that fails the run with guidance instead of faulting the task.
/// </summary>
internal sealed class PhaseTable(IReadOnlyDictionary<string, TaskPhaseHandler> phases, TaskPhaseHandler missing) : IReadOnlyDictionary<string, TaskPhaseHandler>
{
    public TaskPhaseHandler this[string key] => TryGetValue(key, out var handler) ? handler : throw new KeyNotFoundException(key);
    public IEnumerable<string> Keys => phases.Keys;
    public IEnumerable<TaskPhaseHandler> Values => phases.Values;
    public int Count => phases.Count;
    public bool ContainsKey(string key) => phases.ContainsKey(key);
    public IEnumerator<KeyValuePair<string, TaskPhaseHandler>> GetEnumerator() => phases.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TaskPhaseHandler value)
    {
        if (phases.TryGetValue(key, out value)) return true;
        value = missing;
        return true;
    }
}
