// Decision 0011 (run W3, S5): what the chat pages show of the Spend Guard, worked out from the guard's decisions and the ledger's rows, never
// from anything unmeasured. The composer's estimate (_ChatSpendEstimate): the chip by Send ("≈ $0.03", "≈ $0.21 list" for a subscription,
// "price unknown", nothing for a free model), its tooltip (typical, the worst case worked out, the output and tier notes, how the account
// is billed, the caps near their limit) and, when the guard would ask or block, the over-cap chip named after the cause ("Over request
// cap", "Over chat cap", "Over daily cap", "Over monthly cap", "No price"). The answers' footer marks: metered "$0.0357", list price
// "≈ $0.0509 list", settled at an estimate "≈ $0.0213 · est.", uncertain (a chip, the amount reserved and Resolve… on History › Spend), price
// unknown, and the running answer's "≈ $0.048 · est." from the tokens received so far. A footer finds its ledger row by the run (the chat
// id) and the reported usage, else by provider, model and time (an answer without usage: dropped, stopped or failed after output); the
// stored entries carry no op. A call the guard stopped inside the run has no row: its entry's error names the op (ChatSpendStopView).
// The session panel's chat spend (ChatSpendSummary) is the ledger's total of the run and its per-run caps. Text set here is plain; the
// tooltip's markup is built from encoded parts only.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Client;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Chat;

/// <summary>The composer's estimate by Send (<c>Views/Chat/_ChatSpendEstimate.cshtml</c>).</summary>
public sealed record ChatSpendEstimateView
{
    /// <summary>Nothing to show (a free model, or no Spend Guard).</summary>
    public static ChatSpendEstimateView None { get; } = new();

    /// <summary>The chip: <c>≈ $0.03</c>, <c>≈ $0.21 list</c> or <c>price unknown</c>; null shows none.</summary>
    public string? Chip { get; init; }

    /// <summary>The over-cap chip (<c>Over request cap</c>...), or null when the guard allows the request.</summary>
    public string? Over { get; init; }

    /// <summary><c>allow</c>, <c>ask</c> or <c>block</c>.</summary>
    public string Verdict { get; init; } = "allow";

    /// <summary>The confirm modal's pane of the first cause, or null.</summary>
    public string? Pane { get; init; }

    /// <summary>The spend error code, or null when allowed.</summary>
    public string? Code { get; init; }

    /// <summary>The tooltip, as markup built from encoded parts (<c>strong</c> and <c>br</c> only).</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>The chip's accessible name.</summary>
    public string Label { get; init; } = "";

    /// <summary>Caps at or over their warning threshold with this request counted.</summary>
    public IReadOnlyList<ChatSpendWarning> Warnings { get; init; } = [];

    /// <summary>Whether there is anything to show.</summary>
    public bool Shown => Chip is not null || Over is not null || Warnings.Count > 0;
}

/// <summary>A cap near its limit: <c>81% of anthropic monthly cap</c>, with what it has used on hover.</summary>
/// <param name="Text">The chip.</param>
/// <param name="Title">The tooltip.</param>
public sealed record ChatSpendWarning(string Text, string Title);

/// <summary>The names an estimate's text uses.</summary>
/// <param name="ProviderName">The provider's display name (or id).</param>
/// <param name="AccountLabel">The account's label, or null.</param>
public sealed record ChatSpendNames(string ProviderName, string? AccountLabel)
{
    /// <summary>A cap's name in a sentence (<c>per-request cap</c>, <c>anthropic monthly cap</c>); null uses <see cref="ChatSpendText.CapName"/>.</summary>
    public Func<SpendCap, string>? CapName { get; init; }
}

/// <summary>What an answer's footer says about its cost.</summary>
public enum ChatSpendMarkKind
{
    /// <summary>Metered: what it cost and counts toward the caps.</summary>
    Metered,

    /// <summary>A subscription's list price, never counted.</summary>
    List,

