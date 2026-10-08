using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    /// <summary>Await one host-requested shutdown notification before disposing the registry.
    /// Returns false when the captured snapshot has no shutdown handlers. Does not stop session
    /// admission, dispose extensions, or establish terminal shutdown ordering.</summary>
    public async ValueTask<bool> DispatchSessionShutdownAsync(ExtensionRegistrySnapshot captured, JsonData observation,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? reportDiagnostic = null,
        ExtensionSessionSnapshot? retainedSessionSnapshot = null)
    {
        const string topic = "session_shutdown";
        const string operation = "dispatch-session-shutdown";
        ValidateDispatchData(observation, operation);
        if (observation.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !observation.Value.TryGetProperty("type", out var type) ||
            type.ValueKind != System.Text.Json.JsonValueKind.String || type.GetString() != topic ||
            !observation.Value.TryGetProperty("reason", out var reason) ||
            reason.ValueKind != System.Text.Json.JsonValueKind.String ||
            reason.GetString() is not ("quit" or "reload" or "new" or "resume" or "fork") ||
            observation.Value.TryGetProperty("targetSessionFile", out var target) &&
                target.ValueKind != System.Text.Json.JsonValueKind.String)
            throw Failure(ExtensionRegistrationFailure.InvalidDescriptor, "registry", operation);
        var retained = retainedSessionSnapshot is null ? null : OwnSessionSnapshot(retainedSessionSnapshot, "registry");
        var admission = Admit(captured, RegistrationKind.Observation, topic, operation,
            CancellationToken.None, CancellationToken.None);
        using var shutdownLifetime = new CancellationTokenSource();
        using var dispatchFrame = new CallbackFrame(admission.Select(item => item.Scope).Distinct().ToImmutableArray());
        Exception? failure = null;
        try
        {
            // Lease the complete captured list before invoking any handler, just as other native
            // lifecycle notifications do. Never allow a participant to await its own disposal.
            foreach (var (scope, entry) in admission)
            {
                try
                {
                    using var frame = new CallbackFrame(scope);
                    // Retired attachment tokens and action brokers cannot supply shutdown authority.
                    // The caller retained the final acknowledged view before stopping that attachment.
                    var context = new ShutdownReadContext(scope, retained, shutdownLifetime.Token);
                    await ((ExtensionObservationDescriptor)entry.Descriptor).ObserveAsync(observation,
                        context, shutdownLifetime.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Shutdown handler failure (including OCE) is a reported notification failure,
                    // not a veto. An error reporter that throws still aborts dispatch, as Pi's
                    // emitError listener does; the outer finally releases every admitted lease.
                    if (reportDiagnostic is not null)
                        await reportDiagnostic(new(topic, scope.OwnerId, scope.OwnerGeneration,
                            entry.RegistrationId, ExtensionEventFailure.HandlerFailed),
                            CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            // End this operation's own lifetime only after the original handler/reporter work joins.
            // Listener failures remain secondary facts without masking the original dispatch failure.
            try { shutdownLifetime.Cancel(); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            finally { ReleaseAdmission(admission); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return admission.Length != 0;
    }

    private sealed class ShutdownReadContext(RegistrationScope scope, ExtensionSessionSnapshot? snapshot,
        CancellationToken shutdownToken) : IExtensionSessionContext, IExtensionUiContext
    {
        public string OwnerId => scope.OwnerId;
        public long OwnerGeneration => scope.OwnerGeneration;
        public CancellationToken OperationCancellationToken => shutdownToken;
        public CancellationToken SessionCancellationToken => CancellationToken.None;
        public CancellationToken ExtensionLifetimeCancellationToken => scope.ExtensionLifetimeCancellationToken;
        public ExtensionSessionSnapshot? SessionSnapshot => snapshot;
        public IExtensionUi Ui => UnavailableExtensionUiScope.Default;
    }
}
