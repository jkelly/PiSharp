namespace PiSharp.Extensions.Runtime;

/// <summary>Explicitly owned connection/session generation. The host owns dispatch and disposal.</summary>
public sealed class RegisteredTerminalInputSession : IAsyncDisposable
{
    private readonly object gate = new();
    private ExtensionRegistry? registry;
    private readonly CancellationToken session;
    private readonly ExtensionTerminalInputHub hub;
    private readonly Dictionary<RegistrationScope, OwnerBinding> owners = new(ReferenceEqualityComparer.Instance);
    private bool closed;
    private Task? close;
    private readonly List<Exception> retirementFaults = [];
    public RegisteredTerminalInputSession(ExtensionRegistry registry, long connectionGeneration,
        long sessionGeneration, CancellationToken sessionCancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry; session = sessionCancellationToken;
        hub = new(connectionGeneration, sessionGeneration);
    }
    public long ConnectionGeneration => hub.ConnectionGeneration;
    public long SessionGeneration => hub.SessionGeneration;
    /// <summary>For host construction before profile activation. The first actual native callback fixes the registry.</summary>
    public RegisteredTerminalInputSession(long connectionGeneration, long sessionGeneration,
        CancellationToken sessionCancellationToken = default)
    {
        session = sessionCancellationToken;
        hub = new(connectionGeneration, sessionGeneration);
    }
    public Task<ExtensionTerminalInputOutcome> DispatchAsync(string data, long connectionGeneration,
        long sessionGeneration, CancellationToken token = default) =>
        Volatile.Read(ref closed) ? Task.FromResult(ExtensionTerminalInputOutcome.Unavailable(ExtensionUiUnavailableReason.StaleContext)) :
        hub.DispatchAsync(data, connectionGeneration, sessionGeneration, token);

    /// <summary>For the UI provider selector; requires an actual current native callback context.</summary>
    public ExtensionTerminalInputHub.Scope SelectOwnerScope(IExtensionContext context) => Select(context).Input;
    public IExtensionTerminalInput SelectRegistrationSink(IExtensionContext context) => Select(context).Sink;

    /// <summary>Only fences raw membership; actual callback and cancellation originals are joined by DisposeAsync.</summary>
    public void RetireAdmission()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            foreach (var binding in owners.Values)
                try { binding.Retire(); } catch (Exception error) { retirementFaults.Add(error); }
        }
    }

    private OwnerBinding Select(IExtensionContext context)
    {
        if (context is not ExtensionContext native || !CallbackFrame.IsExecuting(native.Scope))
            throw new InvalidOperationException("Terminal input binding requires the current native callback owner.");
        context.OperationCancellationToken.ThrowIfCancellationRequested();
        context.ExtensionLifetimeCancellationToken.ThrowIfCancellationRequested();
        session.ThrowIfCancellationRequested();
        OwnerBinding binding;
        bool attach = false;
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(RegisteredTerminalInputSession));
            registry ??= native.Scope.Registry;
            if (!ReferenceEquals(registry, native.Scope.Registry))
                throw new InvalidOperationException("Terminal session is already bound to another native registry.");
            if (!owners.TryGetValue(native.Scope, out binding!))
            {
                // Operation cancellation belongs to the registration callback, not the persistent listener.
                var persistent = new ExtensionContext(native.Scope, CancellationToken.None, session);
                var input = hub.OpenScope(persistent, () => new TerminalInputRegistryCleanupFrame(native.Scope));
                binding = new(native.Scope, input, registry.BindTerminalInput(native.Scope, input, session));
                owners.Add(native.Scope, binding); attach = true;
            }
        }
        // Register may synchronously invoke for an already-cancelled owner. Never join under the map gate.
        if (attach) binding.Attach();
        return binding;
    }

    public void AssertCanClose()
    {
        // Refuse before publishing close: native callbacks and real cancellation handlers cannot join themselves.
        lock (gate)
            if (owners.Keys.Any(owner => CallbackFrame.IsExecuting(owner) || TerminalInputRegistryCleanupFrame.IsExecuting(owner)))
                throw new InvalidOperationException("Terminal session close cannot join its own native callback or cleanup.");
        hub.AssertCanClose();
    }
    public ValueTask DisposeAsync()
    {
        AssertCanClose();
        lock (gate)
        {
            if (close is not null) return new(close);
            RetireAdmission();
            var bindings = owners.Values.ToArray();
            // The stable returned task is the actual worker original; no cancellation task is detached.
            close = Task.Run(() => CloseAsync(bindings, new List<Exception>(retirementFaults)));
            return new(close);
        }
    }
    private async Task CloseAsync(OwnerBinding[] bindings, List<Exception> faults)
    {
        var originals = new List<Task>();
        foreach (var binding in bindings)
            try { originals.Add(binding.CloseAsync()); }
            catch (Exception error) { faults.Add(error); }
        foreach (var originalClose in originals)
            try { await originalClose.ConfigureAwait(false); }
            catch (Exception error) { faults.Add(originalClose.Exception ?? error); }
        Task? original = null;
        try { original = hub.DisposeAsync().AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { faults.Add(original?.Exception ?? error); }
        if (faults.Count != 0) throw new AggregateException("Terminal session originals failed.", faults);
    }

    private sealed class OwnerBinding(RegistrationScope owner, ExtensionTerminalInputHub.Scope input,
        IExtensionTerminalInput sink)
    {
        private readonly object lifetimeGate = new();
        private CancellationTokenRegistration lifetime;
        private bool detached;
        internal ExtensionTerminalInputHub.Scope Input => input;
        internal IExtensionTerminalInput Sink => sink;
        internal void Retire()
        {
            input.RetireAdmission();
            owner.Registry.RetireTerminalInput(owner, input);
        }
        internal void Attach()
        {
            var original = owner.ExtensionLifetimeCancellationToken.UnsafeRegister(static state =>
            {
                var binding = (OwnerBinding)state!;
                // This is the actual lifetime callback; no detached cancellation task escapes owner cleanup.
                var close = binding.Input.DisposeAsync();
                close.GetAwaiter().GetResult();
            }, this);
            bool dispose;
            lock (lifetimeGate) { dispose = detached; if (!dispose) lifetime = original; }
            if (dispose) original.Dispose();
        }
        internal async Task CloseAsync()
        {
            var faults = new List<Exception>();
            try { owner.Registry.RetireTerminalInput(owner, input); }
            catch (Exception error) { faults.Add(error); }
            Task? original = null;
            try { original = input.DisposeAsync().AsTask(); await original.ConfigureAwait(false); }
            catch (Exception error) { faults.Add(original?.Exception ?? error); }
            CancellationTokenRegistration registration;
            lock (lifetimeGate) { detached = true; registration = lifetime; lifetime = default; }
            try { registration.Dispose(); } catch (Exception error) { faults.Add(error); }
            if (faults.Count != 0) throw new AggregateException("Terminal owner originals failed.", faults);
        }
    }
}
