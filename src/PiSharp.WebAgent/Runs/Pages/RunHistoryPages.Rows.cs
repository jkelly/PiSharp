// Decision 0011 (run W4, R3): History › Runs' rows and totals strip. A grouped run (an agent compare run) is one row: its tokens cell counts
// its models' turns (their recorded calls), its cost sums those calls (metered, and subscription use at list price apart), its latency is
// how long the run took, and its stop chip says how many models finished; the collapse row under it lists each model with its outcome line
// as the run recorded it (turns, check, diff) and links to the model's own row in the inspector.
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

public sealed partial class RunHistoryPages
{
    private sealed record CallSums(long Calls, long Metered, long List, double Input, double Output);

    internal static IReadOnlyList<RunHistoryRow> RowsOf(WebAgentDb db, IReadOnlyList<(RunRecord Run, string? ToolCalls)> records, DateTimeOffset now)
    {
        var groups = records.Where(row => row.Run.Kind == RunKind.Parent).Select(row => row.Run.Id).ToList();
        var children = new Dictionary<string, List<RunRecord>>(StringComparer.Ordinal);
        var sums = new Dictionary<string, CallSums>(StringComparer.Ordinal);
        if (groups.Count > 0)
        {
            var (set, parameters) = InSet("$g", groups);
            foreach (var (child, _) in db.Query($"SELECT {RowColumns} FROM runs r WHERE r.parent_id IN ({set}) ORDER BY r.created, r.id;", ReadRow, parameters))
            {
                if (!children.TryGetValue(child.ParentId!, out var list)) children[child.ParentId!] = list = [];
                list.Add(child);
            }
            var nested = children.Values.SelectMany(list => list).Where(child => child.Kind == RunKind.Parent).Select(child => child.Id).ToList();
            if (nested.Count > 0)
            {
                var (inner, innerParameters) = InSet("$m", nested);
                foreach (var (parent, sum) in db.Query($"""
                    SELECT parent_id, COUNT(*), COALESCE(SUM(CASE WHEN billing = 'subscription' THEN 0 ELSE cost_micros END), 0),
                        COALESCE(SUM(CASE WHEN billing = 'subscription' THEN cost_micros ELSE 0 END), 0), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0)
                    FROM runs WHERE kind = 'call' AND parent_id IN ({inner}) GROUP BY parent_id;
                    """, reader => (reader.GetString(0), new CallSums(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetDouble(5))), innerParameters))
                    sums[parent] = sum;
            }
        }
        return [.. records.Select(row => row.Run.Kind == RunKind.Parent
            ? GroupRow(row.Run, children.GetValueOrDefault(row.Run.Id) ?? [], sums, now)
            : CallRow(row.Run, row.ToolCalls, now))];
    }

    private static RunHistoryRow CallRow(RunRecord run, string? toolCalls, DateTimeOffset now)
    {
        var source = Source(run.Source);
        var sub = new List<string>();
        if (Str(run.Options, "thinking") is { } thinking && thinking != "off") sub.Add("thinking " + thinking);
        if (run.Options?["toolNames"] is JsonArray tools && tools.Count > 0) sub.Add(tools.Count + (tools.Count == 1 ? " tool" : " tools"));
        if (run.Pinned) sub.Add("pinned");
        var running = run.Status == RunStatus.Running;
        var status = run.Status == RunStatus.Error ? RunHistoryText.HttpStatus(run.Error) : null;
        return new RunHistoryRow
        {
            Run = run,
            Time = RunHistoryText.Time(run.Created),
            Day = RunHistoryText.Day(run.Created, now),
            SourceLabel = source.Label,
            SourceIcon = source.Icon,
            ModelSub = string.Join(" · ", sub),
            Prompt = run.Title ?? RunText.Title(run.OutputText) ?? "(no prompt text)",
            ToolCalls = string.IsNullOrWhiteSpace(toolCalls) ? null : toolCalls,
            Sub = run.Id + (run.Session is { Length: > 0 } session ? " · session " + Short(session) : run.GroupId is { Length: > 0 } group ? " · group " + Short(group) : ""),
            Tokens = running ? new RunCell("—", "—", "Known when the run ends.", Faint: true)
                : new RunCell(RunHistoryText.Tokens(run.Usage.Input), run.Usage.Output == 0 && run.Status == RunStatus.Error ? "—" : RunHistoryText.Tokens(run.Usage.Output),
                    run.Usage.CacheRead > 0 ? RunHistoryText.Tokens(run.Usage.CacheRead) + " input tokens read from the cache" : null),
            Cost = RunHistoryText.Cost(run),
            Latency = new RunCell(run.TtftMs is { } ttft ? RunHistoryText.Seconds(ttft) : "—", run.DurationMs is { } total ? RunHistoryText.Duration(total) : "—",
                "Time to first token (top) / total duration (bottom)"),
            Stop = RunHistoryText.Stop(run),
            StopSub = status is null ? null : "HTTP " + status,
            StopSubDanger = status is not null,
        };
    }

