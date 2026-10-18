// PiSharp-only (decision 0008). The device API's logic over PiSharp.Durable.Hosting, after durable-pi's Store (apps/dpi-backend/src/store.ts,
// MIT): host records become the v1 wire bodies, tasks get labels (a pi.tool task its tool, a job run its title and number), queued
// messages their text, and an agent its detail (configuration, tools with their replay policy and effect, extensions).
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Tools;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using NodeJson = System.Text.Json.Nodes.JsonValue;
using DeviceInfo = PiSharp.Durable.Client.DeviceInfo;
using SendResult = PiSharp.Durable.Hosting.SendResult;

namespace PiSharp.Durable.Server;

internal sealed class DeviceService(DurableDeviceHost host, DurableDeviceServerOptions options)
{
    /// <summary>Device documents a client may read or stream.</summary>
    public static readonly IReadOnlyList<string> DeviceDocKinds = ["app.shared-state"];

    /// <summary>Agent documents a client may read or stream.</summary>
    public static readonly IReadOnlyList<string> AgentDocKinds = ["pisharp.job-runs", "pisharp.system-prompts"];

    /// <summary>What a PiSharp device can do beyond durable-pi's core.</summary>
    public static readonly IReadOnlyList<string> Capabilities = ["events", "docs", "demo", "jobs", "state", "interactions", "extensions", "plan-mode", "transcript"];

    private readonly ConcurrentDictionary<(string Session, long Task), (string? Label, string? CallId)> labels = new();
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;

    public DurableDeviceHost Host => host;

    public DurableDeviceServerOptions Options => options;

    public static DurableContext Context(CancellationToken cancellationToken) => DurableContext.Background.WithCancellation(cancellationToken);

    // ─── Device ─────────────────────────────────────────────────────────────

    public async Task<DeviceInfo> InfoAsync(CancellationToken cancellationToken)
    {
        var info = host.Info;
        var models = await host.ModelsAsync(cancellationToken).ConfigureAwait(false);
        var catalog = Path.Join(info.DataDirectory, "catalog.sqlite");
        return new DeviceInfo
        {
            Uid = info.Uid,
            Name = info.Name,
            Hostname = info.HostName,
            Platform = info.Platform,
            ApiVersion = DurableWire.ApiVersion,
            DurableVersion = DurableWire.DurableVersion,
            StartedAt = info.StartedAt.ToUnixTimeMilliseconds(),
            StartDirs = info.StartDirectories,
            Models = [.. models.Select(Model)],
            DefaultModel = info.DefaultModel?.ModelId ?? models.FirstOrDefault()?.Id ?? "",
            DefaultProvider = info.DefaultModel?.Provider ?? models.FirstOrDefault()?.Provider,
            Storage = new DeviceStorage(info.DataDirectory, catalog, catalog),
            CodeDir = AppContext.BaseDirectory,
            Capabilities = Capabilities,
            Jobs = new JsonArray([.. host.Jobs.Select(job => ToNode(job.ToJson()))]),
        };
    }

    private static ModelInfo Model(DeviceModel model) => new()
    {
        Id = model.Id,
        Provider = model.Provider,
        Name = model.Name,
        Tier = "",
        Icon = model.Provider,
        ContextWindow = model.ContextWindow,
        Reasoning = model.Reasoning,
        Cost = new ModelCost(model.Cost?.Input ?? 0, model.Cost?.Output ?? 0),
    };

