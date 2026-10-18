// PiSharp-only (decision 0008): durable jobs, after durable-pi's (apps/dpi-backend/src/jobs.ts and apps/dpi-sdk/src/jobs.ts, MIT). A job is
// a repeatable workflow of named phases; each phase is a task checkpoint, so a restart resumes the current phase. A repeat phase runs again
// after a durable sleep; an approval phase waits for a person in a child task that watches a task document (the pattern of decision 0005's
// UserInteraction) instead of polling it as durable-pi does. Runs are recorded in a conversation document the UI renders as job cards.
using System.Diagnostics;
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>What a job phase is.</summary>
public enum JobPhaseKind
{
    /// <summary>Runs once.</summary>
    Step,
    /// <summary>Runs again, after a pause, until it says it is done; one checkpoint per run.</summary>
    Repeat,
    /// <summary>Waits for a person to approve or decline; a restart keeps waiting.</summary>
    Approval,
}

/// <summary>Runs one phase of a job.</summary>
public delegate Task<JobPhaseResult> JobPhaseHandler(JobContext context);

/// <summary>One phase of a job.</summary>
public sealed record JobPhase(string Id, string Label, string Summary, JobPhaseKind Kind = JobPhaseKind.Step)
{
    /// <summary>The phase's work; null for an approval phase.</summary>
    public JobPhaseHandler? Run { get; init; }
    /// <summary>Whether the phase asks a subagent (shown on the phase).</summary>
    public bool UsesAgent { get; init; }
}

/// <summary>A numeric job input with its default and range.</summary>
public sealed record JobInput(string Name, double Default, double Minimum, double Maximum, string Description = "");

/// <summary>A repeatable durable workflow.</summary>
public sealed record JobDefinition
{
    /// <summary>The job's ID; its task kind is <c>pisharp.job.&lt;id&gt;</c>.</summary>
    public required string Id { get; init; }
    /// <summary>The display title.</summary>
    public required string Title { get; init; }
    /// <summary>What the job does.</summary>
    public required string Summary { get; init; }
    /// <summary>The phases, in order.</summary>
    public required IReadOnlyList<JobPhase> Phases { get; init; }
    /// <summary>Numeric inputs.</summary>
    public IReadOnlyList<JobInput> Inputs { get; init; } = [];
    /// <summary>Where in App State the job saves its result.</summary>
    public string? ResultPath { get; init; }
    /// <summary>A rough duration for the UI.</summary>
    public int EstimatedSeconds { get; init; } = 10;
    /// <summary>Whether a phase waits for a person.</summary>
    public bool NeedsApproval => Phases.Any(phase => phase.Kind == JobPhaseKind.Approval);

    /// <summary>The definition as JSON for clients and <c>list_durable_jobs</c>.</summary>
    public JsonObjectValue ToJson() => JsonValue.Object(
    [
        new("id", Id),
        new("title", Title),
        new("summary", Summary),
        new("needsApproval", NeedsApproval),
        new("estimatedSeconds", EstimatedSeconds),
        new("resultPath", ResultPath is null ? JsonValue.Null : ResultPath),
        new("inputs", JsonValue.Array(Inputs.Select(input => (JsonValue)JsonValue.Object([new("name", input.Name), new("default", input.Default),
            new("minimum", input.Minimum), new("maximum", input.Maximum), new("description", input.Description)])))),
        new("phases", JsonValue.Array(Phases.Select(phase => (JsonValue)JsonValue.Object([new("id", phase.Id), new("label", phase.Label),
            new("summary", phase.Summary), new("kind", phase.Kind.ToString().ToLowerInvariant()), new("usesAgent", phase.UsesAgent)])))),
    ]);
}

/// <summary>What a phase reports: done (the next phase starts) or again (a repeat phase runs once more after a pause), with the data the
/// next run receives.</summary>
public sealed record JobPhaseResult(bool Done, JsonObjectValue Data, string Summary, double AgainAfterMs = 0)
{
    /// <summary>The phase is done.</summary>
    public static JobPhaseResult Next(string summary, JsonObjectValue? data = null) => new(true, data ?? JsonObjectValue.Empty, summary);
    /// <summary>A repeat phase runs again after <paramref name="afterMs"/>.</summary>
    public static JobPhaseResult Again(string summary, double afterMs, JsonObjectValue? data = null) => new(false, data ?? JsonObjectValue.Empty, summary, afterMs);
}

/// <summary>What a running phase can use. Every step it takes is recorded on the run's card.</summary>
public sealed class JobContext
{
    private readonly ITaskRuntime runtime;
    private readonly RunningTask task;
    private readonly DurableContext durable;
    private readonly DeviceAppState state;
    private readonly string sessionId;
    private int steps;