    private static RunHistoryRow GroupRow(RunRecord run, IReadOnlyList<RunRecord> models, Dictionary<string, CallSums> sums, DateTimeOffset now)
    {
        var source = Source(run.Source);
        long metered = 0, list = 0, turns = 0;
        var turnsEach = new List<long>();
        var lines = new List<RunHistoryModel>();
        var finished = 0;
        var checks = 0;
        var passed = 0;
        var index = 0;
        foreach (var model in models)
        {
            index++;
            var sum = model.Kind == RunKind.Parent ? sums.GetValueOrDefault(model.Id) ?? new CallSums(0, 0, 0, 0, 0)
                : new CallSums(1, model.Billing == "subscription" ? 0 : model.CostMicros ?? 0, model.Billing == "subscription" ? model.CostMicros ?? 0 : 0, model.Usage.Input, model.Usage.Output);
            metered += sum.Metered;
            list += sum.List;
            var notes = (model.Notes ?? "").Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var turnNote = notes.FirstOrDefault(note => note.EndsWith(" turns", StringComparison.Ordinal) || note.EndsWith(" turn", StringComparison.Ordinal));
            var modelTurns = turnNote is not null && long.TryParse(turnNote.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var counted) ? counted : sum.Calls;
            turns += modelTurns;
            turnsEach.Add(modelTurns);
            var check = notes.FirstOrDefault(note => note.StartsWith("check ", StringComparison.Ordinal));
            RunChip? chip = null;
            if (check is not null)
            {
                checks++;
                var ok = check.StartsWith("check passed", StringComparison.Ordinal);
                if (ok) passed++;
                chip = new RunChip(check, ok ? "ok" : check.StartsWith("check failed", StringComparison.Ordinal) ? "danger" : "outline");
            }
            if (model.Status == RunStatus.Completed) finished++;
            var parts = new List<string> { model.Status == RunStatus.Running ? "running" : model.StopReason ?? model.Status };
            if (turnNote is null) parts.Add(modelTurns + (modelTurns == 1 ? " turn" : " turns"));
            parts.AddRange(notes.Where(note => !note.StartsWith("check ", StringComparison.Ordinal)));
            if (model.DurationMs is { } ms) parts.Add(RunHistoryText.Duration(ms));
            if (sum.Metered > 0) parts.Add(RunHistoryText.Money(sum.Metered));
            if (sum.List > 0) parts.Add("≈ " + RunHistoryText.Money(sum.List) + " list");
            var letter = Str(model.Options, "slot")?.ToUpperInvariant() ?? (model.Title is { } title && title.IndexOf(" · ", StringComparison.Ordinal) is > 0 and <= 3 and var at
                ? title[..at] : index.ToString(CultureInfo.InvariantCulture));
            var thinking = Str(model.Options, "thinking");
            lines.Add(new RunHistoryModel(letter, model.Provider, model.Model, thinking is null or "off" ? null : "thinking " + thinking, string.Join(" · ", parts), chip,
                "/runs/" + Uri.EscapeDataString(model.Id)));
        }
        RunChip stop;
        if (run.Status == RunStatus.Running) stop = new RunChip("running", "info", "bi-broadcast");
        else if (models.Count == 0) stop = RunHistoryText.Stop(run);
        else stop = new RunChip($"{finished} of {models.Count} finished", finished == models.Count ? "ok" : finished == 0 ? "danger" : "warn");
        RunCell cost;
        if (metered == 0 && list == 0) cost = new RunCell("—", null, "No cost was recorded for its models' calls.", Faint: true);
        else if (metered == 0) cost = new RunCell("≈ " + RunHistoryText.Money(list), "list", "Subscription use at list price, not counted toward spend caps.");
        else cost = new RunCell(RunHistoryText.Money(metered), list > 0 ? "+ ≈ " + RunHistoryText.Money(list) + " list" : null,
            "Metered " + RunHistoryText.Money(metered) + (list > 0 ? " · subscription use ≈ " + RunHistoryText.Money(list) + " at list price, not counted toward spend caps" : ""));
        return new RunHistoryRow
        {
            Run = run,
            Time = RunHistoryText.Time(run.Created),
            Day = RunHistoryText.Day(run.Created, now),
            SourceLabel = source.Label,
            SourceIcon = source.Icon,
            Prompt = run.Title ?? "(no task text)",
            Sub = run.Id + " · " + models.Count + (models.Count == 1 ? " model" : " models"),
            Tokens = new RunCell(turns + (turns == 1 ? " turn" : " turns"), turnsEach.Count > 1 ? string.Join(" · ", turnsEach) : null,
                "Turns per model: " + string.Join(", ", lines.Zip(turnsEach, (line, count) => line.Letter + " " + count)) + ". Token counts are in each model's row."),
            Cost = cost,
            Latency = new RunCell("—", run.DurationMs is { } duration ? RunHistoryText.Duration(duration) : "—", "The models ran side by side; this is how long the run took."),
            Stop = stop,
            StopSub = checks > 0 ? $"{passed} of {checks} checks passed" : null,
            IsGroup = true,
            Models = lines,
        };
    }

