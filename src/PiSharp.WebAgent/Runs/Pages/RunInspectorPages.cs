// Decision 0011 (run W4, R3): builds the Run inspector's model from runs.db. Raw items are read as RunCapture wrote them: one "request"
// item per HTTP attempt (method, redacted URL and headers, body), a "response" item with its status and headers, one "sse" item per raw
// line ("event: x" and "data: {...}" are separate lines, joined here into one row), a "body" item for a body that was not a stream, "error"
// items, and "timing" marks (request-sent, headers, first-byte, end, closed, truncated). The waterfall uses those marks and the stored
// events' arrival times (the first thinking, text and tool-call tokens, the last token, done); without raw payloads it uses the events
// alone. Usage & cost prices the recorded usage at the rates the Spend Guard reserved with (its ledger row), or the catalog's when there
// is no ledger row; the total is the recorded cost. Header values that carry a credential are always shown redacted.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Runs;

/// <summary>Builds the Run inspector. A singleton.</summary>
public sealed class RunInspectorPages(IServiceProvider services, ILogger<RunInspectorPages>? log = null)
{
    /// <summary>The most raw SSE rows the page renders.</summary>
    public const int MaxSseRows = 3_000;

    /// <summary>The most characters of a body the page renders (the download has all of it).</summary>
    public const int MaxBodyChars = 200_000;

    private RunStore Store => services.GetRequiredService<RunStore>();

    /// <summary>The run whose id or op is <paramref name="idOrOp"/>, or null.</summary>
    public RunRecord? Find(string idOrOp)
    {
        ArgumentNullException.ThrowIfNull(idOrOp);
        return Store.Get(idOrOp) ?? Store.GetByOp(idOrOp);
    }

    /// <summary>The inspector of <paramref name="run"/>; View code addresses <paramref name="origin"/>.</summary>
    public async Task<RunInspectorView> ViewAsync(RunRecord run, string origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var store = Store;
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        var running = run.Status == RunStatus.Running && services.GetService<RunLive>()?.Find(run.Id) is { Ended: false };
        var settings = services.GetService<ISettingsStore>()?.Get<RunHistorySettings>() ?? RunHistorySettings.CreateDefault();
        var source = SpendSources.TryParse(run.Source, out var parsed) ? (Label: parsed.Label(), Icon: SpendPages.SourceIcon(parsed)) : (Label: run.Source, Icon: "bi-question-circle");
        var spec = PlaygroundPages.FromRun(run);
        var raw = run.RawState == RunRawState.Stored ? store.Raw(run.Id) : [];
        var events = store.Events(run.Id);
        var toolCalls = ToolCalls(run.Output);
        var state = run.Kind == RunKind.Parent ? "group"
            : running || run.Status == RunStatus.Running ? "running"
            : run.Status is RunStatus.Error or RunStatus.Interrupted ? "error"
            : run.StopReason == "toolUse" || toolCalls.Count > 0 && run.Status == RunStatus.Completed ? "toolcall"
            : "default";

        var (requests, responses, errors) = Http(raw);
        var (sse, sseCount, sseBytes, groups) = Sse(raw);
        var spendPages = services.GetService<SpendPages>();
        var view = new RunInspectorView
        {
            Run = run,
            State = state,
            Running = running,
            SourceLabel = source.Label,
            SourceIcon = source.Icon,
            Stop = RunHistoryText.Stop(run),
            AccountText = run.Account is null ? null : spendPages?.AccountText(run.Account),
            BillingText = BillingLabel(run.Billing),
            Started = (run.Started ?? run.Created).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Thinking = Str(run.Options, "thinking") ?? "off",
            Session = run.Session,
            SessionHref = run.Source == "chat" && !string.IsNullOrEmpty(run.Session) ? "/chat/" + Uri.EscapeDataString(run.Session) : null,
            Prompt = spec?.FirstUserText() is { } first ? RunText.Cut(first.ReplaceLineEndings(" "), 400) : run.Title,
            Replayable = RunHistoryText.Replayable(run) && spec is not null,
            Code = spec is null ? null : Code(spec, origin, run),
            ErrorTitle = state == "error" ? ErrorTitle(run, responses) : null,
            ErrorText = state == "error" ? run.Error ?? "The run ended with an error and no message." : null,
            ToolCalls = toolCalls,
            Metrics = Metrics(run, requests, responses, raw),
            RawNote = run.RawState switch
            {
                RunRawState.Stored => null,
                RunRawState.Expired => $"Raw payloads are kept {RunHistorySettings.RawRetentionDays} days; this run's were removed.",
                _ when run.Api == RunModelOptions.FauxApi => "This run used an offline faux model, which sends no HTTP request.",
                _ when run.Kind == RunKind.Parent => "This run groups other runs; their requests are in their own rows.",
                _ => "Raw payloads were off for this run. Turn on \"Store raw payloads\" in Settings › Data to keep the next runs' requests and responses.",
            },
            Requests = requests,
            Responses = responses,
            TransportErrors = errors,
            OptionsJson = run.Options is null ? null : RequestJson.Indented(run.Options),
            MessagesJson = run.Messages is null ? null : RequestJson.Indented(Abbreviate(run.Messages.DeepClone())),
            MessagesRef = run.MessagesRef,
            OutputJson = run.Output is null ? null : RequestJson.Indented(Abbreviate(run.Output.DeepClone())),
            Sse = sse,
            SseCount = sseCount,
            SseBytes = sseBytes,
            SseGroups = groups,
            Events = new RunOutputView { Run = run, Events = events }.EventRows,
            EventCount = events.Count,
            EventsKept = settings.StoreSseEvents,
            Usage = await UsageAsync(run, spendPages, cancellationToken).ConfigureAwait(false),
            ParentHref = run.ParentId is { } parent ? "/runs/" + Uri.EscapeDataString(parent) : null,
        };
        var (waterfall, ticks, note) = Timing(run, raw, events, responses);
        view = view with { Waterfall = waterfall, Ticks = ticks, TimingNote = note };
        if (run.Kind == RunKind.Parent) view = view with { Children = Children(run.Id, now) };
        return view;
    }

