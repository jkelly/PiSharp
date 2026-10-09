// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (createExtensionUIContext
// custom(): the extension's component replaces the editor and has focus until it calls done()).
using System.Collections.Immutable;
using PiSharp.Extensions;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>The extension UI the session host gives extensions in interactive mode: the RPC UI (dialogs, notify, status, widgets)
/// plus ctx.ui.custom() components the mode shows in place of the editor.</summary>
internal sealed class InteractiveCustomComponentUi(IExtensionUiProvider inner, Func<InteractiveMode?> mode) : IExtensionUiProvider
{
    public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope(inner.OpenScope(context), context, mode);

    private sealed class Scope(IExtensionUiScope inner, IExtensionContext context, Func<InteractiveMode?> mode) : IExtensionUiScope, IExtensionCustomComponentUi
    {
        public ExtensionUiCapabilities Capabilities => inner.Capabilities.Supports(ExtensionUiFeature.CustomTerminalComponent) ? inner.Capabilities
            : inner.Capabilities with { Features = (inner.Capabilities.Features.IsDefault ? [] : inner.Capabilities.Features).Add(ExtensionUiFeature.CustomTerminalComponent) };

        public Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default) =>
            (mode() ?? throw new NotSupportedException("ctx.ui.custom() needs the interactive mode")).ShowExtensionCustomComponentAsync(component, context, token);
        public Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default)
        { mode()?.SignalExtensionCustomComponent(identity, done: true); return Task.CompletedTask; }
        public Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default)
        { mode()?.SignalExtensionCustomComponent(identity, done: false); return Task.CompletedTask; }

        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null,
            CancellationToken cancellationToken = default) => inner.SelectAsync(title, choices, options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null,
            CancellationToken cancellationToken = default) => inner.ConfirmAsync(title, message, options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null,
            CancellationToken cancellationToken = default) => inner.InputAsync(title, placeholder, options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default) =>
            inner.EditorAsync(title, prefill, cancellationToken);
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification,
            CancellationToken cancellationToken = default) => inner.PublishAsync(notification, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