    private static RunTotalsView Totals(WebAgentDb db, RunHistoryFilter filter, DateTimeOffset now, long totalAll, IReadOnlyCollection<string> hidden)
    {
        var filtered = filter.Active;
        var (where, parameters) = RootWhere(filter, filtered ? null : now.AddDays(-30), hidden);
        var calls = $"""
            WITH roots AS (SELECT r.id FROM runs r{where}),
            calls AS (SELECT c.* FROM runs c WHERE c.kind = 'call' AND (c.id IN (SELECT id FROM roots) OR c.parent_id IN (SELECT id FROM roots)
                OR c.parent_id IN (SELECT m.id FROM runs m WHERE m.parent_id IN (SELECT id FROM roots))))
            """;
        var roots = db.Query($"SELECT COUNT(*), COALESCE(SUM(CASE WHEN r.status = 'error' THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN r.created >= $week THEN 1 ELSE 0 END), 0) FROM runs r{where};",
            reader => (Count: reader.GetInt64(0), Errors: reader.GetInt64(1), Week: reader.GetInt64(2)), [.. parameters, ("$week", RunStore.Ms(now.AddDays(-7)))]).Single();
        var sums = db.Query($"""
            {calls}
            SELECT COUNT(*), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), COALESCE(SUM(cache_read_tokens), 0),
                COALESCE(SUM(CASE WHEN billing = 'subscription' THEN 0 ELSE cost_micros END), 0), COALESCE(SUM(CASE WHEN billing = 'subscription' THEN cost_micros ELSE 0 END), 0),
                AVG(ttft_ms), COUNT(ttft_ms), AVG(tokens_per_second)
            FROM calls;
            """,
            reader => (Calls: reader.GetInt64(0), Input: reader.GetDouble(1), Output: reader.GetDouble(2), Cache: reader.GetDouble(3), Metered: reader.GetInt64(4), List: reader.GetInt64(5),
                Ttft: reader.IsDBNull(6) ? (double?)null : reader.GetDouble(6), TtftCount: reader.GetInt64(7), Rate: reader.IsDBNull(8) ? (double?)null : reader.GetDouble(8)),
            [.. parameters]).Single();
        double? p95 = null;
        if (sums.TtftCount > 0)
            p95 = db.Query($"{calls} SELECT ttft_ms FROM calls WHERE ttft_ms IS NOT NULL ORDER BY ttft_ms LIMIT 1 OFFSET $at;", reader => reader.GetDouble(0),
                [.. parameters, ("$at", (long)Math.Floor(0.95 * (sums.TtftCount - 1)))]).FirstOrDefault();
        var share = db.Query($"SELECT r.source, COUNT(*) FROM runs r{where} GROUP BY r.source ORDER BY COUNT(*) DESC, r.source;",
            reader => (Source: reader.GetString(0), Count: reader.GetInt64(1)), [.. parameters]);
        var errorCodes = roots.Errors == 0 ? [] : db.Query($"SELECT r.error FROM runs r{where} AND r.status = 'error' ORDER BY r.created DESC LIMIT 500;",
            reader => reader.IsDBNull(0) ? null : RunHistoryText.HttpStatus(reader.GetString(0)), [.. parameters]).OfType<string>()
            .GroupBy(code => code).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(2).ToList();

        var empty = roots.Count == 0;
        string Dash(string text) => empty ? "—" : text;
        var total = sums.Metered + sums.List;
        var rate = roots.Count == 0 ? (double?)null : roots.Errors * 100.0 / roots.Count;
        var metrics = new List<RunTotalMetric>
        {
            new("Runs", RunHistoryText.Tokens(roots.Count), null, Dash(filtered ? "of " + RunHistoryText.Tokens(totalAll) : "+" + RunHistoryText.Tokens(roots.Week) + " this week")),
            new("Tokens in", RunHistoryText.Compact(sums.Input), null, Dash(RunHistoryText.Compact(sums.Cache) + " from cache")),
            new("Tokens out", RunHistoryText.Compact(sums.Output), null,
                Dash(sums.Output == 0 ? "no output" : sums.Rate is { } speed ? "avg " + speed.ToString("0", CultureInfo.InvariantCulture) + " tok/s" : RunHistoryText.Tokens(sums.Calls) + " calls")),
            new("Est. cost", RunHistoryText.Money(total), null,
                Dash(sums.List > 0 ? RunHistoryText.Money(sums.Metered) + " metered" : total == 0 ? "nothing metered" : "all metered"),
                sums.List > 0 ? "The rest (≈ " + RunHistoryText.Money(sums.List) + ") is subscription use at list price, which Spend Guard does not count toward money caps." : null),
            new("Avg TTFT", sums.Ttft is { } ttft ? (ttft / 1000).ToString("0.00", CultureInfo.InvariantCulture) : "—", sums.Ttft is null ? null : "s",
                Dash(p95 is { } slow ? "p95 " + (slow / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" : "no first token")),
            new("Error rate", rate is { } percent ? percent.ToString(percent is > 0 and < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%" : "—", null,
                Dash(RunHistoryText.Tokens(roots.Errors) + (roots.Errors == 1 ? " run" : " runs") + string.Concat(errorCodes.Select(group => " · " + group.Count() + " × " + group.Key))),
                Tone: rate is null or 0 ? null : rate >= 50 ? "text-ps-danger" : "text-ps-warn"),
        };
        string? shareText = null;
        if (!empty && share.Count > 0)
            shareText = string.Join(" · ", share.Select(item =>
            {
                var percent = item.Count * 100.0 / roots.Count;
                return Source(item.Source).Label + " " + (percent < 1 ? "<1" : Math.Round(percent).ToString("0", CultureInfo.InvariantCulture)) + "%";
            }));
        return new RunTotalsView(filtered, shareText, metrics);
    }

    private static (string Label, string Icon) Source(string wire) =>
        SpendSources.TryParse(wire, out var source) ? (source.Label(), SpendPages.SourceIcon(source)) : (wire, "bi-question-circle");

    private static string? Str(JsonObject? json, string name) => json?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Short(string id) => id.Length > 12 ? id[..12] + "…" : id;

    private static (string Set, (string Name, object? Value)[] Parameters) InSet(string prefix, IReadOnlyList<string> values)
    {
        var names = values.Select((_, index) => prefix + index.ToString(CultureInfo.InvariantCulture)).ToList();
        return (string.Join(", ", names), [.. names.Zip(values, (name, value) => (name, (object?)value))]);
    }
}
