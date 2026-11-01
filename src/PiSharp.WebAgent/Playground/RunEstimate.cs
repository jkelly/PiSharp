// Decision 0011 (run W4, R1): the estimate by Run (the mockups' run-estimate chip left of Stop), from the Spend Guard's preflight of the
// request the run would make (W3: SpendGuard.PreflightAsync, nothing reserved; approvals that wait are ignored, as the chat composer does).
// The chip is the guard's own figure: "≈ $0.03" metered, "≈ $0.21 list" for a subscription, "price unknown", nothing for a free model; its
// tooltip is the worst case worked out (SpendEstimateText), the typical figure, the output and tier notes and how the account is billed.
// When the guard would ask or block, the chip names the cause ("Over request cap", "Over run cap", "Over daily cap", "No price") and Run
// opens modal-spend-confirm. The wording is the run's ("Run", "run cap"), not the chat's. Text is plain; the tooltip's markup is built from
// encoded parts only (strong and br, which Bootstrap's sanitizer keeps).
using System.Net;
using System.Text;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Playground;

/// <summary>The estimate chip by Run (<c>Views/Playground/_RunEstimate.cshtml</c>).</summary>
public sealed record RunEstimateView
{
    /// <summary>Nothing to show (a free model, no Spend Guard, or a request that is not valid yet).</summary>
    public static RunEstimateView None { get; } = new();

    /// <summary>The chip (<c>≈ $0.03</c>, <c>≈ $0.21 list</c>, <c>price unknown</c>), or null.</summary>
    public string? Chip { get; init; }

    /// <summary>The over-cap chip, or null when the guard allows the request.</summary>
    public string? Over { get; init; }

    /// <summary>allow, ask or block.</summary>
    public string Verdict { get; init; } = "allow";

    /// <summary>The confirm modal's pane of the first cause, or null.</summary>
    public string? Pane { get; init; }

    /// <summary>The tooltip markup (encoded parts, strong and br only).</summary>
    public string Tooltip { get; init; } = "";

    /// <summary>The chip's accessible name.</summary>
    public string Label { get; init; } = "";

    /// <summary>Caps near their limit with this request counted.</summary>
    public IReadOnlyList<ChatSpendWarning> Warnings { get; init; } = [];

    /// <summary>Whether there is anything to show.</summary>
    public bool Shown => Chip is not null || Over is not null || Warnings.Count > 0;
}

/// <summary>Builds <see cref="RunEstimateView"/>.</summary>
public static class RunEstimate
{
    /// <summary>The estimate of <paramref name="decision"/>, worded for <paramref name="verb"/> (Run, Run all).</summary>
    /// <param name="decision">The guard's preflight decision.</param>
    /// <param name="who">The provider (and account label) as the tooltip names them.</param>
    /// <param name="capName">A cap's name in a sentence (SpendPages.CapName), or null for ChatSpendText's.</param>
    /// <param name="verb">The button the estimate sits by.</param>
    public static RunEstimateView Build(SpendDecision decision, string who, Func<SpendCap, string>? capName = null, string verb = "Run")
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(who);
        var estimate = decision.Estimate;
        string Cap(SpendCap cap) => capName?.Invoke(cap) ?? ChatSpendText.CapName(cap).Replace("per-chat", "per-run", StringComparison.Ordinal);
        var warnings = decision.Warnings.Where(status => status.SpentMicros is not null)
            .Select(status => ChatSpendText.Warning(status, Cap(status.Cap)))
            .Select(warning => warning with { Title = warning.Title.Replace(" in this chat", " in this run", StringComparison.Ordinal) })
            .ToList();
        var chip = SpendEstimateText.Chip(estimate);
        var verdict = decision.Verdict.ToString().ToLowerInvariant();
        if (decision.Allowed && chip is null && warnings.Count == 0) return RunEstimateView.None with { Verdict = verdict };