    /// <summary>Settled at an estimate from what was received.</summary>
    Estimate,

    /// <summary>The stream ended without usage: the reserved amount counts until the user resolves it.</summary>
    Uncertain,

    /// <summary>The model has no price.</summary>
    UnknownPrice,

    /// <summary>The running answer, from the tokens received so far.</summary>
    Live,
}

/// <summary>An answer's cost in its footer.</summary>
/// <param name="Kind">What kind of figure it is.</param>
/// <param name="Text">The figure: <c>$0.0357</c>, <c>≈ $0.0509 list</c>, <c>≈ $0.0213 · est.</c>, <c>$0.38 reserved</c>, <c>price unknown</c>.</param>
/// <param name="Title">How it was worked out.</param>
public sealed record ChatSpendMark(ChatSpendMarkKind Kind, string Text, string Title)
{
    /// <summary>The ledger row's op.</summary>
    public string? Op { get; init; }

    /// <summary>A chip before the figure (<c>uncertain</c>), or null.</summary>
    public string? Chip { get; init; }

    /// <summary>The chip's tooltip.</summary>
    public string? ChipTitle { get; init; }

    /// <summary>Where to resolve an uncertain reservation (History › Spend's Resolve), or null.</summary>
    public string? ResolveUrl { get; init; }
}

/// <summary>The ledger facts of a branch's answers, by entry id, and the running request's reservation.</summary>
public sealed class ChatSpendFacts
{
    /// <summary>No facts (no ledger).</summary>
    public static ChatSpendFacts Empty { get; } = new();

    /// <summary>The footer marks by assistant entry id.</summary>
    public IReadOnlyDictionary<long, ChatSpendMark> Marks { get; init; } = new Dictionary<long, ChatSpendMark>();

    /// <summary>The newest reservation still held while the chat runs (the running answer's), or null.</summary>
    public Reservation? Live { get; init; }

    /// <summary>The mark of entry <paramref name="entryId"/>, or null.</summary>
    public ChatSpendMark? For(long? entryId) => entryId is { } id && Marks.TryGetValue(id, out var mark) ? mark : null;

    /// <summary>The running answer's mark from the tokens received so far, or null.</summary>
    public ChatSpendMark? LiveFor(AssistantItem item) => item is not null && Live is { } row ? ChatSpendText.Live(row, ChatSpendText.StreamedTokens(item)) : null;

    /// <summary>What an item's footer depends on (for the stream's fingerprint).</summary>
    public string Print(TranscriptItem item) => item switch
    {
        AssistantItem { Streaming: true } live => "live:" + LiveFor(live),
        AssistantItem answer => "mark:" + For(answer.Id),
        _ => "",
    };
}

/// <summary>A call the Spend Guard stopped inside the run (the stop card and the banner).</summary>
/// <param name="Code"><see cref="SpendErrors.Blocked"/>, <see cref="SpendErrors.Ask"/> or <see cref="SpendErrors.UnknownPrice"/>.</param>
/// <param name="Text">Why, as the guard said it.</param>
/// <param name="Op">The stopped call's op, or null.</param>
/// <param name="EntryId">The answer entry that records the stop, or null.</param>
public sealed record ChatSpendStopView(string Code, string Text, string? Op, long? EntryId)
{
    /// <summary>Whether the user may answer it (proceed once, or cap the output); a Block may still fit with a shorter output.</summary>
    public bool CanProceed => Code != SpendErrors.Blocked;

    /// <summary>The card's heading.</summary>
    public string Title => Code switch
    {
        SpendErrors.Blocked => "Stopped by a spend cap",
        SpendErrors.UnknownPrice => "No price for this model",
        _ => "Over a spend cap · waiting for your OK",
    };

    /// <summary>Settings › Spend, at the part that changes this.</summary>
    public string SettingsUrl => Code == SpendErrors.UnknownPrice ? "/settings/spend#unknown-prices" : "/settings/spend#caps";

