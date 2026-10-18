// PiSharp-only (decision 0008). The durable network's v1 JSON bodies, after durable-pi's wire contract (apps/dpi-sdk/src/api.ts, MIT). Keys and
// value spellings are durable-pi's, so its clients and hub can talk to a PiSharp device and ours to theirs. PiSharp additions are optional
// fields a durable-pi client ignores: a model's provider next to its id, a tool's effect and whether it is deferred, and a session's
// provider. Agent events are not typed here: they pass through as pi-durable's own JSON (PiSharp.Durable's AgentEvent.ToJson()).
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.Durable.Client;

/// <summary>The wire's constants and its JSON settings.</summary>
public static class DurableWire
{
    /// <summary>The API version every device and hub of this contract speaks.</summary>
    public const int ApiVersion = 1;

    /// <summary>The pi-durable version a PiSharp device reports.</summary>
    public const string DurableVersion = "1.2.0";

    /// <summary>The device's default port.</summary>
    public const int DefaultPort = 4780;

    /// <summary>Thinking levels, weakest first.</summary>
    public static IReadOnlyList<string> ThinkingLevels { get; } = ["off", "minimal", "low", "medium", "high", "xhigh"];

    /// <summary>camelCase, null fields left out.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A body as JSON text.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>A body as a JSON node.</summary>
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>A body read from a JSON node; null for JSON null.</summary>
    public static T? Read<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(Options);
}

/// <summary>The error envelope's codes and their HTTP statuses.</summary>
public static class DurableErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not_found";
    public const string Invalid = "invalid";
    public const string Busy = "busy";
    public const string Conflict = "conflict";
    public const string Ambiguous = "ambiguous";
    public const string Unavailable = "unavailable";
    public const string Internal = "internal";
    /// <summary>A client-side code: the device did not answer.</summary>
    public const string Offline = "offline";

    /// <summary>The HTTP status of a code.</summary>
    public static int Status(string code) => code switch
    {
        Unauthorized => 401,
        Invalid => 400,
        NotFound => 404,
        Busy or Conflict or Ambiguous => 409,
        Unavailable => 503,
        _ => 500,
    };
}

/// <summary><c>{error: {code, message}}</c>.</summary>
public sealed record ApiErrorBody(ApiError Error);

/// <summary>An error's code and message.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>A device or hub answered with an error, or could not be reached (<see cref="DurableErrorCodes.Offline"/>).</summary>
public sealed class DurableApiException(string code, string message, int status = 0, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>The error code.</summary>
    public string Code { get; } = code;

    /// <summary>The HTTP status; 0 when no response arrived.</summary>
    public int Status { get; } = status;
}

/// <summary>A chat model of a device (durable-pi <c>ModelInfo</c>). Costs are USD per million tokens.</summary>
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string Name { get; init; }
    public string Tier { get; init; } = "";
    public string Icon { get; init; } = "";
    public double ContextWindow { get; init; }
    public bool Reasoning { get; init; }
    public ModelCost Cost { get; init; } = new(0, 0);
}

/// <summary>Input and output prices, USD per million tokens.</summary>
public sealed record ModelCost(double Input, double Output);

/// <summary>Where a device keeps its data.</summary>
public sealed record DeviceStorage(string DataDir, string CatalogPath, string AppStatePath);

/// <summary>What a device is (<c>GET /v1/device</c>).</summary>
public sealed record DeviceInfo
{
    public required string Uid { get; init; }
    public required string Name { get; init; }
    public string Hostname { get; init; } = "";
    public string Platform { get; init; } = "";
    public int ApiVersion { get; init; }
    public string DurableVersion { get; init; } = "";
    public long StartedAt { get; init; }
    public IReadOnlyList<string> StartDirs { get; init; } = [];
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];
    /// <summary>The default model's id.</summary>
    public string DefaultModel { get; init; } = "";
    /// <summary>PiSharp: the default model's provider.</summary>
    public string? DefaultProvider { get; init; }
    public DeviceStorage? Storage { get; init; }
    public string? CodeDir { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    /// <summary>The device's job definitions.</summary>
    public JsonArray? Jobs { get; init; }
}

