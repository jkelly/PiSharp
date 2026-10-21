// Decision 0009: authoritative history from durable task/entry records, rather than a lossy secondary projection.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

public sealed partial class HostedSession
{
    /// <summary>Chat generations and jobs, newest task first, with an opaque continuation. Filters apply before the limit.</summary>
    public async Task<JsonObjectValue> ExecutionsAsync(string? kind = null, string? status = null, string? query = null, int limit = 20,
        JsonObjectValue? cursor = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100 || query?.Length > 200 || kind is not (null or "" or "job" or "chat"))
            throw new HostInvalidException("Use a limit of 1–100, a query up to 200 characters and kind job or chat.");
        if (status is not (null or "" or "pending" or "running" or "waiting" or "completing" or "completed" or "failed" or "aborted" or "declined"))
            throw new HostInvalidException("Invalid execution status.");
        var context = Context(cancellationToken);
        var opened = GetHarness();
        var result = new List<JsonValue>();
        var next = cursor;
        do
        {
            var page = await opened.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Order = ScanOrder.Descending }, 1, next), context).ConfigureAwait(false);
            next = page.Next;
            foreach (var task in page.Items)
            {
                if (!IsExecution(task) || kind == "job" && !IsJob(task) || kind == "chat" && IsJob(task)) continue;
                var run = await RunRecordAsync(task, context).ConfigureAwait(false);
                var item = ExecutionSummary(task, run);
                if (status is { Length: > 0 } && Jobs.String(item, "status") != status) continue;
                if (query is { Length: > 0 } && !item.ToJson().Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(item);
            }
        } while (next is not null && result.Count < limit);
        return JsonValue.Object([new("items", JsonValue.Array(result)), new("next", next is { } more ? more : JsonValue.Null)]);
    }

    /// <summary>An execution with all descendant tasks, owned subagents and recorded model/tool entries, plus aggregate recorded usage. A job
    /// branch given a retrieval allowance carries its pages on its row (<c>retrieval</c>).</summary>
    public async Task<JsonObjectValue> ExecutionAsync(long taskId, CancellationToken cancellationToken = default)
    {
        TaskRecord? root = null;
        var context = Context(cancellationToken);
        var detail = await GetHarness().CommitAsync(async tx =>
        {
            root = await tx.GetTaskAsync(new TaskId(taskId)).ConfigureAwait(false);
            if (root is null || !IsExecution(root)) throw new HostNotFoundException($"No execution {taskId} in {Name}.");
            var tasks = new List<TaskRecord>();
            JsonObjectValue? cursor = null;
            do
            {
                var page = await tx.ScanTasksAsync(new TaskQuery(), 500, cursor).ConfigureAwait(false);
                tasks.AddRange(page.Items); cursor = page.Next;
            } while (cursor is not null);
            var conversations = new List<ConversationRecord>();
            do
            {
                var page = await tx.ScanConversationsAsync(new ConversationQuery(), 500, cursor).ConfigureAwait(false);
                conversations.AddRange(page.Items); cursor = page.Next;
            } while (cursor is not null);
            var owned = new HashSet<TaskId> { root.Id };
            var childConversations = new HashSet<ConversationId>();
            bool changed;
            do
            {
                changed = false;
                foreach (var conversation in conversations)
                    if (conversation.Owner is { } owner && owned.Contains(owner.TaskId)) changed |= childConversations.Add(conversation.Id);
                foreach (var task in tasks)
                    if (task.Owner is { } owner && owned.Contains(owner) || childConversations.Contains(task.ConversationId)) changed |= owned.Add(task.Id);
            } while (changed);
            var entries = new Dictionary<EntryId, EntryRecord>();
            foreach (var conversation in tasks.Where(task => owned.Contains(task.Id)).Select(task => task.ConversationId).Distinct())
            {
                do
                {
                    var page = await tx.ScanEntriesAsync(new EntryQuery(conversation) { Order = ScanOrder.Ascending }, 500, cursor).ConfigureAwait(false);
                    foreach (var entry in page.Items)
                        if (entry.ConversationId == conversation && (entry.ByTaskId is { } by && owned.Contains(by))) entries.TryAdd(entry.Id, entry);
                    cursor = page.Next;
                } while (cursor is not null);
            }
            double cost = 0, tokens = 0;
            foreach (var message in entries.Values.SelectMany(entry => entry.Model?.Items ?? []).OfType<JsonObjectValue>())
                if (message.TryGetValue("usage", out var value) && value is JsonObjectValue usage)
                {
                    tokens += Number(usage, "totalTokens");
                    if (usage.TryGetValue("cost", out var price) && price is JsonObjectValue prices) cost += Number(prices, "total");
                }
            return ExecutionSummary(root, null)
                .SetItem("tasks", JsonValue.Array(tasks.Where(task => owned.Contains(task.Id)).Select(task => (JsonValue)task.ToJson())))
                .SetItem("entries", JsonValue.Array(entries.Values.OrderBy(entry => entry.Id.Value).Select(entry => (JsonValue)entry.ToJson())))
                .SetItem("usage", JsonValue.Object([new("totalTokens", tokens), new("cost", cost)]));
        }, context).ConfigureAwait(false);
        var run = await RunRecordAsync(root!, context).ConfigureAwait(false);
        if (run is not null) run = await BranchRetrieval.WithFetchesAsync(run, GetHarness(), context).ConfigureAwait(false);
        return ExecutionSummary(root!, run).SetItem("tasks", detail["tasks"]).SetItem("entries", detail["entries"]).SetItem("usage", detail["usage"])
            .SetItem("ledger", host.Budgets.Usage(RunKey(taskId)));
    }

    private static bool IsJob(TaskRecord task) => task.Kind.StartsWith("pisharp.job.", StringComparison.Ordinal);
    private static bool IsExecution(TaskRecord task) => task.Kind == "pi.generation" || IsJob(task);

    private async Task<JsonObjectValue?> RunRecordAsync(TaskRecord task, DurableContext context)
    {
        if (!IsJob(task)) return null;
        if (task.Input is JsonObjectValue input && Jobs.Long(input, "historyVersion") == 1)
            return (await GetHarness().SnapshotAsync(Jobs.RecordDoc, task.ConversationId,
                task.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), context).ConfigureAwait(false))?.GetValueOrDefault("run") as JsonObjectValue;
        // Legacy jobs did not retain a task-owned record; recover what remains in their conversation's card window.
        var legacy = await GetHarness().SnapshotAsync(Jobs.RunsDoc, task.ConversationId, context).ConfigureAwait(false);
        return Jobs.Items(legacy, "items").OfType<JsonObjectValue>().FirstOrDefault(run => Jobs.Long(run, "id") == task.Id.Value);
    }

    private static JsonObjectValue ExecutionSummary(TaskRecord task, JsonObjectValue? run)
    {
        var status = task.State is TerminalTaskState settled ? settled.Outcome.Status.ToString().ToLowerInvariant() : task.State.Status.ToString().ToLowerInvariant();
        // Declined jobs settle successfully at the task layer; their application outcome remains visible.
        if (run is not null && Jobs.String(run, "status") == "declined") status = "declined";
        return JsonValue.Object([new("id", task.Id.Value), new("agentId", task.ConversationId.Value), new("kind", IsJob(task) ? "job" : "chat"),
            new("title", run is not null ? Jobs.String(run, "title") ?? task.Kind : "Chat generation"), new("status", status),
            new("startedAt", task.StartedAt is { } start ? start : JsonValue.Null), new("endedAt", task.EndedAt is { } end ? end : JsonValue.Null),
            new("durationMs", task.StartedAt is { } from && task.EndedAt is { } to ? Math.Max(0, to - from) : JsonValue.Null),
            new("task", task.ToJson()), new("run", run is { } record ? record : JsonValue.Null)]);
    }

    private static double Number(JsonObjectValue value, string field) => value.TryGetValue(field, out var count) && count is JsonNumberValue number ? number.Value : 0;
}
