// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/main.ts (main, resolveAppMode, createSessionManager,
// resolveSessionPath, validateForkFlags, validateSessionIdFlags, buildSessionOptions, reportDiagnostics, isTruthyEnvFlag),
// packages/coding-agent/src/cli/setup.ts and packages/coding-agent/src/core/output-guard.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Settings;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Resources;
using PiSharp.CodingAgent.Resources.Skills;

namespace PiSharp.Cli.Pi;

internal enum PiAppMode { Interactive, Print, Json, Rpc }

/// <summary>The non-interactive session choice for <c>--resume</c> (IMPL-I replaces it with the picker): the cwd's sessions and all
/// sessions, returning the chosen file or null for none.</summary>
internal delegate Task<string?> PiSessionSelector(ImmutableArray<PiSessionInfo> current, Func<ImmutableArray<PiSessionInfo>> all, CancellationToken cancellationToken);

/// <summary>The process the Pi entry runs in. Production uses the real console, environment and directories; tests substitute them.</summary>
internal sealed record PiHost
{
    internal required string Cwd { get; init; }
    internal required string Home { get; init; }
    internal required Func<string, string?> GetEnvironment { get; init; }
    internal Action<string, string?> SetEnvironment { get; init; } = (_, _) => { };
    /// <summary>Observes the RPC mode's background catalog refresh (tests wait for it).</summary>
    internal Action<Task>? CatalogRefreshStarted { get; init; }
    internal required TextWriter Stdout { get; init; }
    internal required TextWriter Stderr { get; init; }
    internal TextReader Stdin { get; init; } = TextReader.Null;
    internal bool StdinIsTty { get; init; } = true;
    internal bool StdoutIsTty { get; init; } = true;
    internal bool Color { get; init; }
    internal required LiveSessionRuntime LiveRuntime { get; init; }
    internal Func<string, PiSharp.Cli.Mcp.McpSessionHost?> CreateMcpHost { get; init; } = _ => null;
    /// <summary>Raw streams for <c>--mode rpc</c> (the dispatcher owns framing).</summary>
    internal Func<Stream>? OpenRpcInput { get; init; }
    internal Func<Stream>? OpenRpcOutput { get; init; }
    /// <summary>Interactive mode (IMPL-I): runs the terminal frontend over the session the arguments describe.</summary>
    internal Func<string[], PiEntryOptions, CancellationToken, Task<int>>? RunInteractive { get; init; }
    internal PiProjectTrustPrompt? TrustPrompt { get; init; }
    internal PiSessionSelector? SelectSession { get; init; }
    /// <summary>Extensions loaded before project trust is resolved (IMPL-E: user and CLI extensions); their project_trust and
    /// resources_discover handlers take part in the run. Null without extensions.</summary>
    internal Func<string, CancellationToken, Task<PiLoadedExtensions?>>? LoadExtensions { get; init; }
    /// <summary>The non-interactive hosts' termination signals (print, JSON and RPC modes): null in tests and interactive mode.</summary>
    internal Func<ShutdownSignals>? Signals { get; init; }
    /// <summary>The HTTP handler and base URL of rg/fd release downloads (tools-manager.ts; a fake release server in tests).</summary>
    internal Func<HttpMessageHandler>? ToolsHttp { get; init; }
    internal string ToolsReleaseBase { get; init; } = "https://github.com";
    /// <summary>PI_TIMING startup timings (IMPL-G's StartupTimings); disabled by default.</summary>
    internal PiSharp.CodingAgent.Diagnostics.StartupTimings Timings { get; init; } = new(false);
    /// <summary>Source promptConfirm on stdin/stdout.</summary>
    internal Func<string, CancellationToken, Task<bool>>? Confirm { get; init; }
    internal string? ApplicationDirectory { get; init; }
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>An exit decided by the entry after its messages were written.</summary>
internal sealed class PiExit(int code) : Exception { internal int Code { get; } = code; }

/// <summary>The Pi-compatible command line (plain <c>pisharp</c>, <c>-p</c>, <c>--mode json|rpc</c>) over PiSharp's session host.</summary>
internal static class PiCommand
{
    private static readonly string[] PackageCommands = ["install", "remove", "uninstall", "update", "list", "config"];