        var list = estimate.Billing == BillingKind.Subscription;
        var worst = estimate.WorstMicros is { } micros ? (list ? $"≈ {ChatSpendText.Money(micros)} list" : ChatSpendText.Money(micros)) : null;
        var formula = SpendEstimateText.Formula(estimate);
        var model = $"{estimate.Provider}/{estimate.Model}";
        var lines = new List<string>();
        string head;
        string label;
        string? over = null;
        if (!decision.Allowed && decision.Causes.FirstOrDefault() is { } cause)
        {
            over = cause.Kind == SpendCauseKind.UnknownPrice ? "No price" : cause.Cap?.Period switch
            {
                CapPeriod.Request => "Over request cap",
                CapPeriod.Run => "Over run cap",
                CapPeriod.Day => "Over daily cap",
                _ => "Over monthly cap",
            };
            if (cause.Kind == SpendCauseKind.UnknownPrice)
            {
                head = $"No price for {model}";
                label = head;
                lines.Add("It can't be checked against your money caps until it has a price.");
                lines.Add(cause.Action == CapAction.Block
                    ? $"Unknown prices are set to Block, so {verb} can't send it. Enter a price in Settings › Spend."
                    : $"Unknown prices are set to Ask, so {verb} asks you first. Nothing is reserved until you choose.");
            }
            else
            {
                var cap = cause.Cap!;
                head = $"Over the {SpendMoney.Format(cap.LimitMicros)} {Cap(cap)}";
                label = worst is null ? head : $"{head}: worst case {worst}";
                if (worst is not null) lines.Add(formula is null ? $"Worst case {worst}." : $"Worst case {worst}: {formula}");
                if (cap.Period != CapPeriod.Request)
                {
                    var window = cap.Period switch { CapPeriod.Run => " in this run", CapPeriod.Day => " today", _ => " this month" };
                    lines.Add($"{SpendMoney.Format(cause.SpentMicros)} used{window}, {SpendMoney.Format(cause.LeftMicros ?? 0)} left.");
                }
                lines.Add(cause.Action == CapAction.Block
                    ? $"The cap is set to Block, so {verb} can't send it as it is" +
                      (decision.FitOutputTokens is { } fit ? $"; capping the output at {SpendEstimateText.Tokens(fit)} tokens fits." : ".")
                    : $"The cap is set to Ask, so {verb} asks you first. Nothing is reserved until you choose.");
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
            head = $"Worst case{(list ? " at list price" : "")} · {worst ?? chip}";
            label = $"Estimated worst-case cost{(list ? " at list price" : "")}: up to {worst ?? chip}";
            if (formula is not null) lines.Add(formula);
            if (estimate.TypicalMicros is { } typical)
                lines.Add($"Typical ≈ {ChatSpendText.Money(typical)}{(list ? " list" : "")} · median of your last {CostEstimator.TypicalSampleSize} replies on this model.");
        }
        else
        {
            head = "";
            label = string.Join(" ", warnings.Select(warning => warning.Text));
        }
        if (estimate.PriceKnown && estimate.Billing != BillingKind.Free)
            lines.Add(SpendEstimateText.OutputNote(estimate) + (SpendEstimateText.TierNote(estimate) is { } tier ? " " + tier : ""));
        if (estimate.Billing == BillingKind.Subscription) lines.Add($"{who} is a subscription: list price only, never counted toward money caps.");
        else if (estimate.Billing == BillingKind.Metered && estimate.WorstMicros is not null) lines.Add($"{who} is metered: counted toward your caps.");
        lines.AddRange(warnings.Select(warning => warning.Title));

        var tooltip = new StringBuilder();
        if (head.Length > 0) tooltip.Append("<strong>").Append(WebUtility.HtmlEncode(head)).Append("</strong>");
        foreach (var line in lines)
        {
            if (tooltip.Length > 0) tooltip.Append("<br>");
            tooltip.Append(WebUtility.HtmlEncode(line));
        }
        return new RunEstimateView
        {
            Chip = over is null ? chip : null,
            Over = over,
            Verdict = verdict,
            Pane = decision.Allowed ? null : decision.Causes.FirstOrDefault()?.Pane,
            Tooltip = tooltip.ToString(),
            Label = label,
            Warnings = warnings,
        };
    }
}
