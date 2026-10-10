// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/keybindings.ts (KEYBINDINGS, migrations,
// KeybindingsManager.create/reload) and src/modes/interactive/components/keybinding-hints.ts (keyText, keyDisplayText, keyHint,
// rawKeyHint).
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using TuiKeybindingsManager = PiSharp.Tui.Pi.KeybindingsManager;

namespace PiSharp.Cli.Interactive.Mode;

internal static class AppKeybindings
{
    /// <summary>useWindowsKeybindings: Windows, or Linux under WSL.</summary>
    public static bool UseWindowsKeybindings(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && (!string.IsNullOrEmpty(env("WSL_DISTRO_NAME")) || !string.IsNullOrEmpty(env("WSL_INTEROP")));
    }

    public static IReadOnlyList<KeyValuePair<string, KeybindingDefinition>> Definitions(Func<string, string?>? env = null)
    {
        var windows = UseWindowsKeybindings(env);
        var list = new List<KeyValuePair<string, KeybindingDefinition>>();
        foreach (var (id, definition) in TuiKeybindingsManager.TuiKeybindings)
        {
            var current = definition;
            if (id == "tui.editor.undo") current = definition with { DefaultKeys = [OperatingSystem.IsWindows() ? "ctrl+z" : windows ? "alt+z" : "ctrl+-"] };
            else if (id == "tui.altScreen.previousPrompt") current = definition with { DefaultKeys = windows ? ["ctrl+up"] : ["ctrl+shift+up", "ctrl+up"] };
            else if (id == "tui.altScreen.nextPrompt") current = definition with { DefaultKeys = windows ? ["ctrl+down"] : ["ctrl+shift+down", "ctrl+down"] };
            else if (id == "tui.altScreen.search") current = definition with { DefaultKeys = [windows ? "ctrl+f" : "ctrl+shift+f"] };
            list.Add(new(id, current));
        }
        void Add(string id, string[] keys, string description) => list.Add(new(id, new KeybindingDefinition(keys, description)));
        Add("app.interrupt", ["escape"], "Cancel or abort");
        Add("app.clear", ["ctrl+c"], "Clear editor");
        Add("app.exit", ["ctrl+d"], "Exit when editor is empty");
        Add("app.suspend", OperatingSystem.IsWindows() ? [] : ["ctrl+z"], "Suspend to background");
        Add("app.thinking.cycle", ["shift+tab"], "Cycle thinking level");
        Add("app.thinking.save", ["ctrl+s"], "Save thinking level");
        Add("app.model.cycleForward", ["ctrl+p"], "Cycle to next model");
        Add("app.model.cycleBackward", [windows ? "alt+p" : "shift+ctrl+p"], "Cycle to previous model");
        Add("app.model.select", ["ctrl+l"], "Open model selector");
        Add("app.tools.expand", ["ctrl+o"], "Toggle tool output");
        Add("app.thinking.toggle", ["ctrl+t"], "Toggle thinking blocks");
        Add("app.session.toggleNamedFilter", ["ctrl+n"], "Toggle named session filter");
        Add("app.editor.external", ["ctrl+g"], "Open external editor");
        Add("app.message.copy", ["ctrl+x"], "Copy selection or last assistant message");
        Add("app.message.followUp", [windows ? "ctrl+q" : "alt+enter"], "Queue follow-up message");
        Add("app.message.dequeue", [windows ? "alt+q" : "alt+up"], "Restore queued messages");
        Add("app.clipboard.pasteImage", [windows ? "alt+v" : "ctrl+v"], "Paste files on macOS, images, or text from clipboard");
        Add("app.session.new", [], "Start a new session");
        Add("app.session.tree", [], "Open session tree");
        Add("app.session.fork", [], "Fork current session");
        Add("app.session.resume", [], "Resume a session");
        Add("app.tree.foldOrUp", OperatingSystem.IsMacOS() ? ["alt+left", "ctrl+left"] : ["ctrl+left", "alt+left"], "Fold tree branch or move up");
        Add("app.tree.unfoldOrDown", OperatingSystem.IsMacOS() ? ["alt+right", "ctrl+right"] : ["ctrl+right", "alt+right"], "Unfold tree branch or move down");
        Add("app.tree.editLabel", ["shift+l"], "Edit tree label");
        Add("app.tree.toggleLabelTimestamp", ["shift+t"], "Toggle tree label timestamps");
        Add("app.session.togglePath", ["ctrl+p"], "Toggle session path display");
        Add("app.session.toggleSort", ["ctrl+s"], "Toggle session sort mode");
        Add("app.session.rename", ["ctrl+r"], "Rename session");
        Add("app.session.delete", ["ctrl+d"], "Delete session");
        Add("app.session.deleteNoninvasive", ["ctrl+backspace"], "Delete session when query is empty");
        Add("app.models.save", ["ctrl+s"], "Save model selection");
        Add("app.models.enableAll", ["ctrl+a"], "Enable all models");
        Add("app.models.clearAll", ["ctrl+x"], "Clear all models");
        Add("app.models.toggleProvider", ["ctrl+p"], "Toggle all models for provider");
        Add("app.models.reorderUp", ["alt+up"], "Move model up in order");
        Add("app.models.reorderDown", ["alt+down"], "Move model down in order");
        Add("app.tree.filter.default", ["ctrl+d"], "Tree filter: default view");
        Add("app.tree.filter.noTools", ["ctrl+t"], "Tree filter: hide tool results");
        Add("app.tree.filter.userOnly", ["ctrl+u"], "Tree filter: user messages only");
        Add("app.tree.filter.labeledOnly", ["ctrl+l"], "Tree filter: labeled entries only");
        Add("app.tree.filter.all", ["ctrl+a"], "Tree filter: show all entries");
        Add("app.tree.filter.cycleForward", ["ctrl+o"], "Tree filter: cycle forward");
        Add("app.tree.filter.cycleBackward", ["shift+ctrl+o"], "Tree filter: cycle backward");
        return list;
    }

