namespace PiSharp.CodingAgent;

/// <summary>One host-created set of cwd-dependent runtime bindings. The coordinator joins release
/// after its agent and writer close, including rejected staging. Factories must return a fresh lease
/// on every call and clean up any resources allocated before a factory exception is returned.</summary>
public sealed class SessionRuntimeLease : IAsyncDisposable
{
    private readonly object gate = new();
    private IAsyncDisposable? resources;
    private readonly Action<ReplaceableAgentSession, AgentSessionAttachment>? bindOwner;
    private Task? release;
    private bool claimed;
    private bool bindingAttempted;
    private bool wrappingResources;
    public SessionRuntimeRegistry Registry { get; }
    public SessionRuntimeLease(SessionRuntimeRegistry registry, IAsyncDisposable? resources = null,
        Action<ReplaceableAgentSession, AgentSessionAttachment>? bindOwner = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (bindOwner?.GetInvocationList().Length > 1) throw new ArgumentException("One owner binding callback is required.", nameof(bindOwner));
        Registry = registry; this.resources = resources; this.bindOwner = bindOwner;
    }
    internal bool RequiresOwnerBinding => bindOwner is not null;
    /// <summary>Transfer the unclaimed lease's resource ownership into one host wrapper,
    /// preserving this lease and its binder. The callback runs outside the state gate.
    /// Rejected construction leaves the original resources owned by this lease; the
    /// callback must clean any separate allocations it does not return.</summary>
    public void WrapResourcesBeforeClaim(Func<IAsyncDisposable?, IAsyncDisposable> wrap)
    {
        ArgumentNullException.ThrowIfNull(wrap);
        if (wrap.GetInvocationList().Length != 1) throw new ArgumentException("One resource wrapper is required.", nameof(wrap));
        lock (gate)
        {
            if (claimed || release is not null || wrappingResources)
                throw new InvalidOperationException("Resource wrapping requires an unclaimed unreleased runtime lease.");
            wrappingResources = true;
        }
        try
        {
            var wrapped = wrap(resources) ?? throw new InvalidOperationException("Resource wrapper returned no owner.");
            if (ReferenceEquals(wrapped, this) || ReferenceEquals(wrapped, resources))
                throw new InvalidOperationException("Resource wrapper must return a distinct resource owner, not this lease.");
            lock (gate) resources = wrapped;
        }
        finally { lock (gate) wrappingResources = false; }
    }
    internal void BindOwner(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        lock (gate)
        {
            if (!claimed || release is not null || bindingAttempted || wrappingResources)
                throw new InvalidOperationException("Runtime owner binding requires one claimed unreleased lease.");
            bindingAttempted = true;
        }
        bindOwner?.Invoke(owner, attachment);
    }
    internal void Claim()
    {
        lock (gate)
        {
            if (claimed || release is not null || wrappingResources) throw new InvalidOperationException("Runtime lease has already been used or released, or its resources are being wrapped.");
            claimed = true;
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (wrappingResources) throw new InvalidOperationException("Runtime resource wrapping cannot overlap disposal.");
            release ??= ReleaseAsync(); return new(release);
        }
    }
    private async Task ReleaseAsync()
    {
        await Task.Yield();
        if (resources is not null) await AgentSessionReloadTask.JoinOwned(resources.DisposeAsync().AsTask()).ConfigureAwait(false);
    }
}
