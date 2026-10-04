namespace PiSharp.Tui.Input;

// Defaults/descriptions adapted from Mario Zechner's MIT-licensed Pi.
// Copyright (c) 2025 Mario Zechner; full notice: tests/PiSharp.Tui.Keybindings.Tests/UPSTREAM-LICENSE.
/// <summary>
/// Pi v0.99.1 TUI defaults, in upstream definition order. Agent and platform defaults are separate.
/// Source: d86654abb8862e201933517d6f1fce9f88dd117f/packages/tui/src/keybindings.ts.
/// </summary>
public static class TerminalKeybindingDefinitions
{
    public static IReadOnlyList<KeyValuePair<string, TerminalKeybindingDefinition>> Tui { get; } =
        Array.AsReadOnly(new KeyValuePair<string, TerminalKeybindingDefinition>[]
        {
            Define("tui.editor.cursorUp", "up", "Move cursor up"),
            Define("tui.editor.cursorDown", "down", "Move cursor down"),
            Define("tui.editor.historyPrevious", [], "Select previous prompt history entry"),
            Define("tui.editor.historyNext", [], "Select next prompt history entry"),
            Define("tui.editor.cursorLeft", ["left", "ctrl+b"], "Move cursor left"),
            Define("tui.editor.cursorRight", ["right", "ctrl+f"], "Move cursor right"),
            Define("tui.editor.cursorWordLeft", ["alt+left", "ctrl+left", "alt+b"], "Move cursor word left"),
            Define("tui.editor.cursorWordRight", ["alt+right", "ctrl+right", "alt+f"], "Move cursor word right"),
            Define("tui.editor.cursorLineStart", ["home", "ctrl+home", "ctrl+a"], "Move to line start"),
            Define("tui.editor.cursorLineEnd", ["end", "ctrl+end", "ctrl+e"], "Move to line end"),
            Define("tui.editor.jumpForward", "ctrl+]", "Jump forward to character"),
            Define("tui.editor.jumpBackward", "ctrl+alt+]", "Jump backward to character"),
            Define("tui.editor.pageUp", ["pageUp", "ctrl+pageUp"], "Page up"),
            Define("tui.editor.pageDown", ["pageDown", "ctrl+pageDown"], "Page down"),
            Define("tui.editor.deleteCharBackward", "backspace", "Delete character backward"),
            Define("tui.editor.deleteCharForward", ["delete", "ctrl+d"], "Delete character forward"),
            Define("tui.editor.deleteWordBackward", ["ctrl+w", "alt+backspace"], "Delete word backward"),
            Define("tui.editor.deleteWordForward", ["alt+d", "alt+delete"], "Delete word forward"),
            Define("tui.editor.deleteToLineStart", "ctrl+u", "Delete to line start"),
            Define("tui.editor.deleteToLineEnd", "ctrl+k", "Delete to line end"),
            Define("tui.editor.yank", "ctrl+y", "Yank"),
            Define("tui.editor.yankPop", "alt+y", "Yank pop"),
            Define("tui.editor.undo", "ctrl+-", "Undo"),
            Define("tui.input.newLine", ["shift+enter", "ctrl+j"], "Insert newline"),
            Define("tui.input.submit", "enter", "Submit input"),
            Define("tui.input.tab", "tab", "Tab / autocomplete"),
            Define("tui.input.copy", "ctrl+c", "Copy selection"),
            Define("tui.select.up", "up", "Move selection up"),
            Define("tui.select.down", "down", "Move selection down"),
            Define("tui.select.pageUp", "pageUp", "Selection page up"),
            Define("tui.select.pageDown", "pageDown", "Selection page down"),
            Define("tui.select.confirm", "enter", "Confirm selection"),
            Define("tui.select.cancel", ["escape", "ctrl+c"], "Cancel selection"),
            Define("tui.altScreen.pageUp", "pageUp", "Scroll viewport up one page"),
            Define("tui.altScreen.pageDown", "pageDown", "Scroll viewport down one page"),
            Define("tui.altScreen.halfPageUp", [], "Scroll viewport up half a page"),
            Define("tui.altScreen.halfPageDown", [], "Scroll viewport down half a page"),
            Define("tui.altScreen.lineUp", [], "Scroll viewport up one line"),
            Define("tui.altScreen.lineDown", [], "Scroll viewport down one line"),
            Define("tui.altScreen.previousPrompt", ["ctrl+shift+up", "ctrl+up"], "Jump to previous semantic prompt"),
            Define("tui.altScreen.nextPrompt", ["ctrl+shift+down", "ctrl+down"], "Jump to next semantic prompt"),
            Define("tui.altScreen.search", "ctrl+shift+f", "Search the primary scroll view"),
            Define("tui.altScreen.searchNext", ["enter", "ctrl+g"], "Select the next search match"),
            Define("tui.altScreen.searchPrevious", ["shift+enter", "ctrl+shift+g"], "Select the previous search match"),
            Define("tui.altScreen.searchClose", "escape", "Close transcript search"),
            Define("tui.altScreen.top", "home", "Scroll viewport to top"),
            Define("tui.altScreen.bottom", "end", "Scroll viewport to bottom")
        });

    private static KeyValuePair<string, TerminalKeybindingDefinition> Define(string id, string key, string description) =>
        new(id, new(new TerminalKeybindingValue(key), description));

    private static KeyValuePair<string, TerminalKeybindingDefinition> Define(string id, string[] keys, string description) =>
        new(id, new(new TerminalKeybindingValue(keys), description));
}
