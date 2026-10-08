using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    private long terminalInputSequence;

    /// <summary>Connects an explicitly owned raw scope to native registration admission. No terminal is acquired.</summary>
    public IExtensionTerminalInput BindTerminalInput(RegistrationScope owner, ExtensionTerminalInputHub.Scope input,
        CancellationToken sessionCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);
        lock (gate)
        {
            EnsureScope(owner, "bind-terminal-input", allowInitializing: true);
            if (input.Context.OwnerId != owner.OwnerId || input.Context.OwnerGeneration != owner.OwnerGeneration ||
                input.Context.ExtensionLifetimeCancellationToken != owner.ExtensionLifetimeCancellationToken)
                throw new ArgumentException("The raw scope must belong to this actual extension lifetime.", nameof(input));
        }
        return new RegisteredTerminalInput(this, owner, input, sessionCancellationToken);
    }

    private IDisposable SubscribeTerminalInput(RegistrationScope owner, ExtensionTerminalInputHub.Scope input,
        CancellationToken session, ExtensionTerminalInputAsyncHandler handler)
    {
        owner.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        session.ThrowIfCancellationRequested();
        lock (gate)
        {
            EnsureScope(owner, "subscribe-terminal-input", allowInitializing: true);
            string id;
            do { id = "terminal-input-" + checked(++terminalInputSequence); } while (owner.Staged.ContainsId(id));
            if (!RegistrationPolicy.Identifier(id, options.MaximumIdentifierCharacters))
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, owner.OwnerId, "subscribe-terminal-input");
            // EventBus entries are charged, leased, and physically retired by the existing scope fence,
            // while remaining excluded from public descriptor snapshots.
            var registration = Add(owner, id, id, RegistrationKind.EventBus, new TerminalInputDescriptor(input, handler),
                (long)id.Length * 2, "subscribe-terminal-input");
            var entry = owner.Staged.Entries.Single(candidate => candidate.RegistrationId == id);
            try
            {
                var physical = input.OnTerminalInputAsync((data, _, token) =>
                    DeliverTerminalInputAsync(owner, entry, handler, data, token, session));
                var membership = new TerminalInputMembership(physical);
                TrackEventBusSubscription(entry, membership);
                return new TerminalInputRegistration(registration);
            }
            catch { registration.Dispose(); throw; }
        }
    }

    internal void RetireTerminalInput(RegistrationScope owner, ExtensionTerminalInputHub.Scope input)
    {
        lock (gate)
            foreach (var entry in owner.Staged.Entries.Where(entry => entry.Descriptor is TerminalInputDescriptor descriptor &&
                ReferenceEquals(descriptor.Input, input)).ToArray()) Remove(owner, entry);
    }

    private sealed record TerminalInputDescriptor(ExtensionTerminalInputHub.Scope Input, ExtensionTerminalInputAsyncHandler Handler);

    private async Task<ExtensionTerminalInputResult?> DeliverTerminalInputAsync(RegistrationScope owner,
        RegistrationEntry entry, ExtensionTerminalInputAsyncHandler handler, string data,
        CancellationToken operation, CancellationToken session)
    {
        ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> admission;
        lock (gate)
        {
            if (closing || owner.State != RegistrationScopeState.Active || !entry.Registered ||
                !owners.TryGetValue(owner.OwnerId, out var current) || !ReferenceEquals(current, owner) ||
                !owner.Staged.Contains(entry)) return null;
            admission = Admit(snapshot, RegistrationKind.EventBus, entry.Name, "terminal-input-callback",
                operation, session, maximumSelected: 1);
        }
        try
        {
            using var frame = new CallbackFrame(owner);
            TerminalInputContextLease? context = null;
            Task<TerminalInputContextLease>? originalContext = null;
            Task<ExtensionTerminalInputResult?>? original = null;
            Task? cleanup = null;
            ExtensionTerminalInputResult? result = null;
            var faults = new List<Exception>();
            bool cancelled = false;
            try
            {
                var contextCreation = CreateTerminalInputContextAsync(owner, operation, session);
                originalContext = contextCreation.AsTask();
                context = await originalContext.ConfigureAwait(false);
                original = handler(data, context.Context, operation) ??
                    throw new InvalidOperationException("Terminal input handler returned no original Task.");
                result = await original.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Preserve faulted OCE as a fault, together with every original aggregate sibling.
                if (original is { IsCanceled: true } || original is null && originalContext is { IsCanceled: true }) cancelled = true;
                else faults.Add(original?.Exception ?? originalContext?.Exception ?? error);
            }
            if (context is not null)
                try
                {
                    var originalCleanup = context.DisposeAsync();
                    cleanup = originalCleanup.AsTask();
                    await cleanup.ConfigureAwait(false);
                }
                catch (Exception error) { faults.Add(cleanup?.Exception ?? error); }
            if (faults.Count != 0) throw new AggregateException("Native terminal callback originals failed.", faults);
            if (cancelled) throw new OperationCanceledException(operation);
            return result;
        }
        finally { ReleaseAdmission(admission); }
    }

    private sealed class RegisteredTerminalInput(ExtensionRegistry registry, RegistrationScope owner,
        ExtensionTerminalInputHub.Scope input, CancellationToken session) : IExtensionTerminalInput
    {
        private readonly object subscriptionsGate = new();
        private readonly Dictionary<ExtensionTerminalInputHandler, IDisposable> synchronous = new(ReferenceEqualityComparer.Instance);
        public IDisposable OnTerminalInput(ExtensionTerminalInputHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (subscriptionsGate)
            {
                owner.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
                session.ThrowIfCancellationRequested();
                lock (registry.gate) registry.EnsureScope(owner, "subscribe-terminal-input", allowInitializing: true);
                if (synchronous.TryGetValue(handler, out var existing) && existing is SynchronousTerminalInputRegistration { Retired: false })
                    return existing;
                var original = registry.SubscribeTerminalInput(owner, input, session,
                    (data, _, _) => Task.FromResult(handler(data)));
                var registration = new SynchronousTerminalInputRegistration(original, () =>
                { lock (subscriptionsGate) synchronous.Remove(handler); });
                synchronous[handler] = registration; return registration;
            }
        }
        public IDisposable OnTerminalInputAsync(ExtensionTerminalInputAsyncHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return registry.SubscribeTerminalInput(owner, input, session, handler);
        }
    }

    private sealed class SynchronousTerminalInputRegistration(IDisposable original, Action remove) : IDisposable
    {
        private int retired;
        internal bool Retired => Volatile.Read(ref retired) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref retired, 1) != 0) return;
            try { original.Dispose(); } finally { remove(); }
        }
    }

    private sealed class TerminalInputRegistration(IExtensionRegistration registration) : IDisposable
    {
        private int retired;
        internal bool Retired => Volatile.Read(ref retired) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref retired, 1) == 0) registration.Dispose();
        }
    }

    private sealed class TerminalInputMembership(IDisposable physical) : IExtensionEventBusSubscription
    {
        // This object is called under the registry gate. Only physical unsubscription is permitted.
        public void Dispose() => physical.Dispose();
        public ValueTask DrainAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
