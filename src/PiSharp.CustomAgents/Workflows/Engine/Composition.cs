// Decision 0012: composite steps. Run, Parallel and ForEach start child workflow tasks owned by the run's task and wait for them in the
// commit that creates them (one durable Waiting state); their "<step>#join" phase reads the outcomes and merges them into the Data.
// Aborting the run cascades to the children. WaitForSignal waits on the run document's signal queue, so a signal sent before the run gets
// there is kept, and consumes one signal in the commit that records it.
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.CustomAgents.Workflows.Engine;

internal sealed partial class WorkflowEngine<TData>
{
    /// <summary>Creates child runs and waits for them, in one commit; the join phase continues.</summary>
    internal Task StartChildrenAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, string step,
        IReadOnlyList<(IWorkflowEngine Engine, JsonValue Input)> children, JoinPolicy policy, JsonObjectValue state, DurableContext context,
        IReadOnlyList<TaskId>? earlier = null) =>
        runtime.CommitAsync(async (tx, _) =>
        {
            var created = new List<TaskId>();
            foreach (var (engine, input) in children)
                created.Add(await tx.CreateTaskAsync(engine.Task, input, new TaskOptions(TaskOwnership.Task(task.Id))).ConfigureAwait(false));
            var all = (earlier ?? []).Concat(created).Select(id => (JsonValue)id.ToJson());
            var next = checkpoint with { Phase = step + "#join", Seq = checkpoint.Seq + 1, State = state.SetItem("children", JsonValue.Array(all)) };
            var doc = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
            doc.Set("status", "waiting");
            doc.Set("current", RunDocs.Object(("action", step), ("step", checkpoint.Visit), ("children", JsonValue.Array(created.Select(id => (JsonValue)id.ToJson())))));
            return (TaskState?)NextTaskState.Waiting(next.ToJson(), created, policy);
        }, context);

    /// <summary>The outcomes of <paramref name="ids"/>; children still live (a fail-fast wake) are aborted first.</summary>
    internal static async Task<IReadOnlyList<TaskOutcome>> CollectAsync(ITaskRuntime runtime, IReadOnlyList<TaskId> ids, DurableContext context)
    {
        foreach (var id in ids)
            if (await runtime.GetTaskAsync(id, context).ConfigureAwait(false) is { State: not SettlingTaskState })
                await runtime.AbortOwnedAsync(id, context).ConfigureAwait(false);
        return await runtime.OutcomesAsync(ids, context).ConfigureAwait(false);
    }

    internal static IReadOnlyList<TaskId> Children(Checkpoint checkpoint) =>
        checkpoint.State is { } state && state.TryGetValue("children", out var list) && list is JsonArrayValue ids
            ? [.. ids.OfType<JsonNumberValue>().Select(id => new TaskId((long)id.Value))]
            : [];

    /// <summary>Runs <paramref name="merge"/> on the run's Data and continues after <paramref name="step"/>; a throw fails the run.</summary>
    internal async Task MergeAndContinueAsync(RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, Step<TData> step, Action<TData> merge,
        DurableContext context)
    {
        TData data;
        try
        {
            data = Codec.Deserialize<TData>(checkpoint.Data);
            merge(data);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await FailAsync(task, runtime, checkpoint, step.Name, $"Step '{step.Name}' could not merge its results: {error.Message}", "merge_failed",
                checkpoint.Data, context).ConfigureAwait(false);
            return;
        }
        await TransitionAsync(task, runtime, checkpoint, step.Name, "next", data, NextOf(step), null, context).ConfigureAwait(false);
    }

    internal IWorkflowEngine EngineOf(WorkflowDefinition child) => Catalog.Engine(child);
}