/// <summary>A model by provider and id.</summary>
public sealed record WireModelRef(string Provider, string ModelId);

/// <summary>Automatic compaction thresholds of a session (durable-pi <c>CompactionSettings</c>).</summary>
public sealed record CompactionSettings(bool Enabled, double ReserveTokens, double BackgroundTokens, double KeepRecentTokens)
{
    /// <summary>pi-durable's own margins.</summary>
    public static CompactionSettings PiDurableDefaults { get; } = new(true, 16384, 32768, 20000);

    /// <summary>Context sizes where compaction starts for a model window: <c>Auto</c> in the background (null when off), <c>Wait</c>
    /// blocking.</summary>
    public (double Wait, double? Auto) Thresholds(double window)
    {
        var wait = Math.Max(0, window - ReserveTokens);
        return (wait, BackgroundTokens > 0 ? Math.Max(0, wait - BackgroundTokens) : null);
    }
}

/// <summary>One agent of a session (durable-pi <c>AgentSummary</c>).</summary>
public record AgentSummary
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public bool Named { get; init; }
    /// <summary><c>root</c>, <c>fork</c> or <c>subagent</c>.</summary>
    public required string Kind { get; init; }
    public long? ParentId { get; init; }
    public long? ForkedAt { get; init; }
    public long? OwnerAgentId { get; init; }
    public long? OwnerTaskId { get; init; }
    public bool Busy { get; init; }
    public int Background { get; init; }
}

/// <summary>One session of a device (durable-pi <c>SessionSummary</c>).</summary>
public sealed record SessionSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Cwd { get; init; } = "";
    /// <summary>Main's model id.</summary>
    public string Model { get; init; } = "";
    /// <summary>PiSharp: Main's provider.</summary>
    public string? Provider { get; init; }
    public long CreatedAt { get; init; }
    public string DbPath { get; init; } = "";
    public bool Busy { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<AgentSummary> Agents { get; init; } = [];
    public CompactionSettings? Compaction { get; init; }
}

/// <summary>A device's sessions (<c>GET /v1/tree</c>).</summary>
public sealed record DeviceTree
{
    public required string Uid { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<SessionSummary> Sessions { get; init; } = [];
    public long At { get; init; }
}

/// <summary>How an agent runs (durable-pi <c>AgentConfig</c>).</summary>
public sealed record AgentConfig
{
    public WireModelRef? Model { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? Instructions { get; init; }
    public string? Cwd { get; init; }
    public bool? ReadOnly { get; init; }
}

/// <summary>A tool of an agent's extensions.</summary>
public sealed record ToolInfo
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary><c>safe</c> or <c>unsafe</c>.</summary>
    public string Replay { get; init; } = "unsafe";
    public JsonNode? Parameters { get; init; }
    public bool Writes { get; init; }
    public bool Offered { get; init; }
    /// <summary>PiSharp: <c>read</c>, <c>write</c> or <c>exec</c>.</summary>
    public string? Effect { get; init; }
    /// <summary>PiSharp: the tool is deferred (found through tool search).</summary>
    public bool? Deferred { get; init; }
}

/// <summary>An agent with its configuration, tools and extensions (durable-pi <c>AgentDetail</c>).</summary>
public sealed record AgentDetail : AgentSummary
{
    public required string SessionId { get; init; }
    public AgentConfig Config { get; init; } = new();
    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];
    public IReadOnlyList<string> Extensions { get; init; } = [];
}

/// <summary>A prompt file of the <c>system-prompts</c> extension.</summary>
public sealed record PromptInfo(string Id, string Title, string Description, bool Enabled, string Text);

/// <summary>An extension an agent can turn on or off.</summary>
public sealed record ExtensionInfo
{
    public required string Name { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; }
    public IReadOnlyList<PromptInfo>? Prompts { get; init; }
}

