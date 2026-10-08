namespace PiSharp.CodingAgent;

public sealed partial class ReplaceableAgentSession
{
    private PersistentAgentSession.ReplacementReservation? initialRuntimeReservation;
    private PersistentAgentSession.ReplacementReservation? runtimeBindingReservation;
    private AgentSessionAttachment? runtimeBindingAttachment;

    // Repeated joins can observe the same original fault through different aggregate wrappers.
    // Preserve each original leaf once; distinct faults with equal messages remain distinct.
    private static void AddRuntimeBindingFailure(List<Exception> failures, Exception error)
    {
        if (error is AggregateException aggregate && aggregate.InnerExceptions.Count != 0)
        {
            foreach (var inner in aggregate.InnerExceptions) AddRuntimeBindingFailure(failures, inner);
        }
        else if (!failures.Any(existing => ReferenceEquals(existing, error))) failures.Add(error);
    }
    private void BindRuntimeUnderReservation(AgentSessionAttachment attachment,
        PersistentAgentSession.ReplacementReservation reservation)
    {
        var priorTransition = inTransition.Value; var priorRegistration = resourceRegistrationAttachment.Value;
        inTransition.Value = true; resourceRegistrationAttachment.Value = attachment;
        lock (gate) { runtimeBindingReservation = reservation; runtimeBindingAttachment = attachment; }
        try { attachment.Session.BindRuntimeOwner(this, attachment); }
        finally
        {
            lock (gate) { runtimeBindingReservation = null; runtimeBindingAttachment = null; }
            resourceRegistrationAttachment.Value = priorRegistration; inTransition.Value = priorTransition;
        }
    }

    public static async Task<ReplaceableAgentSession> WithLifecycleAndRuntimeBindingAsync(
        PersistentAgentSession initial, PersistentSessionLifecycle services)
    {
        ArgumentNullException.ThrowIfNull(initial); ArgumentNullException.ThrowIfNull(services);
        ReplaceableAgentSession? owner = null;
        owner = new(initial, (request, token) =>
        {
            var captured = owner!.Current;
            return services.OpenForAttachmentAsync(request, captured.Session.Snapshot.Agent.Model,
                checked(captured.Generation + 1), token);
        }, services, retainInitialReservation: true);
        var reservation = owner.initialRuntimeReservation!;
        try { owner.BindRuntimeUnderReservation(owner.Current, reservation); return owner; }
        catch (Exception bindingError)
        {
            var failures = new List<Exception>(); AddRuntimeBindingFailure(failures, bindingError);
            try { await owner.InitiateOwnedResourceStopsAsync(owner.Current).ConfigureAwait(false); } catch (Exception error) { AddRuntimeBindingFailure(failures, error); }
            try { await owner.RetireOwnedResourcesAsync(owner.Current, reservation, joinAll: true).ConfigureAwait(false); } catch (Exception error) { AddRuntimeBindingFailure(failures, error); }
            try { await initial.ReleaseRuntimeAfterBindingFailureAsync().ConfigureAwait(false); } catch (Exception error) { AddRuntimeBindingFailure(failures, error); }
            try { await reservation.RetireWriterAsync().ConfigureAwait(false); } catch (Exception error) { AddRuntimeBindingFailure(failures, error); }
            reservation.Dispose();
            try { await owner.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { AddRuntimeBindingFailure(failures, error); }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException("Initial runtime binding and owned cleanup failed.", failures);
        }
        finally { reservation.Dispose(); owner.initialRuntimeReservation = null; }
    }
    /// <summary>Capture the actual catalog for executable owner-binding proof. Reserved access is
    /// confined to this owner's exact registration callback; it grants no catalog mutation.</summary>
    public SessionRuntimeRegistry CaptureToolCatalogRegistryForBinding(AgentSessionAttachment attachment)
    {
        var captured = CaptureRuntimeBindingReservation(attachment);
        var registry = captured is null ? attachment.Session.CaptureToolCatalogRegistry() : captured.CaptureToolCatalogRegistry();
        ValidateAttachment(attachment);
        return registry;
    }
    /// <summary>Install a trusted host observer in the exact runtime-binding callback, or through
    /// the ordinary idle-session guard when no binding reservation is authorized.</summary>
    public void ConfigureCompactionObservationForBinding(AgentSessionAttachment attachment,
        Func<SessionCompactionObservation, ValueTask>? observer)
    {
        var captured = CaptureRuntimeBindingReservation(attachment);
        attachment.Session.ConfigureCompactionObservationForBinding(this, attachment, captured, observer);
    }
    /// <summary>Install a trusted host metadata observer without releasing runtime-binding authority.</summary>
    public void ConfigureSessionInfoObservationForBinding(AgentSessionAttachment attachment,
        Func<SessionInfoChanged, ValueTask>? observer)
    {
        var captured = CaptureRuntimeBindingReservation(attachment);
        attachment.Session.ConfigureSessionInfoObservationForBinding(this, attachment, captured, observer);
    }
    // Called under the session gate, using the same session -> owner order as reload
    // publication. Recheck the exact callback/reservation before assigning an observer.
    internal void ValidateRuntimeObservationBinding(AgentSessionAttachment attachment,
        PersistentAgentSession.ReplacementReservation? expected)
    {
        if (!ReferenceEquals(CaptureRuntimeBindingReservation(attachment), expected))
            throw new InvalidOperationException("Runtime observation binding authority changed.");
    }
    private PersistentAgentSession.ReplacementReservation? CaptureRuntimeBindingReservation(AgentSessionAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        PersistentAgentSession.ReplacementReservation? captured = null;
        lock (gate)
        {
            ValidateAttachment(attachment);
            if (inTransition.Value && ReferenceEquals(resourceRegistrationAttachment.Value, attachment) &&
                ReferenceEquals(runtimeBindingAttachment, attachment) && runtimeBindingReservation is { } reservation)
            {
                captured = reservation;
            }
        }
        return captured;
    }
}
