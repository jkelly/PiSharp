// Decision 0011 (run W4, R2; brief rule 4): what each Compare column sends. A column runs the shared prompt with the shared options and its
// own overrides, planned by the run engine itself (RunEngine.Plan: RunModelOptions' check of what the model and its API take). Shared
// options apply to each column only where its model takes them, and the column says so: a shared thinking level the model does not list is
// clamped to its nearest level ("thinking xhigh → high", the mockups' "Levels are clamped per model"), a model that does not think gets
// none ("thinking n/a"), a shared max tokens above the model's output cap is capped to it, and a shared option the model rejects (a reasoning
// model's temperature) is not sent ("temperature n/a", with the reason). An override is the user's explicit choice for that column: one the
// model does not take is never dropped or changed, the run is refused with the column and the reason, and nothing is sent.
using System.Globalization;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Compare;

/// <summary>A chip under a column's model: an override, or how a shared option was applied.</summary>
/// <param name="Text">What it says.</param>
/// <param name="Tone">The ps-chip tone (accent for an override), or null.</param>
/// <param name="Title">The tooltip, or null.</param>
/// <param name="Kind">override, adjusted or not-sent.</param>
public sealed record CompareChip(string Text, string? Tone = null, string? Title = null, string Kind = "adjusted");

/// <summary>What one column sends.</summary>
public sealed record CompareColumnPlan
{
    /// <summary>The column's position, from 0.</summary>
    public required int Index { get; init; }

    /// <summary>The column as the page sent it.</summary>
    public required CompareColumnDraft Column { get; init; }

    /// <summary>What the model takes, or null when it is not a model.</summary>
    public RunCapabilities? Capabilities { get; init; }

    /// <summary>The run this column sends (source compare), or null when it cannot run.</summary>
    public RunSpec? Spec { get; init; }

    /// <summary>The run engine's plan of <see cref="Spec"/>, or null.</summary>
    public RunRequestPlan? Plan { get; init; }

    /// <summary>The overrides the model does not take (the run is refused for them).</summary>
    public IReadOnlyList<RunOptionSupport> Refused { get; init; } = [];

    /// <summary>The overrides and how the shared options were applied.</summary>
    public IReadOnlyList<CompareChip> Chips { get; init; } = [];

    /// <summary>Why the column cannot run, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The thinking level sent, or null for none.</summary>
    public string? Thinking => Spec?.ThinkingLevel;

    /// <summary>Whether the column can run.</summary>
    public bool Ok => Error is null && Plan is { Allowed: true };
}

/// <summary>What a Compare draft sends, column by column.</summary>
/// <param name="Draft">The draft.</param>
/// <param name="Columns">Each column's plan.</param>
public sealed record ComparePlan(CompareDraft Draft, IReadOnlyList<CompareColumnPlan> Columns)
{
    /// <summary>Why Run all cannot send the draft, or null when it can.</summary>
    public string? Message
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Draft.Prompt)) return "Write a prompt to compare the models on.";
            if (Columns.Count < CompareDraft.MinColumns) return $"Add at least {CompareDraft.MinColumns} models to compare.";
            if (Columns.Count > CompareDraft.MaxColumns) return $"Compare runs at most {CompareDraft.MaxColumns} models.";
            return Columns.FirstOrDefault(column => !column.Ok)?.Error;
        }
    }

    /// <summary>Whether Run all can send the draft.</summary>
    public bool Valid => Message is null;

    /// <summary>The runs Run all sends, one per column.</summary>
    /// <exception cref="ArgumentException">The draft cannot be sent (<see cref="Message"/>).</exception>
    public IReadOnlyList<RunSpec> Specs => Valid ? [.. Columns.Select(column => column.Spec!)] : throw new CompareRefusedException(this);
}

/// <summary>A Compare run refused before anything was reserved or sent: an override the model does not take, an unknown model, or a draft
/// that is not complete (400).</summary>
public sealed class CompareRefusedException(ComparePlan plan) : ArgumentException(plan.Message)
{
    /// <summary>The plan.</summary>
    public ComparePlan Plan { get; } = plan;

    /// <summary>The first column that cannot run, or null.</summary>
    public CompareColumnPlan? Column => Plan.Columns.FirstOrDefault(column => !column.Ok);
}