    /// <summary>Source isTruthyEnvFlag.</summary>
    internal static bool IsTruthyEnvFlag(string? value) =>
        !string.IsNullOrEmpty(value) && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    internal static async Task<int> RunAsync(string[] args, PiHost host, CancellationToken cancellationToken)
    {
        try { return await RunCoreAsync(args, host, cancellationToken).ConfigureAwait(false); }
        catch (PiExitWithMessage exit)
        {
            await Line(host.Stderr, host.Color ? Red + exit.Message + "\u001b[39m" : exit.Message).ConfigureAwait(false);
            await host.Stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return exit.Code;
        }
        catch (PiExit exit)
        {
            await host.Stdout.FlushAsync(CancellationToken.None).ConfigureAwait(false); await host.Stderr.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return exit.Code;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, PiHost host, CancellationToken token)
    {
        var err = host.Stderr; var color = host.Color;
        // cli/setup.ts: child processes see that they run under the agent.
        host.SetEnvironment("PI_CODING_AGENT", "true");
        host.SetEnvironment("AI_AGENT", "pi");
        var offline = args.Contains("--offline") || IsTruthyEnvFlag(host.GetEnvironment("PI_OFFLINE"));
        if (offline) { host.SetEnvironment("PI_OFFLINE", "1"); host.SetEnvironment("PI_SKIP_VERSION_CHECK", "1"); }
        if (args.Length > 0 && args[0] == "auth") return await PiAuthCommand.RunAsync(args, host, token).ConfigureAwait(false);
        if (args.Length > 0 && PackageCommands.Contains(args[0]))
        {
            // Package commands (pi install|remove|update|list|config) belong to IMPL-E.
            await Error($"\"{PiConfig.DisplayName} {args[0]}\" is not available in this PiSharp build yet.").ConfigureAwait(false);
            return 1;
        }

        host.Timings.ResetTimings();
        var parsed = PiArgs.Parse(args);
        if (parsed.Diagnostics.Count > 0)
        {
            foreach (var diagnostic in parsed.Diagnostics)
                await Line(err, Paint(diagnostic.Type == "error" ? Red : Yellow, $"{(diagnostic.Type == "error" ? "Error" : "Warning")}: {diagnostic.Message}")).ConfigureAwait(false);
            if (parsed.Diagnostics.Any(diagnostic => diagnostic.Type == "error")) return 1;
        }
        if (parsed.Version) { await Line(host.Stdout, PiConfig.Version).ConfigureAwait(false); return 0; }
        if (parsed.Export is { } exportPath) return await PiExport.RunAsync(exportPath, parsed.Messages.FirstOrDefault(), host, token).ConfigureAwait(false);

        var appMode = ResolveAppMode(parsed, host.StdinIsTty, host.StdoutIsTty);
        if (parsed.Mode == PiOutputMode.Rpc && parsed.FileArgs.Count > 0) { await Error("@file arguments are not supported in RPC mode").ConfigureAwait(false); return 1; }
        ValidateForkFlags(parsed);
        ValidateSessionIdFlags(parsed);

        host.Timings.Time("parseArgs");
        var cwd = host.Cwd; var home = host.Home;
        var agentDir = PiPaths.AgentDirectory(host.GetEnvironment, home);
        // Source isPlainRuntimeMetadataCommand/takeOverStdout: outside interactive mode (and plain --help/--list-models) console output
        // goes to stderr, so the mode's stdout stays clean.
        var plainMetadata = !parsed.Print && parsed.Mode is null && (parsed.Help || parsed.ListModels is not null);
        var console = appMode == PiAppMode.Interactive || plainMetadata ? host.Stdout : err;
        var migrations = PiMigrations.Run(cwd, agentDir);
        foreach (var message in migrations.Messages) await Line(console, message).ConfigureAwait(false);
        // Source startupSettingsManager: SettingsManager.create(cwd, agentDir) reads the project layer (projectTrusted defaults to true).
        host.Timings.Time("runMigrations");
        var startupSettings = PiSettings.Load(cwd, agentDir, projectTrusted: true);
        var startupDiagnostics = startupSettings.DrainDiagnostics();
        // http-dispatcher.ts applyHttpProxySettings: the global httpProxy fills HTTP_PROXY and HTTPS_PROXY when they are unset.
        if (startupSettings.Global["httpProxy"] is JsonValue proxyValue && proxyValue.TryGetValue<string>(out var proxy) && PiArgs.JsTrim(proxy) is { Length: > 0 } trimmedProxy)
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY" })
                if (host.GetEnvironment(name) is null) host.SetEnvironment(name, trimmedProxy);

        if (parsed.Help)
        {
            await Report(startupDiagnostics).ConfigureAwait(false);
            // main.ts: help prints after the runtime loaded the extensions (no trust prompt in this pass), listing their flags.
            IReadOnlyList<PiExtensionFlag>? helpFlags = null;
            if (host.LoadExtensions is null)
            {
                var helpTrusted = parsed.ProjectTrustOverride ?? (!ProjectTrustStore.HasTrustRequiringProjectResources(cwd, home) || new ProjectTrustStore(agentDir, home).Get(cwd) == true);
                await using var helpRun = await PiExtensionRun.LoadAsync(host, parsed, cwd, agentDir, home, helpTrusted, "print", false, null, token).ConfigureAwait(false);
                helpFlags = helpRun.Host?.Flags;
            }
            await console.WriteAsync((PiHelp.Text(helpFlags, color: color) + "\n").AsMemory(), token).ConfigureAwait(false);
            await console.FlushAsync(token).ConfigureAwait(false);
            return 0;
        }
        if (parsed.ListModels is { } search)
        {
            await Report(startupDiagnostics).ConfigureAwait(false);
            var registry = await host.LiveRuntime.CreateModelRegistryAsync(token).ConfigureAwait(false);
            await PiSharp.Cli.Models.ModelListing.ListAsync(registry, search.Length == 0 ? null : search, console, err, cancellationToken: token).ConfigureAwait(false);
            await console.FlushAsync(token).ConfigureAwait(false);
            return 0;
        }
        if (appMode == PiAppMode.Interactive && parsed.UseTheme is not null) startupSettings.ApplyOverrides(new JsonObject { ["theme"] = parsed.UseTheme });

        var envSessionDir = host.GetEnvironment(PiConfig.EnvSessionDir);
        var sessionDir = (parsed.SessionDir is { } cliDir ? PiPaths.ResolvePath(cliDir, cwd, home) : null) ??
            (!string.IsNullOrEmpty(envSessionDir) ? PiPaths.ResolvePath(envSessionDir, cwd, home) : null) ??
            (startupSettings.SessionDir(home) is { } settingsDir ? PiPaths.ResolvePath(settingsDir, cwd, home) : null);
        var plan = await PiSessionPlanner.PlanAsync(parsed, cwd, sessionDir, agentDir, home, appMode, host, token).ConfigureAwait(false);
        if (plan.SessionFile is not null && plan.Cwd.Length > 0 && !Directory.Exists(plan.Cwd))
        {
            // Source getMissingSessionCwdIssue: the interactive prompt belongs to IMPL-I; without it the run stops as print mode does.
            await Line(err, Paint(Red, PiSessions.MissingCwdError(plan.Cwd, plan.SessionFile, cwd))).ConfigureAwait(false);
            return 1;
        }
        host.Timings.Time("createSessionManager");
        string? sessionName = null;
        if (parsed.Name is not null)
        {
            sessionName = PiArgs.NormalizeSessionName(parsed.Name);
            if (sessionName is null) { await Error("--name requires a non-empty value").ConfigureAwait(false); return 1; }
        }

        // Project trust for the session cwd (source createRuntime): the override, else no trust-requiring resources, else the store,
        // defaultProjectTrust and, with a UI, the prompt.
        var sessionCwd = plan.Cwd;
        var trustStore = new ProjectTrustStore(agentDir, home);
        // resource-loader.ts loadProjectTrustExtensions: user and CLI extensions load before trust (their project_trust handlers vote);
        // project extensions load after a trusted decision (loadFinalExtensionSet). Tests substitute host.LoadExtensions.
        var extensionMode = appMode switch { PiAppMode.Interactive => "tui", PiAppMode.Rpc => "rpc", PiAppMode.Json => "json", _ => "print" };
        await using var extensionRun = host.LoadExtensions is null
            ? await PiExtensionRun.LoadAsync(host, parsed, sessionCwd, agentDir, home, false, extensionMode, appMode is PiAppMode.Interactive or PiAppMode.Rpc,
                appMode == PiAppMode.Interactive ? null : err, token).ConfigureAwait(false)
            : null;
        var extensions = host.LoadExtensions is not null ? await host.LoadExtensions(sessionCwd, token).ConfigureAwait(false)
            : extensionRun?.Host is not null ? await extensionRun.PreSessionAsync(token).ConfigureAwait(false) : null;
        var projectTrustDiagnostics = new List<PiDiagnostic>();
        bool projectTrusted;
        try
        {
            projectTrusted = await PiProjectTrust.ResolveAsync(sessionCwd, home, trustStore, parsed.ProjectTrustOverride, startupSettings.DefaultProjectTrust,
                appMode == PiAppMode.Interactive ? host.TrustPrompt : null, token, extensions is null ? null : (trustCwd, trustToken) =>
                    PiExtensionEvents.ProjectTrustAsync(extensions.Registry, extensions.Snapshot, trustCwd, message =>
                    { projectTrustDiagnostics.Add(new("warning", message)); return ValueTask.CompletedTask; }, trustToken)).ConfigureAwait(false);
        }
        catch (InvalidDataException error) { await Error(error.Message).ConfigureAwait(false); return 1; }
        var settings = PiSettings.Load(sessionCwd, agentDir, projectTrusted);
        if (extensionRun is not null)
        {
            if (projectTrusted) await extensionRun.LoadProjectAsync(settings, token).ConfigureAwait(false);
            await extensionRun.ApplyFlagValuesAsync(parsed.UnknownFlags, token).ConfigureAwait(false);
            if (extensionRun.Host is not null) extensions = await extensionRun.PreSessionAsync(token).ConfigureAwait(false);
        }
        var runtimeDiagnostics = new List<PiDiagnostic>([.. projectTrustDiagnostics, .. settings.DrainDiagnostics(), .. extensionRun?.Diagnostics ?? []]);
        // agent-session.ts extendResourcesFromExtensions: resources_discover adds skill, prompt and theme paths (reason "startup").
        var extensionErrors = new List<string>();
        var discovered = extensions is null ? PiDiscoveredResources.Empty : await PiExtensionEvents.ResourcesDiscoverAsync(extensions.Registry, extensions.Snapshot,
            sessionCwd, "startup", (path, message) => { extensionErrors.Add($"Extension error ({path}): {message}"); return ValueTask.CompletedTask; }, token).ConfigureAwait(false);
        var resources = PiResources.Discover(new(sessionCwd, agentDir, home, settings, projectTrusted)
        {
            CliSkills = [.. parsed.Skills ?? []], CliPromptTemplates = [.. parsed.PromptTemplates ?? []], CliThemes = [.. parsed.Themes ?? []],
            NoSkills = parsed.NoSkills, NoPromptTemplates = parsed.NoPromptTemplates, NoThemes = parsed.NoThemes, NoContextFiles = parsed.NoContextFiles,
            SystemPrompt = parsed.SystemPrompt, AppendSystemPrompt = parsed.AppendSystemPrompt is null ? default : [.. parsed.AppendSystemPrompt]
        });
        resources = PiResources.WithDiscovered(resources, discovered, sessionCwd, home);
        if (appMode != PiAppMode.Interactive) foreach (var line in extensionErrors) await Line(err, line).ConfigureAwait(false);
        runtimeDiagnostics.AddRange(resources.Diagnostics.Where(diagnostic => diagnostic.Message.StartsWith("Warning: ", StringComparison.Ordinal))
            .Select(diagnostic => diagnostic with { Message = diagnostic.Message["Warning: ".Length..] }));
        foreach (var diagnostic in resources.Diagnostics.Where(diagnostic => diagnostic.Type == "error"))
            runtimeDiagnostics.Add(new("warning", diagnostic.Message));

        // Skills: loaded once here for the prompt and the /skill:name commands.
        SkillCliBinding? skills = null;
        if (!resources.SkillPaths.IsEmpty)
            skills = await SkillCliBinding.LoadAsync(new([.. resources.SkillPaths.Select(path => new SkillPathSelection(path.Path)
            {
                Scope = path.Scope switch { "user" => PromptTemplateSourceScope.User, "project" => PromptTemplateSourceScope.Project, _ => PromptTemplateSourceScope.Temporary },
                ReportMissingPath = path.ReportMissing
            })]), token: token).ConfigureAwait(false);
        var prompts = new PromptTemplateCliConfiguration([.. resources.PromptPaths.Select(path => new PromptTemplatePathSelection(path.Path,
            new(path.Path, path.Source, path.Scope switch { "user" => PromptTemplateSourceScope.User, "project" => PromptTemplateSourceScope.Project, _ => PromptTemplateSourceScope.Temporary },
                PromptTemplateSourceOrigin.TopLevel), ReportMissingPath: path.ReportMissing))]);

        // Model (source buildSessionOptions), then the --api-key runtime override for its provider.
        var startupSnapshot = await settings.ToStartupSnapshotAsync(token).ConfigureAwait(false);
        // main.ts configureHttpDispatcher(settingsManager.getHttpIdleTimeoutMs()): provider requests time out when headers or body data
        // stall; an invalid setting stops startup.
        long idleTimeout;
        try { idleTimeout = PiHttpIdleTimeout.FromSettings(settings.Merged); }
        catch (InvalidDataException error) { await Error(error.Message).ConfigureAwait(false); return 1; }
        var runtime = host.LiveRuntime with { CreateHttpHandler = PiHttpIdleTimeout.Wrap(host.LiveRuntime.CreateHttpHandler, idleTimeout) };
        LiveSessionSelection selection;
        try
        {
            selection = await ResolveModelAsync(parsed, startupSnapshot, runtime, plan.HasMessages, runtimeDiagnostics, token).ConfigureAwait(false);
            if (parsed.ApiKey is { } apiKey)
            {
                runtime = WithRuntimeApiKey(runtime, selection.Model.Provider, apiKey);
                selection = await ResolveModelAsync(parsed, startupSnapshot, runtime, plan.HasMessages, [], token).ConfigureAwait(false);
            }
        }
        catch (LiveSessionException) when (parsed.ApiKey is not null && parsed.Model is null && parsed.Models is null)
        {
            // Source: --api-key without a resolved model.
            runtimeDiagnostics.Add(new("error", "--api-key requires a model to be specified via --model, --provider/--model, or --models"));
            await Report(Deduplicate([.. startupDiagnostics, .. runtimeDiagnostics])).ConfigureAwait(false);
            return 1;
        }
        catch (LiveSessionException error) when (error.Code is "NoLiveModel")
        {
            await Report([.. startupDiagnostics, .. runtimeDiagnostics]).ConfigureAwait(false);
            await Line(err, Paint(Red, error.Message)).ConfigureAwait(false);
            return 1;
        }
        catch (LiveSessionException error)
        {
            runtimeDiagnostics.Add(new("error", error.Message));
            await Report(Deduplicate([.. startupDiagnostics, .. runtimeDiagnostics])).ConfigureAwait(false);
            return 1;
        }
        catch (SessionCommandException error)
        {
            runtimeDiagnostics.Add(new("error", error.Message));
            await Report(Deduplicate([.. startupDiagnostics, .. runtimeDiagnostics])).ConfigureAwait(false);
            return 1;
        }

        host.Timings.Time("createAgentSessionRuntime");
        // Piped stdin joins the first message; it also turns interactive mode into print mode.
        string? stdinContent = null;
        if (appMode != PiAppMode.Rpc && !host.StdinIsTty)
        {
            stdinContent = await PiInitialMessage.ReadPipedStdinAsync(host.Stdin, token).ConfigureAwait(false);
            if (stdinContent is not null && appMode == PiAppMode.Interactive) appMode = PiAppMode.Print;
        }
        string? fileText = null; ImmutableArray<PiImageContent> fileImages = [];
        if (parsed.FileArgs.Count > 0)
        {
            try { (fileText, fileImages) = await PiInitialMessage.ProcessFileArgumentsAsync(parsed.FileArgs, cwd, home, autoResizeImages: false, token).ConfigureAwait(false); }
            catch (PiFileArgumentException error) { await Error(error.Message).ConfigureAwait(false); return 1; }
        }
        host.Timings.Time("readPipedStdin");
        var (initialMessage, initialImages) = PiInitialMessage.Build(parsed.Messages, fileText, fileImages, stdinContent);
        host.Timings.Time("prepareInitialMessage");

        var allDiagnostics = Deduplicate([.. startupDiagnostics, .. runtimeDiagnostics]);
        if (appMode != PiAppMode.Interactive) await Report(allDiagnostics).ConfigureAwait(false);
        // main.ts: runtime errors (an extension that failed to load, an unknown extension flag) stop the run in every mode.
        if (extensionRun?.Diagnostics.Any(diagnostic => diagnostic.Type == "error") == true)
        {
            if (appMode == PiAppMode.Interactive) await Report(allDiagnostics).ConfigureAwait(false);
            if (extensionRun.HasLoadErrors) await Line(err, Paint(Yellow, PiExtensionLoading.LoadFailureHint)).ConfigureAwait(false);
            return 1;
        }
        if (IsTruthyEnvFlag(host.GetEnvironment("PI_STARTUP_BENCHMARK")) && appMode != PiAppMode.Interactive)
        { await Error("PI_STARTUP_BENCHMARK only supports interactive mode").ConfigureAwait(false); return 1; }
        var packageDir = host.GetEnvironment("PI_PACKAGE_DIR") is { Length: > 0 } configuredPackage ? PiPaths.NormalizePath(configuredPackage, home) : null;

        var policyName = parsed.ToolPolicy ?? settings.ToolPolicy ?? "pi";
        // Decision 0004 (amended): as in Pi, project trust gates only project-local resources; an untrusted project keeps the pi policy.
        var toolPolicy = policyName == "explicit" ? PiToolPolicy.Explicit : new PiToolPolicy(PiToolPolicyMode.Pi)
        {
            // tools-manager.ts: rg and fd from <agentDir>/bin or PATH, downloaded into <agentDir>/bin on first use.
            Search = new PiToolsManager(Path.Join(agentDir, "bin"), host.GetEnvironment, host.ToolsHttp, host.ToolsReleaseBase),
            ProtectedDirectories = [.. new[] { plan.SessionDirectory, Path.GetDirectoryName(plan.SessionPath) }.OfType<string>().Select(Path.GetFullPath).Distinct(PiPaths.Comparer)],
            ProtectedTrees = [Path.GetFullPath(Path.Join(agentDir, "sessions"))]
        };
        var trustedDirectories = new Dictionary<string, bool>(PiPaths.Comparer) { [Path.GetFullPath(sessionCwd)] = projectTrusted };
        var options = new PiEntryOptions
        {
            ToolPolicy = toolPolicy, Settings = startupSnapshot, Selection = selection, LiveRuntime = runtime,
            SystemPrompt = PiSystemPrompt.Admission(resources, skills?.Resources, host.ApplicationDirectory ?? packageDir),
            HeaderId = plan.HeaderId, HeaderTimestamp = plan.HeaderTimestamp, SessionName = sessionName,
            Skills = skills, PromptTemplates = prompts, ThinkingLevel = parsed.Thinking, ThinkingFromCli = parsed.Thinking is not null,
            InitialMessage = initialMessage, InitialImages = [.. initialImages.Select(image => image.ToJson())], InitialMessages = [.. parsed.Messages],
            Theme = startupSettings.Theme, TuiMode = parsed.TuiMode, Verbose = parsed.Verbose, Themes = resources.Themes,
            // interactive-mode.ts: the header hides only for quietStartup true, the details for true or "header"; --verbose shows both.
            ShowStartupHeader = parsed.Verbose || settings.QuietStartup is not true, ShowStartupDetails = parsed.Verbose || settings.QuietStartup is false,
            MigratedAuthProviders = migrations.MigratedAuthProviders, DeprecationWarnings = migrations.DeprecationWarnings,
            ProjectTrusted = PiProjectTrust.Seam(trustedDirectories), StartupDiagnostics = allDiagnostics,
            ExtensionPaths = [.. (parsed.Extensions ?? []).Select(path => PiPaths.IsLocalPath(path) ? PiPaths.ResolvePath(path, cwd, home) : path)],
            NoExtensions = parsed.NoExtensions, ExtensionFlagValues = parsed.UnknownFlags.ToImmutableDictionary(StringComparer.Ordinal),
            Extensions = extensionRun?.Host, ExtensionMode = extensionMode
        };
        var sessionArgs = SessionArguments(plan, parsed);
        // The project .pi/mcp.json is read only for a trusted project (IMPL-H seam): the run's own trust answer.
        var mcpHost = host.CreateMcpHost(agentDir) is { } createdHost ? createdHost with { IsProjectTrusted = options.ProjectTrusted } : null;
        using var entered = options.Enter();
        // print-mode.ts/rpc-mode.ts registerSignalHandlers: SIGTERM (and SIGHUP off Windows) shut the host down gracefully, then the
        // process exits 143 (129). Interactive mode keeps the terminal's own handling.
        var signals = appMode == PiAppMode.Interactive ? null : host.Signals?.Invoke();
        using var signalled = signals is null ? null : CancellationTokenSource.CreateLinkedTokenSource(token, signals.Token);
        var runToken = signalled?.Token ?? token;
        Func<bool> userShutdown = () => signals?.Received is not null;
        host.Timings.Time("createAgentSession");
        host.Timings.PrintTimings();
        switch (appMode)
        {
            case PiAppMode.Rpc:
            {
                // main.ts: RPC refreshes the model catalogs in the background (15 s, errors ignored) unless offline; interactive mode
                // starts its own refresh after the TUI is up.
                if (!offline) { var refresh = RefreshCatalogsInBackground(runtime); host.CatalogRefreshStarted?.Invoke(refresh); }
                var input = host.OpenRpcInput?.Invoke() ?? throw new InvalidOperationException("RPC standard input is unavailable.");
                var output = host.OpenRpcOutput?.Invoke() ?? throw new InvalidOperationException("RPC standard output is unavailable.");
                await using (input.ConfigureAwait(false))
                {
                    var code = await RpcSessionCommand.RunWithPresentationAsync(["session", "rpc", .. sessionArgs], input, output, err, null!, runToken,
                        userShutdown: userShutdown, mcpHost: mcpHost).ConfigureAwait(false);
                    return signals?.Exit(code) ?? code;
                }
            }
            case PiAppMode.Interactive:
                if (host.RunInteractive is null)
                {
                    await Error("Interactive mode is not available in this host; use -p or --mode json|rpc.").ConfigureAwait(false);
                    return 1;
                }
                return await host.RunInteractive(["session", "terminal", .. sessionArgs], options, token).ConfigureAwait(false);
            default:
            {
                var code = await PiPrintMode.RunAsync(["session", "rpc", .. sessionArgs], appMode == PiAppMode.Json, plan, options, host, mcpHost, runToken, userShutdown).ConfigureAwait(false);
                return signals?.Exit(code) ?? code;
            }
        }

        static Task RefreshCatalogsInBackground(LiveSessionRuntime runtime) => Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                // A registry of its own: the running session's registry is not shared with this refresh.
                var registry = await runtime.CreateModelRegistryAsync(timeout.Token).ConfigureAwait(false);
                await registry.RefreshAsync(allowNetwork: true, force: null, providers: null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception) { } // .catch(() => {})
        });

