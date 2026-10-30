// Decision 0011 (run W3, S4): what the Spend pages show, worked out from the ledger (caps, their spend, reservations), the spend settings,
// the billing modes and the prices; never a number nothing measured. Settings › Spend (SpendSettingsPage): the caps with their spend in the
// current UTC window, every account's billing mode, the models without a price, the reservation settings and the uncertain spend. The
// provider pages' account cards (SpendAccountLine): today's metered spend of that account against the daily cap, or the subscription note.
// History › Spend is SpendPages.History.cs, the confirm modal SpendConfirm.cs. Accounts are named by label, kind and masked hint only.
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Spend;

/// <summary>The read side of the Spend pages. A singleton; safe to call from many threads.</summary>
public sealed partial class SpendPages
{
    /// <summary>The input of the worked example under Reservations ("12,000 input tokens").</summary>
    public const int ExampleInputTokens = 12_000;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly ISpendLedger ledger;
    private readonly ISettingsStore settings;
    private readonly IModelCatalog catalog;
    private readonly BillingModes billing;
    private readonly ModelPrices prices;
    private readonly CostEstimator estimator;
    private readonly IAccountStore? accounts;
    private readonly IServiceProvider services;
    private readonly TimeProvider clock;
    private readonly ILogger log;

    public SpendPages(ISpendLedger ledger, ISettingsStore settings, IModelCatalog catalog, BillingModes billing, ModelPrices prices, CostEstimator estimator,
        WebAgentOptions options, IServiceProvider services, IAccountStore? accounts = null, TimeProvider? clock = null, ILogger<SpendPages>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.billing = billing ?? throw new ArgumentNullException(nameof(billing));
        this.prices = prices ?? throw new ArgumentNullException(nameof(prices));
        this.estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.accounts = accounts;
        this.clock = clock ?? TimeProvider.System;
        this.log = log ?? (ILogger)NullLogger<SpendPages>.Instance;
        DatabasePath = Path.Join(options.DataDirectory, "webagent.db");
    }

    /// <summary>The ledger's file, for the empty state.</summary>
    public string DatabasePath { get; }

    /// <summary>Now, in UTC.</summary>
    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>The ledger the pages read and the API writes.</summary>
    public ISpendLedger Ledger => ledger;

    /// <summary>The spend settings now.</summary>
    public SpendSettings Settings => settings.Get<SpendSettings>();

    /// <summary>Settings › Spend.</summary>
    public async Task<SpendSettingsPage> SettingsPageAsync(CancellationToken cancellationToken = default)
    {
        var spend = Settings;
        var statuses = await ledger.CapStatusAsync(null, cancellationToken).ConfigureAwait(false);
        var rows = billing.Accounts();
        var uncertain = await ledger.TotalAsync(new SpendLedgerQuery { Status = LedgerStatus.Uncertain }, cancellationToken).ConfigureAwait(false);
        var latest = uncertain.Requests == 0 ? null
            : (await ledger.QueryAsync(new SpendLedgerQuery { Status = LedgerStatus.Uncertain, Limit = 1 }, cancellationToken).ConfigureAwait(false)).Items.FirstOrDefault();
        return new SpendSettingsPage
        {
            Settings = spend,
            Caps = [.. statuses.Select(CapRow)],
            Accounts = [.. rows.Select(row => AccountRow(row, rows))],
            Unknown = UnknownRows(rows, spend),
            UncertainCount = uncertain.Requests,
            UncertainMicros = uncertain.CountedMicros,
            LatestUncertain = latest is null ? null : LedgerRow(latest),
            Example = Example(rows, statuses),
            CapModal = CapModal(rows, spend),
        };
    }

    /// <summary>The cap modal's choices: metered providers and accounts, every source.</summary>
    public SpendCapModalView CapModal() => CapModal(billing.Accounts(), Settings);

