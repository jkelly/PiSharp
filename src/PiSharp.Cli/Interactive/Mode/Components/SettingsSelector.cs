// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/settings-selector.ts.
// Also ports the inputs it imports: HTTP_IDLE_TIMEOUT_CHOICES and formatHttpIdleTimeoutMs (core/http-dispatcher.ts),
// CACHE_WARMING_MODES and WarningSettings (core/settings-manager.ts) and getSupportedThinkingLevels (ai/src/models.ts).
using System.Globalization;
using System.Text.Json.Nodes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source WarningSettings (settings-manager.ts).</summary>
internal sealed record WarningSettings(bool? AnthropicExtraUsage = null);

/// <summary>
/// Source SettingsConfig. Enum-like settings keep upstream's string values. <see cref="QuietStartup"/> is <c>bool</c> or
/// <c>"header"</c> (as <see cref="InteractiveSettings.QuietStartup"/>); <see cref="FullscreenWheelScrollLines"/> null is <c>"auto"</c>.
/// Models are Pi model JSON objects (provider, id, reasoning, thinkingLevelMap).
/// </summary>
internal sealed class SettingsConfig
{
    public bool AutoCompact { get; init; }
    public string DefaultModel { get; init; } = "not set";
    public JsonObject? CurrentModel { get; init; }
    public IReadOnlyList<JsonObject> AvailableDefaultModels { get; init; } = [];
    public bool ShowImages { get; init; }
    public int ImageWidthCells { get; init; } = 60;
    public bool AutoResizeImages { get; init; }
    public bool BlockImages { get; init; }
    public bool EnableSkillCommands { get; init; }
    /// <summary>"all" | "one-at-a-time".</summary>
    public string SteeringMode { get; init; } = "one-at-a-time";
    /// <summary>"all" | "one-at-a-time".</summary>
    public string FollowUpMode { get; init; } = "one-at-a-time";
    /// <summary>"sse" | "websocket" | "websocket-cached" | "auto".</summary>
    public string Transport { get; init; } = "auto";
    public int HttpIdleTimeoutMs { get; init; } = 300_000;
    /// <summary>"off" | "streaming" | "idle".</summary>
    public string CacheWarmingMode { get; init; } = "streaming";
    public string ThinkingLevel { get; init; } = "medium";
    public IReadOnlyList<string> AvailableThinkingLevels { get; init; } = [];
    /// <summary>"provider/modelId" to thinking level.</summary>
    public IReadOnlyDictionary<string, string> ModelThinkingLevels { get; init; } = new Dictionary<string, string>();
    public string CurrentTheme { get; init; } = "dark";
    /// <summary>"light" | "dark".</summary>
    public string TerminalTheme { get; init; } = "dark";
    public IReadOnlyList<string> AvailableThemes { get; init; } = [];
    public bool HideThinkingBlock { get; init; }
    /// <summary>"off" | "final" | "streaming".</summary>
    public string MermaidRenderingMode { get; init; } = "streaming";
    public bool ShowCacheMissNotices { get; init; }
    public bool CollapseChangelog { get; init; }
    public bool EnableInstallTelemetry { get; init; }
    /// <summary>"fork" | "tree" | "none".</summary>
    public string DoubleEscapeAction { get; init; } = "tree";
    /// <summary>"default" | "no-tools" | "user-only" | "labeled-only" | "all".</summary>
    public string TreeFilterMode { get; init; } = "default";
    public bool ShowHardwareCursor { get; init; }
    public int EditorPaddingX { get; init; }
    /// <summary>0 | 1.</summary>
    public int OutputPad { get; init; } = 1;
    public int AutocompleteMaxVisible { get; init; } = 5;
    /// <summary><c>true</c>, <c>false</c> or <c>"header"</c>.</summary>
    public object QuietStartup { get; init; } = false;
    /// <summary>"ask" | "always" | "never".</summary>
    public string DefaultProjectTrust { get; init; } = "ask";
    public bool ClearOnShrink { get; init; }
    public bool ShowTerminalProgress { get; init; }
    /// <summary>"regular" | "fullscreen".</summary>
    public string TuiMode { get; init; } = "fullscreen";
    /// <summary>"transcript" | "resume-hint".</summary>
    public string FullscreenExitOutput { get; init; } = "transcript";
    /// <summary>"auto" | "always" | "hidden".</summary>
    public string FullscreenScrollbar { get; init; } = "auto";
    public bool FullscreenCopyOnSelect { get; init; }
    /// <summary>Lines per wheel event; null is "auto".</summary>
    public int? FullscreenWheelScrollLines { get; init; }
    public WarningSettings Warnings { get; init; } = new();
}