    public async Task<DeviceTree> TreeAsync(CancellationToken cancellationToken) => new()
    {
        Uid = host.Info.Uid,
        Name = host.Info.Name,
        Sessions = [.. (await host.SessionSummariesAsync(cancellationToken).ConfigureAwait(false)).Select(Summary)],
        At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    public long StartedAt => started.ToUnixTimeMilliseconds();

    public static SessionSummary Summary(HostedSessionSummary summary) => new()
    {
        Id = summary.Id,
        Name = summary.Name,
        Cwd = summary.Cwd,
        Model = summary.Model?.ModelId ?? "",
        Provider = summary.Model?.Provider,
        CreatedAt = summary.CreatedAt.ToUnixTimeMilliseconds(),
        DbPath = summary.DatabasePath,
        Busy = summary.Busy,
        Error = summary.Error,
        Agents = [.. summary.Agents.Select(Agent)],
        Compaction = Compaction(summary.Compaction),
    };

    public static AgentSummary Agent(HostedAgent agent) => new()
    {
        Id = agent.Id,
        Name = agent.Name,
        Named = agent.Named,
        Kind = Kind(agent.Kind),
        ParentId = agent.ParentId,
        ForkedAt = agent.ForkedAt,
        OwnerAgentId = agent.OwnerAgentId,
        OwnerTaskId = agent.OwnerTaskId,
        Busy = agent.Busy,
        Background = agent.Background,
    };

    private static string Kind(AgentKind kind) => kind switch
    {
        AgentKind.Fork => "fork",
        AgentKind.Subagent => "subagent",
        _ => "root",
    };

    public static Client.CompactionSettings Compaction(SessionCompaction compaction) =>
        new(compaction.Enabled, compaction.ReserveTokens, compaction.BackgroundTokens, compaction.KeepRecentTokens);

    public DirListing Browse(string? path)
    {
        var listing = host.BrowseDirectories(path);
        return new DirListing(listing.Path, listing.Parent, [.. listing.Directories.Select(entry => new DirEntry(entry.Name, entry.Path))]);
    }

    public JsonObject AppState()
    {
        var state = host.AppState.Get();
        return new JsonObject { ["kind"] = "app.shared-state", ["value"] = ToNode(state.Value), ["revision"] = state.Revision };
    }

    // ─── Sessions ───────────────────────────────────────────────────────────

    public async Task<SessionSummary> SessionAsync(HostedSession session, CancellationToken cancellationToken) =>
        Summary(await session.SummaryAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Live tasks with their labels.</summary>
    public async Task<IReadOnlyList<TaskNode>> LiveTasksAsync(HostedSession session, TaskGraph? graph, CancellationToken cancellationToken)
    {
        if (graph is null) graph = await session.TaskGraphAsync(cancellationToken).ConfigureAwait(false);
        var nodes = new List<TaskNode>();
        foreach (var node in graph.Tasks.Values)
        {
            var json = (JsonObject)ToNode(node.ToJson())!;
            var state = json["state"] as JsonObject ?? [];
            var (label, callId) = await LabelAsync(session, node.Id, node.Kind, node.ConversationId.Value, cancellationToken).ConfigureAwait(false);
            nodes.Add(new TaskNode
            {
                Id = node.Id.Value,
                Kind = node.Kind,
                ConversationId = node.ConversationId.Value,
                Owner = node.Owner?.Value,
                Background = node.Background,
                Aborting = node.AbortRequested ? true : null,
                State = new TaskNodeState
                {
                    Status = Transcript.Describe(state["status"]),
                    Phase = state["phase"] is JsonNode phase ? Transcript.Describe(phase) : null,
                    On = state["on"] is JsonArray on ? [.. on.Select(item => (long)item!.GetValue<double>())] : null,
                    Policy = state["policy"] is JsonNode policy ? Transcript.Describe(policy) : null,
                },
                Conversations = [.. node.Conversations.Select(id => id.Value)],
                Label = label,
                CallId = callId,
            });
        }
        return nodes;
    }

    private async Task<(string? Label, string? CallId)> LabelAsync(HostedSession session, TaskId id, string kind, long conversation, CancellationToken cancellationToken)
    {
        if (labels.TryGetValue((session.Id, id.Value), out var known)) return known;
        (string? Label, string? CallId) found = (null, null);
        try
        {
            var harness = session.GetHarness();
            var context = Context(cancellationToken);
            if (kind.StartsWith("pisharp.job.", StringComparison.Ordinal))
            {
                var job = host.Jobs.FirstOrDefault(definition => "pisharp.job." + definition.Id == kind);
                var runs = await harness.SnapshotAsync(Jobs.RunsDoc, new ConversationId(conversation), context).ConfigureAwait(false);
                var run = Jobs.Items(runs, "items").OfType<JsonObjectValue>().FirstOrDefault(item => Jobs.Long(item, "id") == id.Value);
                found = (job is null ? null : run is null ? job.Title : $"{job.Title} #{Jobs.Long(run, "run")}", null);
            }
            else if (kind == "pi.tool" && await harness.GetTaskAsync(id, context).ConfigureAwait(false) is { Input: JsonObjectValue input })
            {
                if (input.TryGetValue("kind", out var nested) && nested is JsonStringValue { Value: "nested" })
                {
                    var call = input.TryGetValue("call", out var value) ? value as JsonObjectValue : null;
                    found = (call is not null && call.TryGetValue("name", out var name) && name is JsonStringValue text ? text.Value : null,
                        input.TryGetValue("parentCallId", out var parent) && parent is JsonStringValue parentId && input.TryGetValue("key", out var key) &&
                        key is JsonStringValue keyText ? parentId.Value + "/" + keyText.Value : null);
                }
                else if (input.TryGetValue("assistant", out var assistant) && assistant is JsonNumberValue entryId &&
                    input.TryGetValue("callId", out var callValue) && callValue is JsonStringValue callId)
                {
                    var entry = await harness.CommitAsync(tx => tx.GetEntryAsync(new EntryId((long)entryId.Value)), context).ConfigureAwait(false);
                    var name = entry?.Model?.OfType<JsonObjectValue>().SelectMany(message => message.TryGetValue("content", out var content) && content is JsonArrayValue blocks
                        ? blocks.OfType<JsonObjectValue>() : []).FirstOrDefault(block => block.TryGetValue("id", out var blockId) && blockId is JsonStringValue blockText &&
                        blockText.Value == callId.Value) is { } toolCall && toolCall.TryGetValue("name", out var toolName) && toolName is JsonStringValue toolText ? toolText.Value : null;
                    found = (name, callId.Value);
                }
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return (null, null);
        }
        if (found.Label is not null || found.CallId is not null)
        {
            if (labels.Count > 5000) labels.Clear();
            labels[(session.Id, id.Value)] = found;
        }
        return found;
    }

    /// <summary>Recent tasks of a status: <c>live</c> or <c>terminal</c>, newest first (durable-pi <c>TaskOutcome</c>).</summary>
    public async Task<JsonArray> TasksAsync(HostedSession session, string status, int limit, CancellationToken cancellationToken)
    {
        var harness = session.GetHarness();
        var context = Context(cancellationToken);
        var all = new List<TaskRecord>();
        JsonObjectValue? cursor = null;
        do
        {
            var from = cursor;
            var page = await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery(), 500, from), context).ConfigureAwait(false);
            all.AddRange(page.Items);
            if (all.Count > 2000) all.RemoveRange(0, all.Count - 2000);
            cursor = page.Next;
        } while (cursor is not null);
        var wanted = all.Where(task => (task.State.Status == DurableTaskStatus.Terminal) == (status == "terminal")).ToList();
        var items = new JsonArray();
        foreach (var task in Enumerable.Reverse(wanted).Take(Math.Clamp(limit, 1, 200)))
            items.Add(new JsonObject
            {
                ["id"] = task.Id.Value,
                ["kind"] = task.Kind,
                ["conversationId"] = task.ConversationId.Value,
                ["state"] = ToNode(task.State.ToJson()),
            });
        return items;
    }

    public static async Task<JsonObject> InspectAsync(HostedSession session, CancellationToken cancellationToken)
    {
        var inspection = await session.GetHarness().InspectAsync(Context(cancellationToken)).ConfigureAwait(false);
        return new JsonObject
        {
            ["scheduling"] = Camel(inspection.Scheduling.ToString()),
            ["tasks"] = new JsonArray([.. inspection.Tasks.Select(task => (JsonNode)new JsonObject
            {
                ["record"] = ToNode(task.Record.ToJson()),
                ["state"] = new JsonObject
                {
                    ["kind"] = Camel(task.State.Kind.ToString()),
                    ["on"] = new JsonArray([.. task.State.On.Select(id => (JsonNode)NodeJson.Create(id.Value))]),
                    ["reason"] = task.State.Reason is { } reason ? TaskInspectionState.ReasonName(reason) : null,
                },
            })]),
            ["submissions"] = new JsonArray([.. inspection.Submissions.Select(record => ToNode(record.ToJson()))]),
        };
    }

    public static async Task<JsonNode?> UsageAsync(HostedSession session, CancellationToken cancellationToken) =>
        ToNode(await session.GetHarness().UsageAsync(Context(cancellationToken)).ConfigureAwait(false));

    // ─── Agents ─────────────────────────────────────────────────────────────

    public static async Task<IConversation> ConversationAsync(HostedSession session, long agentId, CancellationToken cancellationToken) =>
        await session.GetHarness().ConversationAsync(new ConversationId(agentId), Context(cancellationToken)).ConfigureAwait(false)
            ?? throw new HostNotFoundException($"No agent {agentId} in {session.Name}.");

    public async Task<AgentDetail> DetailAsync(HostedSession session, HostedAgent agent, CancellationToken cancellationToken)
    {
        var configuration = await session.ConfigurationAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        var tools = await session.ToolsAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        var conversation = await ConversationAsync(session, agent.Id, cancellationToken).ConfigureAwait(false);
        var state = await conversation.AgentAsync(Context(cancellationToken)).ConfigureAwait(false);
        var summary = Agent(agent);
        return new AgentDetail
        {
            Id = summary.Id,
            Name = summary.Name,
            Named = summary.Named,
            Kind = summary.Kind,
            ParentId = summary.ParentId,
            ForkedAt = summary.ForkedAt,
            OwnerAgentId = summary.OwnerAgentId,
            OwnerTaskId = summary.OwnerTaskId,
            Busy = summary.Busy,
            Background = summary.Background,
            SessionId = session.Id,
            Config = Config(configuration),
            Tools = [.. tools.Select(tool => new ToolInfo
            {
                Name = tool.Name,
                Description = tool.Description,
                Replay = tool.Replay == ToolReplay.Safe ? "safe" : "unsafe",
                Parameters = ToNode(tool.Parameters),
                Writes = tool.Effect != ToolEffect.Read,
                Offered = tool.Offered,
                Effect = tool.Effect.ToString().ToLowerInvariant(),
                Deferred = tool.Deferred ? true : null,
            })],
            Extensions = [.. state.Extensions.Select(extension => extension.Name)],
        };
    }

    public static AgentConfig Config(AgentConfiguration configuration) => new()
    {
        Model = configuration.Model is { } model ? new WireModelRef(model.Provider, model.ModelId) : null,
        ThinkingLevel = configuration.ThinkingLevel,
        Instructions = configuration.Instructions,
        Cwd = configuration.Cwd,
        ReadOnly = configuration.ReadOnly,
    };

    public static Client.AgentExtensions Extensions(Hosting.AgentExtensions extensions) => new(
        [.. extensions.Optional.Select(extension => new ExtensionInfo
        {
            Name = extension.Name,
            Title = extension.Title,
            Description = extension.Description,
            Enabled = extension.Enabled,
            Prompts = extension.Name == SystemPrompts.ExtensionName
                ? [.. extension.Prompts.Select(prompt => new PromptInfo(prompt.Id, PromptTitle(prompt.Id), prompt.Description, prompt.Enabled, prompt.Text))]
                : null,
        })],
        extensions.BuiltIn);

    private static string PromptTitle(string id)
    {
        var name = Path.GetFileNameWithoutExtension(id).Replace('-', ' ').Replace('_', ' ');
        return name.Length == 0 ? id : char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
    }

    /// <summary>Queued messages with their text (durable-pi <c>inbox</c>).</summary>
    public static async Task<List<InboxItem>> InboxAsync(HostedSession session, long agentId, CancellationToken cancellationToken)
    {
        var doc = await session.GetHarness().SnapshotAsync(Inbox.InboxDoc, new ConversationId(agentId), Context(cancellationToken)).ConfigureAwait(false);
        var items = new List<InboxItem>();
        if (doc is null || !doc.TryGetValue("items", out var value) || value is not JsonArrayValue array) return items;
        foreach (var item in array.OfType<JsonObjectValue>())
        {
            var id = item.TryGetValue("id", out var number) && number is JsonNumberValue n ? (long)n.Value : 0;
            var mode = item.TryGetValue("mode", out var modeValue) && modeValue is JsonStringValue modeText ? modeText.Value : "followUp";
            if (mode == "write")
            {
                items.Add(new InboxItem(id, "write", "Context update"));
                continue;
            }
            var text = item.TryGetValue("content", out var content) ? content switch
            {
                JsonStringValue s => s.Value,
                JsonArrayValue blocks => string.Concat(blocks.OfType<JsonObjectValue>().Select(block =>
                    block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "text" } && block.TryGetValue("text", out var t) && t is JsonStringValue part ? part.Value : "")),
                _ => null,
            } : null;
            items.Add(new InboxItem(id, mode, text));
        }
        return items;
    }

