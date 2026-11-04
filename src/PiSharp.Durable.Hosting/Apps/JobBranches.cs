// PiSharp-only (decision 0010, M4): a bounded fan-out of a job phase into child conversations the job task owns. Every branch gets its child
// in one commit before any starts, so a restart rejoins the same children; each attempt has its own request identity
// (job:<task>:<phase>:<iteration>:branch:<id>:<attempt>), budget reservation and durable deadline, and its state lives in the run record's
// `branches`. AskAgentAsync stays the sequential single-child call it was; this is the concurrent one.
using System.Diagnostics;
using System.Globalization;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>One branch of <see cref="JobContext.FanOutAsync"/>: a question for its own child conversation.</summary>
/// <param name="Id">Unique within the fan-out: <c>^[a-z0-9][a-z0-9_-]{0,39}$</c>.</param>
/// <param name="Prompt">The question, at most 100 000 characters.</param>
public sealed record JobBranch(string Id, string Prompt)
{
    /// <summary>A display title (at most 200 characters); the ID when null.</summary>
    public string? Title { get; init; }
    /// <summary>The child's model; null uses the job agent's model.</summary>
    public ModelRef? Model { get; init; }
    /// <summary>The child's thinking level; null is <c>off</c>.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Instructions for the child, rendered as its instructions section (at most 8000 characters).</summary>
    public string? Instructions { get; init; }
    /// <summary>The tools the child may use: a subset of <see cref="JobBranches.ReadOnlyTools"/>, and <c>fetch_page</c> when the branch has a
    /// <see cref="Retrieval"/>.</summary>
    public IReadOnlyList<string> Tools { get; init; } = JobBranches.ReadOnlyTools;
    /// <summary>What the child may fetch with <c>fetch_page</c>; null gives it nothing to fetch. Its fetches count against the run's
    /// limits.</summary>
    public JobRetrieval? Retrieval { get; init; }
}

/// <summary>How <see cref="JobContext.FanOutAsync"/> runs its branches.</summary>
public sealed record JobFanOutOptions
{
    /// <summary>How many branches run at once: 1 to <see cref="JobBranches.MaxConcurrency"/>.</summary>
    public int Concurrency { get; init; } = 2;
    /// <summary>Attempts per branch: 1 to 3. A failed or expired attempt is tried again until they are used.</summary>
    public int MaxAttempts { get; init; } = 2;
    /// <summary>How long one attempt may take, at most an hour; the child is stopped when it passes.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>A branch's outcome: <c>completed</c> with its answer, or <c>failed</c> or <c>cancelled</c> with the last error.</summary>
public sealed record JobBranchResult(string Id, string Status, string? Answer, string? Error, int Attempts, long ConversationId)
{
    /// <summary>Whether the branch answered.</summary>
    public bool Completed => Status == "completed";
}

/// <summary>Limits and records of job branches.</summary>
public static class JobBranches
{
    /// <summary>The tools a branch may be given: they only read.</summary>
    public static IReadOnlyList<string> ReadOnlyTools { get; } = ["read", "search_history"];
    /// <summary>The host ceiling of branches running at once.</summary>
    public const int MaxConcurrency = 5;
    /// <summary>The most branches one fan-out may have.</summary>
    public const int MaxBranches = 10;