    private static readonly Dictionary<string, string> NameMigrations = new(StringComparer.Ordinal)
    {
        ["cursorUp"] = "tui.editor.cursorUp", ["cursorDown"] = "tui.editor.cursorDown", ["cursorLeft"] = "tui.editor.cursorLeft",
        ["cursorRight"] = "tui.editor.cursorRight", ["cursorWordLeft"] = "tui.editor.cursorWordLeft", ["cursorWordRight"] = "tui.editor.cursorWordRight",
        ["cursorLineStart"] = "tui.editor.cursorLineStart", ["cursorLineEnd"] = "tui.editor.cursorLineEnd", ["jumpForward"] = "tui.editor.jumpForward",
        ["jumpBackward"] = "tui.editor.jumpBackward", ["pageUp"] = "tui.editor.pageUp", ["pageDown"] = "tui.editor.pageDown",
        ["deleteCharBackward"] = "tui.editor.deleteCharBackward", ["deleteCharForward"] = "tui.editor.deleteCharForward",
        ["deleteWordBackward"] = "tui.editor.deleteWordBackward", ["deleteWordForward"] = "tui.editor.deleteWordForward",
        ["deleteToLineStart"] = "tui.editor.deleteToLineStart", ["deleteToLineEnd"] = "tui.editor.deleteToLineEnd", ["yank"] = "tui.editor.yank",
        ["yankPop"] = "tui.editor.yankPop", ["undo"] = "tui.editor.undo", ["newLine"] = "tui.input.newLine", ["submit"] = "tui.input.submit",
        ["tab"] = "tui.input.tab", ["copy"] = "tui.input.copy", ["selectUp"] = "tui.select.up", ["selectDown"] = "tui.select.down",
        ["selectPageUp"] = "tui.select.pageUp", ["selectPageDown"] = "tui.select.pageDown", ["selectConfirm"] = "tui.select.confirm",
        ["selectCancel"] = "tui.select.cancel", ["interrupt"] = "app.interrupt", ["clear"] = "app.clear", ["exit"] = "app.exit",
        ["suspend"] = "app.suspend", ["cycleThinkingLevel"] = "app.thinking.cycle", ["cycleModelForward"] = "app.model.cycleForward",
        ["cycleModelBackward"] = "app.model.cycleBackward", ["selectModel"] = "app.model.select", ["expandTools"] = "app.tools.expand",
        ["toggleThinking"] = "app.thinking.toggle", ["toggleSessionNamedFilter"] = "app.session.toggleNamedFilter",
        ["externalEditor"] = "app.editor.external", ["followUp"] = "app.message.followUp", ["dequeue"] = "app.message.dequeue",
        ["pasteImage"] = "app.clipboard.pasteImage", ["newSession"] = "app.session.new", ["tree"] = "app.session.tree", ["fork"] = "app.session.fork",
        ["resume"] = "app.session.resume", ["treeFoldOrUp"] = "app.tree.foldOrUp", ["treeUnfoldOrDown"] = "app.tree.unfoldOrDown",
        ["treeEditLabel"] = "app.tree.editLabel", ["treeToggleLabelTimestamp"] = "app.tree.toggleLabelTimestamp",
        ["toggleSessionPath"] = "app.session.togglePath", ["toggleSessionSort"] = "app.session.toggleSort", ["renameSession"] = "app.session.rename",
        ["deleteSession"] = "app.session.delete", ["deleteSessionNoninvasive"] = "app.session.deleteNoninvasive"
    };

