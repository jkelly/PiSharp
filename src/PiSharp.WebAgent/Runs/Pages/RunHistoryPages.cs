// Decision 0011 (run W4, R3): History › Runs (GET /history) over runs.db. A row of the page is a top-level run (parent_id IS NULL): a
// provider call (Playground, Compare, Tool Bench, a chat's call) or a run that groups calls (an agent compare run, whose models are its
// child rows and whose models' turns are their children). RunStore.List filters exact columns of every row, so the page reads runs.db with
// its own queries (read only, through RunStore.Db after the store has opened it): the filters apply to the top-level row, and Provider and
// Model also match a grouped run when one of its models does. The totals strip sums the calls under the matching runs (the last 30 days
// when no filter is set). Export writes the matching (or chosen) runs with their grouped rows as CSV or JSON (with the stored stream events
// and raw payloads when asked); raw payloads were redacted before they were stored, and nothing here adds a credential.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

/// <summary>A file to download: its name, media type and bytes.</summary>
public sealed record RunExportFile(string Name, string ContentType, byte[] Content);

/// <summary>History › Runs' page model, exports and re-run requests. A singleton.</summary>
public sealed partial class RunHistoryPages(IServiceProvider services)
{
    /// <summary>The most top-level runs one export writes (newest first).</summary>
    public const int MaxExport = 5_000;

    /// <summary>The most top-level runs one export with raw payloads writes.</summary>
    public const int MaxRawExport = 500;

    /// <summary>The most ids an export or delete of chosen runs takes.</summary>
    public const int MaxIds = RunStore.MaxDelete;

    /// <summary>The export formats: <c>csv</c>, <c>json</c> and <c>json-raw</c> (JSON with stream events and raw payloads).</summary>
    public static IReadOnlyList<string> ExportFormats { get; } = ["csv", "json", "json-raw"];

    internal const string RowColumns = "r.id, r.op, r.kind, r.source, r.status, r.provider, r.model, r.api, r.account, r.group_id, r.parent_id, r.session, " +
        "r.title, r.messages_ref, r.output_text, r.stop_reason, r.error, r.notes, r.input_tokens, r.output_tokens, r.cache_read_tokens, r.cache_write_tokens, " +
        "r.total_tokens, r.cost_micros, r.cost_kind, r.billing, r.spend_op, r.ttft_ms, r.duration_ms, r.tokens_per_second, r.pinned, r.raw_state, r.created, " +
        "r.started, r.finished, r.updated, r.options, " +
        "CASE WHEN r.output IS NOT NULL AND json_valid(r.output) THEN (SELECT group_concat(json_extract(j.value, '$.name'), ', ') " +
        "FROM json_each(r.output, '$.content') j WHERE json_extract(j.value, '$.type') = 'toolCall') END";

    /// <summary>The run store.</summary>
    public RunStore Store => services.GetRequiredService<RunStore>();

