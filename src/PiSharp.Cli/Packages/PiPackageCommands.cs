// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/package-manager-cli.ts (getPackageCommandUsage,
// printConfigCommandHelp, printPackageCommandHelp, parsePackageCommand, refreshModelCatalogs, createCommandSettingsManager,
// reportSettingsErrors, reportProjectTrustWarnings, handleConfigCommand, handlePackageCommand) and src/cli/config-selector.ts.
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Packages;

/// <summary>What the <c>config</c> resource selector (cli/config-selector.ts, IMPL-I's TUI) receives: the resources resolved for the
/// global and the project view, the settings it edits (with <see cref="PiSettings.SetField"/>), and the scope it starts in.</summary>
internal sealed record PiPackageConfigRequest(PiResolvedPaths Global, PiResolvedPaths Project, PiSettings Settings, string Cwd, string AgentDir,
    string WriteScope, bool ProjectModeAvailable);

/// <summary>Source selectConfig: shows the resource configuration TUI and completes when it closes.</summary>
internal delegate Task PiPackageConfigSelector(PiPackageConfigRequest request, CancellationToken cancellationToken);

/// <summary>Source package-manager-cli.ts: <c>install</c>, <c>remove</c>/<c>uninstall</c>, <c>update</c>, <c>list</c> and <c>config</c>.
/// Install telemetry is not ported (decision 0004).</summary>
internal static class PiPackageCommands
{
    private static string App => PiConfig.DisplayName;

    internal sealed record Options(string Command, string? Source, (string Type, string? Source)? UpdateTarget, bool ShowExtensionsSkippedNote, bool Local,
        bool Force, bool? ProjectTrustOverride, bool Help, string? InvalidOption, string? InvalidArgument, string? MissingOptionValue, string? ConflictingOptions);

    internal static string Usage(string command) => command switch
    {
        "install" => $"{App} install <source> [-l] [--approve|--no-approve]",
        "remove" => $"{App} remove <source> [-l] [--approve|--no-approve]",
        "update" => $"{App} update [source|self|pi] [--self|--extensions|--models|--all] [--extension <source>] [--approve|--no-approve] [--force]",
        _ => $"{App} list [--approve|--no-approve]"
    };

    internal static string ConfigUsage => $"{App} config [-l] [--approve|--no-approve]";

    private sealed class Console(PiHost host)
    {
        private string Paint(string open, string close, string text) => host.Color ? open + text + close : text;
        internal string Bold(string text) => Paint("\u001b[1m", "\u001b[22m", text);
        internal string Dim(string text) => Paint("\u001b[2m", "\u001b[22m", text);
        internal string Red(string text) => Paint("\u001b[31m", "\u001b[39m", text);
        internal string Green(string text) => Paint("\u001b[32m", "\u001b[39m", text);
        internal string Yellow(string text) => Paint("\u001b[33m", "\u001b[39m", text);
        internal Task Log(string text = "") => PiCommand.Line(host.Stdout, text);
        internal Task Error(string text) => PiCommand.Line(host.Stderr, text);
    }

    private static string ConfigHelp(Console console) => $@"{console.Bold("Usage:")}
  {ConfigUsage}

Open the resource configuration TUI to enable or disable package resources.
Without -l, starts in global settings (~/{PiConfig.ConfigDirName}/agent/settings.json).
Press Tab in the TUI to switch between global and project-local modes.

Options:
  -l, --local       Edit project overrides ({PiConfig.ConfigDirName}/settings.json)
  -a, --approve     Trust project-local files for this command with -l
  -na, --no-approve Ignore project-local files for this command with -l
";

