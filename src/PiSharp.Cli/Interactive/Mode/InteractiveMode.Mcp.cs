// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/extensions/mcp/ui.ts (the /mcp manager view: menus rebuilt on
// change, the status screen with its cancel key, the redirect-URL prompt) and the /mcp command entry of extensions/mcp/index.ts,
// over the CLI's McpServerManager (IMPL-H).
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed partial class InteractiveMode
{
    private Task<List<AutocompleteItem>?> McpArgumentCompletions(string prefix)
    {
        var items = context.Mcp?.Manager?.GetArgumentCompletions(prefix);
        return Task.FromResult(items is null ? null : items.Select(item => new AutocompleteItem(item.Value, item.Label, item.Description)).ToList());
    }

    private async Task HandleMcpCommandAsync(string arguments)
    {
        if (context.Mcp?.Manager is not { } manager)
        {
            ShowWarning("MCP servers are not available in this session.");
            return;
        }
        try { await manager.ExecuteCommandAsync(arguments, new McpCommandUi(this)); }
        catch (Exception error) { ShowError(error.Message); }
    }

    private sealed class McpCommandUi(InteractiveMode mode) : IMcpCommandUi
    {
        public bool HasUi => true;
        public bool IsTui => true;
        public void Notify(string message, string level) => mode.context.Loop.Post(() => mode.ShowExtensionNotify(message, level));
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken token) =>
            mode.context.Loop.InvokeAsync(() => mode.ShowExtensionSelectorAsync(title, options, signal: token)).Unwrap();
        public Task<string?> InputAsync(string title, string placeholder, CancellationToken token) =>
            mode.context.Loop.InvokeAsync(() => mode.ShowExtensionInputAsync(title, placeholder)).Unwrap().WaitAsync(token);
        public async Task ShowManagerAsync(Func<IMcpManagerUi, Task> manage)
        {
            var view = new McpManagerView(mode);
            try { await manage(view); }
            finally { await mode.context.Loop.InvokeAsync(view.Close); }
        }
    }

    /// <summary>The manager view in the editor slot: a titled select list rebuilt on every change.</summary>
    private sealed class McpManagerView(InteractiveMode mode) : IMcpManagerUi
    {
        private Container? root;

        private void Show(IComponent component, IComponent focus)
        {
            mode.DisposeActiveSelector();
            mode.editorContainer.Clear();
            mode.editorContainer.AddChild(component);
            mode.ui.SetFocus(focus);
            mode.ui.RequestRender();
        }

        public void Close()
        {
            if (root is null) return;
            root = null;
            mode.editorContainer.Clear();
            mode.editorContainer.AddChild(mode.editor);
            mode.ui.SetFocus(mode.editor);
            mode.ui.RequestRender();
        }

        public Task<string?> MenuAsync(Func<McpMenu> build, Func<Action, IDisposable>? subscribe = null)
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            mode.context.Loop.Post(() =>
            {
                IDisposable? subscription = null;
                string? selected = null;
                void Finish(string? value) { subscription?.Dispose(); result.TrySetResult(value); }
                void Render()
                {
                    var menu = build();
                    var container = new Container();
                    container.AddChild(new DynamicBorder());
                    container.AddChild(new Text(theme.Fg("accent", theme.Bold(menu.Title)), 1, 0));
                    if (menu.Details is { Length: > 0 } details) container.AddChild(new Text(theme.Fg("muted", details), 1, 0));
                    if (menu.Error is { Length: > 0 } error) container.AddChild(new Text(theme.Fg("error", error), 1, 0));
                    container.AddChild(new Spacer(1));
                    var items = menu.Items.Select(item => new SelectItem(item.Value, item.Label, item.Description)).ToList();
                    if (items.Count == 0 && menu.Empty is { } empty) container.AddChild(new Text(theme.Fg("muted", empty), 1, 0));
                    var list = new SelectList(items, Math.Min(Math.Max(items.Count, 1), 12), Themes.GetSelectListTheme());
                    var initial = selected ?? menu.Selected;
                    if (initial is not null && items.FindIndex(item => item.Value == initial) is var index and >= 0) list.SetSelectedIndex(index);
                    list.OnSelectionChange = item => selected = item.Value;
                    list.OnSelect = item => Finish(item.Value);
                    list.OnCancel = () => Finish(null);
                    container.AddChild(list);
                    container.AddChild(new Spacer(1));
                    container.AddChild(new Text(theme.Fg("dim", $"enter {menu.ConfirmLabel} · esc {menu.CancelLabel}"), 1, 0));
                    container.AddChild(new DynamicBorder());
                    root = container;
                    Show(container, list);
                }
                Render();
                if (subscribe is not null) subscription = subscribe(() => mode.context.Loop.Post(() => { if (!result.Task.IsCompleted) Render(); }));
            });
            return result.Task;
        }

        public void Status(string title, string message, Action? onCancel = null) => mode.context.Loop.Post(() =>
        {
            var container = new McpStatusView(onCancel);
            container.AddChild(new DynamicBorder());
            container.AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
            container.AddChild(new Text(theme.Fg("muted", message), 1, 0));
            if (onCancel is not null) container.AddChild(new Text(theme.Fg("dim", "esc cancel"), 1, 0));
            container.AddChild(new DynamicBorder());
            root = container;
            Show(container, container);
        });

        public Task<string?> RedirectUrlAsync(string title, string authorizationUrl, CancellationToken signal)
        {
            var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.Register(() => result.TrySetResult(null));
            mode.context.Loop.Post(() =>
            {
                var container = new Container();
                var input = new Input();
                container.AddChild(new DynamicBorder());
                container.AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));
                container.AddChild(new AuthUrlComponent(mode.ui, authorizationUrl, mode.context.CopyToClipboard));
                container.AddChild(new Text(theme.Fg("muted", "Paste the redirect URL after signing in:"), 1, 0));
                container.AddChild(input);
                container.AddChild(new DynamicBorder());
                input.OnSubmit = value => result.TrySetResult(TextUtils.JsTrim(value).Length > 0 ? TextUtils.JsTrim(value) : null);
                input.OnEscape = () => result.TrySetResult(null);
                root = container;
                Show(container, input);
            });
            return result.Task;
        }
    }

    private sealed class McpStatusView(Action? onCancel) : Container, IInputHandler
    {
        public void HandleInput(string data)
        {
            if (onCancel is not null && PiSharp.Tui.Pi.KeybindingsManager.Global.Matches(data, "tui.select.cancel")) onCancel();
        }
    }
}
