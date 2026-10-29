// Decision 0012: a handle to one workflow run, and its result.
using System.Collections.Immutable;
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

namespace PiSharp.CustomAgents.Workflows.Hosting;

/// <summary>How a run stands or ended.</summary>
public enum WorkflowRunStatus
{
    /// <summary>Created, not started.</summary>
    Pending,
    /// <summary>Running an action, or waiting (sleep, retry delay, children, signal).</summary>
    Running,
    /// <summary>Ended at <see cref="Workflow.End"/> or <see cref="ActionResult.Stop"/>.</summary>
    Completed,
    /// <summary>Ended by <see cref="ActionResult.Fail"/>, an exhausted retry policy, a limit, or a Data error.</summary>
    Failed,
    /// <summary>Aborted.</summary>
    Aborted,
    /// <summary>Ended without running its own outcome (faulted or orphaned by durable).</summary>
    Faulted,
}

/// <summary>One line of a run's history.</summary>
public sealed record WorkflowHistoryEntry(long Seq, string Action, int Attempt, string Outcome, string? Next, string? Error, double At);

/// <summary>A run's current state.</summary>
public sealed record WorkflowRunState(WorkflowRunStatus Status, string? CurrentAction, long? Step, ImmutableArray<WorkflowHistoryEntry> History,
    ImmutableArray<string> Log, string? Error, TaskRecord? Task);

/// <summary>The result of a finished run.</summary>
public sealed class WorkflowResult<TData> where TData : class
{
    private WorkflowResult(WorkflowRunStatus status, TData? data, string? error, string? errorKind, string? stoppedAt, int steps, TaskId taskId)
    {
        Status = status;
        Data = data;
        Error = error;
        ErrorKind = errorKind;
        StoppedAt = stoppedAt;
        Steps = steps;
        TaskId = taskId;
    }

    /// <summary>How the run ended.</summary>
    public WorkflowRunStatus Status { get; }
    /// <summary>The final Data (also for failed and aborted runs); null when it could not be read.</summary>
    public TData? Data { get; }
    /// <summary>Why the run did not complete.</summary>
    public string? Error { get; }
    /// <summary>The failure kind: <c>failed</c> (an action's Fail), <c>action_failed</c>, <c>limit</c>, <c>data</c>, <c>routing</c>,
    /// <c>missing_action</c>, or a durable outcome status.</summary>
    public string? ErrorKind { get; }
    /// <summary>The action the run ended at.</summary>
    public string? StoppedAt { get; }
    /// <summary>How many actions the run entered.</summary>
    public int Steps { get; }
    /// <summary>The run's task.</summary>
    public TaskId TaskId { get; }
    /// <summary>Whether the run completed.</summary>
    public bool Completed => Status == WorkflowRunStatus.Completed;

    /// <summary>The Data of a completed run; throws <see cref="WorkflowFailedException"/> otherwise.</summary>
    public TData EnsureCompleted() => Completed && Data is not null ? Data
        : throw new WorkflowFailedException($"Workflow run {TaskId.Value} {Status.ToString().ToLowerInvariant()}: {Error ?? "no result"}", Status);

    /// <inheritdoc/>
    public override string ToString() => Status + (Error is null ? "" : ": " + Error);

    internal static WorkflowResult<TData> From(TaskRecord record, DataCodec codec) =>
        From(record.Id, (record.State as SettlingTaskState)?.Outcome ?? throw new InvalidOperationException($"Task {record.Id.Value} has not ended."), codec);

    internal static WorkflowResult<TData> From(TaskId taskId, TaskOutcome outcome, DataCodec codec)
    {
        var result = outcome.Result as JsonObjectValue;
        TData? data = null;
        if (result is not null && result.TryGetValue("data", out var json))
        {
            try { data = codec.Deserialize<TData>(json); }
            catch (WorkflowDataException) { data = null; }
        }
        var steps = result is not null && result.TryGetValue("steps", out var count) && count is JsonNumberValue number ? (int)number.Value : 0;
        string? Text(string key) => result is not null && result.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;
        return outcome switch
        {
            CompletedTaskOutcome => new(WorkflowRunStatus.Completed, data, null, null, Text("stoppedAt"), steps, taskId),
            FailedTaskOutcome failed => new(WorkflowRunStatus.Failed, data, failed.Error.Message,
                failed.Error.Detail is JsonObjectValue detail && detail.TryGetValue("kind", out var kind) && kind is JsonStringValue name ? name.Value : "failed",
                Text("failedAt"), steps, taskId),
            AbortedTaskOutcome aborted => new(WorkflowRunStatus.Aborted, data, aborted.Reason ?? "aborted", "aborted", Text("abortedAt"), steps, taskId),
            FaultedTaskOutcome faulted => new(WorkflowRunStatus.Faulted, data, faulted.Error.Message, "faulted", null, steps, taskId),
            OrphanedTaskOutcome orphaned => new(WorkflowRunStatus.Faulted, data, orphaned.Reason, "orphaned", null, steps, taskId),
            _ => new(WorkflowRunStatus.Faulted, data, outcome.Status.ToString(), "unknown", null, steps, taskId),
        };
    }
}

/// <summary>A handle to one run of a workflow over <typeparamref name="TData"/>.</summary>
public sealed class WorkflowRun<TData> where TData : class
{
    private readonly WorkflowRunner runner;

