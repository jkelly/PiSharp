// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (createExtensionUIContext,
// showExtensionSelector/Confirm/Input/Editor, setExtensionStatus/Widget/Footer/Header, setCustomEditorComponent, resetExtensionUI,
// extension shortcuts and terminal input listeners). Extension dialogs reach the mode as RPC extension_ui_request records (the host's
// native presentation observer for dialogs, output records for notifications) and are answered with extension_ui_response.
using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Rpc.Ui;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed partial class InteractiveMode : IRpcExtensionUiPresentationObserver
{
    /// <summary>Open RPC dialogs by request id: the cancel action that closes the component without answering.</summary>
    private readonly Dictionary<string, Action> openRpcDialogs = new(StringComparer.Ordinal);

    public ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(presentation.Request.ToString()) is JsonObject request)
            context.Loop.Post(() => HandleExtensionUiRequest(request));
        return ValueTask.CompletedTask;
    }

    public ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken cancellationToken)
    {
        context.Loop.Post(() =>
        {
            if (openRpcDialogs.Remove(retirement.Identity.RequestId, out var close)) close();
        });
        return ValueTask.CompletedTask;
    }

    private void RespondExtensionUi(string id, JsonObject body)
    {
        body["type"] = "extension_ui_response"; body["id"] = id;
        rpc.Post(body);
    }

    /// <summary>An extension_ui_request: dialogs open in the editor slot; notifications apply immediately.</summary>
    private void HandleExtensionUiRequest(JsonObject request)
    {
        var id = S(request["id"]) ?? "";
        var method = S(request["method"]);
        var timeout = request["timeout"] is JsonValue t && t.TryGetValue<double>(out var ms) ? ms : (double?)null;
        switch (method)
        {
            case "select":
            case "confirm":
            case "input":
            case "editor":
            {
                if (openRpcDialogs.ContainsKey(id)) return;
                var title = S(request["title"]) ?? "";
                var answered = false;
                // Pi 1.4.0: each dialog closes on its own (the stack shows the one below); a retired request closes only its dialog.
                var close = new CancellationTokenSource();
                void Answer(JsonObject body) { if (answered) return; answered = true; openRpcDialogs.Remove(id); RespondExtensionUi(id, body); }
                openRpcDialogs[id] = () => { answered = true; close.Cancel(); };
                switch (method)
                {
                    case "select":
                    {
                        var options = request["options"] is JsonArray array ? array.Select(S).OfType<string>().ToList() : [];
                        _ = ShowExtensionSelectorAsync(title, options, timeout, signal: close.Token).ContinueWith(task =>
                            context.Loop.Post(() => Answer(task.Result is { } value ? new JsonObject { ["value"] = value } : new JsonObject { ["cancelled"] = true })),
                            TaskScheduler.Default);
                        break;
                    }
                    case "confirm":
                        _ = ShowExtensionSelectorAsync($"{title}\n{S(request["message"])}", ["Yes", "No"], timeout, new BlockedStatus(ProgramBlockedKind.Permission, title), close.Token)
                            .ContinueWith(task => context.Loop.Post(() => Answer(task.Result is null ? new JsonObject { ["cancelled"] = true } : new JsonObject { ["confirmed"] = task.Result == "Yes" })),
                                TaskScheduler.Default);
                        break;
                    case "input":
                        _ = ShowExtensionInputAsync(title, S(request["placeholder"]), timeout, close.Token).ContinueWith(task =>
                            context.Loop.Post(() => Answer(task.Result is { } value ? new JsonObject { ["value"] = value } : new JsonObject { ["cancelled"] = true })),
                            TaskScheduler.Default);
                        break;
                    default:
                        _ = ShowExtensionEditorAsync(title, S(request["prefill"]), signal: close.Token).ContinueWith(task =>
                            context.Loop.Post(() => Answer(task.Result is { } value ? new JsonObject { ["value"] = value } : new JsonObject { ["cancelled"] = true })),
                            TaskScheduler.Default);
                        break;
                }
                break;
            }
            case "notify":
                ShowExtensionNotify(S(request["message"]) ?? "", S(request["notifyType"]));
                break;
            case "setStatus":
                SetExtensionStatus(S(request["statusKey"]) ?? "", S(request["statusText"]));
                break;
            case "setWidget":
                // A widget an extension host component draws keeps drawing live; the host's one-off text copy of it is not shown.
                if (request["widgetLines"] is JsonArray && extensionComponentWidgets.Contains(S(request["widgetKey"]) ?? "")) break;
                SetExtensionWidget(S(request["widgetKey"]) ?? "", request["widgetLines"] is JsonArray lines ? lines.Select(S).OfType<string>().ToList() : null,
                    S(request["widgetPlacement"]) ?? "aboveEditor");
                break;
            case "setTitle":
                ui.Terminal.SetTitle(S(request["title"]) ?? "");
                break;
            case "set_editor_text":
                editor.SetText(S(request["text"]) ?? "");
                ui.RequestRender();
                break;
        }
    }

    // =========================================================================
    // Dialogs
    // =========================================================================

    /// <summary>Pi 1.4.0 pushExtensionDialog: shows an extension dialog in the editor slot. Dialogs form a stack: a new dialog hides the
    /// visible one and closing the visible dialog shows the previous one again, so every dialog stays answerable and dialogs opened
    /// from inside another dialog (a confirm from a custom component) work.</summary>
    private void PushExtensionDialog(IComponent component, BlockedStatus? blocked, Action? dispose = null)
    {
        DisposeActiveSelector();
        extensionDialogs.Add((component, blocked, dispose));
        ShowTopExtensionDialog();
    }

    /// <summary>Removes an extension dialog; if it was visible, the previous dialog or the editor shows. False when it was not open.</summary>
    private bool RemoveExtensionDialog(IComponent component)
    {
        var index = extensionDialogs.FindIndex(dialog => ReferenceEquals(dialog.Component, component));
        if (index == -1) return false;
        extensionDialogs.RemoveAt(index);
        if (index == extensionDialogs.Count) ShowTopExtensionDialog();
        return true;
    }

    /// <summary>The top dialog (or the editor) takes the editor slot and focus; the blocked program status follows it.</summary>
    private void ShowTopExtensionDialog()
    {
        var top = extensionDialogs.Count > 0 ? extensionDialogs[^1] : default;
        var component = top.Component ?? editor;
        editorContainer.Clear();
        editorContainer.AddChild(component);
        ui.SetFocus(component);
        programStatus.SetBlocked("extension-dialog", top.Blocked);
        ui.RequestRender();
    }

    /// <summary>A selector for extensions; resolves with the chosen option or null when cancelled, timed out or aborted.</summary>
    internal Task<string?> ShowExtensionSelectorAsync(string title, IReadOnlyList<string> options, double? timeout = null, BlockedStatus? blocked = null,
        CancellationToken signal = default, string? description = null)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (signal.IsCancellationRequested) { result.TrySetResult(null); return result.Task; }
        ExtensionSelectorComponent? selector = null;
        CancellationTokenRegistration registration = default;
        var finished = false;
        void Finish(string? option)
        {
            if (finished) return;
            finished = true;
            registration.Dispose();
            selector!.Dispose();
            RemoveExtensionDialog(selector);
            result.TrySetResult(option);
        }
        selector = new ExtensionSelectorComponent(title, options, option => Finish(option), () => Finish(null),
            new ExtensionSelectorOptions(ui, timeout, ToggleToolOutputExpansion, description));
        PushExtensionDialog(selector, blocked ?? new BlockedStatus(ProgramBlockedKind.Question, title), selector.Dispose);
        registration = signal.Register(() => context.Loop.Post(() => Finish(null)));
        return result.Task;
    }

    internal async Task<bool> ShowExtensionConfirmAsync(string title, string message, double? timeout = null)
    {
        var result = await ShowExtensionSelectorAsync($"{title}\n{message}", ["Yes", "No"], timeout, new BlockedStatus(ProgramBlockedKind.Permission, title));
        return result == "Yes";
    }

    /// <summary>A text input for extensions; resolves with the value or null when cancelled, timed out or aborted.</summary>
    internal Task<string?> ShowExtensionInputAsync(string title, string? placeholder, double? timeout = null, CancellationToken signal = default)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (signal.IsCancellationRequested) { result.TrySetResult(null); return result.Task; }
        ExtensionInputComponent? input = null;
        CancellationTokenRegistration registration = default;
        var finished = false;
        void Finish(string? value)
        {
            if (finished) return;
            finished = true;
            registration.Dispose();
            input!.Dispose();
            RemoveExtensionDialog(input);
            result.TrySetResult(value);
        }
        input = new ExtensionInputComponent(title, placeholder, value => Finish(value), () => Finish(null), new ExtensionInputOptions(ui, timeout));
        PushExtensionDialog(input, new BlockedStatus(ProgramBlockedKind.Question, title), input.Dispose);
        registration = signal.Register(() => context.Loop.Post(() => Finish(null)));
        return result.Task;
    }

    /// <summary>A multi-line editor for extensions (with the external editor shortcut). <paramref name="signal"/> closes it unanswered
    /// (a retired RPC request).</summary>
    internal Task<string?> ShowExtensionEditorAsync(string title, string? prefill, string? description = null, CancellationToken signal = default)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (signal.IsCancellationRequested) { result.TrySetResult(null); return result.Task; }
        ExtensionEditorComponent? dialog = null;
        CancellationTokenRegistration registration = default;
        var finished = false;
        void Finish(string? value)
        {
            if (finished) return;
            finished = true;
            registration.Dispose();
            RemoveExtensionDialog(dialog!);
            result.TrySetResult(value);
        }
        dialog = new ExtensionEditorComponent(ui, keybindings.Manager, title, prefill, value => Finish(value), () => Finish(null),
            description is null ? null : new ExtensionEditorOptions(Description: description), settings.ExternalEditorCommand);
        PushExtensionDialog(dialog, new BlockedStatus(ProgramBlockedKind.Question, title));
        registration = signal.Register(() => context.Loop.Post(() => Finish(null)));
        return result.Task;
    }

    private void ShowExtensionNotify(string message, string? type)
    {
        if (type == "error") ShowError(message);
        else if (type == "warning") ShowWarning(message);
        else ShowStatus(message);
    }

    private void ShowExtensionError(string extensionPath, string error, string? stack)
    {
        var errorMsg = $"Extension \"{extensionPath}\" error: {error}";
        chatContainer.AddChild(new ThemedText(() => theme.Fg("error", errorMsg), 1, 0));
        if (stack is not null)
        {
            var stackLines = stack.Split('\n').Skip(1).ToList();
            if (stackLines.Count > 0)
                chatContainer.AddChild(new ThemedText(() => string.Join("\n", stackLines.Select(line => theme.Fg("dim", "  " + TextUtils.JsTrim(line)))), 1, 0));
        }
        ui.RequestRender();
    }

    // =========================================================================
    // Status, widgets, header and footer
    // =========================================================================

    private void SetExtensionStatus(string key, string? text)
    {
        footerDataProvider.SetExtensionStatus(key, text);
        ui.RequestRender();
    }

    /// <summary>Set an extension widget from text lines (null removes it).</summary>
    private void SetExtensionWidget(string key, IReadOnlyList<string>? content, string placement = "aboveEditor") =>
        SetExtensionWidgetComponent(key, content is null ? null : (_, _) =>
        {
            var container = new Container();
            foreach (var line in content.Take(MaxWidgetLines)) container.AddChild(new Text(line, 1, 0));
            if (content.Count > MaxWidgetLines) container.AddChild(new ThemedText(() => theme.Fg("muted", "... (widget truncated)"), 1, 0));
            return container;
        }, placement);

    /// <summary>Set an extension widget component factory (null removes it).</summary>
    internal void SetExtensionWidgetComponent(string key, Func<ITui, Theme, IComponent>? factory, string placement = "aboveEditor")
    {
        void RemoveExisting(Dictionary<string, IComponent> map)
        {
            if (map.Remove(key, out var existing) && existing is IDisposableComponent disposable) disposable.Dispose();
        }
        RemoveExisting(extensionWidgetsAbove);
        RemoveExisting(extensionWidgetsBelow);
        if (factory is null) { RenderWidgets(); return; }
        var component = factory(ui, theme);
        (placement == "belowEditor" ? extensionWidgetsBelow : extensionWidgetsAbove)[key] = component;
        RenderWidgets();
    }

    private void ClearExtensionWidgets()
    {
        foreach (var widget in extensionWidgetsAbove.Values.Concat(extensionWidgetsBelow.Values)) (widget as IDisposableComponent)?.Dispose();
        extensionWidgetsAbove.Clear();
        extensionWidgetsBelow.Clear();
        RenderWidgets();
    }

    private void RenderWidgets()
    {
        RenderWidgetContainer(widgetContainerAbove, extensionWidgetsAbove, spacerWhenEmpty: true, leadingSpacer: true);
        RenderWidgetContainer(widgetContainerBelow, extensionWidgetsBelow, spacerWhenEmpty: false, leadingSpacer: false);
        ui.RequestRender();
    }

    private static void RenderWidgetContainer(Container container, Dictionary<string, IComponent> widgets, bool spacerWhenEmpty, bool leadingSpacer)
    {
        container.Clear();
        if (widgets.Count == 0)
        {
            if (spacerWhenEmpty) container.AddChild(new Spacer(1));
            return;
        }
        if (leadingSpacer) container.AddChild(new Spacer(1));
        foreach (var component in widgets.Values) container.AddChild(component);
    }

    /// <summary>A custom footer component, or the built-in footer (null).</summary>
    internal void SetExtensionFooter(Func<ITui, Theme, IReadonlyFooterDataProvider, IComponent>? factory)
    {
        (customFooter as IDisposableComponent)?.Dispose();
        footerContainer.Clear();
        if (factory is not null)
        {
            customFooter = factory(ui, theme, footerDataProvider);
            footerContainer.AddChild(customFooter);
        }
        else
        {
            customFooter = null;
            footerContainer.AddChild(footer);
        }
        ui.RequestRender();
    }

    /// <summary>A custom header component, or the built-in header (null).</summary>
    internal void SetExtensionHeader(Func<ITui, Theme, IComponent>? factory)
    {
        if (builtInHeader is null) return;
        (customHeader as IDisposableComponent)?.Dispose();
        var current = customHeader ?? builtInHeader;
        var index = headerContainer.Children.IndexOf(current);
        if (factory is not null)
        {
            customHeader = factory(ui, theme);
            if (customHeader is IExpandable expandable) expandable.SetExpanded(toolOutputExpanded);
            if (index != -1) headerContainer.Children[index] = customHeader;
            else headerContainer.Children.Insert(0, customHeader);
        }
        else
        {
            customHeader = null;
            if (builtInHeader is IExpandable expandable) expandable.SetExpanded(toolOutputExpanded);
            if (index != -1) headerContainer.Children[index] = builtInHeader;
        }
        ui.RequestRender();
    }

    internal Action AddExtensionTerminalInputListener(Func<string, TuiInputListenerResult?> handler)
    {
        var subscription = new ExtensionTerminalInputSubscription(handler);
        subscription.Unsubscribe = ui.AddInputListener(handler);
        extensionTerminalInputSubscriptions.Add(subscription);
        return () =>
        {
            subscription.Unsubscribe();
            extensionTerminalInputSubscriptions.Remove(subscription);
        };
    }

    private void RebindExtensionTerminalInputListeners()
    {
        foreach (var subscription in extensionTerminalInputSubscriptions)
        {
            subscription.Unsubscribe();
            subscription.Unsubscribe = ui.AddInputListener(subscription.Handler);
        }
    }

    private void ClearExtensionTerminalInputListeners()
    {
        foreach (var subscription in extensionTerminalInputSubscriptions) subscription.Unsubscribe();
        extensionTerminalInputSubscriptions.Clear();
    }

    /// <summary>A custom editor component from an extension; null restores the default editor.</summary>
    internal void SetCustomEditorComponent(Func<ITui, EditorTheme, PiSharp.Tui.Pi.KeybindingsManager, IEditorComponent>? factory)
    {
        editorComponentFactory = factory;
        var currentText = editor.GetText();
        // Pi 1.5.0: an open extension dialog keeps the editor slot; the new editor shows when the last dialog closes.
        var dialogOpen = extensionDialogs.Count > 0;
        if (!dialogOpen)
        {
            DisposeActiveSelector();
            editorContainer.Clear();
        }
        if (factory is not null)
        {
            var newEditor = factory(ui, Themes.GetEditorTheme(), keybindings.Manager);
            newEditor.OnSubmit = defaultEditor.OnSubmit;
            newEditor.OnChange = defaultEditor.OnChange;
            newEditor.SetText(currentText);
            newEditor.BorderColor = defaultEditor.BorderColor;
            newEditor.SetPaddingX(defaultEditor.GetPaddingX());
            newEditor.SetAutocompleteMaxVisible(defaultEditor.GetAutocompleteMaxVisible());
            if (autocompleteProvider is not null) newEditor.SetAutocompleteProvider(autocompleteProvider);
            if (newEditor is CustomEditor custom)
            {
                custom.OnEscape ??= () => defaultEditor.OnEscape?.Invoke();
                custom.OnCtrlD ??= () => defaultEditor.OnCtrlD?.Invoke();
                custom.OnPasteImage ??= () => defaultEditor.OnPasteImage?.Invoke();
                custom.OnExtensionShortcut ??= data => defaultEditor.OnExtensionShortcut?.Invoke(data) ?? false;
                foreach (var (action, handler) in defaultEditor.ActionHandlers) custom.ActionHandlers[action] = handler;
            }
            editor = newEditor;
        }
        else
        {
            defaultEditor.SetText(currentText);
            editor = defaultEditor;
        }
        if (!dialogOpen) editorContainer.AddChild(editor);
        if (activeStatusIndicator is not null)
        {
            statusContainer.Clear();
            activeWorkingIndicatorEmbedded = SetEditorWorkingStatusIndicator(activeStatusIndicator);
            if (!activeWorkingIndicatorEmbedded) statusContainer.AddChild(activeStatusIndicator);
        }
        if (!dialogOpen) ui.SetFocus(editor);
        ui.RequestRender();
    }

    private void ResetExtensionUI()
    {
        if (extensionDialogs.Count > 0)
        {
            foreach (var dialog in extensionDialogs) dialog.Dispose?.Invoke();
            extensionDialogs.Clear();
            ShowTopExtensionDialog();
        }
        openRpcDialogs.Clear();
        ui.HideOverlay();
        ClearExtensionTerminalInputListeners();
        extensionTerminalInputs.Clear();
        extensionComponentWidgets.Clear();
        extensionComponents.Clear();
        SetExtensionFooter(null);
        SetExtensionHeader(null);
        ClearExtensionWidgets();
        footerDataProvider.ClearExtensionStatuses();
        footer.Invalidate();
        autocompleteProviderWrappers.Clear();
        SetCustomEditorComponent(null);
        SetupAutocompleteProvider();
        defaultEditor.OnExtensionShortcut = null;
        UpdateTerminalTitle();
        workingMessage = null;
        workingVisible = true;
        SetWorkingIndicator();
        if (activeStatusIndicator?.Kind == StatusIndicatorKind.Working)
            activeStatusIndicator.SetMessage($"{DefaultWorkingMessage} ({KeybindingHints.KeyText("app.interrupt")} to interrupt)");
        SetHiddenThinkingLabel();
    }

    /// <summary>Keyboard shortcuts extensions registered (the host resolves them against the effective keybindings).</summary>
    private void SetupExtensionShortcuts()
    {
        var shortcuts = context.GetExtensionShortcuts?.Invoke(keybindings.GetEffectiveConfig()) ?? [];
        if (shortcuts.Count == 0) return;
        defaultEditor.OnExtensionShortcut = data =>
        {
            foreach (var shortcut in shortcuts)
            {
                if (!Keys.Matches(data, shortcut.Key)) continue;
                _ = Task.Run(async () =>
                {
                    try { await shortcut.Run(); }
                    catch (Exception error) { context.Loop.Post(() => ShowError($"Shortcut handler error: {error.Message}")); }
                });
                return true;
            }
            return false;
        };
    }

    // =========================================================================
    // Selector slot
    // =========================================================================

    private void DisposeActiveSelector()
    {
        var dispose = activeSelectorDispose;
        activeSelectorToken = null;
        activeSelectorDispose = null;
        dispose?.Invoke();
    }

    /// <summary>Shows a selector in place of the editor. The factory receives <c>done</c> and returns the component and its focus.</summary>
    private void ShowSelector(Func<Action, (IComponent Component, IComponent Focus, Action? Dispose)> create)
    {
        var token = new object();
        Action? dispose = null;
        void Done()
        {
            dispose?.Invoke();
            if (!ReferenceEquals(activeSelectorToken, token)) return;
            activeSelectorToken = null;
            activeSelectorDispose = null;
            editorContainer.Clear();
            editorContainer.AddChild(editor);
            ui.SetFocus(editor);
        }
        var created = create(Done);
        dispose = created.Dispose;
        DisposeActiveSelector();
        activeSelectorToken = token;
        activeSelectorDispose = dispose;
        editorContainer.Clear();
        editorContainer.AddChild(created.Component);
        ui.SetFocus(created.Focus);
        ui.RequestRender();
    }
}
