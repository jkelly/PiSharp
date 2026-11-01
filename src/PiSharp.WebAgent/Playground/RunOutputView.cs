// Decision 0011 (run W4, R1): the model of the shared _RunOutput partial (the Playground's output pane; Compare's columns, the Tool Bench
// and the Run inspector can reuse it): one run of runs.db, or none. Its state is the mockups': empty (nothing run), streaming (the run is
// live: the text and thinking streamed so far, from the run's live log), completed (the final AssistantMessage: thinking, Markdown text,
// tool calls awaiting a result), stopped (cancelled, with what had streamed) and error (failed or interrupted). The metrics are the row's
// own (time to first token, duration, tokens per second, usage, the ledger's cost, the stop reason); the Events tab lists the stored (or
// live) normalized events with their deltas collapsed. Nothing here is computed from anything the run did not record.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Playground;

/// <summary>A content block of a run's answer.</summary>
/// <param name="Kind">text, thinking or toolCall.</param>
/// <param name="Text">The text or thinking, or the tool call's arguments (indented JSON).</param>
/// <param name="Id">The tool call's id.</param>
/// <param name="Name">The tool's name.</param>
/// <param name="Summary">The tool call's first argument value, shortened (the card's summary line).</param>
public sealed record RunOutputBlock(string Kind, string Text, string? Id = null, string? Name = null, string? Summary = null);

/// <summary>One row of the Events tab.</summary>
/// <param name="AtMs">Milliseconds since the run started.</param>
/// <param name="Event">The event's type (<c>text_delta</c>...).</param>
/// <param name="Data">A one-line summary of its data.</param>
/// <param name="Group">The filter it belongs to: thinking, text, toolcall or other.</param>
/// <param name="Extra">Whether it is a delta after the first of its run of deltas (hidden while deltas are collapsed).</param>
/// <param name="Collapsed">For the summary row of a run of deltas: how many it stands for; 0 otherwise.</param>
public sealed record RunEventRow(long AtMs, string Event, string Data, string Group, bool Extra = false, int Collapsed = 0)
{
    /// <summary>The time column.</summary>
    public string Time => Collapsed > 0 ? "…" : "+" + AtMs.ToString("N0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>The tone of the event name: ok for done, danger for error.</summary>
    public string? Tone => Event switch { "done" => "text-ps-ok", "error" => "text-ps-danger", _ => null };
}

/// <summary>A metric of the strip.</summary>
/// <param name="Label">TTFT, Total...</param>
/// <param name="Value">The figure, or an em dash.</param>
/// <param name="Unit">A unit after it, or null.</param>
/// <param name="Title">A tooltip, or null.</param>
/// <param name="Tone">A text class (live, danger, faint), or null.</param>
public sealed record RunMetric(string Label, string Value, string? Unit = null, string? Title = null, string? Tone = null);

/// <summary>The model of <c>Views/Shared/_RunOutput.cshtml</c>.</summary>
public sealed record RunOutputView
{
    /// <summary>The most event rows rendered (the rest are in the Run inspector).</summary>
    public const int MaxEventRows = 2_000;

    /// <summary>The prefix of every element id.</summary>
    public string IdPrefix { get; init; } = "pg";

    /// <summary>The run, or null for the empty state.</summary>
    public RunRecord? Run { get; init; }

    /// <summary>Whether the run is still live (its events are streaming).</summary>
    public bool Live { get; init; }

    /// <summary>The run's events so far (live) or as stored.</summary>
    public IReadOnlyList<RunEventRecord> Events { get; init; } = [];

    /// <summary>Whether events were stored for a finished run (Settings › Data, "Store SSE events").</summary>
    public bool EventsKept { get; init; } = true;

    /// <summary>The shown model's display name, or null for the run's id.</summary>
    public string? ModelName { get; init; }

    /// <summary>The thinking level the run asked for, or null.</summary>
    public string? Thinking { get; init; }

    /// <summary>The tools of the request with a mock response (the Add toolResult button fills it in), by name.</summary>
    public IReadOnlySet<string> MockTools { get; init; } = new HashSet<string>();

    /// <summary>Where "Open in inspector" goes.</summary>
    public string? InspectorUrl => Run is null ? null : "/runs/" + Uri.EscapeDataString(Run.Id);

    /// <summary>Where "Re-run from history" of the empty state goes.</summary>
    public string HistoryUrl { get; init; } = "/history?source=playground";

