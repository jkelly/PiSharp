// Decision 0011 (run W4, R0): what runs.db holds, as the History pages, the Run inspector, Compare, Bench and agent compare read it. One
// RunRecord per provider call (kind "call"); an agent compare run is a "parent" row whose models are calls with its id as their parent.
// Status, kind, raw state and cost kind are wire words (lower case), stored and answered as they are. Options, messages and output are the
// JSON the engine sent and received (Pi wire JSON for the output), never a credential: the account is named by id only, and request headers
// a user sets for a run never carry a key (RunSpec refuses them).
using System.Text.Json.Nodes;

namespace PiSharp.WebAgent.Runs;

/// <summary>The status words of a run.</summary>
public static class RunStatus
{
    /// <summary>Sending or streaming now.</summary>
    public const string Running = "running";

    /// <summary>Finished with an answer (stop, length or tool use).</summary>
    public const string Completed = "completed";

    /// <summary>Ended with an error (the provider's, the route's or the Spend Guard's).</summary>
    public const string Error = "error";

    /// <summary>Cancelled by the user.</summary>
    public const string Aborted = "aborted";

    /// <summary>The app stopped while it ran; it is never retried by itself.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>Every status, in the order the History filter lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Running, Completed, Error, Aborted, Interrupted];
}

/// <summary>The kind words of a run row.</summary>
public static class RunKind
{
    /// <summary>One provider call.</summary>
    public const string Call = "call";

    /// <summary>A run that groups calls (an agent compare run; its models' calls name it as their parent).</summary>
    public const string Parent = "parent";
}

/// <summary>The raw payload state of a run.</summary>
public static class RunRawState
{
    /// <summary>Nothing was captured (raw payloads were off, or the call was offline).</summary>
    public const string None = "none";

    /// <summary>Captured and kept (at most <see cref="RunHistorySettings.RawRetentionDays"/> days).</summary>
    public const string Stored = "stored";

    /// <summary>Captured, then removed after <see cref="RunHistorySettings.RawRetentionDays"/> days.</summary>
    public const string Expired = "expired";
}

