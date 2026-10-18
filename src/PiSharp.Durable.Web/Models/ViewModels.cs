using System.Text.Json.Nodes;
using PiSharp.Durable.Client;
using PiSharp.Durable.Web.Hub;

namespace PiSharp.Durable.Web.Models;

/// <summary>The selection a page opens on: a device, a session and an agent, any of them absent.</summary>
public sealed record Selection(string? DeviceId, string? SessionId, long? AgentId)
{
    public static Selection None { get; } = new(null, null, null);

    /// <summary>The tree key of the selected row.</summary>
    public string? Key => DeviceId is null ? null : SessionId is null ? DeviceId : AgentId is null
        ? NetworkTree.SessionKey(DeviceId, SessionId) : NetworkTree.AgentKey(DeviceId, SessionId, AgentId.Value);
}

/// <summary>The network page.</summary>
public sealed record NetworkPage(IReadOnlyList<NetworkDevice> Devices, Selection Selection);

/// <summary>The devices page.</summary>
public sealed record DevicesPage(IReadOnlyList<NetworkDevice> Devices);

/// <summary>What rendering an agent's items needs besides the items.</summary>
public sealed class AgentRenderContext
{
    public required string DeviceId { get; init; }
    public required string SessionId { get; init; }
    public required long AgentId { get; init; }
    public required MarkdownRenderer Markdown { get; init; }
    /// <summary>The agent's <c>pisharp.job-runs</c> document.</summary>
    public JsonObject? JobRuns { get; init; }
    /// <summary>The device's job definitions, by id.</summary>
    public IReadOnlyDictionary<string, JsonObject> Jobs { get; init; } = new Dictionary<string, JsonObject>();
    /// <summary>The session's agents, by id (subagent links).</summary>
    public IReadOnlyDictionary<long, AgentSummary> Agents { get; init; } = new Dictionary<long, AgentSummary>();

    /// <summary>The run record of a job task.</summary>
    public JsonObject? Run(long taskId) => (JobRuns?["items"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(run => Display.Long(run, "id") == taskId);

    /// <summary>A deep link to an agent of this session.</summary>
    public string AgentHref(long agentId) => $"/d/{Uri.EscapeDataString(DeviceId)}/s/{Uri.EscapeDataString(SessionId)}/a/{agentId}";
}

/// <summary>One transcript item to render.</summary>
public sealed record TranscriptItemModel(TranscriptItem Item, AgentRenderContext Context);

/// <summary>A tool call to render, nested or not.</summary>
public sealed record ToolCallModel(ToolCallView Call, AgentRenderContext Context, bool Nested);

/// <summary>A job run card.</summary>
public sealed record JobCardModel(JsonObject Run, JsonObject? Definition, AgentRenderContext Context);

/// <summary>The "Waiting for you" panel: tool calls and job runs that wait for a person.</summary>
public sealed record PendingModel(IReadOnlyList<PendingCall> Calls, IReadOnlyList<JsonObject> Approvals, string AgentName, string DeviceName);

/// <summary>The live task tree of an agent and the subagents it owns.</summary>
public sealed record TasksModel(IReadOnlyList<(TaskNode Task, int Depth)> Rows, long AgentId, IReadOnlyDictionary<long, AgentSummary> Agents);

/// <summary>The Session tab.</summary>
public sealed record SessionTabModel(NetworkDevice Device, SessionSummary Session, AgentDetail Agent, ModelInfo? Model, CompactionSettings Compaction);

/// <summary>The Tools tab.</summary>
public sealed record ToolsTabModel(AgentDetail Agent);

/// <summary>The Extend tab.</summary>
public sealed record ExtendTabModel(AgentExtensions Extensions);

/// <summary>The job definitions of the Tasks tab, with each one's newest run.</summary>
public sealed record JobsTabModel(NetworkDevice Device, IReadOnlyList<JsonObject> Definitions, IReadOnlyList<JsonObject> Runs);

/// <summary>The offline, checking or rejected panel of a device.</summary>
public sealed record OfflineModel(NetworkDevice Device);