    internal WorkflowRun(WorkflowRunner runner, Workflow<TData> workflow, TaskId taskId, ConversationId conversationId)
    {
        this.runner = runner;
        Workflow = workflow;
        TaskId = taskId;
        ConversationId = conversationId;
    }

    /// <summary>The workflow.</summary>
    public Workflow<TData> Workflow { get; }
    /// <summary>The run's durable task.</summary>
    public TaskId TaskId { get; }
    /// <summary>The run's conversation.</summary>
    public ConversationId ConversationId { get; }
    /// <summary>True when the start found an earlier run by its run key instead of starting one.</summary>
    public bool Existing { get; internal init; }

    /// <summary>Waits for the run to end and returns its result.</summary>
    public async Task<WorkflowResult<TData>> WaitAsync(DurableContext? context = null)
    {
        context ??= DurableContext.Background;
        var record = await runner.Harness.WaitForTaskAsync(TaskId, context).ConfigureAwait(false);
        return (WorkflowResult<TData>)runner.Catalog.Engine(Workflow).Result(record);
    }

    /// <summary>Sends the signal <paramref name="signal"/> to the run: a WaitForSignal step waiting for that name takes it (in the order
    /// sent), and one sent before the run gets there is kept until it does. A non-null <paramref name="id"/> makes the send idempotent:
    /// a second send with the same id is ignored and returns false.</summary>
    public Task<bool> SignalAsync<T>(string signal, T value, string? id = null, DurableContext? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        var json = runner.Catalog.Engine(Workflow).Codec.Serialize(value, "signal '" + signal + "'");
        return runner.Harness.CommitAsync(async tx =>
        {
            var doc = await RunDocs.RunAsync(tx, ConversationId, TaskId).ConfigureAwait(false);
            if (id is not null)
            {
                if (doc.Get("signalIds") is JsonArrayValue seen && seen.OfType<JsonStringValue>().Any(item => item.Value == id)) return false;
                RunDocs.Append(doc, "signalIds", id, RunDocs.SignalLimit * 4);
            }
            if (doc.Get("signals") is JsonArrayValue { Count: >= RunDocs.SignalLimit })
                throw new InvalidOperationException($"Workflow run {TaskId.Value} already holds {RunDocs.SignalLimit} signals nobody has taken.");
            RunDocs.Append(doc, "signals", RunDocs.Object(("name", signal), ("id", RunDocs.Opt(id)), ("value", json),
                ("at", (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), RunDocs.SignalLimit);
            return true;
        }, context ?? DurableContext.Background);
    }

    /// <summary>The run's status, current action, recent history and log.</summary>
    public async Task<WorkflowRunState> StatusAsync(DurableContext? context = null)
    {
        context ??= DurableContext.Background;
        var record = await runner.Harness.GetTaskAsync(TaskId, context).ConfigureAwait(false);
        var doc = await runner.Harness.SnapshotAsync(RunDocs.Run, ConversationId, RunDocs.Key(TaskId), context).ConfigureAwait(false);
        return State(record, doc);
    }

    /// <summary>Asks the run to abort (its OnAbort handler runs) and returns at once; <see cref="WaitAsync"/> observes the end.</summary>
    public Task<TaskAbortResult> AbortAsync(DurableContext? context = null) =>
        runner.Harness.AbortTaskAsync(TaskId, context ?? DurableContext.Background);

    internal static WorkflowRunState State(TaskRecord? record, JsonObjectValue? doc)
    {
        static string? Text(JsonObjectValue? value, string key) =>
            value is not null && value.TryGetValue(key, out var item) && item is JsonStringValue text ? text.Value : null;
        static double Number(JsonObjectValue value, string key) => value.TryGetValue(key, out var item) && item is JsonNumberValue number ? number.Value : 0;
        var status = record?.State switch
        {
            SettlingTaskState { Outcome: CompletedTaskOutcome } => WorkflowRunStatus.Completed,
            SettlingTaskState { Outcome: FailedTaskOutcome } => WorkflowRunStatus.Failed,
            SettlingTaskState { Outcome: AbortedTaskOutcome } => WorkflowRunStatus.Aborted,
            SettlingTaskState => WorkflowRunStatus.Faulted,
            PendingTaskState when Text(doc, "status") is null or "pending" => WorkflowRunStatus.Pending,
            null => WorkflowRunStatus.Pending,
            _ => WorkflowRunStatus.Running,
        };
        var current = doc is not null && doc.TryGetValue("current", out var item) ? item as JsonObjectValue : null;
        var history = doc is not null && doc.TryGetValue("history", out var lines) && lines is JsonArrayValue list
            ? list.OfType<JsonObjectValue>().Select(line => new WorkflowHistoryEntry((long)Number(line, "seq"), Text(line, "action") ?? "",
                (int)Number(line, "attempt"), Text(line, "outcome") ?? "", Text(line, "next"), Text(line, "error"), Number(line, "at"))).ToImmutableArray()
            : [];
        var log = doc is not null && doc.TryGetValue("log", out var entries) && entries is JsonArrayValue logged
            ? logged.OfType<JsonObjectValue>().Select(line => Text(line, "text") ?? "").ToImmutableArray()
            : [];
        return new WorkflowRunState(status, Text(current, "action"), current is not null ? (long)Number(current, "step") : null, history, log,
            Text(doc, "error"), record);
    }
}