/// <summary>The token usage of a run, as the provider reported it (zero when it reported none).</summary>
public sealed record RunUsage(double Input, double Output, double CacheRead, double CacheWrite, double Total)
{
    /// <summary>No usage.</summary>
    public static RunUsage Zero { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>One row of runs.db.</summary>
public sealed record RunRecord
{
    /// <summary>The run id (<c>r</c> and 32 hex digits for the engine's runs; the chat call's own id otherwise).</summary>
    public required string Id { get; init; }

    /// <summary>The spend op of the request (unique): <c>run-&lt;id&gt;</c> for the engine's runs, the durable request's
    /// <c>metadata.pisharp.op</c> for a chat's, so a recovered request updates its row instead of adding one.</summary>
    public required string Op { get; init; }

    /// <summary><see cref="RunKind"/>.</summary>
    public string Kind { get; init; } = RunKind.Call;

    /// <summary>Where it was started: a <see cref="Spend.SpendSource"/> wire name (<c>playground</c>, <c>compare</c>, <c>bench</c>,
    /// <c>agent-compare</c>, <c>chat</c>, ...).</summary>
    public required string Source { get; init; }

    /// <summary><see cref="RunStatus"/>.</summary>
    public string Status { get; init; } = RunStatus.Running;

    /// <summary>The provider id.</summary>
    public string Provider { get; init; } = "";

    /// <summary>The model id.</summary>
    public string Model { get; init; } = "";

    /// <summary>The model's API (<c>openai-responses</c>, <c>anthropic-messages</c>, <c>faux</c>, ...), or null.</summary>
    public string? Api { get; init; }

    /// <summary>The account id the call used, or null (faux, a custom provider without one).</summary>
    public string? Account { get; init; }

    /// <summary>The group (one Compare run, one Bench run), or null.</summary>
    public string? GroupId { get; init; }

    /// <summary>The parent run (an agent compare run), or null.</summary>
    public string? ParentId { get; init; }

    /// <summary>The session the call belongs to (a chat id, an agent compare workspace), or null.</summary>
    public string? Session { get; init; }

    /// <summary>A short title (the page's, or the first user text), or null.</summary>
    public string? Title { get; init; }

    /// <summary>The request options as sent (RunSpec without its messages; for a chat call: maxTokens, thinking and the request's size).</summary>
    public JsonObject? Options { get; init; }

    /// <summary>The request's messages (system prompt, tools, messages) as the run sent them, or null when <see cref="MessagesRef"/> names
    /// where they are (a chat's transcript).</summary>
    public JsonNode? Messages { get; init; }

    /// <summary>Where the messages are when they are not stored here: <c>chat:&lt;id&gt;</c> (a chat), <c>&lt;source&gt;:&lt;run&gt;</c>.</summary>
    public string? MessagesRef { get; init; }

    /// <summary>The final assistant message (Pi wire JSON), or null while running or when none arrived.</summary>
    public JsonNode? Output { get; init; }

    /// <summary>The final message's text, for lists and search.</summary>
    public string? OutputText { get; init; }

    /// <summary>The stop reason (Pi wire: <c>stop</c>, <c>length</c>, <c>toolUse</c>, <c>error</c>, <c>aborted</c>), or null.</summary>
    public string? StopReason { get; init; }

    /// <summary>The model the provider said answered (the final message's <c>responseModel</c>), or null when it did not say (or the row
    /// is older than runs.db migration 2).</summary>
    public string? ResponseModel { get; init; }

    /// <summary>The error text, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Notes about how the request was sent (for example options a faux model accepts and does not apply), or null.</summary>
    public string? Notes { get; init; }

    /// <summary>The usage.</summary>
    public RunUsage Usage { get; init; } = RunUsage.Zero;

    /// <summary>The cost in micro-dollars, or null when unknown.</summary>
    public long? CostMicros { get; init; }

    /// <summary>How <see cref="CostMicros"/> was found: <c>actual</c>, <c>estimate</c>, <c>entered</c> (the ledger's settlement),
    /// <c>uncertain</c> (the reserved amount counts until the user resolves it), <c>released</c> (no cost), <c>usage</c> (the provider's
    /// reported cost, no ledger row), or null.</summary>
    public string? CostKind { get; init; }

    /// <summary>The billing mode: <c>metered</c>, <c>subscription</c>, <c>free</c> or <c>unknown</c>; null when unknown.</summary>
    public string? Billing { get; init; }

    /// <summary>The ledger row this call settled (its op, or <c>op~n</c> for a later send), or null.</summary>
    public string? SpendOp { get; init; }

    /// <summary>Milliseconds from the start to the first content (text, thinking or a tool call), or null.</summary>
    public long? TtftMs { get; init; }

    /// <summary>Milliseconds from the start to the end, or null while running.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Output tokens per second after the first token, or null.</summary>
    public double? TokensPerSecond { get; init; }

    /// <summary>Whether the user pinned it (pinned runs never expire).</summary>
    public bool Pinned { get; init; }

    /// <summary><see cref="RunRawState"/>.</summary>
    public string RawState { get; init; } = RunRawState.None;

    /// <summary>When the row was made.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>When the call started, or null.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>When the call ended, or null.</summary>
    public DateTimeOffset? Finished { get; init; }

    /// <summary>When the row last changed.</summary>
    public DateTimeOffset Updated { get; init; }
}

/// <summary>A run list filter (History, Compare, Bench). Every field is optional; text matches are case-insensitive.</summary>
public sealed record RunQuery
{
    /// <summary>The most rows a page may ask for.</summary>
    public const int MaxLimit = 500;

    /// <summary>Only this source (wire name).</summary>
    public string? Source { get; init; }

    /// <summary>Only this provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Only this model.</summary>
    public string? Model { get; init; }

    /// <summary>Only this status.</summary>
    public string? Status { get; init; }

    /// <summary>Only this kind.</summary>
    public string? Kind { get; init; }

    /// <summary>Text in the title, the output, the error, the model or the provider.</summary>
    public string? Search { get; init; }

    /// <summary>Created at or after.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Created before.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Only this group.</summary>
    public string? GroupId { get; init; }

    /// <summary>Only the children of this parent.</summary>
    public string? ParentId { get; init; }

    /// <summary>Only top-level runs (no parent): a call of its own, a Compare or Bench group's calls, an agent compare run's top row; not
    /// an agent compare model's row or its calls.</summary>
    public bool TopLevelOnly { get; init; }

    /// <summary>Only this stop reason (the run's own, as recorded: <c>stop</c>, <c>length</c>, <c>toolUse</c>, or an agent compare model's
    /// outcome); its status is not implied, so a caller that means "completed with" also sets <see cref="Status"/>.</summary>
    public string? StopReason { get; init; }

    /// <summary>Only pinned (true) or unpinned (false) runs.</summary>
    public bool? Pinned { get; init; }

    /// <summary>Leave out the runs these groups hide (<see cref="RunBlinds.Groups"/>: an unvoted blind round's runs), or null.</summary>
    public IReadOnlyCollection<string>? HiddenGroups { get; init; }

    /// <summary>The page size (1 to <see cref="MaxLimit"/>; default 50).</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Rows to skip.</summary>
    public int Offset { get; init; }
}

/// <summary>One page of runs, newest first.</summary>
/// <param name="Items">The rows (without their messages and output, which <see cref="RunStore.Get"/> returns).</param>
/// <param name="Total">How many rows match the filter.</param>
public sealed record RunPage(IReadOnlyList<RunRecord> Items, long Total);

/// <summary>Totals over the runs a filter matches (History's summary strip).</summary>
public sealed record RunTotals(long Count, long Errors, long CostMicros, double InputTokens, double OutputTokens, double? AverageTtftMs,
    double? AverageTokensPerSecond);

/// <summary>One normalized stream event of a run: its sequence number (from 1), when it arrived (milliseconds after the call started) and the
/// event as Pi wire JSON (<c>PiWireJson.WriteEvent</c>: <c>start</c>, <c>text_delta</c>, <c>done</c>, <c>error</c>, ...).</summary>
public sealed record RunEventRecord(long Seq, long AtMs, JsonNode Event);

/// <summary>One raw payload item of a run (only when the user turned raw payloads on): the redacted request (<c>request</c>: method, URL,
/// headers, body), the response headers (<c>response</c>), a raw SSE line (<c>sse</c>), a small non-SSE body (<c>body</c>), a transport
/// error (<c>error</c>) or a timing mark (<c>timing</c>); when it happened (milliseconds after the call started) and its text.</summary>
public sealed record RunRawItem(long Seq, string Kind, long AtMs, string Data);

/// <summary>What a cleanup removed.</summary>
/// <param name="Runs">Runs deleted for their age.</param>
/// <param name="RawRuns">Runs whose raw payloads were removed for their age (their rows stay).</param>
public sealed record RunCleanup(int Runs, int RawRuns);

/// <summary>A change to runs.db, for pages that follow it.</summary>
/// <param name="Kind"><c>started</c>, <c>finished</c>, <c>pinned</c>, <c>deleted</c> or <c>cleaned</c>.</param>
/// <param name="Ids">The runs it touched (empty for a cleanup).</param>
public sealed record RunChange(string Kind, IReadOnlyList<string> Ids);
