// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (InteractiveMode: fields,
// constructor, init, run, the startup header, loaded resources, status/error/warning lines, shutdown). The session is driven through
// the in-process RPC host (RpcSessionClient); live host objects come from InteractiveStartup.Host where the protocol has no command.
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;
using TuiKeybindingsManager = PiSharp.Tui.Pi.KeybindingsManager;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Interface for components that can be expanded/collapsed.</summary>
internal interface IExpandable { void SetExpanded(bool expanded); }

/// <summary>ThemedText that switches between collapsed and expanded content.</summary>
internal class ExpandableText : Text, IExpandable
{
    private readonly Func<string> collapsed, expanded;
    private bool isExpanded, stale = true;
    public ExpandableText(Func<string> getCollapsedText, Func<string> getExpandedText, bool expanded = false, int paddingX = 0, int paddingY = 0)
        : base("", paddingX, paddingY)
    { collapsed = getCollapsedText; this.expanded = getExpandedText; isExpanded = expanded; }
    public void SetExpanded(bool value) { isExpanded = value; Invalidate(); }
    public override void Invalidate() { base.Invalidate(); stale = true; }
    public override List<string> Render(int width)
    {
        if (stale) { stale = false; SetText(isExpanded ? expanded() : collapsed()); }
        return base.Render(width);
    }
}

/// <summary>The built-in header. Clicking its logo (the first two lines, after one column of padding) plays an easter egg.</summary>
internal sealed class BuiltInHeader(Func<string> collapsed, Func<string> expanded, bool isExpanded, int paddingX, int paddingY)
    : ExpandableText(collapsed, expanded, isExpanded, paddingX, paddingY), IMouseHandler
{
    public Action<int, int>? OnLogoClick { get; set; }
    public TuiMouseEventResult? HandleMouse(TuiMouseEvent mouseEvent)
    {
        if (mouseEvent.Type != TuiMouseEventType.Click || mouseEvent.Y > 1 || mouseEvent.X < 1 || mouseEvent.X > 4 || OnLogoClick is null) return null;
        OnLogoClick(mouseEvent.ScreenX - mouseEvent.X + 1, mouseEvent.ScreenY - mouseEvent.Y);
        return new TuiMouseEventResult(Handled: true);
    }
}

internal sealed record CompactionQueuedMessage(string Text, string Mode);

internal sealed record InteractiveModeOptions
{
    public IReadOnlyList<string> MigratedProviders { get; init; } = [];
    public IReadOnlyList<(string Type, string Message)> StartupDiagnostics { get; init; } = [];
    public string? ModelFallbackMessage { get; init; }
    public string? InitialMessage { get; init; }
    public JsonArray? InitialImages { get; init; }
    public IReadOnlyList<string> InitialMessages { get; init; } = [];
    public bool Verbose { get; init; }
    public string? TuiMode { get; init; }
    public string? InitialThemeSetting { get; init; }
    public IReadOnlyList<string> DeprecationWarnings { get; init; } = [];
    /// <summary>The session's scoped models (--models, else enabledModels): Ctrl+P cycles them (agent-session.ts scopedModels).</summary>
    public IReadOnlyList<ScopedModel> ScopedModels { get; init; } = [];
}

/// <summary>Pi's interactive TUI mode over the RPC session host.</summary>
internal sealed partial class InteractiveMode
{
    public const string AppName = "pi";
    public const string AppTitle = "π";

