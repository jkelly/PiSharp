// Decision 0011 (run W3, unit AP): the preferences of Settings › Appearance, which belong to the browser, not to the app: they live in this
// browser's localStorage and never reach the server, so two browsers (or a private window) can look different while sharing every chat.
// wwwroot/js/appearance.js reads them in <head> before the first paint (no flash of the wrong theme) and sets each one as an attribute of
// <html> that wwwroot/css/appearance.css reads; the top bar's moon and Ctrl+Shift+L change the same theme choice. The theme is kept under
// Web.Common's theme key ("pisharp-theme": dark, light or system), the rest as one JSON object ("pisharp.appearance"). This catalogue is
// the one list the page renders its controls from; AppearanceCases checks that the script and the stylesheet know every key, value and
// attribute. Only what the app honours is offered: "Render markdown in user messages" is left out because the chat renders a user's text
// as text only (nothing in the browser could render it safely).
namespace PiSharp.WebAgent.Appearance;

/// <summary>How a preference is edited.</summary>
public enum AppearanceControl
{
    /// <summary>A row of buttons, one per choice (font size, density).</summary>
    Buttons,

    /// <summary>A dropdown (code theme).</summary>
    Select,

    /// <summary>An on/off switch.</summary>
    Switch,
}

/// <summary>One value of a preference.</summary>
/// <param name="Value">The stored value, and the value of its <c>&lt;html&gt;</c> attribute.</param>
/// <param name="Label">The text shown.</param>
public sealed record AppearanceChoice(string Value, string Label);

/// <summary>A preference of Settings › Appearance.</summary>
/// <param name="Key">The key in the stored object (<see cref="AppearancePrefs.StorageKey"/>), such as <c>fontSize</c>; also the form field's name.</param>
/// <param name="Section">The page section: <c>display</c> or <c>messages</c>.</param>
/// <param name="Label">The setting's name.</param>
/// <param name="Description">What it does.</param>
/// <param name="Control">How it is edited.</param>
/// <param name="Attribute">The <c>&lt;html&gt;</c> attribute appearance.js sets, such as <c>data-font-size</c>.</param>
/// <param name="Default">The default value (<c>on</c> or <c>off</c> for a switch; <c>system</c> for a switch that follows the system).</param>
/// <param name="Choices">The values of a <see cref="AppearanceControl.Buttons"/> or <see cref="AppearanceControl.Select"/> preference; a switch
/// has <c>on</c> and <c>off</c>.</param>
public sealed record AppearancePref(string Key, string Section, string Label, string Description, AppearanceControl Control, string Attribute,
    string Default, IReadOnlyList<AppearanceChoice> Choices)
{
    /// <summary>The label of <paramref name="value"/>, or the value itself.</summary>
    public string LabelOf(string value) => Choices.FirstOrDefault(choice => choice.Value == value)?.Label ?? value;

    /// <summary>The page's note under the description: where the value is kept.</summary>
    public string KeyNote => AppearancePrefs.StorageKey + "." + Key + " · this browser";
}

/// <summary>Every preference of Settings › Appearance, in page order.</summary>
public static class AppearancePrefs
{
    /// <summary>The localStorage key of the theme choice (<c>dark</c>, <c>light</c> or <c>system</c>): Web.Common's theme key, which
    /// theme-boot.js and theme.js also read.</summary>
    public const string ThemeKey = "pisharp-theme";

    /// <summary>The localStorage key of every other preference: one JSON object of <see cref="AppearancePref.Key"/> to value.</summary>
    public const string StorageKey = "pisharp.appearance";

    /// <summary>The <c>&lt;html&gt;</c> attribute with the theme choice (<c>data-bs-theme</c> holds what it resolves to).</summary>
    public const string ThemeAttribute = "data-theme-choice";

    /// <summary>The event appearance.js dispatches on <c>document</c> after it applied a change (the theme, a save, another tab, the system).</summary>
    public const string ChangeEvent = "pisharp:appearance";

    /// <summary>The theme choices, as the page's cards show them.</summary>
    public static IReadOnlyList<AppearanceChoice> Themes { get; } =
    [
        new("dark", "Dark"),
        new("light", "Light"),
        new("system", "System"),
    ];

    /// <summary>The default theme.</summary>
    public const string DefaultTheme = "dark";

    private static readonly AppearanceChoice[] OnOff = [new("on", "On"), new("off", "Off")];

    /// <summary>The preferences after the theme, in page order (Display, then Messages).</summary>
    public static IReadOnlyList<AppearancePref> All { get; } =
    [
        new("fontSize", "display", "UI font size", "Scales the whole interface: 14, 15 or 17 px base.", AppearanceControl.Buttons, "data-font-size",
            "default", [new("small", "Small"), new("default", "Default"), new("large", "Large")]),
        new("density", "display", "Message density", "Compact tightens the space between messages, thinking blocks and tool cards in chats.",
            AppearanceControl.Buttons, "data-density", "comfortable", [new("comfortable", "Comfortable"), new("compact", "Compact")]),
        new("codeTheme", "display", "Code theme", "Colours of code blocks, tool output and JSON. Default follows the theme (GitHub dark or light).",
            AppearanceControl.Select, "data-code-theme", "default", [new("default", "Default"), new("github", "GitHub light"), new("solarized", "Solarized dark")]),
        new("codeLigatures", "display", "Code font ligatures",
            "Draw =>, != and >= as single glyphs when the system's monospace font has ligatures.", AppearanceControl.Switch, "data-code-ligatures", "off", OnOff),
        new("expandThinking", "messages", "Expand thinking blocks by default",
            "Show the model's reasoning text open. You can still close one; it stays closed while the page is open.", AppearanceControl.Switch,
            "data-expand-thinking", "off", OnOff),
        new("showUsage", "messages", "Show per-message usage footer",
            "Input, output and cached tokens, cost, duration and speed under each reply. The reply's buttons stay.", AppearanceControl.Switch,
            "data-usage-footer", "on", OnOff),
        new("expandToolCalls", "messages", "Show tool calls expanded",
            "Open each tool card with its arguments and result. Errors and running commands always open.", AppearanceControl.Switch,
            "data-expand-tools", "off", OnOff),
        new("reduceMotion", "messages", "Reduce motion",
            "Stops the streaming cursor, pulsing status dots and animated transitions. Follows the system (Windows: animation effects off) until you change it.",
            AppearanceControl.Switch, "data-reduce-motion", "system", OnOff),
    ];

    /// <summary>The preferences of <paramref name="section"/> (<c>display</c> or <c>messages</c>).</summary>
    public static IEnumerable<AppearancePref> InSection(string section) => All.Where(pref => pref.Section == section);
}

/// <summary>The model of Views/Settings/Appearance.cshtml.</summary>
/// <param name="SampleModel">The model the live preview's sample reply names: the default model (<c>provider/model</c>) when one is set, else null.</param>
public sealed record AppearancePage(string? SampleModel);