    /// <summary>A HAR 1.2 document of <paramref name="run"/>'s raw payloads (one entry per attempt), or null when none were stored.</summary>
    public JsonObject? Har(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var raw = run.RawState == RunRawState.Stored ? Store.Raw(run.Id) : [];
        var (requests, responses, _) = Http(raw, cut: false);
        if (requests.Count == 0) return null;
        var started = run.Started ?? run.Created;
        var sse = new StringBuilder();
        foreach (var item in raw.Where(item => item.Kind == "sse")) sse.Append(item.Data).Append('\n');
        var entries = new JsonArray();
        foreach (var request in requests)
        {
            var response = responses.FirstOrDefault(item => item.Attempt == request.Attempt);
            var last = request.Attempt == requests[^1].Attempt;
            var body = response?.Body ?? (last && response?.Stream == true ? sse.ToString() : null);
            var end = last ? run.DurationMs ?? response?.AtMs ?? request.AtMs : requests.FirstOrDefault(next => next.Attempt == request.Attempt + 1)?.AtMs ?? request.AtMs;
            entries.Add(new JsonObject
            {
                ["startedDateTime"] = started.AddMilliseconds(request.AtMs).ToString("O", CultureInfo.InvariantCulture),
                ["time"] = Math.Max(0, end - request.AtMs),
                ["request"] = new JsonObject
                {
                    ["method"] = request.Method,
                    ["url"] = request.Url ?? "",
                    ["httpVersion"] = "HTTP/" + (request.Version ?? "1.1"),
                    ["cookies"] = new JsonArray(),
                    ["headers"] = HarHeaders(request.Headers),
                    ["queryString"] = new JsonArray(),
                    ["postData"] = request.Body is null ? null : new JsonObject { ["mimeType"] = request.ContentType ?? "application/json", ["text"] = request.Body },
                    ["headersSize"] = -1,
                    ["bodySize"] = request.BodyBytes ?? -1,
                },
                ["response"] = new JsonObject
                {
                    ["status"] = response?.Status ?? 0,
                    ["statusText"] = response?.Reason ?? "",
                    ["httpVersion"] = "HTTP/" + (response?.Version ?? "1.1"),
                    ["cookies"] = new JsonArray(),
                    ["headers"] = response is null ? new JsonArray() : HarHeaders(response.Headers),
                    ["content"] = new JsonObject { ["size"] = body is null ? 0 : Encoding.UTF8.GetByteCount(body), ["mimeType"] = response?.ContentType ?? "", ["text"] = body },
                    ["redirectURL"] = "",
                    ["headersSize"] = -1,
                    ["bodySize"] = -1,
                },
                ["cache"] = new JsonObject(),
                ["timings"] = new JsonObject { ["send"] = 0, ["wait"] = Math.Max(0, (response?.AtMs ?? request.AtMs) - request.AtMs), ["receive"] = Math.Max(0, end - (response?.AtMs ?? request.AtMs)) },
                ["comment"] = $"PiSharp run {run.Id}, attempt {request.Attempt}. Credentials were redacted before this was stored.",
            });
        }
        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject { ["name"] = "PiSharp Web Agent", ["version"] = typeof(RunInspectorPages).Assembly.GetName().Version?.ToString() ?? "" },
                ["entries"] = entries,
            },
        };
    }

    /// <summary>A run with its children, events and raw payload items, as the Run JSON download writes it.</summary>
    public JsonObject RunJson(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var store = Store;
        var json = RunHistoryPages.RunJson(store, run, raw: true);
        var children = RunHistoryPages.Descendants(store.Db.Db, run.Id).Select(store.Get).OfType<RunRecord>().Select(child => (JsonNode)RunHistoryPages.RunJson(store, child, raw: true)).ToArray();
        if (children.Length > 0) json["children"] = new JsonArray(children);
        return json;
    }

    private static JsonArray HarHeaders(IReadOnlyList<RunHeader> headers) =>
        new([.. headers.Select(header => (JsonNode)new JsonObject { ["name"] = header.Name, ["value"] = header.Redacted ? RunRedaction.Redacted : header.Value })]);

    private ViewCodeView? Code(RunSpec spec, string origin, RunRecord run)
    {
        try
        {
            return services.GetService<PlaygroundPages>()?.Code(spec, origin) is { } code ? code with { Subtitle = "Reproduce run " + run.Id + " · " + run.Provider + "/" + run.Model } : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            log?.LogDebug(error, "View code of run {Run} could not be made.", run.Id);
            return null;
        }
    }

    private IReadOnlyList<RunHistoryRow> Children(string id, DateTimeOffset now)
    {
        var db = Store.Db.Db;
        var records = db.Query($"SELECT {RunHistoryPages.RowColumns} FROM runs r WHERE r.parent_id = $id ORDER BY r.created, r.id;", RunHistoryPages.ReadRow, ("$id", id));
        return RunHistoryPages.RowsOf(db, records, now);
    }

    private static string ErrorTitle(RunRecord run, IReadOnlyList<RunRawResponse> responses)
    {
        if (run.Status == RunStatus.Interrupted) return "Interrupted · the app stopped while this run was running";
        if (SpendErrors.Parse(run.Error) is { } spend)
            return spend.Code == SpendErrors.Blocked ? "Stopped by a spend cap · nothing was sent" : "Stopped by the Spend Guard · nothing was sent";
        var status = responses.LastOrDefault(response => !response.Ok) is { } failed ? failed.Status.ToString(CultureInfo.InvariantCulture) + (failed.Reason is { Length: > 0 } reason ? " " + reason : "")
            : RunHistoryText.HttpStatus(run.Error);
        return "Run failed" + (status is null ? "" : " · HTTP " + status) + (responses.Count > 1 ? $" · {responses.Count} attempts" : "");
    }

    private static List<RunToolCallView> ToolCalls(JsonNode? output)
    {
        var calls = new List<RunToolCallView>();
        if (output?["content"] is not JsonArray content) return calls;
        foreach (var item in content.OfType<JsonObject>())
            if (Str(item, "type") == "toolCall") calls.Add(new RunToolCallView(Str(item, "name") ?? "?", Str(item, "id") ?? ""));
        return calls;
    }

    private static (List<RunRawRequest>, List<RunRawResponse>, List<string>) Http(IReadOnlyList<RunRawItem> raw, bool cut = true)
    {
        var requests = new List<RunRawRequest>();
        var responses = new List<RunRawResponse>();
        var errors = new List<string>();
        foreach (var item in raw)
        {
            switch (item.Kind)
            {
                case "request" when Parse(item.Data) is JsonObject json:
                    var headers = Headers(json["headers"] as JsonObject);
                    var body = Str(json, "body");
                    var bodyCut = cut && body is { Length: > MaxBodyChars };
                    if (bodyCut) body = body![..MaxBodyChars];
                    requests.Add(new RunRawRequest(requests.Count + 1, item.AtMs, Str(json, "method") ?? "POST", Str(json, "url"), Str(json, "version"), headers,
                        body is null ? null : Pretty(body), json["bodyBytes"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : null,
                        headers.FirstOrDefault(header => header.Name == "content-type")?.Value, bodyCut));
                    break;
                case "response" when Parse(item.Data) is JsonObject json:
                    var responseHeaders = Headers(json["headers"] as JsonObject);
                    responses.Add(new RunRawResponse(Math.Max(1, requests.Count), item.AtMs, json["status"] is JsonValue status && status.TryGetValue<int>(out var code) ? code : 0,
                        Str(json, "reason"), Str(json, "version"), responseHeaders, null, responseHeaders.FirstOrDefault(header => header.Name == "content-type")?.Value));
                    break;
                case "body" when responses.Count > 0:
                    var text = cut && item.Data.Length > MaxBodyChars ? item.Data[..MaxBodyChars] : item.Data;
                    responses[^1] = responses[^1] with { Body = Pretty(text) };
                    break;
                case "error":
                    var error = Parse(item.Data) as JsonObject;
                    errors.Add(error is null ? item.Data : string.Join(": ", new[] { Str(error, "type"), Str(error, "message") }.Where(part => part is not null)));
                    break;
            }
        }
        return (requests, responses, errors);
    }

    private static List<RunHeader> Headers(JsonObject? headers)
    {
        var list = new List<RunHeader>();
        if (headers is null) return list;
        foreach (var (name, value) in headers)
        {
            var text = value is JsonValue node && node.TryGetValue<string>(out var str) ? str : value?.ToJsonString() ?? "";
            var redacted = RunRedaction.SecretHeader(name.ToLowerInvariant()) || text.Contains(RunRedaction.Redacted, StringComparison.Ordinal);
            list.Add(new RunHeader(name.ToLowerInvariant(), redacted ? "" : text, redacted));
        }
        return list;
    }

    private static (List<RunSseRow>, int, long, List<string>) Sse(IReadOnlyList<RunRawItem> raw)
    {
        var rows = new List<RunSseRow>();
        var groups = new List<string>();
        var count = 0;
        long bytes = 0;
        string? pending = null;
        foreach (var item in raw)
        {
            if (item.Kind != "sse") continue;
            var line = item.Data;
            bytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (line.Length == 0) continue;
            string name;
            string data;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                pending = line[6..].Trim();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line[5..].TrimStart();
                count++;
                name = pending ?? (Parse(data) is JsonObject json ? Str(json, "type") ?? Str(json, "object") ?? "data" : data == "[DONE]" ? "done" : "data");
                pending = null;
            }
            else if (line.StartsWith(':'))
            {
                name = "comment";
                data = line[1..].Trim();
            }
            else continue;
            if (rows.Count >= MaxSseRows) continue;
            var group = Family(name);
            if (!groups.Contains(group, StringComparer.Ordinal)) groups.Add(group);
            rows.Add(new RunSseRow("+" + item.AtMs.ToString("N0", CultureInfo.InvariantCulture) + " ms", name, data, group));
        }
        return (rows, count, bytes, groups);
    }

    /// <summary>The family of an SSE event name, for the toggles: <c>response.output_text.delta</c> → <c>output_text</c>,
    /// <c>response.created</c> → <c>response.*</c>, <c>content_block_delta</c> → <c>content_block</c>.</summary>
    internal static string Family(string name)
    {
        var parts = name.Split('.');
        if (parts.Length >= 3) return parts[1];
        if (parts.Length == 2) return parts[0] + ".*";
        var underscore = name.LastIndexOf('_');
        return underscore > 0 ? name[..underscore] : name;
    }

    private static IReadOnlyList<RunMetric> Metrics(RunRecord run, IReadOnlyList<RunRawRequest> requests, IReadOnlyList<RunRawResponse> responses, IReadOnlyList<RunRawItem> raw)
    {
        var faint = "text-ps-faint";
        var firstByte = raw.FirstOrDefault(item => item.Kind == "timing" && Parse(item.Data) is JsonObject json && Str(json, "mark") == "first-byte");
        var head = responses.FirstOrDefault();
        var ttfb = firstByte?.AtMs ?? head?.AtMs;
        var cost = RunHistoryText.Cost(run);
        var subscription = run.Billing == "subscription";
        var stored = run.RawState == RunRawState.Stored;
        var retries = stored ? Math.Max(0, requests.Count - 1) : (int?)null;
        var failed = responses.Count > 0 && !responses[^1].Ok;
        return
        [
            new RunMetric("TTFB", ttfb is { } at ? at.ToString("N0", CultureInfo.InvariantCulture) : "—",
                ttfb is null ? null : "ms" + (head is { Ok: false } ? " · " + head.Status.ToString(CultureInfo.InvariantCulture) : ""),
                ttfb is null ? (stored ? "No response arrived." : "Needs raw payloads (Settings › Data).") : "Time to the response's first byte.", ttfb is null ? faint : null),
            new RunMetric("TTFT", run.TtftMs is { } ttft ? ttft.ToString("N0", CultureInfo.InvariantCulture) : "—", run.TtftMs is null ? null : "ms", "Time to the first content token.", run.TtftMs is null ? faint : null),
            new RunMetric("Total", run.DurationMs is { } total ? (total / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) : "—", run.DurationMs is null ? null : "s", null, run.DurationMs is null ? faint : null),
            new RunMetric("Tokens in", RunHistoryText.Tokens(run.Usage.Input)),
            new RunMetric("Tokens out", RunHistoryText.Tokens(run.Usage.Output), run.TokensPerSecond is { } rate ? rate.ToString("0", CultureInfo.InvariantCulture) + " tok/s" : null),
            new RunMetric("Cache read", RunHistoryText.Tokens(run.Usage.CacheRead)),
            new RunMetric(subscription ? "List cost" : "Cost", cost.Top, cost.Bottom, cost.Title, cost.Faint ? faint : null),
            new RunMetric("Retries", retries is { } count ? count.ToString(CultureInfo.InvariantCulture) : "—", retries > 0 && failed ? "gave up" : null,
                stored ? "HTTP attempts after the first, from the raw payloads." : "Needs raw payloads (Settings › Data).", retries is null ? faint : retries > 0 && failed ? "text-ps-danger" : null),
        ];
    }

    private static (IReadOnlyList<RunWaterfallRow>, IReadOnlyList<string>, string?) Timing(RunRecord run, IReadOnlyList<RunRawItem> raw, IReadOnlyList<RunEventRecord> events,
        IReadOnlyList<RunRawResponse> responses)
    {
        var marks = new List<(string Label, long At, string Phase)>();
        var requests = raw.Where(item => item.Kind == "request").ToList();
        if (requests.Count > 1)
        {
            for (var index = 0; index < requests.Count; index++)
            {
                var response = responses.FirstOrDefault(item => item.Attempt == index + 1);
                var status = response is null ? "no response" : response.Status.ToString(CultureInfo.InvariantCulture);
                marks.Add(($"Attempt {index + 1} sent", requests[index].AtMs, "wait"));
                if (response is not null) marks.Add(($"Attempt {index + 1} → {status}", response.AtMs, response.Ok ? "ttfb" : "error"));
            }
        }
        else
        {
            foreach (var item in raw.Where(item => item.Kind == "timing"))
            {
                if (Parse(item.Data) is not JsonObject json) continue;
                switch (Str(json, "mark"))
                {
                    case "request-sent": marks.Add(("Request sent", item.AtMs, "request")); break;
                    case "first-byte": marks.Add(("TTFB", item.AtMs, "ttfb")); break;
                    case "headers" when json["status"] is JsonValue code && code.TryGetValue<int>(out var status) && status >= 400:
                        marks.Add(("Response " + status.ToString(CultureInfo.InvariantCulture), item.AtMs, "error"));
                        break;
                }
            }
        }
        long? First(Func<string, bool> match) => events.FirstOrDefault(item => match(Type(item.Event)))?.AtMs;
        if (First(type => type.StartsWith("thinking_", StringComparison.Ordinal)) is { } thinking) marks.Add(("First thinking token", thinking, "thinking"));
        if (First(type => type.StartsWith("text_", StringComparison.Ordinal)) is { } text) marks.Add(("First text token", text, "text"));
        if (First(type => type.StartsWith("toolcall_", StringComparison.Ordinal)) is { } tool) marks.Add(("First tool-call token", tool, "tool"));
        var deltas = events.Where(item => Type(item.Event).EndsWith("_delta", StringComparison.Ordinal)).ToList();
        if (deltas.Count > 1) marks.Add(("Last token", deltas[^1].AtMs, "last"));
        if (events.LastOrDefault(item => Type(item.Event) is "done" or "error") is { } end)
            marks.Add((Type(end.Event) == "done" ? "Done" : "Error", end.AtMs, Type(end.Event) == "done" ? "done" : "error"));
        else if (run.DurationMs is { } duration && run.Status != RunStatus.Running)
            marks.Add((run.Status == RunStatus.Completed ? "Done" : "Ended", duration, run.Status == RunStatus.Completed ? "done" : "error"));
        if (marks.Count == 0) return ([], [], null);
        marks = [.. marks.OrderBy(mark => mark.At)];
        var total = Math.Max(marks[^1].At, run.DurationMs ?? 0);
        if (total <= 0) total = 1;
        double[] steps = [50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 15_000, 30_000, 60_000, 120_000, 300_000, 600_000, 1_800_000, 3_600_000];
        var step = steps.FirstOrDefault(candidate => total / candidate <= 7, steps[^1]);
        var scale = Math.Max(step, Math.Ceiling(total / step) * step);
        var ticks = new List<string>();
        for (double at = 0; at <= scale + 0.5; at += step) ticks.Add(at == 0 ? "0" : at >= 1_000 ? (at / 1_000).ToString("0.##", CultureInfo.InvariantCulture) + " s" : at.ToString("0", CultureInfo.InvariantCulture) + " ms");
        var rows = new List<RunWaterfallRow>();
        long previous = 0;
        foreach (var (label, at, phase) in marks)
        {
            var start = Math.Min(previous, at);
            rows.Add(new RunWaterfallRow(label, Math.Round(start * 100 / scale, 2), Math.Round(Math.Max(0, at - start) * 100 / scale, 2), phase, at.ToString("N0", CultureInfo.InvariantCulture) + " ms"));
            previous = Math.Max(previous, at);
        }
        var note = new List<string>();
        if (run.Usage.Output > 0) note.Add(RunHistoryText.Tokens(run.Usage.Output) + " output tokens");
        if (run.TokensPerSecond is { } rate) note.Add(rate.ToString("0", CultureInfo.InvariantCulture) + " tok/s after the first token");
        if (run.RawState != RunRawState.Stored && run.Kind == RunKind.Call) note.Add("connection timings need raw payloads (Settings › Data)");
        return (rows, ticks, note.Count == 0 ? null : string.Join(" · ", note));
    }

    private async Task<RunUsageView> UsageAsync(RunRecord run, SpendPages? spendPages, CancellationToken cancellationToken)
    {
        var usage = run.Usage;
        var recorded = usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite > 0;
        SpendPrice? price = null;
        var fromLedger = false;
        if (services.GetService<ISpendLedger>() is { } ledger && (run.SpendOp ?? (run.Kind == RunKind.Call ? run.Op : null)) is { } op)
        {
            try
            {
                price = (await ledger.GetAsync(op, cancellationToken).ConfigureAwait(false))?.Price;
                fromLedger = price is not null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log?.LogDebug(error, "The ledger row of run {Run} could not be read.", run.Id);
            }
        }
        if (price is null && services.GetService<IModelCatalog>()?.FindModel(run.Provider, run.Model)?.Cost is { } cost) price = SpendPrice.From(cost);
        string Rate(double? rate) => rate is { } value ? "$" + value.ToString("0.00##", CultureInfo.InvariantCulture) : "—";
        string Cost(double tokens, double? rate) => rate is { } value ? "$" + (tokens * value / 1_000_000).ToString("0.000000", CultureInfo.InvariantCulture) : "—";
        var rows = new List<RunUsageRow>
        {
            new("input", RunHistoryText.Tokens(usage.Input), Rate(price?.Input), Cost(usage.Input, price?.Input)),
            new("output", RunHistoryText.Tokens(usage.Output), Rate(price?.Output), Cost(usage.Output, price?.Output)),
            new("cacheRead", RunHistoryText.Tokens(usage.CacheRead), Rate(price?.CacheRead), Cost(usage.CacheRead, price?.CacheRead)),
            new("cacheWrite", RunHistoryText.Tokens(usage.CacheWrite), Rate(price?.CacheWrite), Cost(usage.CacheWrite, price?.CacheWrite)),
        };
        double? computed = price is null ? null : (usage.Input * price.Input + usage.Output * price.Output + usage.CacheRead * price.CacheRead + usage.CacheWrite * price.CacheWrite) / 1_000_000;
        var total = run.CostMicros is { } micros ? micros / 1_000_000.0 : computed;
        string? note = null;
        if (run.CostMicros is { } settled && computed is { } at && Math.Abs(settled / 1_000_000.0 - at) >= 0.000002)
            note = "The total is the cost the run recorded (" + run.CostKind + "); tokens × rates gives $" + at.ToString("0.000000", CultureInfo.InvariantCulture) + ".";
        else if (run.CostMicros is null && computed is not null) note = "No cost was recorded for this run; the total is tokens × rates.";
        var tier = price is null ? new RunChip("no price", "outline")
            : price.TierInputTokensAbove is { } above ? new RunChip("tier above " + RunHistoryText.Compact(above) + " input", "warn") : new RunChip("base tier", "ok");
        var tierText = price is null ? "No price is known for this model, so no cost is computed from its tokens."
            : (price.TierInputTokensAbove is { } limit ? $"The prompt was over {RunHistoryText.Tokens(limit)} input tokens, so this tier's rates applied. " : "")
              + (fromLedger ? "Rates as the Spend Guard reserved this call with them." : "Rates from the catalog (this call has no ledger row).");
        var usageJson = run.Output?["usage"] is JsonObject reported ? RequestJson.Indented(reported) : RequestJson.Indented(JsonSerializer.SerializeToNode(usage, SettingsStore.JsonOptions));
        var account = run.Account is null ? null : spendPages?.AccountText(run.Account);
        return new RunUsageView
        {
            Recorded = recorded,
            Rows = rows,
            TotalTokens = RunHistoryText.Tokens(usage.Total > 0 ? usage.Total : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite),
            TotalCost = total is { } dollars ? "$" + dollars.ToString("0.000000", CultureInfo.InvariantCulture) : "—",
            CostNote = note,
            Tier = tier,
            Rates = price is null ? "" : $"${price.Input.ToString("0.##", CultureInfo.InvariantCulture)} in / ${price.Output.ToString("0.##", CultureInfo.InvariantCulture)} out per 1M",
            TierText = tierText,
            Billing = new RunChip(BillingLabel(run.Billing) ?? "Unknown", run.Billing switch { "subscription" => "accent", "metered" => "info", "free" => "ok", _ => "outline" }),
            BillingSub = string.Join(" · ", new[] { account, run.Provider }.Where(part => !string.IsNullOrEmpty(part))),
            BillingText = run.Billing switch
            {
                "subscription" => "Billed through a subscription, not per token. The cost is an estimate at API list prices, useful for comparing runs; Spend Guard does not count it toward money caps.",
                "metered" => "Billed per token. Spend Guard reserved the worst case before the call and settled to this cost; it counts toward your money caps.",
                "free" => "This provider is free: nothing counts toward your caps.",
                _ => "The billing mode was not recorded for this call.",
            },
            UsageJson = usageJson,
            EmptyText = run.Status switch
            {
                RunStatus.Running => "The usage is known when the run ends.",
                RunStatus.Error => "The run failed before the provider reported usage, so no tokens were counted." + (run.CostMicros is { } c ? " Recorded cost " + RunHistoryText.Money(c) + "." : ""),
                _ => "The provider reported no usage for this run.",
            },
        };
    }

    private static string? BillingLabel(string? billing) => billing switch
    {
        "metered" => "Metered",
        "subscription" => "Subscription",
        "free" => "Free",
        "unknown" => "Unknown price",
        _ => null,
    };

    private static JsonNode? Parse(string text)
    {
        if (text.Length == 0 || (text[0] != '{' && text[0] != '[')) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string Pretty(string body) => Parse(body) is { } json ? RequestJson.Indented(Abbreviate(json)) : body;

    /// <summary><paramref name="node"/> with long base64 image data replaced by its length (the page and View code never need the bytes).</summary>
    private static JsonNode Abbreviate(JsonNode node)
    {
        switch (node)
        {
            case JsonObject json:
                foreach (var (key, value) in json.ToList())
                {
                    if (value is JsonValue text && text.TryGetValue<string>(out var str) && str.Length > 512 && key is "data" or "image_url" or "url" && LooksBase64(str))
                        json[key] = $"<{str.Length:N0} base64 characters>";
                    else if (value is not null) Abbreviate(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array) if (item is not null) Abbreviate(item);
                break;
        }
        return node;
    }

    private static bool LooksBase64(string text) => text.StartsWith("data:", StringComparison.Ordinal) || text.Take(64).All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=');

    private static string Type(JsonNode? e) => e is JsonObject json ? Str(json, "type") ?? "" : "";

    private static string? Str(JsonObject? json, string name) => json?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
