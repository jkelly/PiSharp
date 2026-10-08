using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

public sealed partial class ExtensionRegistry
{
    private long customComponentSequence;

    public static RegisteredExtensionComponent BindCustomComponentContext(IExtensionContext context, string componentId,
        long sessionGeneration, Func<long> captureSessionGeneration, Func<Task> retireHostSignal,
        Func<IExtensionContext, Task> disposeOriginal, CancellationToken sessionToken = default)
    {
        if (context is not ExtensionContext actual || !CallbackFrame.IsExecuting(actual.Scope))
            throw new InvalidOperationException("Custom components require an actual admitted native callback context.");
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        return actual.Scope.Registry.BindCustomComponent(actual.Scope, componentId, sessionGeneration,
            captureSessionGeneration, retireHostSignal, disposeOriginal, sessionToken);
    }

    /// <summary>Native host allocation, called under an actual owner callback. No terminal is acquired.</summary>
    public RegisteredExtensionComponent BindCustomComponent(RegistrationScope owner, string componentId,
        long sessionGeneration, Func<long> captureSessionGeneration, Func<Task> retireHostSignal,
        Func<IExtensionContext, Task> disposeOriginal, CancellationToken sessionToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentException.ThrowIfNullOrEmpty(componentId);
        ArgumentNullException.ThrowIfNull(captureSessionGeneration); ArgumentNullException.ThrowIfNull(retireHostSignal);
        ArgumentNullException.ThrowIfNull(disposeOriginal);
        if (sessionGeneration <= 0 || componentId.Length > options.MaximumIdentifierCharacters ||
            !RegistrationPolicy.Identifier(componentId, options.MaximumIdentifierCharacters))
            throw new ArgumentException("A bounded actual component/session identity is required.", nameof(componentId));
        RegisteredExtensionComponent result;
        lock (gate)
        {
            EnsureScope(owner, "bind-custom-component");
            if (!CallbackFrame.IsExecuting(owner))
                throw Failure(ExtensionRegistrationFailure.StaleSnapshot, owner.OwnerId, "bind-custom-component");
            string id;
            do { id = "custom-component-" + checked(++customComponentSequence); }
            while (owner.Staged.ContainsId(id));
            if (!RegistrationPolicy.Identifier(id, options.MaximumIdentifierCharacters))
                throw Failure(ExtensionRegistrationFailure.LimitExceeded, owner.OwnerId, "bind-custom-component");
            var registration = Add(owner, id, id, RegistrationKind.EventBus, componentId,
                (long)id.Length * 2 + componentId.Length, "bind-custom-component");
            var entry = owner.Staged.Entries.Single(candidate => candidate.RegistrationId == id);
            result = new(this, owner, entry, registration, componentId, sessionGeneration,
                captureSessionGeneration, retireHostSignal, disposeOriginal, sessionToken);
            try { TrackEventBusSubscription(entry, result.PhysicalMembership); }
            catch { registration.Dispose(); throw; }
        }
        // Cancellation registration can invoke a callback synchronously. Never acquire/join it under gate.
        try { result.AttachLifetime(); return result; }
        catch (Exception acquisitionError)
        {
            var original = result.CloseCore();
            try { original.GetAwaiter().GetResult(); }
            catch (Exception cleanupError) { throw new AggregateException(acquisitionError, original.Exception ?? cleanupError); }
            throw;
        }
    }

    private ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> AdmitCustomComponent(
        RegisteredExtensionComponent component, CancellationToken operation)
    {
        lock (gate)
        {
            var owner = component.Owner; var entry = component.Entry;
            if (component.Fenced || closing || owner.State != RegistrationScopeState.Active || !entry.Registered ||
                !owners.TryGetValue(owner.OwnerId, out var current) || !ReferenceEquals(current, owner) ||
                !owner.Staged.Contains(entry))
                throw Failure(ExtensionRegistrationFailure.StaleSnapshot, owner.OwnerId, "custom-component-callback");
            return Admit(snapshot, RegistrationKind.EventBus, entry.Name, "custom-component-callback",
                operation, component.SessionToken, maximumSelected: 1);
        }
    }

