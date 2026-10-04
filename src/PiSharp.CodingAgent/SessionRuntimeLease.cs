namespace PiSharp.CodingAgent;

/// <summary>One host-created set of cwd-dependent runtime bindings. The coordinator joins release
/// after its agent and writer close, including rejected staging. Factories must return a fresh lease
/// on every call and clean up any resources allocated before a factory exception is returned.</summary>
public sealed class SessionRuntimeLease : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IAsyncDisposable? resources;
    private Task? release;
    private bool claimed;
    public SessionRuntimeRegistry Registry { get; }
    public SessionRuntimeLease(SessionRuntimeRegistry registry, IAsyncDisposable? resources = null)
    { ArgumentNullException.ThrowIfNull(registry); Registry = registry; this.resources = resources; }
    internal void Claim()
    {
        lock (gate)
        {
            if (claimed || release is not null) throw new InvalidOperationException("Runtime lease has already been used or released.");
            claimed = true;
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) { release ??= ReleaseAsync(); return new(release); }
    }
    private async Task ReleaseAsync()
    {
        await Task.Yield();
        if (resources is not null) await resources.DisposeAsync().ConfigureAwait(false);
    }
}