    internal static void Check(IReadOnlyList<JobBranch> branches, JobFanOutOptions options)
    {
        if (branches.Count is < 1 or > MaxBranches) throw new HostInvalidException($"A fan-out has 1 to {MaxBranches} branches.");
        if (options.Concurrency is < 1 or > MaxConcurrency) throw new HostInvalidException($"Fan-out concurrency is 1 to {MaxConcurrency}.");
        if (options.MaxAttempts is < 1 or > 3) throw new HostInvalidException("A branch makes 1 to 3 attempts.");
        if (options.Deadline <= TimeSpan.Zero || options.Deadline > TimeSpan.FromHours(1)) throw new HostInvalidException("A branch deadline is at most an hour.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            if (!JudgmentPolicy.IsId(branch.Id) || !seen.Add(branch.Id)) throw new HostInvalidException($"Branch IDs must be unique and match ^[a-z0-9][a-z0-9_-]{{0,39}}$: {branch.Id}.");
            if (string.IsNullOrWhiteSpace(branch.Prompt) || branch.Prompt.Length > 100_000) throw new HostInvalidException($"Branch {branch.Id} needs a prompt of at most 100000 characters.");
            if (branch.Title is { Length: > 200 } || branch.Instructions is { Length: > 8000 }) throw new HostInvalidException($"Branch {branch.Id} has too long a title or instructions.");
            if (branch.ThinkingLevel is { } level && !HostNames.ThinkingLevels.Contains(level, StringComparer.Ordinal))
                throw new HostInvalidException($"Branch {branch.Id} has an unknown thinking level {level}.");
            if (branch.Tools.Any(tool => !ReadOnlyTools.Contains(tool, StringComparer.Ordinal) && !(tool == BranchRetrieval.ToolName && branch.Retrieval is not null)))
                throw new HostInvalidException($"Branch {branch.Id} may only use {string.Join(", ", ReadOnlyTools)}, and {BranchRetrieval.ToolName} with a retrieval allowance.");
            if (branch.Retrieval is { } retrieval) BranchRetrieval.Normalize(branch.Id, retrieval);
        }
    }

    /// <summary>A branch row's key in the run record: phase, iteration and branch ID.</summary>
    internal static string Key(string phase, int iteration, string id) => $"{phase}:{iteration.ToString(CultureInfo.InvariantCulture)}:{id}";

    /// <summary>The request IDs of the attempts still running in a run record.</summary>
    internal static IReadOnlyList<string> OpenRequests(JsonObjectValue run) =>
        [.. Jobs.Items(run, "branches").OfType<JsonObjectValue>().SelectMany(row => Jobs.Items(row, "attempts").OfType<JsonObjectValue>())
            .Where(attempt => Jobs.String(attempt, "status") == "running").Select(attempt => Jobs.String(attempt, "requestId")).OfType<string>()];

    /// <summary>The child conversations of a run record's branches.</summary>
    internal static IReadOnlyList<long> Children(JsonObjectValue run) =>
        [.. Jobs.Items(run, "branches").OfType<JsonObjectValue>().Select(row => Jobs.Long(row, "conversationId")).OfType<long>()];