    /// <summary>empty, streaming, completed, stopped or error.</summary>
    public string State => Run switch
    {
        null => "empty",
        { Status: RunStatus.Running } => "streaming",
        _ when Live => "streaming",
        { Status: RunStatus.Completed } => "completed",
        { Status: RunStatus.Aborted } => "stopped",
        _ => "error",
    };

    /// <summary>The status chip: text, tone and icon.</summary>
    public (string Text, string? Tone, string? Icon) Chip => State switch
    {
        "streaming" => ("streaming", "info", null),
        "completed" => ("done", "ok", "bi-check2"),
        "stopped" => ("stopped", "warn", "bi-stop-fill"),
        "error" => (Run?.Status == RunStatus.Interrupted ? "interrupted" : "error", "danger", "bi-x-octagon"),
        _ => ("idle", null, null),
    };

    /// <summary>The answer's content blocks: the final message's, or what streamed so far.</summary>
    public IReadOnlyList<RunOutputBlock> Blocks
    {
        get
        {
            if (Run?.Output?["content"] is JsonArray content)
                return [.. content.OfType<JsonObject>().Select(Block).OfType<RunOutputBlock>()];
            return Streamed();
        }
    }

    /// <summary>The answer's text (the Text tab).</summary>
    public string Text => Run?.OutputText ?? string.Concat(Blocks.Where(block => block.Kind == "text").Select(block => block.Text));

    /// <summary>The final message, indented (the JSON tab), or null.</summary>
    public string? Json => Run?.Output is { } output ? RequestJson.Indented(output) : null;

    /// <summary>The tool calls of the answer.</summary>
    public IReadOnlyList<RunOutputBlock> ToolCalls => [.. Blocks.Where(block => block.Kind == "toolCall")];

    /// <summary>The tool calls as the message editor appends them (<c>data-tool-calls</c>): [{id, name, arguments}].</summary>
    public string ToolCallsJson => new JsonArray([.. ToolCalls.Select(call => (JsonNode)new JsonObject
    {
        ["id"] = call.Id,
        ["name"] = call.Name,
        ["arguments"] = JsonNode.Parse(call.Text.Length == 0 ? "{}" : call.Text),
    })]).ToJsonString();

    /// <summary>The model line above the answer: <c>provider/model · level</c>.</summary>
    public string ModelLine => Run is null ? "" : $"{Run.Provider}/{Run.Model}" + (Thinking is { } level ? " · " + level : "");

    /// <summary>The events' rows, deltas collapsed into a summary row after the first of each run.</summary>
    public IReadOnlyList<RunEventRow> EventRows
    {
        get
        {
            var rows = new List<RunEventRow>();
            var index = 0;
            var items = Events;
            while (index < items.Count && rows.Count < MaxEventRows)
            {
                var item = items[index];
                var type = Type(item.Event);
                if (type.EndsWith("_delta", StringComparison.Ordinal))
                {
                    var end = index;
                    while (end + 1 < items.Count && Type(items[end + 1].Event) == type) end++;
                    rows.Add(Row(item));
                    for (var next = index + 1; next <= end && rows.Count < MaxEventRows; next++) rows.Add(Row(items[next]) with { Extra = true });
                    if (end > index) rows.Add(new RunEventRow(items[end].AtMs, type, $"{end - index:N0} more · collapsed", Group(type), Collapsed: end - index));
                    index = end + 1;
                    continue;
                }
                rows.Add(Row(item));
                index++;
            }
            return rows;
        }
    }

    /// <summary>How many events there are.</summary>
    public int EventCount => Events.Count;

    /// <summary>Whether some events were not rendered.</summary>
    public bool EventsCut => EventRows.Count >= MaxEventRows;

    /// <summary>The last event's sequence number (the page reconnects after it).</summary>
    public long LastSeq => Events.Count > 0 ? Events[^1].Seq : 0;

    /// <summary>The metric strip.</summary>
    public IReadOnlyList<RunMetric> Metrics
    {
        get
        {
            var faint = Run is null ? "text-ps-faint" : null;
            if (Run is not { } run) return [.. new[] { "TTFT", "Total", "Tokens/s", "Input", "Output", "Cache read", "Cost", "stopReason" }.Select(label => new RunMetric(label, "—", Tone: faint))];
            var live = State == "streaming";
            var usage = run.Usage;
            return
            [
                new RunMetric("TTFT", run.TtftMs is { } ttft ? Seconds(ttft) : "—", run.TtftMs is null ? null : "s"),
                new RunMetric("Total", run.DurationMs is { } total ? Seconds(total) : "—", run.DurationMs is null ? null : "s"),
                new RunMetric("Tokens/s", run.TokensPerSecond is { } rate ? rate.ToString("0.#", CultureInfo.InvariantCulture) : "—"),
                new RunMetric("Input", live ? "—" : Count(usage.Input)),
                new RunMetric("Output", live ? "—" : Count(usage.Output)),
                new RunMetric("Cache read", live ? "—" : Count(usage.CacheRead)),
                new RunMetric("Cost", Cost.Text, Cost.Unit, Cost.Title),
                new RunMetric("stopReason", run.StopReason ?? (State == "error" ? "error" : "—"), Tone: State == "error" ? "text-ps-danger" : null),
            ];
        }
    }

