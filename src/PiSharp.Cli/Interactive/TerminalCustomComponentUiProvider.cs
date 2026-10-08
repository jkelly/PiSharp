using System.Collections.Immutable;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Interactive;

/// <summary>One actual terminal custom presentation, with signal acknowledgements and an owned open
/// task. The provider borrows the view/input host; it neither opens nor disposes a terminal.</summary>
internal sealed class TerminalCustomComponentUiProvider(IExtensionUiProvider inner, TerminalSessionView view,
    TerminalExtensionInputAdmission input, Func<long> captureSessionGeneration) : IExtensionUiProvider
{
    private readonly TerminalExtensionInputAdmission terminalInput = input;
    private readonly Func<long> captureGeneration = captureSessionGeneration;
    private readonly object gate = new();
    private Active? active;
    private TerminalToolComponentUiBroker? toolBroker;
    private TerminalToolComponentUiBroker Tools()
    { lock (gate) return toolBroker ??= new(view, captureGeneration); }
    public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope(this, inner.OpenScope(context), context);

    private async Task OpenAsync(IExtensionContext context, ExtensionCustomComponentCallbacks source, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        var identity = source.Identity;
        if (identity.OwnerId != context.OwnerId || identity.OwnerGeneration != context.OwnerGeneration ||
            identity.SessionGeneration != captureGeneration()) throw new InvalidOperationException("Stale custom component owner/session.");
        var owned = new Active(identity);
        lock (gate) { if (active is not null) throw new InvalidOperationException("Native custom presentation already owned."); active = owned; }
        var errors = new List<Exception>(); var originals = new List<Task>();
        ExtensionRegistry.RegisteredExtensionComponent? participant = null;
        IDisposable? physicalInput = null; TerminalCustomComponentPresentation? presentation = null;
        bool focusAcquired = false, presentationInstalled = false, previousEditorFocus = false;
        try
        {
            participant = ExtensionRegistry.BindCustomComponentContext(context, identity.ComponentId,
                identity.SessionGeneration, captureGeneration,
                () => { owned.Done.TrySetResult(); return Task.CompletedTask; }, source.Dispose,
                context.SessionCancellationToken);
            owned.Participant = participant;
            presentation = new(identity.OwnerId, identity.OwnerGeneration, identity.SessionGeneration, identity.ComponentId,
                (width, renderToken) => participant.InvokeAsync(async (fresh, actualToken) =>
                {
                    Task<ExtensionCustomComponentRows>? original = null;
                    try
                    {
                        original = source.Render(width, fresh, actualToken) ?? throw new InvalidOperationException("Missing source render original.");
                        var rows = await original.ConfigureAwait(false); return new TerminalCustomComponentRows(rows.Rows, rows.CellWidths);
                    }
                    catch (Exception error) { throw new AggregateException("Source render original failed.", original?.Exception ?? error); }
                }, renderToken));
            owned.Presentation = presentation;
            // The real input subscription is itself a registry participant; component callbacks enter
            // their distinct persistent marker and keep both originals admitted through settlement.
            physicalInput = terminalInput.RegistrationSink(context).OnTerminalInputAsync(async (data, _, originalToken) =>
            {
                var original = participant.InvokeAsync(async (fresh, actualToken) =>
                {
                    Task? callback = null;
                    try { callback = source.Input(data, fresh, actualToken) ?? throw new InvalidOperationException("Missing source input original."); await callback.ConfigureAwait(false); }
                    catch (Exception error) { throw new AggregateException("Source input original failed.", callback?.Exception ?? error); }
                    return true;
                }, originalToken);
                await original.ConfigureAwait(false); return new(Consume: true);
            });
            var captureFocus = view.CaptureCustomEditorFocusAsync(token); originals.Add(captureFocus);
            previousEditorFocus = await captureFocus.ConfigureAwait(false);
            // A failing focus original may already have changed actual focus before transport fails.
            // Capture restoration ownership before starting that effect.
            focusAcquired = true;
            var focus = view.SetCustomEditorFocusAsync(false, token); originals.Add(focus);
            await focus.ConfigureAwait(false);
            // Open may install its reference before a physical render faults, so cleanup always
            // attempts to clear that exact owner once this installation acquisition starts.
            presentationInstalled = true;
            var install = view.OpenCustomComponentAsync(presentation, token).AsTask(); originals.Add(install);
            await install.ConfigureAwait(false);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, context.OperationCancellationToken,
                context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
            var completed = owned.Done.Task.WaitAsync(stop.Token); originals.Add(completed);
            await completed.ConfigureAwait(false);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            // Fence raw delivery first. A signal never waits for this enclosing open/finally.
            try { physicalInput?.Dispose(); } catch (Exception error) { errors.Add(error); }
            lock (gate) owned.Retiring = true;
            if (participant is not null)
                try { var close = participant.DisposeAsync().AsTask(); originals.Add(close); await close.ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            Task[] paints; lock (gate) paints = owned.Paints.ToArray();
            originals.AddRange(paints);
            lock (gate) originals.AddRange(owned.Observations);
            foreach (var paint in paints)
                try { await paint.ConfigureAwait(false); } catch (Exception error) { errors.Add(paint.Exception ?? error); }
            if (presentationInstalled && presentation is not null)
                try { var clear = view.CloseCustomComponentAsync(presentation, CancellationToken.None).AsTask(); originals.Add(clear); await clear.ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            if (focusAcquired)
                try { var restore = view.SetCustomEditorFocusAsync(previousEditorFocus, CancellationToken.None); originals.Add(restore); await restore.ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            lock (gate) { if (ReferenceEquals(active, owned)) active = null; }
            // Rejoin exact originals independently. Task.Exception retains full sibling vectors.
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original.ConfigureAwait(false); } catch (Exception error) { errors.Add(original.Exception ?? error); }
        }
        if (errors.Count != 0) throw new AggregateException("Native custom open originals failed.", errors);
    }
    private Task Signal(ExtensionCustomComponentIdentity identity, bool invalidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var owner = active;
            if (owner is null || owner.Retiring || owner.Identity != identity ||
                identity.SessionGeneration != captureGeneration()) throw new InvalidOperationException("Stale custom component signal.");
            if (!invalidate) { owner.Done.TrySetResult(); return Task.CompletedTask; }
            if (owner.Participant is null || owner.Presentation is null || owner.Paints.Count >= 960)
                throw new InvalidOperationException("Native custom invalidation unavailable or exhausted.");
            var participant = owner.Participant; var presentation = owner.Presentation;
            // Queue an actual paint; acknowledge its admission promptly. Full settlement is owned by
            // the pending open and the participant, never by the render/input publishing this signal.
            var paint = participant.InvokeAsync(async (_, actualToken) =>
            { await view.RefreshCustomComponentAsync(presentation, actualToken).ConfigureAwait(false); return true; }, token);
            owner.Paints.Add(paint);
            // Observe immediately, retaining the exact original for final join and diagnosis.
            owner.Observations.Add(paint.ContinueWith(completed =>
            {
                if (completed.IsFaulted) { var full = completed.Exception!; lock (gate) owner.PaintErrors.Add(full); }
            }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            return Task.CompletedTask;
        }
    }
    private sealed class Active(ExtensionCustomComponentIdentity identity)
    {
        internal ExtensionCustomComponentIdentity Identity { get; } = identity;
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ExtensionRegistry.RegisteredExtensionComponent? Participant;
        internal TerminalCustomComponentPresentation? Presentation;
        internal bool Retiring;
        internal List<Task> Paints { get; } = [];
        internal List<Task> Observations { get; } = [];
        internal List<Exception> PaintErrors { get; } = [];
    }
    private sealed class Scope(TerminalCustomComponentUiProvider owner, IExtensionUiScope inner,
        IExtensionContext context) : IExtensionUiScope, IExtensionCustomComponentUi, IExtensionToolComponentUi
    {
        private int closed;
        public ExtensionUiCapabilities Capabilities => new(ExtensionUiMode.Tui, owner.terminalInput.ConnectionGeneration,
            owner.captureGeneration(), inner.Capabilities.Features.Contains(ExtensionUiFeature.CustomTerminalComponent)
                ? inner.Capabilities.Features : inner.Capabilities.Features.Add(ExtensionUiFeature.CustomTerminalComponent));
        public Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks source, CancellationToken token = default)
        { ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this); return owner.OpenAsync(context, source, token); }
        private void RequireSignalOwner(ExtensionCustomComponentIdentity identity)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this);
            context.OperationCancellationToken.ThrowIfCancellationRequested();
            context.SessionCancellationToken.ThrowIfCancellationRequested(); context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
            if (identity.OwnerId != context.OwnerId || identity.OwnerGeneration != context.OwnerGeneration)
                throw new InvalidOperationException("Custom signal belongs to another native owner.");
        }
        public Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default)
        { RequireSignalOwner(identity); return owner.Signal(identity, false, token); }
        public Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default)
        { RequireSignalOwner(identity); return owner.Signal(identity, true, token); }
        public IExtensionToolComponentPresentation AttachToolComponent(ExtensionCustomComponentCallbacks source)
        { RequireSignalOwner(source.Identity); return owner.Tools().Attach(context, source); }
        public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => inner.SelectAsync(title, choices, options, token);
        public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => inner.ConfirmAsync(title, message, options, token);
        public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken token = default) => inner.InputAsync(title, placeholder, options, token);
        public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken token = default) => inner.EditorAsync(title, prefill, token);
        public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken token = default) => inner.PublishAsync(notification, token);
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref closed, 1); return inner.DisposeAsync(); }
    }
}
