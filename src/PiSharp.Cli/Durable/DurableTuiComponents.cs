// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/experimental/durable/tui.ts (SELECT_THEME, ListSelector,
// CompactionComponent). Built from PiSharp.Tui's ports of pi-tui's Input, SelectList, Box, Text, Markdown and Spacer, pi's DynamicBorder
// and theme. PiSharp-only since pi-durable 1.2.0 (durable work list area l): a tool card's group with the rows of its nested calls.
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Durable;

/// <summary>A filterable list in place of the editor.</summary>
internal sealed class DurableListSelector : Container, IFocusable, IInputHandler
{
    private static readonly string[] Forwarded = ["tui.select.up", "tui.select.down", "tui.select.confirm", "tui.select.cancel"];

    private readonly Input input = new();
    private readonly Container listContainer = new();
    private readonly IReadOnlyList<SelectItem> items;
    private readonly Action<string> onSelect;
    private readonly Action onCancel;
    private SelectList list;
    private bool focused;

    public DurableListSelector(string title, IReadOnlyList<SelectItem> items, Action<string> onSelect, Action onCancel)
    {
        this.items = items;
        this.onSelect = onSelect;
        this.onCancel = onCancel;
        list = Build(items);
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
        AddChild(input);
        AddChild(new Spacer(1));
        AddChild(listContainer);
        AddChild(new DynamicBorder());
    }

    /// <summary>The list theme (upstream <c>SELECT_THEME</c>).</summary>
    internal static SelectListTheme SelectTheme => new(
        text => theme.Fg("accent", text),
        text => theme.Fg("accent", text),
        text => theme.Fg("muted", text),
        text => theme.Fg("dim", text),
        text => theme.Fg("warning", text));

    public bool Focused
    {
        get => focused;
        set
        {
            focused = value;
            input.Focused = value;
        }
    }

    public void HandleInput(string data)
    {
        var keybindings = PiSharp.Tui.Pi.KeybindingsManager.Global;
        if (Forwarded.Any(action => keybindings.Matches(data, action)))
        {
            list.HandleInput(data);
            return;
        }
        input.HandleInput(data);
        var query = input.GetValue();
        var filtered = query.Length == 0 ? items : Fuzzy.Filter(items, query, item => $"{item.Label} {item.Value}");
        list = Build(filtered);
    }

    private SelectList Build(IReadOnlyList<SelectItem> shown)
    {
        var built = new SelectList(shown, 10, SelectTheme)
        {
            OnSelect = item => onSelect(item.Value),
            OnCancel = onCancel,
        };
        listContainer.Clear();
        listContainer.AddChild(built);
        return built;
    }
}

/// <summary>The summary that replaced earlier context: collapsed to one line until expanded.</summary>
internal sealed class DurableCompactionComponent : Box
{
    private readonly string summary;

    public DurableCompactionComponent(string summary, bool expanded)
        : base(1, 1, text => theme.Bg("customMessageBg", text))
    {
        this.summary = summary;
        SetExpanded(expanded);
    }

    public void SetExpanded(bool expanded)
    {
        Clear();
        AddChild(new Text(theme.Fg("customMessageLabel", theme.Bold("[compaction]")), 0, 0));
        AddChild(new Spacer(1));
        AddChild(expanded
            ? new Markdown(summary, 0, 0, Themes.GetMarkdownTheme(), new DefaultTextStyle(Color: text => theme.Fg("customMessageText", text)))
            : new Text(
                theme.Fg("customMessageText", "Earlier context summarized (") +
                    theme.Fg("dim", KeybindingHints.KeyText("app.tools.expand")) +
                    theme.Fg("customMessageText", " to expand)"),
                0,
                0));
    }
}

/// <summary>A tool call's card and, under it, the nested calls it made (PiSharp-only, pi-durable 1.2.0).</summary>
internal sealed class DurableToolGroup : Container
{
    public DurableToolGroup(ToolExecutionComponent card)
    {
        Card = card;
        AddChild(card);
        AddChild(Nested);
    }

    /// <summary>The call's card.</summary>
    public ToolExecutionComponent Card { get; }

    /// <summary>The rows of its nested calls, empty until it makes one.</summary>
    public DurableNestedCalls Nested { get; } = new();
}

/// <summary>
/// The nested tool calls of one model-issued call, one row each in the order they were made, a nested call's own calls indented under it
/// (PiSharp-only, pi-durable 1.2.0). A row is pi's codemode row (<see cref="CodemodeRenderers.FormatCall"/>): status, name, arguments,
/// duration and cost, and the error when expanded. Collapsed, the newest eight rows show, as in codemode's result.
/// </summary>
internal sealed class DurableNestedCalls : Container
{
    private const int CollapsedRows = 8;

    private readonly SortedDictionary<long, (System.Text.Json.Nodes.JsonObject Call, int Depth)> rows = [];
    private readonly Text text = new("", 2, 0);
    private bool expanded;

    /// <summary>Sets the row of nested call <paramref name="taskId"/>: <paramref name="call"/> is <c>{name, args, status, durationMs?, cost?,
    /// error?}</c> with codemode's statuses, <paramref name="depth"/> 1 for a call the model-issued call made.</summary>
    public void Set(long taskId, System.Text.Json.Nodes.JsonObject call, int depth)
    {
        if (rows.TryGetValue(taskId, out var row) && row.Depth == depth && System.Text.Json.Nodes.JsonNode.DeepEquals(row.Call, call)) return;
        rows[taskId] = (call, depth);
        Refresh();
    }

    /// <summary>The number of rows (tests).</summary>
    internal int Count => rows.Count;

    public void SetExpanded(bool value)
    {
        expanded = value;
        Refresh();
    }

    private void Refresh()
    {
        Clear();
        if (rows.Count == 0) return;
        var all = rows.Values.ToList();
        var shown = expanded ? all : all.Skip(Math.Max(0, all.Count - CollapsedRows)).ToList();
        var lines = shown.Select(row =>
        {
            var indent = new string(' ', 2 * (row.Depth - 1));
            return indent + CodemodeRenderers.FormatCall(row.Call, theme, expanded).Replace("\n", "\n" + indent, StringComparison.Ordinal);
        }).ToList();
        if (shown.Count < all.Count)
            lines.Insert(0, theme.Fg("muted", $"... ({all.Count - shown.Count} earlier calls,") + " " +
                KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")"));
        text.SetText(string.Join("\n", lines));
        AddChild(text);
    }
}