    private DateTimeOffset Now => (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();

    /// <summary>The page of <paramref name="filter"/>.</summary>
    public RunHistoryPage Page(RunHistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var store = Store;
        store.Count(); // Opens the store first: migrations, and runs a stopped app left running marked interrupted.
        var db = store.Db.Db;
        var now = Now;
        // The plain counts are the store's (RunQuery.TopLevelOnly); the filtered rows below stay this page's own query, since a provider or
        // model filter also matches a run through its children and a row carries its options and tool names, which RunStore.List leaves out.
        var totalAll = store.Totals(new RunQuery { TopLevelOnly = true }).Count;
        var blind = ArenaBlindSet.Of(services);
        var (where, parameters) = RootWhere(filter, since: null, blind);
        var total = Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM runs r{where};", [.. parameters]) ?? 0L, CultureInfo.InvariantCulture);
        var pages = total == 0 ? 1 : (int)Math.Ceiling(total / (double)filter.Size);
        if (filter.Page > pages) filter = filter with { Page = pages };
        var records = db.Query($"SELECT {RowColumns} FROM runs r{where} ORDER BY r.created DESC, r.id DESC LIMIT $limit OFFSET $offset;", ReadRow,
            [.. parameters, ("$limit", filter.Size), ("$offset", (filter.Page - 1) * filter.Size)]);
        var hidden = blind.RunSql("r");
        var settings = services.GetService<ISettingsStore>();
        var history = settings?.Get<RunHistorySettings>() ?? RunHistorySettings.CreateDefault();
        var saved = settings?.Get<RunSavedFilters>() ?? RunSavedFilters.CreateDefault();
        var current = filter.QueryString(paging: false).TrimStart('?');
        return new RunHistoryPage
        {
            Filter = filter,
            Rows = RowsOf(db, records, now, blind),
            Total = total,
            TotalAll = totalAll,
            Totals = Totals(db, filter, now, totalAll, blind),
            // A blind battle's runs name no provider or model here, not even as a filter choice.
            Providers = Options(db, $"SELECT DISTINCT r.provider FROM runs r WHERE r.provider <> '' AND NOT {hidden.Sql} ORDER BY r.provider;", hidden.Parameters, filter.Provider),
            Models = filter.Provider is null ? []
                : Options(db, $"SELECT DISTINCT r.model FROM runs r WHERE r.provider = $provider AND r.model <> '' AND NOT {hidden.Sql} ORDER BY r.model;",
                    [("$provider", filter.Provider), .. hidden.Parameters], filter.Model),
            Saved = [.. (saved.Filters ?? []).Select(item => new RunSavedFilterView(item.Name, item.Query, item.Href, RunSavedFilters.Describe(item.Query), Icon(item.Query),
                item.Query == current && current.Length > 0))],
            DbPath = store.Db.Path,
            SizeBytes = store.SizeBytes(),
            RetentionText = history.RetentionDays is { } days ? "retention " + days.ToString(CultureInfo.InvariantCulture) + (days == 1 ? " day" : " days") : "kept forever",
            PinnedCount = store.Totals(new RunQuery { TopLevelOnly = true, Pinned = true }).Count,
        };
    }

    /// <summary>The request of <paramref name="id"/> to send again (Re-run): its options and messages as stored, under its own source
    /// (playground, compare or bench; the group and parent left out). Null when the run does not exist or did not store its request (a chat's
    /// call, an agent compare turn).</summary>
    public JsonObject? RerunSpec(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (Store.Get(id) is not { } run || !RunHistoryText.Replayable(run) || PlaygroundPages.FromRun(run) is not { } spec) return null;
        if (ArenaBlindSet.Of(services).Hides(run)) return null;
        var source = run.Source is "playground" or "compare" or "bench" ? run.Source : "playground";
        var json = JsonSerializer.SerializeToNode(spec with { Source = source, GroupId = null, ParentId = null, Title = run.Title }, SettingsStore.JsonOptions)!.AsObject();
        foreach (var key in new[] { "spendSource", "thinkingLevel" }) json.Remove(key);
        foreach (var key in json.Where(pair => pair.Value is null).Select(pair => pair.Key).ToList()) json.Remove(key);
        return json;
    }

    /// <summary>The runs <paramref name="filter"/> matches (or the runs <paramref name="ids"/> names) as a file in <paramref name="format"/>.</summary>
    /// <exception cref="ArgumentException">The format is not one of <see cref="ExportFormats"/>, or too many ids.</exception>
    public RunExportFile Export(string format, RunHistoryFilter? filter, IReadOnlyList<string>? ids)
    {
        if (!ExportFormats.Contains(format)) throw new ArgumentException("Export as csv, json or json-raw.");
        var store = Store;
        store.Count();
        var db = store.Db.Db;
        var limit = format == "json-raw" ? MaxRawExport : MaxExport;
        // A blind battle's runs are left out until its vote (they would name its models).
        var blind = ArenaBlindSet.Of(services);
        List<string> roots;
        if (ids is not null)
        {
            var chosen = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (chosen.Count > MaxIds) throw new ArgumentException($"Export at most {MaxIds:N0} chosen runs at a time.");
            roots = [.. chosen.Where(id => store.Get(id) is { } run && !blind.Hides(run)).Take(limit)];
        }
        else
        {
            var (where, parameters) = RootWhere(filter ?? new RunHistoryFilter(), since: null, blind, leaveOutBlind: true);
            roots = [.. db.Query($"SELECT r.id FROM runs r{where} ORDER BY r.created DESC, r.id DESC LIMIT $limit;", reader => reader.GetString(0), [.. parameters, ("$limit", limit)])];
        }
        var stamp = Now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (format == "csv") return new RunExportFile($"pisharp-runs-{stamp}.csv", "text/csv; charset=utf-8", Csv(db, roots));
        var raw = format == "json-raw";
        var runs = new JsonArray();
        foreach (var id in roots)
        {
            if (store.Get(id) is not { } run) continue;
            var item = RunJson(store, run, raw);
            var children = Descendants(db, id).Select(child => store.Get(child)).OfType<RunRecord>().Select(child => (JsonNode)RunJson(store, child, raw)).ToArray();
            if (children.Length > 0) item["children"] = new JsonArray(children);
            runs.Add(item);
        }
        var document = new JsonObject
        {
            ["exported"] = Now.ToString("O", CultureInfo.InvariantCulture),
            ["filter"] = ids is null ? (filter ?? new RunHistoryFilter()).QueryString(paging: false) : null,
            ["rawPayloads"] = raw,
            ["count"] = runs.Count,
            ["runs"] = runs,
        };
        var name = $"pisharp-runs-{stamp}{(raw ? "-raw" : "")}.json";
        return new RunExportFile(name, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions(SettingsStore.JsonOptions) { WriteIndented = true })));
    }

    /// <summary>A run as JSON (camelCase), with its stored events and raw payload items when <paramref name="raw"/>.</summary>
    internal static JsonObject RunJson(RunStore store, RunRecord run, bool raw)
    {
        var json = JsonSerializer.SerializeToNode(run, SettingsStore.JsonOptions)!.AsObject();
        if (!raw) return json;
        json["events"] = new JsonArray([.. store.Events(run.Id).Select(item => (JsonNode)new JsonObject { ["seq"] = item.Seq, ["atMs"] = item.AtMs, ["event"] = item.Event.DeepClone() })]);
        json["raw"] = new JsonArray([.. store.Raw(run.Id).Select(item => (JsonNode)new JsonObject { ["seq"] = item.Seq, ["kind"] = item.Kind, ["atMs"] = item.AtMs, ["data"] = item.Data })]);
        return json;
    }

    /// <summary>The ids of the rows under <paramref name="id"/> (its children and theirs), oldest first.</summary>
    internal static IReadOnlyList<string> Descendants(WebAgentDb db, string id) => db.Query(
        "SELECT id FROM runs WHERE parent_id = $id OR parent_id IN (SELECT id FROM runs WHERE parent_id = $id) ORDER BY created, id;", reader => reader.GetString(0), ("$id", id));

    private static readonly string[] CsvColumns =
    [
        "id", "created", "source", "kind", "status", "stop_reason", "provider", "model", "account", "session", "group_id", "parent_id", "title",
        "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens", "cost_usd", "cost_kind", "billing", "ttft_ms", "duration_ms", "error",
    ];

    private static byte[] Csv(WebAgentDb db, IReadOnlyList<string> roots)
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", CsvColumns)).Append("\r\n");
        foreach (var root in roots)
        {
            var ids = new List<string> { root };
            ids.AddRange(Descendants(db, root));
            foreach (var id in ids)
            {
                var found = db.Query($"SELECT {RowColumns} FROM runs r WHERE r.id = $id;", ReadRow, ("$id", id));
                if (found.Count == 0) continue;
                var run = found[0].Run;
                string?[] cells =
                [
                    run.Id, run.Created.ToString("O", CultureInfo.InvariantCulture), run.Source, run.Kind, run.Status, run.StopReason, run.Provider, run.Model,
                    run.Account, run.Session, run.GroupId, run.ParentId, run.Title, Number(run.Usage.Input), Number(run.Usage.Output), Number(run.Usage.CacheRead),
                    Number(run.Usage.CacheWrite), run.CostMicros is { } micros ? (micros / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture) : null,
                    run.CostKind, run.Billing, run.TtftMs?.ToString(CultureInfo.InvariantCulture), run.DurationMs?.ToString(CultureInfo.InvariantCulture), run.Error,
                ];
                text.Append(string.Join(",", cells.Select(CsvCell))).Append("\r\n");
            }
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One CSV cell: quoted when needed, and a leading <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> of text made inert for spreadsheets.</summary>
    internal static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value;
        if (text[0] is '=' or '+' or '@' or '\t' or '\r' || (text[0] == '-' && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) text = "'" + text;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>The WHERE clause (with its parameters) of the top-level runs <paramref name="filter"/> matches, created at or after
    /// <paramref name="since"/> when given. The table is aliased <c>r</c>. The runs of a battle in <paramref name="blind"/> never match a
    /// provider or model filter, nor a search by model, provider or error (so no filter can single out one contestant before the vote), and
    /// are left out entirely with <paramref name="leaveOutBlind"/>.</summary>
    internal static (string Where, List<(string Name, object? Value)> Parameters) RootWhere(RunHistoryFilter filter, DateTimeOffset? since,
        ArenaBlindSet? blind = null, bool leaveOutBlind = false)
    {
        var clauses = new List<string> { "r.parent_id IS NULL" };
        var parameters = new List<(string Name, object? Value)>();
        var (hidden, hiddenParameters) = (blind ?? ArenaBlindSet.Empty).RunSql("r", "$hidden");
        parameters.AddRange(hiddenParameters);
        if (leaveOutBlind || (hiddenParameters.Length > 0 && (filter.Provider is not null || filter.Model is not null))) clauses.Add($"NOT {hidden}");
        if (since is { } start)
        {
            clauses.Add("r.created >= $since");
            parameters.Add(("$since", RunStore.Ms(start)));
        }
        if (filter.FromTime is { } from)
        {
            clauses.Add("r.created >= $from");
            parameters.Add(("$from", RunStore.Ms(from)));
        }
        if (filter.ToTime is { } to)
        {
            clauses.Add("r.created < $to");
            parameters.Add(("$to", RunStore.Ms(to)));
        }
        if (filter.Source is { } source)
        {
            clauses.Add("r.source = $source");
            parameters.Add(("$source", source));
        }
        if (filter.Provider is { } provider)
        {
            parameters.Add(("$provider", provider));
            if (filter.Model is { } model)
            {
                parameters.Add(("$model", model));
                clauses.Add("((r.provider = $provider AND r.model = $model) OR EXISTS (SELECT 1 FROM runs c WHERE c.parent_id = r.id AND c.provider = $provider AND c.model = $model))");
            }
            else clauses.Add("(r.provider = $provider OR EXISTS (SELECT 1 FROM runs c WHERE c.parent_id = r.id AND c.provider = $provider))");
        }
        if (filter.Status is { } status)
        {
            clauses.Add(RunHistoryFilter.StopReasons.Contains(status) ? "(r.stop_reason = $status AND r.status = 'completed')" : "r.status = $status");
            parameters.Add(("$status", status));
        }
        if (filter.Search is { } search)
        {
            const string like = " LIKE $q ESCAPE '\\'";
            clauses.Add($"(r.title{like} OR r.output_text{like} OR r.session{like} OR r.id{like} OR r.op = $exact OR (NOT {hidden} AND (r.error{like} OR r.model{like} OR r.provider{like})))");
            parameters.Add(("$q", "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
            parameters.Add(("$exact", search));
        }
        return (" WHERE " + string.Join(" AND ", clauses), parameters);
    }

    /// <summary>A row of <see cref="RowColumns"/>: the run (with its options) and the tools its answer called.</summary>
    internal static (RunRecord Run, string? ToolCalls) ReadRow(SqliteDataReader reader)
    {
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        long? Long(int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
        DateTimeOffset? Time(int index) => reader.IsDBNull(index) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(index));
        JsonObject? options = null;
        if (Text(36) is { } json)
        {
            try { options = JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { options = null; }
        }
        var run = new RunRecord
        {
            Id = reader.GetString(0),
            Op = reader.GetString(1),
            Kind = reader.GetString(2),
            Source = reader.GetString(3),
            Status = reader.GetString(4),
            Provider = reader.GetString(5),
            Model = reader.GetString(6),
            Api = Text(7),
            Account = Text(8),
            GroupId = Text(9),
            ParentId = Text(10),
            Session = Text(11),
            Title = Text(12),
            MessagesRef = Text(13),
            OutputText = Text(14),
            StopReason = Text(15),
            Error = Text(16),
            Notes = Text(17),
            Usage = new RunUsage(reader.GetDouble(18), reader.GetDouble(19), reader.GetDouble(20), reader.GetDouble(21), reader.GetDouble(22)),
            CostMicros = Long(23),
            CostKind = Text(24),
            Billing = Text(25),
            SpendOp = Text(26),
            TtftMs = Long(27),
            DurationMs = Long(28),
            TokensPerSecond = reader.IsDBNull(29) ? null : reader.GetDouble(29),
            Pinned = reader.GetInt64(30) != 0,
            RawState = reader.GetString(31),
            Created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(32)),
            Started = Time(33),
            Finished = Time(34),
            Updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(35)),
            Options = options,
        };
        return (run, Text(37));
    }

    private static IReadOnlyList<RunFilterOption> Options(WebAgentDb db, string sql, (string, object?)[] parameters, string? chosen)
    {
        var values = db.Query(sql, reader => reader.GetString(0), parameters).ToList();
        if (chosen is not null && !values.Contains(chosen, StringComparer.Ordinal)) values.Add(chosen);
        return [.. values.Select(value => new RunFilterOption(value, value))];
    }

    private static string Icon(string query)
    {
        var filter = RunHistoryFilter.ParseQuery(query);
        return filter.Status switch
        {
            "error" => "bi-exclamation-octagon text-ps-danger",
            "toolUse" => "bi-tools",
            _ => filter.Source is not null ? "bi-funnel" : filter.Search is not null ? "bi-search" : "bi-bookmark",
        };
    }
}