/// <summary>Run: one child workflow.</summary>
internal sealed class RunStep<TData, TChild>(string name, Workflow<TChild> child, Func<TData, TChild> input, Action<TData, WorkflowResult<TChild>> output,
    bool failOnError) : Step<TData>(name) where TData : class where TChild : class
{
    public override string Kind => "run:" + child.Name;
    public override IEnumerable<WorkflowDefinition> SubWorkflows => [child];

    public override void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine)
    {
        phases[Name] = async (task, runtime, context) =>
        {
            var checkpoint = Checkpoint.Parse(task.Checkpoint);
            var childEngine = engine.EngineOf(child);
            JsonValue childInput;
            try { childInput = childEngine.Input(input(engine.Codec.Deserialize<TData>(checkpoint.Data))); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await engine.FailAsync(task, runtime, checkpoint, Name, $"Step '{Name}' could not build the input of '{child.Name}': {error.Message}",
                    "input_failed", checkpoint.Data, context).ConfigureAwait(false);
                return;
            }
            await engine.StartChildrenAsync(task, runtime, checkpoint, Name, [(childEngine, childInput)], JoinPolicy.AllSettled, JsonObjectValue.Empty,
                context).ConfigureAwait(false);
        };
        phases[Name + "#join"] = async (task, runtime, context) =>
        {
            var checkpoint = Checkpoint.Parse(task.Checkpoint);
            var ids = WorkflowEngine<TData>.Children(checkpoint);
            var outcomes = await WorkflowEngine<TData>.CollectAsync(runtime, ids, context).ConfigureAwait(false);
            var result = (WorkflowResult<TChild>)engine.EngineOf(child).Result(ids[0], outcomes[0]);
            if (failOnError && !result.Completed)
            {
                await engine.FailAsync(task, runtime, checkpoint, Name, $"Sub-workflow '{child.Name}' {result.Status.ToString().ToLowerInvariant()}: {result.Error}",
                    "child_failed", checkpoint.Data, context).ConfigureAwait(false);
                return;
            }
            await engine.MergeAndContinueAsync(task, runtime, checkpoint, this, data => output(data, result), context).ConfigureAwait(false);
        };
    }
}

/// <summary>Parallel: branches over copies of the Data, merged when they end.</summary>
internal sealed class ParallelStep<TData>(string name, IReadOnlyList<CompiledBranch<TData>> branches,
    Action<TData, IReadOnlyList<BranchResult<TData>>> merge, JoinPolicy policy) : Step<TData>(name) where TData : class
{
    public override string Kind => "parallel";
    public override string Shape => string.Join(",", branches.Select(branch => branch.Name)) + ";" + policy;
    public override IEnumerable<WorkflowDefinition> SubWorkflows => branches.Select(branch => branch.Workflow);

    public override void Validate(string workflow)
    {
        if (branches.Count == 0) throw new WorkflowDefinitionException($"Parallel step '{Name}' of workflow '{workflow}' has no branches.");
        if (branches.Select(branch => branch.Name).Distinct(StringComparer.Ordinal).Count() != branches.Count)
            throw new WorkflowDefinitionException($"Parallel step '{Name}' of workflow '{workflow}' has two branches with one name.");
    }

    public override void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine)
    {
        phases[Name] = (task, runtime, context) =>
        {
            var checkpoint = Checkpoint.Parse(task.Checkpoint);
            var data = engine.Codec.Deserialize<TData>(checkpoint.Data);
            var children = branches.Select(branch => (engine.EngineOf(branch.Workflow), engine.EngineOf(branch.Workflow).Input(data))).ToList();
            return engine.StartChildrenAsync(task, runtime, checkpoint, Name, children, policy, JsonObjectValue.Empty, context);
        };
        phases[Name + "#join"] = async (task, runtime, context) =>
        {
            var checkpoint = Checkpoint.Parse(task.Checkpoint);
            var ids = WorkflowEngine<TData>.Children(checkpoint);
            var outcomes = await WorkflowEngine<TData>.CollectAsync(runtime, ids, context).ConfigureAwait(false);
            var results = branches.Select((branch, index) =>
                new BranchResult<TData>(branch.Name, (WorkflowResult<TData>)engine.EngineOf(branch.Workflow).Result(ids[index], outcomes[index]))).ToList();
            if (policy == JoinPolicy.FailFast && results.FirstOrDefault(result => !result.Result.Completed) is { } failed)
            {
                await engine.FailAsync(task, runtime, checkpoint, Name,
                    $"Branch '{failed.Branch}' of '{Name}' {failed.Result.Status.ToString().ToLowerInvariant()}: {failed.Result.Error}", "child_failed",
                    checkpoint.Data, context).ConfigureAwait(false);
                return;
            }
            await engine.MergeAndContinueAsync(task, runtime, checkpoint, this, data => merge(data, results), context).ConfigureAwait(false);
        };
    }
}

