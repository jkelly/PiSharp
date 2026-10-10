// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/cli/startup-ui.ts (createStartupTui, startStartupTui,
// showStartupSelector, showStartupInput, showFirstTimeSetup, shouldRunFirstTimeSetup), coding-agent/src/cli/session-picker.ts
// (selectSession), coding-agent/src/cli/config-selector.ts (selectConfig) and the interactive prompts main.ts runs before the session\n// starts (project trust, missing session cwd).
using System.Collections.Immutable;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Pi;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Short-lived main-screen TUIs for the questions asked before the interactive session exists.</summary>
internal sealed class StartupUi(string agentDir, string cwd, Func<string, string?>? environment = null, Func<UiLoop, ITerminal>? createTerminal = null)
{
    private readonly Func<string, string?> env = environment ?? Environment.GetEnvironmentVariable;

    private InteractiveSettings Settings() => new(cwd, agentDir, projectTrusted: false, env);

    /// <summary>createStartupTui + startStartupTui: theme from settings, keybindings, a main-screen TUI on its own loop.</summary>
    private (UiLoop Loop, TuiMainScreen Ui) Create(InteractiveSettings settings)
    {
        TerminalImage.SetCapabilityOverrides(settings.TerminalCapabilityOverrides);
        Themes.AgentDirectory = agentDir;
        Themes.MarkTerminalColorsPending();
        Themes.InitTheme(Themes.ResolveThemeSetting(settings.ThemeSetting, Themes.GetTerminalTheme()) ?? Themes.SystemThemeName);
        PiSharp.Tui.Pi.KeybindingsManager.SetGlobal(AppKeybindings.Create(agentDir).Manager);
        var loop = new UiLoop("pi-startup");
        var terminal = createTerminal?.Invoke(loop) ?? new ProcessTerminal(loop);
        var ui = new TuiMainScreen(terminal, loop, settings.ShowHardwareCursor, agentDir) { ClearOnShrink = settings.ClearOnShrink };
        return (loop, ui);
    }

    private static void Start(TuiMainScreen ui, InteractiveSettings settings)
    {
        ui.Start();
        var themeSetting = settings.ThemeSetting;
        _ = InteractiveThemeController.RequestTerminalColors(ui, colors =>
        {
            Themes.SetTerminalColors(colors);
            Themes.SetTheme(Themes.ResolveThemeSetting(themeSetting, Themes.GetTerminalTheme()) ?? Themes.SystemThemeName);
            ui.Invalidate();
            ui.RequestRender();
        });
    }

    private static async Task Clear(TuiMainScreen ui)
    {
        ui.Clear();
        ui.RequestRender();
        await Task.Delay(25);
    }

