// Decision 0011 (run W2, WU4): the shell's navigation, exactly as the mockups' sidebar and settings nav (_Sidebar, _SettingsNav): New chat ·
// Playground · Compare · Tool Bench · History · Evals · Presets · recent chats · Settings; and Settings' Connections, Agent and App groups.
// Evals' leaderboard, which W5 builds, still goes to the neutral "Coming in a later milestone" page (ShellController, LaterFeature). A page names its
// sidebar item, settings item and chat through ShellViewData.
namespace PiSharp.WebAgent.Shell;

/// <summary>A link in the sidebar or the settings nav.</summary>
/// <param name="Id">The id a page sets in <see cref="ShellViewData.Nav"/> or <see cref="ShellViewData.SettingsSection"/>.</param>
/// <param name="Label">The text shown.</param>
/// <param name="Icon">The Bootstrap Icons class, such as <c>bi-gear</c>.</param>
/// <param name="Href">The route.</param>
public sealed record NavItem(string Id, string Label, string Icon, string Href);

/// <summary>A labelled group of the settings nav.</summary>
/// <param name="Label">The group's label.</param>
/// <param name="Items">Its links.</param>
public sealed record NavGroup(string Label, IReadOnlyList<NavItem> Items);

/// <summary>The sidebar and settings navigation.</summary>
public static class ShellNav
{
    /// <summary>Sidebar id of New chat (and of a chat page).</summary>
    public const string NewChat = "new-chat";

    /// <summary>Sidebar id of Playground.</summary>
    public const string Playground = "playground";

    /// <summary>Sidebar id of Compare.</summary>
    public const string Compare = "compare";

    /// <summary>Sidebar id of Tool Bench.</summary>
    public const string Bench = "bench";

    /// <summary>Sidebar id of History.</summary>
    public const string History = "history";

    /// <summary>Sidebar id of Evals.</summary>
    public const string Evals = "evals";

    /// <summary>Sidebar id of Presets.</summary>
    public const string Presets = "presets";

    /// <summary>Sidebar id of Settings (every settings page).</summary>
    public const string Settings = "settings";

    /// <summary>How many recent chats the sidebar lists.</summary>
    public const int RecentChatLimit = 30;

    /// <summary>The sidebar's main links, in order.</summary>
    public static IReadOnlyList<NavItem> Primary { get; } =
    [
        new(NewChat, "New chat", "bi-pencil-square", "/chat/new"),
        new(Playground, "Playground", "bi-sliders2", "/playground"),
        new(Compare, "Compare", "bi-layout-three-columns", "/compare"),
        new(Bench, "Tool Bench", "bi-tools", "/bench"),
        new(History, "History", "bi-clock-history", "/history"),
        new(Evals, "Evals", "bi-clipboard-check", "/evals/leaderboard"),
        new(Presets, "Presets", "bi-bookmark", "/presets"),
    ];

    /// <summary>The sidebar's footer link.</summary>
    public static NavItem SettingsLink { get; } = new(Settings, "Settings", "bi-gear", "/settings/providers");

    /// <summary>The settings nav, in order.</summary>
    public static IReadOnlyList<NavGroup> SettingsGroups { get; } =
    [
        new("Connections",
        [
            new("providers", "Providers", "bi-plug", "/settings/providers"),
            new("accounts", "Accounts & sign-ins", "bi-person-badge", "/settings/accounts"),
            new("models", "Models", "bi-boxes", "/settings/models"),
            new("custom-providers", "Custom providers", "bi-hdd-network", "/settings/custom-providers"),
        ]),
        new("Agent",
        [
            new("defaults", "Defaults", "bi-sliders", "/settings/defaults"),
            new("tools", "Tools & workspace", "bi-terminal", "/settings/tools"),
            new("spend", "Spend", "bi-wallet2", "/settings/spend"),
        ]),
        new("App",
        [
            new("appearance", "Appearance", "bi-palette", "/settings/appearance"),
            new("data", "Data & storage", "bi-database", "/settings/data"),
            new("about", "About", "bi-info-circle", "/settings/about"),
        ]),
    ];

    /// <summary>The settings item with id <paramref name="id"/>, or null.</summary>
    public static NavItem? SettingsItem(string? id) => SettingsGroups.SelectMany(group => group.Items).FirstOrDefault(item => item.Id == id);
}

/// <summary>The <c>ViewData</c> keys the layouts read. A page sets <c>ViewData["Title"]</c> and these.</summary>
public static class ShellViewData
{
    /// <summary>The active sidebar item (<see cref="ShellNav"/> ids).</summary>
    public const string Nav = "Shell.Nav";

    /// <summary>The open chat's id, highlighted among the recent chats.</summary>
    public const string ActiveChat = "Shell.ActiveChat";

    /// <summary>The active settings item (<see cref="ShellNav.SettingsGroups"/> ids); set by settings pages using <c>_SettingsLayout</c>.</summary>
    public const string SettingsSection = "Shell.SettingsSection";

    /// <summary>Extra classes for <c>&lt;main class="app-content"&gt;</c>, such as <c>no-scroll</c> for a chat.</summary>
    public const string ContentClass = "Shell.ContentClass";

    /// <summary>A page name for <c>&lt;body data-page&gt;</c>, for page scripts and tests.</summary>
    public const string Page = "Shell.Page";

    /// <summary>False hides the top bar's keyboard-shortcuts button (the settings pages, as in the mockups).</summary>
    public const string ShortcutsButton = "Shell.ShortcutsButton";
}
