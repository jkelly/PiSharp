// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (showSettingsSelector,
// handleThinkingCommand, showThinkingSelector, handleModelCommand, findExactModelMatch, showTrustSelector, showModelSelector,
// showModelsSelector, showUserMessageSelector, showTreeSelector, showSessionSelector, handleResumeSession).
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed partial class InteractiveMode
{
    public const string DefaultThinkingLevel = "medium";
    public static readonly IReadOnlyList<string> ThinkingLevelOptions = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    private void ShowSettingsSelector()
    {
        ShowSelector(done =>
        {
            SettingsSelectorComponent? selector = null;
            var defaultProvider = settings.DefaultProvider;
            var defaultModelId = settings.DefaultModel;
            var config = new SettingsConfig
            {
                AutoCompact = state.AutoCompactionEnabled,
                DefaultModel = defaultProvider is not null && defaultModelId is not null ? $"{defaultProvider}/{defaultModelId}" : "not set",
                CurrentModel = state.Model,
                AvailableDefaultModels = state.AvailableModels,
                ShowImages = settings.ShowImages,
                ImageWidthCells = settings.ImageWidthCells,
                AutoResizeImages = settings.ImageAutoResize,
                BlockImages = settings.BlockImages,
                EnableSkillCommands = settings.EnableSkillCommands,
                SteeringMode = state.SteeringMode,
                FollowUpMode = state.FollowUpMode,
                Transport = settings.Transport,
                HttpIdleTimeoutMs = settings.HttpIdleTimeoutMs,
                CacheWarmingMode = settings.CacheWarmingMode,
                ThinkingLevel = settings.DefaultThinkingLevel ?? DefaultThinkingLevel,
                AvailableThinkingLevels = ThinkingLevelOptions,
                ModelThinkingLevels = settings.AllModelThinkingLevels,
                CurrentTheme = themeController.GetThemeSelection() ?? Themes.SystemThemeName,
                TerminalTheme = themeController.GetTerminalTheme(),
                AvailableThemes = Themes.GetAvailableThemes(),
                HideThinkingBlock = hideThinkingBlock,
                MermaidRenderingMode = settings.MermaidRenderingMode,
                CollapseChangelog = settings.CollapseChangelog,
                EnableInstallTelemetry = settings.EnableInstallTelemetry,
                DoubleEscapeAction = settings.DoubleEscapeAction,
                TreeFilterMode = settings.TreeFilterMode,
                ShowHardwareCursor = settings.ShowHardwareCursor,
                ShowCacheMissNotices = settings.ShowCacheMissNotices,
                DefaultProjectTrust = settings.DefaultProjectTrust,
                EditorPaddingX = settings.EditorPaddingX,
                OutputPad = settings.OutputPad,
                AutocompleteMaxVisible = settings.AutocompleteMaxVisible,
                QuietStartup = settings.QuietStartup,
                ClearOnShrink = settings.ClearOnShrink,
                ShowTerminalProgress = settings.ShowTerminalProgress,
                TuiMode = ui.Mode == TuiMode.Fullscreen ? "fullscreen" : "regular",
                FullscreenExitOutput = settings.FullscreenExitOutput,
                FullscreenScrollbar = settings.FullscreenScrollbar,
                FullscreenCopyOnSelect = settings.FullscreenCopyOnSelect,
                FullscreenWheelScrollLines = settings.FullscreenWheelScrollLines,
                Warnings = new WarningSettings(settings.WarningAnthropicExtraUsage)
            };
            var callbacks = new SettingsCallbacks
            {
                OnAutoCompactChange = enabled => Run(async () =>
                {
                    await SetAutoCompactionEnabledAsync(enabled);
                    footer.SetAutoCompactEnabled(enabled);
                }),
                OnShowImagesChange = enabled =>
                {
                    settings.SetShowImages(enabled);
                    foreach (var child in chatContainer.Children) if (child is ToolExecutionComponent tool) tool.SetShowImages(enabled);
                },
                OnImageWidthCellsChange = width =>
                {
                    settings.SetImageWidthCells(width);
                    foreach (var child in chatContainer.Children) if (child is ToolExecutionComponent tool) tool.SetImageWidthCells(width);
                },
                OnAutoResizeImagesChange = settings.SetImageAutoResize,
                OnBlockImagesChange = settings.SetBlockImages,
                OnEnableSkillCommandsChange = enabled =>
                {
                    settings.SetEnableSkillCommands(enabled);
                    SetupAutocompleteProvider();
                },
                OnSteeringModeChange = mode =>
                {
                    settings.SetSteeringMode(mode); state.SteeringMode = mode;
                    rpc.Post(new JsonObject { ["type"] = "set_steering_mode", ["mode"] = mode });
                },
                OnFollowUpModeChange = mode =>
                {
                    settings.SetFollowUpMode(mode); state.FollowUpMode = mode;
                    rpc.Post(new JsonObject { ["type"] = "set_follow_up_mode", ["mode"] = mode });
                },
                OnTransportChange = settings.SetTransport,
                OnHttpIdleTimeoutMsChange = timeoutMs =>
                {
                    settings.SetHttpIdleTimeoutMs(timeoutMs);
                    ShowStatus($"HTTP idle timeout: {HttpIdleTimeoutChoices.Format(timeoutMs)}");
                },
                OnCacheWarmingModeChange = mode =>
                {
                    settings.SetCacheWarmingMode(mode);
                    context.SetCacheWarmingMode?.Invoke(mode);
                    ShowStatus($"Cache warming: {mode}");
                },
                OnModelThinkingLevelChange = (provider, modelId, level) =>
                {
                    settings.SetModelThinkingLevel(provider, modelId, level);
                    if (state.Model is { } current && S(current["provider"]) == provider && S(current["id"]) == modelId)
                        Run(async () => { await SetThinkingLevelAsync(level, persist: false); footer.Invalidate(); UpdateEditorBorderColor(); });
                },
                OnModelThinkingLevelRemove = (provider, modelId) =>
                {
                    settings.RemoveModelThinkingLevel(provider, modelId);
                    if (state.Model is { } current && S(current["provider"]) == provider && S(current["id"]) == modelId)
                    {
                        var globalDefault = settings.DefaultThinkingLevel ?? DefaultThinkingLevel;
                        Run(async () => { await SetThinkingLevelAsync(globalDefault, persist: false); footer.Invalidate(); UpdateEditorBorderColor(); });
                    }
                },
                OnThemeChange = themeSetting =>
                {
                    settings.SetTheme(themeSetting);
                    themeController.SetThemeSetting(themeSetting);
                },
                OnThemePreview = themeController.Preview,
                OnHideThinkingBlockChange = hidden =>
                {
                    hideThinkingBlock = hidden;
                    settings.SetHideThinkingBlock(hidden);
                    UpdateThinkingBlockVisibility();
                },
                OnMermaidRenderingModeChange = mode =>
                {
                    settings.SetMermaidRenderingMode(mode);
                    chatContainer.Invalidate();
                    ui.RequestRender();
                },
                OnShowCacheMissNoticesChange = shown =>
                {
                    settings.SetShowCacheMissNotices(shown);
                    RebuildChatFromMessages();
                },
                OnCollapseChangelogChange = settings.SetCollapseChangelog,
                OnEnableInstallTelemetryChange = settings.SetEnableInstallTelemetry,
                OnQuietStartupChange = settings.SetQuietStartup,
                OnDefaultProjectTrustChange = settings.SetDefaultProjectTrust,
                OnDoubleEscapeActionChange = settings.SetDoubleEscapeAction,
                OnTreeFilterModeChange = settings.SetTreeFilterMode,
                OnShowHardwareCursorChange = enabled =>
                {
                    settings.SetShowHardwareCursor(enabled);
                    ui.ShowHardwareCursor = enabled;
                },
                OnEditorPaddingXChange = padding =>
                {
                    settings.SetEditorPaddingX(padding);
                    defaultEditor.SetPaddingX(padding);
                    if (!ReferenceEquals(editor, defaultEditor)) editor.SetPaddingX(padding);
                },
                OnOutputPadChange = padding =>
                {
                    settings.SetOutputPad(padding);
                    outputPad = padding;
                    foreach (var container in new[] { chatContainer, pendingMessagesContainer })
                        foreach (var child in container.Children) SetOutputPadIfSupported(child, padding);
                    ui.RequestRender();
                },
                OnAutocompleteMaxVisibleChange = maxVisible =>
                {
                    settings.SetAutocompleteMaxVisible(maxVisible);
                    defaultEditor.SetAutocompleteMaxVisible(maxVisible);
                    if (!ReferenceEquals(editor, defaultEditor)) editor.SetAutocompleteMaxVisible(maxVisible);
                },
                OnClearOnShrinkChange = enabled =>
                {
                    settings.SetClearOnShrink(enabled);
                    ui.ClearOnShrink = enabled;
                    if (!enabled && activeStatusIndicator is null) statusContainer.Clear();
                },
                OnShowTerminalProgressChange = settings.SetShowTerminalProgress,
                OnTuiModeChange = mode =>
                {
                    if (!SwitchTuiMode(mode))
                    {
                        selector?.GetSettingsList().UpdateValue("tui-mode", ui.Mode == TuiMode.Fullscreen ? "fullscreen" : "regular");
                        ShowStatus("Close active overlays before changing TUI mode");
                        return;
                    }
                    settings.SetTuiMode(mode);
                    if (activeStatusIndicator is null) statusContainer.Clear();
                    ShowStatus($"TUI mode: {mode}");
                },
                OnFullscreenExitOutputChange = settings.SetFullscreenExitOutput,
                OnFullscreenScrollbarChange = mode =>
                {
                    settings.SetFullscreenScrollbar(mode);
                    transcriptScrollView?.SetScrollbar(ToScrollbar(mode));
                },
                OnFullscreenCopyOnSelectChange = enabled =>
                {
                    settings.SetFullscreenCopyOnSelect(enabled);
                    if (renderer is TuiAltScreen alt) alt.CopyOnSelect = enabled;
                },
                OnFullscreenWheelScrollLinesChange = lines =>
                {
                    settings.SetFullscreenWheelScrollLines(lines);
                    if (renderer is TuiAltScreen alt) alt.SetWheelScrollLines(lines);
                },
                OnWarningsChange = warnings => settings.SetWarningAnthropicExtraUsage(warnings.AnthropicExtraUsage ?? true),
                OnCancel = () => { done(); ui.RequestRender(); }
            };
            selector = new SettingsSelectorComponent(config, callbacks);
            return (selector, selector.GetSettingsList(), null);
        });
    }

    private static void SetOutputPadIfSupported(IComponent child, int padding)
    {
        switch (child)
        {
            case AssistantMessageComponent c: c.SetOutputPad(padding); break;
            case UserMessageComponent c: c.SetOutputPad(padding); break;
            case CustomMessageComponent c: c.SetOutputPad(padding); break;
            case CustomEntryComponent c: c.SetOutputPad(padding); break;
            case ToolExecutionComponent c: c.SetOutputPad(padding); break;
            case BashExecutionComponent c: c.SetOutputPad(padding); break;
        }
    }

    private async Task HandleThinkingCommandAsync(string? searchTerm)
    {
        await RefreshThinkingLevelsAsync();
        var availableLevels = state.AvailableThinkingLevels;
        if (string.IsNullOrEmpty(searchTerm)) { ShowThinkingSelector(); return; }
        var normalized = TextUtils.JsTrim(searchTerm).ToLowerInvariant();
        var level = availableLevels.FirstOrDefault(candidate => candidate.ToLowerInvariant() == normalized);
        if (level is null)
        {
            ShowError($"Unknown thinking level \"{searchTerm}\". Available levels: {string.Join(", ", availableLevels)}.");
            return;
        }
        await SelectThinkingLevelAsync(level, persist: false);
    }

    private async Task SelectThinkingLevelAsync(string level, bool persist)
    {
        try
        {
            await SetThinkingLevelAsync(level, persist);
            footer.Invalidate();
            UpdateEditorBorderColor();
            ShowStatus(persist ? $"Default thinking level: {level}" : $"Thinking level: {level}");
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void ShowThinkingSelector()
    {
        ShowSelector(done =>
        {
            void SelectLevel(string level, bool persist) { Run(() => SelectThinkingLevelAsync(level, persist)); done(); }
            var selector = new ThinkingSelectorComponent(state.ThinkingLevel ?? DefaultThinkingLevel, state.AvailableThinkingLevels,
                level => SelectLevel(level, false), () => { done(); ui.RequestRender(); }, level => SelectLevel(level, true),
                settings.DefaultThinkingLevel ?? DefaultThinkingLevel);
            return (selector, selector, null);
        });
    }

    private async Task HandleModelCommandAsync(string? searchTerm)
    {
        if (string.IsNullOrEmpty(searchTerm)) { ShowModelSelector(); return; }
        var model = await FindExactModelMatchAsync(searchTerm);
        if (model is not null)
        {
            try
            {
                await SetModelAsync(model, persist: false);
                footer.Invalidate();
                UpdateEditorBorderColor();
                ShowStatus($"Model: {S(model["id"])}");
                _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
            }
            catch (Exception error) { ShowError(error.Message); }
            return;
        }
        ShowModelSelector(searchTerm);
    }

    private async Task<JsonObject?> FindExactModelMatchAsync(string searchTerm)
    {
        var cached = state.ScopedModels.Count > 0 ? state.ScopedModels.Select(scoped => scoped.Model).ToList() : state.AvailableModels.ToList();
        var match = ModelResolverMatch.FindExactModelReferenceMatch(searchTerm, cached);
        if (match is not null || state.ScopedModels.Count > 0) return match;
        ShowStatus("Refreshing model catalogs…");
        using var timeout = new CancellationTokenSource(15_000);
        try
        {
            var result = await context.RefreshModelCatalogs(timeout.Token);
            if (result.Aborted && timeout.IsCancellationRequested) ShowWarning("Model refresh timed out; searching cached models.");
            else if (result.Errors.Count > 0) ShowWarning($"Could not refresh {string.Join(", ", result.Errors.Select(error => error.Key))}; searching cached models.");
        }
        catch (Exception error)
        {
            ShowWarning(timeout.IsCancellationRequested ? "Model refresh timed out; searching cached models." : $"Could not refresh model catalogs: {error.Message}");
        }
        await RefreshAvailableModelsAsync();
        return ModelResolverMatch.FindExactModelReferenceMatch(searchTerm, state.AvailableModels);
    }

    private void ShowTrustSelector()
    {
        var cwd = Cwd;
        var savedDecision = context.GetTrustEntry(cwd);
        ShowSelector(done =>
        {
            var selector = new TrustSelectorComponent(new TrustSelectorOptions(cwd, savedDecision, settings.IsProjectTrusted,
                selection =>
                {
                    context.SaveTrustDecisions(selection.Updates);
                    done();
                    ShowStatus($"Saved trust decision: {(selection.Trusted ? "trusted" : "untrusted")}. Restart {AppName} for this to take effect.");
                },
                () => { done(); ui.RequestRender(); }));
            return (selector, selector, null);
        });
    }

    private void ShowModelSelector(string? initialSearchInput = null)
    {
        ShowSelector(done =>
        {
            void SelectModel(JsonObject model, bool persist) => Run(async () =>
            {
                try
                {
                    await SetModelAsync(model, persist);
                    UpdateAvailableProviderCount();
                    footer.Invalidate();
                    UpdateEditorBorderColor();
                    done();
                    ShowStatus(persist ? $"Default model: {S(model["provider"])}/{S(model["id"])}" : $"Model: {S(model["id"])}");
                    _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
                }
                catch (Exception error)
                {
                    done();
                    ShowError(error.Message);
                }
            });
            var defaultProvider = settings.DefaultProvider;
            var defaultModel = settings.DefaultModel;
            var selector = new ModelSelectorComponent(ui, state.Model, new ModelRuntimeView(this), state.ScopedModels.Select(scoped => new ScopedModelItem(scoped.Model, scoped.ThinkingLevel)).ToList(),
                model => SelectModel(model, false), () => { done(); ui.RequestRender(); }, initialSearchInput, model => SelectModel(model, true),
                defaultProvider is not null && defaultModel is not null ? new DefaultModelReference(defaultProvider, defaultModel) : null);
            return (selector, selector, selector.Dispose);
        });
    }

    /// <summary>The model runtime the selectors read: the host's admitted models, refreshed through the catalog refresh.</summary>
    private sealed class ModelRuntimeView(InteractiveMode mode) : IModelSelectorRuntime
    {
        public IReadOnlyList<JsonObject> GetAvailableSnapshot() => mode.state.AvailableModels;
        public JsonObject? GetModel(string provider, string modelId) => mode.FindModel(provider, modelId);
        public string? GetError() => mode.context.GetModelsJsonError?.Invoke();
        public async Task<ModelsRefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            var result = await mode.context.RefreshModelCatalogs(cancellationToken);
            await mode.RefreshAvailableModelsAsync();
            return result;
        }
    }

    private void ShowModelsSelector()
    {
        var availableModels = state.AvailableModels.ToList();
        var availableModelIds = new HashSet<string>(availableModels.Select(ModelKey), StringComparer.Ordinal);
        var configuredPatterns = settings.EnabledModels;
        var sessionScoped = state.ScopedModels.ToList();
        List<string>? ConfiguredEnabledIds(IReadOnlyList<JsonObject> models)
        {
            if (configuredPatterns is not { Count: > 0 }) return null;
            var resolved = ModelResolverMatch.ResolveModelScopeFromModels(configuredPatterns, models);
            var ids = resolved.ScopedModels.Select(scoped => ModelKey(scoped.Model)).ToList();
            foreach (var pattern in resolved.UnmatchedPatterns) if (!ids.Contains(pattern)) ids.Add(pattern);
            return ids;
        }
        var currentEnabledIds = sessionScoped.Count > 0 ? sessionScoped.Select(scoped => ModelKey(scoped.Model)).ToList() : ConfiguredEnabledIds(availableModels);
        var selectionChanged = false;
        void UpdateSessionModels(IReadOnlyList<string>? enabledIds)
        {
            currentEnabledIds = enabledIds?.ToList();
            var hasEnabledAvailable = enabledIds?.Any(availableModelIds.Contains) ?? false;
            var allEnabled = enabledIds is not null && availableModelIds.All(enabledIds.Contains);
            state.ScopedModels = enabledIds is not null && hasEnabledAvailable && !allEnabled
                ? [.. ModelResolverMatch.ResolveModelScopeFromModels(enabledIds, availableModels).ScopedModels]
                : [];
            UpdateAvailableProviderCount();
            ui.RequestRender();
        }
        ShowSelector(done =>
        {
            var disposed = false;
            var timeout = new CancellationTokenSource(15_000);
            ScopedModelsSelectorComponent? selector = null;
            selector = new ScopedModelsSelectorComponent(new ModelsConfig(availableModels, currentEnabledIds, "Refreshing model catalogs…"),
                new ModelsCallbacks(
                    enabledIds => { selectionChanged = true; UpdateSessionModels(enabledIds); },
                    enabledIds =>
                    {
                        var allEnabled = enabledIds is not null && enabledIds.Count == availableModels.Count && enabledIds.All(availableModelIds.Contains);
                        settings.SetEnabledModels(enabledIds is null || allEnabled ? null : enabledIds.ToList());
                        ShowStatus("Model selection saved to settings");
                    },
                    () => { done(); ui.RequestRender(); }));
            _ = RefreshAsync();
            async Task RefreshAsync()
            {
                try
                {
                    var result = await context.RefreshModelCatalogs(timeout.Token);
                    await RefreshAvailableModelsAsync();
                    if (disposed) return;
                    availableModels = state.AvailableModels.ToList();
                    availableModelIds = new HashSet<string>(availableModels.Select(ModelKey), StringComparer.Ordinal);
                    if (!selectionChanged && sessionScoped.Count == 0)
                    {
                        currentEnabledIds = ConfiguredEnabledIds(availableModels);
                        selector!.UpdateModels(availableModels, currentEnabledIds, updateEnabled: true);
                    }
                    else selector!.UpdateModels(availableModels);
                    if (currentEnabledIds is not null) UpdateSessionModels(currentEnabledIds);
                    if (result.Aborted && timeout.IsCancellationRequested) selector.SetRefreshStatus("Model refresh timed out; showing cached models.", "warning");
                    else if (result.Errors.Count > 0) selector.SetRefreshStatus($"Could not refresh {string.Join(", ", result.Errors.Select(error => error.Key))}; showing cached models.", "warning");
                    else selector.SetRefreshStatus("Model catalogs refreshed.", "success");
                    ui.RequestRender();
                }
                catch (Exception error)
                {
                    if (disposed) return;
                    selector!.SetRefreshStatus(timeout.IsCancellationRequested ? "Model refresh timed out; showing cached models." : $"Could not refresh model catalogs: {error.Message}", "warning");
                    ui.RequestRender();
                }
            }
            return (selector, selector, () => { disposed = true; timeout.Cancel(); timeout.Dispose(); });
        });
    }

    private static string ModelKey(JsonObject model) => $"{S(model["provider"])}/{S(model["id"])}";

    private void ShowUserMessageSelector() => Run(async () =>
    {
        var userMessages = await GetUserMessagesForForkingAsync();
        if (userMessages.Count == 0) { ShowStatus("No messages to fork from"); return; }
        var initialSelectedId = userMessages[^1].EntryId;
        ShowSelector(done =>
        {
            var selector = new UserMessageSelectorComponent(userMessages.Select(message => new UserMessageItem(message.EntryId, message.Text)).ToList(),
                entryId => Run(async () =>
                {
                    done();
                    try
                    {
                        var data = await rpc.RequestAsync(new JsonObject { ["type"] = "fork", ["entryId"] = entryId });
                        if (data is JsonObject result && B(result["cancelled"])) { ui.RequestRender(); return; }
                        editor.SetText(S((data as JsonObject)?["text"]) ?? "");
                        ShowStatus("Forked to new session");
                    }
                    catch (Exception error) { ShowError(error.Message); }
                }),
                () => { done(); ui.RequestRender(); },
                initialSelectedId);
            return (selector, selector.GetMessageList(), null);
        });
    });

    private static FilterMode ToFilterMode(string mode) => mode switch
    {
        "no-tools" => FilterMode.NoTools, "user-only" => FilterMode.UserOnly, "labeled-only" => FilterMode.LabeledOnly, "all" => FilterMode.All, _ => FilterMode.Default
    };

    private static SessionTreeNode ToTreeNode(JsonObject node) => new((node["entry"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject(),
        node["children"] is JsonArray children ? [.. children.OfType<JsonObject>().Select(ToTreeNode)] : [], S(node["label"]), S(node["labelTimestamp"]));

    private void ShowTreeSelector(string? initialSelectedId = null) => Run(async () =>
    {
        var data = await rpc.RequestAsync(new JsonObject { ["type"] = "get_tree" }) as JsonObject;
        var tree = data?["tree"] is JsonArray nodes ? nodes.OfType<JsonObject>().Select(ToTreeNode).ToList() : [];
        var realLeafId = S(data?["leafId"]);
        var initialFilterMode = ToFilterMode(settings.TreeFilterMode);
        if (tree.Count == 0) { ShowStatus("No entries in session"); return; }
        ShowSelector(done =>
        {
            var selector = new TreeSelectorComponent(tree, realLeafId, ui.Terminal.Rows,
                entryId => Run(() => NavigateTreeAsync(entryId, done)),
                () => { done(); ui.RequestRender(); },
                (_, _) => ShowError("Tree labels are not supported by this session host."),
                initialSelectedId, initialFilterMode);
            selector.OnCopy = text => Run(async () =>
            {
                if (string.IsNullOrEmpty(text)) { ShowError("Selected entry has no text to copy"); return; }
                try
                {
                    await context.CopyToClipboard(text);
                    ShowStatus("Copied selected message to clipboard");
                }
                catch (Exception error) { ShowError(error.Message); }
            });
            return (selector, selector, null);
        });
    });

    private async Task NavigateTreeAsync(string entryId, Action done)
    {
        if (entryId == state.LeafId)
        {
            done();
            ShowStatus("Already at this point");
            return;
        }
        done();
        var wantsSummary = false;
        string? customInstructions = null;
        if (!settings.BranchSummarySkipPrompt)
        {
            while (true)
            {
                var choice = await ShowExtensionSelectorAsync("Summarize branch?", ["No summary", "Summarize", "Summarize with custom prompt"]);
                if (choice is null) { ShowTreeSelector(entryId); return; }
                wantsSummary = choice != "No summary";
                if (choice == "Summarize with custom prompt")
                {
                    customInstructions = await ShowExtensionEditorAsync("Custom summarization instructions", null);
                    if (customInstructions is null) continue;
                }
                break;
            }
        }
        if (state.IsStreaming)
        {
            await RestoreQueuedMessagesToEditorAsync();
            await AbortAsync();
        }
        if (state.IsCompacting)
        {
            ShowError("Wait for the current compaction or tree navigation to finish before navigating the session tree.");
            return;
        }
        var showingSummaryIndicator = false;
        var originalOnEscape = defaultEditor.OnEscape;
        if (wantsSummary)
        {
            defaultEditor.OnEscape = AbortBranchSummary;
            chatContainer.AddChild(new Spacer(1));
            ShowStatusIndicator(new BranchSummaryStatusIndicator(ui));
            showingSummaryIndicator = true;
            ui.RequestRender();
        }
        try
        {
            string? editorText = null;
            if (wantsSummary)
            {
                var summary = new JsonObject { ["type"] = "pisharp_branch_summary", ["generation"] = state.Generation, ["targetId"] = entryId };
                if (customInstructions is not null) summary["customInstructions"] = customInstructions;
                if (state.Model?["contextWindow"] is JsonValue window) summary["contextWindow"] = window.DeepClone();
                try { await rpc.RequestAsync(summary); }
                catch (RpcCommandFailedException error) when (error.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    ShowStatus("Branch summarization cancelled");
                    ShowTreeSelector(entryId);
                    return;
                }
            }
            else
            {
                var capture = await rpc.RequestAsync(new JsonObject { ["type"] = "pisharp_capture_navigation", ["mode"] = "tree", ["generation"] = state.Generation }) as JsonObject;
                var viewId = S(capture?["viewId"]) ?? "";
                var generation = capture?["generation"]?.DeepClone() ?? JsonValue.Create(state.Generation);
                var selected = await rpc.RequestAsync(new JsonObject
                {
                    ["type"] = "pisharp_select_navigation", ["mode"] = "tree", ["viewId"] = viewId, ["targetId"] = entryId, ["generation"] = generation
                }) as JsonObject;
                if (S(selected?["disposition"]) is { } disposition && disposition != "Selected")
                {
                    ShowStatus("Navigation cancelled");
                    return;
                }
                editorText = S(selected?["editorText"]);
            }
            await RefreshEntriesAsync();
            await RefreshStateAsync();
            chatContainer.Clear();
            RenderInitialMessages();
            if (editorText is { Length: > 0 } && TextUtils.JsTrim(editor.GetText()).Length == 0) editor.SetText(editorText);
            ShowStatus("Navigated to selected point");
            _ = FlushCompactionQueueAsync(false);
        }
        catch (Exception error) { ShowError(error.Message); }
        finally
        {
            if (showingSummaryIndicator) ClearStatusIndicator(StatusIndicatorKind.BranchSummary);
            defaultEditor.OnEscape = originalOnEscape;
        }
    }

    private void ShowSessionSelector()
    {
        ShowSelector(done =>
        {
            var selector = new SessionSelectorComponent(
                (progress, token) => Task.Run(() => (IReadOnlyList<PiSharp.Cli.Pi.PiSessionInfo>)PiSharp.Cli.Pi.PiSessions.List(Cwd, context.Startup.SessionDir, context.Startup.AgentDir), token),
                (progress, token) => Task.Run(() => (IReadOnlyList<PiSharp.Cli.Pi.PiSessionInfo>)PiSharp.Cli.Pi.PiSessions.ListAll(context.Startup.SessionDir, context.Startup.AgentDir), token),
                sessionPath => Run(async () => { done(); await HandleResumeSessionAsync(sessionPath); }),
                () => { done(); ui.RequestRender(); },
                () => Run(() => ShutdownAsync()),
                () => ui.RequestRender(),
                new SessionSelectorOptions
                {
                    RenameSession = async (sessionFilePath, nextName) =>
                    {
                        var next = TextUtils.JsTrim(nextName ?? "");
                        if (next.Length == 0) return;
                        await context.RenameSessionFile(sessionFilePath, next, sessionFilePath == state.SessionFile ? rpc : null);
                    },
                    ShowRenameHint = true,
                    Keybindings = keybindings.Manager
                },
                state.SessionFile);
            return (selector, selector, null);
        });
    }

    private async Task<bool> HandleResumeSessionAsync(string sessionPath)
    {
        ClearStatusIndicator();
        try
        {
            var data = await rpc.RequestAsync(new JsonObject { ["type"] = "switch_session", ["sessionPath"] = sessionPath });
            if (data is JsonObject result && B(result["cancelled"])) return false;
            ShowStatus("Resumed session");
            return true;
        }
        catch (Exception error)
        {
            ShowError($"Failed to resume session: {error.Message}");
            return false;
        }
    }
}
