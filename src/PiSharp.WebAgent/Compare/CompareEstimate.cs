// Decision 0011 (run W4, R2): the estimate by Run all (the mockups' run-estimate chip left of Run all, rendered by the Playground's shared
// _RunEstimate partial). Each column is checked by the Spend Guard's own preflight (source compare; nothing reserved, approvals that wait
// ignored), as the Playground's Run is. The chip is the metered columns' worst cases added up (they are reserved together, all or none,
// when Run all sends); a subscription column is list price only and never counted, so it is in the tooltip, not the sum; a free column
// costs nothing; a column without a price says so. The tooltip lists every column. When any column alone would be asked about or blocked,
// the chip names that cause ("Over request cap", "Over run cap", "No price") and Run all opens modal-spend-confirm. The preflight checks
// each column on its own: the group's sum against a run, daily or monthly cap is checked when Run all reserves the group (the
// authoritative check; a refusal then opens the same modal). Text is plain; the tooltip's markup is built from encoded parts only.
using System.Net;
using System.Text;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Playground;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Compare;

/// <summary>One column's preflight, for the estimate by Run all.</summary>
/// <param name="Name">The model's display name.</param>
/// <param name="Who">The provider (and account label) as the tooltip names them.</param>
/// <param name="Decision">The Spend Guard's preflight decision.</param>
public sealed record CompareColumnEstimate(string Name, string Who, SpendDecision Decision);

/// <summary>Builds the estimate by Run all.</summary>
public static class CompareEstimate
{
    /// <summary>The verb the estimate sits by.</summary>
    public const string Verb = "Run all";

    /// <summary>The estimate of a group whose columns were preflighted as <paramref name="columns"/>.</summary>
    /// <param name="columns">Each column's preflight (columns that cannot run are left out).</param>
    /// <param name="capName">A cap's name in a sentence (SpendPages.CapName), or null.</param>
    public static RunEstimateView Build(IReadOnlyList<CompareColumnEstimate> columns, Func<SpendCap, string>? capName = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0) return RunEstimateView.None;
        long metered = 0, list = 0;
        var meteredCount = 0;
        var unknown = 0;
        var lines = new List<string>();
        foreach (var column in columns)
        {
            var estimate = column.Decision.Estimate;
            var worst = estimate.WorstMicros;
            switch (estimate.Billing)
            {
                case BillingKind.Free:
                    lines.Add($"{column.Name} · free");
                    break;
                case BillingKind.Subscription when worst is { } listed:
                    list += listed;
                    lines.Add($"{column.Name} · ≈ {ChatSpendText.Money(listed)} list, subscription (not counted toward caps)");
                    break;
                case BillingKind.Subscription:
                    lines.Add($"{column.Name} · subscription, no list price");
                    break;
                default:
                    if (worst is { } micros)
                    {
                        metered += micros;
                        meteredCount++;
                        lines.Add($"{column.Name} · {ChatSpendText.Money(micros)}");
                    }
                    else
                    {
                        unknown++;
                        lines.Add($"{column.Name} · no price, not counted toward money caps");
                    }
                    break;
            }
        }

        // A column the guard would ask about or block decides the chip (a block before an ask): the group cannot go as it is.
        var refusing = columns.Where(column => !column.Decision.Allowed)
            .OrderBy(column => column.Decision.Verdict == SpendVerdict.Block ? 0 : 1).FirstOrDefault();
        var single = refusing is null ? null : RunEstimate.Build(refusing.Decision, refusing.Who, capName, Verb);
        var warnings = columns.Select(column => RunEstimate.Build(column.Decision, column.Who, capName, Verb))
            .SelectMany(view => view.Warnings).GroupBy(warning => warning.Title).Select(group => group.First()).ToList();

        string? chip = meteredCount > 0 ? "≈ " + ChatSpendText.Money(metered)
            : list > 0 ? "≈ " + ChatSpendText.Money(list) + " list"
            : unknown > 0 ? "price unknown"
            : null;
        if (refusing is null && chip is null && warnings.Count == 0) return RunEstimateView.None;

        var head = chip is null ? "Worst case for this run" : $"Worst case for this run · {chip}";
        var tooltip = new StringBuilder("<strong>").Append(WebUtility.HtmlEncode(head)).Append("</strong>");
        foreach (var line in lines) tooltip.Append("<br>").Append(WebUtility.HtmlEncode(line));
        if (meteredCount > 0)
            tooltip.Append("<br>").Append(WebUtility.HtmlEncode(meteredCount > 1
                ? "The metered columns are reserved together before the run: all of them or none."
                : "The metered column is reserved before the run."));
        if (refusing is not null && single is not null && single.Tooltip.Length > 0)
            tooltip.Append("<br>").Append(WebUtility.HtmlEncode(refusing.Name + ":")).Append("<br>").Append(single.Tooltip);
        var label = refusing is not null && single is not null
            ? $"{refusing.Name}: {single.Label}"
            : chip is null ? string.Join(" ", warnings.Select(warning => warning.Text)) : $"Estimated worst-case cost for this run: up to {chip}";
        return new RunEstimateView
        {
            Chip = refusing is null ? chip : null,
            Over = single?.Over,
            Verdict = refusing?.Decision.Verdict.ToString().ToLowerInvariant() ?? "allow",
            Pane = single?.Pane,
            Tooltip = tooltip.ToString(),
            Label = label,
            Warnings = warnings,
        };
    }
}