/// <summary>ForEach: one child run per item, at most <c>Concurrency</c> at a time (in batches).</summary>
internal sealed class ForEachStep<TData, TItem, TChild>(string name, Func<TData, IEnumerable<TItem>> items, Workflow<TChild> body,
    Func<TData, TItem, int, TChild> input, Action<TData, IReadOnlyList<ItemResult<TItem, TChild>>> merge, ForEachOptions options)
    : Step<TData>(name) where TData : class where TChild : class
{
    public override string Kind => "foreach:" + body.Name;
    public override IEnumerable<WorkflowDefinition> SubWorkflows => [body];

    public override void Validate(string workflow)
    {
        if (options.Concurrency < 1) throw new WorkflowDefinitionException($"ForEach step '{Name}' of workflow '{workflow}' has Concurrency below 1.");
    }

    public override void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine)
    {
        phases[Name] = async (task, runtime, context) =>
        {
            var checkpoint = Checkpoint.Parse(task.Checkpoint);
            var list = items(engine.Codec.Deserialize<TData>(checkpoint.Data)).ToList();
            if (list.Count == 0)
            {
                await engine.MergeAndContinueAsync(task, runtime, checkpoint, this, data => merge(data, []), context).ConfigureAwait(false);
                return;
            }
            await StartBatchAsync(engine, task, runtime, checkpoint, list, [], context).ConfigureAwait(false);
        };
        phases[Name + "#join"] = async (task, runtime, context) =>
        {
            var checkpoint = Checkpoint.Parse(task.Checkpoint);
            var data = engine.Codec.Deserialize<TData>(checkpoint.Data);
            var list = items(data).ToList();
            var ids = WorkflowEngine<TData>.Children(checkpoint);
            var batch = ids.Skip(ids.Count - (checkpoint.State is { } state && state.TryGetValue("batch", out var size) ? (int)((JsonNumberValue)size).Value : ids.Count)).ToList();
            var batchOutcomes = await WorkflowEngine<TData>.CollectAsync(runtime, batch, context).ConfigureAwait(false);
            var bodyEngine = engine.EngineOf(body);
            if (options.JoinPolicy == JoinPolicy.FailFast &&
                batchOutcomes.Select((outcome, index) => (WorkflowResult<TChild>)bodyEngine.Result(batch[index], outcome)).FirstOrDefault(result => !result.Completed)
                    is { } failed)
            {
                await engine.FailAsync(task, runtime, checkpoint, Name, $"An item of '{Name}' {failed.Status.ToString().ToLowerInvariant()}: {failed.Error}",
                    "child_failed", checkpoint.Data, context).ConfigureAwait(false);
                return;
            }
            if (ids.Count < list.Count)
            {
                await StartBatchAsync(engine, task, runtime, checkpoint, list, ids, context).ConfigureAwait(false);
                return;
            }
            var outcomes = await runtime.OutcomesAsync(ids, context).ConfigureAwait(false);
            var results = list.Select((item, index) => new ItemResult<TItem, TChild>(item, index, (WorkflowResult<TChild>)bodyEngine.Result(ids[index], outcomes[index])))
                .ToList();
            await engine.MergeAndContinueAsync(task, runtime, checkpoint, this, current => merge(current, results), context).ConfigureAwait(false);
        };
    }

    private Task StartBatchAsync(WorkflowEngine<TData> engine, RunningTask task, ITaskRuntime runtime, Checkpoint checkpoint, List<TItem> list,
        IReadOnlyList<TaskId> started, DurableContext context)
    {
        var data = engine.Codec.Deserialize<TData>(checkpoint.Data);
        var bodyEngine = engine.EngineOf(body);
        var next = list.Skip(started.Count).Take(options.Concurrency)
            .Select((item, offset) => (bodyEngine, bodyEngine.Input(input(data, item, started.Count + offset)))).ToList();
        var state = RunDocs.Object(("batch", next.Count), ("total", list.Count));
        return engine.StartChildrenAsync(task, runtime, checkpoint, Name, next, JoinPolicy.AllSettled, state, context, started);
    }
}