    private readonly InteractiveModeOptions options;
    private readonly InteractiveModeContext context;
    private readonly RpcSessionClient rpc;
    /// <summary>The in-process RPC client (tests read the host state through it).</summary>
    internal RpcSessionClient Rpc => rpc;
    private readonly InteractiveSettings settings;
    private TuiBase renderer;
    private readonly TuiReference ui;
    private Container headerContainer, loadedResourcesContainer, chatContainer, documentContainer, pendingMessagesContainer, statusContainer;
    private Container widgetContainerAbove, widgetContainerBelow, editorContainer, footerContainer;
    private ScrollView? transcriptScrollView;
    private IComponent? fullscreenLayoutRoot;
    private TuiMainScreenRenderState? mainScreenRenderState;
    private readonly CustomEditor defaultEditor;
    private IEditorComponent editor;
    private Func<ITui, EditorTheme, TuiKeybindingsManager, IEditorComponent>? editorComponentFactory;
    private IAutocompleteProvider? autocompleteProvider;
    private readonly List<Func<IAutocompleteProvider, IAutocompleteProvider>> autocompleteProviderWrappers = [];
    private string? fdPath;
    private object? activeSelectorToken;
    private Action? activeSelectorDispose;
    private readonly FooterComponent footer;
    private readonly FooterDataProvider footerDataProvider;
    private readonly AppKeybindingsManager keybindings;
    private readonly string version;
    private bool isInitialized;
    private Action<string>? onInputCallback;
    private readonly Queue<string> pendingUserInputs = new();
    private StatusIndicator? activeStatusIndicator;
    private bool activeWorkingIndicatorEmbedded;
    private readonly IdleStatus idleStatus = new();
    private string? workingMessage;
    private bool workingVisible = true;
    private LoaderIndicatorOptions? workingIndicatorOptions;
    private const string DefaultWorkingMessage = "Working";
    private const string DefaultHiddenThinkingLabel = "Thinking...";
    private string hiddenThinkingLabel = DefaultHiddenThinkingLabel;

    private long lastSigintTime, lastEscapeTime;
    private string? changelogMarkdown;
    private bool startupNoticesShown, anthropicSubscriptionWarningShown;

    private Spacer? lastStatusSpacer;
    private ThemedText? lastStatusText;
    private string lastStatusMessage = "";
    private bool managedToolStatusStarted;

    private AssistantMessageComponent? streamingComponent;
    private readonly HashSet<string> entriesRenderedByBoundaryCompaction = new(StringComparer.Ordinal);
    private JsonObject? streamingMessage;
    private readonly Dictionary<string, ToolExecutionComponent> pendingTools = new(StringComparer.Ordinal);
    private bool toolOutputExpanded;
    private bool hideThinkingBlock;
    private int outputPad = 1;
    private readonly Dictionary<string, string> skillCommands = new(StringComparer.Ordinal);
    private bool isBashMode;
    private BashExecutionComponent? bashComponent;
    private readonly List<BashExecutionComponent> pendingBashComponents = [];
    private Action? autoCompactionEscapeHandler, retryEscapeHandler;
    private List<CompactionQueuedMessage> compactionQueuedMessages = [];
    private bool shutdownRequested;
    /// <summary>ctx.shutdown(): quit once the agent settles.</summary>
    internal void RequestShutdown() { shutdownRequested = true; if (state.IsIdle) Run(() => ShutdownAsync()); }
    private readonly InteractiveProgramStatus programStatus;
    private bool bugReportHintShown, installChangeWarningShown;

    // Extension UI state
    private ExtensionSelectorComponent? extensionSelector;
    private ExtensionInputComponent? extensionInput;
    private ExtensionEditorComponent? extensionEditor;
    private readonly List<ExtensionTerminalInputSubscription> extensionTerminalInputSubscriptions = [];
    private readonly Dictionary<string, IComponent> extensionWidgetsAbove = new(StringComparer.Ordinal), extensionWidgetsBelow = new(StringComparer.Ordinal);
    private IComponent? customFooter, builtInHeader, customHeader;
    private readonly InteractiveThemeController themeController;
    private const int MaxWidgetLines = 10;

    private sealed class ExtensionTerminalInputSubscription(Func<string, TuiInputListenerResult?> handler)
    {
        public Func<string, TuiInputListenerResult?> Handler { get; } = handler;
        public Action Unsubscribe { get; set; } = () => { };
    }

    private InteractiveSettings settingsManager => settings;
    private string Cwd => state.Cwd;