    private static string PackageHelp(Console console, string command) => command switch
    {
        "install" => $@"{console.Bold("Usage:")}
  {Usage("install")}

Install a package and add it to settings.

Options:
  -l, --local       Install project-locally ({PiConfig.ConfigDirName}/settings.json)
  -a, --approve     Trust project-local files for this command
  -na, --no-approve Ignore project-local files for this command

Examples:
  {App} install npm:@foo/bar
  {App} install git:github.com/user/repo
  {App} install git:git@github.com:user/repo
  {App} install https://github.com/user/repo
  {App} install ssh://git@github.com/user/repo
  {App} install ./local/path
",
        "remove" => $@"{console.Bold("Usage:")}
  {Usage("remove")}

Remove a package and its source from settings.
Alias: {App} uninstall <source> [-l]

Options:
  -l, --local       Remove from project settings ({PiConfig.ConfigDirName}/settings.json)
  -a, --approve     Trust project-local files for this command
  -na, --no-approve Ignore project-local files for this command

Examples:
  {App} remove npm:@foo/bar
  {App} uninstall npm:@foo/bar
",
        "update" => $@"{console.Bold("Usage:")}
  {Usage("update")}

Update pi, installed packages, or model catalogs.

Options:
  --self                  Update pi only (default when no target is given)
  --extensions            Update installed packages only
  --models                Refresh model catalogs only
  --all                   Update pi and installed packages
  --extension <source>    Update one package only
  -a, --approve           Trust project-local files for this command
  -na, --no-approve       Ignore project-local files for this command
  --force                 Reinstall pi even if the current version is latest

Short forms:
  {App} update                Update pi only
  {App} update --all          Update pi and all extensions
  {App} update --models       Refresh model catalogs only
  {App} update <source>       Update one package
  {App} update pi             Update pi only (self works as alias to pi)
",
        _ => $@"{console.Bold("Usage:")}
  {Usage("list")}

List installed packages from user and project settings.

Options:
  -a, --approve      Trust project-local files for this command
  -na, --no-approve  Ignore project-local files for this command
"
    };

    /// <summary>Source parsePackageCommand; null when the first argument is not a package command.</summary>
    internal static Options? Parse(string[] args)
    {
        if (args.Length == 0) return null;
        var command = args[0] == "uninstall" ? "remove" : args[0] is "install" or "remove" or "update" or "list" ? args[0] : null;
        if (command is null) return null;
        bool local = false, force = false, help = false, selfFlag = false, extensionsFlag = false, modelsFlag = false, allFlag = false;
        bool? trustOverride = null;
        string? invalidOption = null, invalidArgument = null, missingValue = null, conflicting = null, source = null, extensionSource = null;
        var rest = args[1..];
        for (var index = 0; index < rest.Length; index++)
        {
            var arg = rest[index];
            switch (arg)
            {
                case "-h" or "--help": help = true; continue;
                case "-l" or "--local": if (command is "install" or "remove") local = true; else invalidOption ??= arg; continue;
                case "--self": if (command == "update") selfFlag = true; else invalidOption ??= arg; continue;
                case "--extensions": if (command == "update") extensionsFlag = true; else invalidOption ??= arg; continue;
                case "--models": if (command == "update") modelsFlag = true; else invalidOption ??= arg; continue;
                case "--all": if (command == "update") allFlag = true; else invalidOption ??= arg; continue;
                case "--approve" or "-a": trustOverride = true; continue;
                case "--no-approve" or "-na": trustOverride = false; continue;
                case "--force": if (command == "update") force = true; else invalidOption ??= arg; continue;
                case "--extension":
                    if (command != "update") { invalidOption ??= arg; continue; }
                    var value = index + 1 < rest.Length ? rest[index + 1] : null;
                    if (string.IsNullOrEmpty(value) || value.StartsWith('-')) missingValue ??= arg;
                    else if (extensionSource is not null) { conflicting ??= "--extension can only be provided once"; index++; }
                    else { extensionSource = value; index++; }
                    continue;
            }
            if (arg.StartsWith('-')) { invalidOption ??= arg; continue; }
            if (source is null) source = arg; else invalidArgument ??= arg;
        }
        (string, string?)? target = null;
        var skippedNote = false;
        if (command == "update")
        {
            if (allFlag && (selfFlag || extensionsFlag || modelsFlag || extensionSource is not null))
                conflicting ??= "--all cannot be combined with --self, --extensions, --models, or --extension";
            if (allFlag && source is not null) conflicting ??= "--all cannot be combined with a positional source";
            if (modelsFlag)
            {
                if (selfFlag || extensionsFlag || allFlag || extensionSource is not null)
                    conflicting ??= "--models cannot be combined with --self, --extensions, --all, or --extension";
                if (source is not null) conflicting ??= "--models cannot be combined with a positional source";
                target = ("models", null);
            }
            else if (extensionSource is not null)
            {
                if (selfFlag || extensionsFlag || allFlag) conflicting ??= "--extension cannot be combined with --self, --extensions, or --all";
                if (source is not null) conflicting ??= "--extension cannot be combined with a positional source";
                target = ("extensions", extensionSource);
            }
            else if (source is not null)
            {
                if (source is "self" or "pi") target = extensionsFlag ? ("all", null) : ("self", null);
                else
                {
                    if (extensionsFlag || selfFlag || allFlag)
                        conflicting ??= "positional update targets cannot be combined with --self, --extensions, or --all";
                    target = ("extensions", source);
                }
            }
            else if (allFlag || selfFlag && extensionsFlag) target = ("all", null);
            else if (selfFlag) target = ("self", null);
            else if (extensionsFlag) target = ("extensions", null);
            else { target = ("self", null); skippedNote = true; }
        }
        return new(command, source, target, skippedNote, local, force, trustOverride, help, invalidOption, invalidArgument, missingValue, conflicting);
    }