    /// <summary>One row of the Caps table.</summary>
    public SpendCapRow CapRow(SpendCapStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var cap = status.Cap;
        var scope = ScopeName(cap);
        var used = cap.Period switch
        {
            CapPeriod.Request => "Worst case checked per request",
            CapPeriod.Run => "Checked against each run's spend",
            _ => status.SpentMicros is { } spent ? $"{SpendMoney.Format(spent)} · {status.PercentShown}%" : "—",
        };
        return new SpendCapRow(status, cap.PeriodLabel, scope, cap.PeriodLabel + " · " + (cap.Scope == CapScope.Everything ? "all metered" : scope),
            cap.Scope switch
            {
                CapScope.Everything => "Everything metered",
                CapScope.Provider => "Provider · " + scope,
                CapScope.Account => "Account · " + scope,
                _ => "Source · " + scope,
            },
            cap.Period switch
            {
                CapPeriod.Request => "Each request",
                CapPeriod.Run => "Each run (a chat counts as one run)",
                CapPeriod.Day => "Day (UTC)",
                _ => "Month (UTC)",
            }, used);
    }

    /// <summary>What a cap applies to, for people: <c>Everything metered</c>, <c>anthropic</c>, <c>anthropic · Work</c>, <c>Chat</c>.</summary>
    public string ScopeName(SpendCap cap)
    {
        ArgumentNullException.ThrowIfNull(cap);
        return cap.Scope switch
        {
            CapScope.Everything => "Everything metered",
            CapScope.Provider => cap.Key ?? "",
            CapScope.Account => cap.Key is { } id && accounts?.Get(id) is { } account ? account.Provider + " · " + AccountText(account) : "One account",
            _ => SpendSources.TryParse(cap.Key, out var source) ? source.Label() : cap.Key ?? "",
        };
    }

    /// <summary>A cap's name in a sentence: <c>daily cap</c> (everything metered), <c>anthropic monthly cap</c>, <c>per-request cap</c>.</summary>
    public string CapName(SpendCap cap)
    {
        ArgumentNullException.ThrowIfNull(cap);
        var period = cap.Period switch
        {
            CapPeriod.Request => "per-request",
            CapPeriod.Run => cap.Scope == CapScope.Source && cap.Key == SpendSource.Chat.Wire() ? "per-chat" : "per-run",
            CapPeriod.Day => "daily",
            _ => "monthly",
        };
        return cap.Scope == CapScope.Everything ? period + " cap" : ScopeName(cap) + " " + period + " cap";
    }

    /// <summary>How an account shows in the ledger and the filters: <c>key sk-ant-…Q7x2</c>, <c>env OPENAI_API_KEY</c>, <c>Personal</c>.
    /// Empty for none; a removed account by a short id.</summary>
    public string AccountText(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return "";
        return accounts?.Get(accountId) is { } account ? AccountText(account) : "account " + (accountId.Length > 8 ? accountId[..8] + "…" : accountId);
    }

    /// <summary>Whether the account store holds <paramref name="accountId"/>.</summary>
    public bool AccountExists(string accountId) => !string.IsNullOrWhiteSpace(accountId) && accounts?.Get(accountId.Trim()) is not null;

    internal static string AccountText(AccountInfo account) => account.Kind switch
    {
        AccountKind.ApiKey => account.Hint.Length > 0 ? "key " + account.Hint : account.Label,
        AccountKind.Environment => "env " + (account.Source ?? account.Label),
        AccountKind.Ambient => "ambient " + (account.Source ?? account.Label),
        _ => account.Label,
    };

    /// <summary>A billing mode for people: Metered, Subscription, Free, Unknown price.</summary>
    public static string ModeText(BillingKind mode) => mode switch
    {
        BillingKind.Metered => "Metered",
        BillingKind.Subscription => "Subscription",
        BillingKind.Free => "Free",
        _ => "Unknown price",
    };

    /// <summary>A billing mode on the wire (<c>metered</c>, <c>subscription</c>, <c>free</c>, <c>unknown</c>).</summary>
    public static string ModeWire(BillingKind mode) => mode.ToString().ToLowerInvariant();