    /// <summary>Persistent actual registry participant. Every admitted original keeps ActiveCallbacks/Idle
    /// charged through source invocation, diagnosis and context cleanup. Retirement fences the physical
    /// entry before owner cancellation or task joins. The host retires the enclosing open separately.</summary>
    public sealed class RegisteredExtensionComponent : IAsyncDisposable
    {
        private readonly ExtensionRegistry registry;
        internal RegistrationScope Owner { get; }
        internal RegistrationEntry Entry { get; }
        internal CancellationToken SessionToken { get; }
        public string ComponentId { get; }
        public string ScopeId => Entry.RegistrationId;
        public long OwnerGeneration => Owner.OwnerGeneration;
        public long SessionGeneration { get; }
        public long CurrentSessionGeneration => captureSessionGeneration();
        public bool IsRetired => Fenced;
        /// <summary>The host cancels its actually owned callback lifetime under the same synchronous
        /// owner ancestry used by real linked parent cancellation. No cancellation task is detached.</summary>
        public void CancelOwnedCallbackLifetime(CancellationTokenSource original)
        {
            ArgumentNullException.ThrowIfNull(original);
            using var scope = new TerminalInputRegistryCleanupFrame(Owner);
            using var component = new CleanupFrame(this);
            original.Cancel();
        }
        public void AssertExternalObservation() => AssertExternalClose();
        private readonly IExtensionRegistration registration;
        private readonly Func<long> captureSessionGeneration;
        private readonly Func<Task> retireHostSignal;
        private readonly Func<IExtensionContext, Task> disposeOriginal;
        private readonly object participantGate = new();
        private readonly List<Task> pending = [];
        private readonly CancellationTokenSource stop = new();
        private readonly List<CancellationTokenRegistration> lifetimes = [];
        private readonly TaskCompletionSource lifetimeAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? closeCore, publicClose;
        private int fenced;
        internal bool Fenced => Volatile.Read(ref fenced) != 0;
        internal IExtensionEventBusSubscription PhysicalMembership { get; }

        internal RegisteredExtensionComponent(ExtensionRegistry registry, RegistrationScope owner, RegistrationEntry entry,
            IExtensionRegistration registration, string componentId, long sessionGeneration, Func<long> captureSessionGeneration,
            Func<Task> retireHostSignal, Func<IExtensionContext, Task> disposeOriginal, CancellationToken sessionToken)
        {
            this.registry = registry; Owner = owner; Entry = entry; this.registration = registration;
            ComponentId = componentId; SessionGeneration = sessionGeneration; SessionToken = sessionToken;
            this.captureSessionGeneration = captureSessionGeneration; this.retireHostSignal = retireHostSignal;
            this.disposeOriginal = disposeOriginal; PhysicalMembership = new Membership(this);
        }
        internal void AttachLifetime()
        {
            // No cancellation-registration join in the callback itself. Public external disposal detaches
            // only after this original close task has settled, avoiding joining our own registration.
            try
            {
                foreach (var token in new[] { Owner.ExtensionLifetimeCancellationToken, SessionToken }.Distinct())
                {
                    var acquired = token.UnsafeRegister(_ =>
                    {
                        using var ancestry = new TerminalInputRegistryCleanupFrame(Owner);
                        using var componentAncestry = new CleanupFrame(this);
                        CloseCore().GetAwaiter().GetResult();
                    }, null);
                    lock (participantGate) lifetimes.Add(acquired);
                }
            }
            finally { lifetimeAttached.TrySetResult(); }
        }

        public Task<T> InvokeAsync<T>(Func<IExtensionContext, CancellationToken, Task<T>> callback,
            CancellationToken operationToken = default)
        {
            ArgumentNullException.ThrowIfNull(callback);
            operationToken.ThrowIfCancellationRequested(); SessionToken.ThrowIfCancellationRequested();
            if (captureSessionGeneration() != SessionGeneration)
                throw new InvalidOperationException("Stale terminal component session generation.");
            // Registry -> participant is the only nested lock order. Physical retirement takes no lock.
            lock (registry.gate)
            {
                var admission = registry.AdmitCustomComponent(this, operationToken);
                try
                {
                    lock (participantGate)
                    {
                        if (Fenced || pending.Count >= 960)
                            throw new InvalidOperationException("Component callback admission is fenced or exhausted.");
                        Task<T> original;
                        // Preserve actual still-active native/component ancestor frames. Their active
                        // flags fence inherited stale frames after the originating callback settles.
                        original = Task.Run(() => Deliver(callback, operationToken, admission));
                        pending.Add(original); return original;
                    }
                }
                catch { registry.ReleaseAdmission(admission); throw; }
            }
        }