    public static async Task<JsonNode?> AgentDocAsync(HostedSession session, long agentId, string kind, CancellationToken cancellationToken)
    {
        var harness = session.GetHarness();
        var context = Context(cancellationToken);
        var conversation = new ConversationId(agentId);
        return kind switch
        {
            "pisharp.job-runs" => ToNode(await harness.SnapshotAsync(Jobs.RunsDoc, conversation, context).ConfigureAwait(false)
                ?? ChordJson.Object([new("items", JsonArrayValue.Empty), new("counters", JsonObjectValue.Empty)])),
            "pisharp.system-prompts" => ToNode(await harness.SnapshotAsync(SystemPrompts.ChecklistDoc, conversation, context).ConfigureAwait(false) ?? JsonObjectValue.Empty),
            _ => throw new HostNotFoundException($"Unknown agent document {kind}. Known: {string.Join(", ", AgentDocKinds)}"),
        };
    }

    /// <summary>The active context's entries (durable-pi <c>scope=active</c>).</summary>
    public static async Task<List<JsonObject>> ActiveEntriesAsync(HostedSession session, long agentId, CancellationToken cancellationToken)
    {
        var conversation = await ConversationAsync(session, agentId, cancellationToken).ConfigureAwait(false);
        using var view = await conversation.ViewStateAsync(Context(cancellationToken)).ConfigureAwait(false);
        return [.. view.Value.Entries.Select(entry => (JsonObject)ToNode(entry.ToJson())!)];
    }