/// <summary>WaitForSignal: waits for a named signal sent with <see cref="WorkflowRun{TData}.SignalAsync{T}"/>.</summary>
internal sealed class SignalStep<TData, TSignal>(string name, string signal, Action<TData, TSignal> apply, TimeSpan? timeout, string? onTimeout)
    : Step<TData>(name) where TData : class
{
    public override string Kind => "signal:" + signal;
    public override IEnumerable<string> StaticTargets => onTimeout is null ? [] : [onTimeout];

    public override void AddPhases(IDictionary<string, TaskPhaseHandler> phases, WorkflowEngine<TData> engine) =>
        phases[Name] = (task, runtime, context) => RunAsync(engine, task, runtime, context);

    private async Task RunAsync(WorkflowEngine<TData> engine, RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = Checkpoint.Parse(task.Checkpoint);
        var state = checkpoint.State ?? JsonObjectValue.Empty;
        if (state.TryGetValue("received", out var received))
        {
            TData data;
            try
            {
                data = engine.Codec.Deserialize<TData>(checkpoint.Data);
                apply(data, engine.Codec.Deserialize<TSignal>(received, "signal '" + signal + "'"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await engine.FailAsync(task, runtime, checkpoint, Name, $"Signal '{signal}' could not be applied: {error.Message}", "signal_failed",
                    checkpoint.Data, context).ConfigureAwait(false);
                return;
            }
            await engine.TransitionAsync(task, runtime, checkpoint, Name, "signal", data, engine.NextOf(this), null, context).ConfigureAwait(false);
            return;
        }
        double? deadline = state.TryGetValue("deadline", out var at) && at is JsonNumberValue when ? when.Value : null;
        if (timeout is { } limit && deadline is null)
        {
            // Persist the deadline first, so a restart keeps the original one.
            var withDeadline = checkpoint with { Seq = checkpoint.Seq + 1, State = state.SetItem("deadline", runtime.Now() + limit.TotalMilliseconds) };
            await runtime.CommitAsync((_, _) => (TaskState?)NextTaskState.Running(withDeadline.ToJson()), context).ConfigureAwait(false);
            return;
        }
        var key = RunDocs.Key(task.Id);
        while (true)
        {
            var doc = await runtime.SnapshotAsync(RunDocs.Run, task.ConversationId, key, context).ConfigureAwait(false);
            if (First(doc) is not null)
            {
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var draft = await RunDocs.RunAsync(tx, task.ConversationId, task.Id).ConfigureAwait(false);
                    // Only this run consumes its signals; should the queue have changed, loop again (with progress).
                    if (draft.Get("signals") is not JsonArrayValue queue || First(queue) is not { } index)
                        return (TaskState?)NextTaskState.Running((checkpoint with { Seq = checkpoint.Seq + 1 }).ToJson());
                    var value = ((JsonObjectValue)queue[index])["value"];
                    draft.GetArray("signals").RemoveAt(index);
                    return (TaskState?)NextTaskState.Running((checkpoint with { Seq = checkpoint.Seq + 1, State = state.SetItem("received", value) }).ToJson());
                }, context).ConfigureAwait(false);
                return;
            }
            if (deadline is { } due && runtime.Now() >= due)
            {
                if (onTimeout is not null)
                {
                    var data = engine.Codec.Deserialize<TData>(checkpoint.Data);
                    await engine.TransitionAsync(task, runtime, checkpoint, Name, "timeout", data, onTimeout, null, context).ConfigureAwait(false);
                }
                else
                    await engine.FailAsync(task, runtime, checkpoint, Name, $"No signal '{signal}' arrived within {timeout}.", "timeout", checkpoint.Data,
                        context).ConfigureAwait(false);
                return;
            }
            await WaitForChangeAsync(runtime, task, key, deadline, context).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the run document holds a matching signal or <paramref name="deadline"/> passes.</summary>
    private async Task WaitForChangeAsync(ITaskRuntime runtime, RunningTask task, string key, double? deadline, DurableContext context)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = await runtime.WatchDocAsync(RunDocs.Run, task.ConversationId, key, context).ConfigureAwait(false);
        var sleeping = context.WithCancel();
        try
        {
            if (watch is not null)
            {
                watch.Start((value, _, _) =>
                {
                    if (First(value) is not null) arrived.TrySetResult();
                    return Task.CompletedTask;
                });
                if (First(watch.Value) is not null) arrived.TrySetResult();
            }
            var sleep = deadline is { } due ? runtime.SleepAsync(due, sleeping.Context) : Task.Delay(Timeout.Infinite, sleeping.Context.CancellationToken);
            await Task.WhenAny(arrived.Task, sleep).ConfigureAwait(false);
            context.ThrowIfCancellationRequested();
        }
        finally
        {
            sleeping.Cancel();
            if (watch is not null) await watch.StopAsync().ConfigureAwait(false);
        }
    }

    private int? First(JsonObjectValue? doc) => doc is not null && doc.TryGetValue("signals", out var queue) && queue is JsonArrayValue list ? First(list) : null;

    private int? First(JsonArrayValue queue)
    {
        for (var index = 0; index < queue.Count; index++)
            if (queue[index] is JsonObjectValue item && item.TryGetValue("name", out var named) && named is JsonStringValue text && text.Value == signal)
                return index;
        return null;
    }
}
