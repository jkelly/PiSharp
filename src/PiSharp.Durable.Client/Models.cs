// PiSharp-only (decision 0008). Reducers over a device's stream frames, after durable-pi's client models (apps/dpi-sdk/src/models.ts, MIT):
// an agent stream's snapshot, event batches, inbox and documents become a transcript; a session stream's frames become its agents, tasks and
// usage; a hub's network frames become the device tree. They hold plain state and know nothing of rendering, so the web hub and a terminal
// share them. PiSharp additions: nested tool calls (pi-durable 1.2.0 nestedTools and parentToolCallId) and compaction failures.
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

/// <summary>One agent's view state, rebuilt from its stream.</summary>
public sealed class AgentModel
{
    private readonly SortedDictionary<long, JsonObject> entries = [];
    private readonly Dictionary<string, LiveTool> tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveTool> nested = new(StringComparer.Ordinal);
    private JsonObject? live;
    private long order;

    /// <summary>The transcript: the active context's entries, the live message, then running tools.</summary>
    public IReadOnlyList<TranscriptItem> Items { get; private set; } = [];

    /// <summary>The active context's committed entries, by id.</summary>
    public IReadOnlyList<JsonObject> Entries => [.. entries.Values];

    /// <summary>Between <c>run_start</c> and <c>run_end</c>.</summary>
    public bool Running { get; private set; }

    /// <summary>While an assistant message streams.</summary>
    public bool Generating => live is not null;

    /// <summary>Queued messages.</summary>
    public IReadOnlyList<InboxItem> Inbox { get; private set; } = [];

    /// <summary><c>pi.agent</c>: model, thinking level, tools, instructions, cwd.</summary>
    public JsonObject Agent { get; private set; } = [];

    /// <summary><c>pi.usage</c>.</summary>
    public JsonObject Usage { get; private set; } = [];

    /// <summary>The retry backoff of the current generation: when it retries, and why.</summary>
    public (double At, string Error)? Retry { get; private set; }

    /// <summary>Live compactions, by task.</summary>
    public IReadOnlyList<long> Compactions { get; private set; } = [];

    /// <summary>The newest compaction failure since a compaction last succeeded.</summary>
    public string? CompactionFailure { get; private set; }

    /// <summary>Recent task failures, newest last (at most 10).</summary>
    public IReadOnlyList<string> Notices { get; private set; } = [];

    /// <summary>Documents the stream asked for, by kind.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Docs { get; private set; } = new Dictionary<string, JsonNode?>();

    /// <summary>Set by the first snapshot.</summary>
    public bool Ready { get; private set; }

    /// <summary>Running tool calls that wait for a person: <c>ask_user_question</c> forms and <c>exit_plan_mode</c> reviews, lowest task
    /// first.</summary>
    public IReadOnlyList<PendingCall> Pending => [.. tools
        .Where(pair => pair.Value.Status == "running" && pair.Value.TaskId is not null && pair.Value.Details is JsonObject details &&
            Transcript.Str(details, "state") == "pending" && Transcript.Str(details, "kind") is "pisharp.questions" or "pisharp.plan-approval")
        .Select(pair => new PendingCall(pair.Value.TaskId!.Value, pair.Key, pair.Value.Name, (JsonObject)pair.Value.Details!))
        .OrderBy(call => call.TaskId)];

    /// <summary>The tokens of the newest assistant message's request: about the size of the context now.</summary>
    public double ContextTokens
    {
        get
        {
            if (live?["usage"] is JsonObject liveUsage && Total(liveUsage) > 0) return Total(liveUsage);
            foreach (var entry in entries.Values.Reverse())
                if (Transcript.Str(entry, "kind") == "pi.assistant")
                    foreach (var message in Transcript.Messages(entry))
                        if (message["usage"] is JsonObject usage && Total(usage) > 0) return Total(usage);
            return 0;
        }
    }

    private static double Total(JsonObject usage)
    {
        var total = Number(usage, "totalTokens");
        return total > 0 ? total : Number(usage, "input") + Number(usage, "output") + Number(usage, "cacheRead") + Number(usage, "cacheWrite");
    }