/// <summary>An agent's optional extensions and the built-in ones it always runs.</summary>
public sealed record AgentExtensions(IReadOnlyList<ExtensionInfo> Extensions, IReadOnlyList<string> BuiltIn);

/// <summary>An answer's entry and text.</summary>
public sealed record SendAnswer(long EntryId, string Text);

/// <summary>Where a sent message stands: <c>queued</c>, <c>pending</c>, <c>done</c> with its answer, or <c>unanswered</c> with a
/// reason.</summary>
public sealed record SendResult
{
    public required long SubmissionId { get; init; }
    public required string Status { get; init; }
    public SendAnswer? Answer { get; init; }
    public string? Reason { get; init; }
}

/// <summary>A task's state as a task list shows it.</summary>
public sealed record TaskNodeState
{
    public required string Status { get; init; }
    public string? Phase { get; init; }
    public IReadOnlyList<long>? On { get; init; }
    public string? Policy { get; init; }
}

/// <summary>A live task of a session (durable-pi <c>TaskNode</c>).</summary>
public sealed record TaskNode
{
    public required long Id { get; init; }
    public required string Kind { get; init; }
    public long ConversationId { get; init; }
    public long? Owner { get; init; }
    public bool Background { get; init; }
    public bool? Aborting { get; init; }
    public required TaskNodeState State { get; init; }
    public IReadOnlyList<long> Conversations { get; init; } = [];
    /// <summary>The tool of a <c>pi.tool</c> task, or the job title of a run.</summary>
    public string? Label { get; init; }
    /// <summary>A <c>pi.tool</c> task's call id.</summary>
    public string? CallId { get; init; }
}

/// <summary>Token counts and costs (pi-ai <c>Usage</c>).</summary>
public sealed record WireUsage
{
    public double Input { get; init; }
    public double Output { get; init; }
    public double CacheRead { get; init; }
    public double CacheWrite { get; init; }
    public double TotalTokens { get; init; }
    public WireUsageCost Cost { get; init; } = new();
}

/// <summary>Costs in USD.</summary>
public sealed record WireUsageCost
{
    public double Input { get; init; }
    public double Output { get; init; }
    public double CacheRead { get; init; }
    public double CacheWrite { get; init; }
    public double Total { get; init; }
}

/// <summary>A session's usage by model (<c>provider/model</c>) and by tool.</summary>
public sealed record UsageTotals
{
    public IReadOnlyDictionary<string, WireUsage> Models { get; init; } = new Dictionary<string, WireUsage>();
    public IReadOnlyDictionary<string, WireUsage> Tools { get; init; } = new Dictionary<string, WireUsage>();
}

/// <summary>A directory listing for choosing a working directory.</summary>
public sealed record DirListing(string Path, string? Parent, IReadOnlyList<DirEntry> Dirs);

/// <summary>One directory of a listing.</summary>
public sealed record DirEntry(string Name, string Path);

/// <summary>A queued message with its content.</summary>
public sealed record InboxItem(long Id, string Mode, string? Content);

/// <summary>The public view of a hub's device: its token never leaves the hub.</summary>
public sealed record NetworkDevice
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary><c>local</c>, <c>ssh</c>, <c>exe-dev</c> or <c>url</c>.</summary>
    public string Kind { get; init; } = "url";
    public string Url { get; init; } = "";
    /// <summary><c>config</c> (read-only) or <c>ui</c>.</summary>
    public string Source { get; init; } = "ui";
    public bool HasToken { get; init; }
    /// <summary><c>checking</c>, <c>online</c>, <c>offline</c>, <c>unauthorized</c>, <c>mismatch</c> or <c>incompatible</c>.</summary>
    public string Status { get; init; } = "checking";
    public string? Error { get; init; }
    public long? LastSeenAt { get; init; }
    public DeviceInfo? Info { get; init; }
    public DeviceTree? Tree { get; init; }
}
