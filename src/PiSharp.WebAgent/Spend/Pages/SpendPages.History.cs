// Decision 0011 (run W3, S4): History › Spend's read side: the filters (search, UTC date range, provider → model, source, account, status),
// one page of the ledger as the table shows it, the summary (today and this month against the everything-metered day and month caps, the
// other caps above their warning threshold, the prompt-cache meter, what is not counted), the uncertain banner, and the Resolve modal's
// figures (what settling or releasing would leave in each cap the row counts toward). Every figure is the ledger's. W5 (fix 1, arena): a
// request of an Arena battle not voted yet is shown as "Arena battle · blind" (no provider, model, account, op, run, usage, amounts or end
// time, and no Resolve), and a filter that could single out one model (provider, model, account, text) leaves such rows out, totals too. The filter chips and
// the saved filters are SpendPages.SavedFilters.cs (W3 fix, spend-ui).
using System.Globalization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Evals.Arena;

namespace PiSharp.WebAgent.Spend;

public sealed partial class SpendPages
{
    /// <summary>History › Spend for <paramref name="filter"/>.</summary>
    /// <exception cref="ArgumentException">The filter is not allowed (an end date before the start).</exception>
    public async Task<SpendHistoryPage> HistoryPageAsync(SpendHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var now = Now;
        var blind = Blind();
        var query = BlindQuery(filter, blind);
        var page = await ledger.QueryAsync(query, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count == 0 && filter.Page > 1 && page.Total > 0)
        {
            filter = filter with { Page = Math.Max(1, (page.Total + filter.Size - 1) / filter.Size) };
            page = await ledger.QueryAsync(BlindQuery(filter, blind), cancellationToken).ConfigureAwait(false);
        }
        var uncertain = await ledger.QueryAsync(new SpendLedgerQuery { Status = LedgerStatus.Uncertain, Limit = 3 }, cancellationToken).ConfigureAwait(false);
        var uncertainTotal = uncertain.Total == 0 ? SpendTotal.Zero
            : await ledger.TotalAsync(new SpendLedgerQuery { Status = LedgerStatus.Uncertain }, cancellationToken).ConfigureAwait(false);
        ResolveReservationView? resolve = null;
        if (filter.Resolve is { } op && await ledger.GetAsync(op, cancellationToken).ConfigureAwait(false) is { State: ReservationState.Uncertain } row && !blind.Hides(row))
            resolve = await ResolveViewAsync(row, cancellationToken).ConfigureAwait(false);
        // The providers that have rows (a blind battle's left out until its vote), not every catalog provider (after the W5 live test).
        var used = await ledger.ProvidersAsync(blind.IsEmpty ? new SpendLedgerQuery() : new SpendLedgerQuery { ExceptRuns = blind.Keys }, cancellationToken)
            .ConfigureAwait(false);
        var options = FilterOptions(filter, used);
        return new SpendHistoryPage
        {
            Filter = filter,
            Ledger = page,
            Rows = [.. page.Items.Select(item => LedgerRow(item, now, blind))],
            Summary = await SummaryAsync(filter, now, blind, cancellationToken).ConfigureAwait(false),
            Uncertain = [.. uncertain.Items.Select(item => LedgerRow(item, now, blind))],
            UncertainCount = uncertain.Total,
            UncertainMicros = uncertainTotal.CountedMicros,
            Options = options,
            DatabasePath = DatabasePath,
            Resolve = resolve,
            Chips = FilterChips(filter, options),
            Saved = SavedFilters(filter, options),
            SaveName = SuggestedFilterName(filter, options),
        };
    }

    /// <summary>The Resolve modal of the uncertain row <paramref name="op"/>; null when there is no such row.</summary>
    /// <exception cref="SpendException">409 when the row is not uncertain (already resolved, or still running).</exception>
    public async Task<ResolveReservationView?> ResolveViewAsync(string op, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op);
        if (await ledger.GetAsync(op.Trim(), cancellationToken).ConfigureAwait(false) is not { } row) return null;
        if (Blind().Hides(row)) throw SpendException.Conflict(BlindRefusal);
        if (row.State != ReservationState.Uncertain) throw SpendException.Conflict("This request's spend is not uncertain any more; reload the ledger.");
        return await ResolveViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a blind Arena battle's request cannot be resolved (or shown) before the vote.</summary>
    public const string BlindRefusal = "This request is part of a blind Arena battle that hasn't been voted on, so its model stays hidden. Vote first, then resolve it here.";

