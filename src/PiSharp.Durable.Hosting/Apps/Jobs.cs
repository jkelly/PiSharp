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
    /// <summary>Whether the phase runs, given the data the previous phase returned; null always runs. A phase that does not run is
    /// <c>bypassed</c>, which counts as done in the completion audit.</summary>
    public Func<JsonObjectValue, bool>? When { get; init; }
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
    /// <summary>Numeric inputs; ignored when <see cref="InputSchema"/> is set.</summary>
    public IReadOnlyList<JobInput> Inputs { get; init; } = [];
    /// <summary>A typed object input in the subset <see cref="Hosting.InputSchema"/> supports (decision 0010); null keeps the numeric
    /// <see cref="Inputs"/>.</summary>
    public JsonObjectValue? InputSchema { get; init; }
    /// <summary>The version of <see cref="InputSchema"/>, recorded with each run.</summary>
    public int InputSchemaVersion { get; init; } = 1;
    /// <summary>The largest canonical typed input, in UTF-8 bytes.</summary>
    public int MaxInputBytes { get; init; } = 64 * 1024;
    /// <summary>Whether the job is one of the host's application jobs (judgment, source checks, research) rather than a definition of
    /// <see cref="DurableDeviceOptions.Jobs"/>.</summary>
    public bool Application { get; init; }
    /// <summary>An application job's own input check, used instead of <see cref="InputSchema"/> where the subset cannot express the input
    /// (an item of a policy's own schema). It returns the normalized input or throws <see cref="HostValidationException"/>.</summary>
    internal Func<JsonObjectValue?, JsonObjectValue>? Admit { get; init; }
    /// <summary>What an application job freezes when a run is admitted (not when a schedule is saved): the policy version a judgment will
    /// use, for example. Kept as <c>frozen</c> in the task input and the run record.</summary>
    internal Func<JsonObjectValue, JsonObjectValue>? Freeze { get; init; }
    /// <summary>Named checks a handler must pass through <see cref="JobContext.CheckAsync"/> before completion.</summary>
    public IReadOnlyList<string> RequiredChecks { get; init; } = [];
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
        new("requiredChecks", JsonValue.Array(RequiredChecks.Select(check => (JsonValue)check))),
        new("inputs", JsonValue.Array(Inputs.Select(input => (JsonValue)JsonValue.Object([new("name", input.Name), new("default", input.Default),
            new("minimum", input.Minimum), new("maximum", input.Maximum), new("description", input.Description)])))),
        new("inputSchema", InputSchema is { } schema ? schema : JsonValue.Null),
        new("inputSchemaVersion", InputSchema is null ? JsonValue.Null : InputSchemaVersion),
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
public sealed partial class JobContext
{
    private readonly ITaskRuntime runtime;
    private readonly RunningTask task;
    private readonly DurableContext durable;
    private readonly DeviceAppState state;
    private readonly string sessionId;
    private int steps;
    private int agentCalls;
    private int judgeCalls;
    private int artifactCalls;