/// <summary>Source SettingsCallbacks. Every callback defaults to a no-op; <see cref="OnThemePreview"/> is optional upstream.</summary>
internal sealed class SettingsCallbacks
{
    public Action<bool> OnAutoCompactChange { get; init; } = _ => { };
    public Action<bool> OnShowImagesChange { get; init; } = _ => { };
    public Action<int> OnImageWidthCellsChange { get; init; } = _ => { };
    public Action<bool> OnAutoResizeImagesChange { get; init; } = _ => { };
    public Action<bool> OnBlockImagesChange { get; init; } = _ => { };
    public Action<bool> OnEnableSkillCommandsChange { get; init; } = _ => { };
    public Action<string> OnSteeringModeChange { get; init; } = _ => { };
    public Action<string> OnFollowUpModeChange { get; init; } = _ => { };
    public Action<string> OnTransportChange { get; init; } = _ => { };
    public Action<int> OnHttpIdleTimeoutMsChange { get; init; } = _ => { };
    public Action<string> OnCacheWarmingModeChange { get; init; } = _ => { };
    /// <summary>(provider, modelId, level).</summary>
    public Action<string, string, string> OnModelThinkingLevelChange { get; init; } = (_, _, _) => { };
    /// <summary>(provider, modelId).</summary>
    public Action<string, string> OnModelThinkingLevelRemove { get; init; } = (_, _) => { };
    public Action<string> OnThemeChange { get; init; } = _ => { };
    public Action<string>? OnThemePreview { get; init; }
    public Action<bool> OnHideThinkingBlockChange { get; init; } = _ => { };
    public Action<string> OnMermaidRenderingModeChange { get; init; } = _ => { };
    public Action<bool> OnShowCacheMissNoticesChange { get; init; } = _ => { };
    public Action<bool> OnCollapseChangelogChange { get; init; } = _ => { };
    public Action<bool> OnEnableInstallTelemetryChange { get; init; } = _ => { };
    public Action<string> OnDoubleEscapeActionChange { get; init; } = _ => { };
    public Action<string> OnTreeFilterModeChange { get; init; } = _ => { };
    public Action<bool> OnShowHardwareCursorChange { get; init; } = _ => { };
    public Action<int> OnEditorPaddingXChange { get; init; } = _ => { };
    public Action<int> OnOutputPadChange { get; init; } = _ => { };
    public Action<int> OnAutocompleteMaxVisibleChange { get; init; } = _ => { };
    /// <summary>Receives <c>true</c>, <c>false</c> or <c>"header"</c>.</summary>
    public Action<object> OnQuietStartupChange { get; init; } = _ => { };
    public Action<string> OnDefaultProjectTrustChange { get; init; } = _ => { };
    public Action<bool> OnClearOnShrinkChange { get; init; } = _ => { };
    public Action<bool> OnShowTerminalProgressChange { get; init; } = _ => { };
    public Action<string> OnTuiModeChange { get; init; } = _ => { };
    public Action<string> OnFullscreenExitOutputChange { get; init; } = _ => { };
    public Action<string> OnFullscreenScrollbarChange { get; init; } = _ => { };
    public Action<bool> OnFullscreenCopyOnSelectChange { get; init; } = _ => { };
    /// <summary>Receives the line count, or null for "auto".</summary>
    public Action<int?> OnFullscreenWheelScrollLinesChange { get; init; } = _ => { };
    public Action<WarningSettings> OnWarningsChange { get; init; } = _ => { };
    public Action OnCancel { get; init; } = () => { };
}

/// <summary>Source HTTP_IDLE_TIMEOUT_CHOICES and formatHttpIdleTimeoutMs (core/http-dispatcher.ts).</summary>
internal static class HttpIdleTimeoutChoices
{
    public static IReadOnlyList<(string Label, int TimeoutMs)> All { get; } =
        [("30 sec", 30_000), ("1 min", 60_000), ("2 min", 120_000), ("5 min", 300_000), ("disabled", 0)];

    public static string Format(int timeoutMs)
    {
        foreach (var choice in All) if (choice.TimeoutMs == timeoutMs) return choice.Label;
        return (timeoutMs / 1000.0).ToString(CultureInfo.InvariantCulture) + " sec";
    }
}

/// <summary>A submenu component for the individual warning toggles.</summary>
file sealed class WarningSettingsSubmenu : Container, IInputHandler
{
    private readonly SettingsList settingsList;
    private WarningSettings state;

    public WarningSettingsSubmenu(WarningSettings warnings, Action<WarningSettings> onChange, Action onCancel)
    {
        state = warnings with { };

        List<SettingItem> items =
        [
            new()
            {
                Id = "anthropic-extra-usage",
                Label = "Anthropic extra usage",
                Description = "Warn when Anthropic subscription auth may use paid extra usage",
                CurrentValue = (state.AnthropicExtraUsage ?? true) ? "true" : "false",
                Values = ["true", "false"],
            },
        ];

        settingsList = new SettingsList(items, Math.Min(items.Count, 10), Themes.GetSettingsListTheme(), (id, newValue) =>
        {
            switch (id)
            {
                case "anthropic-extra-usage":
                    state = state with { AnthropicExtraUsage = newValue == "true" };
                    onChange(state with { });
                    break;
            }
        }, onCancel);

        AddChild(settingsList);
    }

    public void HandleInput(string data) => settingsList.HandleInput(data);
}

