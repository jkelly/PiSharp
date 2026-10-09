// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/keybindings.ts.
namespace PiSharp.Tui.Pi;

public sealed record KeybindingDefinition(IReadOnlyList<string> DefaultKeys, string? Description = null)
{
    public KeybindingDefinition(string defaultKey, string? description = null) : this([defaultKey], description) { }
}
public sealed record KeybindingConflict(string Key, IReadOnlyList<string> Keybindings);

/// <summary>Resolves keybinding ids (<c>tui.select.up</c>, <c>app.model.select</c>) to keys, with user overrides.</summary>
public sealed class KeybindingsManager
{
    /// <summary>The TUI library's own bindings in upstream order.</summary>
    public static IReadOnlyList<KeyValuePair<string, KeybindingDefinition>> TuiKeybindings { get; } =
    [
        new("tui.editor.cursorUp", new("up", "Move cursor up")),
        new("tui.editor.cursorDown", new("down", "Move cursor down")),
        new("tui.editor.historyPrevious", new([], "Select previous prompt history entry")),
        new("tui.editor.historyNext", new([], "Select next prompt history entry")),
        new("tui.editor.cursorLeft", new(["left", "ctrl+b"], "Move cursor left")),
        new("tui.editor.cursorRight", new(["right", "ctrl+f"], "Move cursor right")),
        new("tui.editor.cursorWordLeft", new(["alt+left", "ctrl+left", "alt+b"], "Move cursor word left")),
        new("tui.editor.cursorWordRight", new(["alt+right", "ctrl+right", "alt+f"], "Move cursor word right")),
        new("tui.editor.cursorLineStart", new(["home", "ctrl+a"], "Move to line start")),
        new("tui.editor.cursorLineEnd", new(["end", "ctrl+e"], "Move to line end")),
        new("tui.editor.jumpForward", new("ctrl+]", "Jump forward to character")),
        new("tui.editor.jumpBackward", new("ctrl+alt+]", "Jump backward to character")),
        new("tui.editor.pageUp", new(["pageUp", "ctrl+pageUp"], "Page up")),
        new("tui.editor.pageDown", new(["pageDown", "ctrl+pageDown"], "Page down")),
        new("tui.editor.deleteCharBackward", new("backspace", "Delete character backward")),
        new("tui.editor.deleteCharForward", new(["delete", "ctrl+d"], "Delete character forward")),
        new("tui.editor.deleteWordBackward", new(["ctrl+w", "alt+backspace"], "Delete word backward")),
        new("tui.editor.deleteWordForward", new(["alt+d", "alt+delete"], "Delete word forward")),
        new("tui.editor.deleteToLineStart", new("ctrl+u", "Delete to line start")),
        new("tui.editor.deleteToLineEnd", new("ctrl+k", "Delete to line end")),
        new("tui.editor.yank", new("ctrl+y", "Yank")),
        new("tui.editor.yankPop", new("alt+y", "Yank pop")),
        new("tui.editor.undo", new("ctrl+-", "Undo")),
        new("tui.input.newLine", new(["shift+enter", "ctrl+j"], "Insert newline")),
        new("tui.input.submit", new("enter", "Submit input")),
        new("tui.input.tab", new("tab", "Tab / autocomplete")),
        new("tui.input.copy", new("ctrl+c", "Copy selection")),
        new("tui.select.up", new("up", "Move selection up")),
        new("tui.select.down", new("down", "Move selection down")),
        new("tui.select.pageUp", new("pageUp", "Selection page up")),
        new("tui.select.pageDown", new("pageDown", "Selection page down")),
        new("tui.select.confirm", new("enter", "Confirm selection")),
        new("tui.select.cancel", new(["escape", "ctrl+c"], "Cancel selection")),
        new("tui.altScreen.pageUp", new("pageUp", "Scroll viewport up one page")),
        new("tui.altScreen.pageDown", new("pageDown", "Scroll viewport down one page")),
        new("tui.altScreen.halfPageUp", new([], "Scroll viewport up half a page")),
        new("tui.altScreen.halfPageDown", new([], "Scroll viewport down half a page")),
        new("tui.altScreen.lineUp", new([], "Scroll viewport up one line")),
        new("tui.altScreen.lineDown", new([], "Scroll viewport down one line")),
        new("tui.altScreen.previousPrompt", new(["ctrl+shift+up", "ctrl+up"], "Jump to previous semantic prompt")),
        new("tui.altScreen.nextPrompt", new(["ctrl+shift+down", "ctrl+down"], "Jump to next semantic prompt")),
        new("tui.altScreen.search", new("ctrl+shift+f", "Search the primary scroll view")),
        new("tui.altScreen.searchNext", new(["enter", "ctrl+g"], "Select the next search match")),
        new("tui.altScreen.searchPrevious", new(["shift+enter", "ctrl+shift+g"], "Select the previous search match")),
        new("tui.altScreen.searchClose", new("escape", "Close transcript search")),
        new("tui.altScreen.top", new("ctrl+home", "Scroll viewport to top")),
        new("tui.altScreen.bottom", new("ctrl+end", "Scroll viewport to bottom")),
    ];