    private async Task<T?> RunAsync<T>(Func<TuiMainScreen, InteractiveSettings, Action<T?>, IComponent> build, CancellationToken token, bool focusChild = true,
        Func<IComponent, IComponent>? focus = null)
    {
        var settings = Settings();
        var (loop, ui) = Create(settings);
        var result = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => result.TrySetCanceled(token));
        try
        {
            await loop.InvokeAsync(() =>
            {
                var settled = false;
                var component = build(ui, settings, value =>
                {
                    if (settled) return;
                    settled = true;
                    result.TrySetResult(value);
                });
                ui.AddChild(component);
                ui.SetFocus(focus?.Invoke(component) ?? component);
                Start(ui, settings);
            });
            return await result.Task;
        }
        finally
        {
            await loop.InvokeAsync(async () => { await Clear(ui); ui.Stop(); });
            loop.Stop();
        }
    }

    /// <summary>showStartupSelector: the chosen option's value, or default when cancelled.</summary>
    public Task<T?> ShowStartupSelectorAsync<T>(string title, IReadOnlyList<(string Label, T Value)> options, CancellationToken token = default) =>
        RunAsync<T>((ui, _, finish) => new ExtensionSelectorComponent(title, options.Select(option => option.Label).ToList(),
            label => finish(options.First(option => option.Label == label).Value), () => finish(default), new ExtensionSelectorOptions(ui)), token);

    /// <summary>showStartupInput.</summary>
    public Task<string?> ShowStartupInputAsync(string title, string? placeholder, CancellationToken token = default) =>
        RunAsync<string>((ui, _, finish) => new ExtensionInputComponent(title, placeholder, value => finish(value), () => finish(null), new ExtensionInputOptions(ui)), token);

    /// <summary>The project trust prompt (core/project-trust.ts through ctx.ui.select).</summary>
    public async Task<ProjectTrustOption?> PromptProjectTrustAsync(string title, ImmutableArray<ProjectTrustOption> options, CancellationToken token)
    {
        var chosen = await ShowStartupSelectorAsync(title, options.Select(option => (option.Label, option.Label)).ToList(), token);
        return chosen is null ? null : options.FirstOrDefault(option => option.Label == chosen);
    }

    /// <summary>promptForMissingSessionCwd: Continue (the fallback cwd) or Cancel.</summary>
    public Task<string?> PromptForMissingSessionCwdAsync(string prompt, string fallbackCwd, CancellationToken token = default) =>
        ShowStartupSelectorAsync<string>(prompt, [("Continue", fallbackCwd), ("Cancel", null!)], token);

    /// <summary>selectSession: the TUI session selector for --resume; null when cancelled.</summary>
    public Task<string?> SelectSessionAsync(ImmutableArray<PiSessionInfo> current, Func<ImmutableArray<PiSessionInfo>> all, CancellationToken token) =>
        RunAsync<string>((ui, _, finish) =>
        {
            var keybindings = AppKeybindings.Create(agentDir);
            PiSharp.Tui.Pi.KeybindingsManager.SetGlobal(keybindings.Manager);
            return new SessionSelectorComponent(
                (_, _) => Task.FromResult<IReadOnlyList<PiSessionInfo>>(current),
                (_, cancellation) => Task.Run(() => (IReadOnlyList<PiSessionInfo>)all(), cancellation),
                path => finish(path), () => finish(null), () => { ui.Stop(); Environment.Exit(0); }, () => ui.RequestRender(),
                new SessionSelectorOptions { ShowRenameHint = false, Keybindings = keybindings.Manager });
        }, token, focus: component => ((SessionSelectorComponent)component).GetSessionList());

    /// <summary>cli/config-selector.ts selectConfig (<c>pisharp config</c>): the package resource configuration TUI; completes when it
    /// closes. Changes are written through the command's settings (SettingsManager setters).</summary>
    public Task SelectConfigAsync(PiSharp.Cli.Packages.PiPackageConfigRequest request, CancellationToken token = default)
    {
        static ResolvedPaths Map(PiSharp.Cli.Packages.PiResolvedPaths paths)
        {
            static IReadOnlyList<ResolvedResource> Resources(IEnumerable<PiSharp.Cli.Packages.PiResolvedResource> items) => [.. items.Select(item =>
                new ResolvedResource(item.Path, item.Enabled, new(item.Metadata.Source, item.Metadata.Scope, item.Metadata.Origin, item.Metadata.BaseDir, item.Metadata.PackageRoot)))];
            return new(Resources(paths.Extensions), Resources(paths.Skills), Resources(paths.Prompts), Resources(paths.Themes));
        }
        return RunAsync<bool>((ui, _, finish) => new ConfigSelectorComponent(new(Map(request.Global), Map(request.Project)),
            new ConfigSettings(request.Settings), request.Cwd, request.AgentDir, () => finish(true), () => { ui.Stop(); Environment.Exit(0); },
            () => ui.RequestRender(), ui.Terminal.Rows, request.WriteScope, request.ProjectModeAvailable),
            token, focus: component => ((ConfigSelectorComponent)component).GetResourceList());
    }

    /// <summary>The SettingsManager setters config-selector.ts calls, over the command's <see cref="PiSettings"/>.</summary>
    private sealed class ConfigSettings(PiSettings settings) : IConfigSelectorSettings
    {
        private static System.Text.Json.Nodes.JsonArray Array(IReadOnlyList<string> paths) => [.. paths.Select(path => (System.Text.Json.Nodes.JsonNode)path)];
        public System.Text.Json.Nodes.JsonObject GetGlobalSettings() => settings.Global;
        public System.Text.Json.Nodes.JsonObject GetProjectSettings() => settings.Project;
        public void SetExtensionPaths(IReadOnlyList<string> paths) => settings.SetField("global", "extensions", Array(paths));
        public void SetSkillPaths(IReadOnlyList<string> paths) => settings.SetField("global", "skills", Array(paths));
        public void SetPromptTemplatePaths(IReadOnlyList<string> paths) => settings.SetField("global", "prompts", Array(paths));
        public void SetThemePaths(IReadOnlyList<string> paths) => settings.SetField("global", "themes", Array(paths));
        public void SetProjectExtensionPaths(IReadOnlyList<string> paths) => settings.SetField("project", "extensions", Array(paths));
        public void SetProjectSkillPaths(IReadOnlyList<string> paths) => settings.SetField("project", "skills", Array(paths));
        public void SetProjectPromptTemplatePaths(IReadOnlyList<string> paths) => settings.SetField("project", "prompts", Array(paths));
        public void SetProjectThemePaths(IReadOnlyList<string> paths) => settings.SetField("project", "themes", Array(paths));
        public void SetPackages(System.Text.Json.Nodes.JsonArray packages) => settings.SetField("global", "packages", packages);
        public void SetProjectPackages(System.Text.Json.Nodes.JsonArray packages) => settings.SetField("project", "packages", packages);
    }
}