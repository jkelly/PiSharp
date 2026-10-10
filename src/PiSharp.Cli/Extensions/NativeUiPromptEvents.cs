// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts (wrapUIPromptContext,
// withUIPrompt, emitUIPromptEvent) and core/extensions/types.ts (UIPromptStartEvent, UIPromptEndEvent).
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions;

/// <summary>Source ui_prompt_start/ui_prompt_end: while an extension waits on a blocking dialog (select, confirm, input, editor),
/// extensions are told the host is waiting on the user. Nested prompts report once, for the outermost prompt. The events are
/// published asynchronously in raise order, as Pi queues them (one dispatch after another). A custom terminal component (ctx.ui.custom) is a "custom" prompt from its open until
/// done(). Scopes keep exactly the capability interfaces of the scope they wrap.</summary>
internal sealed class NativeUiPromptEvents(IExtensionUiProvider inner) : IExtensionUiProvider
{
    private readonly object _gate = new();
    private int _depth; private (string Kind, string? Title)? _active;
    private (ExtensionRegistry Registry, ExtensionRegistrySnapshot Snapshot, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? Report)? _target;

    internal void Bind(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot, Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report)
    { lock (_gate) _target = (registry, snapshot, report); }

    public IExtensionUiScope OpenScope(IExtensionContext context)
    {
        var scope = inner.OpenScope(context);
        return scope switch
        {
            IExtensionCustomComponentUi and IExtensionToolComponentUi and IExtensionTerminalInput => new TerminalScope(this, scope),
            IExtensionCustomComponentUi and not IExtensionToolComponentUi and not IExtensionTerminalInput => new CustomScope(this, scope),
            IExtensionToolComponentUi or IExtensionTerminalInput => scope, // No host composes these without custom components.
            _ => new Scope(this, scope)
        };
    }

    private IDisposable Prompt(string kind, string? title)
    {
        lock (_gate) if (_depth++ == 0) { _active = (kind, title); Publish("ui_prompt_start", kind, title); }
        return new Finish(this, kind, title);
    }
    private void End(string kind, string? title)
    {
        lock (_gate)
        {
            if (--_depth > 0) return;
            _depth = 0; var prompt = _active ?? (kind, title); _active = null;
            Publish("ui_prompt_end", prompt.Kind, prompt.Title);
        }
    }
    private void Publish(string type, string kind, string? title)
    {
        if (_target is not { } target || !target.Registry.HasObservers(target.Snapshot, type)) return;
        var value = NativeSessionEventBinding.Json(writer =>
        {
            writer.WriteString("type", type); writer.WriteString("reason", "ui_prompt"); writer.WriteString("kind", kind);
            if (!string.IsNullOrEmpty(title)) writer.WriteString("title", title);
        });
        // runner.ts emitUIPromptEvent: queued in raise order (queueMicrotask), never awaited by the dialog. Each dispatch waits for
        // the previous one, so every observer sees ui_prompt_start before its ui_prompt_end.
        _ = _queue.Enqueue(() => target.Registry.DispatchObservationsReportingAsync(target.Snapshot, type, value, target.Report).AsTask());
    }
    private readonly OrderedObservationQueue _queue = new();
    private sealed class Finish(NativeUiPromptEvents owner, string kind, string? title) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.End(kind, title); }
    }

    /// <summary>Source withUIPrompt("custom", undefined, ...) around ui.custom(): the open lasts until the component is done.</summary>
    private class CustomScope(NativeUiPromptEvents owner, IExtensionUiScope inner) : Scope(owner, inner), IExtensionCustomComponentUi
    {
        private IExtensionCustomComponentUi Custom => (IExtensionCustomComponentUi)Inner;
        public async Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default)
        { using (Owner.Prompt("custom", null)) await Custom.OpenCustomComponentAsync(component, token).ConfigureAwait(false); }
        public Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default) =>
            Custom.SignalCustomComponentDoneAsync(identity, token);
        public Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default) =>
            Custom.InvalidateCustomComponentAsync(identity, token);
    }

    private sealed class TerminalScope(NativeUiPromptEvents owner, IExtensionUiScope inner) : CustomScope(owner, inner), IExtensionToolComponentUi, IExtensionTerminalInput
    {
        public IExtensionToolComponentPresentation AttachToolComponent(ExtensionCustomComponentCallbacks source) =>
            ((IExtensionToolComponentUi)Inner).AttachToolComponent(source);
        public IDisposable OnTerminalInput(ExtensionTerminalInputHandler handler) => ((IExtensionTerminalInput)Inner).OnTerminalInput(handler);
        public IDisposable OnTerminalInputAsync(ExtensionTerminalInputAsyncHandler handler) => ((IExtensionTerminalInput)Inner).OnTerminalInputAsync(handler);
    }

    private class Scope(NativeUiPromptEvents owner, IExtensionUiScope inner) : IExtensionUiScope
    {
        protected NativeUiPromptEvents Owner => owner;
        protected IExtensionUiScope Inner => inner;
        public ExtensionUiCapabilities Capabilities => inner.Capabilities;
        public async ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null,
            CancellationToken cancellationToken = default)
        { using (owner.Prompt("select", title)) return await inner.SelectAsync(title, choices, options, cancellationToken).ConfigureAwait(false); }
        public async ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null,
            CancellationToken cancellationToken = default)
        { using (owner.Prompt("confirm", title)) return await inner.ConfirmAsync(title, message, options, cancellationToken).ConfigureAwait(false); }
        public async ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null,
            CancellationToken cancellationToken = default)
        { using (owner.Prompt("input", title)) return await inner.InputAsync(title, placeholder, options, cancellationToken).ConfigureAwait(false); }
        public async ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken cancellationToken = default)
        { using (owner.Prompt("editor", title)) return await inner.EditorAsync(title, prefill, cancellationToken).ConfigureAwait(false); }
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken cancellationToken = default) =>
            inner.PublishAsync(notification, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