    /// <summary>The stop in <paramref name="errorMessage"/>, or null when it is not a spend error.</summary>
    public static ChatSpendStopView? From(string? errorMessage, long? entryId) =>
        SpendErrors.Parse(errorMessage) is { } info ? new ChatSpendStopView(info.Code, info.Text, info.Op, entryId) : null;
}

/// <summary>The chat's spend in the session panel: the ledger's total of the run (the chat id) and its per-run caps.</summary>
public sealed record ChatSpendSummary
{
    /// <summary>What the chat's requests add up to (<c>$0.1234</c>, <c>≈ $0.18 list</c>), or null before any request.</summary>
    public string? Text { get; init; }

    /// <summary>How it was worked out.</summary>
    public string Title { get; init; } = "";

    /// <summary>The uncertain part (<c>$0.38 uncertain</c>), or null.</summary>
    public string? Uncertain { get; init; }

    /// <summary>History › Spend's uncertain rows of this chat.</summary>
    public string? UncertainUrl { get; init; }

    /// <summary>Requests in the ledger.</summary>
    public int Requests { get; init; }

    /// <summary>The tightest per-chat cap (<c>$0.40 of $2.00</c>), or <c>No chat cap</c>.</summary>
    public string Cap { get; init; } = "No chat cap";

    /// <summary>The cap's tooltip.</summary>
    public string CapTitle { get; init; } = "";

    /// <summary>Whether the cap is at or over its warning threshold.</summary>
    public bool CapWarning { get; init; }

    /// <summary>The billing chip: <c>Personal · subscription</c>, <c>metered</c>.</summary>
    public required string Billing { get; init; }

    /// <summary>The billing chip's tooltip.</summary>
    public string BillingTitle { get; init; } = "";

    /// <summary>History › Spend, searched for this chat.</summary>
    public required string HistoryUrl { get; init; }
}

