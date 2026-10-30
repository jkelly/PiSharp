// Decision 0011 (run W3, S4): the Spend pages' JSON API under /api/spend: caps (list, add or edit, remove), the spend settings (warning
// threshold, reservation output cap, unknown-price behaviour per provider, billing overrides per account), entered prices, resolving an
// uncertain reservation, the ledger as JSON and as CSV, and the confirm modal (a preflight that reserves nothing). Writes are POSTs with
// JSON bodies, so HostGuard's same-origin and JSON-only rules cover them; every answer is no-store. Writes are idempotent: adding a cap that
// already exists with the same values answers that cap, removing one that is gone answers removed: false, and resolving a row again the same
// way answers the row. Errors are {error, code}: 400 invalid, 404 not_found, 409 conflict. No answer carries a secret: accounts are ids,
// labels and masked hints.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

public static class SpendEndpoints
{
    public const string Prefix = "/api/spend";

    /// <summary>Rows read per query while writing the CSV.</summary>
    public const int CsvBatch = SpendLedgerQuery.MaxLimit;

    public const string ConfirmPartial = "/Views/Shared/_SpendConfirmModal.cshtml";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IEndpointRouteBuilder MapSpendEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var api = routes.MapGroup(Prefix);
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context).ConfigureAwait(false);
        });

        api.MapGet("/caps", (SpendPages pages, CancellationToken token) => RunAsync(async () =>
            Json(new { caps = (await pages.Ledger.CapStatusAsync(null, token).ConfigureAwait(false)).Select(status => Cap(pages, status)).ToList() })));
        api.MapPost("/caps", (JsonObject? body, SpendPages pages, CancellationToken token) => RunAsync(() => SaveCapAsync(Required(body), pages, token)));
        api.MapPost("/caps/{id:long}/remove", (long id, SpendPages pages, CancellationToken token) =>
            RunAsync(async () => Json(new { removed = await pages.Ledger.RemoveCapAsync(id, token).ConfigureAwait(false) })));

        api.MapGet("/settings", (SpendPages pages) => Run(() => Json(pages.Settings)));
        api.MapPost("/settings", (JsonObject? body, SpendPages pages, ISettingsStore store, BillingModes billing, CancellationToken token) =>
            RunAsync(async () => Json(await SaveSettingsAsync(Required(body), store, billing, token).ConfigureAwait(false))));
        api.MapPost("/prices", (JsonObject? body, ISettingsStore store, ModelPrices prices, IModelCatalog catalog, CancellationToken token) =>
            RunAsync(() => SavePriceAsync(Required(body), store, prices, catalog, token)));
        api.MapPost("/prices/remove", (JsonObject? body, ISettingsStore store, CancellationToken token) => RunAsync(async () =>
        {
            var provider = Text(Required(body), "provider") ?? throw new ArgumentException("Choose a provider.");
            var model = Text(body!, "model") ?? throw new ArgumentException("Choose a model.");
            var removed = false;
            await store.UpdateAsync<SpendSettings>(current =>
            {
                var kept = (current.EnteredPrices ?? []).Where(price => !(price.Provider == provider && price.Model == model)).ToList();
                removed = kept.Count != (current.EnteredPrices?.Count ?? 0);
                return (current with { EnteredPrices = kept }).Normalize();
            }, token).ConfigureAwait(false);
            return Json(new { removed });
        }));

        api.MapPost("/resolve", (JsonObject? body, SpendPages pages, CancellationToken token) => RunAsync(() => ResolveAsync(Required(body), pages, token)));
        api.MapGet("/ledger", (HttpRequest request, SpendPages pages, CancellationToken token) => RunAsync(async () =>
        {
            var filter = Filter(request);
            var page = await pages.Ledger.QueryAsync(filter.Query(), token).ConfigureAwait(false);
            return Json(new
            {
                items = page.Items.Select(row => new { reservation = row, account = pages.AccountText(row.Account), status = row.Status, countedMicros = row.CountedMicros }).ToList(),
                total = page.Total,
                totalAll = page.TotalAll,
                page = filter.Page,
                size = filter.Size,
            });
        }));
        api.MapGet("/ledger.csv", (HttpRequest request, SpendPages pages, CancellationToken token) => RunAsync(async () =>
        {
            var csv = await CsvAsync(Filter(request), pages, token).ConfigureAwait(false);
            return Results.File(csv, "text/csv; charset=utf-8", CsvName(pages.Now));
        }));
        api.MapPost("/confirm", (JsonObject? body, SpendPages pages, FragmentRenderer renderer, CancellationToken token) =>
            RunAsync(() => ConfirmAsync(Required(body), pages, renderer, token)));
        return routes;
    }

    /// <summary>The CSV's file name: <c>pisharp-spend-2026-10-21.csv</c> (today, UTC).</summary>
    public static string CsvName(DateTimeOffset now) => $"pisharp-spend-{now.UtcDateTime:yyyy-MM-dd}.csv";

    private static SpendHistoryFilter Filter(HttpRequest request) =>
        SpendHistoryFilter.Parse(key => request.Query.TryGetValue(key, out var value) ? value.ToString() : null);

    private static object Cap(SpendPages pages, SpendCapStatus status)
    {
        var row = pages.CapRow(status);
        var cap = status.Cap;
        return new
        {
            id = cap.Id,
            scope = cap.Scope,
            key = cap.Key,
            period = cap.Period,
            limitMicros = cap.LimitMicros,
            limit = SpendMoney.Format(cap.LimitMicros),
            action = cap.Action,
            enabled = cap.Enabled,
            spentMicros = status.SpentMicros,
            percent = status.PercentShown,
            warning = status.Warning,
            atLimit = status.AtLimit,
            title = row.EditTitle,
            appliesTo = row.ScopeField,
            window = row.WindowField,
            used = row.UsedField,
        };
    }

    private static async Task<IResult> SaveCapAsync(JsonObject body, SpendPages pages, CancellationToken token)
    {
        var ledger = pages.Ledger;
        var caps = await ledger.CapsAsync(token).ConfigureAwait(false);
        var id = Long(body, "id") ?? 0;
        var limit = Money(body, "limit", "a limit") ?? throw new ArgumentException("Enter a limit in US dollars, such as 5.00.");
        if (limit <= 0) throw new ArgumentException("Enter a limit of at least $0.01.");
        SpendCap saved;
        var existing = false;
        if (id != 0)
        {
            var current = caps.FirstOrDefault(cap => cap.Id == id) ?? throw SpendException.NotFound("That cap no longer exists; reload the page.");
            saved = await ledger.SaveCapAsync(current with
            {
                LimitMicros = limit,
                Action = Choice<CapAction>(body, "action") ?? current.Action,
                Enabled = Bool(body, "enabled") ?? current.Enabled,
            }, token).ConfigureAwait(false);
        }
        else
        {
            var scope = Choice<CapScope>(body, "scope") ?? throw new ArgumentException("Choose what the cap applies to.");
            var key = Text(body, "key");
            if (scope == CapScope.Everything) key = null;
            else if (key is null) throw new ArgumentException(scope switch
            {
                CapScope.Provider => "Choose a provider.",
                CapScope.Account => "Choose an account.",
                _ => "Choose a source.",
            });
            if (scope == CapScope.Source) key = SpendSources.TryParse(key, out var source) ? source.Wire() : throw new ArgumentException($"There is no source {key}.");
            if (scope == CapScope.Account && !pages.AccountExists(key!)) throw SpendException.NotFound("That account no longer exists; reload the page.");
            var cap = new SpendCap(0, scope, key, Choice<CapPeriod>(body, "period") ?? throw new ArgumentException("Choose a window."), limit,
                Choice<CapAction>(body, "action") ?? throw new ArgumentException("Choose Block or Ask."), Bool(body, "enabled") ?? true).Normalize();
            if (caps.FirstOrDefault(other => other.SameTarget(cap)) is { } same && same.LimitMicros == cap.LimitMicros && same.Action == cap.Action && same.Enabled == cap.Enabled)
            {
                saved = same;
                existing = true;
            }
            else
            {
                saved = await ledger.SaveCapAsync(cap, token).ConfigureAwait(false);
            }
        }
        var status = (await ledger.CapStatusAsync(null, token).ConfigureAwait(false)).FirstOrDefault(other => other.Cap.Id == saved.Id)
            ?? new SpendCapStatus(saved, null, null, null, pages.Settings.WarnPercent);
        return Json(new { cap = Cap(pages, status), existing });
    }

    private static async Task<SpendSettings> SaveSettingsAsync(JsonObject body, ISettingsStore store, BillingModes billing, CancellationToken token)
    {
        var warn = Int(body, "warnPercent");
        var reserve = Int(body, "reservationOutputTokens");
        var unknown = Choice<UnknownPriceBehavior>(body, "unknownPrice");
        var rules = new List<UnknownPriceRule>();
        foreach (var item in Array(body, "unknownPriceRules"))
        {
            var provider = Text(item, "provider") ?? throw new ArgumentException("An unknown-price rule needs a provider.");
            rules.Add(new UnknownPriceRule(provider, Choice<UnknownPriceBehavior>(item, "behavior") ?? throw new ArgumentException($"Choose what happens to {provider}'s requests.")));
        }
        var modes = new List<(string Provider, string? Account, BillingKind? Mode)>();
        foreach (var item in Array(body, "billing"))
        {
            var provider = Text(item, "provider") ?? throw new ArgumentException("A billing mode needs a provider.");
            var account = Text(item, "account");
            var mode = Text(item, "mode");
            BillingKind? chosen = mode is null or "detected" ? null : Choice<BillingKind>(item, "mode");
            // Choosing the mode that was detected removes the override, so the account follows how it connects again.
            if (chosen is { } kind && billing.Resolve(provider, account).Detected == kind) chosen = null;
            modes.Add((provider, account, chosen));
        }
        return await store.UpdateAsync<SpendSettings>(current =>
        {
            var overrides = (current.BillingOverrides ?? []).ToList();
            foreach (var (provider, account, mode) in modes)
            {
                overrides.RemoveAll(rule => rule.Provider == provider && rule.Account == account);
                if (mode is { } kind) overrides.Add(new BillingOverride(provider, account, kind));
            }
            return (current with
            {
                WarnPercent = warn ?? current.WarnPercent,
                ReservationOutputTokens = reserve ?? current.ReservationOutputTokens,
                UnknownPrice = unknown ?? current.UnknownPrice,
                UnknownPriceRules = [.. current.UnknownPriceRules ?? [], .. rules],
                BillingOverrides = overrides,
            }).Normalize();
        }, token).ConfigureAwait(false);
    }

    private static async Task<IResult> SavePriceAsync(JsonObject body, ISettingsStore store, ModelPrices prices, IModelCatalog catalog, CancellationToken token)
    {
        var provider = Text(body, "provider") ?? throw new ArgumentException("Choose a provider.");
        var model = Text(body, "model") ?? throw new ArgumentException("Choose a model.");
        var input = Money(body, "input", "the input price") ?? throw new ArgumentException("Enter the input price per 1M tokens, such as 2.50.");
        var output = Money(body, "output", "the output price") ?? throw new ArgumentException("Enter the output price per 1M tokens, such as 10.00.");
        if (catalog.FindModel(provider, model) is null) throw SpendException.NotFound($"There is no model {provider}/{model}.");
        if (!prices.Unpriced(provider).Any(unpriced => unpriced.Model.Id == model))
            throw SpendException.Conflict($"{provider}/{model} already has a price in the catalog; an entered price never replaces it.");
        await store.UpdateAsync<SpendSettings>(current =>
            (current with { EnteredPrices = [.. current.EnteredPrices ?? [], new EnteredPrice(provider, model, input, output)] }).Normalize(), token).ConfigureAwait(false);
        return Json(new
        {
            price = new { provider, model, inputMicrosPerMillion = input, outputMicrosPerMillion = output, input = SpendMoney.Format(input), output = SpendMoney.Format(output) },
        });
    }

    private static async Task<IResult> ResolveAsync(JsonObject body, SpendPages pages, CancellationToken token)
    {
        var op = Text(body, "op") ?? throw new ArgumentException("Choose the request to resolve.");
        var choice = Choice<ResolveChoice>(body, "choice") ?? throw new ArgumentException("Choose how to settle it.");
        var amount = choice == ResolveChoice.Amount ? Money(body, "amount", "the actual amount") ?? throw new ArgumentException("Enter the actual amount, such as 0.32.") : (long?)null;
        var ledger = pages.Ledger;
        var row = await ledger.GetAsync(op, token).ConfigureAwait(false) ?? throw SpendException.NotFound("That request is not in the ledger.");
        var reserved = row.CountedMicros;
        var again = false;
        if (row.State != ReservationState.Uncertain)
        {
            var same = row.ResolvedByUser && choice switch
            {
                ResolveChoice.Estimate => row is { State: ReservationState.Settled, Settlement: SettlementKind.Estimate },
                ResolveChoice.Amount => row is { State: ReservationState.Settled, Settlement: SettlementKind.Entered } && row.ActualMicros == amount,
                ResolveChoice.Reported => row is { State: ReservationState.Settled, Settlement: SettlementKind.Actual },
                _ => row.State == ReservationState.Released,
            };
            if (!same) throw SpendException.Conflict("This request's spend is not uncertain any more; reload the ledger.");
            again = true;
            reserved = row.ReservedMicros ?? 0;
        }
        else
        {
            row = await ledger.ResolveAsync(op, new SpendResolution(choice, amount), token).ConfigureAwait(false);
        }
        var today = SpendMoney.Format(await pages.TodayAsync(token).ConfigureAwait(false));
        var (title, text) = choice switch
        {
            ResolveChoice.Estimate => ("Reservation resolved", $"Settled at the estimate, {SpendMoney.Format(row.ActualMicros ?? 0, 4)}. Today's metered spend is {today}."),
            ResolveChoice.Amount => ("Reservation resolved", $"Settled at the amount you entered. It replaces the {SpendMoney.Format(reserved)} reservation in your totals."),
            ResolveChoice.Reported => ("Reservation resolved", $"Settled at the reported cost, {SpendMoney.Format(row.ActualMicros ?? 0, 4)}. Today's metered spend is {today}."),
            _ => ("Reservation released", $"The {SpendMoney.Format(reserved)} no longer counts. Today's metered spend is {today}."),
        };
        return Json(new { reservation = row, again, today, toast = new { title, body = text } });
    }

    private static async Task<IResult> ConfirmAsync(JsonObject body, SpendPages pages, FragmentRenderer renderer, CancellationToken token)
    {
        var call = new SpendCall
        {
            Provider = Text(body, "provider") ?? throw new ArgumentException("Choose a provider."),
            Model = Text(body, "model") ?? throw new ArgumentException("Choose a model."),
            Account = Text(body, "account"),
            Source = Text(body, "source") is { } wire ? SpendSources.TryParse(wire, out var source) ? source : throw new ArgumentException($"There is no source {wire}.") : SpendSource.Chat,
            Run = Text(body, "run"),
            InputBytes = Long(body, "inputBytes") ?? 0,
            MaxTokens = Int(body, "maxTokens"),
        };
        if (call.InputBytes < 0) throw new ArgumentException("The request size is never negative.");
        var decision = await pages.PreflightAsync(call, token).ConfigureAwait(false);
        var estimate = decision.Estimate;
        var summary = new { worstMicros = estimate.WorstMicros, typicalMicros = estimate.TypicalMicros, billing = estimate.Billing, chip = SpendEstimateText.Chip(estimate) };
        if (decision.Allowed)
            return Json(new { verdict = decision.Verdict, code = (string?)null, pane = (string?)null, estimate = summary, html = (string?)null });
        var view = pages.ConfirmView(call, decision);
        var html = await renderer.RenderAsync(ConfirmPartial, view).ConfigureAwait(false);
        return Json(new
        {
            verdict = decision.Verdict,
            code = decision.ErrorCode,
            pane = view.Pane,
            message = SpendErrors.Describe(decision),
            canProceed = view.CanProceed,
            capOutputTokens = view.CapOutputTokens,
            estimate = summary,
            html,
        });
    }

    /// <summary>Every row matching <paramref name="filter"/>, newest first, as UTF-8 CSV. A cell that a spreadsheet would read as a formula
    /// starts with an apostrophe.</summary>
    internal static async Task<byte[]> CsvAsync(SpendHistoryFilter filter, SpendPages pages, CancellationToken token)
    {
        var text = new StringBuilder();
        text.Append("time_utc,op,source,provider,model,account_id,account,billing,status,reserved_usd,actual_usd,counted_usd,list_usd,input_tokens,output_tokens,run,note,reason\r\n");
        var query = filter.Query() with { Offset = 0, Limit = CsvBatch };
        while (true)
        {
            var page = await pages.Ledger.QueryAsync(query, token).ConfigureAwait(false);
            foreach (var row in page.Items)
            {
                string?[] cells =
                [
                    row.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant), row.Op, row.Source.Wire(), row.Provider, row.Model, row.Account,
                    pages.AccountText(row.Account), SpendPages.ModeWire(row.Billing), SpendHistoryFilter.StatusWire(row.Status), Usd(row.ReservedMicros),
                    Usd(row.ActualMicros), Usd(row.CountedMicros), Usd(row.ListMicros), row.InputTokens.ToString("0", Invariant), row.OutputTokens.ToString(Invariant),
                    row.Run, row.Note, row.Reason,
                ];
                text.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");
            }
            if (page.Items.Count < CsvBatch) break;
            query = query with { Offset = query.Offset + CsvBatch };
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static string? Usd(long? micros) => micros is { } value ? SpendMoney.ToUsd(value).ToString("0.000000", Invariant) : null;

    /// <summary>One CSV cell: quoted when it holds a comma, quote or line break; a leading <c>= + - @</c>, tab or carriage return is
    /// neutralised with an apostrophe, so a spreadsheet never runs it.</summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !IsNumber(value)) value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static bool IsNumber(string value) => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out _);

    private static T Required<T>(T? body) where T : class => body ?? throw new ArgumentException("Send a JSON body.");

    private static string? Text(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        _ => throw new ArgumentException($"{name} must be text."),
    };

    private static long? Long(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var parsed) => parsed,
        JsonValue value when value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text) => null,
        _ => throw new ArgumentException($"{name} must be a whole number."),
    };

    private static int? Int(JsonObject body, string name) =>
        Long(body, name) is { } value ? value is >= int.MinValue and <= int.MaxValue ? (int)value : throw new ArgumentException($"{name} is too large.") : null;

    private static bool? Bool(JsonObject body, string name) => body[name] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        _ => throw new ArgumentException($"{name} must be true or false."),
    };

    private static long? Money(JsonObject body, string name, string what)
    {
        string? text = body[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var typed) => typed,
            JsonValue value when value.TryGetValue<decimal>(out var number) => number.ToString(Invariant),
            _ => throw new ArgumentException($"Enter {what} in US dollars."),
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        return SpendMoney.TryParseUsd(text, out var micros) ? micros
            : throw new ArgumentException($"Enter {what} in US dollars, from $0 to {SpendMoney.Format(SpendMoney.MaxMicros)}, such as 5.00.");
    }

    private static IEnumerable<JsonObject> Array(JsonObject body, string name) => body[name] switch
    {
        null => [],
        JsonArray items => items.Select(item => item as JsonObject ?? throw new ArgumentException($"Each item of {name} is an object.")).ToList(),
        _ => throw new ArgumentException($"{name} must be a list."),
    };

    private static T? Choice<T>(JsonObject body, string name) where T : struct, Enum
    {
        if (Text(body, name) is not { } text) return null;
        foreach (var value in Enum.GetValues<T>())
            if (string.Equals(JsonSerializer.Serialize(value, SettingsStore.JsonOptions).Trim('"'), text, StringComparison.OrdinalIgnoreCase)) return value;
        throw new ArgumentException($"{text} is not a choice of {name}.");
    }

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, SettingsStore.JsonOptions, statusCode: status);

    private static IResult Error(int status, string code, string message) => Json(new ErrorBody(SecretText.Scrub(message), code), status);

    private static IResult Run(Func<IResult> action)
    {
        try { return action(); }
        catch (Exception error) when (Status(error) is { } status) { return Error(status.Status, status.Code, error.Message); }
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) when (Status(error) is { } status) { return Error(status.Status, status.Code, error.Message); }
    }

    private static (int Status, string Code)? Status(Exception error) => error switch
    {
        SpendException spend => (spend.Status, spend.Code),
        ArgumentException => (StatusCodes.Status400BadRequest, SpendErrors.Invalid),
        KeyNotFoundException => (StatusCodes.Status404NotFound, SpendErrors.NotFound),
        InvalidOperationException => (StatusCodes.Status409Conflict, SpendErrors.Conflict),
        _ => null,
    };

    private sealed record ErrorBody(string Error, string Code);
}