    /// <summary>Marks the branches still planned or running as <c>cancelled</c> when a run ends.</summary>
    internal static JsonObjectValue Close(JsonObjectValue run, double now) =>
        run.TryGetValue("branches", out var value) && value is JsonArrayValue branches
            ? run.SetItem("branches", JsonValue.Array(branches.Items.Select(item => item is JsonObjectValue row && Jobs.String(row, "status") is "planned" or "running"
                ? row.SetItem("status", "cancelled").SetItem("endedAt", now)
                    .SetItem("attempts", JsonValue.Array(Jobs.Items(row, "attempts").Select(attempt => attempt is JsonObjectValue open && Jobs.String(open, "status") == "running"
                        ? open.SetItem("status", "cancelled").SetItem("endedAt", now) : attempt)))
                : item)))
            : run;
}

public sealed partial class JobContext
{
    /// <summary>
    /// Asks every branch its question in its own child conversation, at most <see cref="JobFanOutOptions.Concurrency"/> at once and in
    /// branch order, and returns the outcomes in branch order. The children are created (with their model, thinking level and read-only
    /// tools) in one commit before any branch starts; a rerun of the phase finds them, keeps completed answers, and waits for a running
    /// attempt instead of asking again. Each attempt reserves budget under the run's limits (a refused reservation fails the branch) and
    /// is stopped at its deadline. <paramref name="completed"/> runs once per completed branch, also for one completed before a restart
    /// whose callback had not finished, and may return fields (an artifact ID, say) to keep on the branch's row.
    /// </summary>
    /// <exception cref="HostInvalidException">The branches or options are invalid.</exception>
    public async Task<IReadOnlyList<JobBranchResult>> FanOutAsync(IReadOnlyList<JobBranch> branches, JobFanOutOptions? options = null,
        Func<JobBranchResult, Task<JsonObjectValue?>>? completed = null)
    {
        ArgumentNullException.ThrowIfNull(branches);
        options ??= new JobFanOutOptions();
        JobBranches.Check(branches, options);
        if (branches.Any(Fetches) && runtime.Registry.GetExtension(BranchRetrieval.ExtensionName) is null)
            throw new HostInvalidException($"This device's registry has no {BranchRetrieval.ExtensionName} extension, so no branch can fetch pages.");
        var agent = await runtime.AgentAsync(durable).ConfigureAwait(false);
        foreach (var branch in branches)
            if ((branch.Model ?? agent.Model) is { } model) Host.RequireModel(model);
            else throw new HostInvalidException($"Branch {branch.Id} has no model.");
        var children = await EnsureBranchesAsync(branches, agent.Model!).ConfigureAwait(false);
        var results = new JobBranchResult[branches.Count];
        var next = -1;
        async Task WorkAsync()
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < branches.Count)
                results[index] = await RunBranchAsync(branches[index], children[branches[index].Id], options, completed).ConfigureAwait(false);
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(options.Concurrency, branches.Count)).Select(_ => WorkAsync())).ConfigureAwait(false);
        return results;
    }

    /// <summary>Creates the children the run record does not list yet, all in one commit; returns each branch's child.</summary>
    private async Task<Dictionary<string, long>> EnsureBranchesAsync(IReadOnlyList<JobBranch> branches, ModelRef agentModel)
    {
        var children = new Dictionary<string, long>(StringComparer.Ordinal);
        await runtime.CommitAsync(async (tx, running) =>
        {
            var record = (await tx.DocAsync(Jobs.RecordDoc, running.ConversationId, running.Id.Value.ToString(CultureInfo.InvariantCulture), JsonValue.Null)
                .ConfigureAwait(false)).Get("run") as JsonObjectValue;
            var rows = Jobs.Items(record, "branches").OfType<JsonObjectValue>().ToList();
            var added = new List<JsonValue>();
            foreach (var branch in branches)
            {
                var key = JobBranches.Key(Phase.Id, Iteration, branch.Id);
                if (rows.FirstOrDefault(row => Jobs.String(row, "key") == key) is { } existing)
                {
                    children[branch.Id] = Jobs.Long(existing, "conversationId")!.Value;
                    continue;
                }
                var model = branch.Model ?? agentModel;
                var created = await tx.CreateConversationAsync(ConversationOwnership.Task(running.Id)).ConfigureAwait(false);
                var tools = runtime.Registry.Tools().Select(registered => registered.Tool).Where(tool => branch.Tools.Contains(tool.Name, StringComparer.Ordinal)).ToList();
                var change = new AgentChange { Model = model, ThinkingLevel = branch.ThinkingLevel ?? "off", Tools = ToolSelection.Only(tools) };
                if (branch.Instructions is { } instructions) change = change with { Instructions = instructions };
                if (Fetches(branch))
                {
                    // Only this child selects fetch_page's extension, and its allowance lives on the child, written in the same commit.
                    change = change with { Extensions = ExtensionSelection.Edit(add: [runtime.Registry.GetExtension(BranchRetrieval.ExtensionName)!]) };
                    var allowance = await tx.DocAsync(BranchRetrieval.AllowanceDoc, created.Id).ConfigureAwait(false);
                    allowance.Set("allowance", BranchRetrieval.Allowance(BranchRetrieval.Normalize(branch.Id, branch.Retrieval!), RunKey, TaskId, Budget));
                }
                await Agents.ConfigureAsync(tx, created.Id, change).ConfigureAwait(false);
                children[branch.Id] = created.Id.Value;
                added.Add(JsonValue.Object(
                [
                    new("key", key), new("id", branch.Id), new("phase", Phase.Id), new("iteration", Iteration), new("title", branch.Title ?? branch.Id),
                    new("conversationId", created.Id.Value), new("model", $"{model.Provider}/{model.ModelId}"), new("thinking", branch.ThinkingLevel ?? "off"),
                    new("status", "planned"), new("attempts", JsonArrayValue.Empty), new("answerEntryId", JsonValue.Null), new("answerChars", JsonValue.Null),
                    new("error", JsonValue.Null), new("published", false), new("startedAt", JsonValue.Null), new("endedAt", JsonValue.Null),
                ]));
            }
            if (added.Count > 0)
                await Jobs.UpdateRunAsync(tx, running.ConversationId, running.Id, run => run.SetItem("branches", JsonValue.Array(Jobs.Items(run, "branches").Concat(added))))
                    .ConfigureAwait(false);
        }, durable).ConfigureAwait(false);
        return children;
    }

    private static bool Fetches(JobBranch branch) => branch.Retrieval is not null && branch.Tools.Contains(BranchRetrieval.ToolName, StringComparer.Ordinal);

    private async Task<JobBranchResult> RunBranchAsync(JobBranch branch, long child, JobFanOutOptions options, Func<JobBranchResult, Task<JsonObjectValue?>>? completed)
    {
        var key = JobBranches.Key(Phase.Id, Iteration, branch.Id);
        while (true)
        {
            var row = await BranchRowAsync(key).ConfigureAwait(false);
            var status = Jobs.String(row, "status");
            var attempts = Jobs.Items(row, "attempts").OfType<JsonObjectValue>().ToList();
            if (status == "completed")
            {
                var answer = await AnswerAsync(new EntryId(Jobs.Long(row, "answerEntryId")!.Value)).ConfigureAwait(false);
                var result = new JobBranchResult(branch.Id, "completed", answer, null, attempts.Count, child);
                if (completed is not null && row.TryGetValue("published", out var published) && published is JsonBooleanValue { Value: false })
                {
                    var fields = await completed(result).ConfigureAwait(false) ?? JsonObjectValue.Empty;
                    await UpdateBranchAsync(key, current => fields.Aggregate(current, (merged, field) => field.Key is "key" or "id" or "status" or "attempts"
                        ? merged : merged.SetItem(field.Key, field.Value)).SetItem("published", true)).ConfigureAwait(false);
                }
                return result;
            }
            if (status is "failed" or "cancelled") return new JobBranchResult(branch.Id, status, null, Jobs.String(row, "error"), attempts.Count, child);

            JsonObjectValue attempt;
            var resumed = false;
            if (attempts.LastOrDefault() is { } open && Jobs.String(open, "status") == "running")
            {
                attempt = open;
                resumed = true;
            }
            else if (attempts.Count >= options.MaxAttempts)
            {
                var error = attempts.LastOrDefault() is { } last ? Jobs.String(last, "error") : null;
                await UpdateBranchAsync(key, current => current.SetItem("status", "failed").SetItem("endedAt", runtime.Now())).ConfigureAwait(false);
                return new JobBranchResult(branch.Id, "failed", null, error, attempts.Count, child);
            }
            else
            {
                var number = attempts.Count + 1;
                var requestId = $"job:{TaskId.ToString(CultureInfo.InvariantCulture)}:{Phase.Id}:{Iteration.ToString(CultureInfo.InvariantCulture)}:branch:{branch.Id}:{number.ToString(CultureInfo.InvariantCulture)}";
                var model = ModelRef(row);
                var estimate = Estimate(branch, model);
                try
                {
                    Host.Budgets.Reserve(requestId, RunKey, model.Provider, estimate, Budget);
                }
                catch (HostLimitException limited)
                {
                    var now = runtime.Now();
                    await UpdateBranchAsync(key, current => current.SetItem("status", "failed").SetItem("error", "limited: " + limited.Message).SetItem("endedAt", now)
                        .SetItem("attempts", JsonValue.Array(Jobs.Items(current, "attempts").Append(JsonValue.Object([new("attempt", number), new("requestId", requestId),
                            new("status", "limited"), new("startedAt", now), new("endedAt", now), new("error", limited.Message)]))))).ConfigureAwait(false);
                    await StepAsync("agent", $"Branch {branch.Id}: attempt {number}", "error", 0, limited.Message, child).ConfigureAwait(false);
                    return new JobBranchResult(branch.Id, "failed", null, "limited: " + limited.Message, number, child);
                }
                var mark = await LastEntryAsync(child).ConfigureAwait(false);
                var started = runtime.Now();
                attempt = JsonValue.Object([new("attempt", number), new("requestId", requestId), new("status", "running"), new("startedAt", started),
                    new("deadlineAt", started + options.Deadline.TotalMilliseconds), new("mark", mark), new("endedAt", JsonValue.Null), new("error", JsonValue.Null)]);
                var opened = attempt;
                await UpdateBranchAsync(key, current => current.SetItem("status", "running").SetItem("attempts", JsonValue.Array(Jobs.Items(current, "attempts").Append(opened)))
                    .SetItem("startedAt", current.TryGetValue("startedAt", out var at) && at is JsonNumberValue ? at : started)).ConfigureAwait(false);
            }
            await AttemptAsync(branch, key, child, attempt, resumed, options).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one attempt to its end, settles its reservation, and records how it ended on the branch's row.</summary>
    private async Task AttemptAsync(JobBranch branch, string key, long child, JsonObjectValue attempt, bool resumed, JobFanOutOptions options)
    {
        var timer = Stopwatch.StartNew();
        var number = (int)Jobs.Long(attempt, "attempt")!.Value;
        var requestId = Jobs.String(attempt, "requestId")!;
        var deadlineAt = ((JsonNumberValue)attempt["deadlineAt"]).Value;
        var handle = await runtime.ConversationAsync(new ConversationId(child), durable).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The child conversation {child} of branch {branch.Id} is gone.");
        var submission = await handle.SubmitAsync(SubmissionDraft.Input(branch.Prompt, requestId: requestId), durable).ConfigureAwait(false);
        var waiting = submission.WaitAsync(durable);
        using var stop = new CancellationTokenSource();
        var sleeping = Jobs.DelayUntilAsync(runtime, deadlineAt, durable.WithCancellation(stop.Token));
        var expired = false;
        if (await Task.WhenAny(waiting, sleeping).ConfigureAwait(false) == sleeping && !waiting.IsCompleted)
        {
            await sleeping.ConfigureAwait(false);
            expired = true;
            await handle.AbortAsync(durable).ConfigureAwait(false);
        }
        else
        {
            stop.Cancel();
            try { await sleeping.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* The answer came first. */ }
        }
        var settled = await waiting.ConfigureAwait(false);
        if (Fetches(branch)) await BranchRetrieval.CloseAsync(Host, runtime, new ConversationId(child), durable).ConfigureAwait(false);
        string? answer = null;
        if (!expired && settled.Status == SubmissionStatus.Done && settled.Answer is { } entry)
        {
            answer = await AnswerAsync(entry).ConfigureAwait(false);
            if (answer.Trim().Length == 0) answer = null;
        }
        var error = answer is not null ? null : expired ? $"The attempt passed its {options.Deadline.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s deadline."
            : settled.Status == SubmissionStatus.Done ? "The answer was empty." : $"No answer: {settled.Reason}{(settled.Detail is JsonStringValue detail ? " — " + detail.Value : "")}";
        var (requests, tokens, cost) = await UsageSinceAsync(child, Jobs.Long(attempt, "mark") ?? 0).ConfigureAwait(false);
        if (Host.Budgets.Get(requestId) is { Status: "reserved" } reservation)
        {
            if (requests == 0) Host.Budgets.Release(requestId);
            else
            {
                var actual = new Dictionary<string, long>(StringComparer.Ordinal) { ["requests"] = requests, ["tokens"] = tokens };
                foreach (var meter in reservation.Requested.Keys.Where(meter => meter.StartsWith("money:", StringComparison.Ordinal)))
                    actual[meter] = meter == "money:USD" ? (long)Math.Ceiling(cost * 1_000_000) : reservation.Requested[meter];
                // An attempt the device resumed may have repeated a provider call it never recorded.
                Host.Budgets.Settle(requestId, actual, costKnown: !resumed);
            }
        }
        var now = runtime.Now();
        var outcome = answer is not null ? "completed" : expired ? "expired" : "failed";
        await UpdateBranchAsync(key, current =>
        {
            var updated = current.SetItem("attempts", JsonValue.Array(Jobs.Items(current, "attempts").Select(item => item is JsonObjectValue row && Jobs.Long(row, "attempt") == number
                ? row.SetItem("status", outcome).SetItem("endedAt", now).SetItem("error", error is null ? JsonValue.Null : error).SetItem("resumed", resumed)
                    .SetItem("usage", JsonValue.Object([new("requests", requests), new("tokens", tokens), new("cost", cost)]))
                : item)));
            return answer is not null
                ? updated.SetItem("status", "completed").SetItem("answerEntryId", settled.Answer!.Value.Value).SetItem("answerChars", answer.Length)
                    .SetItem("error", JsonValue.Null).SetItem("endedAt", now)
                : updated.SetItem("error", error!);
        }).ConfigureAwait(false);
        await StepAsync("agent", $"Branch {branch.Id}: attempt {number}{(resumed ? " (resumed)" : "")}", answer is not null ? "done" : "error",
            timer.ElapsedMilliseconds, answer is not null ? (answer.Length > 2000 ? answer[..2000] + "…" : answer) : error, child).ConfigureAwait(false);
    }

    /// <summary>The branch rows of a phase run, in the order they were planned, with each completed branch's answer.</summary>
    internal async Task<IReadOnlyList<(JsonObjectValue Row, string? Answer)>> BranchesAsync(string phase, int iteration = 0)
    {
        var record = await runtime.SnapshotAsync(Jobs.RecordDoc, task.ConversationId, task.Id.Value.ToString(CultureInfo.InvariantCulture), durable).ConfigureAwait(false);
        var rows = Jobs.Items(record?.GetValueOrDefault("run") as JsonObjectValue, "branches").OfType<JsonObjectValue>()
            .Where(row => Jobs.String(row, "phase") == phase && Jobs.Long(row, "iteration") == iteration).ToList();
        var result = new List<(JsonObjectValue, string?)>();
        foreach (var row in rows)
            result.Add((row, Jobs.String(row, "status") == "completed" && Jobs.Long(row, "answerEntryId") is { } entry
                ? await AnswerAsync(new EntryId(entry)).ConfigureAwait(false) : null));
        return result;
    }

    private static ModelRef ModelRef(JsonObjectValue row)
    {
        var name = Jobs.String(row, "model")!;
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        return new ModelRef(name[..slash], name[(slash + 1)..]);
    }

    /// <summary>What an attempt reserves: one request and the prompt plus the model's largest answer, and its price when the catalog has
    /// one. The agent loop may make more requests; the reservation settles at what the child recorded.</summary>
    private Dictionary<string, long> Estimate(JobBranch branch, ModelRef model)
    {
        var known = Host.Options.Models.GetModel(model.Provider, model.ModelId);
        var inputTokens = (long)Math.Ceiling((branch.Prompt.Length + (branch.Instructions?.Length ?? 0) + 2000) / 3.0);
        var outputTokens = (long)Math.Clamp(known?.MaxTokens ?? 4096, 256, 32_000);
        var requested = new Dictionary<string, long>(StringComparer.Ordinal) { ["requests"] = 1, ["tokens"] = inputTokens + outputTokens };
        if (known?.Wire is { } wire && wire.TryGetValue("cost", out var costValue) && costValue is JsonObjectValue cost &&
            cost.TryGetValue("input", out var input) && input is JsonNumberValue inputPrice && cost.TryGetValue("output", out var output) && output is JsonNumberValue outputPrice &&
            inputPrice.Value >= 0 && outputPrice.Value >= 0)
            requested["money:USD"] = (long)Math.Ceiling(inputTokens * inputPrice.Value + outputTokens * outputPrice.Value);
        return requested;
    }

    private async Task<JsonObjectValue> BranchRowAsync(string key)
    {
        var record = await runtime.SnapshotAsync(Jobs.RecordDoc, task.ConversationId, task.Id.Value.ToString(CultureInfo.InvariantCulture), durable).ConfigureAwait(false);
        var run = record?.GetValueOrDefault("run") as JsonObjectValue;
        return Jobs.Items(run, "branches").OfType<JsonObjectValue>().FirstOrDefault(row => Jobs.String(row, "key") == key)
            ?? throw new InvalidOperationException($"Branch {key} has no row.");
    }

    private Task UpdateBranchAsync(string key, Func<JsonObjectValue, JsonObjectValue> change) =>
        runtime.CommitAsync((tx, running) => Jobs.UpdateRunAsync(tx, running.ConversationId, running.Id, run => run.SetItem("branches",
            JsonValue.Array(Jobs.Items(run, "branches").Select(item => item is JsonObjectValue row && Jobs.String(row, "key") == key ? change(row) : item)))), durable);

    private async Task<string> AnswerAsync(EntryId entry)
    {
        var answer = "";
        // Runtime.EntryAsync only reads entries visible in the parent's conversation; the answer belongs to the child.
        await runtime.CommitAsync(async (tx, _) => answer = SubmissionAnswers.TextOf(await tx.GetEntryAsync(Entries.AssistantEntry, entry).ConfigureAwait(false)),
            durable).ConfigureAwait(false);
        return answer;
    }

    private async Task<long> LastEntryAsync(long child)
    {
        long last = 0;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var page = await tx.ScanEntriesAsync(new EntryQuery(new ConversationId(child)) { Order = ScanOrder.Descending }, 1).ConfigureAwait(false);
            last = page.Items.FirstOrDefault()?.Id.Value ?? 0;
        }, durable).ConfigureAwait(false);
        return last;
    }

    /// <summary>The model requests, tokens and dollars the child recorded after entry <paramref name="mark"/>.</summary>
    private async Task<(long Requests, long Tokens, double Cost)> UsageSinceAsync(long child, long mark)
    {
        long requests = 0;
        double tokens = 0, cost = 0;
        await runtime.CommitAsync(async (tx, _) =>
        {
            requests = 0; tokens = 0; cost = 0;
            JsonObjectValue? cursor = null;
            do
            {
                var page = await tx.ScanEntriesAsync(new EntryQuery(new ConversationId(child)) { MinEntryId = new EntryId(mark + 1), Order = ScanOrder.Ascending }, 500, cursor)
                    .ConfigureAwait(false);
                foreach (var message in page.Items.Where(entry => entry.ConversationId.Value == child).SelectMany(entry => entry.Model?.Items ?? []).OfType<JsonObjectValue>())
                {
                    if (Jobs.String(message, "role") != "assistant") continue;
                    requests++;
                    if (message.TryGetValue("usage", out var value) && value is JsonObjectValue usage)
                    {
                        if (usage.TryGetValue("totalTokens", out var total) && total is JsonNumberValue count) tokens += count.Value;
                        if (usage.TryGetValue("cost", out var price) && price is JsonObjectValue prices && prices.TryGetValue("total", out var sum) && sum is JsonNumberValue dollars)
                            cost += dollars.Value;
                    }
                }
                cursor = page.Next;
            } while (cursor is not null);
        }, durable).ConfigureAwait(false);
        return (requests, (long)Math.Ceiling(tokens), cost);
    }
}
