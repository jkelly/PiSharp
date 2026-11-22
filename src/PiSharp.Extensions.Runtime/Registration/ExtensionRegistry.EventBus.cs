using System.Collections.Immutable;
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    private readonly ExtensionEventBus eventBus = new();
    private long eventBusSequence;
    private readonly Dictionary<RegistrationEntry, IExtensionEventBusSubscription> eventBusSubscriptions = new(ReferenceEqualityComparer.Instance);

    /// <summary>The registry's shared extension event bus, for a host that bridges another runtime's bus into it (the Pi Node host's
    /// pi.events): <see cref="ExtensionEventBus.Tap"/> observes native emissions, and Emit delivers the other runtime's.</summary>
    public ExtensionEventBus SharedEventBus => eventBus;

    /// <summary>Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (#11264), loader.ts <c>pi.events.on</c> and runner.ts
    /// <c>runtime.reportError</c>: receives the failure of an owner's event-bus listener as an extension error whose event name is
    /// <c>events:&lt;channel&gt;</c> (the message is the handler's exception message; the exception itself is the second argument),
    /// instead of the bus's console diagnostic. Null drops the failure, as upstream does before a runner exists. A listener that fails
    /// after its owner was closed or withdrawn (the extension runtime was replaced), or while the registry closes, is never reported.
    /// The host's own <see cref="SharedEventBus"/> listeners keep the bus's diagnostic.</summary>
    public Action<ExtensionEventDiagnostic, Exception>? EventBusErrorReporter { get; set; }

    private void ReportEventBusFailure(RegistrationScope scope, RegistrationEntry entry, string channel, Exception error)
    {
        Action<ExtensionEventDiagnostic, Exception>? report;
        lock (gate)
        {
            // runner.ts reportError: a handler still running when its runtime was replaced must not report into the replacement.
            if (closing || scope.Withdrawn || scope.State is RegistrationScopeState.Closing or RegistrationScopeState.Disposed ||
                !owners.TryGetValue(scope.OwnerId, out var current) || !ReferenceEquals(current, scope)) return;
            report = EventBusErrorReporter;
        }
        if (report is null) return;
        try
        {
            report(new ExtensionEventDiagnostic("events:" + channel, scope.OwnerId, scope.OwnerGeneration, entry.RegistrationId,
                ExtensionEventFailure.HandlerFailed) { Message = error.Message }, error);
        }
        catch { /* A reporter failure is isolated, as the bus's own diagnostic failure is. */ }
    }

    internal void EmitEventBus(RegistrationScope scope, string channel, object? data)
    {
        scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        lock (gate) EnsureScope(scope, "emit-event-bus", allowInitializing: true);
        eventBus.Emit(channel, data);
    }

    internal IDisposable SubscribeEventBus(RegistrationScope scope, string channel, Action<object?> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);
        scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureScope(scope, "subscribe-event-bus", allowInitializing: true);
            string id;
            do { id = "eventbus-" + checked(++eventBusSequence); } while (scope.Staged.ContainsId(id));
            if (!RegistrationPolicy.Identifier(id, options.MaximumIdentifierCharacters))
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, scope.OwnerId, "subscribe-event-bus");
            var registration = Add(scope, id, id, RegistrationKind.EventBus, handler,
                (long)id.Length * 2 + channel.Length, "subscribe-event-bus");
            var entry = scope.Staged.Entries.Single(candidate => candidate.RegistrationId == id);
            try
            {
                var listener = eventBus.On(channel, data => DeliverEventBus(scope, entry, channel, handler, data));
                TrackEventBusSubscription(entry, (IExtensionEventBusSubscription)listener);
                return new EventBusRegistration(scope, (IExtensionEventBusSubscription)listener, registration);
            }
            catch { registration.Dispose(); throw; }
        }
    }

    internal IExtensionEventBusSubscription SubscribeEventBusAsync(RegistrationScope scope, string channel, Func<object?, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);
        scope.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureScope(scope, "subscribe-event-bus-async", allowInitializing: true);
            string id;
            do { id = "eventbus-" + checked(++eventBusSequence); } while (scope.Staged.ContainsId(id));
            if (!RegistrationPolicy.Identifier(id, options.MaximumIdentifierCharacters))
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, scope.OwnerId, "subscribe-event-bus-async");
            var registration = Add(scope, id, id, RegistrationKind.EventBus, handler,
                (long)id.Length * 2 + channel.Length, "subscribe-event-bus-async");
            var entry = scope.Staged.Entries.Single(candidate => candidate.RegistrationId == id);
            try
            {
                var listener = eventBus.OnAsync(channel, data => DeliverEventBusAsync(scope, entry, channel, handler, data));
                TrackEventBusSubscription(entry, listener);
                return new EventBusRegistration(scope, listener, registration);
            }
            catch { registration.Dispose(); throw; }
        }
    }

    private void TrackEventBusSubscription(RegistrationEntry entry, IExtensionEventBusSubscription listener)
    {
        // Called under registry gate, with the charged entry already staged. Never track owner wrappers.
        try { eventBusSubscriptions.Add(entry, listener); }
        catch { listener.Dispose(); throw; }
    }

    private void RetireEventBusSubscription(RegistrationEntry entry)
    {
        // Remove/CloseScope already fenced entry admission under registry gate. Physical retirement
        // only takes the emitter gate: no user callbacks, cancellation-registration joins or task waits.
        // Pending deliveries are retained by the bus and their original registry leases until settlement.
        if (eventBusSubscriptions.Remove(entry, out var listener)) listener.Dispose();
    }
    internal ValueTask DrainEventBus(RegistrationScope scope)
    {
        GuardEventBusSettlement(scope);
        return eventBus.DrainAsync();
    }

    internal void GuardEventBusSettlement(RegistrationScope scope)
    {
        if (CallbackFrame.IsExecuting(scope))
            throw Failure(ExtensionRegistrationFailure.ReentrantDisposal, scope.OwnerId, "drain-event-bus");
    }

    private async Task DeliverEventBusAsync(RegistrationScope scope, RegistrationEntry entry, string channel,
        Func<object?, Task> handler, object? data)
    {
        ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> admission;
        lock (gate)
        {
            if (closing || scope.State != RegistrationScopeState.Active || !entry.Registered ||
                !owners.TryGetValue(scope.OwnerId, out var current) || !ReferenceEquals(current, scope) ||
                !scope.Staged.Contains(entry)) return;
            admission = Admit(snapshot, RegistrationKind.EventBus, entry.Name, "event-bus-async-callback",
                default, default, maximumSelected: 1);
        }
        try
        {
            using var frame = new CallbackFrame(scope);
            Task? original = null;
            try
            {
                // The user's one actual original remains admitted through settlement and diagnosis.
                original = handler(data) ?? throw new InvalidOperationException("Event listener returned no original Task.");
                await original.ConfigureAwait(false);
            }
            // The caught exception is what the listener threw or its task's first fault (JavaScript's caught rejection).
            catch (Exception error) { ReportEventBusFailure(scope, entry, channel, error); }
        }
        finally { ReleaseAdmission(admission); }
    }
    private void DeliverEventBus(RegistrationScope scope, RegistrationEntry entry, string channel, Action<object?> handler, object? data)
    {
        ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> admission;
        lock (gate)
        {
            // The shared emitter's snapshot grants no owner authority. Check the live entry
            // and admit it under the same gate used by CloseScope and QuiesceScopeAsync.
            if (closing || scope.State != RegistrationScopeState.Active || !entry.Registered ||
                !owners.TryGetValue(scope.OwnerId, out var current) || !ReferenceEquals(current, scope) ||
                !scope.Staged.Contains(entry)) return;
            admission = Admit(snapshot, RegistrationKind.EventBus, entry.Name, "event-bus-callback",
                default, default, maximumSelected: 1);
        }
        try
        {
            using var frame = new CallbackFrame(scope);
            try { handler(data); }
            catch (Exception error) { ReportEventBusFailure(scope, entry, channel, error); }
        }
        finally { ReleaseAdmission(admission); }
    }

    private sealed class EventBusRegistration(RegistrationScope scope, IExtensionEventBusSubscription listener, IExtensionRegistration registration) : IExtensionEventBusSubscription
    {
        public void Dispose()
        {
            // Retire the charged entry first. A concurrently captured emitter snapshot then
            // cannot enter user code; already admitted callbacks keep the entry's lease.
            registration.Dispose();
            listener.Dispose();
        }

        public ValueTask DrainAsync()
        {
            scope.Registry.GuardEventBusSettlement(scope);
            return listener.DrainAsync();
        }

        public ValueTask DisposeAsync()
        {
            scope.Registry.GuardEventBusSettlement(scope);
            Dispose();
            return listener.DrainAsync();
        }
    }
}