    /// <summary>Source createCommandSettingsManager: <c>update</c> uses only the saved trust decision (or the override); other commands
    /// resolve project trust as the main entry does (extensions' project_trust handlers, trust.json, defaultProjectTrust, and the
    /// prompt when stdin and stdout are terminals).</summary>
    private static async Task<(PiSettings Settings, List<string> Warnings)> CreateCommandSettings(PiHost host, string cwd, string agentDir, bool? trustOverride,
        bool useSavedProjectTrustOnly, CancellationToken token)
    {
        var warnings = new List<string>();
        var initial = PiSettings.Load(cwd, agentDir, projectTrusted: false);
        var store = new ProjectTrustStore(agentDir, host.Home);
        bool trusted;
        if (useSavedProjectTrustOnly) trusted = trustOverride ?? store.Get(cwd) == true;
        else
        {
            var interactive = host.StdinIsTty && host.StdoutIsTty;
            var extensions = trustOverride is null && host.LoadExtensions is not null && ProjectTrustStore.HasTrustRequiringProjectResources(cwd, host.Home)
                ? await host.LoadExtensions(cwd, token).ConfigureAwait(false) : null;
            trusted = await PiProjectTrust.ResolveAsync(cwd, host.Home, store, trustOverride, initial.DefaultProjectTrust, interactive ? host.TrustPrompt : null, token,
                extensions is null ? null : (trustCwd, trustToken) => PiExtensionEvents.ProjectTrustAsync(extensions.Registry, extensions.Snapshot, trustCwd, message =>
                { warnings.Add(message); return ValueTask.CompletedTask; }, trustToken)).ConfigureAwait(false);
        }
        return (PiSettings.Load(cwd, agentDir, trusted), warnings);
    }

    private static async Task ReportSettingsErrors(Console console, PiSettings settings, string context)
    {
        foreach (var error in settings.Errors.ToList())
            await console.Error(console.Yellow($"Warning ({context}, {error.Scope} settings): {error.Message}")).ConfigureAwait(false);
        settings.DrainDiagnostics();
    }

    private static PiPackageProcesses Processes(PiHost host) => host.PackageProcesses?.Invoke(host.Stdout, host.Stderr) ?? new PiPackageProcesses { Output = host.Stdout, ErrorOutput = host.Stderr };