    /// <summary>W5 (fix 1, arena): the blind Arena battles now (their run groups, runs and agent model keys), or none when the Arena is not
    /// registered. Their ledger rows are shown masked (<see cref="MaskBlind"/>) and never match a filter that could single out one model.</summary>
    public ArenaBlindSet Blind() => ArenaBlindSet.Of(services);

    /// <summary>Whether <paramref name="filter"/> could single out one model (a provider, model, account or text): such a filter leaves blind
    /// battles' rows out, rows and totals alike, until the vote.</summary>
    public static bool Narrows(SpendHistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Provider is not null || filter.Model is not null || filter.Account is not null || filter.Search is not null;
    }

    /// <summary>The ledger query of <paramref name="filter"/>, leaving <paramref name="blind"/>'s rows out when the filter
    /// <see cref="Narrows"/>.</summary>
    public static SpendLedgerQuery BlindQuery(SpendHistoryFilter filter, ArenaBlindSet blind)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(blind);
        var query = filter.Query();
        return !blind.IsEmpty && Narrows(filter) ? query with { ExceptRuns = blind.Keys } : query;
    }

    /// <summary><paramref name="row"/> as a page may show it before its battle's vote: its source, state, billing and reservation time only;
    /// no provider, model, account, op, run, tokens, usage, price, cost, note, reason or end time. Its amount still counts in the totals.</summary>
    public static Reservation MaskBlind(Reservation row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Reservation
        {
            Op = "blind",
            Provider = "",
            Model = "",
            Source = row.Source,
            Billing = row.Billing,
            State = row.State,
            Created = row.Created,
        };
    }

    private async Task<ResolveReservationView> ResolveViewAsync(Reservation row, CancellationToken cancellationToken)
    {
        var now = Now;
        var reserved = row.CountedMicros;
        var estimate = row.EstimateMicros ?? row.ReservedMicros ?? 0;
        var reported = row.ReportedMicros;
        var counted = new List<string>();
        var afterEstimate = new List<string>();
        var afterReported = new List<string>();
        var afterRelease = new List<string>();
        foreach (var status in await ledger.CapStatusAsync(row.Run, cancellationToken).ConfigureAwait(false))
        {
            var cap = status.Cap;
            if (!cap.Enabled || !row.CountsTowardCaps || !cap.AppliesTo(row.Provider, row.Account, row.Source) || status.SpentMicros is not { } spent) continue;
            if (cap.Period == CapPeriod.Request) continue;
            if (cap.Period == CapPeriod.Run && row.Run is null) continue;
            if (status.WindowStart is { } start && row.Created < start) continue;
            if (status.WindowEnd is { } end && row.Created >= end) continue;
            var name = WindowName(cap);
            counted.Add(name);
            var limit = SpendMoney.Format(cap.LimitMicros);
            afterEstimate.Add($"{name} {SpendMoney.Format(Math.Max(0, spent - reserved + estimate))} of {limit}");
            if (reported is { } cost) afterReported.Add($"{name} {SpendMoney.Format(Math.Max(0, spent - reserved + cost))} of {limit}");
            afterRelease.Add($"{name} {SpendMoney.Format(Math.Max(0, spent - reserved))} of {limit}");
        }
        var shown = LedgerRow(row, now);
        string? formula = null;
        if (row.Price is { } price && row.ReservedMicros is { } total)
        {
            formula = $"{SpendEstimateText.Tokens(row.InputTokens)} input tokens × {SpendEstimateText.Rate(price.WorstInput)} = {SpendMoney.Format(price.InputMicros(row.InputTokens), 4)}\n" +
                $"{SpendEstimateText.Tokens(row.OutputTokens)} max output tokens × {SpendEstimateText.Rate(price.Output)} = {SpendMoney.Format(price.OutputMicros(row.OutputTokens), 4)}\n" +
                $"Reserved = {SpendMoney.Format(total, 4)}";
        }
        return new ResolveReservationView
        {
            Row = row,
            Shown = shown,
            Subtitle = $"{row.Source.Label().ToLowerInvariant()} · {shown.Day.ToLowerInvariant()} {shown.Time}",
            Account = row.Account is null ? row.Provider : row.Provider + " · " + AccountText(row.Account),
            CountsToward = counted.Count == 0 ? "no cap" : Join(counted),
            Formula = formula,
            EstimateMicros = estimate,
            EstimateText = row.EstimateMicros is null
                ? "No estimate was recorded for this request, so it settles at the reserved worst case."
                : "The input and the output received before the stream dropped, at this model's price.",
            AfterEstimate = afterEstimate.Count == 0 ? null : "After settling: " + string.Join(" · ", afterEstimate) + ".",
            ReportedMicros = reported,
            ReportedText = row.Usage is { } usage
                ? $"The request was sent again after a restart and that answer reported {SpendEstimateText.Tokens(usage.InputTokens)} input and " +
                  $"{SpendEstimateText.Tokens(usage.OutputTokens)} output tokens."
                : null,
            AfterReported = afterReported.Count == 0 ? null : "After settling: " + string.Join(" · ", afterReported) + ".",
            AfterRelease = afterRelease.Count == 0 ? null : "After releasing: " + string.Join(" · ", afterRelease) + ".",
        };
    }

    /// <summary>A cap's window in the Resolve modal: <c>today</c>, <c>this month</c>, <c>anthropic monthly</c>, <c>this chat</c>.</summary>
    private string WindowName(SpendCap cap) => (cap.Scope, cap.Period) switch
    {
        (CapScope.Everything, CapPeriod.Day) => "today",
        (CapScope.Everything, CapPeriod.Month) => "this month",
        (_, CapPeriod.Run) => cap.Scope == CapScope.Everything ? "this run" : ScopeName(cap) + " per run",
        (_, CapPeriod.Day) => ScopeName(cap) + " today",
        _ => ScopeName(cap) + " monthly",
    };

    /// <summary>Today's metered spend (counted toward caps), for the resolved toast.</summary>
    public async Task<long> TodayAsync(CancellationToken cancellationToken = default) =>
        (await ledger.TotalAsync(new SpendLedgerQuery { From = SpendWindows.DayStart(Now) }, cancellationToken).ConfigureAwait(false)).CountedMicros;

    private async Task<SpendSummaryView> SummaryAsync(SpendHistoryFilter filter, DateTimeOffset now, ArenaBlindSet blind, CancellationToken cancellationToken)
    {
        var today = SpendWindows.DayStart(now);
        var month = SpendWindows.MonthStart(now);
        var statuses = (await ledger.CapStatusAsync(null, cancellationToken).ConfigureAwait(false)).Where(status => status.Cap.Enabled).ToList();
        var cells = new List<SpendMeter>();
        SpendTotal notCounted;
        SpendCacheMeter cache;
        string cacheLabel;
        if (!filter.Active)
        {
            var day = await ledger.TotalAsync(new SpendLedgerQuery { From = today }, cancellationToken).ConfigureAwait(false);
            var thisMonth = await ledger.TotalAsync(new SpendLedgerQuery { From = month }, cancellationToken).ConfigureAwait(false);
            var dayCap = statuses.FirstOrDefault(status => status.Cap is { Scope: CapScope.Everything, Period: CapPeriod.Day });
            var monthCap = statuses.FirstOrDefault(status => status.Cap is { Scope: CapScope.Everything, Period: CapPeriod.Month });
            cells.Add(Meter("Today", day, dayCap, "daily", Warnings(statuses, CapPeriod.Day, dayCap)));
            cells.Add(Meter("This month", thisMonth, monthCap, "monthly", Warnings(statuses, CapPeriod.Month, monthCap)));
            notCounted = thisMonth;
            cache = await ledger.CacheMeterAsync(month, null, cancellationToken).ConfigureAwait(false);
            cacheLabel = "Prompt cache · this month";
        }
        else
        {
            // A filter that could single out one model leaves blind battles' rows out of its totals too (BlindQuery).
            var query = BlindQuery(filter, blind);
            var matching = await ledger.TotalAsync(query, cancellationToken).ConfigureAwait(false);
            var matchingToday = await ledger.TotalAsync(query with { From = Later(query.From, today) }, cancellationToken).ConfigureAwait(false);
            var allToday = await ledger.TotalAsync(new SpendLedgerQuery { From = today }, cancellationToken).ConfigureAwait(false);
            var who = filter.Provider ?? "Matching";
            var scoped = filter.Provider is { } provider && filter.Model is null && filter.Account is null && filter.Source is null && filter.Status is null && filter.Search is null
                ? statuses.FirstOrDefault(status => status.Cap is { Scope: CapScope.Provider, Period: CapPeriod.Month } && status.Cap.Key == provider) : null;
            var all = new SpendMeter("Matching requests", matching.CountedMicros, null,
                Includes(matching.UncertainMicros, Count(matching.Requests) + (matching.Requests == 1 ? " request" : " requests") + " in the filters"), null, []);
            if (scoped is not null && filter.From is null && filter.To is null)
            {
                var monthly = await ledger.TotalAsync(query with { From = month }, cancellationToken).ConfigureAwait(false);
                all = Meter(who + " · this month", monthly, scoped, "monthly", [], scope: who);
            }
            cells.Add(all);
            cells.Add(new SpendMeter(who + " · today", matchingToday.CountedMicros, null,
                Includes(matchingToday.UncertainMicros, filter.Provider is { } p && !statuses.Any(status => status.Cap is { Period: CapPeriod.Day } && status.Cap.Scope != CapScope.Everything && status.Cap.AppliesTo(p, null, SpendSource.Chat))
                    ? $"No daily cap for {p}" : "Metered, counted toward caps"),
                "of " + SpendMoney.Format(allToday.CountedMicros) + " metered today", []));
            notCounted = matching;
            cache = await ledger.CacheMeterAsync(month, filter.Provider, cancellationToken).ConfigureAwait(false);
            cacheLabel = "Prompt cache · " + (filter.Provider ?? "this month");
        }
        return new SpendSummaryView(filter.Active, cells, cacheLabel, cache, NotCounted(notCounted, filter.Active));
    }

    private static DateTimeOffset Later(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    private static IReadOnlyList<SpendCapStatus> Warnings(List<SpendCapStatus> statuses, CapPeriod period, SpendCapStatus? shown) =>
        [.. statuses.Where(status => status.Cap.Period == period && !ReferenceEquals(status, shown) && (status.Warning || status.AtLimit))];

    private SpendMeter Meter(string label, SpendTotal total, SpendCapStatus? cap, string window, IReadOnlyList<SpendCapStatus> warnings, string? scope = null)
    {
        if (cap is null) return new SpendMeter(label, total.CountedMicros, null, Includes(total.UncertainMicros, $"No {window} cap"), null, warnings);
        var verb = cap.Cap.Action == CapAction.Block ? "blocks" : "asks";
        var percent = cap.SpentMicros is null ? 0 : (int)Math.Round(total.CountedMicros * 100.0 / cap.Cap.LimitMicros, MidpointRounding.AwayFromZero);
        var name = scope is null ? $"the {window} cap" : $"the {scope} cap";
        return new SpendMeter(label, total.CountedMicros, cap,
            Includes(total.UncertainMicros, $"{percent}% of {name} · {verb} at {SpendMoney.Format(cap.Cap.LimitMicros)}"), null, warnings)
        { Percent = percent };
    }

    private static string Includes(long uncertain, string text) => uncertain > 0 ? $"{text} · includes {SpendMoney.Format(uncertain)} uncertain" : text;

    private static string? NotCounted(SpendTotal total, bool filtered)
    {
        var parts = new List<string>();
        if (total.ListMicros > 0) parts.Add($"subscription use {SpendMoney.FormatList(total.ListMicros)}{(filtered ? "" : " this month")}");
        if (total.UnknownPriceRequests > 0) parts.Add($"{Count(total.UnknownPriceRequests)} {(total.UnknownPriceRequests == 1 ? "request" : "requests")} with an unknown price");
        if (total.FreeRequests > 0) parts.Add($"{Count(total.FreeRequests)} free {(total.FreeRequests == 1 ? "request" : "requests")}");
        return parts.Count == 0 ? null : "Not counted toward money caps: " + string.Join(" · ", parts) + ".";
    }

    /// <summary>The filters' choices: the providers in <paramref name="used"/> (those with ledger rows), with an account, or chosen, in the
    /// catalog's order and then by id; the chosen provider's models; the accounts by billing mode.</summary>
    private SpendFilterOptions FilterOptions(SpendHistoryFilter filter, IReadOnlyList<string> used)
    {
        var rows = billing.Accounts();
        var offered = used.Concat(rows.Select(row => row.Provider)).Append(filter.Provider).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var providers = catalog.Providers.Where(provider => offered.Contains(provider.Id)).Select(provider => new SpendOption(provider.Id, provider.Id)).ToList();
        foreach (var extra in offered.Order(StringComparer.Ordinal))
            if (!providers.Any(option => option.Value == extra)) providers.Add(new SpendOption(extra, extra));
        var models = filter.Provider is { } chosen ? catalog.Models(chosen).Select(model => new SpendOption(model.Id, model.Id)).ToList() : [];
        if (filter.Model is { } model && !models.Any(option => option.Value == model)) models.Add(new SpendOption(model, model));
        var groups = new List<SpendOptionGroup>();
        foreach (var mode in new[] { BillingKind.Subscription, BillingKind.Metered, BillingKind.Free, BillingKind.Unknown })
        {
            var options = rows.Where(row => row.Billing.Mode == mode && row.Account is not null)
                .Select(row => new SpendOption(row.Account!.Id, row.Provider + " · " + AccountText(row.Account!))).ToList();
            if (options.Count > 0) groups.Add(new SpendOptionGroup(ModeText(mode), options));
        }
        if (filter.Account is { } account && !groups.Any(group => group.Options.Any(option => option.Value == account)))
            groups.Add(new SpendOptionGroup("Other", [new SpendOption(account, AccountText(account))]));
        return new SpendFilterOptions(providers, models, groups);
    }

    /// <summary>One ledger row as the table shows it (masked while its Arena battle is blind).</summary>
    public SpendLedgerRow LedgerRow(Reservation row) => LedgerRow(row, Now, Blind());

    private SpendLedgerRow LedgerRow(Reservation row, DateTimeOffset now, ArenaBlindSet blind) =>
        blind.BattleOf(row) is { } battle ? BlindRow(row, now, battle) : LedgerRow(row, now);

    // W5 (fix 1, arena): a request of a battle that is held or ready, as "Arena battle · blind": no provider, model, account, op, run, tokens,
    // amounts or end time (a column's usage, cost or timing could be matched to its answer), and no Resolve until the vote; it opens the battle.
    private static SpendLedgerRow BlindRow(Reservation row, DateTimeOffset now, string battle)
    {
        var created = row.Created.ToUniversalTime();
        var day = created.Date == now.UtcDateTime.Date ? "Today" : created.Date == now.UtcDateTime.Date.AddDays(-1) ? "Yesterday"
            : created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        const string hidden = "Hidden until you vote; it counts in the totals.";
        return new SpendLedgerRow(MaskBlind(row), created.ToString("HH:mm:ss", CultureInfo.InvariantCulture), day, SourceIcon(SpendSource.Arena), ArenaBlindText.Title,
            "names and amounts hidden until you vote", "—", hidden, "hidden", null, hidden, row.State == ReservationState.Reserved ? "reserved" : "blind",
            row.State == ReservationState.Reserved ? "info" : "outline", row.State == ReservationState.Uncertain ? "uncertain: resolve it after you vote" : null, null, false)
        {
            Blind = ArenaBlindText.Href(battle),
        };
    }

    private SpendLedgerRow LedgerRow(Reservation row, DateTimeOffset now)
    {
        var created = row.Created.ToUniversalTime();
        var day = created.Date == now.UtcDateTime.Date ? "Today" : created.Date == now.UtcDateTime.Date.AddDays(-1) ? "Yesterday"
            : created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var account = AccountText(row.Account);
        var mode = row.Billing switch
        {
            BillingKind.Metered => "metered",
            BillingKind.Subscription => "subscription",
            BillingKind.Free => "free",
            _ => "unknown price",
        };
        string reserved;
        string? reservedTitle;
        if (row.Billing == BillingKind.Metered && row.ReservedMicros is { } worst)
        {
            reserved = SpendMoney.Format(worst, 4);
            reservedTitle = row.Price is { } price
                ? $"Worst case: {SpendEstimateText.Tokens(row.InputTokens)} in × {SpendEstimateText.Rate(price.WorstInput)} + {SpendEstimateText.Tokens(row.OutputTokens)} max out × {SpendEstimateText.Rate(price.Output)}"
                : "Worst case reserved before the request";
        }
        else
        {
            reserved = "—";
            reservedTitle = row.Billing switch
            {
                BillingKind.Subscription => "Subscription: not reserved in dollars and never counted toward money caps",
                BillingKind.Free => "Free: nothing to reserve",
                _ => "No price: the worst case could not be worked out",
            };
        }
        var (actual, actualSub, actualTitle) = Actual(row);
        var (status, tone) = row.Status switch
        {
            LedgerStatus.Settled => ("settled", (string?)null),
            LedgerStatus.Estimate => ("estimate", "outline"),
            LedgerStatus.Uncertain => ("uncertain", "warn"),
            LedgerStatus.Released => ("released", null),
            _ => ("reserved", "info"),
        };
        var notes = new List<string>();
        if (row.State == ReservationState.Uncertain && row.Reason is { Length: > 0 } reason) notes.Add(reason);
        else if (row.State == ReservationState.Released && row.Reason is { Length: > 0 } why) notes.Add(why);
        if (row.ResolvedByUser) notes.Add("resolved by you");
        if (row.Note is { Length: > 0 } note) notes.Add(note);
        return new SpendLedgerRow(row, created.ToString("HH:mm:ss", CultureInfo.InvariantCulture), day, SourceIcon(row.Source), row.Source.Label(),
            account.Length == 0 ? mode : account + " · " + mode, reserved, reservedTitle, actual, actualSub, actualTitle, status, tone,
            notes.Count == 0 ? null : string.Join(" · ", notes),
            row.Source == SpendSource.Chat && row.Run is { Length: > 0 } run ? "/chat/" + Uri.EscapeDataString(run) : null,
            row.State == ReservationState.Uncertain);
    }

    private static (string Text, string? Sub, string? Title) Actual(Reservation row)
    {
        switch (row.State)
        {
            case ReservationState.Uncertain:
                return ("—", null, "No usage reported. The reserved amount keeps counting until you resolve it.");
            case ReservationState.Reserved:
                return ("—", null, "Still running: the worst case is held until usage arrives.");
            case ReservationState.Released:
                return ("—", null, "Released: nothing counted.");
        }
        var micros = row.ActualMicros ?? 0;
        var how = row.Settlement switch
        {
            SettlementKind.Estimate => row.ResolvedByUser ? "Settled at the estimate when you resolved it" : "Settled at an estimate",
            SettlementKind.Entered => "Settled at the amount you entered",
            _ => row.Usage is { HasTokens: true } usage && row.Price is { } price ? "Settled from reported usage: " + Usage(usage, price) : "Settled from reported usage",
        };
        return row.Billing switch
        {
            BillingKind.Metered => (SpendMoney.Format(micros, 4), null, how),
            BillingKind.Subscription => ("≈ " + SpendMoney.Format(micros, 4), "list", "List price · billed through the subscription, not counted toward spend caps"),
            BillingKind.Free => ("free", null, "Free: no cost"),
            _ => ("price unknown", null, row.Usage is { HasTokens: true } tokens
                ? $"No price: {SpendEstimateText.Tokens(tokens.InputTokens)} in · {SpendEstimateText.Tokens(tokens.OutputTokens)} out recorded without a cost" : "No price: tokens only"),
        };
    }

    private static string Usage(SpendUsage usage, SpendPrice price)
    {
        var parts = new List<string> { $"{SpendEstimateText.Tokens(usage.InputTokens)} in × {SpendEstimateText.Rate(price.Input)}" };
        if (usage.CacheReadTokens > 0) parts.Add($"{SpendEstimateText.Tokens(usage.CacheReadTokens)} cache read × {SpendEstimateText.Rate(price.CacheRead)}");
        if (usage.CacheWriteTokens > 0) parts.Add($"{SpendEstimateText.Tokens(usage.CacheWriteTokens)} cache write × {SpendEstimateText.Rate(price.CacheWrite)}");
        parts.Add($"{SpendEstimateText.Tokens(usage.OutputTokens)} out × {SpendEstimateText.Rate(price.Output)}");
        return string.Join(" + ", parts);
    }

    /// <summary>The Bootstrap icon of a source, as the sidebar shows it.</summary>
    public static string SourceIcon(SpendSource source) => source switch
    {
        SpendSource.Chat => "bi-chat-square-text",
        SpendSource.Playground => "bi-sliders2",
        SpendSource.Compare or SpendSource.AgentCompare => "bi-layout-three-columns",
        SpendSource.Bench => "bi-tools",
        SpendSource.Arena => "bi-trophy",
        SpendSource.Drift => "bi-activity",
        _ => "bi-thermometer-half",
    };
}

