// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/settings-submenu.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source SelectSubmenuOptions.</summary>
internal sealed record SelectSubmenuOptions(bool Searchable = false, SelectListLayoutOptions? Layout = null);

/// <summary>
/// Single-step submenu that shows a titled select list.
/// With <c>Searchable</c>, typing filters the list using fuzzy matching.
/// </summary>
internal sealed class SelectSubmenu : Container, IInputHandler
{
    private static readonly SelectListLayoutOptions SubmenuSelectListLayout = new(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 32);

    private SelectList selectList;
    private readonly int listChildIndex;
    private readonly List<SelectItem> allOptions;
    private readonly SelectListLayoutOptions listLayout;
    private readonly Input? searchInput;
    private readonly Action<string> onSelectCb;
    private readonly Action onCancelCb;
    private readonly Action<string>? onSelectionChangeCb;

    public SelectSubmenu(string title, string description, IReadOnlyList<SelectItem> options, string currentValue, Action<string> onSelect,
        Action onCancel, Action<string>? onSelectionChange = null, SelectSubmenuOptions? submenuOptions = null)
    {
        allOptions = [.. options];
        listLayout = submenuOptions?.Layout ?? SubmenuSelectListLayout;
        onSelectCb = onSelect;
        onCancelCb = onCancel;
        onSelectionChangeCb = onSelectionChange;

        // Title
        AddChild(new Text(theme.Bold(theme.Fg("accent", title)), 0, 0));

        // Description
        if (description.Length > 0)
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("muted", description), 0, 0));
        }

        // Search input
        if (submenuOptions?.Searchable == true)
        {
            AddChild(new Spacer(1));
            searchInput = new Input();
            searchInput.OnSubmit = _ => selectList!.HandleInput("\r");
            AddChild(searchInput);
        }

        // Spacer
        AddChild(new Spacer(1));

        // Select list
        selectList = BuildSelectList(allOptions, currentValue);
        listChildIndex = Children.Count;
        AddChild(selectList);

        // Hint
        AddChild(new Spacer(1));
        var hint = submenuOptions?.Searchable == true
            ? "  Type to filter · Enter to select · Esc to go back"
            : "  Enter to select · Esc to go back";
        AddChild(new Text(theme.Fg("dim", hint), 0, 0));
    }

    private SelectList BuildSelectList(List<SelectItem> options, string preselect)
    {
        var list = new SelectList(options, Math.Min(options.Count, 10), Themes.GetSelectListTheme(), listLayout);

        var idx = options.FindIndex(o => o.Value == preselect);
        if (idx != -1) list.SetSelectedIndex(idx);

        list.OnSelect = item => onSelectCb(item.Value);
        list.OnCancel = onCancelCb;
        if (onSelectionChangeCb is { } cb) list.OnSelectionChange = item => cb(item.Value);

        return list;
    }

    private void ApplyFilter(string query)
    {
        var filtered = query.Length > 0
            ? Fuzzy.Filter(allOptions, query, item => $"{item.Label} {item.Description ?? ""}")
            : allOptions;

        var newList = BuildSelectList(filtered, "");
        Children[listChildIndex] = newList;
        selectList = newList;
    }

    public void HandleInput(string data)
    {
        if (searchInput is not null)
        {
            var kb = KeybindingsManager.Global;
            var isNav = kb.Matches(data, "tui.select.up") || kb.Matches(data, "tui.select.down") ||
                kb.Matches(data, "tui.select.confirm") || kb.Matches(data, "tui.select.cancel");
            if (isNav) selectList.HandleInput(data);
            else
            {
                searchInput.HandleInput(data);
                ApplyFilter(searchInput.GetValue());
            }
        }
        else selectList.HandleInput(data);
    }
}

// ============================================================================
// SteppedSubmenu — reusable multi-step selector
// ============================================================================

