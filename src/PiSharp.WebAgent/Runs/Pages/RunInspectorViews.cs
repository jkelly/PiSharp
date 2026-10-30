// Decision 0011 (run W4, R3): the model of the Run inspector (GET /runs/{id}, Views/Runs/Details.cshtml: _RunHeader and _RunTabs). Every
// value is runs.db's: the run row, its stored stream events and, when the user turned raw payloads on, its raw request, response, SSE lines
// and timing marks (redacted before they were stored; a key-bearing header is shown redacted whatever was stored). A tab whose data was not
// stored says so ("not stored", or "expired" after RunHistorySettings.RawRetentionDays) instead of showing anything made up.
using PiSharp.WebAgent.Playground;

namespace PiSharp.WebAgent.Runs;

/// <summary>The model of <c>Views/Runs/Details.cshtml</c>.</summary>
public sealed record RunInspectorView
{
    /// <summary>The run (with its options, messages and output).</summary>
    public required RunRecord Run { get; init; }

    /// <summary>The page state: <c>default</c>, <c>toolcall</c>, <c>error</c>, <c>running</c> or <c>group</c> (a run that groups calls).</summary>
    public required string State { get; init; }

    /// <summary>Whether the run is still streaming in this app.</summary>
    public bool Running { get; init; }

    /// <summary>The source's label.</summary>
    public required string SourceLabel { get; init; }

    /// <summary>The source's icon.</summary>
    public required string SourceIcon { get; init; }

    /// <summary>The stop chip.</summary>
    public required RunChip Stop { get; init; }

    /// <summary>The account as the Spend pages name it (<c>key …7f3a</c>, a sign-in's label), or null.</summary>
    public string? AccountText { get; init; }

    /// <summary>The billing mode's label (<c>Metered</c>, <c>Subscription</c>, <c>Free</c>), or null.</summary>
    public string? BillingText { get; init; }

    /// <summary>When it started, local (<c>2026-10-08 14:21:07.312</c>).</summary>
    public string Started { get; init; } = "";

    /// <summary>The thinking level as sent (<c>medium</c>, <c>off</c>).</summary>
    public string Thinking { get; init; } = "off";

    /// <summary>The session (a chat id, a workspace), or null.</summary>
    public string? Session { get; init; }

    /// <summary>Where the session opens (a chat), or null.</summary>
    public string? SessionHref { get; init; }

    /// <summary>The user prompt (the first user text, or the title).</summary>
    public string? Prompt { get; init; }

    /// <summary>Whether Re-run and Load into playground apply.</summary>
    public bool Replayable { get; init; }

    /// <summary>View code for the run's request, or null (a chat's call).</summary>
    public ViewCodeView? Code { get; init; }

    /// <summary>The failure banner's heading, or null.</summary>
    public string? ErrorTitle { get; init; }

    /// <summary>The failure banner's text, or null.</summary>
    public string? ErrorText { get; init; }

    /// <summary>The tool calls the answer ended with.</summary>
    public IReadOnlyList<RunToolCallView> ToolCalls { get; init; } = [];

    /// <summary>The summary metrics: TTFB, TTFT, Total, Tokens in, Tokens out, Cache read, Cost, Retries.</summary>
    public IReadOnlyList<RunMetric> Metrics { get; init; } = [];

    /// <summary><see cref="RunRawState"/> of the run.</summary>
    public string RawState => Run.RawState;

    /// <summary>Why a raw tab is empty (raw payloads off, expired, an offline model), or null when they were stored.</summary>
    public string? RawNote { get; init; }

    /// <summary>The raw requests (one per attempt), when stored.</summary>
    public IReadOnlyList<RunRawRequest> Requests { get; init; } = [];

    /// <summary>The raw responses (one per attempt that got headers), when stored.</summary>
    public IReadOnlyList<RunRawResponse> Responses { get; init; } = [];

    /// <summary>Transport errors captured with the raw payloads.</summary>
    public IReadOnlyList<string> TransportErrors { get; init; } = [];

    /// <summary>The run's own request as runs.db stored it: its options (indented JSON), or null.</summary>
    public string? OptionsJson { get; init; }

    /// <summary>Its system prompt, tools and messages (indented JSON, image data abbreviated), or null when stored elsewhere.</summary>
    public string? MessagesJson { get; init; }

    /// <summary>Where the messages are when not here (<c>chat:id</c>), or null.</summary>
    public string? MessagesRef { get; init; }

    /// <summary>The final message as recorded (indented JSON), or null.</summary>
    public string? OutputJson { get; init; }

    /// <summary>The raw SSE rows (at most <see cref="RunInspectorPages.MaxSseRows"/>).</summary>
    public IReadOnlyList<RunSseRow> Sse { get; init; } = [];

    /// <summary>How many SSE data lines were stored.</summary>
    public int SseCount { get; init; }

    /// <summary>The bytes of SSE stored.</summary>
    public long SseBytes { get; init; }

