using PiSharp.Contracts;
namespace PiSharp.Extensions.Runtime;
public sealed partial class ExtensionRegistry
{
    private async ValueTask<TerminalInputContextLease> CreateTerminalInputContextAsync(RegistrationScope scope, CancellationToken operationToken, CancellationToken sessionToken)
    {
        var context = new ExtensionContext(scope, operationToken, sessionToken);
        IExtensionSessionActionScope? suppliedActions = null;
        // Adopt supplied authority before every policy setup/validation so failure owns its cleanup.
        var acquired = suppliedActions;
        TerminalInputContextLease lease;
        try
        {
            if (sessionProvider is IExtensionSessionToolActivationProvider)
                context.ConfigureToolActivation(names =>
                {
                    if (names.IsDefault || names.Length > 4096 ||
                        names.Any(name => !RegistrationPolicy.Identifier(name, options.MaximumIdentifierCharacters)) ||
                        names.Sum(name => (long)name.Length) > options.MaximumJsonCharacters)
                        throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, "set-active-tools");
                });
            if (sessionProvider is IExtensionSessionCompactionProvider)
                context.ConfigureCompaction(summary =>
                {
                    const string operation = "append-compaction-summary";
                    if (summary is null || summary.Text is null || !RegistrationPolicy.Scalars(summary.Text) ||
                        summary.FirstKeptEntryId is not null && !RegistrationPolicy.Scalars(summary.FirstKeptEntryId) ||
                        (long)summary.Text.Length + (summary.FirstKeptEntryId?.Length ?? 4) + (summary.Details?.ToString().Length ?? 4) > options.MaximumJsonCharacters ||
                        summary.Details is not null && !RegistrationPolicy.Json(summary.Details, options))
                        throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, operation);
                    if (summary.Usage is not null)
                    {
                        var wire = PiWireJson.WriteMessage(new("summary", "summary", "summary", 0, [], summary.Usage, StopReason.Stop));
                        if (!RegistrationPolicy.Json(wire, options)) throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, operation);
                    }
                }, (staged, token) =>
                {
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context);
                    var owned = OwnSessionSnapshot(staged, context.OwnerId);
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context); return owned;
                });
            if (sessionProvider is IExtensionSessionContextEditProvider)
                context.ConfigureContextEdits((target, replacement) =>
                {
                    const string operation = "append-context-edit";
                    if (target is null || target.Length > options.MaximumJsonCharacters || !RegistrationPolicy.Scalars(target) ||
                        replacement is not null && !RegistrationPolicy.Json(replacement, options) ||
                        (long)target.Length + (replacement?.ToString().Length ?? 4) > options.MaximumJsonCharacters)
                        throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, context.OwnerId, operation);
                }, (staged, token) =>
                {
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context);
                    var owned = OwnSessionSnapshot(staged, context.OwnerId);
                    token.ThrowIfCancellationRequested(); ThrowIfContextCancelled(context);
                    return owned;
                });
            if (suppliedActions is not null)
            {
                context.SetSessionActions(suppliedActions);
                context.SetSessionSnapshot(OwnSessionSnapshot(suppliedActions.Snapshot, context.OwnerId));
            }
            else if (sessionProvider is not null)
            {
                ThrowIfContextCancelled(context);
                var captured = sessionProvider.Capture(context);
                ThrowIfContextCancelled(context);
                if (captured is not null)
                {
                    var snapshot = OwnSessionSnapshot(captured, context.OwnerId);
                    context.SetSessionSnapshot(snapshot);
                    if (sessionProvider is IExtensionSessionActionProvider actions)
                    {
                        acquired = actions.OpenScope(context, snapshot);
                        context.SetSessionActions(acquired);
                    }
                }
                ThrowIfContextCancelled(context);
            }
            lease = new(context, uiProvider);
        }
        catch (Exception acquisitionError)
        {
            Task? cleanupOriginal = null;
            Exception? cleanupError = null;
            if (acquired is not null)
                try
                {
                    var cleanup = acquired.DisposeAsync(); cleanupOriginal = cleanup.AsTask();
                    await cleanupOriginal.ConfigureAwait(false);
                }
                catch (Exception error) { cleanupError = cleanupOriginal?.Exception ?? error; }
            if (cleanupError is not null)
                throw new AggregateException("Terminal context acquisition and original cleanup failed.", acquisitionError, cleanupError);
            if (acquisitionError is OperationCanceledException cancelled &&
                (cancelled.CancellationToken == operationToken && operationToken.IsCancellationRequested ||
                 cancelled.CancellationToken == sessionToken && sessionToken.IsCancellationRequested ||
                 cancelled.CancellationToken == scope.ExtensionLifetimeCancellationToken && scope.ExtensionLifetimeCancellationToken.IsCancellationRequested))
                throw;
            // A synchronous provider faulted OCE is a fault, not a cancelled async helper adaptation.
            throw new AggregateException("Terminal context acquisition failed.", acquisitionError);
        }
        return lease;
    }

    // Raw contexts do not own command replacement leases. Keep their action/UI cleanup originals
    // here instead of changing the global callback lease used by unrelated extension APIs.
    private sealed class TerminalInputContextLease : IAsyncDisposable
    {
        internal ExtensionContext Context { get; }
        private readonly IExtensionUiScope ui;
        internal TerminalInputContextLease(ExtensionContext context, IExtensionUiProvider? provider)
        {
            Context = context; ui = provider?.OpenScope(context) ?? new UnavailableExtensionUiScope(context);
            context.SetUi(ui);
        }
        public async ValueTask DisposeAsync()
        {
            var faults = new List<Exception>();
            async Task Join(Func<ValueTask> acquire)
            {
                Task? original = null;
                try { var cleanup = acquire(); original = cleanup.AsTask(); await original.ConfigureAwait(false); }
                catch (Exception error) { faults.Add(original?.Exception ?? error); }
            }
            if (Context.SessionActions is { } actions) await Join(actions.DisposeAsync).ConfigureAwait(false);
            await Join(ui.DisposeAsync).ConfigureAwait(false);
            if (faults.Count != 0) throw new AggregateException("Terminal context original cleanup failed.", faults);
        }
    }
}
