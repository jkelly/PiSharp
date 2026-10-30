// Decision 0011 (run W4, R2): the models of the Compare partials (Views/Compare: Index, _ComparePrompt, _CompareColumn, _CompareMetrics,
// _CompareModeSwitch, and R6's _AgentMode). A column shows one run of runs.db through the Playground's RunOutputView (its state, answer
// blocks and cost text), so Compare never computes a figure the run did not record: time to first token, duration, tokens per second,
// usage, the ledger's cost and the stop reason are the row's own. The chips under a column's model are its overrides and how the shared
// options were applied (ComparePlanner); the run's own chips (best, fastest, the stop reason, streaming, error) follow its state.
using System.Globalization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Compare;

/// <summary>The model of <c>Views/Compare/Index.cshtml</c>.</summary>
public sealed record ComparePage
{
    /// <summary>Request mode.</summary>
    public const string RequestMode = "request";

    /// <summary>Agent mode (R6's <c>_AgentMode</c>).</summary>
    public const string AgentModeName = "agent";

    /// <summary><see cref="RequestMode"/> or <see cref="AgentModeName"/>.</summary>
    public string Mode { get; init; } = RequestMode;

    /// <summary>Whether the page shows agent mode.</summary>
    public bool AgentMode => Mode == AgentModeName;

    /// <summary>The shared request and the columns.</summary>
    public CompareDraft Draft { get; init; } = new();

    /// <summary>The group shown (<c>?group=</c>), or null for a new comparison.</summary>
    public string? GroupId { get; init; }

    /// <summary>The columns.</summary>
    public IReadOnlyList<CompareColumnView> Columns { get; init; } = [];

    /// <summary>The metrics tab.</summary>
    public CompareMetricsView Metrics { get; init; } = new();

    /// <summary>The estimate by Run all.</summary>
    public RunEstimateView Estimate { get; init; } = RunEstimateView.None;

    /// <summary>Why Run all cannot send the draft, or null.</summary>
    public string? Message { get; init; }

    /// <summary>A notice above the columns (a group that is gone), or null.</summary>
    public string? Notice { get; init; }

    /// <summary>The model picker (#modal-model-picker).</summary>
    public ModelPickerView Picker { get; init; } = new(new Shell.ProviderModelSelect("mp"));

    /// <summary>Whether a column streams.</summary>
    public bool Running => Columns.Any(column => column.Streaming);

    /// <summary>The thinking levels the shared control offers (the run engine's; each column clamps them to its model's).</summary>
    public IReadOnlyList<string> ThinkingLevels => RunSpec.ThinkingLevels;

    /// <summary>The top bar's subtitle.</summary>
    public string Subtitle => AgentMode ? "Agent · one task, several models"
        : Columns.Count == 1 ? "1 model · one shared prompt" : $"{Columns.Count} models · one shared prompt";
}

/// <summary>The model of <c>_CompareColumn</c>: one column, empty or showing a run.</summary>
public sealed record CompareColumnView
{
    /// <summary>The element id part (<c>c1</c>...).</summary>
    public required string Key { get; init; }

    /// <summary>The model and overrides.</summary>
    public required CompareColumnDraft Column { get; init; }

    /// <summary>The shared request (for the overrides' "inherit" values).</summary>
    public CompareDraft Shared { get; init; } = new();

    /// <summary>What the model takes, or null when it is not a model any more.</summary>
    public RunCapabilities? Capabilities { get; init; }

    /// <summary>The model's display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>How the provider bills.</summary>
    public BillingKind Billing { get; init; } = BillingKind.Unknown;

    /// <summary>The overrides and how the shared options apply to this model.</summary>
    public IReadOnlyList<CompareChip> Chips { get; init; } = [];

    /// <summary>The group the run belongs to, or null.</summary>
    public string? GroupId { get; init; }

    /// <summary>The run shown (empty when the column has not run).</summary>
    public RunOutputView Output { get; init; } = new();

    /// <summary>The failed run's details, or null.</summary>
    public ErrorDetailsView? Error { get; init; }

    /// <summary>Whether the run is marked best.</summary>
    public bool Best { get; init; }

