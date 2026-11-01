// Decision 0011 (run W4, R2): the estimate by Run all (the mockups' run-estimate chip left of Run all, rendered by the Playground's shared
// _RunEstimate partial). The columns are checked by the Spend Guard's group preflight (W4 fix, compare: SpendGuard.PreflightGroupAsync,
// source compare; nothing reserved, approvals that wait ignored): each column as Run all's group reservation checks it, with the columns
// before it counted, so a run, daily or monthly cap that only the group's total crosses shows here before Run, not only when Run all is
// refused. The chip is the metered columns' worst cases added up (they are reserved together, all or none, when Run all sends); a
// subscription column is list price only and never counted, so it is in the tooltip, not the sum; a free column costs nothing; a column
// without a price says so. The tooltip lists every column. When the group would be asked about or blocked, the chip names the cause ("Over
// request cap", "Over run cap", "Over daily cap", "No price"; a block before an ask) and Run all opens modal-spend-confirm; a cap that only
// the columns together cross is worded as the group's ("the columns together need up to …"), not as the one column that crossed it. A
// cap near its limit shows one amber chip, at the group's total. Run all's reservation stays the authoritative check. Text is plain; the
// tooltip's markup is built from encoded parts only.
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
/// <param name="Decision">The Spend Guard's preflight decision on the column as part of the group (the columns before it counted).</param>
public sealed record CompareColumnEstimate(string Name, string Who, SpendDecision Decision);

/// <summary>Builds the estimate by Run all.</summary>
public static class CompareEstimate
{
    /// <summary>The verb the estimate sits by.</summary>
    public const string Verb = "Run all";

    /// <summary>The estimate of a group whose columns were preflighted as <paramref name="columns"/>.</summary>
    /// <param name="columns">Each column's preflight as part of the group, in order (columns that cannot run are left out).</param>
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
        var together = refusing is null ? null : GroupCause(refusing.Decision);
        var warnings = Warnings(columns, capName);

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
        string label;
        if (refusing is not null && together is not null)
        {
            // Only the group's total crosses this cap: say so, rather than blame the column that happened to cross it.
            var (sentence, detail, action) = GroupText(together, refusing.Decision.Estimate, capName);
            tooltip.Append("<br>").Append(WebUtility.HtmlEncode(sentence + ".")).Append("<br>").Append(WebUtility.HtmlEncode(detail))
                .Append("<br>").Append(WebUtility.HtmlEncode(action));
            label = sentence;
        }
        else if (refusing is not null && single is not null)
        {
            if (single.Tooltip.Length > 0) tooltip.Append("<br>").Append(WebUtility.HtmlEncode(refusing.Name + ":")).Append("<br>").Append(single.Tooltip);
            label = $"{refusing.Name}: {single.Label}";
        }
        else
        {
            label = chip is null ? string.Join(" ", warnings.Select(warning => warning.Text)) : $"Estimated worst-case cost for this run: up to {chip}";
        }
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

    /// <summary>The first cause of <paramref name="decision"/> when it is a cap that only the group's total crosses (the column's own worst
    /// case fits what is left without the columns before it: <see cref="SpendCause.GroupMicros"/>), else null.</summary>
    internal static SpendCause? GroupCause(SpendDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Causes.FirstOrDefault() is not { Kind: SpendCauseKind.Cap, Cap: { } cap, GroupMicros: > 0 } cause) return null;
        if (decision.Estimate.WorstMicros is not { } worst) return null;
        return cause.SpentMicros - cause.GroupMicros!.Value + worst <= cap.LimitMicros ? cause : null;
    }

    /// <summary>The words of a cap only the columns together cross: the sentence (the chip's accessible name), how much they need against
    /// what the cap allows, and what Run all does about it.</summary>
    private static (string Sentence, string Detail, string Action) GroupText(SpendCause cause, SpendEstimate crossing, Func<SpendCap, string>? capName)
    {
        var cap = cause.Cap!;
        var limit = SpendMoney.Format(cap.LimitMicros);
        var group = cause.GroupMicros ?? 0;
        // The columns up to the one that crosses the cap: those before it (counted, not reserved yet) and its own worst case.
        var need = group + (crossing.WorstMicros ?? 0);
        var used = Math.Max(0, cause.SpentMicros - group);
        var window = cap.Period switch { CapPeriod.Run => " in this run", CapPeriod.Day => " today", _ => " this month" };
        var sentence = $"The columns together are over the {limit} {Name(cap, capName)}";
        var detail = $"Together they need up to {ChatSpendText.Money(need)}" +
            (used > 0 || cap.Period != CapPeriod.Run ? $", with {SpendMoney.Format(used)} already used{window}" : "") + ".";
        var action = cause.Action == CapAction.Block
            ? $"The cap is set to Block, so {Verb} can't send the group as it is: try fewer columns or a lower maxTokens."
            : $"The cap is set to Ask, so {Verb} asks you first. Nothing is reserved until you choose.";
        return (sentence, detail, action);
    }

    /// <summary>The amber chips of caps near their limit: one per cap, at the group's total (in a group each column's warning counts the
    /// columns before it, so the highest is the whole group's); a cap the group is already over is the over-cap chip instead.</summary>
    private static List<ChatSpendWarning> Warnings(IReadOnlyList<CompareColumnEstimate> columns, Func<SpendCap, string>? capName)
    {
        var over = columns.SelectMany(column => column.Decision.Causes).Select(cause => cause.Cap?.Id).OfType<long>().ToHashSet();
        return [.. columns.SelectMany(column => column.Decision.Warnings)
            .Where(status => status.SpentMicros is not null && !over.Contains(status.Cap.Id))
            .GroupBy(status => status.Cap.Id)
            .Select(group => group.MaxBy(status => status.SpentMicros)!)
            .Select(status =>
            {
                var warning = ChatSpendText.Warning(status, Name(status.Cap, capName));
                return warning with { Title = warning.Title.Replace(" in this chat", " in this run", StringComparison.Ordinal) };
            })];
    }

    /// <summary>A cap's name in a sentence, worded for a run (as <see cref="RunEstimate"/> names it).</summary>
    private static string Name(SpendCap cap, Func<SpendCap, string>? capName) =>
        capName?.Invoke(cap) ?? ChatSpendText.CapName(cap).Replace("per-chat", "per-run", StringComparison.Ordinal);
}