/// <summary>History › Spend's filters, from the query string (<c>q</c>, <c>from</c>, <c>to</c>, <c>provider</c>, <c>model</c>,
/// <c>source</c>, <c>account</c>, <c>status</c>, <c>page</c>, <c>size</c>; <c>resolve</c> opens the Resolve modal of that row). Dates are
/// UTC days; <c>to</c> is inclusive.</summary>
public sealed record SpendHistoryFilter
{
    public static readonly IReadOnlyList<int> Sizes = [25, 50, 100];

    public string? Search { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public SpendSource? Source { get; init; }

    public string? Account { get; init; }

    public LedgerStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int Size { get; init; } = 25;

    public string? Resolve { get; init; }

    /// <summary>Whether any filter narrows the ledger (paging aside).</summary>
    public bool Active => Search is not null || From is not null || To is not null || Provider is not null || Model is not null || Source is not null
        || Account is not null || Status is not null;

    /// <summary>The filters of a query string; values that are not allowed are left out.</summary>
    public static SpendHistoryFilter Parse(Func<string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        static string? Text(string? value, int max = 200) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { Length: var length } trimmed && length <= max ? trimmed : null;
        static DateOnly? Day(string? value) =>
            DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
        var provider = Text(get("provider"));
        var from = Day(get("from"));
        var to = Day(get("to"));
        if (from is { } start && to is { } end && end < start) (from, to) = (to, from);
        var size = int.TryParse(get("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var rows) && Sizes.Contains(rows) ? rows : 25;
        var page = int.TryParse(get("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 1_000_000 ? number : 1;
        return new SpendHistoryFilter
        {
            Search = Text(get("q")),
            From = from,
            To = to,
            Provider = provider,
            Model = provider is null ? null : Text(get("model")),
            Source = SpendSources.TryParse(get("source"), out var source) ? source : null,
            Account = Text(get("account")),
            Status = Enum.GetValues<LedgerStatus>().Select(status => (LedgerStatus?)status).FirstOrDefault(status => StatusWire(status!.Value) == get("status")?.Trim()),
            Page = page,
            Size = size,
            Resolve = Text(get("resolve")),
        };
    }

    /// <summary>A status on the wire: <c>settled</c>, <c>estimate</c>, <c>uncertain</c>, <c>released</c>, <c>reserved</c>.</summary>
    public static string StatusWire(LedgerStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>The ledger query of this page.</summary>
    public SpendLedgerQuery Query() => new()
    {
        From = From is { } from ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        To = To is { } to ? new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Provider = Provider,
        Model = Model,
        Source = Source,
        Account = Account,
        Status = Status,
        Search = Search,
        Offset = (int)Math.Min(int.MaxValue, (long)(Page - 1) * Size),
        Limit = Size,
    };

    /// <summary>The query string of these filters (paging and size kept unless they are the defaults), with a leading <c>?</c> or empty.</summary>
    public string QueryString(bool paging = true)
    {
        var pairs = new List<KeyValuePair<string, string?>>();
        void Add(string key, string? value)
        {
            if (value is not null) pairs.Add(new(key, value));
        }
        Add("q", Search);
        Add("from", From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("to", To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add("provider", Provider);
        Add("model", Model);
        Add("source", Source?.Wire());
        Add("account", Account);
        Add("status", Status is { } status ? StatusWire(status) : null);
        if (paging && Page > 1) Add("page", Page.ToString(CultureInfo.InvariantCulture));
        if (paging && Size != 25) Add("size", Size.ToString(CultureInfo.InvariantCulture));
        return Microsoft.AspNetCore.Http.QueryString.Create(pairs).ToUriComponent();
    }

    /// <summary>History › Spend with these filters.</summary>
    public string Href => "/history/spend" + QueryString();
}

/// <summary>History › Spend.</summary>
public sealed record SpendHistoryPage
{
    public required SpendHistoryFilter Filter { get; init; }

    public required SpendLedgerPage Ledger { get; init; }

    public required IReadOnlyList<SpendLedgerRow> Rows { get; init; }

    public required SpendSummaryView Summary { get; init; }

    /// <summary>The newest uncertain rows (at most three), for the banner.</summary>
    public required IReadOnlyList<SpendLedgerRow> Uncertain { get; init; }

    public int UncertainCount { get; init; }

    public long UncertainMicros { get; init; }

    public required SpendFilterOptions Options { get; init; }

    public required string DatabasePath { get; init; }

    /// <summary>The Resolve modal to open when the page loads (<c>?resolve=</c>), or null.</summary>
    public ResolveReservationView? Resolve { get; init; }

    /// <summary>The active filters as chips, each with the address without it.</summary>
    public IReadOnlyList<SpendFilterChip> Chips { get; init; } = [];

    /// <summary>The saved filters (the Saved filters menu and the Manage modal).</summary>
    public IReadOnlyList<SpendSavedFilterView> Saved { get; init; } = [];

    /// <summary>The name the Save current filters modal suggests for the active filters.</summary>
    public string SaveName { get; init; } = "";

    /// <summary>Whether the ledger holds no row at all.</summary>
    public bool Empty => Ledger.TotalAll == 0;

    public int Pages => Math.Max(1, (Ledger.Total + Filter.Size - 1) / Filter.Size);

    /// <summary>The CSV of every row matching the filters.</summary>
    public string CsvHref => SpendEndpoints.Prefix + "/ledger.csv" + Filter.QueryString(paging: false);
}

/// <summary>One cell of the summary: an amount, the cap it is measured against (with a progress bar) or none, and its sub line.</summary>
public sealed record SpendMeter(string Label, long Micros, SpendCapStatus? Cap, string Sub, string? Of, IReadOnlyList<SpendCapStatus> Warnings)
{
    /// <summary>The share of the cap, when there is one.</summary>
    public int Percent { get; init; }
}

/// <summary>The summary card.</summary>
public sealed record SpendSummaryView(bool Filtered, IReadOnlyList<SpendMeter> Meters, string CacheLabel, SpendCacheMeter Cache, string? NotCounted);

/// <summary>A group of a dropdown.</summary>
public sealed record SpendOptionGroup(string Label, IReadOnlyList<SpendOption> Options);

/// <summary>The filters' choices.</summary>
public sealed record SpendFilterOptions(IReadOnlyList<SpendOption> Providers, IReadOnlyList<SpendOption> Models, IReadOnlyList<SpendOptionGroup> Accounts);

/// <summary>One ledger row as the table shows it.</summary>
public sealed record SpendLedgerRow(Reservation Row, string Time, string Day, string SourceIcon, string SourceLabel, string AccountText, string Reserved,
    string? ReservedTitle, string Actual, string? ActualSub, string? ActualTitle, string Status, string? StatusTone, string? Note, string? ChatHref, bool CanResolve)
{
    public string Op => Row.Op;

    /// <summary>The row's time in the host's time zone, as History shows its runs (the time cell's title). The ledger's own times are UTC, the
    /// days its caps and date range use (after the W5 live test, which found History and the ledger a time zone apart with nothing saying so).</summary>
    public string LocalTime => "Local time " + Row.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>W5: the page of the Arena battle (held or ready) this request belongs to, or null. A blind row's <see cref="Row"/> is masked
    /// (<see cref="SpendPages.MaskBlind"/>).</summary>
    public string? Blind { get; init; }
}

/// <summary>The Resolve modal of one uncertain row.</summary>
public sealed record ResolveReservationView
{
    public required Reservation Row { get; init; }

    public required SpendLedgerRow Shown { get; init; }

    /// <summary><c>chat · today 11:24:12</c>.</summary>
    public required string Subtitle { get; init; }

    /// <summary><c>anthropic · key sk-ant-…Q7x2</c>.</summary>
    public required string Account { get; init; }

    /// <summary><c>today, this month and anthropic monthly</c>.</summary>
    public required string CountsToward { get; init; }

    /// <summary>How the reservation was worked out (three lines), or null without a price.</summary>
    public string? Formula { get; init; }

    /// <summary>What settling at the estimate counts.</summary>
    public long EstimateMicros { get; init; }

    public required string EstimateText { get; init; }

    public string? AfterEstimate { get; init; }

    /// <summary>What the usage the provider reported for the row costs (a recovered request's resend), or null when nothing was reported:
    /// the modal then offers no "settle at the reported cost".</summary>
    public long? ReportedMicros { get; init; }

    /// <summary>What was reported, in words, or null.</summary>
    public string? ReportedText { get; init; }

    /// <summary>What settling at the reported cost counts.</summary>
    public string? AfterReported { get; init; }

    public string? AfterRelease { get; init; }
}