    /// <summary>Applies one frame; false when the frame is not an agent frame.</summary>
    public bool Apply(JsonObject frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        switch (Transcript.Str(frame, "type"))
        {
            case "snapshot":
                if (frame["snapshot"] is JsonObject snapshot) Reset(snapshot);
                Inbox = ReadInbox(frame["inbox"]);
                break;
            case "batch":
                if (frame["events"] is JsonArray events)
                    foreach (var item in events.OfType<JsonObject>()) Event(item);
                break;
            case "inbox":
                Inbox = ReadInbox(frame["items"]);
                break;
            case "doc":
                Docs = new Dictionary<string, JsonNode?>(Docs) { [Transcript.Str(frame, "kind") ?? ""] = frame["value"]?.DeepClone() };
                break;
            default:
                return false;
        }
        Items = Transcript.Build([.. entries.Values], live, tools, nested);
        return true;
    }

    private static List<InboxItem> ReadInbox(JsonNode? node) => DurableWire.Read<List<InboxItem>>(node) ?? [];

    private void Reset(JsonObject snapshot)
    {
        entries.Clear();
        foreach (var entry in Array(snapshot, "entries")) entries[Transcript.Id(entry)] = (JsonObject)entry.DeepClone();
        live = snapshot["generation"] is JsonObject generation && generation["message"] is JsonObject message ? (JsonObject)message.DeepClone() : null;
        tools.Clear();
        foreach (var slot in Array(snapshot, "tools"))
            if (Transcript.Str(slot, "callId") is { } callId)
                tools[callId] = new LiveTool { Name = Transcript.Str(slot, "name") ?? "tool", Status = Transcript.Str(slot, "status") ?? "pending",
                    Output = Transcript.Str(slot, "output") ?? "", Order = ++order, TaskId = slot["taskId"] is null ? null : Transcript.Id(slot, "taskId"),
                    Details = slot["details"]?.DeepClone() };
        nested.Clear();
        foreach (var slot in Array(snapshot, "nestedTools"))
            if (Transcript.Str(slot, "callId") is { } callId)
                nested[callId] = new LiveTool
                {
                    Name = Transcript.Str(slot, "name") ?? "tool",
                    Status = Transcript.Str(slot, "status") ?? "pending",
                    Output = Transcript.Str(slot, "output") ?? "",
                    Args = slot["arguments"]?.DeepClone(),
                    ParentCallId = Transcript.Str(slot, "parentCallId"),
                    IsError = slot["summary"] is JsonObject summary && Transcript.Bool(summary, "isError"),
                    Order = ++order,
                };
        Running = snapshot["run"] is JsonObject;
        Agent = snapshot["agent"] as JsonObject is { } agent ? (JsonObject)agent.DeepClone() : [];
        Usage = snapshot["usage"] as JsonObject is { } usage ? (JsonObject)usage.DeepClone() : [];
        Retry = snapshot["generation"] is JsonObject attempt && attempt["retry"] is JsonObject retry
            ? (Number(retry, "at"), Transcript.Str(retry, "error") ?? "") : null;
        Compactions = [.. Array(snapshot, "compactions").Select(compaction => Transcript.Id(compaction, "taskId"))];
        CompactionFailure = snapshot["compactionFailure"] is JsonObject failure ? Transcript.Str(failure, "error") ?? "Compaction failed." : null;
        Ready = true;
    }

    private void Commit(JsonObject entry)
    {
        var id = Transcript.Id(entry);
        entries[id] = (JsonObject)entry.DeepClone();
        var kind = Transcript.Str(entry, "kind");
        if (kind is "pi.reset" or "pi.compaction")
        {
            var head = entry["head"] is not null ? Transcript.Id(entry, "head") : id;
            foreach (var older in entries.Keys.Where(key => key < head).ToList()) entries.Remove(older);
            if (kind == "pi.compaction") CompactionFailure = null;
        }
    }