    /// <summary>migrateKeybindingsConfig: legacy names renamed (a new name wins over its legacy one), ordered like KEYBINDINGS.</summary>
    public static (JsonObject Config, bool Migrated) MigrateKeybindingsConfig(JsonObject raw)
    {
        var config = new Dictionary<string, JsonNode?>(StringComparer.Ordinal); var migrated = false;
        foreach (var (key, value) in raw)
        {
            var next = NameMigrations.GetValueOrDefault(key, key);
            if (next != key) migrated = true;
            if (key != next && raw.ContainsKey(next)) { migrated = true; continue; }
            config[next] = value?.DeepClone();
        }
        var ordered = new JsonObject();
        foreach (var (id, _) in Definitions()) if (config.Remove(id, out var value)) ordered[id] = value;
        foreach (var key in config.Keys.Order(StringComparer.Ordinal)) ordered[key] = config[key];
        return (ordered, migrated);
    }

    public static Dictionary<string, IReadOnlyList<string>?> LoadFromFile(string path)
    {
        var result = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return result;
            var text = File.ReadAllText(path);
            if (text.StartsWith('﻿')) text = text[1..];
            if (JsonNode.Parse(text) is not JsonObject raw) return result;
            foreach (var (key, value) in MigrateKeybindingsConfig(raw).Config)
            {
                if (value is JsonValue single && single.TryGetValue<string>(out var key1)) result[key] = [key1];
                else if (value is JsonArray array && array.All(entry => entry is JsonValue v && v.TryGetValue<string>(out _)))
                    result[key] = array.Select(entry => entry!.GetValue<string>()).ToList();
            }
        }
        catch { }
        return result;
    }

    /// <summary>KeybindingsManager.create: the app definitions with the user's keybindings.json.</summary>
    public static AppKeybindingsManager Create(string agentDir)
    {
        var path = Path.Join(agentDir, "keybindings.json");
        return new AppKeybindingsManager(new TuiKeybindingsManager(Definitions(), LoadFromFile(path)), path);
    }
}

/// <summary>The app keybindings manager: the TUI manager plus the config path for reload.</summary>
internal sealed class AppKeybindingsManager(TuiKeybindingsManager manager, string? configPath)
{
    public TuiKeybindingsManager Manager { get; } = manager;
    public string? ConfigPath { get; } = configPath;
    public void Reload() { if (ConfigPath is not null) Manager.SetUserBindings(AppKeybindings.LoadFromFile(ConfigPath)); }
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetEffectiveConfig() => Manager.GetResolvedBindings();
    public bool Matches(string data, string id) => Manager.Matches(data, id);
    public IReadOnlyList<string> GetKeys(string id) => Manager.GetKeys(id);
}

internal static class KeybindingHints
{
    private static string FormatKeyPart(string part, bool capitalize)
    {
        var display = OperatingSystem.IsMacOS() && part.Equals("alt", StringComparison.OrdinalIgnoreCase) ? "option" : part;
        return capitalize && display.Length > 0 ? char.ToUpperInvariant(display[0]) + display[1..] : display;
    }

    public static string FormatKeyText(string key, bool capitalize = false) =>
        string.Join("/", key.Split('/').Select(k => string.Join("+", k.Split('+').Select(part => FormatKeyPart(part, capitalize)))));

    private static string FormatKeys(IReadOnlyList<string> keys, bool capitalize = false) => keys.Count == 0 ? "" : FormatKeyText(string.Join("/", keys), capitalize);

    public static string KeyText(string keybinding) => FormatKeys(TuiKeybindingsManager.Global.GetKeys(keybinding));
    public static string KeyDisplayText(string keybinding) => FormatKeys(TuiKeybindingsManager.Global.GetKeys(keybinding), capitalize: true);
    public static string KeyHint(string keybinding, string description) => Themes.Current.Fg("dim", KeyText(keybinding)) + Themes.Current.Fg("muted", " " + description);
    public static string RawKeyHint(string key, string description) => Themes.Current.Fg("dim", FormatKeyText(key)) + Themes.Current.Fg("muted", " " + description);
}