    /// <summary>Whether the run finished first of the group's completed columns.</summary>
    public bool Fastest { get; init; }

    /// <summary>Whether Remove is offered (more than the fewest columns).</summary>
    public bool CanRemove { get; init; } = true;

    /// <summary>A thinking override the model does not list, kept for when the column switches back to a model that does.</summary>
    public string? KeptThinking { get; init; }

    /// <summary>empty, streaming, completed, stopped or error.</summary>
    public string State => Output.State;

    /// <summary>The run, or null.</summary>
    public RunRecord? Run => Output.Run;

    /// <summary>Whether the run streams.</summary>
    public bool Streaming => State == "streaming";

    /// <summary>Whether the model is known.</summary>
    public bool Known => Capabilities is not null;

    /// <summary>The model's thinking levels, off first.</summary>
    public IReadOnlyList<string> Levels => Capabilities?.ThinkingLevels ?? ["off"];

    /// <summary>Whether the model takes a temperature (with thinking off, or on).</summary>
    public bool TemperatureTaken => Capabilities?.Find(RunModelOptions.Temperature) is { } support && (support.Supported || support.WithThinking == true);

    /// <summary>The override panel's note: why temperature is not taken, or how thinking is taken.</summary>
    public string? OverrideNote
    {
        get
        {
            if (Capabilities is not { } capabilities) return "This model is not available; choose another.";
            var notes = new List<string>();
            var temperature = capabilities.Find(RunModelOptions.Temperature);
            if (temperature is { Supported: false, WithThinking: not true }) notes.Add("Temperature isn't sent to this model" + (temperature.Reason is { } why ? ": " + why + "." : "."));
            else if (temperature?.Reason is { } note) notes.Add("Temperature: " + note + ".");
            notes.Add(Levels.Count <= 1 ? "This model doesn't think." : $"Thinks at {string.Join(", ", Levels.Skip(1))}.");
            return string.Join(" ", notes);
        }
    }

    /// <summary>The shared thinking level as "inherit (…)" shows it.</summary>
    public string SharedThinking => Shared.Thinking ?? "off";

    /// <summary>The shared temperature as "inherit (…)" shows it.</summary>
    public string SharedTemperature => Shared.Temperature?.ToString("0.##", CultureInfo.InvariantCulture) ?? "provider default";

    /// <summary>The thinking level the run sent, or null.</summary>
    public string? SentThinking => Run?.Options?["thinking"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var level) && level != "off" ? level : null;

    /// <summary>The footer under the column: time to first token, total, tokens and cost, as the run recorded them.</summary>
    public IReadOnlyList<RunMetric> Foot
    {
        get
        {
            if (Run is not { } run) return [];
            var cost = Output.Cost;
            var costText = cost.Unit is { } unit ? cost.Text + " " + unit : cost.Text;
            var tokens = $"{Count(run.Usage.Input)} → {Count(run.Usage.Output)} tok";
            switch (State)
            {
                case "streaming":
                    return
                    [
                        new RunMetric("ttft", run.TtftMs is { } live ? "TTFT " + Seconds(live, "0.00") + " s" : "TTFT …"),
                        new RunMetric("elapsed", "streaming…", Tone: "text-ps-blue"),
                    ];
                case "error":
                    return
                    [
                        new RunMetric("failed", run.DurationMs is { } failed ? "failed after " + Seconds(failed, "0.00") + " s" : "failed", Tone: "text-ps-danger"),
                        new RunMetric("tokens", run.Usage.Total > 0 ? tokens : "0 tok"),
                        new RunMetric("cost", costText, Title: cost.Title),
                    ];
                default:
                    var items = new List<RunMetric>();
                    if (run.TtftMs is { } ttft) items.Add(new RunMetric("ttft", "TTFT " + Seconds(ttft, "0.00") + " s"));
                    if (run.DurationMs is { } total) items.Add(new RunMetric("total", "total " + Seconds(total, "0.0") + " s"));
                    items.Add(new RunMetric("tokens", tokens));
                    items.Add(new RunMetric("cost", costText, Title: cost.Title));
                    if (State == "stopped") items.Add(new RunMetric("stopped", "stopped", Tone: "text-ps-warn"));
                    return items;
            }
        }
    }