    /// <summary>The run's cost as the metric shows it: the ledger row it settled (actual, estimate, uncertain, released) with its billing.</summary>
    public (string Text, string? Unit, string? Title) Cost
    {
        get
        {
            if (Run is not { } run || run.CostMicros is not { } micros) return ("—", null, Run?.Status == RunStatus.Running ? "The cost is known when the run ends." : null);
            var money = ChatSpendText.Money(micros);
            return (run.Billing, run.CostKind) switch
            {
                ("free", _) => ("free", null, "This provider is free: nothing counted toward your caps."),
                ("subscription", _) => ("≈ " + money, "list", "List price: a subscription is never counted toward money caps."),
                (_, "estimate") => ("≈ " + money, "est.", "Settled at an estimate from what was received."),
                (_, "uncertain") => (money, "reserved", "The stream ended without usage: the reserved amount counts until you resolve it in History › Spend."),
                (_, "released") => (money, null, "Released: nothing was charged."),
                (_, "reserved") => (money, "reserved", "Reserved while the run runs."),
                _ => (money, null, run.Billing == "metered" ? "Metered: counted toward your caps." : null),
            };
        }
    }

    /// <summary>The footer under the answer: input, output and cache-read tokens, cost, duration and the stop reason.</summary>
    public string? DurationText => Run?.DurationMs is { } ms ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s" : null;

    /// <summary>The error line of a failed run: its message (or the stop note of a cancelled one).</summary>
    public string? ErrorMessage => Run?.Error;

    /// <summary>The spend error in the run's error, or null.</summary>
    public SpendErrorInfo? SpendError => SpendErrors.Parse(Run?.Error);

    /// <summary>The heading of the error card.</summary>
    public string ErrorTitle => Run?.Status == RunStatus.Interrupted ? "Interrupted"
        : SpendError is { } spend ? spend.Code switch { SpendErrors.Blocked => "Stopped by a spend cap", SpendErrors.UnknownPrice => "No price for this model", _ => "Over a spend cap" }
        : "The run failed";

    /// <summary>The run's notes (options sent, caps), or null.</summary>
    public string? Notes => Run?.Notes;

