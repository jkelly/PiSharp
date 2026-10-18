// PiSharp-only (decision 0008): the host's public records. Their shapes follow durable-pi's v1 DTOs (apps/dpi-sdk/src/api.ts: DeviceInfo,
// SessionSummary, AgentSummary, AgentConfig, SendResult) so the device server can map them one to one.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Hosting;

/// <summary>What the device is (durable-pi <c>DeviceInfo</c>, without the models, which <see cref="DurableDeviceHost.ModelsAsync"/>
/// lists).</summary>
/// <param name="Uid">A UUID made when the catalog was created; a different one at the same address means a different device.</param>
/// <param name="Name">The device's name.</param>
/// <param name="HostName">The machine name.</param>
/// <param name="Platform">Node's <c>process.platform</c> spelling: <c>win32</c>, <c>darwin</c> or <c>linux</c>.</param>
/// <param name="StartDirectories">Where new sessions start; not a sandbox.</param>
/// <param name="DataDirectory">Where the catalog and the sessions live.</param>
/// <param name="StartedAt">When the host opened.</param>
/// <param name="DefaultModel">The model of a session created without one, and of re-routed conversations.</param>
public sealed record DeviceInfo(string Uid, string Name, string HostName, string Platform, IReadOnlyList<string> StartDirectories,
    string DataDirectory, DateTimeOffset StartedAt, ModelRef? DefaultModel);

/// <summary>A chat model the device can run (durable-pi <c>ModelInfo</c>). Costs are USD per million tokens.</summary>
public sealed record DeviceModel(string Provider, string Id, string Name, double ContextWindow, bool Reasoning, DeviceModelCost? Cost)
{
    /// <summary>The model as a <see cref="ModelRef"/>.</summary>
    public ModelRef Ref => new(Provider, Id);

    internal static DeviceModel? FromJson(JsonObjectValue json)
    {
        if (String(json, "provider") is not { } provider || String(json, "id") is not { } id) return null;
        var cost = json.TryGetValue("cost", out var value) && value is JsonObjectValue costs
            ? new DeviceModelCost(Number(costs, "input"), Number(costs, "output"), Number(costs, "cacheRead"), Number(costs, "cacheWrite"))
            : null;
        return new DeviceModel(provider, id, String(json, "name") ?? id, Number(json, "contextWindow"),
            json.TryGetValue("reasoning", out var reasoning) && reasoning is JsonBooleanValue { Value: true }, cost);
    }

    private static string? String(JsonObjectValue json, string key) => json.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : null;

    private static double Number(JsonObjectValue json, string key) => json.TryGetValue(key, out var value) && value is JsonNumberValue number ? number.Value : 0;
}

/// <summary>Model prices in USD per million tokens.</summary>
public sealed record DeviceModelCost(double Input, double Output, double CacheRead, double CacheWrite);

/// <summary>What kind of agent a conversation is.</summary>
public enum AgentKind
{
    /// <summary>An ownerless conversation that is not a fork: Main, or one made with "New agent".</summary>
    Root,
    /// <summary>A fork of another agent at one of its entries.</summary>
    Fork,
    /// <summary>A conversation owned by a task, such as a subagent tool call.</summary>
    Subagent,
}

/// <summary>One agent of a session (durable-pi <c>AgentSummary</c>).</summary>
/// <param name="Id">The conversation number; 1 is Main.</param>
/// <param name="Name">The custom name, or the derived one (<c>Main</c>, <c>Fork #n</c>, <c>Subagent #n</c>, <c>Agent #n</c>).</param>
/// <param name="Named">Whether <paramref name="Name"/> is a custom name.</param>
/// <param name="Kind">Root, fork or subagent.</param>
/// <param name="ParentId">A fork's parent conversation.</param>
/// <param name="ForkedAt">The entry a fork was made at.</param>
/// <param name="OwnerAgentId">A subagent's owning conversation.</param>
/// <param name="OwnerTaskId">A subagent's owning task.</param>
/// <param name="Busy">Whether a live non-background task of this conversation exists.</param>
/// <param name="Background">The number of its live background tasks.</param>
public sealed record HostedAgent(long Id, string Name, bool Named, AgentKind Kind, long? ParentId, long? ForkedAt, long? OwnerAgentId,
    long? OwnerTaskId, bool Busy, int Background);

/// <summary>One session of the device (durable-pi <c>SessionSummary</c>). An unavailable session has an <see cref="Error"/> and no
/// agents.</summary>
public sealed record HostedSessionSummary(string Id, string Name, string Cwd, ModelRef? Model, DateTimeOffset CreatedAt, string DatabasePath,
    bool Busy, string? Error, IReadOnlyList<HostedAgent> Agents, SessionCompaction Compaction);