    private void Event(JsonObject item)
    {
        switch (Transcript.Str(item, "type"))
        {
            case "snapshot":
                Reset(item);
                if (DurableWire.Read<List<JsonObject>>(item["inbox"]) is { } queued)
                    Inbox = [.. queued.Select(slot => new InboxItem(Transcript.Id(slot), Transcript.Str(slot, "mode") ?? "followUp",
                        Inbox.FirstOrDefault(known => known.Id == Transcript.Id(slot))?.Content))];
                return;
            case "run_start":
                Running = true;
                return;
            case "run_end":
                Running = false;
                live = null;
                return;
            case "message_start":
                if (item["message"] is JsonObject started && Transcript.Str(started, "role") == "assistant") live = (JsonObject)started.DeepClone();
                return;
            case "message_update":
                live ??= new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(), ["provider"] = "", ["model"] = "", ["stopReason"] = "pending" };
                if (item["changes"] is JsonArray changes)
                    foreach (var change in changes.OfType<JsonObject>()) live = ApplyChange(live, change);
                if (item["usage"] is JsonNode usage) live["usage"] = usage.DeepClone();
                return;
            case "message_end":
                live = null;
                if (item["entry"] is JsonObject ended) Commit(ended);
                return;
            case "entry_appended":
                if (item["entry"] is JsonObject appended) Commit(appended);
                return;
            case "tool_execution_start":
            {
                var callId = Transcript.Str(item, "toolCallId") ?? "";
                var tool = new LiveTool
                {
                    Name = Transcript.Str(item, "toolName") ?? "tool",
                    Status = "running",
                    Args = item["args"]?.DeepClone(),
                    ParentCallId = Transcript.Str(item, "parentToolCallId"),
                    Order = ++order,
                    TaskId = item["taskId"] is null ? null : Transcript.Id(item, "taskId"),
                };
                if (tool.ParentCallId is not null) nested[callId] = tool;
                else tools[callId] = tool;
                return;
            }
            case "tool_execution_update":
            {
                var callId = Transcript.Str(item, "toolCallId") ?? "";
                var isNested = Transcript.Str(item, "parentToolCallId") is not null;
                var map = isNested ? nested : tools;
                var tool = map.TryGetValue(callId, out var known) ? known
                    : new LiveTool { Name = Transcript.Str(item, "toolName") ?? "tool", ParentCallId = Transcript.Str(item, "parentToolCallId"), Order = ++order };
                var output = tool.Output;
                if (item["output"] is JsonObject change)
                {
                    if (Transcript.Str(change, "set") is { } set) output = set;
                    else
                    {
                        var trim = (int)Math.Min(Transcript.Id(change, "trimStart"), output.Length);
                        output = output[trim..] + (Transcript.Str(change, "append") ?? "");
                    }
                }
                map[callId] = tool with
                {
                    Output = output,
                    Status = "running",
                    Details = item.ContainsKey("details") ? item["details"]?.DeepClone() : tool.Details,
                    TaskId = tool.TaskId ?? (item["taskId"] is null ? null : Transcript.Id(item, "taskId")),
                };
                return;
            }
            case "tool_execution_end":
            {
                var callId = Transcript.Str(item, "toolCallId") ?? "";
                if (Transcript.Str(item, "parentToolCallId") is not null)
                {
                    var tool = nested.TryGetValue(callId, out var known) ? known
                        : new LiveTool { Name = Transcript.Str(item, "toolName") ?? "tool", ParentCallId = Transcript.Str(item, "parentToolCallId"), Order = ++order };
                    var result = item["result"] as JsonObject;
                    nested[callId] = tool with
                    {
                        Status = "done",
                        IsError = result is not null && Transcript.Bool(result, "isError"),
                        Result = result?["structuredOutput"] is JsonNode structured ? Transcript.Describe(structured) : tool.Output,
                    };
                    return;
                }
                tools.Remove(callId);
                if (item["entry"] is JsonObject entry) entries[Transcript.Id(entry)] = (JsonObject)entry.DeepClone();
                return;
            }
            case "agent_changed":
                if (item["agent"] is JsonObject agent) Agent = (JsonObject)agent.DeepClone();
                return;
            case "usage_changed":
                if (item["usage"] is JsonObject usageState) Usage = (JsonObject)usageState.DeepClone();
                return;
            case "auto_retry_start":
                Retry = (Number(item, "at"), Transcript.Str(item, "errorMessage") ?? "");
                return;
            case "auto_retry_end":
                Retry = null;
                return;
            case "compaction_start":
            {
                var task = Transcript.Id(item, "taskId");
                if (!Compactions.Contains(task)) Compactions = [.. Compactions, task];
                return;
            }
            case "compaction_end":
            {
                var task = Transcript.Id(item, "taskId");
                Compactions = [.. Compactions.Where(id => id != task)];
                if (Transcript.Str(item, "error") is { } error) CompactionFailure = error;
                return;
            }
            case "task_failed":
                Notices = [.. Notices.TakeLast(9), $"{Transcript.Str(item, "kind")} failed: {Transcript.Str(item, "message")}"];
                return;
        }
    }

    /// <summary>Applies one <c>message_update</c> change to the live message (durable-pi <c>applyMessageChange</c>).</summary>
    public static JsonObject ApplyChange(JsonObject message, JsonObject change)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(change);
        var type = Transcript.Str(change, "type") ?? "";
        if (type == "message") return change["message"] is JsonObject whole ? (JsonObject)whole.DeepClone() : message;
        var content = message["content"] as JsonArray ?? [];
        if (message["content"] is null) message["content"] = content;
        var index = (int)Transcript.Id(change, "contentIndex");
        while (content.Count <= index) content.Add(null);
        if (type == "block" || type.EndsWith("_start", StringComparison.Ordinal))
            content[index] = change["block"]?.DeepClone();
        else if (type == "text_delta")
            content[index] = new JsonObject { ["type"] = "text", ["text"] = TextOf(content[index], "text", "text") + Transcript.Str(change, "delta") };
        else if (type == "thinking_delta")
            content[index] = new JsonObject { ["type"] = "thinking", ["thinking"] = TextOf(content[index], "thinking", "thinking") + Transcript.Str(change, "delta") };
        else if (type == "toolcall_delta")
        {
            var block = content[index] as JsonObject ?? new JsonObject { ["type"] = "toolCall", ["id"] = "", ["name"] = "", ["arguments"] = new JsonObject() };
            content[index] = null;
            if (change["path"] is JsonArray path) SetPath(block, path, Transcript.Str(change, "delta") ?? "");
            content[index] = block;
        }
        return message;
    }

    private static string TextOf(JsonNode? block, string type, string key) =>
        block is JsonObject value && Transcript.Str(value, "type") == type ? Transcript.Str(value, key) ?? "" : "";

    private static void SetPath(JsonObject target, JsonArray path, string delta)
    {
        JsonNode node = target;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var nextIsIndex = path[i + 1] is JsonValue next && next.TryGetValue<double>(out _);
            var child = Child(node, path[i]!);
            if (child is not (JsonObject or JsonArray))
            {
                child = nextIsIndex ? new JsonArray() : new JsonObject();
                SetChild(node, path[i]!, child);
            }
            node = child;
        }
        var last = path[^1]!;
        SetChild(node, last, Transcript.Describe(Child(node, last)) + delta);
    }

    private static JsonNode? Child(JsonNode node, JsonNode key) => node switch
    {
        JsonArray array when key is JsonValue value && value.TryGetValue<double>(out var index) => (int)index < array.Count ? array[(int)index] : null,
        JsonObject json => json[Transcript.Describe(key)],
        _ => null,
    };

    private static void SetChild(JsonNode node, JsonNode key, JsonNode? value)
    {
        if (node is JsonArray array && key is JsonValue number && number.TryGetValue<double>(out var at))
        {
            while (array.Count <= (int)at) array.Add(null);
            array[(int)at] = value;
        }
        else if (node is JsonObject json) json[Transcript.Describe(key)] = value;
    }

    private static IEnumerable<JsonObject> Array(JsonObject json, string key) => json[key] is JsonArray array ? array.OfType<JsonObject>() : [];

    private static double Number(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;
}