    private readonly List<KeyValuePair<string, KeybindingDefinition>> definitions;
    private readonly Dictionary<string, KeybindingDefinition> definitionsById;
    private Dictionary<string, IReadOnlyList<string>?> userBindings;
    private readonly Dictionary<string, List<string>> keysById = new(StringComparer.Ordinal);
    private List<KeybindingConflict> conflicts = [];

    public KeybindingsManager(IEnumerable<KeyValuePair<string, KeybindingDefinition>> definitions, IReadOnlyDictionary<string, IReadOnlyList<string>?>? userBindings = null)
    {
        this.definitions = [.. definitions];
        definitionsById = new(StringComparer.Ordinal);
        foreach (var (id, definition) in this.definitions) definitionsById[id] = definition;
        this.userBindings = userBindings is null ? new(StringComparer.Ordinal) : new(userBindings, StringComparer.Ordinal);
        Rebuild();
    }

    private static List<string> Normalize(IReadOnlyList<string>? keys) => keys is null ? [] : keys.Distinct(StringComparer.Ordinal).ToList();

    private void Rebuild()
    {
        keysById.Clear(); conflicts = [];
        var claims = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (binding, keys) in userBindings)
        {
            if (!definitionsById.ContainsKey(binding)) continue;
            foreach (var key in Normalize(keys))
            {
                if (!claims.TryGetValue(key, out var claimants)) claims[key] = claimants = [];
                if (!claimants.Contains(binding)) claimants.Add(binding);
            }
        }
        foreach (var (key, claimants) in claims) if (claimants.Count > 1) conflicts.Add(new(key, [.. claimants]));
        foreach (var (id, definition) in definitions)
            keysById[id] = userBindings.TryGetValue(id, out var user) ? Normalize(user) : Normalize(definition.DefaultKeys);
    }

    public bool Matches(string data, string keybinding)
    {
        if (!keysById.TryGetValue(keybinding, out var keys)) return false;
        foreach (var key in keys) if (Keys.Matches(data, key)) return true;
        return false;
    }
    public IReadOnlyList<string> GetKeys(string keybinding) => keysById.TryGetValue(keybinding, out var keys) ? [.. keys] : [];
    public KeybindingDefinition? GetDefinition(string keybinding) => definitionsById.GetValueOrDefault(keybinding);
    public IReadOnlyList<KeyValuePair<string, KeybindingDefinition>> Definitions => definitions;
    public IReadOnlyList<KeybindingConflict> GetConflicts() => [.. conflicts];
    public void SetUserBindings(IReadOnlyDictionary<string, IReadOnlyList<string>?> bindings) { userBindings = new(bindings, StringComparer.Ordinal); Rebuild(); }
    public IReadOnlyDictionary<string, IReadOnlyList<string>?> GetUserBindings() => new Dictionary<string, IReadOnlyList<string>?>(userBindings, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetResolvedBindings() =>
        definitions.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)(keysById.TryGetValue(pair.Key, out var keys) ? [.. keys] : []), StringComparer.Ordinal);

    private static KeybindingsManager? global;
    private static readonly object GlobalGate = new();
    public static void SetGlobal(KeybindingsManager manager) { lock (GlobalGate) global = manager; }
    /// <summary>The process-wide bindings (<c>getKeybindings</c>); the TUI defaults until the app sets its own.</summary>
    public static KeybindingsManager Global { get { lock (GlobalGate) return global ??= new(TuiKeybindings); } }
}