    internal JobContext(JobDefinition job, JobPhase phase, int iteration, JsonObjectValue input, JsonObjectValue data, string cwd, ITaskRuntime runtime,
        RunningTask task, DurableContext durable, DeviceAppState state, string sessionId)
    {
        Job = job;
        Phase = phase;
        Iteration = iteration;
        Input = input;
        Data = data;
        Cwd = cwd;
        this.runtime = runtime;
        this.task = task;
        this.durable = durable;
        this.state = state;
        this.sessionId = sessionId;
    }

    /// <summary>The job.</summary>
    public JobDefinition Job { get; }
    /// <summary>The running phase.</summary>
    public JobPhase Phase { get; }
    /// <summary>How many times a repeat phase ran before (0 the first time).</summary>
    public int Iteration { get; }
    /// <summary>The run's inputs.</summary>
    public JsonObjectValue Input { get; }
    /// <summary>The data the previous phase or run returned.</summary>
    public JsonObjectValue Data { get; }
    /// <summary>The agent's working directory.</summary>
    public string Cwd { get; }
    /// <summary>Cancelled when the job is stopped or the device closes.</summary>
    public CancellationToken Cancellation => durable.CancellationToken;

    /// <summary>A numeric input, clamped to its range.</summary>
    public double Number(string name) => Input.TryGetValue(name, out var value) && value is JsonNumberValue number
        ? number.Value
        : Job.Inputs.FirstOrDefault(input => input.Name == name)?.Default ?? 0;