    internal JobContext(JobDefinition job, JobPhase phase, int iteration, JsonObjectValue input, JsonObjectValue data, string cwd, ITaskRuntime runtime,
        RunningTask task, DurableContext durable, DurableDeviceHost host, string sessionId)
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
        Host = host;
        state = host.AppState;
        this.sessionId = sessionId;
    }

    internal DurableDeviceHost Host { get; }
    internal ITaskRuntime Runtime => runtime;
    internal RunningTask Running => task;
    internal DurableContext Durable => durable;

    /// <summary>The session the job runs in.</summary>
    public string SessionId => sessionId;
    /// <summary>The job run's task ID.</summary>
    public long TaskId => task.Id.Value;
    /// <summary>The run's ledger key: <c>&lt;session&gt;/&lt;task&gt;</c>.</summary>
    public string RunKey => sessionId + "/" + task.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>The run's limits, frozen at admission; null when it has none.</summary>
    public IReadOnlyDictionary<string, long>? Budget => ((JsonObjectValue)task.Input).TryGetValue("budget", out var budget) && budget is JsonObjectValue limits
        ? BudgetLedger.Amounts(limits) : null;

    /// <summary>What the run froze at admission (an application job's choices); empty when nothing.</summary>
    public JsonObjectValue Frozen => ((JsonObjectValue)task.Input).TryGetValue("frozen", out var frozen) && frozen is JsonObjectValue value ? value : JsonObjectValue.Empty;

    internal int NextJudgeCall() => Interlocked.Increment(ref judgeCalls) - 1;
    internal int NextArtifactCall() => Interlocked.Increment(ref artifactCalls) - 1;

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

    /// <summary>Records an evidence check in the durable run record. A failed or missing required check prevents successful completion.</summary>
    public Task CheckAsync(string id, bool passed, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || detail?.Length > 4000) throw new HostInvalidException("Invalid audit check.");
        return runtime.CommitAsync((tx, running) => Jobs.UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
            run.SetItem("checks", ((run.TryGetValue("checks", out var checks) ? checks as JsonObjectValue : null) ?? JsonObjectValue.Empty)
                .SetItem(id, JsonValue.Object([new("passed", passed), new("detail", detail is { } text ? text : JsonValue.Null)])))), durable);
    }

    /// <summary>Saves a value in the device's App State, creating missing parents. A rerun of the same phase run saves once and returns
    /// the value originally saved, so the next checkpoint uses the committed result even when the candidate changed on restart.</summary>
    public async Task<JsonValue> SaveStateAsync(string path, JsonValue value)
    {
        var saved = state.Set(path, value, $"job:{sessionId}:{task.Id.Value}:{Phase.Id}:{Iteration}:{path}");
        await StepAsync("state", path, "done", 0, null).ConfigureAwait(false);
        return AppStateDocument.Read(saved.Value, path) ?? throw new InvalidOperationException("The state receipt has no saved value.");
    }

    /// <summary>Reads a value from the device's App State.</summary>
    public JsonValue? ReadState(string path) => AppStateDocument.Read(state.Get().Value, path);

    /// <summary>
    /// Evaluates <paramref name="input"/> under a judgment policy (decision 0010) and returns the judgment record. The policy version is
    /// <paramref name="version"/> or the active one, frozen at the first call; a rerun of the phase returns the recorded judgment without
    /// calling the provider again. The record's <c>route</c> says whether a person must confirm (<c>confirm</c>) or decide (<c>human</c>).
    /// </summary>
    /// <exception cref="HostLimitException">A budget refused the call.</exception>
    public async Task<JsonObjectValue> JudgeAsync(string policyId, JsonObjectValue? input = null, IReadOnlyList<string>? evidenceIds = null, int? version = null,
        string? preferenceRevision = null)
    {
        var started = Stopwatch.StartNew();
        var judgment = await Judgments.JudgeAsync(this, policyId, version, input, evidenceIds ?? [], preferenceRevision).ConfigureAwait(false);
        var status = Jobs.String(judgment, "status");
        await StepAsync("judgment", $"Judgment {Jobs.String(judgment, "id")} under {policyId}", status == "judged" ? "done" : "error", started.ElapsedMilliseconds,
            status == "judged" ? $"{Jobs.String((JsonObjectValue)judgment["result"], "label")} — route {Jobs.String(judgment, "route")}" : Jobs.String(judgment, "error"))
            .ConfigureAwait(false);
        if (status == "judged") await CheckAsync("judgment:" + policyId, true).ConfigureAwait(false);
        return judgment;
    }

    /// <summary>Keeps content as evidence of this session and returns its record; the same URL and content is one record.</summary>
    /// <exception cref="HostValidationException">The draft is invalid.</exception>
    public async Task<JsonObjectValue> SaveEvidenceAsync(EvidenceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Evidence.Check(draft);
        var hash = Evidence.HashOf(draft);
        var blob = Evidence.StoreBody(Host, sessionId, draft, hash);
        JsonObjectValue record = JsonObjectValue.Empty;
        await runtime.CommitAsync(async (tx, running) =>
        {
            record = await Evidence.WriteAsync(tx, sessionId, running.Id.Value, draft, hash, blob, runtime.Now()).ConfigureAwait(false);
        }, durable).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Validates and saves a structured result card (decision 0010) and links it to the run; a rerun of the phase returns the saved
    /// artifact. With a <paramref name="key"/> its identity is that key rather than the order of the call, for artifacts saved
    /// concurrently. A valid artifact records the check <c>artifact:&lt;kind&gt;</c>. An invalid one throws with field errors and is
    /// noted on the run.
    /// </summary>
    /// <exception cref="HostValidationException">The artifact is invalid.</exception>
    public async Task<JsonObjectValue> SaveArtifactAsync(string kind, string title, JsonObjectValue props, IReadOnlyList<string>? evidenceIds = null,
        JsonObjectValue? provenance = null, JsonObjectValue? actionInput = null, string? key = null)
    {
        var started = Stopwatch.StartNew();
        try
        {
            var artifact = await Artifacts.SaveAsync(this, kind, title, props, evidenceIds ?? [], provenance, actionInput, key).ConfigureAwait(false);
            await StepAsync("artifact", $"{kind}: {title}", "done", started.ElapsedMilliseconds, Jobs.String(artifact, "id")).ConfigureAwait(false);
            await CheckAsync("artifact:" + kind, true).ConfigureAwait(false);
            return artifact;
        }
        catch (HostValidationException invalid)
        {
            await StepAsync("artifact", $"{kind}: {(title.Length > 200 ? title[..200] : title)}", "error", started.ElapsedMilliseconds, invalid.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Asks a subagent owned by the job (it can only read) and returns its answer; a rerun finds the same child and
    /// question.</summary>
    public async Task<string> AskAgentAsync(string question)
    {
        var callIndex = Interlocked.Increment(ref agentCalls) - 1;
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
        // Keep the original first-call key for jobs already waiting at this checkpoint. Later calls in the same phase need their
        // own replay identity; otherwise the submission deduplicator answers every question with the first question's result.
        var requestId = $"job:{task.Id.Value}:{Phase.Id}:{Iteration}" + (callIndex == 0 ? "" : $":{callIndex}");
        var submission = await handle.SubmitAsync(SubmissionDraft.Input(question, requestId: requestId), durable).ConfigureAwait(false);
        var settled = await submission.WaitAsync(durable).ConfigureAwait(false);
        var answer = "";
        if (settled.Answer is { } entry)
            await runtime.CommitAsync(async (tx, _) =>
            {
                // Runtime.EntryAsync only reads entries visible in the parent's conversation. The answer belongs to our child.
                answer = SubmissionAnswers.TextOf(await tx.GetEntryAsync(Entries.AssistantEntry, entry).ConfigureAwait(false));
            }, durable).ConfigureAwait(false);
        await StepAsync("agent", $"Subagent #{child.Value}", settled.Status == SubmissionStatus.Done ? "done" : "error", started.ElapsedMilliseconds,
            answer.Length > 2000 ? answer[..2000] + "…" : answer, child.Value).ConfigureAwait(false);
        if (settled.Status != SubmissionStatus.Done) throw new InvalidOperationException($"Subagent #{child.Value} did not answer: {settled.Reason}");
        return answer;
    }

    internal Task StepAsync(string kind, string label, string status, double ms, string? detail, long? conversation = null)
    {
        var step = JsonValue.Object(
        [
            new("id", $"{Phase.Id}:{Iteration}:{Interlocked.Increment(ref steps) - 1}"),
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

    /// <summary>The full run record, independent of the bounded conversation card window.</summary>
    internal static ConversationDocFamilyToken RecordDoc { get; } = Documents.DefineConversationDocFamily("pisharp.job-record", 1,
        DocumentFork.Initial, _ => JsonObjectValue.Empty);

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
                var deadline = task.Input is JsonObjectValue input && input.TryGetValue("deadline", out var at) && at is JsonNumberValue number ? number.Value : (double?)null;
                var decision = await WaitForDecisionAsync(runtime, task.Id, context, deadline).ConfigureAwait(false);
                await runtime.CommitAsync((_, _) => NextTaskState.Completed(decision), context).ConfigureAwait(false);
            },
        },
        Abort = (_, runtime, context) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), context),
    });

    /// <summary>The durable task of a job.</summary>
    internal static DurableTask CreateTask(JobDefinition job, DurableDeviceHost host, string sessionId)
    {
        var phases = new Dictionary<string, TaskPhaseHandler>(StringComparer.Ordinal);
        for (var index = 0; index < job.Phases.Count; index++)
        {
            var phase = job.Phases[index];
            var next = index + 1 < job.Phases.Count ? job.Phases[index + 1] : null;
            if (phase.Kind == JobPhaseKind.Approval)
            {
                phases[phase.Id] = (task, runtime, context) => StartApprovalAsync(job, phase, next, task, runtime, context);
                phases[phase.Id + ".decided"] = (task, runtime, context) => DecidedAsync(job, phase, next, task, runtime, context);
            }
            else
            {
                phases[phase.Id] = (task, runtime, context) => RunPhaseAsync(job, phase, next, task, runtime, context, host, sessionId);
            }
        }
        return Tasks.DefineTask(new TaskDefinition
        {
            Name = "pisharp.job." + job.Id,
            Version = 1,
            Initial = _ => Checkpoint(job.Phases[0].Id, 0, JsonObjectValue.Empty),
            Phases = phases,
            Abort = async (task, runtime, context) =>
            {
                IReadOnlyList<string> open = [];
                await runtime.CommitAsync(async (tx, running) =>
                {
                    await UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
                    {
                        open = JobBranches.OpenRequests(run);
                        return Finish(run, "aborted", "aborted", runtime.Now());
                    }).ConfigureAwait(false);
                    return (TaskState?)NextTaskState.Aborted();
                }, context).ConfigureAwait(false);
                // A branch attempt the abort interrupted may have been billed: its reservation keeps counting until someone resolves it.
                foreach (var operation in open)
                    if (host.Budgets.Get(operation) is { Status: "reserved" }) host.Budgets.MarkUncertain(operation, "The run was aborted during the attempt.");
            },
        });
    }

    /// <summary>Starts a run in a commit: the task and its run record together. The run number counts this job's runs of the
    /// agent.</summary>
    internal static async Task<TaskId> StartAsync(ITx tx, ConversationId conversation, JobDefinition job, DurableTask definition, JsonObjectValue? input,
        TaskOwnership ownership, bool background, string startedBy, string? toolCallId, double now, string? scheduleId = null, long? scheduleSlot = null,
        JsonObjectValue? budget = null)
    {
        var values = Normalize(job, input);
        var taskInput = JsonValue.Object([new("input", values), new("historyVersion", 1),
            new("scheduleId", scheduleId is { } scheduled ? scheduled : JsonValue.Null)]);
        // Decision 0010: the run's limits and an application job's choices are frozen at admission; only a run that has them carries
        // the fields.
        if (budget is not null) taskInput = taskInput.SetItem("budget", budget);
        var frozen = job.Freeze?.Invoke(values);
        if (frozen is not null) taskInput = taskInput.SetItem("frozen", frozen);
        var id = await tx.CreateTaskAsync(definition, taskInput,
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
            new("inputHash", CanonicalJson.Hash(values)),
            new("inputSchemaVersion", job.InputSchema is null ? JsonValue.Null : job.InputSchemaVersion),
            new("budget", budget is null ? JsonValue.Null : budget),
            new("frozen", frozen is null ? JsonValue.Null : frozen),
            new("artifacts", JsonArrayValue.Empty),
            new("judgments", JsonArrayValue.Empty),
            new("startedBy", startedBy),
            new("scheduleId", scheduleId is { } schedule ? schedule : JsonValue.Null),
            new("scheduleSlot", scheduleSlot is { } slot ? slot : JsonValue.Null),
            new("requiredChecks", JsonValue.Array(job.RequiredChecks.Select(check => (JsonValue)check))),
            new("checks", JsonObjectValue.Empty),
            new("audit", JsonValue.Null),
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
        (await tx.DocAsync(RecordDoc, conversation, id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonValue.Null).ConfigureAwait(false)).Set("run", run);
        await tx.AppendEntryAsync(conversation, new EntryDraft
        {
            Kind = "pisharp.job",
            Data = JsonValue.Object([new("taskId", id.Value), new("job", job.Id), new("text", $"Started {job.Title}")]),
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>Commits a person's decision for a run's pending approval. Repeating the same decision does nothing.</summary>
    /// <exception cref="AppStateConflictException">The run has no pending approval, or it was decided the other way.</exception>
    internal static async Task DecideAsync(ITx tx, ConversationId conversation, TaskId jobTask, bool approved, double now, string actor = "you",
        string? reason = null)
    {
        if (reason is { Length: > 1000 }) throw new HostInvalidException("A decision reason may have at most 1000 characters.");
        var runs = (await tx.DocAsync(RunsDoc, conversation).ConfigureAwait(false)).ToValue() as JsonObjectValue;
        var run = Items(runs, "items").OfType<JsonObjectValue>().FirstOrDefault(item => Long(item, "id") == jobTask.Value)
            ?? (await tx.DocAsync(RecordDoc, conversation, jobTask.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonValue.Null).ConfigureAwait(false)).Get("run") as JsonObjectValue
            ?? throw new HostNotFoundException($"No job run {jobTask.Value}.");
        if (run.TryGetValue("approval", out var value) is false || value is not JsonObjectValue approval || Long(approval, "taskId") is not { } approvalTask)
            throw new AppStateConflictException($"Job run {jobTask.Value} is not waiting for approval.");
        // A decided approval's task has ended, and an ended task's documents are read-only, so the run's record answers a repeat.
        if (String(approval, "status") is { } recorded && recorded is "approved" or "declined" or "expired")
        {
            if (recorded == (approved ? "approved" : "declined")) return;
            throw new AppStateConflictException($"Job run {jobTask.Value} was already {recorded}.");
        }
        var task = await tx.GetTaskAsync(new TaskId(approvalTask)).ConfigureAwait(false);
        // Between the approval task settling and the job recording the result in the run, the settled outcome holds the decision.
        if (task?.State is SettlingTaskState { Outcome: CompletedTaskOutcome { Result: JsonObjectValue settled } })
        {
            if (settled.TryGetValue("expired", out var lapsed) && lapsed is JsonBooleanValue { Value: true })
                throw new AppStateConflictException($"The review of job run {jobTask.Value} expired.");
            if (settled.TryGetValue("approved", out var earlier) && earlier is JsonBooleanValue { Value: var was } && was == approved) return;
            throw new AppStateConflictException($"Job run {jobTask.Value} was already {(approved ? "declined" : "approved")}.");
        }
        if (task is null || task.State.Status is DurableTaskStatus.Terminal or DurableTaskStatus.Completing || task.AbortRequested)
            throw new AppStateConflictException($"Job run {jobTask.Value} is not waiting for approval.");
        var doc = await tx.DocAsync(ApprovalDoc, new TaskId(approvalTask)).ConfigureAwait(false);
        if (doc.Get("decision") is JsonObjectValue decided)
        {
            if (decided.TryGetValue("expired", out var expired) && expired is JsonBooleanValue { Value: true })
                throw new AppStateConflictException($"The review of job run {jobTask.Value} expired.");
            if (decided.TryGetValue("approved", out var earlier) && earlier is JsonBooleanValue { Value: var was } && was == approved) return;
            throw new AppStateConflictException($"Job run {jobTask.Value} was already {(approved ? "declined" : "approved")}.");
        }
        doc.Set("decision", JsonValue.Object([new("approved", approved), new("at", now), new("actor", actor), new("reason", reason is null ? JsonValue.Null : reason)]));
        // The judgment this approval reviews records the person's choice in the same commit; the job's resumption repeats it as a no-op.
        if (String(approval, "judgmentId") is { } judgmentId)
            await Judgments.DecideAsync(tx, judgmentId, approved ? "approve" : "reject", actor, reason, now).ConfigureAwait(false);
    }

    internal static async Task UpdateRunAsync(ITx tx, ConversationId conversation, TaskId id, Func<JsonObjectValue, JsonObjectValue> change)
    {
        var doc = await tx.DocAsync(RunsDoc, conversation).ConfigureAwait(false);
        var items = Items(doc.ToValue() as JsonObjectValue, "items");
        var record = await tx.DocAsync(RecordDoc, conversation, id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), JsonValue.Null).ConfigureAwait(false);
        var current = record.Get("run") as JsonObjectValue ?? items.OfType<JsonObjectValue>().FirstOrDefault(item => Long(item, "id") == id.Value)
            ?? throw new InvalidOperationException($"Job run {id.Value} has no record.");
        var updated = change(current);
        record.Set("run", updated);
        doc.Set("items", JsonValue.Array(items.Select(item => item is JsonObjectValue run && Long(run, "id") == id.Value ? updated : item)));
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

    /// <summary>Validates a typed input against the job's schema, or clamps numeric inputs to their ranges, and fills in defaults.</summary>
    /// <exception cref="HostValidationException">A typed input does not match the schema.</exception>
    /// <exception cref="HostTooLargeException">A typed input exceeds <see cref="JobDefinition.MaxInputBytes"/>.</exception>
    internal static JsonObjectValue Normalize(JobDefinition job, JsonObjectValue? input)
    {
        if (job.InputSchema is not null || job.Admit is not null)
        {
            if (input is not null && Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(input)) > job.MaxInputBytes)
                throw new HostTooLargeException($"The input of {job.Id} may have at most {job.MaxInputBytes} bytes.");
            return job.Admit is { } admit ? admit(input) : InputSchema.Normalize(job.InputSchema!, input, $"Invalid input for {job.Id}");
        }
        return JsonValue.Object(job.Inputs.Select(definition =>
        {
            var given = input is not null && input.TryGetValue(definition.Name, out var value) && value is JsonNumberValue number ? number.Value : definition.Default;
            return new KeyValuePair<string, JsonValue>(definition.Name, Math.Clamp(Math.Round(given), definition.Minimum, definition.Maximum));
        }));
    }

    private static JsonObjectValue Checkpoint(string phase, int iteration, JsonObjectValue data, double? wakeAt = null, long? approvalTask = null)
    {
        var checkpoint = JsonValue.Object([new("phase", phase), new("iteration", iteration), new("data", data)]);
        if (wakeAt is { } at) checkpoint = checkpoint.SetItem("wakeAt", at);
        if (approvalTask is { } approval) checkpoint = checkpoint.SetItem("approvalTask", approval);
        return checkpoint;
    }

    private static async Task RunPhaseAsync(JobDefinition job, JobPhase phase, JobPhase? next, RunningTask task, ITaskRuntime runtime, DurableContext context,
        DurableDeviceHost host, string sessionId)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var iteration = (int)(Long(checkpoint, "iteration") ?? 0);
        var data = checkpoint.TryGetValue("data", out var carried) && carried is JsonObjectValue json ? json : JsonObjectValue.Empty;
        if (iteration == 0 && phase.When is { } when && !when(data))
        {
            await BypassAsync(phase, next, data, runtime, context).ConfigureAwait(false);
            return;
        }
        if (checkpoint.TryGetValue("wakeAt", out var wake) && wake is JsonNumberValue at) await runtime.SleepAsync(at.Value, context).ConfigureAwait(false);
        await runtime.CommitAsync((tx, running) => UpdateRunAsync(tx, running.ConversationId, running.Id,
            run => Begin(run, phase, iteration, runtime.Now())), context).ConfigureAwait(false);
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var input = ((JsonObjectValue)task.Input).TryGetValue("input", out var given) && given is JsonObjectValue values ? values : JsonObjectValue.Empty;
        var jobContext = new JobContext(job, phase, iteration, input, data, agent.Cwd ?? Environment.CurrentDirectory, runtime, task, context, host, sessionId);
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
            JsonObjectValue? finished = null;
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
            {
                var updated = UpdatePhase(run, phase.Id, item => item.SetItem("status", "done").SetItem("summary", result.Summary).SetItem("endedAt", now));
                return next is null ? finished = Finish(updated.SetItem("result", result.Data), "completed", "skipped", now) : updated.SetItem("phase", next.Id);
            }).ConfigureAwait(false);
            return next is null
                ? (TaskState?)(String(finished!, "status") == "completed" ? NextTaskState.Completed(result.Data) : NextTaskState.Failed(String(finished!, "error")!))
                : NextTaskState.Running(Checkpoint(next.Id, 0, result.Data));
        }, context).ConfigureAwait(false);
    }

    private static async Task StartApprovalAsync(JobDefinition job, JobPhase phase, JobPhase? next, RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var data = ((JsonObjectValue)task.Checkpoint).TryGetValue("data", out var carried) && carried is JsonObjectValue json ? json : JsonObjectValue.Empty;
        if (phase.When is { } when && !when(data))
        {
            await BypassAsync(phase, next, data, runtime, context).ConfigureAwait(false);
            return;
        }
        // Decision 0010: an approval that reviews a judgment links to it and may expire; it never approves by itself.
        var judgmentId = String(data, "judgmentId");
        var deadline = data.TryGetValue("reviewDeadline", out var due) && due is JsonNumberValue dueAt ? dueAt.Value : (double?)null;
        await runtime.CommitAsync(async (tx, running) =>
        {
            var input = JsonValue.Object([new("job", job.Id), new("phase", phase.Id)]);
            if (deadline is { } at) input = input.SetItem("deadline", at);
            var approval = await tx.CreateTaskAsync(ApprovalTask, input,
                new TaskOptions(TaskOwnership.Task(running.Id)) { ConversationId = running.ConversationId }).ConfigureAwait(false);
            await tx.DocAsync(ApprovalDoc, approval).ConfigureAwait(false);
            if (judgmentId is not null) await Judgments.PendingAsync(tx, judgmentId, approval.Value, deadline).ConfigureAwait(false);
            var now = runtime.Now();
            var pending = JsonValue.Object([new("phase", phase.Id), new("taskId", approval.Value), new("status", "pending")]);
            if (judgmentId is not null) pending = pending.SetItem("judgmentId", judgmentId);
            if (deadline is { } expires) pending = pending.SetItem("deadline", expires);
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run => UpdatePhase(run, phase.Id,
                    item => item.SetItem("status", "waiting").SetItem("startedAt", now).SetItem("summary", phase.Summary))
                .SetItem("status", "waiting").SetItem("phase", phase.Id)
                .SetItem("approval", pending)).ConfigureAwait(false);
            return (TaskState?)NextTaskState.Waiting(Checkpoint(phase.Id + ".decided", 0, data, approvalTask: approval.Value), [approval], JoinPolicy.AllSettled);
        }, context).ConfigureAwait(false);
    }

    /// <summary>Skips a phase whose <see cref="JobPhase.When"/> is false: it is <c>bypassed</c> and the next phase starts with the same
    /// data.</summary>
    private static Task BypassAsync(JobPhase phase, JobPhase? next, JsonObjectValue data, ITaskRuntime runtime, DurableContext context) =>
        runtime.CommitAsync(async (tx, running) =>
        {
            var now = runtime.Now();
            JsonObjectValue? finished = null;
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
            {
                var updated = UpdatePhase(run, phase.Id, item => item.SetItem("status", "bypassed").SetItem("summary", "Not needed")
                    .SetItem("startedAt", now).SetItem("endedAt", now));
                return next is null ? finished = Finish(updated.SetItem("result", data), "completed", "skipped", now) : updated.SetItem("phase", next.Id);
            }).ConfigureAwait(false);
            return next is null
                ? (TaskState?)(String(finished!, "status") == "completed" ? NextTaskState.Completed(data) : NextTaskState.Failed(String(finished!, "error")!))
                : NextTaskState.Running(Checkpoint(next.Id, 0, data));
        }, context);

    private static async Task DecidedAsync(JobDefinition job, JobPhase phase, JobPhase? next, RunningTask task, ITaskRuntime runtime, DurableContext context)
    {
        var checkpoint = (JsonObjectValue)task.Checkpoint;
        var data = checkpoint.TryGetValue("data", out var carried) && carried is JsonObjectValue json ? json : JsonObjectValue.Empty;
        var approvalTask = new TaskId(Long(checkpoint, "approvalTask") ?? 0);
        var outcome = (await runtime.OutcomesAsync([approvalTask], context).ConfigureAwait(false)).Single();
        var decision = outcome is CompletedTaskOutcome { Result: JsonObjectValue result } ? result : JsonObjectValue.Empty;
        var approved = decision.TryGetValue("approved", out var flag) && flag is JsonBooleanValue { Value: true };
        var expired = decision.TryGetValue("expired", out var lapsed) && lapsed is JsonBooleanValue { Value: true };
        var actor = String(decision, "actor") ?? (expired ? "system" : "you");
        var reason = String(decision, "reason");
        var judgmentId = String(data, "judgmentId");
        await runtime.CommitAsync(async (tx, running) =>
        {
            var now = runtime.Now();
            JsonObjectValue Approval(string status)
            {
                var value = JsonValue.Object([new("phase", phase.Id), new("taskId", approvalTask.Value), new("status", status), new("actor", actor),
                    new("reason", reason is null ? JsonValue.Null : reason)]);
                return judgmentId is null ? value : value.SetItem("judgmentId", judgmentId);
            }
            if (!approved)
            {
                var status = outcome.Status != TaskOutcomeStatus.Completed ? "cancelled" : expired ? "expired" : "declined";
                if (judgmentId is not null)
                    await Judgments.DecideAsync(tx, judgmentId, status switch { "declined" => "reject", "expired" => "expired", _ => "cancel" }, actor, reason, now)
                        .ConfigureAwait(false);
                await UpdateRunAsync(tx, running.ConversationId, running.Id, run => Finish(
                    UpdatePhase(run, phase.Id, item => item.SetItem("status", status == "cancelled" ? "aborted" : "declined")
                        .SetItem("summary", status switch { "declined" => "Declined by you", "expired" => "The review expired", _ => "Cancelled" }).SetItem("endedAt", now))
                    .SetItem("approval", Approval(status)),
                    status == "cancelled" ? "aborted" : "declined", "skipped", now)).ConfigureAwait(false);
                return (TaskState?)NextTaskState.Completed(JsonValue.Object([new("declined", true)]));
            }
            if (judgmentId is not null) await Judgments.DecideAsync(tx, judgmentId, "approve", actor, reason, now).ConfigureAwait(false);
            JsonObjectValue? finished = null;
            await UpdateRunAsync(tx, running.ConversationId, running.Id, run =>
            {
                var updated = UpdatePhase(run, phase.Id, item => item.SetItem("status", "done").SetItem("summary", "Approved by you").SetItem("endedAt", now))
                    .SetItem("approval", Approval("approved"))
                    .SetItem("status", "running");
                return next is null ? finished = Finish(updated.SetItem("result", data), "completed", "skipped", now) : updated.SetItem("phase", next.Id);
            }).ConfigureAwait(false);
            return next is null ? (TaskState?)(String(finished!, "status") == "completed" ? NextTaskState.Completed(data) : NextTaskState.Failed(String(finished!, "error")!))
                : NextTaskState.Running(Checkpoint(next.Id, 0, data));
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
        var finished = JobBranches.Close(run, now).SetItem("status", status).SetItem("phases", JsonValue.Array(phases)).SetItem("endedAt", now);
        finished = finished.TryGetValue("approval", out var approval) && approval is JsonObjectValue pending && String(pending, "status") == "pending"
            ? finished.SetItem("approval", pending.SetItem("status", "cancelled"))
            : finished;
        var expected = Items(run, "phases").OfType<JsonObjectValue>().Select(phase => "phase:" + String(phase, "id"))
            .Concat(Items(run, "requiredChecks").OfType<JsonStringValue>().Select(check => "check:" + check.Value)).ToArray();
        var done = Items(finished, "phases").OfType<JsonObjectValue>().Where(phase => String(phase, "status") is "done" or "bypassed").Select(phase => "phase:" + String(phase, "id"))
            .Concat(((run.TryGetValue("checks", out var checks) ? checks as JsonObjectValue : null) ?? JsonObjectValue.Empty)
                .Where(check => check.Value is JsonObjectValue value && value.TryGetValue("passed", out var passed) && passed is JsonBooleanValue { Value: true })
                .Select(check => "check:" + check.Key)).ToArray();
        done = done.Intersect(expected).ToArray();
        var missing = expected.Except(done).ToArray();
        var passedAudit = status == "completed" && missing.Length == 0;
        finished = finished.SetItem("audit", JsonValue.Object([new("status", passedAudit ? "passed" : status == "completed" ? "failed" : "incomplete"),
            new("expected", JsonValue.Array(expected.Select(item => (JsonValue)item))), new("done", JsonValue.Array(done.Select(item => (JsonValue)item))),
            new("missing", JsonValue.Array(missing.Select(item => (JsonValue)item))), new("at", now)]));
        return status == "completed" && !passedAudit ? finished.SetItem("status", "failed").SetItem("error", "Completion audit failed: " + string.Join(", ", missing)) : finished;
    }

    /// <summary>Waits until the approval document holds a decision, watching it rather than polling.</summary>
    private static async Task<JsonObjectValue> WaitForDecisionAsync(ITaskRuntime runtime, TaskId approvalTask, DurableContext context, double? deadline = null)
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
            if (deadline is not { } at) return await context.WaitAsync(decided.Task).ConfigureAwait(false);
            // A review deadline (decision 0010) resolves the approval as expired, never approved. The decision document is written
            // only when nobody decided first, so a late person and the deadline cannot both win.
            using var stop = new CancellationTokenSource();
            var sleeping = runtime.SleepAsync(at, context.WithCancellation(stop.Token));
            var waiting = context.WaitAsync(decided.Task);
            if (await Task.WhenAny(waiting, sleeping).ConfigureAwait(false) == sleeping && !decided.Task.IsCompleted)
            {
                await sleeping.ConfigureAwait(false);
                await runtime.CommitAsync(async (tx, _) =>
                {
                    var doc = await tx.DocAsync(ApprovalDoc, approvalTask).ConfigureAwait(false);
                    if (doc.Get("decision") is null)
                        doc.Set("decision", JsonValue.Object([new("approved", false), new("expired", true), new("at", runtime.Now()), new("actor", "system")]));
                }, context).ConfigureAwait(false);
                return await waiting.ConfigureAwait(false);
            }
            stop.Cancel();
            try { await sleeping.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return await waiting.ConfigureAwait(false);
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