    /// <summary>The event families the Raw SSE toggles filter on.</summary>
    public IReadOnlyList<string> SseGroups { get; init; } = [];

    /// <summary>The normalized events.</summary>
    public IReadOnlyList<RunEventRow> Events { get; init; } = [];

    /// <summary>How many normalized events were stored.</summary>
    public int EventCount { get; init; }

    /// <summary>Whether stream events are stored (Settings › Data).</summary>
    public bool EventsKept { get; init; } = true;

    /// <summary>The timing waterfall's rows.</summary>
    public IReadOnlyList<RunWaterfallRow> Waterfall { get; init; } = [];

    /// <summary>The waterfall's axis labels.</summary>
    public IReadOnlyList<string> Ticks { get; init; } = [];

    /// <summary>A line under the waterfall, or null.</summary>
    public string? TimingNote { get; init; }

    /// <summary>The Usage &amp; cost tab.</summary>
    public required RunUsageView Usage { get; init; }

    /// <summary>The rows this run groups (models, or calls), for a grouped run.</summary>
    public IReadOnlyList<RunHistoryRow> Children { get; init; } = [];

    /// <summary>The run this one belongs to, or null.</summary>
    public string? ParentHref { get; init; }

    /// <summary>The inspector's address of this run.</summary>
    public string Href => "/runs/" + Uri.EscapeDataString(Run.Id);

    /// <summary>The Playground with this run's request.</summary>
    public string PlaygroundHref => "/playground?run=" + Uri.EscapeDataString(Run.Id);

    /// <summary>Whether a HAR can be made (raw payloads were stored).</summary>
    public bool HasHar => Requests.Count > 0;
}

/// <summary>A tool call the answer ended with.</summary>
public sealed record RunToolCallView(string Name, string Id);

/// <summary>One header as the inspector shows it.</summary>
/// <param name="Name">Lower-case name.</param>
/// <param name="Value">The value (empty when redacted).</param>
/// <param name="Redacted">Whether it carries a credential and shows redacted.</param>
public sealed record RunHeader(string Name, string Value, bool Redacted);

/// <summary>One raw request (an attempt).</summary>
public sealed record RunRawRequest(int Attempt, long AtMs, string Method, string? Url, string? Version, IReadOnlyList<RunHeader> Headers, string? Body, long? BodyBytes,
    string? ContentType, bool BodyCut);

/// <summary>One raw response's head (and its body when it was not a stream).</summary>
public sealed record RunRawResponse(int Attempt, long AtMs, int Status, string? Reason, string? Version, IReadOnlyList<RunHeader> Headers, string? Body, string? ContentType)
{
    /// <summary>Whether it is a success.</summary>
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>Whether it streamed server-sent events.</summary>
    public bool Stream => ContentType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>One raw SSE row: arrival time, event name, data and the event family the toggles use.</summary>
public sealed record RunSseRow(string Time, string Event, string Data, string Group);

/// <summary>One waterfall row: its label, where its bar starts and how wide it is (percent of the axis), its phase (the bar's colour) and
/// when it ended.</summary>
public sealed record RunWaterfallRow(string Label, double Left, double Width, string Phase, string At);

/// <summary>The Usage &amp; cost tab.</summary>
public sealed record RunUsageView
{
    /// <summary>Whether usage was recorded (false: the empty state).</summary>
    public bool Recorded { get; init; }

    /// <summary>The bucket rows: input, output, cacheRead, cacheWrite.</summary>
    public IReadOnlyList<RunUsageRow> Rows { get; init; } = [];

    /// <summary>The total tokens.</summary>
    public string TotalTokens { get; init; } = "0";

    /// <summary>The total cost (the recorded one when there is one).</summary>
    public string TotalCost { get; init; } = "—";

    /// <summary>A note about the total, or null.</summary>
    public string? CostNote { get; init; }

    /// <summary>The pricing tier chip.</summary>
    public RunChip Tier { get; init; } = new("no price", "outline");

    /// <summary>The rates in short (<c>$2 in / $10 out per 1M</c>), or empty.</summary>
    public string Rates { get; init; } = "";

    /// <summary>What the tier means.</summary>
    public string TierText { get; init; } = "";

    /// <summary>The billing chip.</summary>
    public RunChip Billing { get; init; } = new("Unknown", "outline");

    /// <summary>The line by the billing chip (account · provider).</summary>
    public string BillingSub { get; init; } = "";

    /// <summary>What the billing mode means.</summary>
    public string BillingText { get; init; } = "";

    /// <summary>The usage as recorded (indented JSON).</summary>
    public string UsageJson { get; init; } = "";

    /// <summary>The empty state's text.</summary>
    public string EmptyText { get; init; } = "";
}

/// <summary>One bucket of the usage table.</summary>
public sealed record RunUsageRow(string Bucket, string Tokens, string Price, string Cost);
