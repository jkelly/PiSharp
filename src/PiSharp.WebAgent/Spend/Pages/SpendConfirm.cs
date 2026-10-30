// Decision 0011 (run W3, S4): modal-spend-confirm (_SpendConfirmModal), shown before anything is reserved or sent when the guard says Ask
// or Block: one pane for the first cause (Block before Ask): sc-request (the per-request cap), sc-chat (a run cap: a chat's, or a run's),
// sc-period (a day or month cap) and sc-unknown (no price). The worst case's parts add up, what is left in the cap, a shorter output that
// fits, the other causes, and cheaper models as suggestions only: PiSharp never switches the model or the account. sc-allowance is left out:
// no subscription allowance data exists yet. The decision comes from the guard's preflight (ISpendGuard, S3) when it is registered, else
// from the estimator and the ledger's own check, which is the same rule without the guard's approvals. Amounts under a dollar show four
// decimals, as the composer's tooltip and the spend errors do, and the input and output parts are rounded so that they add up to the worst
// case shown; the footnote speaks of "the models above" only when there are some (W3 fix, spend-ui).
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Spend;

public sealed partial class SpendPages
{
    /// <summary>The output cap offered for a request whose price is unknown ("keeps the answer short").</summary>
    public const int UnknownPriceOutputTokens = 2_048;

    /// <summary>The confirm modal's promise, said whatever the cause.</summary>
    public const string NeverSwitches = "PiSharp never switches your model or account.";

