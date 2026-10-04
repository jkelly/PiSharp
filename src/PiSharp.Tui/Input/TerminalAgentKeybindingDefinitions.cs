using System.Collections.ObjectModel;

namespace PiSharp.Tui.Input;

/// <summary>Pi v0.99.1 agent definitions and platform overrides. Defining an action does not implement its UI.</summary>
public static class TerminalAgentKeybindingDefinitions
{
    public static bool UseWindowsKeybindings(string platform, IReadOnlyDictionary<string, string?>? environment = null) =>
        platform == "win32" || platform == "linux" && environment is not null &&
        (environment.TryGetValue("WSL_DISTRO_NAME", out var distro) && !string.IsNullOrEmpty(distro) ||
         environment.TryGetValue("WSL_INTEROP", out var interop) && !string.IsNullOrEmpty(interop));

    public static IReadOnlyList<KeyValuePair<string, TerminalKeybindingDefinition>> Create(string platform,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var windows = UseWindowsKeybindings(platform, environment);
        var definitions = TerminalKeybindingDefinitions.Tui.ToList();
        void Replace(string id, TerminalKeybindingValue keys)
        {
            var index = definitions.FindIndex(pair => pair.Key == id);
            definitions[index] = new(id, new(keys, definitions[index].Value.Description));
        }
        Replace("tui.editor.undo", platform == "win32" ? new("ctrl+z") : windows ? new("alt+z") : new("ctrl+-"));
        Replace("tui.altScreen.previousPrompt", windows ? new("ctrl+up") : new(new[] { "ctrl+shift+up", "ctrl+up" }));
        Replace("tui.altScreen.nextPrompt", windows ? new("ctrl+down") : new(new[] { "ctrl+shift+down", "ctrl+down" }));
        Replace("tui.altScreen.search", windows ? new("ctrl+f") : new("ctrl+shift+f"));
        void Add(string id, TerminalKeybindingValue keys, string description) => definitions.Add(new(id, new(keys, description)));
        Add("app.interrupt", new TerminalKeybindingValue("escape"), "Cancel or abort");
        Add("app.clear", new TerminalKeybindingValue("ctrl+c"), "Clear editor");
        Add("app.exit", new TerminalKeybindingValue("ctrl+d"), "Exit when editor is empty");
        Add("app.suspend", (platform == "win32" ? new TerminalKeybindingValue(Array.Empty<string>()) : new TerminalKeybindingValue("ctrl+z")), "Suspend to background");
        Add("app.thinking.cycle", new TerminalKeybindingValue("shift+tab"), "Cycle thinking level");
        Add("app.thinking.save", new TerminalKeybindingValue("ctrl+s"), "Save thinking level");
        Add("app.model.cycleForward", new TerminalKeybindingValue("ctrl+p"), "Cycle to next model");
        Add("app.model.cycleBackward", (windows ? new TerminalKeybindingValue("alt+p") : new TerminalKeybindingValue("shift+ctrl+p")), "Cycle to previous model");
        Add("app.model.select", new TerminalKeybindingValue("ctrl+l"), "Open model selector");
        Add("app.tools.expand", new TerminalKeybindingValue("ctrl+o"), "Toggle tool output");
        Add("app.thinking.toggle", new TerminalKeybindingValue("ctrl+t"), "Toggle thinking blocks");
        Add("app.session.toggleNamedFilter", new TerminalKeybindingValue("ctrl+n"), "Toggle named session filter");
        Add("app.editor.external", new TerminalKeybindingValue("ctrl+g"), "Open external editor");
        Add("app.message.copy", new TerminalKeybindingValue("ctrl+x"), "Copy selection or last assistant message");
        Add("app.message.followUp", (windows ? new TerminalKeybindingValue("ctrl+q") : new TerminalKeybindingValue("alt+enter")), "Queue follow-up message");
        Add("app.message.dequeue", (windows ? new TerminalKeybindingValue("alt+q") : new TerminalKeybindingValue("alt+up")), "Restore queued messages");
        Add("app.clipboard.pasteImage", (windows ? new TerminalKeybindingValue("alt+v") : new TerminalKeybindingValue("ctrl+v")), "Paste files on macOS, images, or text from clipboard");
        Add("app.session.new", new TerminalKeybindingValue(Array.Empty<string>()), "Start a new session");
        Add("app.session.tree", new TerminalKeybindingValue(Array.Empty<string>()), "Open session tree");
        Add("app.session.fork", new TerminalKeybindingValue(Array.Empty<string>()), "Fork current session");
        Add("app.session.resume", new TerminalKeybindingValue(Array.Empty<string>()), "Resume a session");
        Add("app.tree.foldOrUp", (platform == "darwin" ? new TerminalKeybindingValue(new[] { "alt+left", "ctrl+left" }) : new TerminalKeybindingValue(new[] { "ctrl+left", "alt+left" })), "Fold tree branch or move up");
        Add("app.tree.unfoldOrDown", (platform == "darwin" ? new TerminalKeybindingValue(new[] { "alt+right", "ctrl+right" }) : new TerminalKeybindingValue(new[] { "ctrl+right", "alt+right" })), "Unfold tree branch or move down");
        Add("app.tree.editLabel", new TerminalKeybindingValue("shift+l"), "Edit tree label");
        Add("app.tree.toggleLabelTimestamp", new TerminalKeybindingValue("shift+t"), "Toggle tree label timestamps");
        Add("app.session.togglePath", new TerminalKeybindingValue("ctrl+p"), "Toggle session path display");
        Add("app.session.toggleSort", new TerminalKeybindingValue("ctrl+s"), "Toggle session sort mode");
        Add("app.session.rename", new TerminalKeybindingValue("ctrl+r"), "Rename session");
        Add("app.session.delete", new TerminalKeybindingValue("ctrl+d"), "Delete session");
        Add("app.session.deleteNoninvasive", new TerminalKeybindingValue("ctrl+backspace"), "Delete session when query is empty");
        Add("app.models.save", new TerminalKeybindingValue("ctrl+s"), "Save model selection");
        Add("app.models.enableAll", new TerminalKeybindingValue("ctrl+a"), "Enable all models");
        Add("app.models.clearAll", new TerminalKeybindingValue("ctrl+x"), "Clear all models");
        Add("app.models.toggleProvider", new TerminalKeybindingValue("ctrl+p"), "Toggle all models for provider");
        Add("app.models.reorderUp", new TerminalKeybindingValue("alt+up"), "Move model up in order");
        Add("app.models.reorderDown", new TerminalKeybindingValue("alt+down"), "Move model down in order");
        Add("app.tree.filter.default", new TerminalKeybindingValue("ctrl+d"), "Tree filter: default view");
        Add("app.tree.filter.noTools", new TerminalKeybindingValue("ctrl+t"), "Tree filter: hide tool results");
        Add("app.tree.filter.userOnly", new TerminalKeybindingValue("ctrl+u"), "Tree filter: user messages only");
        Add("app.tree.filter.labeledOnly", new TerminalKeybindingValue("ctrl+l"), "Tree filter: labeled entries only");
        Add("app.tree.filter.all", new TerminalKeybindingValue("ctrl+a"), "Tree filter: show all entries");
        Add("app.tree.filter.cycleForward", new TerminalKeybindingValue("ctrl+o"), "Tree filter: cycle forward");
        Add("app.tree.filter.cycleBackward", new TerminalKeybindingValue("shift+ctrl+o"), "Tree filter: cycle backward");
        return new ReadOnlyCollection<KeyValuePair<string, TerminalKeybindingDefinition>>(definitions);
    }
}