/// <summary>One step in a <see cref="SteppedSubmenu"/>. Title and description receive prior selections (source: string or function).</summary>
internal sealed class SteppedSubmenuStep
{
    /// <summary>Unique key — the selected value is stored in the result context under this key.</summary>
    public required string Key { get; init; }
    /// <summary>Title shown at the top of the step. Receives prior selections.</summary>
    public required Func<IReadOnlyDictionary<string, string>, string> Title { get; init; }
    /// <summary>Description shown below the title. Receives prior selections.</summary>
    public required Func<IReadOnlyDictionary<string, string>, string> Description { get; init; }
    /// <summary>Build the option list for this step. Called fresh each time the step is shown.</summary>
    public required Func<IReadOnlyDictionary<string, string>, List<SelectItem>> Options { get; init; }
    /// <summary>Optionally pre-select a value when entering this step.</summary>
    public Func<IReadOnlyDictionary<string, string>, string?>? Preselect { get; init; }
    /// <summary>Enable type-to-search fuzzy filtering for this step.</summary>
    public bool Searchable { get; init; }
    /// <summary>Override the select list layout (column widths) for this step.</summary>
    public SelectListLayoutOptions? Layout { get; init; }

    /// <summary>A constant title or description (the source's string form).</summary>
    public static Func<IReadOnlyDictionary<string, string>, string> Fixed(string text) => _ => text;
}

/// <summary>Source SteppedSubmenuOptions.</summary>
internal sealed record SteppedSubmenuOptions
{
    /// <summary>Start at this step index (0-based), skipping earlier steps. Requires InitialContext for skipped keys.</summary>
    public int? StartAtStep { get; init; }
    /// <summary>Pre-fill selections for skipped steps.</summary>
    public IReadOnlyDictionary<string, string>? InitialContext { get; init; }
    /// <summary>After completing the last step, loop back to step 0 instead of closing.</summary>
    public bool Loop { get; init; }
}

/// <summary>
/// Generic N-step submenu built on top of <see cref="SelectSubmenu"/>.
/// Each step's options can depend on prior selections via the shared context.
/// Esc goes back one step; Esc at step 0 cancels.
/// With <c>Loop</c>, completing the final step invokes onComplete then returns to step 0.
/// </summary>
internal sealed class SteppedSubmenu : Container, IInputHandler
{
    private readonly IReadOnlyList<SteppedSubmenuStep> steps;
    private readonly Action<IReadOnlyDictionary<string, string>> onComplete;
    private readonly Action onCancel;
    private readonly SteppedSubmenuOptions opts;
    private IComponent activeComponent;
    private Dictionary<string, string> context;

    public SteppedSubmenu(IReadOnlyList<SteppedSubmenuStep> steps, Action<IReadOnlyDictionary<string, string>> onComplete, Action onCancel,
        SteppedSubmenuOptions? opts = null)
    {
        this.steps = steps;
        this.onComplete = onComplete;
        this.onCancel = onCancel;
        this.opts = opts ?? new();
        context = new(this.opts.InitialContext ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        activeComponent = BuildStep(this.opts.StartAtStep ?? 0);
    }

    private SelectSubmenu BuildStep(int stepIndex)
    {
        var step = steps[stepIndex];
        var total = steps.Count;
        var stepLabel = total > 1 ? $"Step {stepIndex + 1}/{total} · " : "";

        var title = step.Title(context);
        var desc = step.Description(context);
        var items = step.Options(context);
        var preselect = step.Preselect?.Invoke(context) ?? "";

        return new SelectSubmenu(
            title,
            $"{stepLabel}{desc}",
            items,
            preselect,
            value =>
            {
                context[step.Key] = value;

                if (stepIndex < total - 1)
                {
                    // Advance to next step
                    activeComponent = BuildStep(stepIndex + 1);
                }
                else
                {
                    // Final step — deliver result
                    onComplete(new Dictionary<string, string>(context, StringComparer.Ordinal));

                    if (opts.Loop)
                    {
                        context = new(StringComparer.Ordinal);
                        activeComponent = BuildStep(0);
                    }
                    else onCancel();
                }
            },
            () =>
            {
                if (stepIndex > 0)
                {
                    context.Remove(step.Key);
                    activeComponent = BuildStep(stepIndex - 1);
                }
                else onCancel();
            },
            null,
            step.Searchable || step.Layout is not null ? new SelectSubmenuOptions(step.Searchable, step.Layout) : null);
    }

    public override List<string> Render(int width) => activeComponent.Render(width);

    public void HandleInput(string data) => (activeComponent as IInputHandler)?.HandleInput(data);

    public override void Invalidate() => activeComponent.Invalidate();
}