/// <summary>A tool call that waits for a person.</summary>
/// <param name="TaskId">Its tool task, which the answer names.</param>
/// <param name="CallId">The model's call id.</param>
/// <param name="Tool"><c>ask_user_question</c> or <c>exit_plan_mode</c>.</param>
/// <param name="Details">The questions, or the plan with its file.</param>
public sealed record PendingCall(long TaskId, string CallId, string Tool, JsonObject Details)
{
    /// <summary>Whether it is a plan review.</summary>
    public bool IsPlan => Transcript.Str(Details, "kind") == "pisharp.plan-approval";
}

/// <summary>One session's agents, live tasks and usage, from its stream.</summary>
public sealed class SessionModel
{
    /// <summary>The agents.</summary>
    public IReadOnlyList<AgentSummary> Agents { get; private set; } = [];

    /// <summary>The live tasks.</summary>
    public IReadOnlyList<TaskNode> Tasks { get; private set; } = [];

    /// <summary>The usage.</summary>
    public UsageTotals Usage { get; private set; } = new();

    /// <summary>Set by the first <c>session</c> frame.</summary>
    public bool Ready { get; private set; }

    /// <summary>Applies one frame; false when it is not a session frame.</summary>
    public bool Apply(JsonObject frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        switch (Transcript.Str(frame, "type"))
        {
            case "session":
                Agents = DurableWire.Read<List<AgentSummary>>(frame["agents"]) ?? [];
                Tasks = DurableWire.Read<List<TaskNode>>(frame["tasks"]) ?? [];
                Usage = DurableWire.Read<UsageTotals>(frame["usage"]) ?? new();
                Ready = true;
                return true;
            case "agents":
                Agents = DurableWire.Read<List<AgentSummary>>(frame["agents"]) ?? [];
                return true;
            case "tasks":
                Tasks = DurableWire.Read<List<TaskNode>>(frame["tasks"]) ?? [];
                return true;
            case "usage":
                Usage = DurableWire.Read<UsageTotals>(frame["usage"]) ?? new();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Main → … → the agent, following fork parents.</summary>
    public IReadOnlyList<AgentSummary> Lineage(long agentId) => NetworkTree.Lineage(Agents, agentId);
}

/// <summary>A node of the device → session → agent tree.</summary>
public sealed record TreeNode(string Type, string Key, string DeviceId, NetworkDevice? Device, SessionSummary? Session, AgentSummary? Agent,
    bool Stale, IReadOnlyList<TreeNode> Children);

/// <summary>The network tree and lineage helpers.</summary>
public static class NetworkTree
{
    /// <summary>A session's key: <c>device/s/session</c>.</summary>
    public static string SessionKey(string deviceId, string sessionId) => $"{deviceId}/s/{sessionId}";

    /// <summary>An agent's key: <c>device/s/session/a/agent</c>.</summary>
    public static string AgentKey(string deviceId, string sessionId, long agentId) => $"{deviceId}/s/{sessionId}/a/{agentId}";

    /// <summary>Devices with their sessions, and agents nested under the agent they forked from or whose task owns them.</summary>
    public static IReadOnlyList<TreeNode> Build(IReadOnlyList<NetworkDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return [.. devices.Select(device =>
        {
            var stale = device.Status != "online";
            return new TreeNode("device", device.Id, device.Id, device, null, null, stale,
                [.. (device.Tree?.Sessions ?? []).Select(session => new TreeNode("session", SessionKey(device.Id, session.Id), device.Id, device, session, null,
                    stale, Agents(device, session, stale)))]);
        })];
    }

    private static IReadOnlyList<TreeNode> Agents(NetworkDevice device, SessionSummary session, bool stale)
    {
        var children = session.Agents.ToDictionary(agent => agent.Id, _ => new List<TreeNode>());
        var nodes = new Dictionary<long, TreeNode>();
        TreeNode Node(AgentSummary agent) => nodes.TryGetValue(agent.Id, out var known) ? known
            : nodes[agent.Id] = new TreeNode("agent", AgentKey(device.Id, session.Id, agent.Id), device.Id, device, session, agent, stale, children[agent.Id]);
        var roots = new List<TreeNode>();
        foreach (var agent in session.Agents)
        {
            var parent = agent.ParentId ?? agent.OwnerAgentId;
            if (parent is { } id && id != agent.Id && children.TryGetValue(id, out var siblings)) siblings.Add(Node(agent));
            else roots.Add(Node(agent));
        }
        return roots;
    }

    /// <summary>Main → … → <paramref name="agentId"/>, following fork parents.</summary>
    public static IReadOnlyList<AgentSummary> Lineage(IReadOnlyList<AgentSummary> agents, long agentId)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var byId = agents.ToDictionary(agent => agent.Id);
        var line = new List<AgentSummary>();
        var seen = new HashSet<long>();
        long? current = agentId;
        while (current is { } id && seen.Add(id) && byId.TryGetValue(id, out var agent))
        {
            line.Insert(0, agent);
            current = agent.ParentId;
        }
        return line;
    }
}