    /// <summary>The spend line of an account card on a provider page: today's metered spend against the daily cap, or the subscription
    /// note; null for a free account, or when the ledger cannot be read (the provider page still renders).</summary>
    public async Task<SpendAccountLine?> AccountLineAsync(string provider, AccountInfo account, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            var mode = billing.Resolve(provider, account.Id).Mode;
            if (mode == BillingKind.Subscription)
                return new SpendAccountLine(BillingKind.Subscription, "Subscription · list cost not counted", null, null, null,
                    "Requests show an ≈ list cost for comparison. It never counts toward spend caps.");
            if (mode == BillingKind.Unknown)
                return new SpendAccountLine(BillingKind.Unknown, "Unknown price · ", "not checked against caps", null, "/settings/spend#unknown-prices",
                    "No price is known for this provider's models, so its requests can't be checked against caps.");
            if (mode != BillingKind.Metered) return null;
            var today = SpendWindows.DayStart(Now);
            var own = await ledger.TotalAsync(new SpendLedgerQuery { From = today, Provider = provider, Account = account.Id }, cancellationToken).ConfigureAwait(false);
            var cap = (await ledger.CapStatusAsync(null, cancellationToken).ConfigureAwait(false))
                .Where(status => status.Cap is { Enabled: true, Period: CapPeriod.Day, Scope: not CapScope.Source } && status.Cap.AppliesTo(provider, account.Id, SpendSource.Chat)
                    && status.SpentMicros is not null)
                .OrderBy(status => status.LeftMicros).FirstOrDefault();
            var spent = SpendMoney.Format(own.CountedMicros);
            if (cap is null)
                return new SpendAccountLine(BillingKind.Metered, $"Today {spent} · ", "no daily cap", null, "/settings/spend#caps",
                    $"Metered. This account used {spent} today; no daily cap applies to it.");
            var shared = cap.Cap.Scope == CapScope.Everything;
            var limit = SpendMoney.Format(cap.Cap.LimitMicros);
            var used = SpendMoney.Format(cap.SpentMicros ?? 0);
            return new SpendAccountLine(BillingKind.Metered, $"Today {spent} · ", shared ? "shared daily cap" : "daily cap", $" {used} / {limit}", "/settings/spend#caps",
                shared ? $"Metered. The {limit} daily cap is shared by all metered providers; this account used {spent} of the {used} spent today."
                    : $"Metered. The {limit} daily cap applies to {ScopeName(cap.Cap)}; this account used {spent} of the {used} spent under it today.")
            {
                Tone = cap.AtLimit ? "danger" : cap.Warning ? "warn" : null,
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "The spend line of account {Account} could not be worked out.", account.Id);
            return null;
        }
    }

    private SpendAccountRow AccountRow(BillingAccount row, IReadOnlyList<BillingAccount> rows)
    {
        var account = row.Account;
        var several = rows.Count(other => other.Provider == row.Provider && other.Account is not null) > 1;
        var name = account is not null && (account.Kind == AccountKind.OAuth || several) ? row.ProviderName + " · " + account.Label : row.ProviderName;
        var hint = account is { Kind: AccountKind.ApiKey, Hint.Length: > 0 } ? account.Hint : null;
        var logo = ProviderLogos.Url(row.Provider) ?? (row.LogoKey is { } key ? ProviderLogos.Url(key) : null);
        return new SpendAccountRow(row, name, logo, row.Billing.Basis, hint);
    }

    private IReadOnlyList<SpendUnknownRow> UnknownRows(IReadOnlyList<BillingAccount> rows, SpendSettings spend)
    {
        var found = new List<SpendUnknownRow>();
        foreach (var provider in rows.Where(row => row.Billing.Mode != BillingKind.Free).Select(row => row.Provider).Distinct(StringComparer.Ordinal))
        {
            var unpriced = prices.Unpriced(provider);
            // A provider whose account is Unknown price but that has no model in the catalog (none to price) still gets its row, so the
            // account's "see below" leads somewhere and its requests' behaviour can be set.
            var unknownAccount = rows.Any(row => row.Provider == provider && row.Billing.Mode == BillingKind.Unknown);
            if (unpriced.Count == 0 && !unknownAccount) continue;
            var rule = spend.UnknownPriceRules?.FirstOrDefault(existing => existing.Provider == provider);
            found.Add(new SpendUnknownRow(provider, catalog.FindProvider(provider)?.DisplayName ?? provider, unpriced, spend.UnknownPriceFor(provider), rule is not null));
        }
        return found;
    }

    private SpendCapModalView CapModal(IReadOnlyList<BillingAccount> rows, SpendSettings spend)
    {
        var metered = rows.Where(row => row.Billing.Mode == BillingKind.Metered).ToList();
        return new SpendCapModalView(
            [.. metered.GroupBy(row => row.Provider, StringComparer.Ordinal).Select(group => new SpendOption(group.Key, group.First().ProviderName))],
            [.. metered.Where(row => row.Account is not null).Select(row => new SpendAccountOption(row.Provider, row.ProviderName, row.Account!.Id, AccountOptionText(row.Account!)))],
            spend.WarnPercent);
    }

    private static string AccountOptionText(AccountInfo account) => account.Kind switch
    {
        AccountKind.ApiKey => (account.Label.Length > 0 ? account.Label + " · " : "") + "Stored key" + (account.Hint.Length > 0 ? " · " + account.Hint : ""),
        AccountKind.Environment => "Environment · " + (account.Source ?? account.Label),
        AccountKind.Ambient => "Ambient · " + (account.Source ?? account.Label),
        _ => account.Label,
    };

    private SpendExample? Example(IReadOnlyList<BillingAccount> rows, IReadOnlyList<SpendCapStatus> statuses)
    {
        // 32,727 UTF-8 bytes are 12,000 input tokens by the estimator's rule (bytes ÷ 3, plus 10%, rounded up).
        var bytes = (long)Math.Floor(ExampleInputTokens * 30.0 / 11);
        foreach (var row in rows.Where(row => row.Billing.Mode == BillingKind.Metered))
        {
            foreach (var model in catalog.Models(row.Provider))
            {
                if (prices.Find(row.Provider, model.Id) is not { IsFree: false }) continue;
                SpendEstimate estimate;
                try { estimate = estimator.Estimate(new SpendEstimateInput(row.Provider, model.Id, row.Account?.Id, bytes)); }
                catch (ArgumentException) { continue; }
                if (!estimate.CountsTowardCaps || SpendEstimateText.Formula(estimate) is not { } formula) continue;
                return new SpendExample(row.Provider + "/" + model.Id, estimate, formula, FitSentence(estimate, statuses));
            }
        }
        return null;
    }

    /// <summary>Which caps a request of <paramref name="estimate"/> from a chat fits and which it is over: <c>Fits the $0.50 per-request cap
    /// and the $1.90 left on the anthropic monthly cap.</c> Null when no cap applies.</summary>
    internal string? FitSentence(SpendEstimate estimate, IReadOnlyList<SpendCapStatus> statuses)
    {
        if (estimate.WorstMicros is not { } worst) return null;
        var fits = new List<string>();
        var over = new List<string>();
        foreach (var status in statuses.Where(status => status.Cap.Enabled && status.Cap.AppliesTo(estimate.Provider, estimate.Account, SpendSource.Chat)))
        {
            var cap = status.Cap;
            if (cap.Period == CapPeriod.Request)
                (worst <= cap.LimitMicros ? fits : over).Add($"the {SpendMoney.Format(cap.LimitMicros)} {CapName(cap)}");
            else if (cap.Period is CapPeriod.Day or CapPeriod.Month && status.LeftMicros is { } left)
                (worst <= left ? fits : over).Add($"the {SpendMoney.Format(left)} left on the {CapName(cap)}");
        }
        if (fits.Count + over.Count == 0) return null;
        var text = fits.Count > 0 ? "Fits " + Join(fits) + "." : "";
        if (over.Count > 0) text += (text.Length > 0 ? " " : "") + "Over " + Join(over) + ".";
        return text;
    }

    internal static string Join(IReadOnlyList<string> parts) =>
        parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    internal static string Count(int count) => count.ToString("#,0", Invariant);
}