    /// <summary>What the guard says about <paramref name="call"/> before it is sent; nothing is reserved.</summary>
    /// <exception cref="ArgumentException">The call is not allowed (no provider or model, a negative size).</exception>
    public async Task<SpendDecision> PreflightAsync(SpendCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (services.GetService(typeof(ISpendGuard)) is ISpendGuard guard) return await guard.PreflightAsync(call, cancellationToken).ConfigureAwait(false);
        var estimate = await estimator.EstimateAsync(call.EstimateInput, cancellationToken).ConfigureAwait(false);
        return await ledger.CheckAsync(new SpendRequest
        {
            Op = string.IsNullOrWhiteSpace(call.Op) ? "preflight-" + Guid.NewGuid().ToString("N") : call.Op,
            Source = call.Source,
            Run = call.Run,
            Estimate = estimate,
            Approved = call.Approved,
            Note = call.Note,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The confirm modal of a decision that is not Allow.</summary>
    /// <exception cref="ArgumentException"><paramref name="decision"/> allows the request (there is nothing to confirm).</exception>
    public SpendConfirmView ConfirmView(SpendCall call, SpendDecision decision)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Allowed || decision.Causes.Count == 0) throw new ArgumentException("This request fits every cap; there is nothing to confirm.", nameof(decision));
        var estimate = decision.Estimate;
        var cause = decision.Causes[0];
        var cap = cause.Cap;
        var pane = cause.Pane;
        var worst = estimate.WorstMicros ?? 0;
        var places = worst is > 0 and < SpendMoney.MicrosPerDollar ? 4 : 2;
        string Money(long micros) => SpendMoney.Format(micros, places);
        var provider = catalog.FindProvider(estimate.Provider)?.DisplayName ?? estimate.Provider;
        var who = estimate.Account is { } account ? AccountText(account) : provider;
        var action = cause.Action == CapAction.Block ? "Block" : "Ask";
        var tail = cause.Action == CapAction.Block ? "so the request can't be sent as it is." : "so nothing has been reserved or sent yet.";
        var chat = call.Source == SpendSource.Chat;
        var runWord = chat ? "chat" : "run";
        var periodWord = cap?.Period == CapPeriod.Day ? "daily" : "monthly";
        var window = cap?.Period == CapPeriod.Day ? "today" : "this month";
        var spend = Settings;

        string title;
        string capText;
        SpendConfirmLead lead;
        var parts = new List<SpendConfirmPart>();
        if (pane == "sc-unknown")
        {
            title = "No price for this model";
            capText = "unknown price, " + action;
            lead = new SpendConfirmLead(provider + " has no price for ", estimate.Model,
                ", so this request can't be estimated or checked against your caps. Unknown prices are set to " +
                (cause.Action == CapAction.Block ? "Block, so it can't be sent." : "Ask."), Mono: true);
            parts.Add(new SpendConfirmPart($"Input · about {SpendEstimateText.Tokens(estimate.InputTokens)} tokens", "price unknown", Faint: true));
            parts.Add(new SpendConfirmPart($"Output · up to {SpendEstimateText.Tokens(estimate.OutputTokens)} tokens", "price unknown", Faint: true));
            parts.Add(new SpendConfirmPart("Worst case", "unknown", Strong: true));
        }
        else
        {
            var limit = cap is null ? "" : SpendMoney.Format(cap.LimitMicros);
            var spent = cause.SpentMicros;
            var left = cause.LeftMicros ?? (cap is null ? 0 : Math.Max(0, cap.LimitMicros - spent));
            switch (pane)
            {
                case "sc-request":
                    title = "Over your per-request cap";
                    capText = $"cap {limit}, {action}";
                    lead = new SpendConfirmLead("This request could cost up to ", Money(worst), $". Your per-request cap is {limit} and set to {action}, {tail}");
                    break;
                case "sc-chat":
                    title = $"Over this {runWord}'s cap";
                    capText = $"{runWord} cap {limit}, {action}";
                    lead = new SpendConfirmLead($"This {runWord} has used ", Money(spent),
                        $" of its {limit} cap, and this request could add up to {Money(worst)}. The {runWord} cap is set to {action}, {tail}");
                    break;
                default:
                    var owner = cap?.Scope switch
                    {
                        CapScope.Provider => catalog.FindProvider(cap.Key ?? "")?.DisplayName ?? cap.Key ?? "",
                        CapScope.Account => cap.Key is { } id ? AccountText(id) : "This account",
                        CapScope.Source => SpendSources.TryParse(cap.Key, out var source) ? source.Label() : cap.Key ?? "",
                        _ => null,
                    };
                    title = owner is null ? $"Over your {periodWord} cap" : cap!.Scope == CapScope.Source ? $"Over the {owner} {periodWord} cap" : $"Over {owner}'s {periodWord} cap";
                    capText = $"{periodWord} cap {limit}, {action}";
                    lead = new SpendConfirmLead(owner is null ? "You have used " : owner + " has used ", Money(spent),
                        $" of {(owner is null ? "your" : "its")} {limit} {periodWord} cap, so {Money(left)} is left. This request could cost up to {Money(worst)}. " +
                        $"The cap is set to {action}, {tail}");
                    break;
            }
            if (estimate.Price is { } price)
            {
                var (input, output) = WorstCaseParts(worst, places, price.InputMicros(estimate.InputTokens), price.OutputMicros(estimate.OutputTokens));
                parts.Add(new SpendConfirmPart($"Input · about {SpendEstimateText.Tokens(estimate.InputTokens)} tokens × {SpendEstimateText.RateAmount(price.WorstInput)} per 1M",
                    Money(input)));
                parts.Add(new SpendConfirmPart($"Output · up to {SpendEstimateText.Tokens(estimate.OutputTokens)} tokens × {SpendEstimateText.RateAmount(price.Output)} per 1M",
                    Money(output)));
            }
            parts.Add(new SpendConfirmPart("Worst case", Money(worst), Strong: true));
            if (pane == "sc-request" && estimate.TypicalMicros is { } typical) parts.Add(new SpendConfirmPart("Typical for this model", "≈ " + Money(typical)));
            if (pane == "sc-chat") parts.Add(new SpendConfirmPart($"Left in this {runWord}'s cap", Money(left)));
            if (pane == "sc-period") parts.Add(new SpendConfirmPart(OwnerName(cap) is { } name ? $"Left {window} for {name}" : $"Left {window}", Money(left)));
        }

        int? capOutput = null;
        string? fit;
        if (pane == "sc-unknown")
        {
            if (estimate.OutputTokens > UnknownPriceOutputTokens && cause.Action == CapAction.Ask) capOutput = UnknownPriceOutputTokens;
            fit = capOutput is null ? null : $"Capping the output at {SpendEstimateText.Tokens(UnknownPriceOutputTokens)} tokens keeps the answer short, but the cost is still unknown.";
        }
        else
        {
            var tokens = decision.FitOutputTokens;
            if (tokens is > 0 && tokens < estimate.OutputTokens && estimate.Price is { } price)
            {
                capOutput = tokens;
                var inside = pane switch { "sc-request" => "inside the cap.", "sc-chat" => "inside what's left.", _ => $"inside what's left {window}." };
                fit = $"Capping the output at {SpendEstimateText.Tokens(tokens.Value)} tokens brings the worst case to {Money(price.WorstMicros(estimate.InputTokens, tokens.Value))}, {inside}";
            }
            else
            {
                fit = "Even a very short answer doesn't fit: the input alone is over the cap.";
            }
            if (estimate.OutputBasis == OutputTokenBasis.ReservationCap)
                fit += $" Requests that don't set max output tokens reserve {SpendEstimateText.Tokens(spend.ReservationOutputTokens)}, your default reservation cap.";
        }

        string? others = null;
        if (decision.Causes.Count > 1)
        {
            var more = decision.Causes.Skip(1).Select(other => other.Kind == SpendCauseKind.UnknownPrice || other.Cap is null ? "without a known price"
                : $"over your {SpendMoney.Format(other.Cap.LimitMicros)} {CapName(other.Cap)}, which is set to {(other.Action == CapAction.Block ? "Block" : "Ask")}").ToList();
            others = "It's also " + Join(more) + (cause.Action == CapAction.Block && decision.Causes.Skip(1).Any(other => other.Action == CapAction.Ask)
                ? ", but Block takes priority." : ".");
            if (capOutput is not null && decision.Causes.Skip(1).Any(other => other.Action == CapAction.Ask))
                others += " Capping the output also answers that Ask, for this request only.";
        }

        var within = decision.Causes.Where(other => other.Kind == SpendCauseKind.Cap && other.Cap is not null)
            .Select(other => other.LeftMicros ?? other.Cap!.LimitMicros).DefaultIfEmpty().Min();
        IReadOnlyList<SpendEstimate> cheaper;
        try { cheaper = estimator.Cheaper(call.EstimateInput, decision.Causes.Any(other => other.Kind == SpendCauseKind.Cap) ? within : null); }
        catch (ArgumentException) { cheaper = []; }
        var suggestions = cheaper.Select(other => new SpendConfirmSuggestion(other.Provider, other.Model,
            other.Billing == BillingKind.Free || other.Price?.IsFree == true ? "free"
            : other.Billing == BillingKind.Subscription ? SpendMoney.FormatList(other.WorstMicros ?? 0, places)
            : Money(other.WorstMicros ?? 0),
            other.Billing == BillingKind.Subscription ? "List price only: billed through the subscription, never counted toward money caps" : null)).ToList();

        return new SpendConfirmView
        {
            Decision = decision,
            Cause = cause,
            Pane = pane,
            Title = title,
            Model = estimate.Provider + "/" + estimate.Model,
            Subtitle = " · " + who + " · " + capText,
            Lead = lead,
            Parts = parts,
            Fit = fit,
            EnterPrice = pane == "sc-unknown",
            RaiseCap = cause.Action == CapAction.Block && pane != "sc-unknown",
            Others = others,
            SuggestionsLabel = pane == "sc-unknown" ? "Models with a known price" : "Cheaper models",
            Suggestions = suggestions,
            SubscriptionNote = suggestions.Any(suggestion => suggestion.Title is not null)
                ? "Subscription models show a list price for comparison only; it never counts toward money caps." : null,
            Footnote = suggestions.Count > 0 ? NeverSwitches + " The models above change nothing until you pick one yourself." : NeverSwitches,
            CanProceed = decision.Verdict == SpendVerdict.Ask,
            CapOutputTokens = capOutput,
            Call = call,
        };

        string? OwnerName(SpendCap? scoped) => scoped?.Scope switch
        {
            CapScope.Provider => scoped.Key,
            CapScope.Account or CapScope.Source => ScopeName(scoped),
            _ => null,
        };
    }

    /// <summary>The input and output parts of a worst case, in micro-USD, each rounded to <paramref name="places"/> decimals (halves away
    /// from zero, as <see cref="SpendMoney.Format"/> rounds) so that they add up to <paramref name="worst"/> as it is shown. Each part is
    /// rounded up to a micro-USD on its own and the worst case once, and rounding each to cents or hundredths of a cent can lose or add a
    /// unit; that unit goes to the part its rounding moved furthest the other way.</summary>
    private static (long Input, long Output) WorstCaseParts(long worst, int places, long input, long output)
    {
        var unit = 1L;
        for (var digit = places; digit < 6; digit++) unit *= 10;
        long Shown(long micros) => micros <= 0 ? 0 : (micros + unit / 2) / unit * unit;
        long[] exact = [input, output];
        long[] shown = [Shown(input), Shown(output)];
        var missing = (Shown(worst) - shown[0] - shown[1]) / unit;
        for (; missing > 0; missing--)
        {
            var part = exact[0] - shown[0] >= exact[1] - shown[1] ? 0 : 1;
            shown[part] += unit;
        }
        for (; missing < 0; missing++)
        {
            var part = shown[1] < unit || (shown[0] >= unit && exact[0] - shown[0] <= exact[1] - shown[1]) ? 0 : 1;
            shown[part] -= unit;
        }
        return (shown[0], shown[1]);
    }
}

/// <summary>The lead sentence: <see cref="Before"/>, <see cref="Mark"/> (bold, or monospace), <see cref="After"/>.</summary>
public sealed record SpendConfirmLead(string Before, string Mark, string After, bool Mono = false);

/// <summary>One line of the worst case's panel.</summary>
public sealed record SpendConfirmPart(string Key, string Value, bool Strong = false, bool Faint = false);

/// <summary>A cheaper model, as a suggestion only.</summary>
public sealed record SpendConfirmSuggestion(string Provider, string Model, string Value, string? Title);

/// <summary>modal-spend-confirm for one decision.</summary>
public sealed record SpendConfirmView
{
    public required SpendDecision Decision { get; init; }