/// <summary>Plans Compare drafts through the run engine.</summary>
public static class ComparePlanner
{
    /// <summary>The plan of every column of <paramref name="draft"/>.</summary>
    public static ComparePlan Plan(CompareDraft draft, RunEngine engine)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(engine);
        var trimmed = draft.Trimmed();
        return new ComparePlan(trimmed, [.. trimmed.Columns.Select((column, index) => Column(trimmed, column, index, engine))]);
    }

    /// <summary>The plan of one column of <paramref name="draft"/> (its shared prompt and options with <paramref name="column"/>'s overrides).</summary>
    public static CompareColumnPlan Column(CompareDraft draft, CompareColumnDraft column, int index, RunEngine engine)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(engine);
        column = column.Trimmed();
        var label = $"Column {index + 1}";
        if (column.Provider.Length == 0 || column.Model.Length == 0)
            return new CompareColumnPlan { Index = index, Column = column, Error = $"{label}: choose a model." };
        var capabilities = engine.Capabilities(column.Provider, column.Model);
        if (capabilities is null)
            return new CompareColumnPlan { Index = index, Column = column, Error = $"{label}: there is no model {column.Provider}/{column.Model}. Choose another model." };
        var name = capabilities.Name;
        var chips = new List<CompareChip>();
        var levels = capabilities.ThinkingLevels;

        // Thinking: the override as set; the shared level clamped to what the model lists.
        string? thinking = null;
        if (column.Thinking is { } own)
        {
            thinking = own;
            chips.Add(new CompareChip("thinking " + own, "accent", "Per-column override", "override"));
        }
        else if (draft.Thinking is { } shared && shared != "off")
        {
            if (levels.Count <= 1)
                chips.Add(new CompareChip("thinking n/a", null, $"{name} does not think, so the shared thinking level is not sent.", "not-sent"));
            else if (levels.Contains(shared)) thinking = shared;
            else if (Nearest(shared, levels) is { } nearest)
            {
                thinking = nearest;
                chips.Add(new CompareChip($"thinking {shared} → {nearest}", null,
                    $"{name} thinks at {string.Join(", ", levels.Skip(1))}; the shared level {shared} is clamped to the nearest, {nearest}.", "adjusted"));
            }
            else chips.Add(new CompareChip("thinking n/a", null, $"{name} lists no level PiSharp sends, so the shared thinking level is not sent.", "not-sent"));
        }

        // Max tokens: shared only; above the model's output cap it is capped to it.
        var maxTokens = draft.MaxTokens;
        if (maxTokens is { } max && capabilities.MaxTokens is { } cap && max > cap)
        {
            maxTokens = cap;
            chips.Add(new CompareChip("maxTokens " + cap.ToString("N0", CultureInfo.InvariantCulture), null,
                $"{name} writes at most {cap.ToString("N0", CultureInfo.InvariantCulture)} output tokens, so the shared {max.ToString("N0", CultureInfo.InvariantCulture)} is capped to it.", "adjusted"));
        }

        var temperature = column.Temperature ?? draft.Temperature;
        if (column.Temperature is { } ownTemperature)
            chips.Add(new CompareChip("temperature " + ownTemperature.ToString("0.##", CultureInfo.InvariantCulture), "accent", "Per-column override", "override"));

        var spec = new RunSpec
        {
            Provider = column.Provider,
            Model = column.Model,
            Account = column.Account,
            Source = SpendSource.Compare.Wire(),
            SystemPrompt = draft.SystemPrompt,
            Messages = [new RunMessage { Role = "user", Text = draft.Prompt }],
            Thinking = thinking,
            MaxTokens = maxTokens,
            Temperature = temperature,
        };
        RunRequestPlan plan;
        try
        {
            plan = engine.Plan(spec);
            // A shared option the model rejects is not sent (and the column says why); an override it rejects refuses the run.
            var drop = plan.Refused.Where(refused => Shared(refused.Option, column)).ToList();
            if (drop.Count > 0)
            {
                foreach (var refused in drop)
                {
                    spec = refused.Option switch
                    {
                        RunModelOptions.Temperature => spec with { Temperature = null },
                        RunModelOptions.Thinking => spec with { Thinking = null },
                        RunModelOptions.MaxTokens => spec with { MaxTokens = null },
                        _ => spec,
                    };
                    chips.Add(new CompareChip(refused.Option + " n/a", null,
                        $"The shared {refused.Option} is not sent to {name}" + (refused.Reason is { } reason ? ": " + reason + "." : "."), "not-sent"));
                }
                plan = engine.Plan(spec);
            }
        }
        catch (ArgumentException error)
        {
            return new CompareColumnPlan { Index = index, Column = column, Capabilities = capabilities, Chips = chips, Error = $"{label}: {error.Message}" };
        }
        if (!plan.Allowed)
        {
            var what = string.Join("; ", plan.Refused.Select(refused => refused.Option + (refused.Reason is { } reason ? " (" + reason + ")" : "")));
            return new CompareColumnPlan
            {
                Index = index,
                Column = column,
                Capabilities = capabilities,
                Spec = plan.Spec,
                Plan = plan,
                Refused = plan.Refused,
                Chips = chips,
                Error = $"{label} ({column.Provider}/{column.Model}): {name} does not take the override {what}. Change the override or the model; nothing was sent.",
            };
        }
        // A spec as RunEngine.StartGroupAsync re-plans it: the normalized one the plan carries.
        return new CompareColumnPlan { Index = index, Column = column, Capabilities = capabilities, Spec = plan.Spec, Plan = plan, Chips = chips };
    }

    /// <summary>The level of <paramref name="levels"/> (the model's, off first) nearest to <paramref name="level"/> (lower on a tie), or null.</summary>
    public static string? Nearest(string level, IReadOnlyList<string> levels)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(levels);
        var order = RunSpec.ThinkingLevels;
        var target = IndexOf(order, level);
        if (target < 0) return null;
        string? best = null;
        var distance = int.MaxValue;
        foreach (var candidate in levels)
        {
            if (candidate == "off") continue;
            var at = IndexOf(order, candidate);
            if (at < 0) continue;
            var gap = Math.Abs(at - target);
            if (gap < distance || (gap == distance && best is not null && at < IndexOf(order, best)))
            {
                best = candidate;
                distance = gap;
            }
        }
        return best;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var index = 0; index < list.Count; index++)
            if (list[index] == value) return index;
        return -1;
    }

    // Whether a refused option came from the shared options (not the column's override).
    private static bool Shared(string option, CompareColumnDraft column) => option switch
    {
        RunModelOptions.Thinking => column.Thinking is null,
        RunModelOptions.Temperature => column.Temperature is null,
        RunModelOptions.MaxTokens => true,
        _ => false,
    };
}