    /// <summary>One page of all entries, oldest first, with an opaque cursor.</summary>
    public static async Task<JsonObject> EntriesAsync(HostedSession session, long agentId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var conversation = await ConversationAsync(session, agentId, cancellationToken).ConfigureAwait(false);
        JsonObjectValue? from = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                var padded = cursor.Replace('-', '+').Replace('_', '/');
                padded += new string('=', (4 - padded.Length % 4) % 4);
                from = ChordJson.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded))) as JsonObjectValue;
            }
            catch (Exception failure) when (failure is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
            {
                throw new HostInvalidException("Invalid cursor.");
            }
        }
        var page = await conversation.EntriesAsync(new EntryQueryBounds { Order = ScanOrder.Ascending }, Math.Clamp(limit, 1, 2000), from,
            Context(cancellationToken)).ConfigureAwait(false);
        var json = new JsonObject { ["items"] = new JsonArray([.. page.Items.Select(entry => ToNode(entry.ToJson()))]) };
        if (page.Next is { } next)
            json["next"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(next.ToJson())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return json;
    }

    public static JsonArray Interactions(IReadOnlyList<PendingInteraction> pending) =>
        new([.. pending.Select(item => (JsonNode)new JsonObject
        {
            ["taskId"] = item.TaskId.Value,
            ["callId"] = item.CallId,
            ["kind"] = item.Kind == PlanMode.DetailsKind ? "plan" : "questions",
            ["details"] = ToNode(item.Details),
        })]);

    public static JsonObject Result(SendResult result)
    {
        var json = new JsonObject
        {
            ["submissionId"] = result.SubmissionId.Value,
            ["status"] = result.Status switch
            {
                SendStatus.Done => "done",
                SendStatus.Unanswered => "unanswered",
                SendStatus.Queued => "queued",
                _ => "pending",
            },
        };
        if (result.Status == SendStatus.Done && result.AnswerEntryId is { } entry)
            json["answer"] = new JsonObject { ["entryId"] = entry.Value, ["text"] = result.AnswerText ?? "" };
        if (result.Status == SendStatus.Unanswered) json["reason"] = result.Reason ?? "";
        return json;
    }

    // ─── JSON ───────────────────────────────────────────────────────────────

    public static JsonNode? ToNode(ChordJson? value) => value is null ? null : JsonNode.Parse(value.ToJson());

    public static ChordJson ToChord(JsonNode? node) => node is null ? ChordJson.Null : ChordJson.Parse(node.ToJsonString());

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