        private async Task<T> Deliver<T>(Func<IExtensionContext, CancellationToken, Task<T>> callback,
            CancellationToken caller, ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> admission)
        {
            try
            {
                using var frame = new CallbackFrame(Owner);
                using var componentFrame = new ComponentFrame(this);
                using var linked = new ComponentCancellation(this, caller, SessionToken, stop.Token,
                    Owner.ExtensionLifetimeCancellationToken);
                TerminalInputContextLease? context = null; Task<TerminalInputContextLease>? acquisition = null;
                Task<T>? original = null; Task? cleanup = null; T result = default!;
                var faults = new List<Exception>(); bool cancelled = false;
                try
                {
                    if (captureSessionGeneration() != SessionGeneration) throw new InvalidOperationException("Stale terminal component session generation.");
                    acquisition = registry.CreateTerminalInputContextAsync(Owner, linked.Token, SessionToken).AsTask();
                    context = await acquisition.ConfigureAwait(false);
                    original = callback(context.Context, linked.Token) ?? throw new InvalidOperationException("Component callback returned no original Task.");
                    result = await original.ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (original is { IsCanceled: true } || original is null && acquisition is { IsCanceled: true }) cancelled = true;
                    else faults.Add(original?.Exception ?? acquisition?.Exception ?? error);
                }
                if (context is not null)
                    try { var disposal = context.DisposeAsync(); cleanup = disposal.AsTask(); await cleanup.ConfigureAwait(false); }
                    catch (Exception error) { faults.Add(cleanup?.Exception ?? error); }
                if (faults.Count != 0) throw new AggregateException("Component callback originals failed.", faults);
                if (cancelled) throw new OperationCanceledException(linked.Token);
                return result;
            }
            finally { registry.ReleaseAdmission(admission); }
        }