    public InteractiveMode(InteractiveModeContext context, InteractiveModeOptions options)
    {
        this.context = context;
        rpc = context.Rpc;
        settings = context.Settings;
        TerminalImage.SetCapabilityOverrides(settings.TerminalCapabilityOverrides);
        var tuiMode = options.TuiMode ?? settings.TuiMode;
        this.options = options with { TuiMode = tuiMode };
        version = context.Version;
        state = new SessionState(context.Startup.Cwd);
        state.ScopedModels = [.. options.ScopedModels];
        renderer = CreateRenderer(tuiMode, settings.ShowHardwareCursor, context.Terminal);
        ui = new TuiReference(() => renderer);
        ui.ClearOnShrink = settings.ClearOnShrink;
        headerContainer = new Container();
        loadedResourcesContainer = new Container();
        chatContainer = new Container();
        documentContainer = new Container();
        documentContainer.AddChild(headerContainer);
        documentContainer.AddChild(loadedResourcesContainer);
        documentContainer.AddChild(chatContainer);
        pendingMessagesContainer = new Container();
        statusContainer = new Container();
        widgetContainerAbove = new Container();
        widgetContainerBelow = new Container();
        keybindings = AppKeybindings.Create(context.Startup.AgentDir);
        TuiKeybindingsManager.SetGlobal(keybindings.Manager);
        defaultEditor = new CustomEditor(ui, Themes.GetEditorTheme(), keybindings.Manager,
            new CustomEditorOptions(PaddingX: settings.EditorPaddingX, AutocompleteMaxVisible: settings.AutocompleteMaxVisible, EmbedWorkingStatus: true));
        editor = defaultEditor;
        editorContainer = new Container();
        editorContainer.AddChild(editor);
        footerDataProvider = new FooterDataProvider(Cwd);
        state.UsingSubscription = context.UsingSubscription;
        footer = new FooterComponent(state, footerDataProvider);
        footer.SetAutoCompactEnabled(state.AutoCompactionEnabled);
        footerContainer = new Container();
        footerContainer.AddChild(footer);
        hideThinkingBlock = settings.HideThinkingBlock;
        outputPad = settings.OutputPad;
        Themes.SetRegisteredThemes(context.Startup.Resources.Themes.Select(registered => (registered.Name, registered.Path)));
        programStatus = new InteractiveProgramStatus(() => ui.Terminal, () => state.SessionName, AppName);
        themeController = new InteractiveThemeController(ui, () => settings, ShowError, UpdateEditorBorderColor, options.InitialThemeSetting);
        ConnectExtensionHost();
    }

    private TuiBase CreateRenderer(string tuiMode, bool showHardwareCursor, ITerminal terminal) => InteractiveTui.Create(new InteractiveTuiOptions(
        tuiMode, showHardwareCursor, context.Startup.AgentDir, terminal, context.Loop, OnRightClickPaste,
        settings.FullscreenCopyOnSelect, settings.FullscreenWheelScrollLines, context.CopyToClipboard, context.OpenUrl));

    private void OnRightClickPaste() => _ = HandleRightClickPasteAsync();

    // =========================================================================
    // Startup
    // =========================================================================