/// <summary>A request to create a session.</summary>
public sealed record SessionCreateRequest
{
    /// <summary>The working directory: absolute, <c>~</c>, or relative to the first start directory. It must exist.</summary>
    public required string Cwd { get; init; }
    /// <summary>The name; blank is "New session". Trimmed to 100 characters.</summary>
    public string? Name { get; init; }
    /// <summary>Main's model; null is the device's default model.</summary>
    public ModelRef? Model { get; init; }
    /// <summary>Main's thinking level; null is the device's default.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Makes creation exactly-once: a key that already created a session returns that session (Phase 3's
    /// <c>start_session</c>).</summary>
    public string? SpawnKey { get; init; }
}

/// <summary>How an agent runs (durable-pi <c>AgentConfig</c>).</summary>
public sealed record AgentConfiguration(ModelRef? Model, string ThinkingLevel, string? Instructions, string? Cwd, bool ReadOnly);

/// <summary>A change of an agent's configuration; null fields stay. It applies from the agent's next request.</summary>
public sealed record AgentConfigurationChange
{
    /// <summary>A model the device knows.</summary>
    public ModelRef? Model { get; init; }
    /// <summary><c>off</c>, <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c> or <c>xhigh</c>.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Extra instructions, cut to 12000 characters; the empty string removes them.</summary>
    public string? Instructions { get; init; }
    /// <summary>An existing directory.</summary>
    public string? Cwd { get; init; }
    /// <summary>Offer only tools that read, or every tool again.</summary>
    public bool? ReadOnly { get; init; }
}

/// <summary>One tool an agent's extensions provide.</summary>
/// <param name="Offered">Whether the agent's next request offers it.</param>
public sealed record HostedTool(string Name, string Description, ToolReplay Replay, ToolEffect Effect, bool Offered, bool Deferred,
    JsonObjectValue Parameters);

/// <summary>A message for an agent.</summary>
/// <param name="Content">The text, at most 100000 characters.</param>
/// <param name="RequestId">1 to 128 characters; the device admits a request ID once per agent, so a retry never sends twice.</param>
public sealed record SendRequest(string Content, string RequestId)
{
    /// <summary>What happens while the agent is busy: follow-up (null), steer, or reject.</summary>
    public WhenBusy? WhenBusy { get; init; }
    /// <summary>How long to wait for the answer; null or zero returns once admitted.</summary>
    public TimeSpan? Wait { get; init; }
}

/// <summary>Where a sent message stands.</summary>
public enum SendStatus
{
    /// <summary>Admitted; not waited for.</summary>
    Queued,
    /// <summary>Waited for, and not settled in time.</summary>
    Pending,
    /// <summary>Answered.</summary>
    Done,
    /// <summary>Settled without an answer; see <see cref="SendResult.Reason"/>.</summary>
    Unanswered,
}

/// <summary>The result of <see cref="HostedSession.SendAsync"/> (durable-pi <c>SendResult</c>).</summary>
public sealed record SendResult(SubmissionId SubmissionId, SendStatus Status)
{
    /// <summary>The answer entry, when <see cref="SendStatus.Done"/>.</summary>
    public EntryId? AnswerEntryId { get; init; }
    /// <summary>The answer's text blocks, joined.</summary>
    public string? AnswerText { get; init; }
    /// <summary>Why it was not answered.</summary>
    public string? Reason { get; init; }
}

/// <summary>What changed on the device.</summary>
public enum DeviceChangeKind
{
    /// <summary>The catalog: a session was created, renamed or archived, or an agent was renamed.</summary>
    Catalog,
    /// <summary>A session committed something, or became unavailable.</summary>
    Session,
    /// <summary>The device's App State changed.</summary>
    AppState,
}

/// <summary>One change notification; <see cref="SessionId"/> names the session when there is one.</summary>
public sealed record DeviceChange(DeviceChangeKind Kind, string? SessionId);

/// <summary>An agent's optional extensions and the built-in ones it always has.</summary>
public sealed record AgentExtensions(IReadOnlyList<HostedExtension> Optional, IReadOnlyList<string> BuiltIn);

/// <summary>An optional extension and whether the agent has it on.</summary>
public sealed record HostedExtension(string Name, string Title, string Description, bool Enabled, IReadOnlyList<AgentPrompt> Prompts);

/// <summary>A prompt file of <c>system-prompts</c> and whether the agent has it checked.</summary>
public sealed record AgentPrompt(string Id, string Description, string Text, bool Enabled);

/// <summary>A directory listing for choosing a working directory (durable-pi <c>GET /v1/fs/dirs</c>).</summary>
/// <param name="Path">The listed directory; empty for the start directories.</param>
/// <param name="Parent">Its parent, when it has one.</param>
public sealed record DirectoryListing(string Path, string? Parent, IReadOnlyList<DirectoryEntry> Directories);

/// <summary>One directory of a <see cref="DirectoryListing"/>.</summary>
public sealed record DirectoryEntry(string Name, string Path);