        private void AssertExternalClose()
        {
            if (ComponentFrame.IsExecuting(this) || CleanupFrame.IsExecuting(this))
                throw Failure(ExtensionRegistrationFailure.ReentrantDisposal, Owner.OwnerId, "close-custom-component");
        }
        public ValueTask DisposeAsync()
        {
            AssertExternalClose();
            var original = CloseCore();
            lock (participantGate)
            {
                publicClose ??= DetachAfterClose(original); return new(publicClose);
            }
        }
        private async Task DetachAfterClose(Task original)
        {
            var faults = new List<Exception>();
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
            await lifetimeAttached.Task.ConfigureAwait(false);
            CancellationTokenRegistration[] registrations;
            lock (participantGate) registrations = lifetimes.ToArray();
            foreach (var acquired in registrations)
                try { acquired.Dispose(); } catch (Exception error) { faults.Add(error); }
            if (faults.Count != 0) throw new AggregateException("Component close and lifetime detach failed.", faults);
        }
        internal Task CloseCore()
        {
            registration.Dispose(); // Actual registry physical fencing precedes every cancellation/effect.
            lock (participantGate)
            {
                if (closeCore is not null) return closeCore;
                closeCore = Task.Run(CloseOriginals);
                return closeCore;
            }
        }
        private async Task CloseOriginals()
        {
            var faults = new List<Exception>(); Task? signal = null, disposal = null;
            // Only the real signal is acknowledged here. Joining the enclosing open would self-deadlock
            // its finally/close path; its existing native command admission joins that open separately.
            try { signal = retireHostSignal() ?? throw new InvalidOperationException("Missing host retirement signal original."); }
            catch (Exception error) { faults.Add(error); }
            using (new TerminalInputRegistryCleanupFrame(Owner))
            using (new CleanupFrame(this))
                try { stop.Cancel(); } catch (Exception error) { faults.Add(error); }
            Task[] originals; lock (participantGate) originals = pending.ToArray();
            foreach (var original in originals)
                try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
            if (signal is not null)
                try { await signal.ConfigureAwait(false); } catch (Exception error) { faults.Add(signal.Exception ?? error); }
            using (new CallbackFrame(Owner))
            using (new ComponentFrame(this))
                try
                {
                    disposal = disposeOriginal(new ExtensionContext(Owner, CancellationToken.None, SessionToken)) ??
                        throw new InvalidOperationException("Missing source component disposal original.");
                    await disposal.ConfigureAwait(false);
                }
                catch (Exception error) { faults.Add(disposal?.Exception ?? error); }
            try { stop.Dispose(); } catch (Exception error) { faults.Add(error); }
            if (faults.Count != 0) throw new AggregateException("Component retirement originals failed.", faults);
        }
        private sealed class Membership(RegisteredExtensionComponent owner) : IExtensionEventBusSubscription
        {
            public void Dispose() => Interlocked.Exchange(ref owner.fenced, 1);
            public ValueTask DrainAsync() { owner.AssertExternalClose(); return new(owner.CloseCore()); }
            public ValueTask DisposeAsync() => owner.DisposeAsync();
        }
        private sealed class ComponentFrame : IDisposable
        {
            private static readonly AsyncLocal<ComponentFrame?> Current = new();
            private readonly ComponentFrame? parent = Current.Value;
            private readonly RegisteredExtensionComponent owner;
            private int active = 1;
            internal ComponentFrame(RegisteredExtensionComponent owner) { this.owner = owner; Current.Value = this; }
            internal static bool IsExecuting(RegisteredExtensionComponent owner)
            {
                for (var frame = Current.Value; frame is not null; frame = frame.parent)
                    if (Volatile.Read(ref frame.active) != 0 && ReferenceEquals(frame.owner, owner)) return true;
                return false;
            }
            public void Dispose() { Interlocked.Exchange(ref active, 0); Current.Value = parent; }
        }
        private sealed class CleanupFrame : IDisposable
        {
            [ThreadStatic] private static CleanupFrame? current;
            private readonly CleanupFrame? parent = current;
            private readonly RegisteredExtensionComponent owner;
            internal CleanupFrame(RegisteredExtensionComponent owner) { this.owner = owner; current = this; }
            internal static bool IsExecuting(RegisteredExtensionComponent owner)
            {
                for (var frame = current; frame is not null; frame = frame.parent)
                    if (ReferenceEquals(frame.owner, owner)) return true;
                return false;
            }
            public void Dispose() => current = parent;
        }
        private sealed class ComponentCancellation : IDisposable
        {
            private readonly RegisteredExtensionComponent owner;
            private readonly CancellationTokenSource source = new();
            private readonly List<CancellationTokenRegistration> parents = [];
            internal CancellationToken Token => source.Token;
            internal ComponentCancellation(RegisteredExtensionComponent owner, params CancellationToken[] tokens)
            {
                this.owner = owner;
                try
                {
                    foreach (var token in tokens)
                        if (token.CanBeCanceled) parents.Add(token.UnsafeRegister(state => ((ComponentCancellation)state!).Cancel(), this));
                }
                catch (Exception acquisitionError)
                {
                    try { Dispose(); } catch (Exception cleanupError) { throw new AggregateException(acquisitionError, cleanupError); }
                    throw;
                }
            }
            private void Cancel()
            {
                // Real pre-registered callbacks run under a synchronous seed even when Register
                // restores an empty captured ExecutionContext. No detached cancellation callback.
                using var component = new CleanupFrame(owner);
                using var scope = new TerminalInputRegistryCleanupFrame(owner.Owner);
                source.Cancel();
            }
            public void Dispose()
            {
                var faults = new List<Exception>();
                foreach (var parent in parents) try { parent.Dispose(); } catch (Exception error) { faults.Add(error); }
                try { source.Dispose(); } catch (Exception error) { faults.Add(error); }
                if (faults.Count != 0) throw new AggregateException("Actual component parent cancellation registrations failed to detach.", faults);
            }
        }
    }
}