/// <summary>The chat pages' spend text.</summary>
public static class ChatSpendText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>History › Spend's Resolve modal for <paramref name="op"/>.</summary>
    public static string ResolveUrl(string op) => "/history/spend?resolve=" + Uri.EscapeDataString(op);

    /// <summary>History › Spend searched for a chat.</summary>
    public static string HistoryUrl(string chatId) => "/history/spend?q=" + Uri.EscapeDataString(chatId);

    /// <summary>An amount as the estimate shows it: four decimals under a cent, else two.</summary>
    public static string Money(long micros) => SpendMoney.Format(micros, micros is > 0 and < 10_000 ? 4 : 2);

    /// <summary>A cap's name in a sentence without the Pages unit: <c>per-request cap</c>, <c>per-chat cap</c>, <c>anthropic daily cap</c>.</summary>
    public static string CapName(SpendCap cap)
    {
        ArgumentNullException.ThrowIfNull(cap);
        var period = cap.Period switch
        {
            CapPeriod.Request => "per-request",
            CapPeriod.Run => "per-chat",
            CapPeriod.Day => "daily",
            _ => "monthly",
        };
        return cap.Scope == CapScope.Everything ? period + " cap" : $"{cap.ScopeText()} {period} cap";
    }

    /// <summary>The over-cap chip of a cause's pane.</summary>
    public static string OverLabel(SpendCause cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        if (cause.Kind == SpendCauseKind.UnknownPrice) return "No price";
        return cause.Cap?.Period switch
        {
            CapPeriod.Request => "Over request cap",
            CapPeriod.Run => "Over chat cap",
            CapPeriod.Day => "Over daily cap",
            _ => "Over monthly cap",
        };
    }

    /// <summary>The composer's estimate of <paramref name="decision"/>: nothing for a request that costs nothing.</summary>
    public static ChatSpendEstimateView Estimate(SpendDecision decision, ChatSpendNames names)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(names);
        var estimate = decision.Estimate;
        string Cap(SpendCap cap) => names.CapName?.Invoke(cap) ?? CapName(cap);
        var warnings = decision.Warnings.Where(status => status.SpentMicros is not null).Select(status => Warning(status, Cap(status.Cap))).ToList();
        var chip = SpendEstimateText.Chip(estimate);
        var verdict = decision.Verdict.ToString().ToLowerInvariant();
        if (decision.Allowed && chip is null && warnings.Count == 0) return ChatSpendEstimateView.None with { Verdict = verdict };

        var list = estimate.Billing == BillingKind.Subscription;
        var worst = estimate.WorstMicros is { } micros ? (list ? $"≈ {Money(micros)} list" : Money(micros)) : null;
        var formula = SpendEstimateText.Formula(estimate);
        var model = $"{estimate.Provider}/{estimate.Model}";
        var lines = new List<string>();
        string head;
        string? sub = null;
        string label;
        string? over = null;
        if (!decision.Allowed && decision.Causes.FirstOrDefault() is { } cause)
        {
            over = OverLabel(cause);
            if (cause.Kind == SpendCauseKind.UnknownPrice)
            {
                head = $"No price for {model}";
                label = head;
                lines.Add("It can't be checked against your money caps until it has a price.");
                lines.Add(cause.Action == CapAction.Block
                    ? "Unknown prices are set to Block, so Send can't send it. Enter a price in Settings › Spend."
                    : "Unknown prices are set to Ask, so Send asks you first. Nothing is reserved until you choose.");
            }
            else
            {
                var cap = cause.Cap!;
                head = $"Over the {SpendMoney.Format(cap.LimitMicros)} {Cap(cap)}";
                label = worst is null ? head : $"{head}: worst case {worst}";
                if (worst is not null) lines.Add(formula is null ? $"Worst case {worst}." : $"Worst case {worst}: {formula}");
                if (cap.Period != CapPeriod.Request)
                {
                    var window = cap.Period switch { CapPeriod.Run => " in this chat", CapPeriod.Day => " today", _ => " this month" };
                    lines.Add($"{SpendMoney.Format(cause.SpentMicros)} used{window}, {SpendMoney.Format(cause.LeftMicros ?? 0)} left.");
                }
                if (estimate.TypicalMicros is { } typical)
                    lines.Add($"Typical ≈ {Money(typical)}{(list ? " list" : "")} · median of your last {CostEstimator.TypicalSampleSize} replies on this model.");
                lines.Add(cause.Action == CapAction.Block
                    ? "The cap is set to Block, so Send can't send it as it is" +
                      (decision.FitOutputTokens is { } fit ? $"; capping the output at {SpendEstimateText.Tokens(fit)} tokens fits." : ".")
                    : "The cap is set to Ask, so Send asks you first. Nothing is reserved until you choose.");
            }
        }
        else if (estimate.Billing != BillingKind.Free && estimate.WorstMicros is null && chip is not null)
        {
            head = "Price unknown";
            label = $"No price for {model}";
            lines.Add($"{model} has no price, so it isn't counted toward money caps. Enter a price in Settings › Spend to count it.");
        }
        else if (chip is not null)
        {
            if (estimate.TypicalMicros is { } typical)
            {
                head = chip + " typical";
                sub = $" · median of your last {CostEstimator.TypicalSampleSize} replies on this model";
                label = $"Estimated cost{(list ? " at list price" : "")}: about {Money(typical)} typical" + (estimate.WorstMicros is { } most ? $", {Money(most)} worst case" : "");
            }
            else
            {
                head = chip + " worst case";
                label = $"Estimated cost{(list ? " at list price" : "")}: up to {worst}";
            }
            if (worst is not null) lines.Add(formula is null ? $"Worst case {worst}." : $"Worst case {worst}: {formula}");
        }
        else
        {
            head = "";
            label = string.Join(" ", warnings.Select(warning => warning.Text));
        }
        if (estimate.PriceKnown && estimate.Billing != BillingKind.Free)
            lines.Add(SpendEstimateText.OutputNote(estimate) + (SpendEstimateText.TierNote(estimate) is { } tier ? " " + tier : ""));
        var who = names.AccountLabel is { Length: > 0 } account ? $"{names.ProviderName} · {account}" : names.ProviderName;
        if (estimate.Billing == BillingKind.Subscription) lines.Add($"{who} is a subscription: list price only, never counted toward money caps.");
        else if (estimate.Billing == BillingKind.Metered && estimate.WorstMicros is not null) lines.Add($"{who} is metered: counted toward your caps.");
        lines.AddRange(warnings.Select(warning => warning.Title));

        var tooltip = new StringBuilder();
        if (head.Length > 0) tooltip.Append("<strong>").Append(Encode(head)).Append("</strong>").Append(Encode(sub ?? ""));
        foreach (var line in lines)
        {
            if (tooltip.Length > 0) tooltip.Append("<br>");
            tooltip.Append(Encode(line));
        }
        return new ChatSpendEstimateView
        {
            Chip = over is null ? chip : null,
            Over = over,
            Verdict = verdict,
            Pane = decision.Allowed ? null : decision.Causes.FirstOrDefault()?.Pane,
            Code = decision.ErrorCode,
            Tooltip = tooltip.ToString(),
            Label = label,
            Warnings = warnings,
        };
    }

    /// <summary>A cap near its limit.</summary>
    public static ChatSpendWarning Warning(SpendCapStatus status, string capName)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(capName);
        var cap = status.Cap;
        var limit = SpendMoney.Format(cap.LimitMicros);
        var window = cap.Period switch { CapPeriod.Run => " in this chat", CapPeriod.Day => " today", CapPeriod.Month => " this month", _ => "" };
        var action = cap.Action == CapAction.Block ? "stop" : "ask first";
        return new ChatSpendWarning($"{status.PercentShown ?? 0}% of {capName}",
            $"{char.ToUpperInvariant(capName[0])}{capName[1..]}: {SpendMoney.Format(status.SpentMicros ?? 0)} of {limit} used{window}, uncertain spend included. Requests {action} at {limit}.");
    }

    /// <summary>The footer mark of a finished request's ledger row; null for a free or released one, or one that cost nothing.</summary>
    public static ChatSpendMark? Mark(Reservation row, string? accountLabel)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Billing == BillingKind.Free || row.State == ReservationState.Released) return null;
        var list = row.Billing == BillingKind.Subscription;
        var counted = row.CountsTowardCaps;
        if (row.State == ReservationState.Uncertain)
        {
            var reserved = row.ReservedMicros ?? 0;
            var amount = Money(reserved);
            var worst = row.Price is { } price
                ? $"Worst case: {SpendEstimateText.Tokens(row.InputTokens)} in × {SpendEstimateText.Rate(price.WorstInput)} + {SpendEstimateText.Tokens(row.OutputTokens)} max out × {SpendEstimateText.Rate(price.Output)}"
                : "The worst case reserved before the request was sent.";
            return new ChatSpendMark(ChatSpendMarkKind.Uncertain, row.ReservedMicros is null ? "no usage reported" : list ? $"≈ {amount} list reserved" : $"{amount} reserved", worst)
            {
                Op = row.Op,
                Chip = "uncertain",
                ChipTitle = counted
                    ? $"The stream dropped and no usage was reported, so the {amount} reserved for this request keeps counting toward your caps until you resolve it."
                    : "The stream dropped and no usage was reported, so its cost is uncertain until you resolve it. It isn't counted toward money caps.",
                ResolveUrl = ResolveUrl(row.Op),
            };
        }
        if (row.State != ReservationState.Settled) return null;
        if (row.ActualMicros is not { } actual || (row.Billing == BillingKind.Unknown && row.Settlement != SettlementKind.Entered))
            return new ChatSpendMark(ChatSpendMarkKind.UnknownPrice, "price unknown",
                $"{row.Provider}/{row.Model} has no price, so this request isn't counted toward money caps. Enter a price in Settings › Spend to count it.") { Op = row.Op };
        if (actual <= 0) return null;
        var text = SpendMoney.Format(actual, null);
        var rule = counted ? "Counted toward your caps." : "List price only: never counted toward money caps.";
        switch (row.Settlement)
        {
            case SettlementKind.Estimate:
                return new ChatSpendMark(ChatSpendMarkKind.Estimate, list ? $"≈ {text} list · est." : $"≈ {text} · est.",
                    $"Settled at an estimate from what was received ({SpendEstimateText.Tokens(row.InputTokens)} in estimated" +
                    (row.Usage is { } seen ? $", {SpendEstimateText.Tokens(seen.OutputTokens)} out" : "") + ")" +
                    (row.Reason is { Length: > 0 } reason ? $": {reason}." : ".") + " " + rule) { Op = row.Op };
            case SettlementKind.Entered:
                return new ChatSpendMark(list ? ChatSpendMarkKind.List : ChatSpendMarkKind.Metered, list ? $"≈ {text} list" : text,
                    "Settled at the amount you entered when you resolved it. " + rule) { Op = row.Op };
        }
        var worked = row.Usage is { } usage && row.Price is { } rates ? Worked(usage, rates) : null;
        return list
            ? new ChatSpendMark(ChatSpendMarkKind.List, $"≈ {text} list",
                $"List price at catalog rates{(worked is null ? "" : ": " + worked)}. Billed through the subscription{(accountLabel is { Length: > 0 } label ? $" ({label})" : "")}, so it isn't counted toward money caps.") { Op = row.Op }
            : new ChatSpendMark(ChatSpendMarkKind.Metered, text,
                $"Metered at {(row.Price?.Source == SpendPriceSource.Entered ? "the price you entered" : "catalog rates")}{(worked is null ? "" : ": " + worked)}. {rule}") { Op = row.Op };
    }

    /// <summary>The running answer's mark: the reserved request's input estimate plus <paramref name="outputTokens"/> received so far, at
    /// its rates; null for a free model.</summary>
    public static ChatSpendMark? Live(Reservation row, double outputTokens)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Billing == BillingKind.Free) return null;
        if (row.Price is not { } price || row.ReservedMicros is null)
            return new ChatSpendMark(ChatSpendMarkKind.UnknownPrice, "price unknown", $"{row.Provider}/{row.Model} has no price, so this request isn't counted toward money caps.")
            {
                Op = row.Op,
            };
        var micros = price.InputMicros(row.InputTokens) + price.OutputMicros(Math.Max(0, outputTokens));
        var text = SpendMoney.Format(micros, null);
        var list = row.Billing == BillingKind.Subscription;
        return new ChatSpendMark(ChatSpendMarkKind.Live, list ? $"≈ {text} list · est." : $"≈ {text} · est.",
            $"Estimate from the tokens received so far: {SpendEstimateText.Tokens(row.InputTokens)} in × {SpendEstimateText.Rate(price.WorstInput)} + " +
            $"~{SpendEstimateText.Tokens(outputTokens)} out × {SpendEstimateText.Rate(price.Output)}. The provider's usage replaces it when the answer ends.")
        {
            Op = row.Op,
        };
    }

    /// <summary>About how many tokens a running answer has received: its text, thinking and tool arguments, at the estimator's bytes-per-token.</summary>
    public static double StreamedTokens(AssistantItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        long bytes = 0;
        foreach (var block in item.Blocks)
        {
            bytes += block switch
            {
                TextBlockView text => Encoding.UTF8.GetByteCount(text.Text ?? ""),
                ThinkingBlockView thinking => Encoding.UTF8.GetByteCount(thinking.Text ?? ""),
                ToolBlockView tool => Encoding.UTF8.GetByteCount(tool.Call.Name) + Encoding.UTF8.GetByteCount(tool.Call.Args?.ToJsonString() ?? ""),
                _ => 0,
            };
        }
        return CostEstimator.InputTokens(bytes);
    }

    /// <summary>The chat's spend for the session panel.</summary>
    public static ChatSpendSummary Summary(string chatId, SpendTotal total, IEnumerable<SpendCapStatus> chatCaps, BillingKind billing, string? accountLabel,
        Func<SpendCap, string>? capName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentNullException.ThrowIfNull(total);
        ArgumentNullException.ThrowIfNull(chatCaps);
        var parts = new List<string>();
        if (total.CountedMicros > 0) parts.Add(SpendMoney.Format(total.CountedMicros, null));
        if (total.ListMicros > 0) parts.Add($"≈ {SpendMoney.Format(total.ListMicros, null)} list");
        string? text = total.Requests == 0 ? null : parts.Count > 0 ? string.Join(" · ", parts) : total.FreeRequests == total.Requests ? "free" : SpendMoney.Format(0);
        var title = new StringBuilder($"{total.Requests} request{(total.Requests == 1 ? "" : "s")} of this chat in the spend ledger");
        title.Append(total.CountedMicros > 0 ? $"; {SpendMoney.Format(total.CountedMicros, null)} counts toward your caps" : "");
        title.Append(total.UncertainMicros > 0 ? $", including {Money(total.UncertainMicros)} uncertain until you resolve it" : "");
        title.Append('.');
        if (total.ListMicros > 0) title.Append(" Subscription use is shown at list price and never counted.");
        if (total.UnknownPriceRequests > 0)
            title.Append($" {total.UnknownPriceRequests} request{(total.UnknownPriceRequests == 1 ? " has" : "s have")} no price and isn't counted.");
        var name = capName ?? CapName;
        var tightest = chatCaps.Where(status => status.Cap.Enabled && status.Cap.Period == CapPeriod.Run && status.SpentMicros is not null)
            .OrderBy(status => status.LeftMicros).FirstOrDefault();
        var mode = billing switch
        {
            BillingKind.Subscription => "subscription",
            BillingKind.Metered => "metered",
            BillingKind.Free => "free",
            _ => "price unknown",
        };
        return new ChatSpendSummary
        {
            Text = text,
            Title = title.ToString(),
            Uncertain = total.UncertainMicros > 0 ? $"{Money(total.UncertainMicros)} uncertain" : null,
            UncertainUrl = total.UncertainMicros > 0 ? $"/history/spend?status=uncertain&q={Uri.EscapeDataString(chatId)}" : null,
            Requests = total.Requests,
            Cap = tightest is { } cap ? $"{SpendMoney.Format(cap.SpentMicros ?? 0)} of {SpendMoney.Format(cap.Cap.LimitMicros)}" : "No chat cap",
            CapTitle = tightest is { } status
                ? $"{Capital(name(status.Cap))}: {SpendMoney.Format(status.SpentMicros ?? 0)} of {SpendMoney.Format(status.Cap.LimitMicros)} used in this chat, uncertain spend included. " +
                  (status.Cap.Action == CapAction.Block ? "Requests stop" : "Send asks first") + $" above {SpendMoney.Format(status.Cap.LimitMicros)}."
                : "No per-chat cap is set. Add one in Settings › Spend with the Per run window. The per-request, daily and monthly caps still apply to metered models.",
            CapWarning = tightest?.Warning == true,
            Billing = accountLabel is { Length: > 0 } label ? $"{label} · {mode}" : mode,
            BillingTitle = billing switch
            {
                BillingKind.Subscription => "Billed through a subscription: list prices only, never counted toward money caps.",
                BillingKind.Metered => "Metered: every request counts toward your money caps.",
                BillingKind.Free => "Free: requests cost nothing and aren't counted.",
                _ => "No price: requests aren't counted toward money caps until you enter a price in Settings › Spend.",
            },
            HistoryUrl = HistoryUrl(chatId),
        };
    }

    /// <summary>Links a branch's answers to the chat's ledger rows: an answer with usage to the row with the same usage, provider and model
    /// nearest its time; one without (or with usage no settled row reported) to the nearest row without usage within two minutes. A call
    /// the guard stopped has no row. While the chat runs, its newest row still reserved is the running answer's.</summary>
    public static ChatSpendFacts Link(IReadOnlyList<JsonObject> entries, IReadOnlyList<Reservation> rows, bool running, Func<Reservation, ChatSpendMark?> mark)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(mark);
        var linked = new HashSet<string>(StringComparer.Ordinal);
        var marks = new Dictionary<long, ChatSpendMark>();
        var open = new List<(long Id, ChatEntryMeta Meta)>();
        foreach (var entry in entries.Where(entry => ChatDisplay.Str(entry, "kind") == "pi.assistant").OrderBy(entry => ChatDisplay.Long(entry, "id") ?? 0))
        {
            if (ChatDisplay.Long(entry, "id") is not { } id || ChatEntryMeta.Of(entry) is not { } meta) continue;
            if (SpendErrors.IsSpend(meta.ErrorMessage)) continue;
            if (!meta.HasUsage)
            {
                open.Add((id, meta));
                continue;
            }
            var row = Nearest(rows.Where(row => !linked.Contains(row.Op) && Same(row, meta) && row.Usage is { } usage && usage.InputTokens == meta.Input &&
                usage.OutputTokens == meta.Output && usage.CacheReadTokens == meta.CacheRead && usage.CacheWriteTokens == meta.CacheWrite), meta.Time, null);
            if (row is null)
            {
                open.Add((id, meta));
                continue;
            }
            Add(id, row);
        }
        foreach (var (id, meta) in open)
            if (Nearest(rows.Where(row => !linked.Contains(row.Op) && Same(row, meta) && row.State != ReservationState.Reserved &&
                    (row.Usage is null || row.Settlement != SettlementKind.Actual)), meta.Time, 120_000) is { } row)
                Add(id, row);
        var live = running ? rows.Where(row => row.State == ReservationState.Reserved && !linked.Contains(row.Op)).MaxBy(row => row.Created) : null;
        return new ChatSpendFacts { Marks = marks, Live = live };

        void Add(long id, Reservation row)
        {
            linked.Add(row.Op);
            if (mark(row) is { } found) marks[id] = found with { Op = row.Op };
        }
    }

    private static bool Same(Reservation row, ChatEntryMeta meta) =>
        (meta.Provider is null || row.Provider == meta.Provider) && (meta.Model is null || row.Model == meta.Model);

    private static Reservation? Nearest(IEnumerable<Reservation> rows, double? time, double? within)
    {
        if (time is not { } at) return rows.MaxBy(row => row.Created);
        Reservation? best = null;
        var distance = double.MaxValue;
        foreach (var row in rows)
        {
            var gap = Math.Abs(row.Created.ToUnixTimeMilliseconds() - at);
            if (within is { } limit && gap > limit) continue;
            if (gap < distance)
            {
                best = row;
                distance = gap;
            }
        }
        return best;
    }

    private static string Worked(SpendUsage usage, SpendPrice price)
    {
        var parts = new List<string>
        {
            $"{SpendEstimateText.Tokens(usage.InputTokens)} in × {SpendEstimateText.Rate(price.Input)}",
            $"{SpendEstimateText.Tokens(usage.OutputTokens)} out × {SpendEstimateText.Rate(price.Output)}",
        };
        if (usage.CacheReadTokens > 0) parts.Add($"{SpendEstimateText.Tokens(usage.CacheReadTokens)} cached × {SpendEstimateText.Rate(price.CacheRead)}");
        if (usage.CacheWriteTokens > 0) parts.Add($"{SpendEstimateText.Tokens(usage.CacheWriteTokens)} cache write × {SpendEstimateText.Rate(price.CacheWrite)}");
        return string.Join(" + ", parts);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Invariant) + text[1..];

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
