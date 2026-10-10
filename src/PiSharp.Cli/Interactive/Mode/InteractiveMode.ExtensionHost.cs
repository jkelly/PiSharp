// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (createExtensionUIContext:
// setWidget with a component factory, setFooter, setHeader, setWorkingMessage, setWorkingVisible, setWorkingIndicator,
// setHiddenThinkingLabel, setToolsExpanded, onTerminalInput) for the extensions of the Node extension host. Dialogs, notify, status,
// text widgets, the title and the editor text arrive through the RPC extension UI (InteractiveMode.ExtensionUi.cs).
using System.Text.Json;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed partial class InteractiveMode
{
    /// <summary>Widget keys that show an extension host component: the RPC host's one-off text copy of that widget is not shown.</summary>
    private readonly HashSet<string> extensionComponentWidgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionRowsComponent>> extensionComponents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> extensionTerminalInputs = new(StringComparer.Ordinal);
    private volatile string editorTextSnapshot = "";
    private (int Columns, int Rows) terminalSizeSnapshot = (80, 24);

    private readonly List<(string Op, JsonElement Args)> pendingExtensionPublications = [];
    private bool extensionPublicationsReady;

    /// <summary>Connects the extension host's interactive publications to this mode. Publications made before the mode is set up
    /// (session_start handlers run while the session host starts) are kept and applied by <see cref="AttachExtensionHost"/>.</summary>
    private void ConnectExtensionHost()
    {
        if (context.Extensions is not { } extensions) return;
        extensions.Attach(
            (op, args) =>
            {
                lock (pendingExtensionPublications)
                    if (!extensionPublicationsReady) { pendingExtensionPublications.Add((op, args)); return; }
                context.Loop.Post(() => HandleExtensionPublication(extensions, op, args));
            },
            id => context.Loop.Post(() =>
            {
                if (!extensionComponents.TryGetValue(id, out var components)) return;
                foreach (var component in components) component.Invalidate();
                ui.RequestRender();
            }),
            new(() => editorTextSnapshot, () => terminalSizeSnapshot, () => toolOutputExpanded, footerDataProvider.GetGitBranch,
                footerDataProvider.GetAvailableProviderCount) { SetTheme = SetExtensionTheme });
    }

    /// <summary>interactive-mode.ts bindCurrentSessionExtensions commandContextActions for every extension's ctx.newSession()/ctx.fork():
    /// newSession clears the status indicator first; a fork that happened puts its selected text in the editor (once the forked
    /// session shows) and shows "Forked to new session"; a failure of either is handleFatalRuntimeError ("Failed to create session",
    /// "Failed to fork session").</summary>
    private void ConnectExtensionSessionActions() => context.Startup.Host.ExtensionSessionActions = new(
        () => context.Loop.Post(() => ClearStatusIndicator()),
        (text, generation) => context.Loop.Post(() => Run(async () =>
        {
            if (generation > renderedGeneration)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                generationWaiters.Add((generation, done));
                await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            editor.SetText(text ?? "");
            ShowStatus("Forked to new session");
        })),
        (prefix, error) => context.Loop.Post(() => HandleFatalRuntimeError(prefix, error)));

    /// <summary>interactive-mode.ts createExtensionUIContext setTheme(name): the theme controller applies the theme (one that fails to
    /// load falls back to the system theme) and a theme that applied becomes the theme setting. The extension host asks from its own
    /// thread: the result is whether the theme loads, and the switch itself runs on the mode's loop.</summary>
    private (bool Success, string? Error) SetExtensionTheme(string name)
    {
        var error = Themes.LoadError(name);
        context.Loop.Post(() =>
        {
            var result = themeController.SetThemeName(name);
            if (result.Success && settings.ThemeSetting != name) settings.SetTheme(name);
        });
        return error is null ? (true, null) : (false, error);
    }

    /// <summary>Starts applying extension publications (on the loop, once the editor and its handlers exist).</summary>
    private void AttachExtensionHost()
    {
        if (context.Extensions is not { } extensions) return;
        var onChange = defaultEditor.OnChange;
        defaultEditor.OnChange = text => { editorTextSnapshot = text; onChange?.Invoke(text); };
        terminalSizeSnapshot = (ui.Terminal.Columns, ui.Terminal.Rows);
        List<(string Op, JsonElement Args)> pending;
        lock (pendingExtensionPublications) { extensionPublicationsReady = true; pending = [.. pendingExtensionPublications]; pendingExtensionPublications.Clear(); }
        // CustomEditor-based extension editors match the mode's keybindings, run its app actions and its extension shortcuts.
        extensions.ConfigureEditor(keybindings.GetEffectiveConfig(), [.. defaultEditor.ActionHandlers.Keys,
            "app.interrupt", "app.exit", "app.clipboard.pasteImage"], data => defaultEditor.OnExtensionShortcut?.Invoke(data) ?? false);
        foreach (var (op, args) in pending) HandleExtensionPublication(extensions, op, args);
    }

    /// <summary>An app action of the mode's editor by name (custom-editor.ts: onEscape, onCtrlD, onPasteImage, actionHandlers).</summary>
    private Action? EditorAction(string action) => action switch
    {
        "app.interrupt" => defaultEditor.OnEscape ?? (defaultEditor.ActionHandlers.TryGetValue(action, out var interrupt) ? interrupt : null),
        "app.exit" => defaultEditor.OnCtrlD ?? (defaultEditor.ActionHandlers.TryGetValue(action, out var exit) ? exit : null),
        "app.clipboard.pasteImage" => defaultEditor.OnPasteImage,
        _ => defaultEditor.ActionHandlers.TryGetValue(action, out var handler) ? handler : null
    };

    private ExtensionRowsComponent ExtensionComponent(IInteractiveExtensionHost extensions, string id, bool input = false)
    {
        var component = new ExtensionRowsComponent(width =>
        {
            terminalSizeSnapshot = (ui.Terminal.Columns, ui.Terminal.Rows);
            return extensions.RenderComponent(id, width);
        }, input ? data => extensions.InputComponent(id, data) : null);
        if (!extensionComponents.TryGetValue(id, out var list)) extensionComponents[id] = list = [];
        list.Add(component);
        return component;
    }

    private static string? ComponentId(JsonElement args, int index) =>
        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index && args[index].ValueKind == JsonValueKind.Object &&
        args[index].TryGetProperty("component", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private static JsonElement Arg(JsonElement args, int index) =>
        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index ? args[index] : default;

    private void HandleExtensionPublication(IInteractiveExtensionHost extensions, string op, JsonElement args)
    {
        if (isShuttingDown) return;
        switch (op)
        {
            case "setWidget":
            {
                var key = Arg(args, 0).ValueKind == JsonValueKind.String ? Arg(args, 0).GetString()! : "";
                if (ComponentId(args, 1) is not { } id) { extensionComponentWidgets.Remove(key); return; }
                var placement = Arg(args, 2) is { ValueKind: JsonValueKind.Object } options && options.TryGetProperty("placement", out var where) &&
                    where.ValueKind == JsonValueKind.String ? where.GetString()! : "aboveEditor";
                extensionComponentWidgets.Add(key);
                SetExtensionWidgetComponent(key, (_, _) => ExtensionComponent(extensions, id), placement);
                return;
            }
            case "setFooter":
                if (ComponentId(args, 0) is { } footerId) SetExtensionFooter((_, _, _) => ExtensionComponent(extensions, footerId));
                else SetExtensionFooter(null);
                return;
            case "setHeader":
                if (ComponentId(args, 0) is { } headerId) SetExtensionHeader((_, _) => ExtensionComponent(extensions, headerId));
                else SetExtensionHeader(null);
                return;
            case "setWorkingMessage":
                workingMessage = Arg(args, 0).ValueKind == JsonValueKind.String ? Arg(args, 0).GetString() : null;
                if (activeStatusIndicator?.Kind == StatusIndicatorKind.Working) activeStatusIndicator.SetMessage(workingMessage ?? DefaultWorkingMessage);
                ui.RequestRender();
                return;
            case "setWorkingVisible":
                SetWorkingVisible(Arg(args, 0).ValueKind != JsonValueKind.False);
                return;
            case "setWorkingIndicator":
            {
                var options = Arg(args, 0);
                if (options.ValueKind != JsonValueKind.Object) { SetWorkingIndicator(); return; }
                IReadOnlyList<string>? frames = options.TryGetProperty("frames", out var list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(frame => frame.ValueKind == JsonValueKind.String).Select(frame => frame.GetString()!)] : null;
                int? interval = options.TryGetProperty("intervalMs", out var ms) && ms.ValueKind == JsonValueKind.Number ? (int)ms.GetDouble() : null;
                SetWorkingIndicator(new LoaderIndicatorOptions(frames, interval));
                return;
            }
            case "setHiddenThinkingLabel":
                SetHiddenThinkingLabel(Arg(args, 0).ValueKind == JsonValueKind.String ? Arg(args, 0).GetString() : null);
                return;
            case "setToolsExpanded":
                SetToolsExpanded(Arg(args, 0).ValueKind == JsonValueKind.True);
                return;
            case "onTerminalInput":
                if (Arg(args, 0).ValueKind == JsonValueKind.String && Arg(args, 0).GetString() is { } callback && !extensionTerminalInputs.ContainsKey(callback))
                    extensionTerminalInputs[callback] = AddExtensionTerminalInputListener(data => extensions.TerminalInput(callback, data));
                return;
            case "setEditorComponent":
                // interactive-mode.ts setCustomEditorComponent: the extension's editor (in the Node host) replaces the editor.
                if (ComponentId(args, 0) is { } editorId && extensions.CreateEditor(editorId, context.Loop.Post, EditorAction) is { } created)
                    SetCustomEditorComponent((_, _, _) => created);
                else SetCustomEditorComponent(null);
                return;
            case "addAutocompleteProvider":
                if (Arg(args, 0) is { ValueKind: JsonValueKind.Object } wrapper && wrapper.TryGetProperty("wrapper", out var wrapperId) &&
                    wrapperId.ValueKind == JsonValueKind.String && extensions.AutocompleteWrapper(wrapperId.GetString()!) is { } wrap)
                {
                    autocompleteProviderWrappers.Add(wrap);
                    SetupAutocompleteProvider();
                }
                return;
            case "offTerminalInput":
                if (Arg(args, 0).ValueKind == JsonValueKind.String && extensionTerminalInputs.Remove(Arg(args, 0).GetString()!, out var remove)) remove();
                return;
        }
    }

    private sealed record ActiveCustomComponent(PiSharp.Extensions.ExtensionCustomComponentIdentity Identity, TaskCompletionSource Done,
        ExtensionRowsComponent Component);
    private ActiveCustomComponent? activeCustomComponent;

    /// <summary>ctx.ui.custom(): the component replaces the editor and takes focus; done() (or cancellation) restores the editor.</summary>
    internal async Task ShowExtensionCustomComponentAsync(PiSharp.Extensions.ExtensionCustomComponentCallbacks callbacks,
        PiSharp.Extensions.IExtensionContext extensionContext, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = new ExtensionRowsComponent(width =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { return callbacks.Render(width, extensionContext, timeout.Token).WaitAsync(timeout.Token).GetAwaiter().GetResult().Rows; }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or InvalidOperationException or IOException) { return []; }
        }, data => _ = callbacks.Input(data, extensionContext, CancellationToken.None)
            .ContinueWith(static task => _ = task.Exception, TaskScheduler.Default));
        var active = new ActiveCustomComponent(callbacks.Identity, done, component);
        await context.Loop.InvokeAsync(() =>
        {
            if (activeCustomComponent is not null) throw new InvalidOperationException("Another extension component is open.");
            activeCustomComponent = active;
            editorContainer.Clear();
            editorContainer.AddChild(component);
            ui.SetFocus(component);
            ui.RequestRender();
        });
        try { await done.Task.WaitAsync(token); }
        finally
        {
            await context.Loop.InvokeAsync(() =>
            {
                if (ReferenceEquals(activeCustomComponent, active)) activeCustomComponent = null;
                editorContainer.Clear();
                editorContainer.AddChild(editor);
                ui.SetFocus(editor);
                ui.RequestRender();
            });
            await callbacks.Dispose(extensionContext);
        }
    }

    /// <summary>The open custom component called done() (or asked to be redrawn).</summary>
    internal void SignalExtensionCustomComponent(PiSharp.Extensions.ExtensionCustomComponentIdentity identity, bool done) => context.Loop.Post(() =>
    {
        if (activeCustomComponent is not { } active || active.Identity != identity) return;
        if (done) { active.Done.TrySetResult(); return; }
        active.Component.Invalidate();
        ui.RequestRender();
    });
}