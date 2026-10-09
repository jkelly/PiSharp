// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/first-time-setup.ts.
// Also shouldRunFirstTimeSetup and isOfficialDistribution from coding-agent/src/cli/startup-ui.ts and areExperimentalFeaturesEnabled
// from coding-agent/src/core/experimental.ts, which decide when main.ts shows the dialog.
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source FirstTimeSetupResult.</summary>
internal sealed record FirstTimeSetupResult(string Theme, bool ShareAnalytics);

/// <summary>Source FirstTimeSetupOptions.</summary>
internal sealed record FirstTimeSetupOptions(Action<string> OnThemePreview, Action<FirstTimeSetupResult> OnSubmit, Action OnCancel);

/// <summary>Source DistributionMetadata (startup-ui.ts).</summary>
internal sealed record DistributionMetadata(string PackageName, string AppName, string ConfigDirName);

/// <summary>First-time setup dialog: theme choice and analytics opt-in.</summary>
internal sealed class FirstTimeSetupComponent : Container, IInputHandler
{
    private const string OfficialPackageName = "@earendil-works/pi-coding-agent";
    private const string OfficialAppName = "pi";
    private const string OfficialConfigDirName = ".pi";

    /// <summary>The distribution identity PiSharp reports: it shares Pi's agent directory, file and variable names
    /// (<see cref="PiConfig.AppName"/>, <see cref="PiConfig.ConfigDirName"/>) and so counts as the official distribution.</summary>
    public static DistributionMetadata Distribution { get; } = new(OfficialPackageName, PiConfig.AppName, PiConfig.ConfigDirName);

    private static readonly (string Value, string Label)[] ThemeOptions =
    [
        (Themes.SystemThemeName, "System (matches your terminal colors)"),
        ("dark", "Dark"),
        ("light", "Light"),
    ];

    private static readonly (bool Value, string Label)[] AnalyticsOptions =
    [
        (true, "Share anonymous usage data"),
        (false, "Don't share"),
    ];

    private static readonly string[] SetupLogoLines = ["██████", "██  ██", "████  ██", "██    ██"];

    private bool analyticsStep;
    private int themeIndex;
    private int analyticsIndex;
    private readonly FirstTimeSetupOptions options;

    public FirstTimeSetupComponent(FirstTimeSetupOptions options)
    {
        this.options = options;
        themeIndex = 0;
        Update();
    }

    /// <summary>Rebuild on theme changes, e.g. when the system theme receives the terminal's colors.</summary>
    public override void Invalidate()
    {
        Update();
        base.Invalidate();
    }

    // Rebuild the whole dialog on every change so theme previews recolor all text.
    private void Update()
    {
        Clear();
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", string.Join("\n", SetupLogoLines)), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new Text(theme.Fg("accent", theme.Bold($"Welcome to {PiConfig.AppName}, the minimal coding agent.")), 1, 0));
        AddChild(new Spacer(1));

        if (!analyticsStep)
        {
            AddChild(new Text(theme.Fg("text", "Pick a theme."), 1, 0));
            AddChild(new Spacer(1));
            AddOptionList(ThemeOptions.Select(option => option.Label).ToList(), themeIndex);
        }
        else
        {
            AddChild(new Text(theme.Fg("text", "Opt-in to anonymous usage data sharing?"), 1, 0));
            AddChild(new Text(theme.Fg("muted",
                "Opting in stores a tracking identifier in settings.json and enables anonymous\nusage analytics. This helps us to better debug, reproduce, and resolve issues\nand bugs within Pi. You can observe what is shared using /privacy and make\nchanges anytime in settings.json."), 1, 0));
            AddChild(new Spacer(1));
            AddOptionList(AnalyticsOptions.Select(option => option.Label).ToList(), analyticsIndex);
        }

        AddChild(new Spacer(1));
        AddChild(new Text(
            KeybindingHints.RawKeyHint("↑↓", "navigate") + "  " +
            KeybindingHints.KeyHint("tui.select.confirm", !analyticsStep ? "continue" : "finish") + "  " +
            KeybindingHints.KeyHint("tui.select.cancel", "skip setup"), 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    private void AddOptionList(List<string> labels, int selectedIndex)
    {
        for (var i = 0; i < labels.Count; i++)
        {
            var isSelected = i == selectedIndex;
            var prefix = isSelected ? theme.Fg("accent", "→ ") : "  ";
            var label = isSelected ? theme.Fg("accent", labels[i]) : theme.Fg("text", labels[i]);
            AddChild(new Text($"{prefix}{label}", 1, 0));
        }
    }

    private void MoveSelection(int delta)
    {
        if (!analyticsStep)
        {
            var next = Math.Max(0, Math.Min(ThemeOptions.Length - 1, themeIndex + delta));
            if (next != themeIndex)
            {
                themeIndex = next;
                options.OnThemePreview(ThemeOptions[themeIndex].Value);
            }
        }
        else
        {
            analyticsIndex = Math.Max(0, Math.Min(AnalyticsOptions.Length - 1, analyticsIndex + delta));
        }
        Update();
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.up") || keyData == "k") MoveSelection(-1);
        else if (kb.Matches(keyData, "tui.select.down") || keyData == "j") MoveSelection(1);
        else if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n")
        {
            if (!analyticsStep)
            {
                analyticsStep = true;
                Update();
            }
            else
            {
                options.OnSubmit(new FirstTimeSetupResult(ThemeOptions[themeIndex].Value, AnalyticsOptions[analyticsIndex].Value));
            }
        }
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            options.OnCancel();
        }
    }

    /// <summary>Source isOfficialDistribution.</summary>
    public static bool IsOfficialDistribution(DistributionMetadata metadata) =>
        metadata.PackageName == OfficialPackageName && metadata.AppName == OfficialAppName && metadata.ConfigDirName == OfficialConfigDirName;

    /// <summary>Source areExperimentalFeaturesEnabled: <c>PI_EXPERIMENTAL=1</c>.</summary>
    public static bool AreExperimentalFeaturesEnabled(Func<string, string?> environment) => environment("PI_EXPERIMENTAL") == "1";

    /// <summary>Source shouldRunFirstTimeSetup. First-time setup runs when all of these hold:
    /// this is the official Pi distribution (not a fork/rebrand); experimental features are enabled (PI_EXPERIMENTAL=1);
    /// the default agent directory is used (no custom agent dir override); setup was not completed before (settings.json does
    /// not exist). <paramref name="settingsPath"/> defaults to getSettingsPath() (<c>&lt;agentDir&gt;/settings.json</c>).</summary>
    public static bool ShouldRunFirstTimeSetup(string? settingsPath = null, Func<string, string?>? environment = null,
        DistributionMetadata? distribution = null, string? home = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (!IsOfficialDistribution(distribution ?? Distribution)) return false;
        if (!AreExperimentalFeaturesEnabled(environment)) return false;
        if (!string.IsNullOrEmpty(environment(PiConfig.EnvAgentDir))) return false;
        settingsPath ??= Path.Join(PiPaths.AgentDirectory(environment, home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "settings.json");
        return !Path.Exists(settingsPath);
    }
}