/// <summary>Settings › Spend.</summary>
public sealed record SpendSettingsPage
{
    public required SpendSettings Settings { get; init; }

    public required IReadOnlyList<SpendCapRow> Caps { get; init; }

    public required IReadOnlyList<SpendAccountRow> Accounts { get; init; }

    public required IReadOnlyList<SpendUnknownRow> Unknown { get; init; }

    public int UncertainCount { get; init; }

    public long UncertainMicros { get; init; }

    public SpendLedgerRow? LatestUncertain { get; init; }

    public SpendExample? Example { get; init; }

    public required SpendCapModalView CapModal { get; init; }
}

/// <summary>One cap of the Caps table, with the texts its edit pane shows.</summary>
/// <param name="Title">Per request, Per run, Daily, Monthly.</param>
/// <param name="Scope">What it applies to (<see cref="SpendPages.ScopeName"/>).</param>
/// <param name="EditTitle">The edit pane's subtitle: <c>Monthly · anthropic</c>.</param>
/// <param name="ScopeField">The edit pane's "Applies to": <c>Provider · anthropic</c>.</param>
/// <param name="WindowField">The edit pane's "Window": <c>Month (UTC)</c>.</param>
/// <param name="UsedField">The edit pane's "Spent": <c>$8.10 · 81%</c>.</param>
public sealed record SpendCapRow(SpendCapStatus Status, string Title, string Scope, string EditTitle, string ScopeField, string WindowField, string UsedField)
{
    public SpendCap Cap => Status.Cap;