    /// <summary>The run's time, as the toolbar's status shows it.</summary>
    public string? Finished => Run?.Finished?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The toolbar's status line: <c>last run 10:30:12 · 6.9 s · $0.0078</c>, <c>streaming</c>, <c>last run failed</c> or <c>not run yet</c>.</summary>
    public string StatusLine => State switch
    {
        "empty" => "not run yet",
        "streaming" => "streaming",
        "error" => "last run failed",
        _ => string.Join(" · ", new[] { "last run " + Finished, Run?.DurationMs is { } ms ? (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s" : null,
            Cost.Text == "—" ? null : Cost.Text + (Cost.Unit is { } unit ? " " + unit : "") }.Where(part => part is not null)),
    };

    private IReadOnlyList<RunOutputBlock> Streamed()
    {
        var blocks = new SortedDictionary<int, (string Kind, StringBuilder Text, string? Id, string? Name)>();
        foreach (var item in Events)
        {
            if (item.Event is not JsonObject e) continue;
            var type = Type(e);
            var index = e["contentIndex"] is JsonValue value && value.TryGetValue<int>(out var at) ? at : -1;
            if (index < 0) continue;
            string? kind = type switch
            {
                "text_start" or "text_delta" or "text_end" => "text",
                "thinking_start" or "thinking_delta" or "thinking_end" => "thinking",
                "toolcall_start" or "toolcall_end" => "toolCall",
                _ => null,
            };
            if (kind is null) continue;
            if (!blocks.TryGetValue(index, out var block)) blocks[index] = block = (kind, new StringBuilder(), null, null);
            switch (type)
            {
                case "text_delta" or "thinking_delta":
                    if (block.Text.Length < RunEngine.MaxPartialChars) block.Text.Append(Str(e, "delta"));
                    break;
                case "text_end" or "thinking_end" when e["content"] is JsonValue:
                    block.Text.Clear().Append(Str(e, "content"));
                    break;
                case "toolcall_start" or "toolcall_end" when e["toolCall"] is JsonObject call:
                    blocks[index] = (kind, new StringBuilder(call["arguments"] is { } arguments ? RequestJson.Indented(arguments) : ""), Str(call, "id"), Str(call, "name"));
                    break;
            }
        }
        return [.. blocks.Values.Select(block => new RunOutputBlock(block.Kind, block.Text.ToString(), block.Id, block.Name,
            block.Kind == "toolCall" ? Summary(block.Text.ToString()) : null))];
    }

    private static RunOutputBlock? Block(JsonObject content) => Str(content, "type") switch
    {
        "text" => new RunOutputBlock("text", Str(content, "text")),
        "thinking" => new RunOutputBlock("thinking", Str(content, "thinking")),
        "toolCall" => new RunOutputBlock("toolCall", content["arguments"] is { } arguments ? RequestJson.Indented(arguments) : "{}", Str(content, "id"), Str(content, "name"),
            Summary(content["arguments"])),
        _ => null,
    };

    private static string? Summary(string json)
    {
        try { return Summary(JsonNode.Parse(json)); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Summary(JsonNode? arguments)
    {
        var first = arguments is JsonObject values ? values.Select(pair => pair.Value).FirstOrDefault() : null;
        var text = first is JsonValue value && value.TryGetValue<string>(out var str) ? str : first is null ? null : RequestJson.Compact(first);
        return text is null ? null : text.Length > 80 ? text[..80] + "…" : text;
    }

    /// <summary>The row of one event.</summary>
    public static RunEventRow Row(RunEventRecord item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var e = item.Event as JsonObject;
        var type = Type(item.Event);
        var index = e?["contentIndex"] is JsonValue value && value.TryGetValue<int>(out var at) ? "#" + at.ToString(CultureInfo.InvariantCulture) : "";
        string data;
        switch (type)
        {
            case "start":
                var partial = e?["partial"] as JsonObject;
                data = string.Join(" · ", new[] { Str(partial, "provider"), Str(partial, "model"), Str(partial, "api") }.Where(part => part.Length > 0));
                break;
            case "text_start" or "thinking_start":
                data = index;
                break;
            case "text_delta" or "thinking_delta" or "toolcall_delta":
                data = Quote(Str(e, "delta"));
                break;
            case "text_end" or "thinking_end":
                data = $"{index} · {Str(e, "content").Length:N0} chars";
                break;
            case "toolcall_start" or "toolcall_provisional_start":
                var call = e?["toolCall"] as JsonObject;
                data = string.Join(" · ", new[] { index, Str(call, "name"), Str(call, "id") }.Where(part => part.Length > 0));
                break;
            case "toolcall_end":
                var ended = e?["toolCall"] as JsonObject;
                data = $"{index} · {Str(ended, "name")} {RequestJson.Compact(ended?["arguments"])}";
                break;
            case "done" or "error":
                var message = e?["message"] as JsonObject;
                var usage = message?["usage"] as JsonObject;
                var parts = new List<string> { Str(message, "stopReason") is { Length: > 0 } stop ? stop : Str(e, "reason") };
                if (usage is not null)
                    parts.Add($"{Num(usage, "input")} in · {Num(usage, "output")} out · {Num(usage, "cacheRead")} cache read");
                if (Str(message, "errorMessage") is { Length: > 0 } error) parts.Add(error);
                data = string.Join(" · ", parts.Where(part => part.Length > 0));
                break;
            default:
                data = index;
                break;
        }
        return new RunEventRow(item.AtMs, type, data.Length > 300 ? data[..300] + "…" : data, Group(type));
    }

    private static string Group(string type) =>
        type.StartsWith("thinking", StringComparison.Ordinal) ? "thinking"
        : type.StartsWith("text", StringComparison.Ordinal) ? "text"
        : type.StartsWith("toolcall", StringComparison.Ordinal) ? "toolcall"
        : "other";

    private static string Type(JsonNode? e) => e is JsonObject json ? Str(json, "type") : "";

    private static string Str(JsonObject? json, string name) => json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string Num(JsonObject json, string name) => json[name] is JsonValue value && value.TryGetValue<double>(out var number)
        ? number.ToString("N0", CultureInfo.InvariantCulture) : "0";

    private static string Quote(string text)
    {
        var shown = text.Length > 60 ? text[..60] + "…" : text;
        return "\"" + shown.Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
    }

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Count(double tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);
}