    /// <summary>Source handleConfigCommand; null when the arguments are not a <c>config</c> command.</summary>
    internal static async Task<int?> RunConfigAsync(string[] args, PiHost host, CancellationToken token)
    {
        if (args.Length == 0 || args[0] != "config") return null;
        var console = new Console(host);
        var rest = args[1..];
        if (rest.Contains("-h") || rest.Contains("--help")) { await console.Log(ConfigHelp(console)).ConfigureAwait(false); return 0; }
        var local = false; bool? trustOverride = null;
        foreach (var arg in rest)
        {
            if (arg is "-l" or "--local") local = true;
            else if (arg is "-a" or "--approve") trustOverride = true;
            else if (arg is "-na" or "--no-approve") trustOverride = false;
            else if (arg.StartsWith('-'))
            {
                await console.Error(console.Red($"Unknown option {arg} for \"config\".")).ConfigureAwait(false);
                await console.Error(console.Dim($"Use \"{App} --help\" or \"{ConfigUsage}\".")).ConfigureAwait(false);
                return 1;
            }
            else
            {
                await console.Error(console.Red($"Unexpected argument {arg}.")).ConfigureAwait(false);
                await console.Error(console.Dim($"Usage: {ConfigUsage}")).ConfigureAwait(false);
                return 1;
            }
        }
        var cwd = host.Cwd; var agentDir = PiPaths.AgentDirectory(host.GetEnvironment, host.Home);
        PiSettings settings; List<string> warnings;
        try { (settings, warnings) = await CreateCommandSettings(host, cwd, agentDir, trustOverride, false, token).ConfigureAwait(false); }
        catch (InvalidDataException error) { await console.Error(console.Red($"Error: {error.Message}")).ConfigureAwait(false); return 1; }
        foreach (var warning in warnings) await console.Error(console.Yellow($"Warning: {warning}")).ConfigureAwait(false);
        if (local && !settings.ProjectTrusted)
        {
            await console.Error(console.Red("Project is not trusted. Use --approve to modify local resource config.")).ConfigureAwait(false);
            return 1;
        }
        await ReportSettingsErrors(console, settings, "config command").ConfigureAwait(false);
        var globalSettings = PiSettings.Load(cwd, agentDir, projectTrusted: false);
        var processes = Processes(host);
        try
        {
            var globalResolved = await new PiPackageManager(cwd, agentDir, host.Home, globalSettings, host.GetEnvironment, processes).ResolveAsync(cancellationToken: token).ConfigureAwait(false);
            var projectResolved = settings.ProjectTrusted
                ? await new PiPackageManager(cwd, agentDir, host.Home, settings, host.GetEnvironment, processes).ResolveAsync(cancellationToken: token).ConfigureAwait(false)
                : globalResolved;
            if (host.ConfigSelector is null)
            {
                // Source selectConfig always opens the TUI; this host has none (IMPL-I provides it).
                await console.Error(console.Red($"Error: \"{App} config\" requires the interactive terminal UI, which is not available in this host.")).ConfigureAwait(false);
                return 1;
            }
            await host.ConfigSelector(new(globalResolved, projectResolved, settings, cwd, agentDir, local ? "project" : "global", settings.ProjectTrusted), token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is PiPackageException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await console.Error(console.Red($"Error: {error.Message}")).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Source handlePackageCommand; null when the arguments are not a package command.</summary>
    internal static async Task<int?> RunAsync(string[] args, PiHost host, CancellationToken token)
    {
        var options = Parse(args);
        if (options is null) return null;
        var console = new Console(host);
        if (options.Help) { await console.Log(PackageHelp(console, options.Command)).ConfigureAwait(false); return 0; }
        if (options.InvalidOption is { } invalid)
        {
            await console.Error(console.Red($"Unknown option {invalid} for \"{options.Command}\".")).ConfigureAwait(false);
            await console.Error(console.Dim($"Use \"{App} --help\" or \"{Usage(options.Command)}\".")).ConfigureAwait(false);
            return 1;
        }
        if (options.MissingOptionValue is { } missing)
        {
            await console.Error(console.Red($"Missing value for {missing}.")).ConfigureAwait(false);
            await console.Error(console.Dim($"Usage: {Usage(options.Command)}")).ConfigureAwait(false);
            return 1;
        }
        if (options.InvalidArgument is { } unexpected)
        {
            await console.Error(console.Red($"Unexpected argument {unexpected}.")).ConfigureAwait(false);
            await console.Error(console.Dim($"Usage: {Usage(options.Command)}")).ConfigureAwait(false);
            return 1;
        }
        if (options.ConflictingOptions is { } conflict)
        {
            await console.Error(console.Red(conflict)).ConfigureAwait(false);
            await console.Error(console.Dim($"Usage: {Usage(options.Command)}")).ConfigureAwait(false);
            return 1;
        }
        var source = options.Source;
        if (options.Command is "install" or "remove" && source is null)
        {
            await console.Error(console.Red($"Missing {options.Command} source.")).ConfigureAwait(false);
            await console.Error(console.Dim($"Usage: {Usage(options.Command)}")).ConfigureAwait(false);
            return 1;
        }
        var agentDir = PiPaths.AgentDirectory(host.GetEnvironment, host.Home);
        if (options.Command == "update" && options.UpdateTarget?.Type == "models") return await RefreshModelCatalogs(console, host, token).ConfigureAwait(false);

        var cwd = host.Cwd;
        var writesProjectPackageConfig = options.Command is "install" or "remove" && options.Local;
        PiSettings settings; List<string> warnings;
        try { (settings, warnings) = await CreateCommandSettings(host, cwd, agentDir, options.ProjectTrustOverride, options.Command == "update", token).ConfigureAwait(false); }
        catch (InvalidDataException error) { await console.Error(console.Red($"Error: {error.Message}")).ConfigureAwait(false); return 1; }
        foreach (var warning in warnings) await console.Error(console.Yellow($"Warning: {warning}")).ConfigureAwait(false);
        if (!settings.ProjectTrusted && writesProjectPackageConfig)
        {
            await console.Error(console.Red("Project is not trusted. Use --approve to modify local package config.")).ConfigureAwait(false);
            return 1;
        }
        await ReportSettingsErrors(console, settings, "package command").ConfigureAwait(false);

        var manager = new PiPackageManager(cwd, agentDir, host.Home, settings, host.GetEnvironment, Processes(host));
        manager.SetProgressCallback(progress =>
        {
            if (progress.Type != "start") return;
            host.Stdout.Write(console.Dim($"{progress.Message}\n"));
            host.Stdout.Flush();
        });
        try
        {
            switch (options.Command)
            {
                case "install":
                    await manager.InstallAndPersistAsync(source!, options.Local, token).ConfigureAwait(false);
                    await console.Log(console.Green($"Installed {source}")).ConfigureAwait(false);
                    return 0;
                case "remove":
                    if (!await manager.RemoveAndPersistAsync(source!, options.Local, token).ConfigureAwait(false))
                    {
                        await console.Error(console.Red($"No matching package found for {source}")).ConfigureAwait(false);
                        return 1;
                    }
                    await console.Log(console.Green($"Removed {source}")).ConfigureAwait(false);
                    return 0;
                case "list":
                {
                    var configured = manager.ListConfiguredPackages();
                    var user = configured.Where(package => package.Scope == "user").ToList();
                    var project = configured.Where(package => package.Scope == "project").ToList();
                    if (configured.IsEmpty) { await console.Log(console.Dim("No packages installed.")).ConfigureAwait(false); return 0; }
                    async Task Format(PiConfiguredPackage package)
                    {
                        await console.Log($"  {(package.Filtered ? $"{package.Source} (filtered)" : package.Source)}").ConfigureAwait(false);
                        if (package.InstalledPath is { } installed) await console.Log(console.Dim($"    {installed}")).ConfigureAwait(false);
                    }
                    if (user.Count > 0)
                    {
                        await console.Log(console.Bold("User packages:")).ConfigureAwait(false);
                        foreach (var package in user) await Format(package).ConfigureAwait(false);
                    }
                    if (project.Count > 0)
                    {
                        if (user.Count > 0) await console.Log().ConfigureAwait(false);
                        await console.Log(console.Bold("Project packages:")).ConfigureAwait(false);
                        foreach (var package in project) await Format(package).ConfigureAwait(false);
                    }
                    return 0;
                }
                default:
                {
                    var target = options.UpdateTarget ?? ("self", null);
                    if (options.ShowExtensionsSkippedNote)
                        await console.Log(console.Dim($"Extensions are skipped. Run {App} update --extensions to update extensions.")).ConfigureAwait(false);
                    if (target.Type is "all" or "extensions")
                    {
                        var updateSource = target.Type == "extensions" ? target.Source : null;
                        await manager.UpdateAsync(updateSource, token).ConfigureAwait(false);
                        await console.Log(console.Green(updateSource is not null ? $"Updated {updateSource}" : "Updated packages")).ConfigureAwait(false);
                    }
                    if (target.Type is "all" or "self")
                    {
                        // Source getSelfUpdateCommand/printSelfUpdateUnavailable: PiSharp is not installed by npm or Pi's installer, so the
                        // self-update path always reports that this installation cannot update itself.
                        await console.Error($"error: {App} cannot self-update this installation.").ConfigureAwait(false);
                        await console.Error($"Update {App} using the package manager, wrapper, or source checkout that provides this installation.").ConfigureAwait(false);
                        return 1;
                    }
                    return 0;
                }
            }
        }
        catch (Exception error) when (error is PiPackageException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            await console.Error(console.Red($"Error: {error.Message}")).ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Source refreshModelCatalogs: a forced network refresh of every remote catalog, 15 seconds at most.</summary>
    private static async Task<int> RefreshModelCatalogs(Console console, PiHost host, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var registry = await host.LiveRuntime.CreateModelRegistryAsync(timeout.Token).ConfigureAwait(false);
            IReadOnlyDictionary<string, Exception> errors;
            try { errors = await registry.RefreshAsync(allowNetwork: true, force: true, providers: null, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new PiPackageException("Model catalog refresh timed out."); }
            if (errors.Count > 0)
                throw new PiPackageException($"Could not refresh model catalogs: {string.Join("; ", errors.Select(pair => $"{pair.Key}: {pair.Value.Message}"))}");
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        {
            await console.Error(console.Red($"Error: {(error is OperationCanceledException ? "Model catalog refresh timed out." : error.Message)}")).ConfigureAwait(false);
            return 1;
        }
        await console.Log(console.Green("Model catalogs refreshed")).ConfigureAwait(false);
        return 0;
    }
}