file sealed class ThemeSubmenu : Container, IInputHandler
{
    private const string AutomaticThemeValue = "/";

    private IComponent? inputComponent;
    private readonly SettingsCallbacks callbacks;
    private readonly IReadOnlyList<string> availableThemes;
    private readonly string terminalTheme;
    private readonly Action<string?> onDone;
    private readonly string originalThemeSetting;
    private string mode;
    private string singleTheme;
    private string lightTheme;
    private string darkTheme;

    public ThemeSubmenu(string currentThemeSetting, string terminalTheme, IReadOnlyList<string> availableThemes, SettingsCallbacks callbacks,
        Action<string?> onDone)
    {
        this.callbacks = callbacks;
        this.availableThemes = availableThemes;
        this.terminalTheme = terminalTheme;
        this.onDone = onDone;
        originalThemeSetting = currentThemeSetting;
        var autoTheme = Themes.ParseAutoThemeSetting(currentThemeSetting);
        var automaticThemes = DefaultAutomaticThemes(currentThemeSetting, availableThemes);
        var fixedTheme = autoTheme is not null || currentThemeSetting.Contains('/') ? null : currentThemeSetting;
        mode = autoTheme is not null ? "automatic" : "single";
        lightTheme = automaticThemes.LightTheme;
        darkTheme = automaticThemes.DarkTheme;
        singleTheme = PreferredTheme(availableThemes, fixedTheme ?? (autoTheme is not null ? GetActiveAutomaticTheme() : null), Themes.SystemThemeName);

        if (mode == "automatic") ShowAutomaticMenu();
        else ShowSingleMenu();
    }

    public void HandleInput(string data) => (inputComponent as IInputHandler)?.HandleInput(data);

    private static List<SelectItem> ThemeItems(IReadOnlyList<string> availableThemes, string currentTheme) =>
        [.. availableThemes.Select(name => new SelectItem(name, $"{(name == currentTheme ? "✓ " : "  ")}{name}",
            name == Themes.SystemThemeName ? "Theme created from your terminal's colors" : null))];

    /// <summary>The system theme comes first, then automatic mode, then the remaining themes.</summary>
    private static List<SelectItem> SingleModeThemeItems(IReadOnlyList<string> availableThemes, string currentTheme)
    {
        var items = ThemeItems(availableThemes, currentTheme);
        var systemIndex = items.FindIndex(item => item.Value == Themes.SystemThemeName);
        List<SelectItem> system = [];
        if (systemIndex != -1) { system.Add(items[systemIndex]); items.RemoveAt(systemIndex); }
        return [.. system, new SelectItem(AutomaticThemeValue, "  automatic", "Use separate themes for light and dark terminal appearance"), .. items];
    }

    private static string PreferredTheme(IReadOnlyList<string> availableThemes, string? preferred, string fallback)
    {
        if (!string.IsNullOrEmpty(preferred) && availableThemes.Contains(preferred)) return preferred;
        if (availableThemes.Contains(fallback)) return fallback;
        return availableThemes.Count > 0 ? availableThemes[0] : fallback;
    }

    private static (string LightTheme, string DarkTheme) DefaultAutomaticThemes(string currentThemeSetting, IReadOnlyList<string> availableThemes)
    {
        if (Themes.ParseAutoThemeSetting(currentThemeSetting) is { } autoTheme) return autoTheme;

        var currentFixedTheme = currentThemeSetting.Contains('/') ? null : currentThemeSetting;
        var themeName = PreferredTheme(availableThemes, currentFixedTheme, Themes.SystemThemeName);
        return (themeName, themeName);
    }

    private void SetContent(IComponent renderComponent, IComponent? inputComponent = null)
    {
        Clear();
        AddChild(renderComponent);
        this.inputComponent = inputComponent ?? renderComponent;
    }

    private void ShowSingleMenu()
    {
        mode = "single";
        var menu = new SelectSubmenu(
            "Theme",
            "Select a theme, or choose automatic to follow terminal appearance.",
            SingleModeThemeItems(availableThemes, singleTheme),
            singleTheme,
            value =>
            {
                if (value == AutomaticThemeValue)
                {
                    mode = "automatic";
                    callbacks.OnThemePreview?.Invoke(GetThemeSetting());
                    ShowAutomaticMenu();
                    return;
                }

                singleTheme = value;
                Apply(value);
            },
            Cancel,
            value => callbacks.OnThemePreview?.Invoke(value == AutomaticThemeValue ? GetAutomaticThemeSetting() : value));
        SetContent(menu);
    }

    private void ShowAutomaticMenu()
    {
        mode = "automatic";
        var content = new Container();
        content.AddChild(new Text(theme.Bold(theme.Fg("accent", "Automatic Theme")), 0, 0));
        content.AddChild(new Spacer(1));
        content.AddChild(new Text(theme.Fg("muted", "Choose themes for terminal light and dark appearance."), 0, 0));
        content.AddChild(new Text(theme.Fg("muted", "Light/dark detection requires terminal support."), 0, 0));
        content.AddChild(new Spacer(1));

        List<SettingItem> items =
        [
            new()
            {
                Id = "light-theme",
                Label = "Light theme",
                Description = "Theme to use in automatic mode when the terminal is light",
                CurrentValue = lightTheme,
                Submenu = (currentValue, done) => CreateThemeSelect("Light Theme", "Select the theme to use for light terminal appearance",
                    currentValue, done, value =>
                    {
                        lightTheme = value;
                        callbacks.OnThemePreview?.Invoke(GetThemeSetting());
                        done(value, null);
                    }),
            },
            new()
            {
                Id = "dark-theme",
                Label = "Dark theme",
                Description = "Theme to use in automatic mode when the terminal is dark",
                CurrentValue = darkTheme,
                Submenu = (currentValue, done) => CreateThemeSelect("Dark Theme", "Select the theme to use for dark terminal appearance",
                    currentValue, done, value =>
                    {
                        darkTheme = value;
                        callbacks.OnThemePreview?.Invoke(GetThemeSetting());
                        done(value, null);
                    }),
            },
            new()
            {
                Id = "apply",
                Label = "Apply",
                Description = "Save and go back",
                CurrentValue = "save and go back",
                Values = ["save and go back"],
            },
            new()
            {
                Id = "single-mode",
                Label = "Change mode",
                Description = "Switch to one theme for light and dark",
                CurrentValue = "switch to single theme",
                Values = ["switch to single theme"],
            },
        ];

        var settingsList = new SettingsList(items, Math.Min(items.Count, 10), Themes.GetSettingsListTheme(), (id, _) =>
        {
            switch (id)
            {
                case "single-mode":
                    mode = "single";
                    singleTheme = GetActiveAutomaticTheme();
                    callbacks.OnThemePreview?.Invoke(singleTheme);
                    ShowSingleMenu();
                    break;
                case "apply":
                    Apply(GetAutomaticThemeSetting());
                    break;
            }
        }, Cancel);
        content.AddChild(settingsList);
        SetContent(content, settingsList);
    }

    private SelectSubmenu CreateThemeSelect(string title, string description, string currentValue, Action<string?, string?> done, Action<string> onSelect) =>
        new(title, description, ThemeItems(availableThemes, currentValue), currentValue, onSelect, () =>
        {
            callbacks.OnThemePreview?.Invoke(GetThemeSetting());
            done(null, null);
        }, value => callbacks.OnThemePreview?.Invoke(value));

    private string GetThemeSetting() => mode == "automatic" ? GetAutomaticThemeSetting() : singleTheme;
    private string GetActiveAutomaticTheme() => terminalTheme == "light" ? lightTheme : darkTheme;
    private string GetAutomaticThemeSetting() => $"{lightTheme}/{darkTheme}";
    private void Apply(string themeSetting) => onDone(themeSetting);

    private void Cancel()
    {
        callbacks.OnThemePreview?.Invoke(originalThemeSetting);
        onDone(null);
    }
}