    /// <summary>The run's stop reason as its chip shows it, or null.</summary>
    public string? StopChip => State switch
    {
        "completed" => Run?.StopReason ?? "stop",
        _ => null,
    };

    /// <summary>The error chip (<c>401 · error</c>, <c>error</c>, <c>interrupted</c>), or null.</summary>
    public string? ErrorChip => State != "error" ? null
        : Run?.Status == RunStatus.Interrupted ? "interrupted"
        : Error?.HttpStatus is { Length: > 0 } status ? status.Split(' ')[0] + " · error" : "error";

    /// <summary>The error card's heading.</summary>
    public string ErrorTitle => Run?.Status == RunStatus.Interrupted || Output.SpendError is not null ? Output.ErrorTitle
        : Error?.HttpStatus is { Length: > 0 } status ? "Provider error · " + status : "Provider error";

    /// <summary>Whether the error is the account's (an expired or missing key): the card offers Fix (Settings › Providers).</summary>
    public bool AccountError => Routing.AccountRouteErrors.IsAccountError(Run?.Error);

    internal static string Seconds(long ms, string format) => (ms / 1000.0).ToString(format, CultureInfo.InvariantCulture);

    internal static string Count(double value) => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>One cell of the metrics table.</summary>
/// <param name="Text">The figure, or an em dash.</param>
/// <param name="Note">A faint note after it (<c>override</c>, <c>list · subscription</c>), or null.</param>
/// <param name="Best">Whether it is the best of its row (green).</param>
/// <param name="Tone">A text class, or null.</param>
/// <param name="Chip">A chip tone: the figure shows as a chip (streaming, error), or null.</param>
public sealed record CompareMetricCell(string Text, string? Note = null, bool Best = false, string? Tone = null, string? Chip = null);

/// <summary>One row of the metrics table.</summary>
/// <param name="Label">TTFT, Total time...</param>
/// <param name="Cells">One per column.</param>
public sealed record CompareMetricRow(string Label, IReadOnlyList<CompareMetricCell> Cells);

/// <summary>The status line above the columns.</summary>
/// <param name="Text">What it says.</param>
/// <param name="Tone">faint, live or danger.</param>
public sealed record CompareStatusLine(string Text, string Tone = "faint");

/// <summary>The model of <c>_CompareMetrics</c>: the Metrics tab of a group.</summary>
public sealed record CompareMetricsView
{
    /// <summary>The group, or null before a run.</summary>
    public string? GroupId { get; init; }

    /// <summary>The columns.</summary>
    public IReadOnlyList<CompareColumnView> Columns { get; init; } = [];

    /// <summary>The columns that ran (the table's columns).</summary>
    public IReadOnlyList<CompareColumnView> Ran => [.. Columns.Where(column => column.Run is not null)];

    /// <summary>The column marked best, or null.</summary>
    public CompareColumnView? Best => Columns.FirstOrDefault(column => column.Best);

    /// <summary>A column's inspector page, for "Inspect a column's request".</summary>
    public string? InspectUrl => Ran.FirstOrDefault()?.Output.InspectorUrl;

    /// <summary>The status line.</summary>
    public CompareStatusLine Status
    {
        get
        {
            var ran = Ran;
            if (ran.Count == 0) return new CompareStatusLine("Not run yet");
            var streaming = ran.Count(column => column.Streaming);
            if (streaming > 0) return new CompareStatusLine($"{streaming} streaming", "live");
            var done = ran.Count(column => column.State == "completed");
            var failed = ran.Where(column => column.State == "error").ToList();
            var stopped = ran.Count(column => column.State == "stopped");
            if (failed.Count > 0)
            {
                var which = string.Join(", ", failed.Select(column => column.Column.Provider + (column.Error?.HttpStatus is { Length: > 0 } status ? " " + status.Split(' ')[0] : "")));
                return new CompareStatusLine($"{done} done · {failed.Count} failed ({which})" + (stopped > 0 ? $" · {stopped} stopped" : ""), "danger");
            }
            var parts = new List<string> { $"{done} of {ran.Count} done" };
            if (stopped > 0) parts.Add($"{stopped} stopped");
            if (Columns.FirstOrDefault(column => column.Fastest) is { } fastest) parts.Add("fastest " + fastest.Name);
            if (Best is { } best) parts.Add("best " + best.Name);
            return new CompareStatusLine(string.Join(" · ", parts));
        }
    }