        async Task Error(string message) => await Line(err, Paint(Red, $"Error: {message}")).ConfigureAwait(false);
        async Task Report(IEnumerable<PiDiagnostic> diagnostics)
        {
            foreach (var diagnostic in diagnostics)
            {
                var prefix = diagnostic.Type == "error" ? "Error: " : diagnostic.Type == "warning" ? "Warning: " : "";
                await Line(err, Paint(diagnostic.Type == "error" ? Red : diagnostic.Type == "warning" ? Yellow : Dim, prefix + diagnostic.Message)).ConfigureAwait(false);
            }
        }
        string Paint(string code, string text) => color ? code + text + "\u001b[39m" : text;
    }

    internal const string Red = "\u001b[31m", Yellow = "\u001b[33m", Dim = "\u001b[2m";
    internal static async Task Line(TextWriter writer, string text)
    {
        await writer.WriteAsync((text + "\n").AsMemory()).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Source resolveAppMode.</summary>
    internal static PiAppMode ResolveAppMode(PiArgs parsed, bool stdinIsTty, bool stdoutIsTty) =>
        parsed.Mode == PiOutputMode.Rpc ? PiAppMode.Rpc : parsed.Mode == PiOutputMode.Json ? PiAppMode.Json :
        parsed.Print || !stdinIsTty || !stdoutIsTty ? PiAppMode.Print : PiAppMode.Interactive;

    /// <summary>Source deduplicateDiagnostics.</summary>
    internal static ImmutableArray<PiDiagnostic> Deduplicate(IEnumerable<PiDiagnostic> diagnostics)
    {
        var seen = new HashSet<(string, string)>();
        return [.. diagnostics.Where(diagnostic => seen.Add((diagnostic.Type, diagnostic.Message)))];
    }

    /// <summary>Source validateForkFlags.</summary>
    private static void ValidateForkFlags(PiArgs parsed)
    {
        if (parsed.Fork is null) return;
        var conflicts = new[] { parsed.Session is not null ? "--session" : null, parsed.Continue ? "--continue" : null,
            parsed.Resume ? "--resume" : null, parsed.NoSession ? "--no-session" : null }.OfType<string>().ToArray();
        if (conflicts.Length > 0) throw Fail($"Error: --fork cannot be combined with {string.Join(", ", conflicts)}");
    }

    /// <summary>Source validateSessionIdFlags.</summary>
    private static void ValidateSessionIdFlags(PiArgs parsed)
    {
        if (parsed.SessionId is null) return;
        var conflicts = new[] { parsed.Session is not null ? "--session" : null, parsed.Continue ? "--continue" : null,
            parsed.Resume ? "--resume" : null }.OfType<string>().ToArray();
        if (conflicts.Length > 0) throw Fail($"Error: --session-id cannot be combined with {string.Join(", ", conflicts)}");
        try { PiSessions.AssertValidSessionId(parsed.SessionId); }
        catch (PiSessionException error) { throw Fail($"Error: {error.Message}"); }
    }

    /// <summary>An exit with a message for standard error, written by <see cref="RunAsync"/>'s caller-visible path.</summary>
    internal static PiExitWithMessage Fail(string message, int code = 1) => new(message, code);

    /// <summary>The session host arguments of a plan: the session file and workspace, the storage mode and the tool flags.</summary>
    private static string[] SessionArguments(PiSessionPlan plan, PiArgs parsed)
    {
        var list = new List<string> { "--session", plan.SessionPath, "--workspace", plan.Cwd, "--live", "--session-mode", plan.Mode };
        if (parsed.Thinking is { } thinking) list.AddRange(["--thinking", thinking]);
        if (parsed.Tools is { } tools) list.AddRange(["--tools", string.Join(",", tools)]);
        if (parsed.ExcludeTools is { } excluded) list.AddRange(["--exclude-tools", string.Join(",", excluded)]);
        if (parsed.NoTools) list.Add("--no-tools");
        if (parsed.NoBuiltinTools) list.Add("--no-builtin-tools");
        if (parsed.NoMcp) list.Add("--no-mcp");
        if (plan.SessionDirectory is { } directory && plan.Mode != "open") list.AddRange(["--session-store", "session-directory=" + directory]);
        return [.. list];
    }

    /// <summary>Source buildSessionOptions over the registry: <c>--provider</c> requires <c>--model</c>; the CLI model, else the scoped
    /// models for a new session, else the saved default, else the first available model. Warnings join the run's diagnostics.</summary>
    private static async Task<LiveSessionSelection> ResolveModelAsync(PiArgs parsed, PiSharp.CodingAgent.Configuration.StartupSettingsSnapshot settings,
        LiveSessionRuntime runtime, bool hasExistingSession, List<PiDiagnostic> diagnostics, CancellationToken token)
    {
        if (parsed.Provider is { } provider && parsed.Model is null)
            throw new LiveSessionException("ProviderRequiresModel", $"--provider requires --model (for example: --provider {provider} --model <pattern>)");
        var request = new SettingsModelSelection(parsed.Provider, parsed.Model, null)
        { ModelPatterns = parsed.Models is { } models ? [.. models] : null, CliThinking = parsed.Thinking, UseModelMaximumTokens = true };
        using var warnings = new StringWriter();
        try { return await request.ResolveAsync(settings, runtime, warnings, hasExistingSession, token).ConfigureAwait(false); }
        finally
        {
            foreach (var line in warnings.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (JsonNode.Parse(line)?["message"]?.GetValue<string>() is { } message) diagnostics.Add(new("warning", message));
        }
    }

    /// <summary>Source modelRuntime.setRuntimeApiKey: the key answers for the provider's API key variables, and the stored auth.json
    /// credentials step aside so the key wins.</summary>
    internal static LiveSessionRuntime WithRuntimeApiKey(LiveSessionRuntime runtime, string provider, string apiKey)
    {
        var names = new HashSet<string>(PiSharp.AI.Catalogs.ProviderEnvironmentKeys.GetApiKeyVariables(provider) ?? [], StringComparer.Ordinal);
        if (provider == "anthropic") names.Add(PiSharp.AI.Catalogs.ProviderEnvironmentKeys.AnthropicApiKey);
        var suppressed = provider == "anthropic"
            ? new HashSet<string>(StringComparer.Ordinal) { PiSharp.AI.Catalogs.ProviderEnvironmentKeys.AnthropicAuthToken, PiSharp.AI.Catalogs.ProviderEnvironmentKeys.AnthropicOAuthToken }
            : [];
        var read = runtime.ReadEnvironment;
        return runtime with { ReadEnvironment = name => names.Contains(name) ? apiKey : suppressed.Contains(name) ? null : read(name), AuthPath = null };
    }
}

/// <summary>An exit whose message the entry writes to standard error.</summary>
internal sealed class PiExitWithMessage(string message, int code) : Exception(message) { internal int Code { get; } = code; }