    private void ShowStartupNoticesIfNeeded()
    {
        if (startupNoticesShown) return;
        startupNoticesShown = true;
        if (changelogMarkdown is null) return;
        if (chatContainer.Children.Count > 0) chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new DynamicBorder());
        if (settings.CollapseChangelog)
        {
            var match = System.Text.RegularExpressions.Regex.Match(changelogMarkdown, @"##\s+\[?(\d+\.\d+\.\d+)\]?");
            var latestVersion = match.Success ? match.Groups[1].Value : version;
            var condensedText = $"Updated to v{latestVersion}. Use {theme.Bold("/changelog")} to view full changelog.";
            chatContainer.AddChild(new Text(condensedText, 1, 0));
        }
        else
        {
            chatContainer.AddChild(new ThemedText(() => theme.Bold(theme.Fg("accent", "What's New")), 1, 0));
            chatContainer.AddChild(new Spacer(1));
            chatContainer.AddChild(new Markdown(TextUtils.JsTrim(changelogMarkdown), 1, 0, GetMarkdownThemeWithSettings()));
            chatContainer.AddChild(new Spacer(1));
        }
        chatContainer.AddChild(new DynamicBorder());
    }

    private void MountInteractiveTui(TuiBase tui, IReadOnlyList<IComponent> components)
    {
        foreach (var component in components) tui.AddChild(component);
        if (tui is IViewportTui viewport)
        {
            if (fullscreenLayoutRoot is null) throw new InvalidOperationException("Fullscreen layout is not initialized");
            viewport.SetLayoutRoot(fullscreenLayoutRoot);
        }
    }

    private void StopInteractiveTui(string fullscreenExitOutput)
    {
        if (renderer.Mode == TuiMode.Fullscreen && fullscreenExitOutput == "transcript")
        {
            while (renderer.HasOverlayEntries) renderer.HideOverlay();
            SwitchTuiMode("regular", false, false);
            renderer.RenderNow();
        }
        ui.Stop(preserveScreen: renderer.Mode == TuiMode.Fullscreen);
    }

    private bool SwitchTuiMode(string mode, bool restoreProgress = true, bool startRenderer = true)
    {
        var previousUi = renderer;
        var currentMode = previousUi.Mode == TuiMode.Fullscreen ? "fullscreen" : "regular";
        if (mode == currentMode) return true;
        if (previousUi.HasOverlayEntries) return false;
        var components = previousUi.Children.ToList();
        var focus = previousUi.FocusedComponent;
        var terminal = previousUi.Terminal;
        var showHardwareCursor = previousUi.ShowHardwareCursor;
        var clearOnShrink = previousUi.ClearOnShrink;
        var onDebug = previousUi.OnDebug;
        if (previousUi is TuiMainScreen main) mainScreenRenderState = main.CaptureRenderState();
        previousUi.Stop(preserveScreen: true);
        previousUi.SetFocus(null);
        previousUi.Clear();
        if (previousUi is IViewportTui previousViewport) previousViewport.SetLayoutRoot(null);
        var nextUi = CreateRenderer(mode, showHardwareCursor, terminal);
        nextUi.ClearOnShrink = clearOnShrink;
        nextUi.OnDebug = onDebug;
        if (nextUi is TuiMainScreen nextMain && mainScreenRenderState is not null) nextMain.RestoreRenderState(mainScreenRenderState);
        renderer = nextUi;
        MountInteractiveTui(nextUi, components);
        nextUi.Invalidate();
        nextUi.SetFocus(focus);
        if (!startRenderer) return true;
        nextUi.Start();
        themeController.RebindTui();
        RebindExtensionTerminalInputListeners();
        if (restoreProgress && settings.ShowTerminalProgress && (state.IsStreaming || state.IsCompacting)) terminal.SetProgress(true);
        return true;
    }

    public async Task InitAsync()
    {
        if (isInitialized) return;
        changelogMarkdown = GetChangelogForDisplay();
        if (state.ScopedModels.Count > 0 && ShouldShowStartupDetails())
        {
            var modelList = string.Join(", ", state.ScopedModels.Select(scoped => SessionEntries.Str(scoped.Model["id"]) + (scoped.ThinkingLevel is { } level ? ":" + level : "")));
            var cycleKeys = keybindings.GetKeys("app.model.cycleForward");
            var cycleHint = cycleKeys.Count > 0 ? theme.Fg("muted", $" ({KeybindingHints.FormatKeyText(string.Join("/", cycleKeys), capitalize: true)} to cycle)") : "";
            context.ConsoleLog(theme.Fg("dim", $"Model scope: {modelList}{cycleHint}"));
        }

        RenderWidgets();
        var viewport = InteractiveTui.CreateChatViewport(documentContainer, pendingMessagesContainer, statusContainer, widgetContainerAbove,
            editorContainer, widgetContainerBelow, footerContainer, ToScrollbar(settings.FullscreenScrollbar),
            text => theme.Fg("scrollbarTrack", text), text => theme.Fg("scrollbarThumb", text), context.Loop);
        transcriptScrollView = viewport.Transcript;
        fullscreenLayoutRoot = viewport.Root;
        MountInteractiveTui(renderer, [documentContainer, pendingMessagesContainer, statusContainer, widgetContainerAbove, editorContainer, widgetContainerBelow, footerContainer]);
        defaultEditor.OnAction("app.clear", HandleCtrlC);
        defaultEditor.OnCtrlD = HandleCtrlD;
        defaultEditor.OnSubmit = HandleStartupSubmit;
        ui.SetFocus(editor);

        ui.Start();
        isInitialized = true;
        programStatus.Report();

        themeController.ApplyFromSettings();
        await themeController.WaitForTerminalColors();

        if (ShouldShowStartupHeader())
        {
            var showDetails = ShouldShowStartupDetails();
            var showLogo = PiLogo.SupportsPiLogo();
            string WithLogo(string hints)
            {
                if (!showLogo) return $"{PiLogo.PiWordmark()} {theme.Fg("dim", "v" + version)}\n{hints}";
                var lines = PiLogo.PiLogoLines();
                return $"{lines[0]} {theme.Fg("dim", "v" + version)}\n{lines[1]} {hints}";
            }
            string Hint(string id, string description) => KeybindingHints.KeyHint(id, description);
            string ExpandedInstructions() => string.Join("\n",
                Hint("app.interrupt", "to interrupt"),
                Hint("app.clear", "to clear"),
                KeybindingHints.RawKeyHint($"{KeybindingHints.KeyText("app.clear")} twice", "to exit"),
                Hint("app.exit", "to exit (empty)"),
                Hint("app.suspend", "to suspend"),
                KeybindingHints.KeyHint("tui.editor.deleteToLineEnd", "to delete to end"),
                Hint("app.thinking.cycle", "to cycle thinking level"),
                KeybindingHints.RawKeyHint($"{KeybindingHints.KeyText("app.model.cycleForward")}/{KeybindingHints.KeyText("app.model.cycleBackward")}", "to cycle models"),
                Hint("app.model.select", "to select model"),
                Hint("app.tools.expand", "to expand tools"),
                Hint("app.thinking.toggle", "to expand thinking"),
                Hint("app.editor.external", "for external editor"),
                KeybindingHints.RawKeyHint("/", "for commands"),
                KeybindingHints.RawKeyHint("!", "to run bash"),
                KeybindingHints.RawKeyHint("!!", "to run bash (no context)"),
                Hint("app.message.followUp", "to queue follow-up"),
                Hint("app.message.dequeue", "to edit all queued messages"),
                Hint("app.clipboard.pasteImage", "to paste files on macOS, images, or text"),
                KeybindingHints.RawKeyHint("drop files", "to attach"));
            string CompactInstructions() => string.Join(theme.Fg("muted", " · "),
                Hint("app.interrupt", "interrupt"),
                KeybindingHints.RawKeyHint($"{KeybindingHints.KeyText("app.clear")}/{KeybindingHints.KeyText("app.exit")}", "clear/exit"),
                KeybindingHints.RawKeyHint("/", "commands"),
                KeybindingHints.RawKeyHint("!", "bash"),
                Hint("app.tools.expand", "more"));
            string CompactOnboarding() => theme.Fg("dim", $"Press {KeybindingHints.KeyText("app.tools.expand")} to show full startup help{(showDetails ? " and loaded resources" : "")}.");
            string Onboarding() => theme.Fg("dim", "Pi can explain its own features and look up its docs. Ask it how to use or extend Pi.");
            var header = new BuiltInHeader(() => $"{WithLogo(CompactInstructions())}\n{CompactOnboarding()}\n\n{Onboarding()}",
                () => $"{WithLogo(ExpandedInstructions())}\n\n{Onboarding()}", GetStartupExpansionState(), 1, 0);
            if (showLogo) header.OnLogoClick = (column, row) => EasterEgg3dLazy.PlayPiLogo3d(renderer, column, row);
            builtInHeader = header;
            headerContainer.AddChild(new Spacer(1));
            headerContainer.AddChild(builtInHeader);
            headerContainer.AddChild(new Spacer(1));
        }
        else
        {
            builtInHeader = new Text("", 0, 0);
            headerContainer.AddChild(builtInHeader);
        }
        ui.RequestRender();

        // The tools manager reports from its download threads: statuses join the loop in order.
        void ToolStatus(string type, string message) => context.Loop.Post(() => ShowManagedToolStatus(type, message));
        var ensured = await Task.WhenAll(context.EnsureTool("fd", ToolStatus), context.EnsureTool("rg", ToolStatus));
        fdPath = ensured[0];

        SetupKeyHandlers();
        SetupEditorSubmitHandler();
        AttachExtensionHost();
        ui.RequestRender();

        await RebindCurrentSessionAsync();
        RenderInitialMessages();

        Themes.OnThemeChange(() => context.Loop.Post(() =>
        {
            ui.Invalidate();
            UpdateEditorBorderColor();
            ui.RequestRender();
        }));
        footerDataProvider.OnBranchChange(() => context.Loop.Post(() => ui.RequestRender()));
        UpdateAvailableProviderCount();
        ui.RenderNow();
        // Flush the completed startup state before loading the remaining syntax grammars (loadAllHighlightLanguages).
        _ = Task.Run(() =>
        {
            try { SyntaxHighlight.LoadAllLanguages(); } catch (Exception) { return; } // Eager languages and plaintext remain available.
            context.Loop.Post(() => { if (!isInitialized) return; ui.Invalidate(); ui.RequestRender(); });
        });
    }

    private static ScrollViewScrollbar ToScrollbar(string value) => value switch
    {
        "always" => ScrollViewScrollbar.Always, "hidden" => ScrollViewScrollbar.Hidden, _ => ScrollViewScrollbar.Auto
    };

    private void UpdateTerminalTitle()
    {
        var cwdBasename = Path.GetFileName(Path.TrimEndingDirectorySeparator(Cwd));
        var sessionName = state.SessionName;
        ui.Terminal.SetTitle(sessionName is not null ? $"{AppTitle} - {sessionName} - {cwdBasename}" : $"{AppTitle} - {cwdBasename}");
    }

    /// <summary>Run the interactive mode: initialize, show warnings, process initial messages, then the input loop.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        await InitAsync();
        if (string.IsNullOrEmpty(context.GetEnvironment("PI_OFFLINE")))
            _ = RefreshCatalogsAtStartupAsync();
        _ = CheckForNewVersionAsync();
        _ = CheckForPackageUpdatesAsync();
        _ = CheckTmuxKeyboardSetupAsync();

        foreach (var (type, message) in options.StartupDiagnostics)
        {
            if (type == "error") ShowError(message);
            else if (type == "warning") ShowWarning(message);
            else ShowStatus(message);
        }
        foreach (var warning in options.DeprecationWarnings) ShowWarning(warning);
        if (options.MigratedProviders.Count > 0) ShowWarning($"Migrated credentials to auth.json: {string.Join(", ", options.MigratedProviders)}");
        if (options.ModelFallbackMessage is { } fallback) ShowWarning(fallback);
        if (context.TakeUnnotifiedCrash() is { } crash)
            ShowWarning($"{AppName} crashed on {crash.When} ({crash.Message}). Run /bug to report it; the crash details are attached automatically.");
        _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync();

        if (options.InitialMessage is { } initialMessage)
        {
            try { await PromptAsync(initialMessage, options.InitialImages); }
            catch (Exception error) { ShowError(error.Message); }
        }
        foreach (var message in options.InitialMessages)
        {
            try { await PromptAsync(message); }
            catch (Exception error) { ShowError(error.Message); }
        }

        while (!token.IsCancellationRequested && !isShuttingDown)
        {
            var userInput = await GetUserInputAsync(token);
            if (userInput is null) break;
            try { await PromptAsync(userInput); }
            catch (Exception error) { ShowError(error.Message); }
        }
    }

    private async Task RefreshCatalogsAtStartupAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(15_000);
            await context.RefreshModelCatalogs(timeout.Token);
            await RefreshAvailableModelsAsync();
            UpdateAvailableProviderCount();
        }
        catch { }
    }

    private async Task CheckForNewVersionAsync()
    {
        try
        {
            if (await context.CheckForNewVersion(version) is { } release) ShowNewVersionNotification(release.Version, release.Note);
        }
        catch { }
    }

    /// <summary>Source checkForPackageUpdates (nothing offline or on failure) and showPackageUpdateNotification.</summary>
    private async Task CheckForPackageUpdatesAsync()
    {
        try
        {
            var updates = string.IsNullOrEmpty(context.GetEnvironment("PI_OFFLINE")) ? await context.CheckForPackageUpdates() : [];
            if (updates.Count > 0) ShowPackageUpdateNotification(updates);
        }
        catch { }
        finally
        {
            // npm can overwrite the shared console title on Windows while checking package versions.
            if (OperatingSystem.IsWindows() && isInitialized) UpdateTerminalTitle();
        }
    }

    private async Task CheckTmuxKeyboardSetupAsync()
    {
        if (string.IsNullOrEmpty(context.GetEnvironment("TMUX"))) return;
        async Task<string?> RunTmuxShow(string option)
        {
            try
            {
                var result = await context.RunProcess("tmux", ["show", "-gv", option], TimeSpan.FromSeconds(2));
                return result.ExitCode == 0 ? TextUtils.JsTrim(result.Stdout) : null;
            }
            catch { return null; }
        }
        var extendedKeys = await RunTmuxShow("extended-keys");
        var extendedKeysFormat = await RunTmuxShow("extended-keys-format");
        if (extendedKeys is null) return;
        if (extendedKeys is not ("on" or "always"))
            ShowWarning("tmux extended-keys is off. Modified Enter keys may not work. Add `set -g extended-keys on` to ~/.tmux.conf and restart tmux.");
        else if (extendedKeysFormat == "xterm")
            ShowWarning("tmux extended-keys-format is xterm. Pi works best with csi-u. Add `set -g extended-keys-format csi-u` to ~/.tmux.conf and restart tmux.");
    }

    /// <summary>Changelog entries to display on startup: only new entries since the last seen version, skipped for resumed sessions.</summary>
    private string? GetChangelogForDisplay()
    {
        if (state.MessageCount > 0) return null;
        var lastVersion = settings.LastChangelogVersion;
        var entries = context.ParseChangelog();
        if (lastVersion is null)
        {
            settings.SetLastChangelogVersion(version);
            context.ReportInstallTelemetry(version, settings);
            return null;
        }
        var newEntries = context.GetNewChangelogEntries(entries, lastVersion);
        if (newEntries.Count > 0)
        {
            settings.SetLastChangelogVersion(version);
            context.ReportInstallTelemetry(version, settings);
            return string.Join("\n\n", newEntries);
        }
        return null;
    }

    private MarkdownTheme GetMarkdownThemeWithSettings() => Themes.GetMarkdownTheme(settings.CodeBlockIndent);

    private bool GetStartupExpansionState() => options.Verbose || toolOutputExpanded;

    /// <summary>Startup header (logo, version, key hints). Hidden only by quietStartup: true.</summary>
    private bool ShouldShowStartupHeader() => options.Verbose || settings.QuietStartup is not true;

    /// <summary>Startup details (model scope, loaded resources). Hidden by quietStartup: true or "header".</summary>
    private bool ShouldShowStartupDetails() => options.Verbose || settings.QuietStartup is false;

    // =========================================================================
    // UI helpers
    // =========================================================================

    public void ClearEditor()
    {
        editor.SetText("");
        ui.RequestRender();
    }

    public void ShowError(string errorMessage)
    {
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => theme.Fg("error", $"Error: {errorMessage}"), outputPad, 0));
        ui.RequestRender();
    }

    public void ShowWarning(string warningMessage)
    {
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => theme.Fg("warning", $"Warning: {warningMessage}"), 1, 0));
        ui.RequestRender();
    }

    /// <summary>A status line; back-to-back status messages update the previous line instead of appending.</summary>
    private void ShowStatus(string message)
    {
        var children = chatContainer.Children;
        var last = children.Count > 0 ? children[^1] : null;
        var secondLast = children.Count > 1 ? children[^2] : null;
        if (last is not null && secondLast is not null && ReferenceEquals(last, lastStatusText) && ReferenceEquals(secondLast, lastStatusSpacer))
        {
            lastStatusMessage = message;
            lastStatusText!.Invalidate();
            ui.RequestRender();
            return;
        }
        var spacer = new Spacer(1);
        lastStatusMessage = message;
        var text = new ThemedText(() => theme.Fg("dim", lastStatusMessage), 1, 0);
        chatContainer.AddChild(spacer);
        chatContainer.AddChild(text);
        lastStatusSpacer = spacer;
        lastStatusText = text;
        ui.RequestRender();
    }

    private void ShowManagedToolStatus(string type, string message)
    {
        if (!managedToolStatusStarted)
        {
            chatContainer.AddChild(new Spacer(1));
            managedToolStatusStarted = true;
        }
        var text = type == "warning" ? $"Warning: {message}" : message;
        var color = type == "warning" ? "warning" : "dim";
        chatContainer.AddChild(new ThemedText(() => theme.Fg(color, text), 1, 0));
        lastStatusSpacer = null;
        lastStatusText = null;
        ui.RequestRender();
    }

    public void ShowNewVersionNotification(string releaseVersion, string? releaseNote)
    {
        string UpdateInstruction() => theme.Fg("muted", $"New version {releaseVersion} is available. Run ") + theme.Fg("accent", $"{AppName} update");
        const string changelogUrl = "https://pi.dev/changelog";
        string ChangelogLine()
        {
            var link = TerminalImage.GetCapabilities().Hyperlinks ? TerminalImage.Hyperlink(theme.Fg("accent", changelogUrl), changelogUrl) : theme.Fg("accent", changelogUrl);
            return theme.Fg("muted", "Changelog: ") + link;
        }
        var note = releaseNote is null ? null : TextUtils.JsTrim(releaseNote);
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new DynamicBorder(text => theme.Fg("warning", text)));
        chatContainer.AddChild(new ThemedText(() => $"{theme.Bold(theme.Fg("warning", "Update Available"))}\n{UpdateInstruction()}", 1, 0));
        if (!string.IsNullOrEmpty(note))
        {
            chatContainer.AddChild(new Spacer(1));
            chatContainer.AddChild(new Markdown(note, 1, 0, GetMarkdownThemeWithSettings(), new DefaultTextStyle(Color: text => theme.Fg("muted", text))));
            chatContainer.AddChild(new Spacer(1));
        }
        chatContainer.AddChild(new ThemedText(ChangelogLine, 1, 0));
        chatContainer.AddChild(new DynamicBorder(text => theme.Fg("warning", text)));
        ui.RequestRender();
    }

    public void ShowPackageUpdateNotification(IReadOnlyList<string> packages)
    {
        string UpdateInstruction() => theme.Fg("muted", "Package updates are available. Run ") + theme.Fg("accent", $"{AppName} update --extensions");
        var packageLines = string.Join("\n", packages.Select(pkg => $"- {pkg}"));
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new DynamicBorder(text => theme.Fg("warning", text)));
        chatContainer.AddChild(new ThemedText(() => $"{theme.Bold(theme.Fg("warning", "Package Updates Available"))}\n{UpdateInstruction()}\n{theme.Fg("muted", "Packages:")}\n{packageLines}", 1, 0));
        chatContainer.AddChild(new DynamicBorder(text => theme.Fg("warning", text)));
        ui.RequestRender();
    }

    private void UpdateEditorBorderColor()
    {
        if (isBashMode) editor.BorderColor = theme.GetBashModeBorderColor();
        else editor.BorderColor = theme.GetThinkingBorderColor(state.ThinkingLevel ?? "off");
        activeStatusIndicator?.Invalidate();
        ui.RequestRender();
    }

    // =========================================================================
    // Shutdown
    // =========================================================================

    private bool isShuttingDown;

    private async Task ShutdownAsync(bool fromSignal = false)
    {
        if (isShuttingDown) return;
        isShuttingDown = true;
        themeController.DisableAutoSync();
        try { await ui.Terminal.DrainInputAsync(1000); } catch { }
        Stop();
        onInputCallback?.Invoke(null!);
        context.RequestExit(fromSignal);
    }

    private async Task CheckShutdownRequestedAsync()
    {
        if (!shutdownRequested) return;
        await ShutdownAsync();
    }

    public void Stop(string? fullscreenExitOutput = null)
    {
        fullscreenExitOutput ??= settings.FullscreenExitOutput;
        DisposeActiveSelector();
        if (settings.ShowTerminalProgress) ui.Terminal.SetProgress(false);
        ClearStatusIndicator();
        themeController.DisableAutoSync();
        ClearExtensionTerminalInputListeners();
        footer.Dispose();
        footerDataProvider.Dispose();
        if (isInitialized)
        {
            StopInteractiveTui(fullscreenExitOutput);
            isInitialized = false;
        }
        Themes.StopThemeWatcher();
    }

    /// <summary>The resume command printed after an interactive quit (formatResumeCommand).</summary>
    public string? FormatResumeCommand()
    {
        if (state.SessionFile is not { } sessionFile || !File.Exists(sessionFile)) return null;
        var args = new List<string> { AppName };
        if (!context.Startup.UsesDefaultSessionDir && context.Startup.SessionDir is { } dir) args.AddRange(["--session-dir", QuoteIfNeeded(dir)]);
        args.AddRange(["--session", state.SessionId ?? ""]);
        return string.Join(" ", args);
    }

    internal static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(value, @"[^a-zA-Z0-9_\-./~:@]")) return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public static string? FormatCrashExtensionHint(IReadOnlyList<string>? extensionMatches)
    {
        var matches = extensionMatches?.Where(match => !string.IsNullOrEmpty(match)).ToList() ?? [];
        if (matches.Count == 0) return null;
        var quoted = matches.Select(match => $"`{match}`").ToList();
        var labels = quoted.Count == 1 ? quoted[0] : quoted.Count == 2 ? string.Join(" and ", quoted) : $"{string.Join(", ", quoted.Take(quoted.Count - 1))}, and {quoted[^1]}";
        var noun = matches.Count == 1 ? "extension" : "extensions";
        var pronoun = matches.Count == 1 ? "it" : "them";
        return $"A stack frame came from loaded {noun} {labels}, which may be involved. Try disabling {pronoun} with `{AppName} config`, or run `{AppName} -ne` to confirm.";
    }
}
