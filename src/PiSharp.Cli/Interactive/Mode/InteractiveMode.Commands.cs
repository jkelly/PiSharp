// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (setupKeyHandlers,
// setupEditorSubmitHandler and every built-in slash command handler: /export, /import, /share, /bug, /copy, /name, /session,
// /changelog, /hotkeys, /clone, /new, /compact, /reload, /debug, /arminsayshi, /dementedelves, /quit, ! and !! bash; the key
// handlers for Ctrl+C/D/Z, follow-up, dequeue, thinking and model cycling, tool expansion, external editor and clipboard paste).
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed partial class InteractiveMode
{
    private void Run(Func<Task> action)
    {
        _ = RunCore();
        async Task RunCore()
        {
            try { await action(); }
            catch (Exception error) { ShowError(error.Message); }
        }
    }

    private void SetupKeyHandlers()
    {
        defaultEditor.OnEscape = () =>
        {
            if (state.IsStreaming) Run(() => RestoreQueuedMessagesToEditorAsync(abort: true));
            else if (state.IsBashRunning) AbortBash();
            else if (isBashMode)
            {
                editor.SetText("");
                isBashMode = false;
                UpdateEditorBorderColor();
            }
            else if (TextUtils.JsTrim(editor.GetText()).Length == 0)
            {
                var action = settings.DoubleEscapeAction;
                if (action != "none")
                {
                    var now = Environment.TickCount64;
                    if (now - lastEscapeTime < 500)
                    {
                        if (action == "tree") ShowTreeSelector();
                        else ShowUserMessageSelector();
                        lastEscapeTime = 0;
                    }
                    else lastEscapeTime = now;
                }
            }
        };
        defaultEditor.OnAction("app.clear", HandleCtrlC);
        defaultEditor.OnCtrlD = HandleCtrlD;
        defaultEditor.OnAction("app.suspend", HandleCtrlZ);
        defaultEditor.OnAction("app.thinking.cycle", () => Run(CycleThinkingLevelAsync));
        defaultEditor.OnAction("app.model.cycleForward", () => Run(() => CycleModelAsync("forward")));
        defaultEditor.OnAction("app.model.cycleBackward", () => Run(() => CycleModelAsync("backward")));
        ui.OnDebug = HandleDebugCommand;
        defaultEditor.OnAction("app.model.select", () => ShowModelSelector());
        defaultEditor.OnAction("app.tools.expand", ToggleToolOutputExpansion);
        defaultEditor.OnAction("app.thinking.toggle", ToggleThinkingBlockVisibility);
        defaultEditor.OnAction("app.editor.external", () => Run(HandleOpenExternalEditorAsync));
        defaultEditor.OnAction("app.message.copy", () => Run(() => HandleCopyCommandAsync(flashConfirmation: true, preferSelection: true)));
        defaultEditor.OnAction("app.message.followUp", () => Run(HandleFollowUpAsync));
        defaultEditor.OnAction("app.message.dequeue", () => Run(HandleDequeueAsync));
        defaultEditor.OnAction("app.session.new", () => Run(HandleClearCommandAsync));
        defaultEditor.OnAction("app.session.tree", () => ShowTreeSelector());
        defaultEditor.OnAction("app.session.fork", ShowUserMessageSelector);
        defaultEditor.OnAction("app.session.resume", ShowSessionSelector);
        defaultEditor.OnChange = text =>
        {
            var wasBashMode = isBashMode;
            isBashMode = TextUtils.JsTrimStart(text).StartsWith('!');
            if (wasBashMode != isBashMode) UpdateEditorBorderColor();
        };
        defaultEditor.OnPasteImage = () => Run(HandleClipboardPasteAsync);
    }

    private async Task HandleRightClickPasteAsync()
    {
        var target = renderer.FocusedComponent;
        if (target is not IInputHandler handler) return;
        try
        {
            var text = await context.ReadClipboardText();
            if (string.IsNullOrEmpty(text) || !ReferenceEquals(renderer.FocusedComponent, target)) return;
            context.Loop.Post(() =>
            {
                handler.HandleInput($"\u001b[200~{text}\u001b[201~");
                ui.RequestRender();
            });
        }
        catch { /* Clipboard errors are ignored. */ }
    }

    private async Task HandleClipboardPasteAsync()
    {
        try
        {
            var filePaths = await context.ReadClipboardFilePaths();
            if (filePaths is { Count: > 0 })
            {
                if (filePaths.Any(path => path.Any(char.IsControl))) throw new InvalidOperationException("Clipboard file path contains control characters");
                var paths = isBashMode ? string.Join(" ", filePaths.Select(QuoteIfNeeded)) : string.Join("\n", filePaths);
                var (line, col) = editor is Editor typed ? typed.GetCursor() : (0, 0);
                var currentLine = editor.GetText().Split('\n') is var lines && line < lines.Length ? lines[line] : "";
                var before = col > 0 && col <= currentLine.Length ? currentLine[col - 1].ToString() : "";
                var after = col < currentLine.Length ? currentLine[col].ToString() : "";
                var leadingSpace = before.Length > 0 && !TextUtils.IsJsWhitespace(before[0]) ? " " : "";
                var trailingSpace = after.Length > 0 && !TextUtils.IsJsWhitespace(after[0]) ? " " : "";
                editor.InsertTextAtCursor($"{leadingSpace}{paths}{trailingSpace}");
                ui.RequestRender();
                return;
            }
            if (await context.ReadClipboardImage() is { } image)
            {
                var ext = context.ExtensionForImageMimeType(image.MimeType) ?? "png";
                var filePath = Path.Join(Path.GetTempPath(), $"pi-clipboard-{Guid.NewGuid():D}.{ext}");
                await File.WriteAllBytesAsync(filePath, image.Bytes);
                editor.InsertTextAtCursor(filePath);
                ui.RequestRender();
                return;
            }
            var text = await context.ReadClipboardText();
            if (!string.IsNullOrEmpty(text))
            {
                editor.InsertTextAtCursor(text);
                ui.RequestRender();
            }
        }
        catch (Exception error) { ShowError($"Failed to paste from clipboard: {error.Message}"); }
    }

    private void HandleStartupSubmit(string text)
    {
        editor.SetText(text);
        ShowStatus("Startup is still in progress");
    }

    private void SetupEditorSubmitHandler() => defaultEditor.OnSubmit = text => Run(() => HandleSubmitAsync(text));

    /// <summary>The editor's submit handler: built-in commands, bash, then prompts (queued during compaction, steering while streaming).</summary>
    internal async Task HandleSubmitAsync(string text)
    {
        text = TextUtils.JsTrim(text);
        if (text.Length == 0) return;
        bool Is(string command) => text == command || text.StartsWith(command + " ", StringComparison.Ordinal);

        if (text == "/settings") { ShowSettingsSelector(); editor.SetText(""); return; }
        if (text == "/scoped-models") { editor.SetText(""); ShowModelsSelector(); return; }
        if (Is("/model"))
        {
            var searchTerm = text.StartsWith("/model ", StringComparison.Ordinal) ? TextUtils.JsTrim(text[7..]) : null;
            editor.SetText("");
            await HandleModelCommandAsync(searchTerm);
            return;
        }
        if (Is("/thinking"))
        {
            var searchTerm = text.StartsWith("/thinking ", StringComparison.Ordinal) ? TextUtils.JsTrim(text[10..]) : null;
            editor.SetText("");
            await HandleThinkingCommandAsync(searchTerm);
            return;
        }
        if (Is("/export")) { await HandleExportCommandAsync(text); editor.SetText(""); return; }
        if (Is("/import")) { await HandleImportCommandAsync(text); editor.SetText(""); return; }
        if (text == "/share") { await HandleShareCommandAsync(); editor.SetText(""); return; }
        if (Is("/bug"))
        {
            var hint = TextUtils.JsTrim(text["/bug".Length..]);
            editor.SetText("");
            await HandleBugCommandAsync(hint.Length > 0 ? hint : null);
            return;
        }
        if (text == "/copy") { await HandleCopyCommandAsync(); editor.SetText(""); return; }
        if (Is("/name")) { await HandleNameCommandAsync(text); editor.SetText(""); return; }
        if (text == "/session") { await HandleSessionCommandAsync(); editor.SetText(""); return; }
        if (text == "/changelog") { HandleChangelogCommand(); editor.SetText(""); return; }
        if (text == "/hotkeys") { HandleHotkeysCommand(); editor.SetText(""); return; }
        if (text == "/fork") { ShowUserMessageSelector(); editor.SetText(""); return; }
        if (text == "/clone") { editor.SetText(""); await HandleCloneCommandAsync(); return; }
        if (text == "/tree") { ShowTreeSelector(); editor.SetText(""); return; }
        if (text == "/trust") { ShowTrustSelector(); editor.SetText(""); return; }
        if (Is("/login"))
        {
            var providerRef = text.StartsWith("/login ", StringComparison.Ordinal) ? TextUtils.JsTrim(text[7..]) : null;
            editor.SetText("");
            await HandleLoginCommandAsync(providerRef);
            return;
        }
        if (text == "/logout") { _ = ShowOAuthSelectorAsync("logout"); editor.SetText(""); return; }
        if (text == "/new") { editor.SetText(""); await HandleClearCommandAsync(); return; }
        if (Is("/compact"))
        {
            var customInstructions = text.StartsWith("/compact ", StringComparison.Ordinal) ? TextUtils.JsTrim(text[9..]) : null;
            editor.SetText("");
            await HandleCompactCommandAsync(customInstructions);
            return;
        }
        if (text == "/reload") { editor.SetText(""); await HandleReloadCommandAsync(); return; }
        if (text == "/debug") { HandleDebugCommand(); editor.SetText(""); return; }
        if (text == "/arminsayshi") { HandleArminSaysHi(); editor.SetText(""); return; }
        if (text == "/dementedelves") { HandleDementedDelves(); editor.SetText(""); return; }
        if (text == "/resume") { ShowSessionSelector(); editor.SetText(""); return; }
        if (text == "/quit") { editor.SetText(""); await ShutdownAsync(); return; }
        if (Is("/mcp") && context.Mcp is not null)
        {
            editor.SetText("");
            editor.AddToHistory(text);
            await HandleMcpCommandAsync(TextUtils.JsTrim(text[4..]));
            return;
        }

        if (text.StartsWith('!'))
        {
            var isExcluded = text.StartsWith("!!", StringComparison.Ordinal);
            var command = TextUtils.JsTrim(isExcluded ? text[2..] : text[1..]);
            if (command.Length > 0)
            {
                if (state.IsBashRunning)
                {
                    ShowWarning("A bash command is already running. Press Esc to cancel it first.");
                    editor.SetText(text);
                    return;
                }
                editor.AddToHistory(text);
                await HandleBashCommandAsync(command, isExcluded);
                isBashMode = false;
                UpdateEditorBorderColor();
                return;
            }
        }

        if (state.IsCompacting)
        {
            if (IsExtensionCommand(text))
            {
                editor.AddToHistory(text);
                editor.SetText("");
                await PromptAsync(text);
            }
            else QueueCompactionMessage(text, "steer");
            return;
        }

        if (state.IsStreaming)
        {
            editor.AddToHistory(text);
            editor.SetText("");
            await PromptAsync(text, streamingBehavior: "steer");
            UpdatePendingMessagesDisplay();
            ui.RequestRender();
            return;
        }

        FlushPendingBashComponents();
        if (onInputCallback is not null) onInputCallback(text);
        else pendingUserInputs.Enqueue(text);
        editor.AddToHistory(text);
    }

    // =========================================================================
    // Key handlers
    // =========================================================================

    private void HandleCtrlC()
    {
        var now = Environment.TickCount64;
        if (now - lastSigintTime < 500) Run(() => ShutdownAsync());
        else
        {
            ClearEditor();
            lastSigintTime = now;
        }
    }

    /// <summary>Only called when the editor is empty (enforced by CustomEditor).</summary>
    private void HandleCtrlD() => Run(() => ShutdownAsync());

    private void HandleCtrlZ()
    {
        if (OperatingSystem.IsWindows())
        {
            ShowStatus("Suspend to background is not supported on Windows");
            return;
        }
        context.Suspend(() =>
        {
            ui.Stop();
        }, () =>
        {
            ui.Start();
            ui.RequestRender(force: true);
        });
    }

    private async Task HandleFollowUpAsync()
    {
        var text = TextUtils.JsTrim(editor.GetExpandedText());
        if (text.Length == 0) return;
        if (state.IsCompacting)
        {
            if (IsExtensionCommand(text))
            {
                editor.AddToHistory(text);
                editor.SetText("");
                await PromptAsync(text);
            }
            else QueueCompactionMessage(text, "followUp");
            return;
        }
        if (state.IsStreaming)
        {
            editor.AddToHistory(text);
            editor.SetText("");
            await PromptAsync(text, streamingBehavior: "followUp");
            UpdatePendingMessagesDisplay();
            ui.RequestRender();
        }
        else if (editor.OnSubmit is { } submit)
        {
            editor.SetText("");
            submit(text);
        }
    }

    private async Task HandleDequeueAsync()
    {
        var restored = await RestoreQueuedMessagesToEditorAsync();
        ShowStatus(restored == 0 ? "No queued messages to restore" : $"Restored {restored} queued message{(restored > 1 ? "s" : "")} to editor");
    }

    private async Task CycleThinkingLevelAsync()
    {
        var newLevel = await CycleThinkingLevelRpcAsync();
        if (newLevel is null) ShowStatus("Current model does not support thinking");
        else
        {
            footer.Invalidate();
            UpdateEditorBorderColor();
            ShowStatus($"Thinking level: {newLevel}");
        }
    }

    private async Task CycleModelAsync(string direction)
    {
        try
        {
            var result = await CycleModelRpcAsync(direction);
            if (result is null)
            {
                ShowStatus(state.ScopedModels.Count > 0 ? "Only one model in scope" : "Only one model available");
                return;
            }
            footer.Invalidate();
            UpdateEditorBorderColor();
            var (model, thinkingLevel) = result.Value;
            var reasoning = B(model["reasoning"]);
            var thinkingStr = reasoning && thinkingLevel != "off" ? $" (thinking: {thinkingLevel})" : "";
            ShowStatus($"Switched to {(S(model["name"]) is { Length: > 0 } name ? name : S(model["id"]))}{thinkingStr}");
            _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync(model);
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void ToggleToolOutputExpansion() => SetToolsExpanded(!toolOutputExpanded);

    private void SetToolsExpanded(bool expanded)
    {
        if (expanded == toolOutputExpanded) return;
        toolOutputExpanded = expanded;
        if ((customHeader ?? builtInHeader) is IExpandable header) header.SetExpanded(expanded);
        foreach (var container in new[] { loadedResourcesContainer, chatContainer })
            foreach (var child in container.Children) SetExpandedIfSupported(child, expanded);
        ShowStatus($"Tool output: {(expanded ? "expanded" : "collapsed")}");
    }

    private static void SetExpandedIfSupported(IComponent child, bool expanded)
    {
        switch (child)
        {
            case IExpandable expandable: expandable.SetExpanded(expanded); break;
            case ToolExecutionComponent tool: tool.SetExpanded(expanded); break;
            case CustomMessageComponent custom: custom.SetExpanded(expanded); break;
            case CustomEntryComponent entry: entry.SetExpanded(expanded); break;
            case CompactionSummaryMessageComponent compaction: compaction.SetExpanded(expanded); break;
            case BranchSummaryMessageComponent branch: branch.SetExpanded(expanded); break;
            case SkillInvocationMessageComponent skill: skill.SetExpanded(expanded); break;
            case BashExecutionComponent bash: bash.SetExpanded(expanded); break;
        }
    }

    private void UpdateThinkingBlockVisibility()
    {
        foreach (var child in chatContainer.Children) if (child is AssistantMessageComponent assistant) assistant.SetHideThinkingBlock(hideThinkingBlock);
        ui.RequestRender();
    }

    private void ToggleThinkingBlockVisibility()
    {
        hideThinkingBlock = !hideThinkingBlock;
        settings.SetHideThinkingBlock(hideThinkingBlock);
        UpdateThinkingBlockVisibility();
        ShowStatus($"Thinking blocks: {(hideThinkingBlock ? "hidden" : "visible")}");
    }

    private async Task HandleOpenExternalEditorAsync()
    {
        var editorCommand = settings.ExternalEditorCommand;
        var content = editor.GetExpandedText();
        ui.Stop();
        try
        {
            var result = await ExternalEditor.EditInExternalEditorAsync(editorCommand, content);
            if (result.Status == "complete") editor.SetText(result.Content ?? "");
        }
        finally
        {
            ui.Start();
            ui.RequestRender(force: true);
        }
    }

    // =========================================================================
    // Command handlers
    // =========================================================================

    private async Task HandleReloadCommandAsync()
    {
        if (state.IsStreaming) { ShowWarning("Wait for the current response to finish before reloading."); return; }
        if (state.IsCompacting) { ShowWarning("Wait for compaction to finish before reloading."); return; }
        ResetExtensionUI();
        var reloadBox = new Container();
        Func<string, string> borderColor = text => theme.Fg("border", text);
        reloadBox.AddChild(new DynamicBorder(borderColor));
        reloadBox.AddChild(new Spacer(1));
        reloadBox.AddChild(new ThemedText(() => theme.Fg("muted", "Reloading keybindings, extensions, skills, prompts, themes, and context files..."), 1, 0));
        reloadBox.AddChild(new Spacer(1));
        reloadBox.AddChild(new DynamicBorder(borderColor));
        var previousEditor = editor;
        editorContainer.Clear();
        editorContainer.AddChild(reloadBox);
        ui.SetFocus(reloadBox);
        ui.RequestRender(force: true);
        await Task.Yield();
        void DismissReloadBox(IComponent target)
        {
            editorContainer.Clear();
            editorContainer.AddChild(target);
            ui.SetFocus(target);
            ui.RequestRender();
        }
        var dismissed = false;
        try
        {
            await context.ReloadSession(rpc);
            await settings.ReloadAsync();
            hideThinkingBlock = settings.HideThinkingBlock;
            outputPad = settings.OutputPad;
            await RefreshSessionAsync();
            await RefreshCommandsAsync();
            RebuildChatFromMessages();
            keybindings.Reload();
            if ((customHeader ?? builtInHeader) is IExpandable header) header.SetExpanded(toolOutputExpanded);
            Themes.SetRegisteredThemes(context.Startup.Resources.Themes.Select(registered => (registered.Name, registered.Path)));
            ApplyRuntimeSettings();
            themeController.ApplyFromSettings();
            SetupAutocompleteProvider();
            ShowLoadedResources(force: false, showDiagnosticsWhenQuiet: true);
            ShowStatus("Reloaded keybindings, extensions, skills, prompts, themes, and context files");
            DismissReloadBox(editor);
            dismissed = true;
        }
        catch (Exception error)
        {
            if (!dismissed) DismissReloadBox(previousEditor);
            ShowError($"Reload failed: {error.Message}");
        }
    }

    /// <summary>getPathCommandArgument: the first (optionally quoted) argument of /export or /import.</summary>
    internal static string? GetPathCommandArgument(string text, string command)
    {
        if (text == command || !text.StartsWith(command + " ", StringComparison.Ordinal)) return null;
        var args = TextUtils.JsTrimStart(text[(command.Length + 1)..]);
        if (args.Length == 0) return null;
        var first = args[0];
        if (first is '"' or '\'')
        {
            var closing = args.IndexOf(first, 1);
            return closing < 0 ? null : args[1..closing];
        }
        for (var i = 0; i < args.Length; i++) if (TextUtils.IsJsWhitespace(args[i])) return args[..i];
        return args;
    }

    private async Task HandleExportCommandAsync(string text)
    {
        var outputPath = GetPathCommandArgument(text, "/export");
        try
        {
            if (outputPath?.EndsWith(".jsonl", StringComparison.Ordinal) == true)
            {
                var filePath = await context.ExportToJsonl(outputPath, Cwd);
                ShowStatus($"Session exported to: {filePath}");
            }
            else
            {
                var filePath = await context.ExportToHtml(rpc, outputPath, theme.Name);
                ShowStatus($"Session exported to: {filePath}");
            }
        }
        catch (Exception error) { ShowError($"Failed to export session: {error.Message}"); }
    }

    private async Task HandleImportCommandAsync(string text)
    {
        var inputPath = GetPathCommandArgument(text, "/import");
        if (inputPath is null) { ShowError("Usage: /import <path.jsonl>"); return; }
        var confirmed = await ShowExtensionConfirmAsync("Import session", $"Replace current session with {inputPath}?");
        if (!confirmed) { ShowStatus("Import cancelled"); return; }
        try
        {
            ClearStatusIndicator();
            var resolved = Path.GetFullPath(PiSharp.Cli.Pi.PiPaths.ResolvePath(inputPath, Cwd, context.Startup.Home));
            if (!File.Exists(resolved)) { ShowError($"Failed to import session: File not found: {resolved}"); return; }
            var target = context.ImportSessionFile(resolved);
            var data = await ReplaceSessionAsync(new JsonObject { ["type"] = "switch_session", ["sessionPath"] = target });
            if (data is JsonObject result && B(result["cancelled"])) { ShowStatus("Import cancelled"); return; }
            ShowStatus($"Session imported from: {inputPath}");
        }
        catch (Exception error) { ShowError($"Failed to import session: {error.Message}"); }
    }

    private async Task HandleShareCommandAsync()
    {
        BorderedLoader? loader = null;
        using var abort = new CancellationTokenSource();
        void RestoreEditor()
        {
            loader?.Dispose();
            loader = null;
            editorContainer.Clear();
            editorContainer.AddChild(editor);
            ui.SetFocus(editor);
            ui.RequestRender();
        }
        var shareUi = new PiSharp.CodingAgent.Export.SessionShareUi(
            message => context.Loop.Post(() => ShowStatus(message)),
            message => context.Loop.Post(() => ShowError(message)),
            message => context.Loop.Post(() =>
            {
                loader = new BorderedLoader(ui, theme, message);
                loader.OnAbort = () => { abort.Cancel(); RestoreEditor(); ShowStatus("Share cancelled"); };
                editorContainer.Clear();
                editorContainer.AddChild(loader);
                ui.SetFocus(loader);
                ui.RequestRender();
            }),
            () => context.Loop.Post(RestoreEditor));
        try { await context.ShareSession(shareUi, theme.Name, abort.Token); }
        catch (OperationCanceledException) when (abort.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async Task HandleBugCommandAsync(string? hint)
    {
        try { await context.ReportBug(new BugReportUi(this), hint); }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async Task HandleCopyCommandAsync(bool flashConfirmation = false, bool preferSelection = false)
    {
        if (preferSelection && renderer is TuiAltScreen alt && !alt.CopyOnSelect && alt.HasActiveSelection())
        {
            await alt.CopyActiveSelectionToClipboard();
            return;
        }
        var text = await GetLastAssistantTextAsync();
        if (string.IsNullOrEmpty(text)) { ShowError("No agent messages to copy yet."); return; }
        try
        {
            await context.CopyToClipboard(text);
            if (flashConfirmation && renderer is TuiAltScreen screen) screen.Flash("Copied!");
            else ShowStatus("Copied last agent message to clipboard");
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private async Task HandleNameCommandAsync(string text)
    {
        var name = TextUtils.JsTrim(System.Text.RegularExpressions.Regex.Replace(text, @"^/name\s*", ""));
        if (name.Length == 0)
        {
            var currentName = state.SessionName;
            if (currentName is not null)
            {
                chatContainer.AddChild(new Spacer(1));
                chatContainer.AddChild(new ThemedText(() => theme.Fg("dim", $"Session name: {currentName}"), 1, 0));
            }
            else ShowWarning("Usage: /name <name>");
            ui.RequestRender();
            return;
        }
        await SetSessionNameAsync(name);
        var sessionName = state.SessionName;
        if (sessionName != name)
            ShowWarning($"Session name was normalized from {System.Text.Json.JsonSerializer.Serialize(name)} to {System.Text.Json.JsonSerializer.Serialize(sessionName)}");
        chatContainer.AddChild(new Spacer(1));
        var displayName = sessionName ?? name;
        chatContainer.AddChild(new ThemedText(() => theme.Fg("dim", $"Session name set: {displayName}"), 1, 0));
        ui.RequestRender();
    }

    private static string Locale(double value) => value.ToString("#,0", CultureInfo.GetCultureInfo("en-US"));
    private static string Fixed(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private async Task HandleSessionCommandAsync()
    {
        await RefreshStatsAsync();
        var stats = state.Stats ?? new JsonObject();
        var sessionName = state.SessionName;
        var cacheWaste = context.ComputeCacheWaste?.Invoke(state.Entries) ?? new CacheWaste(0, 0, 0);
        var usageBreakdown = context.GetUsageCostBreakdown?.Invoke(state.Entries) ?? [];
        var cacheWarmingStatus = context.CacheWarmingStatus?.Invoke();
        var cacheWarmingMode = settings.CacheWarmingMode;
        var selectedModelKey = $"{S(state.Model?["provider"])}/{S(state.Model?["id"])}";
        var tokens = stats["tokens"] as JsonObject ?? new JsonObject();
        string RenderInfo()
        {
            var info = new StringBuilder($"{theme.Bold("Session Info")}\n\n");
            if (sessionName is not null) info.Append($"{theme.Fg("dim", "Name:")} {sessionName}\n");
            info.Append($"{theme.Fg("dim", "File:")} {S(stats["sessionFile"]) ?? "In-memory"}\n");
            info.Append($"{theme.Fg("dim", "ID:")} {S(stats["sessionId"]) ?? state.SessionId}\n\n");
            info.Append($"{theme.Bold("Messages")}\n");
            info.Append($"{theme.Fg("dim", "Total:")} {N(stats["totalMessages"]).ToString(CultureInfo.InvariantCulture)}\n");
            info.Append($"{theme.Fg("dim", "User:")} {N(stats["userMessages"]).ToString(CultureInfo.InvariantCulture)}\n");
            info.Append($"{theme.Fg("dim", "Assistant:")} {N(stats["assistantMessages"]).ToString(CultureInfo.InvariantCulture)}\n");
            info.Append($"{theme.Fg("dim", "Tools:")} {N(stats["toolCalls"]).ToString(CultureInfo.InvariantCulture)} calls, {N(stats["toolResults"]).ToString(CultureInfo.InvariantCulture)} results\n\n");
            info.Append($"{theme.Bold("Tokens")}\n");
            double input = N(tokens["input"]), cacheRead = N(tokens["cacheRead"]), cacheWrite = N(tokens["cacheWrite"]);
            var promptTokens = input + cacheRead + cacheWrite;
            info.Append($"{theme.Fg("dim", "Input:")} {Locale(promptTokens)}\n");
            if (promptTokens > 0 && (cacheRead > 0 || cacheWrite > 0))
            {
                var hitRate = theme.Fg("dim", $"({Fixed(cacheRead / promptTokens * 100, 1)}%)");
                info.Append($"  {theme.Fg("dim", "Cached:")} {Locale(cacheRead)} {hitRate}\n");
                var written = cacheWrite > 0 ? $" {theme.Fg("dim", $"({Locale(cacheWrite)} written to cache)")}" : "";
                info.Append($"  {theme.Fg("dim", "Uncached:")} {Locale(input + cacheWrite)}{written}\n");
            }
            info.Append($"{theme.Fg("dim", "Output:")} {Locale(N(tokens["output"]))}\n");
            info.Append($"{theme.Fg("dim", "Total:")} {Locale(N(tokens["total"]))}\n");
            info.Append($"\n{theme.Bold("Cache Warming")}\n");
            info.Append($"{theme.Fg("dim", "Mode:")} {cacheWarmingMode}\n");
            info.Append($"{theme.Fg("dim", "Status:")} {cacheWarmingStatus?.Text ?? "Inactive (cache warming unavailable)"}\n");
            if (cacheWarmingStatus is { EconomicsAvailable: true } decision)
            {
                info.Append($"{theme.Fg("dim", "Cache miss penalty:")} ${Fixed(decision.MissCost, 3)}\n");
                info.Append($"{theme.Fg("dim", "Refresh cost:")} ${Fixed(decision.WarmCost, 3)}\n");
            }
            var cost = N(stats["cost"]);
            if (cost > 0 || cacheWaste.MissedTokens > 0)
            {
                info.Append($"\n{theme.Bold("Cost")}\n");
                info.Append($"{theme.Fg("dim", "Total:")} ${Fixed(cost, 3)}");
                if (usageBreakdown.Count > 1 || usageBreakdown.Count == 1 && usageBreakdown[0].Key != selectedModelKey || usageBreakdown.Count == 0 && false)
                    foreach (var entry in usageBreakdown)
                        info.Append($"\n  {theme.Fg("dim", $"{entry.Key}:")} ${Fixed(entry.Cost, 3)} {theme.Fg("dim", $"({FooterComponent.FormatTokens(entry.Tokens)} tokens)")}");
                if (cacheWaste.MissedTokens > 0)
                {
                    var missLabel = cacheWaste.MissCount == 1 ? "1 miss" : $"{cacheWaste.MissCount} misses";
                    var detail = $"{Locale(cacheWaste.MissedTokens)} tokens, {missLabel}";
                    info.Append(cacheWaste.MissedCost >= 0.0001
                        ? $"\n{theme.Fg("dim", "Cache Re-billed:")} ${Fixed(cacheWaste.MissedCost, 3)} {theme.Fg("dim", $"({detail})")}"
                        : $"\n{theme.Fg("dim", "Cache Re-billed:")} {detail}");
                }
            }
            return info.ToString();
        }
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(RenderInfo, 1, 0));
        ui.RequestRender();
    }

    private void HandleChangelogCommand()
    {
        var allEntries = context.ParseChangelog();
        var markdown = allEntries.Count > 0 ? string.Join("\n\n", allEntries.AsEnumerable().Reverse()) : "No changelog entries found.";
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new DynamicBorder());
        chatContainer.AddChild(new ThemedText(() => theme.Bold(theme.Fg("accent", "What's New")), 1, 0));
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new Markdown(markdown, 1, 1, GetMarkdownThemeWithSettings()));
        chatContainer.AddChild(new DynamicBorder());
        ui.RequestRender();
    }

    private void HandleHotkeysCommand()
    {
        string K(string id) => KeybindingHints.KeyDisplayText(id);
        var hotkeys = $"""

**Navigation**
| Key | Action |
|-----|--------|
| `{K("tui.editor.cursorUp")}` / `{K("tui.editor.cursorDown")}` / `{K("tui.editor.cursorLeft")}` / `{K("tui.editor.cursorRight")}` | Move cursor / browse history |
| `{K("tui.editor.cursorWordLeft")}` / `{K("tui.editor.cursorWordRight")}` | Move by word |
| `{K("tui.editor.cursorLineStart")}` | Start of line |
| `{K("tui.editor.cursorLineEnd")}` | End of line |
| `{K("tui.editor.jumpForward")}` | Jump forward to character |
| `{K("tui.editor.jumpBackward")}` | Jump backward to character |
| `{K("tui.editor.pageUp")}` / `{K("tui.editor.pageDown")}` | Scroll by page |

**Editing**
| Key | Action |
|-----|--------|
| `{K("tui.input.submit")}` | Send message |
| `{K("tui.input.newLine")}` | New line{(OperatingSystem.IsWindows() ? " (Ctrl+Enter on Windows Terminal)" : "")} |
| `{K("tui.editor.deleteWordBackward")}` | Delete word backwards |
| `{K("tui.editor.deleteWordForward")}` | Delete word forwards |
| `{K("tui.editor.deleteToLineStart")}` | Delete to start of line |
| `{K("tui.editor.deleteToLineEnd")}` | Delete to end of line |
| `{K("tui.editor.yank")}` | Paste the most-recently-deleted text |
| `{K("tui.editor.yankPop")}` | Cycle through the deleted text after pasting |
| `{K("tui.editor.undo")}` | Undo |

**Other**
| Key | Action |
|-----|--------|
| `{K("tui.input.tab")}` | Path completion / accept autocomplete |
| `{K("app.interrupt")}` | Cancel autocomplete / abort streaming |
| `{K("app.clear")}` | Clear editor (first) / exit (second) |
| `{K("app.exit")}` | Exit (when editor is empty) |
| `{K("app.suspend")}` | Suspend to background |
| `{K("app.thinking.cycle")}` | Cycle thinking level |
| `{K("app.model.cycleForward")}` / `{K("app.model.cycleBackward")}` | Cycle models |
| `{K("app.model.select")}` | Open model selector |
| `{K("app.tools.expand")}` | Toggle tool output expansion |
| `{K("app.thinking.toggle")}` | Toggle thinking block visibility |
| `{K("app.editor.external")}` | Edit message in external editor |
| `{K("app.message.copy")}` | Copy selection or last assistant message |
| `{K("app.message.followUp")}` | Queue follow-up message |
| `{K("app.message.dequeue")}` | Restore queued messages |
| `{K("app.clipboard.pasteImage")}` | Paste files on macOS, images, or text from clipboard |
| `/` | Slash commands |
| `!` | Run bash command |
| `!!` | Run bash command (excluded from context) |

""".Replace("\r\n", "\n", StringComparison.Ordinal);
        var shortcuts = context.GetExtensionShortcuts?.Invoke(keybindings.GetEffectiveConfig()) ?? [];
        if (shortcuts.Count > 0)
        {
            hotkeys += "\n**Extensions**\n| Key | Action |\n|-----|--------|\n";
            foreach (var shortcut in shortcuts)
                hotkeys += $"| `{KeybindingHints.FormatKeyText(shortcut.Key, capitalize: true)}` | {shortcut.Description ?? shortcut.ExtensionPath} |\n";
        }
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new DynamicBorder());
        chatContainer.AddChild(new ThemedText(() => theme.Bold(theme.Fg("accent", "Keyboard Shortcuts")), 1, 0));
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new Markdown(TextUtils.JsTrim(hotkeys), 1, 1, GetMarkdownThemeWithSettings()));
        chatContainer.AddChild(new DynamicBorder());
        ui.RequestRender();
    }

    private async Task HandleClearCommandAsync()
    {
        ClearStatusIndicator();
        try
        {
            var data = await ReplaceSessionAsync(new JsonObject { ["type"] = "new_session" });
            if (data is JsonObject result && B(result["cancelled"])) return;
            chatContainer.AddChild(new Spacer(1));
            chatContainer.AddChild(new ThemedText(() => theme.Fg("accent", "✓ New session started"), 1, 1));
            ui.RequestRender();
        }
        catch (Exception error) { HandleFatalRuntimeError("Failed to create session", error); }
    }

    private void HandleFatalRuntimeError(string prefix, Exception error)
    {
        ShowError($"{prefix}: {error.Message}");
        if (context.RecordCrash("fatal_error", error, state.SessionFile, Cwd))
            chatContainer.AddChild(new ThemedText(() => theme.Fg("muted", CrashReportInstructions()), outputPad, 0));
        Stop("transcript");
        context.RequestExit(false, 1);
    }

    private string CrashReportInstructions()
    {
        var resume = state.SessionFile is not null ? $"run `{AppName} -r` to resume the session, then" : "start pi and";
        return $"To report this crash: {resume} run /bug. The crash details are attached automatically.";
    }

    private void HandleDebugCommand()
    {
        var width = ui.Terminal.Columns;
        var height = ui.Terminal.Rows;
        var allLines = renderer.Render(width);
        var debugLogPath = Path.Join(context.Startup.AgentDir, "pi-debug.log");
        var data = new List<string>
        {
            $"Debug output at {DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}",
            $"Terminal: {width}x{height}",
            $"Total lines: {allLines.Count}",
            "",
            "=== All rendered lines with visible widths ==="
        };
        for (var i = 0; i < allLines.Count; i++)
            data.Add($"[{i}] (w={TextUtils.VisibleWidth(allLines[i])}) {System.Text.Json.JsonSerializer.Serialize(allLines[i])}");
        data.Add("");
        data.Add("=== Agent messages (JSONL) ===");
        foreach (var entry in SessionEntries.BuildContextEntries(state.Entries, state.LeafId))
            foreach (var message in SessionEntries.SessionEntryToContextMessages(entry)) data.Add(message.ToJsonString());
        data.Add("");
        Directory.CreateDirectory(Path.GetDirectoryName(debugLogPath)!);
        File.WriteAllText(debugLogPath, string.Join("\n", data));
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ThemedText(() => $"{theme.Fg("accent", "✓ Debug log written")}\n{theme.Fg("muted", debugLogPath)}", 1, 1));
        ui.RequestRender();
    }

    private void HandleArminSaysHi()
    {
        if (EasterEgg3dLazy.PlayArmin3d(renderer)) return;
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new ArminComponent(ui));
        ui.RequestRender();
    }

    private void HandleDementedDelves()
    {
        chatContainer.AddChild(new Spacer(1));
        chatContainer.AddChild(new EarendilAnnouncementComponent());
        ui.RequestRender();
    }

    private async Task HandleBashCommandAsync(string command, bool excludeFromContext = false)
    {
        var isDeferred = state.IsStreaming;
        bashComponent = new BashExecutionComponent(command, ui, excludeFromContext, outputPad);
        if (isDeferred)
        {
            pendingMessagesContainer.AddChild(bashComponent);
            pendingBashComponents.Add(bashComponent);
        }
        else chatContainer.AddChild(bashComponent);
        ui.RequestRender();
        state.IsBashRunning = true;
        try
        {
            var data = await rpc.RequestAsync(new JsonObject { ["type"] = "bash", ["command"] = command, ["excludeFromContext"] = excludeFromContext });
            if (bashComponent is not null && data is JsonObject result)
            {
                bashComponent.SetComplete(result["exitCode"] is JsonValue code && code.TryGetValue<int>(out var exit) ? exit : null, B(result["cancelled"]),
                    B(result["truncated"]) ? TruncatedOutput(S(result["output"])) : null, S(result["fullOutputPath"]));
            }
        }
        catch (Exception error)
        {
            bashComponent?.SetComplete(null, false);
            ShowError($"Bash command failed: {(error is RpcCommandFailedException ? error.Message : "Unknown error")}");
        }
        finally { state.IsBashRunning = false; }
        bashComponent = null;
        ui.RequestRender();
    }

    private async Task HandleCompactCommandAsync(string? customInstructions)
    {
        ClearStatusIndicator();
        try { await CompactAsync(customInstructions); }
        catch (RpcCommandFailedException error)
        {
            // Errors arrive as compaction_end events when the host started compacting; otherwise show them here.
            if (!state.IsCompacting && error.Message is "Already compacted" or "Nothing to compact (session too small)" or
                "Session is processing or settling; manual compaction requires idle admission.") ShowError(error.Message);
        }
    }

    private async Task HandleCloneCommandAsync()
    {
        if (state.LeafId is null) { ShowStatus("Nothing to clone yet"); return; }
        try
        {
            var data = await ReplaceSessionAsync(new JsonObject { ["type"] = "clone" });
            if (data is JsonObject result && B(result["cancelled"])) { ui.RequestRender(); return; }
            editor.SetText("");
            ShowStatus("Cloned to new session");
        }
        catch (Exception error) { ShowError(error.Message); }
    }

    private void ApplyRuntimeSettings()
    {
        TerminalImage.SetCapabilityOverrides(settings.TerminalCapabilityOverrides);
        transcriptScrollView?.SetScrollbar(ToScrollbar(settings.FullscreenScrollbar));
        if (renderer is TuiAltScreen alt)
        {
            alt.CopyOnSelect = settings.FullscreenCopyOnSelect;
            alt.SetWheelScrollLines(settings.FullscreenWheelScrollLines);
        }
        footer.SetAutoCompactEnabled(state.AutoCompactionEnabled);
        footerDataProvider.SetCwd(Cwd);
        hideThinkingBlock = settings.HideThinkingBlock;
        outputPad = settings.OutputPad;
        ui.ShowHardwareCursor = settings.ShowHardwareCursor;
        var clearOnShrink = settings.ClearOnShrink;
        ui.ClearOnShrink = clearOnShrink;
        if (!clearOnShrink && activeStatusIndicator is null) statusContainer.Clear();
        defaultEditor.SetPaddingX(settings.EditorPaddingX);
        defaultEditor.SetAutocompleteMaxVisible(settings.AutocompleteMaxVisible);
        if (!ReferenceEquals(editor, defaultEditor))
        {
            editor.SetPaddingX(settings.EditorPaddingX);
            editor.SetAutocompleteMaxVisible(settings.AutocompleteMaxVisible);
        }
    }

    private async Task RebindCurrentSessionAsync()
    {
        SubscribeToAgent();
        programStatus.Reset();
        ApplyRuntimeSettings();
        await RefreshSessionAsync();
        renderedGeneration = state.Generation;
        await RefreshAvailableModelsAsync();
        await RefreshCommandsAsync();
        SetupAutocompleteProvider();
        SetupExtensionShortcuts();
        ShowLoadedResources(force: false, showDiagnosticsWhenQuiet: true);
        ShowStartupNoticesIfNeeded();
        UpdateAvailableProviderCount();
        UpdateEditorBorderColor();
        UpdateTerminalTitle();
    }

    /// <summary>The footer's available provider count from the current snapshot.</summary>
    private void UpdateAvailableProviderCount()
    {
        var models = state.ScopedModels.Count > 0 ? state.ScopedModels.Select(scoped => scoped.Model) : state.AvailableModels;
        footerDataProvider.SetAvailableProviderCount(models.Select(model => S(model["provider"])).Distinct().Count());
    }

    private async Task MaybeWarnAboutAnthropicSubscriptionAuthAsync(JsonObject? model = null)
    {
        model ??= state.Model;
        if (!settings.WarningAnthropicExtraUsage || anthropicSubscriptionWarningShown) return;
        if (model is null || S(model["provider"]) != "anthropic") return;
        try
        {
            if (await context.IsAnthropicSubscriptionAuth())
            {
                anthropicSubscriptionWarningShown = true;
                ShowWarning("Anthropic subscription auth is active. Third-party harness usage draws from extra usage and is billed per token, not your Claude plan limits. Manage extra usage at https://claude.ai/settings/usage. Disable this warning in /settings.");
            }
        }
        catch { }
    }

    /// <summary>The /bug flow's UI seam: editor and selector prompts in the editor slot, and the bordered loader.</summary>
    private sealed class BugReportUi(InteractiveMode mode) : IBugReportUi
    {
        public void ShowStatus(string message) => mode.context.Loop.Post(() => mode.ShowStatus(message));
        public void ShowError(string message) => mode.context.Loop.Post(() => mode.ShowError(message));
        public Task<string?> Input(string title, string description, string? initialValue) =>
            mode.context.Loop.InvokeAsync(() => mode.ShowExtensionEditorAsync(title, initialValue, description)).Unwrap();
        public Task<string?> Choose(string title, IReadOnlyList<string> options, string? description) =>
            mode.context.Loop.InvokeAsync(() => mode.ShowExtensionSelectorAsync(title, options, description: description)).Unwrap();
        public IBugReportLoader ShowLoader(string message)
        {
            var loader = new Loader(mode);
            mode.context.Loop.Post(() =>
            {
                loader.Component = new BorderedLoader(mode.ui, theme, message) { OnAbort = loader.Cancel };
                mode.editorContainer.Clear();
                mode.editorContainer.AddChild(loader.Component);
                mode.ui.SetFocus(loader.Component);
                mode.ui.RequestRender();
            });
            return loader;
        }
        private sealed class Loader(InteractiveMode mode) : IBugReportLoader
        {
            private readonly CancellationTokenSource abort = new();
            public BorderedLoader? Component;
            public CancellationToken Signal => abort.Token;
            public void Cancel() { try { abort.Cancel(); } catch (ObjectDisposedException) { } }
            public void Dispose() => mode.context.Loop.Post(() =>
            {
                Component?.Dispose();
                mode.editorContainer.Clear();
                mode.editorContainer.AddChild(mode.editor);
                mode.ui.SetFocus(mode.editor);
                mode.ui.RequestRender();
            });
        }
    }
}