// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/theme/theme-controller.ts.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Applies the theme setting and keeps it in sync with the terminal: the theme applies immediately and the terminal's
/// colors update it when they arrive (the system theme renders in grayscale until then).</summary>
internal sealed class InteractiveThemeController
{
    /// <summary>How long the system theme stays grayscale before falling back to palette indices.</summary>
    public const int TerminalQueryTimeoutMs = 100;

    private readonly ITui ui;
    private readonly Func<InteractiveSettings> getSettings;
    private readonly Action<string> showError;
    private readonly Action onChanged;
    private string? currentThemeSetting;
    private TerminalColors? terminalColors;
    private string? activeThemeName;
    private bool autoSyncEnabled;
    private Action? terminalColorSchemeUnsubscribe;
    private Task terminalColorQuery = Task.CompletedTask;

    public InteractiveThemeController(ITui ui, Func<InteractiveSettings> getSettings, Action<string> showError, Action onChanged, string? initialThemeSetting)
    {
        this.ui = ui; this.getSettings = getSettings; this.showError = showError; this.onChanged = onChanged;
        currentThemeSetting = initialThemeSetting;
        activeThemeName = ResolveThemeName();
        Themes.MarkTerminalColorsPending();
        Themes.InitTheme(activeThemeName, enableWatcher: true);
        BindTerminalColorSchemeListener();
    }

    /// <summary>requestTerminalColors: apply the colors when the query completes or times out, and again on a late reply.</summary>
    public static Task RequestTerminalColors(ITui ui, Action<TerminalColors> apply)
    {
        Task<TerminalColors> query;
        try { query = ui.QueryTerminalColors(TerminalQueryTimeoutMs, apply); }
        catch { query = Task.FromResult(new TerminalColors(null, null, null)); }
        return query.ContinueWith(task => apply(task.IsCompletedSuccessfully ? task.Result : new TerminalColors(null, null, null)),
            CancellationToken.None, TaskContinuationOptions.None, SynchronizationContext.Current is { } context ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default);
    }

    private static bool SameRgb(RgbColor? a, RgbColor? b) => a == b;
    private static bool SameTerminalColors(TerminalColors a, TerminalColors b)
    {
        if (!SameRgb(a.Foreground, b.Foreground) || !SameRgb(a.Background, b.Background)) return false;
        if (ReferenceEquals(a.Palette, b.Palette)) return true;
        if (a.Palette is null || b.Palette is null || a.Palette.Count != b.Palette.Count) return false;
        return a.Palette.SequenceEqual(b.Palette);
    }

    public void RebindTui()
    {
        terminalColorSchemeUnsubscribe?.Invoke();
        BindTerminalColorSchemeListener();
        ui.SetTerminalColorSchemeNotifications(autoSyncEnabled);
    }

    public void ApplyFromSettings()
    {
        var themeSetting = GetThemeSetting();
        var themeName = ResolveThemeName();
        SetAutoSync(Themes.ParseAutoThemeSetting(themeSetting) is not null || themeName == Themes.SystemThemeName);
        ApplyThemeName(themeName, themeSetting is not null);
        QueryTerminalColors();
    }

    public Task WaitForTerminalColors() => terminalColorQuery;

    public string? GetThemeSelection() => currentThemeSetting ?? getSettings().ThemeSetting ?? activeThemeName;

    public (bool Success, string? Error) SetThemeName(string themeName, bool showErrorOnFailure = false)
    {
        SetAutoSync(themeName == Themes.SystemThemeName);
        var result = ApplyThemeName(themeName, showErrorOnFailure);
        if (result.Success) currentThemeSetting = themeName;
        return result;
    }

    public void SetThemeSetting(string themeSetting)
    {
        currentThemeSetting = themeSetting;
        ApplyFromSettings();
    }

    public (bool Success, string? Error) SetThemeInstance(Theme theme)
    {
        SetAutoSync(false);
        Themes.SetThemeInstance(theme);
        activeThemeName = "<in-memory>";
        NotifyChanged();
        return (true, null);
    }

    public void Preview(string themeSettingOrName)
    {
        var themeName = Themes.ResolveThemeSetting(themeSettingOrName, Themes.GetTerminalTheme()) ?? activeThemeName;
        if (themeName is null) return;
        if (Themes.SetTheme(themeName, enableWatcher: true).Success)
        {
            ui.Invalidate();
            ui.RequestRender();
        }
    }

    public void DisableAutoSync() => SetAutoSync(false);

    public void Dispose()
    {
        SetAutoSync(false);
        terminalColorSchemeUnsubscribe?.Invoke();
        terminalColorSchemeUnsubscribe = null;
    }

    public string GetTerminalTheme() => Themes.GetTerminalTheme();

    private string? GetThemeSetting() => currentThemeSetting ?? getSettings().ThemeSetting;

    private string ResolveThemeName() => Themes.ResolveThemeSetting(GetThemeSetting(), Themes.GetTerminalTheme()) ?? Themes.SystemThemeName;

    private (bool Success, string? Error) ApplyThemeName(string themeName, bool showErrorOnFailure = false)
    {
        var result = Themes.SetTheme(themeName, enableWatcher: true);
        activeThemeName = result.Success ? themeName : Themes.SystemThemeName;
        NotifyChanged();
        if (!result.Success && showErrorOnFailure)
            showError($"Failed to load theme \"{themeName}\": {result.Error}\nFell back to the system theme.");
        return result;
    }

    private void QueryTerminalColors() => terminalColorQuery = RequestTerminalColors(ui, ApplyTerminalColors);

    private void ApplyTerminalColors(TerminalColors reported)
    {
        var previous = terminalColors;
        var next = new TerminalColors(reported.Foreground ?? previous?.Foreground, reported.Background ?? previous?.Background,
            reported.Palette ?? previous?.Palette);
        if (previous is not null && SameTerminalColors(previous, next)) return;
        terminalColors = next;
        Themes.SetTerminalColors(next);
        ReapplyForTerminal();
        ui.Invalidate();
        ui.RequestRender();
    }

    private void ReapplyForTerminal()
    {
        if (activeThemeName == "<in-memory>") return;
        var themeName = ResolveThemeName();
        if (themeName == Themes.SystemThemeName || themeName != activeThemeName) ApplyThemeName(themeName);
    }

    private void SetAutoSync(bool enabled)
    {
        if (autoSyncEnabled == enabled) return;
        autoSyncEnabled = enabled;
        ui.SetTerminalColorSchemeNotifications(enabled);
    }

    private void BindTerminalColorSchemeListener() =>
        terminalColorSchemeUnsubscribe = ui.OnTerminalColorSchemeChange(ApplyTerminalColorSchemeChange);

    private void ApplyTerminalColorSchemeChange(TerminalColorScheme terminalTheme)
    {
        if (!autoSyncEnabled) return;
        var previous = Themes.GetTerminalTheme();
        Themes.SetTerminalColorScheme(terminalTheme);
        if (Themes.GetTerminalTheme() != previous) ReapplyForTerminal();
        QueryTerminalColors();
    }

    private void NotifyChanged()
    {
        ui.Invalidate();
        onChanged();
    }
}