/// <summary>Main settings selector component.</summary>
internal sealed class SettingsSelectorComponent : Container
{
    private static readonly SelectListLayoutOptions ModelPickerLayout = new(MinPrimaryColumnWidth: 12, MaxPrimaryColumnWidth: 46);
    private const string ClearOverrideValue = "__clear__";
    private static readonly string[] CacheWarmingModes = ["off", "streaming", "idle"];
    private static readonly string[] ExtendedThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    private static readonly Dictionary<string, string> ThinkingDescriptions = new(StringComparer.Ordinal)
    {
        ["off"] = "No reasoning",
        ["minimal"] = "Very brief reasoning (~1k tokens)",
        ["low"] = "Light reasoning (~2k tokens)",
        ["medium"] = "Moderate reasoning (~8k tokens)",
        ["high"] = "Deep reasoning (~16k tokens)",
        ["xhigh"] = "Extra-high reasoning (~32k tokens)",
        ["max"] = "Maximum reasoning",
    };

    private static readonly (string Value, string Label)[] DefaultProjectTrustLabels = [("ask", "Ask"), ("always", "Always trust"), ("never", "Never trust")];

    private readonly SettingsList settingsList;

    private static string ModelProvider(JsonObject model) => model["provider"]?.GetValue<string>() ?? "";
    private static string ModelId(JsonObject model) => model["id"]?.GetValue<string>() ?? "";
    private static bool ModelReasoning(JsonObject model) => model["reasoning"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static string ModelSettingKey(JsonObject model) => $"{ModelProvider(model)}/{ModelId(model)}";
    private static string ModelDisplayLabel(JsonObject model) => $"{ModelId(model)} [{ModelProvider(model)}]";
    private static string ModelItemLabel(JsonObject model) => $"{ModelId(model)} {theme.Fg("muted", $"[{ModelProvider(model)}]")}";

    private static string ModelThinkingOverridesSummary(IReadOnlyDictionary<string, string> overrides) =>
        overrides.Count == 0 ? "none" : $"{overrides.Count.ToString(CultureInfo.InvariantCulture)} configured";

    /// <summary>Source getSupportedThinkingLevels (ai/src/models.ts).</summary>
    internal static List<string> GetSupportedThinkingLevels(JsonObject model)
    {
        if (!ModelReasoning(model)) return ["off"];
        var map = model["thinkingLevelMap"] as JsonObject;
        return [.. ExtendedThinkingLevels.Where(level =>
        {
            JsonNode? mapped = null;
            var present = map is not null && map.TryGetPropertyValue(level, out mapped);
            if (present && mapped is null) return false;
            if (level is "xhigh" or "max") return present;
            return true;
        })];
    }

    public SettingsSelectorComponent(SettingsConfig config, SettingsCallbacks callbacks)
    {
        var supportsImages = TerminalImage.GetCapabilities().Images != ImageProtocol.None;
        var followUpKey = KeybindingHints.KeyDisplayText("app.message.followUp");
        var cycleThinkingKey = KeybindingHints.KeyDisplayText("app.thinking.cycle");
        var currentWarnings = config.Warnings with { };
        var currentModelThinkingLevels = new Dictionary<string, string>(config.ModelThinkingLevels, StringComparer.Ordinal);
        var defaultModelByValue = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var model in config.AvailableDefaultModels) defaultModelByValue[ModelSettingKey(model)] = model;
        var currentDefaultModelKey = defaultModelByValue.ContainsKey(config.DefaultModel) ? config.DefaultModel : null;
        var currentModelKey = config.CurrentModel is { } current ? ModelSettingKey(current) : null;

        var wheelValues = new List<string> { "auto" };
        wheelValues.AddRange(new int?[] { 1, 2, 3, 5, 10, config.FullscreenWheelScrollLines }.Distinct().OfType<int>().Order()
            .Select(lines => lines.ToString(CultureInfo.InvariantCulture)));

        List<SettingItem> items =
        [
            new()
            {
                Id = "autocompact",
                Label = "Auto-compact",
                Description = "Automatically compact context when it gets too large",
                CurrentValue = config.AutoCompact ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "steering-mode",
                Label = "Steering mode",
                Description = "Enter while streaming queues steering messages. 'one-at-a-time': deliver one, wait for response. 'all': deliver all at once.",
                CurrentValue = config.SteeringMode,
                Values = ["one-at-a-time", "all"],
            },
            new()
            {
                Id = "follow-up-mode",
                Label = "Follow-up mode",
                Description = $"{followUpKey} queues follow-up messages until agent stops. 'one-at-a-time': deliver one, wait for response. 'all': deliver all at once.",
                CurrentValue = config.FollowUpMode,
                Values = ["one-at-a-time", "all"],
            },
            new()
            {
                Id = "transport",
                Label = "Transport",
                Description = "Preferred transport for providers that support multiple transports",
                CurrentValue = config.Transport,
                Values = ["sse", "websocket", "websocket-cached", "auto"],
            },
            new()
            {
                Id = "http-idle-timeout",
                Label = "HTTP idle timeout",
                Description = "Maximum idle gap while waiting for HTTP headers or body chunks. Disable for local models that pause longer than five minutes.",
                CurrentValue = HttpIdleTimeoutChoices.Format(config.HttpIdleTimeoutMs),
                Values = [.. HttpIdleTimeoutChoices.All.Select(choice => choice.Label)],
            },
            new()
            {
                Id = "cache-warming-mode",
                Label = "Cache warming",
                Description = "off; streaming while the agent runs; idle also between runs while continuation stays profitable",
                CurrentValue = config.CacheWarmingMode,
                Values = [.. CacheWarmingModes],
            },
            new()
            {
                Id = "hide-thinking",
                Label = "Hide thinking",
                Description = "Hide thinking blocks in assistant responses",
                CurrentValue = config.HideThinkingBlock ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "mermaid-rendering",
                Label = "Mermaid diagrams",
                Description = "Render Mermaid code blocks as Unicode diagrams",
                CurrentValue = config.MermaidRenderingMode,
                Values = ["off", "final", "streaming"],
            },
            new()
            {
                Id = "cache-miss-notices",
                Label = "Cache miss notices",
                Description = "Show transcript notices for cache costs and provider recovery diagnostics",
                CurrentValue = config.ShowCacheMissNotices ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "collapse-changelog",
                Label = "Collapse changelog",
                Description = "Show condensed changelog after updates",
                CurrentValue = config.CollapseChangelog ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "quiet-startup",
                Label = "Quiet startup",
                Description = "Disable verbose printing at startup (header: keep only the startup header)",
                CurrentValue = config.QuietStartup is bool quiet ? (quiet ? "true" : "false") : config.QuietStartup.ToString() ?? "false",
                Values = ["true", "header", "false"],
            },
            new()
            {
                Id = "install-telemetry",
                Label = "Install telemetry",
                Description = "Send an anonymous version/update ping after changelog-detected updates",
                CurrentValue = config.EnableInstallTelemetry ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "default-project-trust",
                Label = "Default project trust",
                Description = "Fallback behavior when no extension or saved trust decision decides project trust",
                CurrentValue = DefaultProjectTrustLabels.FirstOrDefault(entry => entry.Value == config.DefaultProjectTrust).Label ?? "",
                Values = [.. DefaultProjectTrustLabels.Select(entry => entry.Label)],
            },
            new()
            {
                Id = "double-escape-action",
                Label = "Double-escape action",
                Description = "Action when pressing Escape twice with empty editor",
                CurrentValue = config.DoubleEscapeAction,
                Values = ["tree", "fork", "none"],
            },
            new()
            {
                Id = "tree-filter-mode",
                Label = "Tree filter mode",
                Description = "Default filter when opening /tree",
                CurrentValue = config.TreeFilterMode,
                Values = ["default", "no-tools", "user-only", "labeled-only", "all"],
            },
            new()
            {
                Id = "warnings",
                Label = "Warnings",
                Description = "Enable or disable individual warnings",
                CurrentValue = "configure",
                Submenu = (_, done) => new WarningSettingsSubmenu(currentWarnings, warnings =>
                {
                    currentWarnings = warnings;
                    callbacks.OnWarningsChange(warnings);
                }, () => done(null, null)),
            },
            new()
            {
                Id = "model-thinking",
                Label = "Default thinking level per model",
                Description = $"Override the default thinking level for specific models. {cycleThinkingKey} cycles in-session.",
                CurrentValue = ModelThinkingOverridesSummary(currentModelThinkingLevels),
                Submenu = (_, done) =>
                {
                    List<SteppedSubmenuStep> steps =
                    [
                        new()
                        {
                            Key = "model",
                            Title = SteppedSubmenuStep.Fixed("Per-Model Thinking Level"),
                            Description = SteppedSubmenuStep.Fixed("Select a model to configure"),
                            Options = _ =>
                            {
                                // Array.prototype.sort is stable; OrderBy keeps that.
                                var sorted = config.AvailableDefaultModels.OrderBy(model => model, Comparer<JsonObject>.Create((a, b) =>
                                {
                                    var aKey = ModelSettingKey(a);
                                    var bKey = ModelSettingKey(b);
                                    if (aKey == currentModelKey) return -1;
                                    if (bKey == currentModelKey) return 1;
                                    if (aKey == currentDefaultModelKey) return -1;
                                    if (bKey == currentDefaultModelKey) return 1;
                                    return string.Compare(ModelProvider(a), ModelProvider(b), CultureInfo.InvariantCulture, CompareOptions.None);
                                })).ToList();
                                var modelItems = sorted.Select(model =>
                                {
                                    var key = ModelSettingKey(model);
                                    currentModelThinkingLevels.TryGetValue(key, out var @override);
                                    return new SelectItem(key, ModelItemLabel(model), @override);
                                }).ToList();
                                if (modelItems.Count == 0)
                                    modelItems.Add(new SelectItem("__none__", "No models available", "Log in to a provider or configure an API key first"));
                                return modelItems;
                            },
                            Preselect = _ => currentModelKey ?? currentDefaultModelKey,
                            Searchable = true,
                            Layout = ModelPickerLayout,
                        },
                        new()
                        {
                            Key = "level",
                            Title = ctx =>
                            {
                                var m = defaultModelByValue.GetValueOrDefault(ctx["model"]);
                                return $"Thinking Level for {(m is not null ? ModelDisplayLabel(m) : ctx["model"])}";
                            },
                            Description = SteppedSubmenuStep.Fixed("Select default thinking level for this model"),
                            Options = ctx =>
                            {
                                if (defaultModelByValue.GetValueOrDefault(ctx["model"]) is not { } model) return [];
                                var levels = ModelReasoning(model) ? GetSupportedThinkingLevels(model) : ["off"];
                                currentModelThinkingLevels.TryGetValue(ctx["model"], out var activeLevel);
                                var levelItems = levels.Select(level => new SelectItem(level, $"{(level == activeLevel ? "✓ " : "  ")}{level}",
                                    ThinkingDescriptions.GetValueOrDefault(level))).ToList();
                                if (currentModelThinkingLevels.ContainsKey(ctx["model"]))
                                    levelItems.Add(new SelectItem(ClearOverrideValue, "  (clear override)", $"Revert to global default ({config.ThinkingLevel})"));
                                return levelItems;
                            },
                            Preselect = ctx => currentModelThinkingLevels.GetValueOrDefault(ctx["model"]),
                        },
                    ];

                    string Summary() => ModelThinkingOverridesSummary(currentModelThinkingLevels);

                    return new SteppedSubmenu(steps, selections =>
                    {
                        if (defaultModelByValue.GetValueOrDefault(selections["model"]) is not { } model) return;
                        if (selections["level"] == ClearOverrideValue)
                        {
                            callbacks.OnModelThinkingLevelRemove(ModelProvider(model), ModelId(model));
                            currentModelThinkingLevels.Remove(selections["model"]);
                        }
                        else
                        {
                            callbacks.OnModelThinkingLevelChange(ModelProvider(model), ModelId(model), selections["level"]);
                            currentModelThinkingLevels[selections["model"]] = selections["level"];
                        }
                    }, () => done(Summary(), null), new SteppedSubmenuOptions { Loop = true });
                },
            },
            new()
            {
                Id = "tui-mode",
                Label = "TUI mode",
                Description = "Interface layout; regular mode uses the terminal's normal scrollback",
                CurrentValue = config.TuiMode,
                Values = ["regular", "fullscreen"],
            },
            new()
            {
                Id = "fullscreen-exit-output",
                Label = "Fullscreen exit output",
                Description = "Print the transcript or only a session resume hint when exiting fullscreen mode",
                CurrentValue = config.FullscreenExitOutput,
                Values = ["transcript", "resume-hint"],
            },
            new()
            {
                Id = "fullscreen-scrollbar",
                Label = "Fullscreen scrollbar",
                Description = "Scrollbar behavior in fullscreen mode; has no effect in regular mode",
                CurrentValue = config.FullscreenScrollbar,
                Values = ["auto", "always", "hidden"],
            },
            new()
            {
                Id = "fullscreen-copy-on-select",
                Label = "Fullscreen copy on select",
                Description = "Automatically copy selected text in fullscreen mode; disable to copy selections with Ctrl+X",
                CurrentValue = config.FullscreenCopyOnSelect ? "true" : "false",
                Values = ["true", "false"],
            },
            new()
            {
                Id = "fullscreen-wheel-scroll-lines",
                Label = "Fullscreen wheel scrolling",
                Description = "Lines per mouse-wheel event in fullscreen mode; 'auto' speeds up fast wheel spins where the terminal does not",
                CurrentValue = config.FullscreenWheelScrollLines is { } lines ? lines.ToString(CultureInfo.InvariantCulture) : "auto",
                Values = wheelValues,
            },
            new()
            {
                Id = "theme",
                Label = "Theme",
                Description = "Color theme for the interface",
                CurrentValue = config.CurrentTheme,
                Submenu = (currentValue, done) => new ThemeSubmenu(currentValue, config.TerminalTheme, config.AvailableThemes, callbacks,
                    selected => done(selected, null)),
            },
        ];

        // Only show image toggle if terminal supports it
        if (supportsImages)
        {
            // Insert after autocompact
            items.Insert(1, new()
            {
                Id = "show-images",
                Label = "Show images",
                Description = "Render images inline in terminal",
                CurrentValue = config.ShowImages ? "true" : "false",
                Values = ["true", "false"],
            });
            items.Insert(2, new()
            {
                Id = "image-width-cells",
                Label = "Image width",
                Description = "Preferred inline image width in terminal cells",
                CurrentValue = config.ImageWidthCells.ToString(CultureInfo.InvariantCulture),
                Values = ["60", "80", "120"],
            });
        }

        // Image auto-resize toggle (always available, affects both attached and read images)
        items.Insert(supportsImages ? 3 : 1, new()
        {
            Id = "auto-resize-images",
            Label = "Auto-resize images",
            Description = "Resize large images to 2000x2000 max for better model compatibility",
            CurrentValue = config.AutoResizeImages ? "true" : "false",
            Values = ["true", "false"],
        });

        void InsertAfter(string id, SettingItem item) => items.Insert(items.FindIndex(existing => existing.Id == id) + 1, item);

        // Block images toggle (always available, insert after auto-resize-images)
        InsertAfter("auto-resize-images", new()
        {
            Id = "block-images",
            Label = "Block images",
            Description = "Prevent images from being sent to LLM providers",
            CurrentValue = config.BlockImages ? "true" : "false",
            Values = ["true", "false"],
        });

        // Skill commands toggle (insert after block-images)
        InsertAfter("block-images", new()
        {
            Id = "skill-commands",
            Label = "Skill commands",
            Description = "Register skills as /skill:name commands",
            CurrentValue = config.EnableSkillCommands ? "true" : "false",
            Values = ["true", "false"],
        });

        // Hardware cursor toggle (insert after skill-commands)
        InsertAfter("skill-commands", new()
        {
            Id = "show-hardware-cursor",
            Label = "Show hardware cursor",
            Description = "Show the terminal cursor while still positioning it for IME support",
            CurrentValue = config.ShowHardwareCursor ? "true" : "false",
            Values = ["true", "false"],
        });

        // Editor padding toggle (insert after show-hardware-cursor)
        InsertAfter("show-hardware-cursor", new()
        {
            Id = "editor-padding",
            Label = "Editor padding",
            Description = "Horizontal padding for input editor (0-3)",
            CurrentValue = config.EditorPaddingX.ToString(CultureInfo.InvariantCulture),
            Values = ["0", "1", "2", "3"],
        });

        // Output padding toggle (insert after editor-padding)
        InsertAfter("editor-padding", new()
        {
            Id = "output-padding",
            Label = "Output padding",
            Description = "Horizontal padding for messages, tool output, and command output",
            CurrentValue = config.OutputPad.ToString(CultureInfo.InvariantCulture),
            Values = ["0", "1"],
        });

        // Autocomplete max visible toggle (insert after output-padding)
        InsertAfter("output-padding", new()
        {
            Id = "autocomplete-max-visible",
            Label = "Autocomplete max items",
            Description = "Max visible items in autocomplete dropdown (3-20)",
            CurrentValue = config.AutocompleteMaxVisible.ToString(CultureInfo.InvariantCulture),
            Values = ["3", "5", "7", "10", "15", "20"],
        });

        // Clear on shrink toggle (insert after autocomplete-max-visible)
        InsertAfter("autocomplete-max-visible", new()
        {
            Id = "clear-on-shrink",
            Label = "Clear on shrink",
            Description = "Clear empty rows when content shrinks (may cause flicker)",
            CurrentValue = config.ClearOnShrink ? "true" : "false",
            Values = ["true", "false"],
        });

        // Terminal progress toggle (insert after clear-on-shrink)
        InsertAfter("clear-on-shrink", new()
        {
            Id = "terminal-progress",
            Label = "Terminal progress",
            Description = "Show OSC 9;4 progress indicators in the terminal tab bar",
            CurrentValue = config.ShowTerminalProgress ? "true" : "false",
            Values = ["true", "false"],
        });

        // Add borders
        AddChild(new DynamicBorder());

        settingsList = new SettingsList(items, 10, Themes.GetSettingsListTheme(), (id, newValue) =>
        {
            switch (id)
            {
                case "autocompact": callbacks.OnAutoCompactChange(newValue == "true"); break;
                case "show-images": callbacks.OnShowImagesChange(newValue == "true"); break;
                case "image-width-cells": callbacks.OnImageWidthCellsChange(int.Parse(newValue, CultureInfo.InvariantCulture)); break;
                case "auto-resize-images": callbacks.OnAutoResizeImagesChange(newValue == "true"); break;
                case "block-images": callbacks.OnBlockImagesChange(newValue == "true"); break;
                case "skill-commands": callbacks.OnEnableSkillCommandsChange(newValue == "true"); break;
                case "steering-mode": callbacks.OnSteeringModeChange(newValue); break;
                case "follow-up-mode": callbacks.OnFollowUpModeChange(newValue); break;
                case "transport": callbacks.OnTransportChange(newValue); break;
                case "http-idle-timeout":
                    foreach (var choice in HttpIdleTimeoutChoices.All)
                        if (choice.Label == newValue) { callbacks.OnHttpIdleTimeoutMsChange(choice.TimeoutMs); break; }
                    break;
                case "cache-warming-mode": callbacks.OnCacheWarmingModeChange(newValue); break;
                case "hide-thinking": callbacks.OnHideThinkingBlockChange(newValue == "true"); break;
                case "mermaid-rendering": callbacks.OnMermaidRenderingModeChange(newValue); break;
                case "cache-miss-notices": callbacks.OnShowCacheMissNoticesChange(newValue == "true"); break;
                case "collapse-changelog": callbacks.OnCollapseChangelogChange(newValue == "true"); break;
                case "quiet-startup": callbacks.OnQuietStartupChange(newValue == "header" ? "header" : newValue == "true"); break;
                case "install-telemetry": callbacks.OnEnableInstallTelemetryChange(newValue == "true"); break;
                case "default-project-trust":
                    foreach (var entry in DefaultProjectTrustLabels)
                        if (entry.Label == newValue) { callbacks.OnDefaultProjectTrustChange(entry.Value); break; }
                    break;
                case "double-escape-action": callbacks.OnDoubleEscapeActionChange(newValue); break;
                case "tree-filter-mode": callbacks.OnTreeFilterModeChange(newValue); break;
                case "show-hardware-cursor": callbacks.OnShowHardwareCursorChange(newValue == "true"); break;
                case "editor-padding": callbacks.OnEditorPaddingXChange(int.Parse(newValue, CultureInfo.InvariantCulture)); break;
                case "output-padding": callbacks.OnOutputPadChange(newValue == "0" ? 0 : 1); break;
                case "autocomplete-max-visible": callbacks.OnAutocompleteMaxVisibleChange(int.Parse(newValue, CultureInfo.InvariantCulture)); break;
                case "clear-on-shrink": callbacks.OnClearOnShrinkChange(newValue == "true"); break;
                case "terminal-progress": callbacks.OnShowTerminalProgressChange(newValue == "true"); break;
                case "tui-mode": callbacks.OnTuiModeChange(newValue); break;
                case "fullscreen-exit-output": callbacks.OnFullscreenExitOutputChange(newValue); break;
                case "fullscreen-scrollbar": callbacks.OnFullscreenScrollbarChange(newValue); break;
                case "fullscreen-copy-on-select": callbacks.OnFullscreenCopyOnSelectChange(newValue == "true"); break;
                case "fullscreen-wheel-scroll-lines":
                    callbacks.OnFullscreenWheelScrollLinesChange(newValue == "auto" ? null : int.Parse(newValue, CultureInfo.InvariantCulture));
                    break;
                case "theme": callbacks.OnThemeChange(newValue); break;
            }
        }, callbacks.OnCancel, enableSearch: true);

        AddChild(settingsList);
        AddChild(new DynamicBorder());
    }

    public SettingsList GetSettingsList() => settingsList;
}