    /// <summary>Whether its spend is measured (a day or month window).</summary>
    public bool Windowed => Cap.Period is CapPeriod.Day or CapPeriod.Month;
}

/// <summary>One account of the Accounts section.</summary>
public sealed record SpendAccountRow(BillingAccount Row, string Name, string? LogoUrl, string Basis, string? Hint)
{
    public string Provider => Row.Provider;

    public string? AccountId => Row.Account?.Id;

    public BillingResolution Billing => Row.Billing;
}

/// <summary>A provider with models that have no price, and what happens to their requests.</summary>
public sealed record SpendUnknownRow(string Provider, string Name, IReadOnlyList<UnpricedModel> Models, UnknownPriceBehavior Behavior, bool HasRule);

/// <summary>The worked example under Reservations.</summary>
public sealed record SpendExample(string Model, SpendEstimate Estimate, string Formula, string? Fit);

/// <summary>A choice of a dropdown.</summary>
public sealed record SpendOption(string Value, string Text);

/// <summary>An account choice of the cap modal (metered accounts only).</summary>
public sealed record SpendAccountOption(string Provider, string ProviderName, string Id, string Text);

/// <summary>The cap modal's choices.</summary>
public sealed record SpendCapModalView(IReadOnlyList<SpendOption> Providers, IReadOnlyList<SpendAccountOption> Accounts, int WarnPercent);

/// <summary>An account card's spend line: <see cref="Text"/>, then <see cref="LinkText"/> as a link to <see cref="Href"/>, then
/// <see cref="After"/>; <see cref="Title"/> explains it.</summary>
public sealed record SpendAccountLine(BillingKind Mode, string Text, string? LinkText, string? After, string? Href, string Title)
{
    /// <summary>warn above the warning threshold of the daily cap, danger at it; null otherwise.</summary>
    public string? Tone { get; init; }
}
