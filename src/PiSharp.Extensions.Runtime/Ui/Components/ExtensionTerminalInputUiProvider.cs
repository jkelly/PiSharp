using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

/// <summary>
/// Connects the existing callback UI to a separately admitted persistent terminal owner.
/// The host supplies and owns the selected scope; this provider acquires no terminal effects.
/// </summary>
public sealed class ExtensionTerminalInputUiProvider : IExtensionUiProvider
{
    private readonly IExtensionUiProvider inner;
    private readonly Func<IExtensionContext, ExtensionTerminalInputHub.Scope> selectOwnerScope;
    private readonly Func<IExtensionContext, IExtensionTerminalInput> selectRegistrationSink;
    public ExtensionTerminalInputUiProvider(IExtensionUiProvider inner,
        Func<IExtensionContext, ExtensionTerminalInputHub.Scope> selectOwnerScope)
        : this(inner, selectOwnerScope, context => selectOwnerScope(context)) { }
    public ExtensionTerminalInputUiProvider(IExtensionUiProvider inner,
        Func<IExtensionContext, ExtensionTerminalInputHub.Scope> selectOwnerScope,
        Func<IExtensionContext, IExtensionTerminalInput> selectRegistrationSink)
    {
        ArgumentNullException.ThrowIfNull(selectRegistrationSink);
        ArgumentNullException.ThrowIfNull(inner); ArgumentNullException.ThrowIfNull(selectOwnerScope);
        this.inner = inner; this.selectOwnerScope = selectOwnerScope; this.selectRegistrationSink = selectRegistrationSink;
    }
    public IExtensionUiScope OpenScope(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var persistent = selectOwnerScope(context) ?? throw new InvalidOperationException("No admitted terminal owner scope.");
        if (context.OwnerGeneration <= 0 || string.IsNullOrEmpty(context.OwnerId) ||
            persistent.Context.OwnerGeneration != context.OwnerGeneration ||
            !string.Equals(persistent.Context.OwnerId, context.OwnerId, StringComparison.Ordinal))
            throw new InvalidOperationException("Terminal input scope does not belong to the callback owner generation.");
        var sink = selectRegistrationSink(context) ?? throw new InvalidOperationException("No native terminal registration sink.");
        return new CallbackScope(inner.OpenScope(context), context, sink);
    }
    private sealed class CallbackScope(IExtensionUiScope inner, IExtensionContext context,
        IExtensionTerminalInput persistent) : IExtensionUiScope, IExtensionTerminalInput, IExtensionCustomComponentUi, IExtensionToolComponentUi
    {
        private readonly object gate = new();
        private bool closed;
        public ExtensionUiCapabilities Capabilities => inner.Capabilities;
        private void RequireRegistration()
        {
            if (closed || context.OperationCancellationToken.IsCancellationRequested ||
                context.SessionCancellationToken.IsCancellationRequested || context.ExtensionLifetimeCancellationToken.IsCancellationRequested)
                throw new ObjectDisposedException("Terminal input callback UI");
        }
        public IDisposable OnTerminalInput(ExtensionTerminalInputHandler handler)
        { lock (gate) { RequireRegistration(); return persistent.OnTerminalInput(handler); } }
        public IDisposable OnTerminalInputAsync(ExtensionTerminalInputAsyncHandler handler)
        { lock (gate) { RequireRegistration(); return persistent.OnTerminalInputAsync(handler); } }
        private IExtensionCustomComponentUi Custom()
        {
            lock (gate)
            {
                RequireRegistration();
                return inner as IExtensionCustomComponentUi ?? throw new InvalidOperationException("No actual native custom component broker.");
            }
        }
        public Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default) => Custom().OpenCustomComponentAsync(component, token);
        public Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default) => Custom().SignalCustomComponentDoneAsync(identity, token);
        public Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default) => Custom().InvalidateCustomComponentAsync(identity, token);
        public IExtensionToolComponentPresentation AttachToolComponent(ExtensionCustomComponentCallbacks source)
        {
            lock (gate)
            {
                RequireRegistration();
                return (inner as IExtensionToolComponentUi ?? throw new InvalidOperationException("No actual terminal tool row host.")).AttachToolComponent(source);
            }
        }
        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices,
            ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) =>
            inner.SelectAsync(title, choices, options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message,
            ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) =>
            inner.ConfirmAsync(title, message, options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null,
            ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) =>
            inner.InputAsync(title, placeholder, options, cancellationToken);
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null,
            CancellationToken cancellationToken = default) => inner.EditorAsync(title, prefill, cancellationToken);
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification,
            CancellationToken cancellationToken = default) => inner.PublishAsync(notification, cancellationToken);
        public ValueTask DisposeAsync()
        {
            lock (gate) closed = true;
            // Return the actual inner cleanup unchanged; persistent input survives callback return.
            return inner.DisposeAsync();
        }
    }
}