    /// <summary>The table's rows: TTFT, total time, tokens in and out, tokens per second, cost, stop reason and thinking. Best per row
    /// (lowest, or highest for tokens per second) among the completed columns, when at least two have the figure and not all are equal.</summary>
    public IReadOnlyList<CompareMetricRow> Rows
    {
        get
        {
            var ran = Ran;
            CompareMetricRow Row(string label, Func<CompareColumnView, double?> value, Func<CompareColumnView, CompareMetricCell> cell, bool higher = false)
            {
                var values = ran.Select(column => column.State == "completed" ? value(column) : null).ToList();
                var known = values.OfType<double>().ToList();
                // A tie between every column marks none (nothing stands out).
                double? best = known.Count >= 2 && known.Distinct().Count() > 1 ? higher ? known.Max() : known.Min() : null;
                return new CompareMetricRow(label, [.. ran.Select((column, index) =>
                {
                    var shown = cell(column);
                    return best is { } top && values[index] == top ? shown with { Best = true } : shown;
                })]);
            }
            static CompareMetricCell Live(CompareColumnView column, CompareMetricCell done) =>
                column.Streaming ? new CompareMetricCell("…", Tone: "text-ps-blue") : done;
            return
            [
                Row("TTFT", column => column.Run!.TtftMs, column => Live(column, column.Run!.TtftMs is { } ms ? new CompareMetricCell(CompareColumnView.Seconds(ms, "0.00") + " s") : new CompareMetricCell("—"))),
                Row("Total time", column => column.Run!.DurationMs, column => column.Streaming ? new CompareMetricCell("streaming…", Tone: "text-ps-blue")
                    : column.Run!.DurationMs is { } ms
                        ? new CompareMetricCell(CompareColumnView.Seconds(ms, "0.0") + " s" + (column.State == "error" ? " · failed" : ""), Tone: column.State == "error" ? "text-ps-danger" : null)
                        : new CompareMetricCell("—")),
                Row("Tokens in", column => column.Run!.Usage.Input > 0 ? column.Run.Usage.Input : null,
                    column => Live(column, column.Run!.Usage.Input > 0 ? new CompareMetricCell(CompareColumnView.Count(column.Run.Usage.Input)) : new CompareMetricCell("—"))),
                Row("Tokens out", column => column.Run!.Usage.Output > 0 ? column.Run.Usage.Output : null,
                    column => Live(column, column.Run!.Usage.Output > 0 ? new CompareMetricCell(CompareColumnView.Count(column.Run.Usage.Output)) : new CompareMetricCell("—"))),
                Row("Tokens/s", column => column.Run!.TokensPerSecond, column => Live(column, column.Run!.TokensPerSecond is { } rate
                    ? new CompareMetricCell(rate.ToString("0.0", CultureInfo.InvariantCulture)) : new CompareMetricCell("—")), higher: true),
                Row("Cost", column => column.Run!.CostMicros, column =>
                {
                    if (column.Streaming || column.Run!.CostMicros is null) return new CompareMetricCell("—");
                    var cost = column.Output.Cost;
                    var note = column.Run.Billing == "subscription" ? "list · subscription" : cost.Unit;
                    return new CompareMetricCell(cost.Text, note);
                }),
                Row("stopReason", _ => null, column => column.State switch
                {
                    "streaming" => new CompareMetricCell("streaming", Chip: "info"),
                    "error" => new CompareMetricCell(column.Run!.Status == RunStatus.Interrupted ? "interrupted" : "error", Chip: "danger"),
                    "stopped" => new CompareMetricCell("stopped", Chip: "warn"),
                    _ => new CompareMetricCell(column.Run!.StopReason ?? "—"),
                }),
                Row("Thinking", _ => null, column => new CompareMetricCell(column.SentThinking ?? "off", column.Column.Thinking is not null ? "override" : null)),
            ];
        }
    }
}