    /// <summary>Runs a program (no shell) in <see cref="Cwd"/> and returns its standard output; a nonzero exit throws. Recorded as a command
    /// step.</summary>
    public async Task<string> RunAsync(params string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var started = Stopwatch.StartNew();
        var label = string.Join(' ', argv);
        try
        {
            var output = await JobProcess.RunAsync(argv, Cwd, Cancellation).ConfigureAwait(false);
            await StepAsync("command", label, "done", started.ElapsedMilliseconds, output.Length > 2000 ? output[..2000] + "…" : output).ConfigureAwait(false);
            return output;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await StepAsync("command", label, "error", started.ElapsedMilliseconds, error.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Records a note on the phase.</summary>
    public Task NoteAsync(string text) => StepAsync("note", text, "done", 0, null);

    /// <summary>Saves a value in the device's App State, creating missing parents. A rerun of the same phase run saves once.</summary>
    public async Task SaveStateAsync(string path, JsonValue value)
    {
        state.Set(path, value, $"job:{sessionId}:{task.Id.Value}:{Phase.Id}:{Iteration}:{path}");
        await StepAsync("state", path, "done", 0, null).ConfigureAwait(false);
    }

    /// <summary>Reads a value from the device's App State.</summary>
    public JsonValue? ReadState(string path) => AppStateDocument.Read(state.Get().Value, path);

    /// <summary>Asks a subagent owned by the job (it can only read) and returns its answer; a rerun finds the same child and
    /// question.</summary>
    public async Task<string> AskAgentAsync(string question)
    {
        var started = Stopwatch.StartNew();
        ConversationId child = default;
        await runtime.CommitAsync(async (tx, running) =>
        {
            var existing = (await tx.ScanConversationsAsync(new ConversationQuery { OwnerTaskId = running.Id }, 1).ConfigureAwait(false)).Items.FirstOrDefault();
            if (existing is not null) { child = existing.Id; return; }
            var created = await tx.CreateConversationAsync(ConversationOwnership.Task(running.Id)).ConfigureAwait(false);
            var read = runtime.Registry.Tools().Select(registered => registered.Tool).Where(tool => tool.Name is "read" or "search_history").ToList();
            await Agents.ConfigureAsync(tx, created.Id, new AgentChange { Tools = ToolSelection.Only(read) }).ConfigureAwait(false);
            await Jobs.UpdateRunAsync(tx, running.ConversationId, running.Id, run => run.SetItem("childConversationId", created.Id.Value)).ConfigureAwait(false);
            child = created.Id;
        }, durable).ConfigureAwait(false);
        var handle = await runtime.ConversationAsync(child, durable).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Subagent {child.Value} is gone.");
        var submission = await handle.SubmitAsync(SubmissionDraft.Input(question, requestId: $"job:{task.Id.Value}:{Phase.Id}:{Iteration}"), durable).ConfigureAwait(false);
        var settled = await submission.WaitAsync(durable).ConfigureAwait(false);
        var answer = settled.Answer is { } entry ? SubmissionAnswers.TextOf(await runtime.EntryAsync(Entries.AssistantEntry, entry, durable).ConfigureAwait(false)) : "";
        await StepAsync("agent", $"Subagent #{child.Value}", settled.Status == SubmissionStatus.Done ? "done" : "error", started.ElapsedMilliseconds,
            answer.Length > 2000 ? answer[..2000] + "…" : answer, child.Value).ConfigureAwait(false);
        if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"Subagent #{child.Value} did not answer: {settled.Reason}");
        return answer;
    }

    private Task StepAsync(string kind, string label, string status, double ms, string? detail, long? conversation = null)
    {
        var step = JsonValue.Object(
        [
            new("id", $"{Phase.Id}:{Iteration}:{steps++}"),
            new("kind", kind),
            new("label", label),
            new("status", status),
            new("ms", ms),
            new("detail", detail is null ? JsonValue.Null : detail),
            new("conversationId", conversation is { } id ? id : JsonValue.Null),
        ]);
        return runtime.CommitAsync((tx, running) => Jobs.UpdateRunAsync(tx, running.ConversationId, running.Id,
            run => Jobs.UpdatePhase(run, Phase.Id, phase => phase.SetItem("steps", JsonValue.Array(Jobs.Items(phase, "steps").Append(step))))), durable);
    }
}

/// <summary>Job tasks, run records and the job tools.</summary>
public static class Jobs
{
    /// <summary>At most this many runs are kept per agent; the oldest go first.</summary>
    public const int MaxRuns = 30;

    /// <summary>The agent's job runs, newest first: <c>{items: [run], counters: {job: n}}</c>.</summary>
    public static ConversationDocToken RunsDoc { get; } = Documents.DefineConversationDoc("pisharp.job-runs", 1, DocumentFork.Initial,
        () => JsonValue.Object([new("items", JsonArrayValue.Empty), new("counters", JsonObjectValue.Empty)]));

    /// <summary>The decision an approval waits for: <c>{decision?: {approved, at}}</c>, on the approval task.</summary>
    internal static TaskDocToken ApprovalDoc { get; } = Documents.DefineTaskDoc("pisharp.job-approval", 1, () => JsonObjectValue.Empty);

    /// <summary>Which job a <c>run_durable_job</c> call started, so a rerun finds it.</summary>
    private static readonly TaskDocToken StartReceipt = Documents.DefineTaskDoc("pisharp.job-start", 1, () => JsonObjectValue.Empty);

    /// <summary>The approval task every approval phase creates.</summary>
    internal static DurableTask ApprovalTask { get; } = Tasks.DefineTask(new TaskDefinition
    {
        Name = "pisharp.job-approval",
        Version = 1,
        Initial = _ => JsonValue.Object([new("phase", "wait")]),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["wait"] = async (task, runtime, context) =>
            {
                var decision = await WaitForDecisionAsync(runtime, task.Id, context).ConfigureAwait(false);
                await runtime.CommitAsync((_, _) => NextTaskState.Completed(decision), context).ConfigureAwait(false);
            },
        },
        Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), context),
    });

    /// <summary>The durable task of a job.</summary>
    internal static DurableTask CreateTask(JobDefinition job, DeviceAppState state, string sessionId)
    {
        var phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal);
        for (var index = 0; index < job.Phases.Count; index++)
        {
            var phase = job.Phases[index];
            var next = index + 1 < job.Phases.Count ? job.Phases[index + 1] : null;
            if (phase.Kind == JobPhaseKind.Approval)
            {
                phases[phase.Id] = (task, runtime, context) => StartApprovalAsync(job, phase, task, runtime, context);
                phases[phase.Id + ".decided"] = (task, runtime, context) => DecidedAsync(job, phase, next, task, runtime, context);
            }
            else
            {
                phases[phase.Id] = (task, runtime, context) => RunPhaseAsync(job, phase, next, task, runtime, context, state, sessionId);
            }
        }
        return Tasks.DefineTask(new TaskDefinition
        {
            Name = "pisharp.job." + job.Id,
            Version = 1,
            Initial = _ => Checkpoint(job.Phases[0].Id, 0, JsonObjectValue.Empty),
            Phases = phases,
            Abort = (task, runtime, context) => runtime.CommitAsync(async (tx, running) =>
            {
                await UpdateRunAsync(tx, running.ConversationId, running.Id, run => Finish(run, "aborted", "aborted", runtime.Now())).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Aborted();
            }, context),
        });
    }

    /// <summary>Starts a run in a commit: the task and its run record together. The run number counts this job's runs of the
    /// agent.</summary>
    internal static async Task<TaskId> StartAsync(ITx tx, ConversationId conversation, JobDefinition job, DurableTask definition, JsonObjectValue? input,
        TaskOwnership ownership, bool background, string startedBy, string? toolCallId, double now)
    {
        var values = Normalize(job, input);
        var id = await tx.CreateTaskAsync(definition, JsonValue.Object([new("input", values)]),
            new TaskOptions(ownership) { ConversationId = conversation, Background = background }).ConfigureAwait(false);
        var doc = await tx.DocAsync(RunsDoc, conversation).ConfigureAwait(false);
        var counters = doc.Get("counters") as JsonObjectValue ?? JsonObjectValue.Empty;
        var number = (counters.TryGetValue(job.Id, out var count) && count is JsonNumberValue counted ? counted.Value : 0) + 1;
        var run = JsonValue.Object(
        [
            new("id", id.Value),
            new("job", job.Id),
            new("title", job.Title),
            new("run", number),
            new("input", values),
            new("startedBy", startedBy),
            new("toolCallId", toolCallId is null ? JsonValue.Null : toolCallId),
            new("background", background),
            new("status", "running"),
            new("phase", job.Phases[0].Id),
            new("phases", JsonValue.Array(job.Phases.Select(phase => (JsonValue)JsonValue.Object([new("id", phase.Id), new("label", phase.Label),
                new("kind", phase.Kind.ToString().ToLowerInvariant()), new("status", "pending"), new("summary", JsonValue.Null),
                new("startedAt", JsonValue.Null), new("endedAt", JsonValue.Null), new("steps", JsonArrayValue.Empty)])))),
            new("approval", JsonValue.Null),
            new("childConversationId", JsonValue.Null),
            new("resumed", 0),
            new("result", JsonValue.Null),
            new("error", JsonValue.Null),
            new("startedAt", now),
            new("endedAt", JsonValue.Null),
        ]);
        doc.Set("items", JsonValue.Array(new JsonValue[] { run }.Concat(Items(doc.ToValue() as JsonObjectValue, "items")).Take(MaxRuns)));
        doc.Set("counters", counters.SetItem(job.Id, number));
        return id;
    }

    /// <summary>Commits a person's decision for a run's pending approval. Repeating the same decision does nothing.</summary>
    /// <exception cref="AppStateConflictException">The run has no pending approval, or it was decided the other way.</exception>
    internal static async Task DecideAsync(ITx tx, ConversationId conversation, TaskId jobTask, bool approved, double now)
    {
        var runs = (await tx.DocAsync(RunsDoc, conversation).ConfigureAwait(false)).ToValue() as JsonObjectValue;
        var run = Items(runs, "items").OfType<JsonObjectValue>().FirstOrDefault(item => Long(item, "id") == jobTask.Value)
            ?? throw new HostNotFoundException($"No job run {jobTask.Value}.");
        if (run.TryGetValue("approval", out var value) is false || value is not JsonObjectValue approval || Long(approval, "taskId") is not { } approvalTask)
            throw new AppStateConflictException($"Job run {jobTask.Value} is not waiting for approval.");
        // A decided approval's task has ended, and an ended task's documents are read-only, so the run's record answers a repeat.
        if (String(approval, "status") is { } recorded && recorded is "approved" or "declined")
        {
            if (recorded == (approved ? "approved" : "declined")) return;
            throw new AppStateConflictException($"Job run {jobTask.Value} was already {recorded}.");
        }
        var task = await tx.GetTaskAsync(new TaskId(approvalTask)).ConfigureAwait(false);
        if (task is null || task.State.Status is DurableTaskStatus.Terminal or DurableTaskStatus.Completing || task.AbortRequested)
            throw new AppStateConflictException($"Job run {jobTask.Value} is not waiting for approval.");
        var doc = await tx.DocAsync(ApprovalDoc, new TaskId(approvalTask)).ConfigureAwait(false);
        if (doc.Get("decision") is JsonObjectValue decided)
        {
            if (decided.TryGetValue("approved", out var earlier) && earlier is JsonBooleanValue { Value: var was } && was == approved) return;
            throw new AppStateConflictException($"Job run {jobTask.Value} was already {(approved ? "declined" : "approved")}.");
        }
        doc.Set("decision", JsonValue.Object([new("approved", approved), new("at", now)]));
    }

    internal static async Task UpdateRunAsync(ITx tx, ConversationId conversation, TaskId id, Func<JsonObjectValue, JsonObjectValue> change)
    {
        var doc = await tx.DocAsync(RunsDoc, conversation).ConfigureAwait(false);
        var items = Items(doc.ToValue() as JsonObjectValue, "items");
        doc.Set("items", JsonValue.Array(items.Select(item => item is JsonObjectValue run && Long(run, "id") == id.Value ? change(run) : item)));
    }

    internal static JsonObjectValue UpdatePhase(JsonObjectValue run, string phaseId, Func<JsonObjectValue, JsonObjectValue> change) =>
        run.SetItem("phases", JsonValue.Array(Items(run, "phases").Select(item =>
            item is JsonObjectValue phase && String(phase, "id") == phaseId ? change(phase) : item)));

    /// <summary>The array under <paramref name="key"/> of a run record or document; empty when absent.</summary>
    public static IEnumerable<JsonValue> Items(JsonObjectValue? json, string key) =>
        json is not null && json.TryGetValue(key, out var value) && value is JsonArrayValue array ? array.Items : [];

    /// <summary>A whole number field of a run record; null when absent.</summary>
    public static long? Long(JsonObjectValue json, string key) => json.TryGetValue(key, out var value) && value is JsonNumberValue number ? (long)number.Value : null;

    /// <summary>A string field of a run record; null when absent.</summary>
    public static string? String(JsonObjectValue json, string key) => json.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    /// <summary>Clamps the inputs to their ranges and fills in defaults.</summary>
    internal static JsonObjectValue Normalize(JobDefinition job, JsonObjectValue? input) => JsonValue.Object(job.Inputs.Select(definition =>
    {
        var given = input is not null && input.TryGetValue(definition.Name, out var value) && value is JsonNumberValue number ? number.Value : definition.Default;
        return new KeyValuePair<string, JsonValue>(definition.Name, Math.Clamp(Math.Round(given), definition.Minimum, definition.Maximum));
    }));

    private static JsonObjectValue Checkpoint(string phase, int iteration, JsonObjectValue data, double? wakeAt = null, long? approvalTask = null)
    {
        var checkpoint = JsonValue.Object([new("phase", phase), new("iteration", iteration), new("data", data)]);
        if (wakeAt is { } at) checkpoint = checkpoint.SetItem("wakeAt", at);
        if (approvalTask is { } approval) checkpoint = checkpoint.SetItem("approvalTask", approval);
        return checkpoint;
    }

    private static async Task RunPhaseAsync(JobDefinition job, JobPhase phase, JobPhase? next, RunningTask task, ITaskRuntime runtime, DurableContext context,
        DeviceAppState state, string sessionId)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var iteration = (int)(Long(checkpoint, "iteration") ?? 0);
        var data = checkpoint.TryGetValue("data", out var carried) && carried is JsonObjectValue json ? json : JsonObjectValue.Empty;
        if (checkpoint.TryGetValue("wakeAt", out var wake) && wake is JsonNumberValue at) await runtime.SleepAsync(at.Value, context).ConfigureAwait(false);
        await runtime.CommitAsync((tx, running) => UpdateRunAsync(tx, running.ConversationId, running.Id,
            run => Begin(run, phase, iteration, runtime.Now())), context).ConfigureAwait(false);
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var input = ((JsonObjectValue)task.Input).TryGetValue("input", out var given) && given is JsonObjectValue values ? values : JsonObjectValue.Empty;
        var jobContext = new JobContext(job, phase, iteration, input, data, agent.Cwd ?? Environment.CurrentDirectory, runtime, task, context, state, sessionId);
        JobPhaseResult result;
        try
        {
            result = await phase.Run!(jobContext).ConfigureAwait(false);
        }
        catch (Exception error) when (!context.CancellationToken.IsCancellationRequested)
        {
            await runtime.CommitAsync(async (tx, running) =>
            {
                await UpdateRunAsync(tx, running.ConversationId, running.Id, run => Finish(
                    UpdatePhase(run, phase.Id, item => item.SetItem("status", "failed").SetItem("summary", error.Message).SetItem("endedAt", runtime.Now()))
                        .SetItem("error", error.Message), "failed", "skipped", runtime.Now())).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Failed(error.Message);
            }, context).ConfigureAwait(false);
            return;
        }
        await runtime.CommitAsync(async (tx, running) =>
        {
            var now = runtime.Now();
            if (!result.Done)
            {
                await UpdateRunAsync(tx, running.ConversationId, running.Id, run => UpdatePhase(run, phase.Id,
                    item => item.SetItem("summary", result.Summary).SetItem("progress", iteration + 1))).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Running(Checkpoint(phase.Id, iteration + 1, result.Data, now + result.AgainAfterMs));
            }
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
            {
                var updated = UpdatePhase(run, phase.Id, item => item.SetItem("status", "done").SetItem("summary", result.Summary).SetItem("endedAt", now));
                return next is null ? Finish(updated.SetItem("result", result.Data), "completed", "skipped", now) : updated.SetItem("phase", next.Id);
            }).ConfigureAwait(false);
            return next is null
                ? (TaskState?)NextTaskState.Completed(result.Data)
                : NextTaskState.Running(Checkpoint(next.Id, 0, result.Data));
        }, context).ConfigureAwait(false);
    }

    private static Task StartApprovalAsync(JobDefinition job, JobPhase phase, RunningTask task, ITaskRuntime runtime, DurableContext context) =>
        runtime.CommitAsync(async (tx, running) =>
        {
            var data = ((JsonObjectValue)running.Checkpoint).TryGetValue("data", out var carried) && carried is JsonObjectValue json ? json : JsonObjectValue.Empty;
            var approval = await tx.CreateTaskAsync(ApprovalTask, JsonValue.Object([new("job", job.Id), new("phase", phase.Id)]),
                new TaskOptions(TaskOwnership.Task(running.Id)) { ConversationId = running.ConversationId }).ConfigureAwait(false);
            await tx.DocAsync(ApprovalDoc, approval).ConfigureAwait(false);
            var now = runtime.Now();
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run => UpdatePhase(run, phase.Id,
                    item => item.SetItem("status", "waiting").SetItem("startedAt", now).SetItem("summary", phase.Summary))
                .SetItem("status", "waiting").SetItem("phase", phase.Id)
                .SetItem("approval", JsonValue.Object([new("phase", phase.Id), new("taskId", approval.Value), new("status", "pending")]))).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Waiting(Checkpoint(phase.Id + ".decided", 0, data, approvalTask: approval.Value), [approval], JoinPolicy.AllSettled);
        }, context);

    private static async Task DecidedAsync(JobDefinition job, JobPhase phase, JobPhase? next, RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var data = checkpoint.TryGetValue("data", out var carried) && carried is JsonObjectValue json ? json : JsonObjectValue.Empty;
        var approvalTask = new TaskId(Long(checkpoint, "approvalTask") ?? 0);
        var outcome = (await runtime.OutcomesAsync([approvalTask], context).ConfigureAwait(false)).Single();
        var approved = outcome is CompletedTaskOutcome { Result: JsonObjectValue result } && result.TryGetValue("approved", out var flag) &&
            flag is JsonBooleanValue { Value: true };
        await runtime.CommitAsync(async (tx, running) =>
        {
            var now = runtime.Now();
            if (!approved)
            {
                var status = outcome.Status == TaskOutcomeStatus.Completed ? "declined" : "cancelled";
                await UpdateRunAsync(tx, running.ConversationId, running.Id, run => Finish(
                    UpdatePhase(run, phase.Id, item => item.SetItem("status", status == "declined" ? "declined" : "aborted")
                        .SetItem("summary", status == "declined" ? "Declined by you" : "Cancelled").SetItem("endedAt", now))
                    .SetItem("approval", JsonValue.Object([new("phase", phase.Id), new("taskId", approvalTask.Value), new("status", status)])),
                    status == "declined" ? "declined" : "aborted", "skipped", now)).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Completed(JsonValue.Object([new("declined", true)]));
            }
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
            {
                var updated = UpdatePhase(run, phase.Id, item => item.SetItem("status", "done").SetItem("summary", "Approved by you").SetItem("endedAt", now))
                    .SetItem("approval", JsonValue.Object([new("phase", phase.Id), new("taskId", approvalTask.Value), new("status", "approved")]))
                    .SetItem("status", "running");
                return next is null ? Finish(updated.SetItem("result", data), "completed", "skipped", now) : updated.SetItem("phase", next.Id);
            }).ConfigureAwait(false);
            return next is null ? (TaskState?)NextTaskState.Completed(data) : NextTaskState.Running(Checkpoint(next.Id, 0, data));
        }, context).ConfigureAwait(false);
    }

    /// <summary>Marks a phase run started. Steps of the same phase run recorded before a restart are dropped and the run says it resumed
    /// (durable-pi's resume note).</summary>
    private static JsonObjectValue Begin(JsonObjectValue run, JobPhase phase, int iteration, double now)
    {
        var resumed = false;
        var updated = UpdatePhase(run, phase.Id, item =>
        {
            var prefix = $"{phase.Id}:{iteration}:";
            var steps = Items(item, "steps").ToList();
            var kept = steps.Where(step => step is not JsonObjectValue recorded || String(recorded, "id")?.StartsWith(prefix, StringComparison.Ordinal) != true).ToList();
            if (kept.Count != steps.Count)
            {
                resumed = true;
                kept.Add(JsonValue.Object([new("id", prefix + "resumed"), new("kind", "checkpoint"), new("label", "Resumed from the last checkpoint after a restart"),
                    new("status", "done"), new("ms", 0), new("detail", JsonValue.Null), new("conversationId", JsonValue.Null)]));
            }
            var started = item.TryGetValue("startedAt", out var at) && at is JsonNumberValue ? at : now;
            return item.SetItem("status", "running").SetItem("startedAt", started).SetItem("steps", JsonValue.Array(kept));
        }).SetItem("status", "running").SetItem("phase", phase.Id);
        return resumed ? updated.SetItem("resumed", (Long(run, "resumed") ?? 0) + 1) : updated;
    }

    /// <summary>Ends a run: its status, its unfinished phases marked <paramref name="unfinished"/>, and a pending approval cancelled.</summary>
    private static JsonObjectValue Finish(JsonObjectValue run, string status, string unfinished, double now)
    {
        var phases = Items(run, "phases").Select(item => item is JsonObjectValue phase && String(phase, "status") is "pending" or "running" or "waiting"
            ? phase.SetItem("status", String(phase, "status") == "pending" ? unfinished : status == "aborted" ? "aborted" : unfinished).SetItem("endedAt", now)
            : item);
        var finished = run.SetItem("status", status).SetItem("phases", JsonValue.Array(phases)).SetItem("endedAt", now);
        return finished.TryGetValue("approval", out var approval) && approval is JsonObjectValue pending && String(pending, "status") == "pending"
            ? finished.SetItem("approval", pending.SetItem("status", "cancelled"))
            : finished;
    }

    /// <summary>Waits until the approval document holds a decision, watching it rather than polling.</summary>
    private static async Task<JsonObjectValue> WaitForDecisionAsync(ITaskRuntime runtime, TaskId approvalTask, DurableContext context)
    {
        var watch = await runtime.WatchDocAsync(ApprovalDoc, approvalTask, context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Approval task {approvalTask.Value} has no decision document.");
        try
        {
            if (Decision(watch.Value) is { } ready) return ready;
            var decided = new TaskCompletionSource<JsonObjectValue>(TaskCreationOptions.RunContinuationsAsynchronously);
            watch.Start((value, _, _) =>
            {
                if (Decision(value) is { } decision) decided.TrySetResult(decision);
                return Task.CompletedTask;
            });
            _ = watch.Closed.ContinueWith(closed => decided.TrySetException(new InvalidOperationException(
                $"The approval watch of task {approvalTask.Value} ended: {closed.Result.Reason}", closed.Result.Error)),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await context.WaitAsync(decided.Task).ConfigureAwait(false);
        }
        finally
        {
            await watch.StopAsync().ConfigureAwait(false);
        }
    }

    private static JsonObjectValue? Decision(JsonObjectValue? doc) =>
        doc is not null && doc.TryGetValue("decision", out var decision) && decision is JsonObjectValue value ? value : null;

    // ─── Tools ──────────────────────────────────────────────────────────────

    internal static ToolRegistration ListTool(IReadOnlyList<JobDefinition> jobs) => Define.Tool(new ToolRegistration
    {
        Name = "list_durable_jobs",
        Description = "List the durable jobs this device defines: repeatable workflows whose phases are checkpoints, so a restart resumes " +
            "the current phase. Some wait for a person to approve.",
        Parameters = ToolRegistration.EmptyParameters,
        Replay = ToolReplay.Safe,
        Execute = (_, _, _) => Task.FromResult(AppTools.Text(JsonValue.Array(jobs.Select(job => (JsonValue)job.ToJson())).ToJson())),
    });

    internal static ToolRegistration RunTool(IReadOnlyList<JobDefinition> jobs, IReadOnlyDictionary<string, DurableTask> tasks) => Define.Tool(new ToolRegistration
    {
        Name = "run_durable_job",
        Description = "Start a durable job. Without wait it runs in the background and this conversation stays free; check_durable_job " +
            "reports its progress. With wait the call waits for it, and interrupting the call stops the job.",
        Parameters = AppTools.Schema("""{"type":"object","required":["job"],"properties":{"job":{"type":"string","description":"The job ID from list_durable_jobs."},"input":{"type":"object","description":"Numeric inputs by name."},"wait":{"type":"boolean"}}}"""),
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var json = (JsonObjectValue)arguments;
            var id = AppTools.String(json, "job") ?? "";
            if (jobs.FirstOrDefault(job => job.Id == id) is not { } job) return AppTools.Error($"No job {id}. Jobs: {string.Join(", ", jobs.Select(item => item.Id))}.");
            var wait = json.TryGetValue("wait", out var waitValue) && waitValue is JsonBooleanValue { Value: true };
            var input = json.TryGetValue("input", out var given) ? given as JsonObjectValue : null;
            var taskId = await api.CommitAsync(async tx =>
            {
                var receipt = await tx.DocAsync(StartReceipt, api.TaskId).ConfigureAwait(false);
                if (receipt.Get("task") is JsonNumberValue started) return new TaskId((long)started.Value);
                var created = await StartAsync(tx, api.ConversationId, job, tasks[job.Id], input,
                    wait ? TaskOwnership.Task(api.TaskId) : TaskOwnership.Conversation, background: !wait, "agent", api.CallId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
                receipt.Set("task", created.Value);
                return created;
            }, context).ConfigureAwait(false);
            await api.DetailsAsync(JsonValue.Object([new("jobTaskId", taskId.Value), new("job", job.Id)]), context).ConfigureAwait(false);
            if (!wait)
                return AppTools.Text($"Started {job.Title} (task #{taskId.Value}) in the background. check_durable_job reports its progress.");
            var record = await api.WaitForTaskAsync(taskId, context).ConfigureAwait(false);
            var run = await RunAsync(api, api.ConversationId, taskId, context).ConfigureAwait(false);
            return AppTools.Text(Describe(run) ?? $"{job.Title} ended: {record.State.Status}");
        },
    });

    internal static ToolRegistration CheckTool() => Define.Tool(new ToolRegistration
    {
        Name = "check_durable_job",
        Description = "Report a durable job run's phases and result, by its task_id or the newest run of a job.",
        Parameters = AppTools.Schema("""{"type":"object","properties":{"task_id":{"type":"integer"},"job":{"type":"string"}}}"""),
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var doc = await api.SnapshotAsync(RunsDoc, api.ConversationId, context).ConfigureAwait(false);
            var runs = Items(doc, "items").OfType<JsonObjectValue>().ToList();
            var taskId = AppTools.Number(arguments, "task_id");
            var job = AppTools.String(arguments, "job");
            var run = runs.FirstOrDefault(item => (taskId is { } id && Long(item, "id") == (long)id) || (taskId is null && (job is null || String(item, "job") == job)));
            return run is null ? AppTools.Error("No such job run.") : AppTools.Text(Describe(run)!);
        },
    });

    private static async Task<JsonObjectValue?> RunAsync(IDocumentReader reader, ConversationId conversation, TaskId id, DurableContext context) =>
        Items(await reader.SnapshotAsync(RunsDoc, conversation, context).ConfigureAwait(false), "items").OfType<JsonObjectValue>()
            .FirstOrDefault(item => Long(item, "id") == id.Value);

    private static string? Describe(JsonObjectValue? run)
    {
        if (run is null) return null;
        var text = new StringBuilder($"{String(run, "title")} run #{Long(run, "run")} (task #{Long(run, "id")}): {String(run, "status")}");
        foreach (var phase in Items(run, "phases").OfType<JsonObjectValue>())
            text.Append(CultureInvariant($"\n- {String(phase, "label")}: {String(phase, "status")}{(String(phase, "summary") is { } summary ? " — " + summary : "")}"));
        if (run.TryGetValue("result", out var result) && result is not JsonNullValue) text.Append("\nResult: ").Append(result.ToJson());
        if (String(run, "error") is { } error) text.Append("\nError: ").Append(error);
        return text.ToString();
    }

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);
}

/// <summary>Runs a program for a job: no shell, a 60 s limit and at most 50000 characters of output.</summary>
internal static class JobProcess
{
    public static async Task<string> RunAsync(string[] argv, string cwd, CancellationToken cancellationToken)
    {
        if (argv.Length == 0) throw new ArgumentException("A command needs a program.", nameof(argv));
        var start = new ProcessStartInfo(argv[0]) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in argv.Skip(1)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{argv[0]} did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var text = await output.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{argv[0]} exited with {process.ExitCode}: {(await errors.ConfigureAwait(false)).Trim()}");
        return text.Length > 50_000 ? text[..50_000] : text;
    }
}