    public required SpendCause Cause { get; init; }

    /// <summary><c>sc-request</c>, <c>sc-chat</c>, <c>sc-period</c> or <c>sc-unknown</c>.</summary>
    public required string Pane { get; init; }

    public required string Title { get; init; }

    /// <summary><c>provider/model</c>.</summary>
    public required string Model { get; init; }

    /// <summary>After the model: <c> · key sk-…Q7x2 · cap $0.50, Ask</c>.</summary>
    public required string Subtitle { get; init; }

    public required SpendConfirmLead Lead { get; init; }

    public required IReadOnlyList<SpendConfirmPart> Parts { get; init; }

    public string? Fit { get; init; }

    /// <summary>Whether to point at Settings › Spend's Enter price.</summary>
    public bool EnterPrice { get; init; }

    /// <summary>Whether to point at Settings › Spend's caps (a Block).</summary>
    public bool RaiseCap { get; init; }

    public string? Others { get; init; }

    public required string SuggestionsLabel { get; init; }

    public required IReadOnlyList<SpendConfirmSuggestion> Suggestions { get; init; }

    public string? SubscriptionNote { get; init; }

    /// <summary>The note above the footer: PiSharp never switches the model or account, and, when <see cref="Suggestions"/> lists models,
    /// that they change nothing until the user picks one.</summary>
    public string Footnote { get; init; } = SpendPages.NeverSwitches;

    /// <summary>Whether "Proceed once" is offered (Ask; never for Block).</summary>
    public bool CanProceed { get; init; }

    /// <summary>The output cap offered, or null.</summary>
    public int? CapOutputTokens { get; init; }

    public required SpendCall Call { get; init; }
}
