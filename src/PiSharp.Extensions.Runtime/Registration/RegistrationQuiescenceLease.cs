namespace PiSharp.Extensions.Runtime;

/// <summary>A host-owned pause of one live extension generation after its registry callbacks drain.
/// Disposal resumes admission exactly once unless scope or registry shutdown already won. This lease
/// neither cancels the extension lifetime nor proves that unregistered plugin work or its ALC unloaded.
/// Release the lease before expecting the old generation's host references to collect.</summary>
public sealed class RegistrationQuiescenceLease : IDisposable, IAsyncDisposable
{
    private RegistrationScope? scope;
    private readonly object pause;
    public string OwnerId { get; }
    public long OwnerGeneration { get; }

    internal RegistrationQuiescenceLease(RegistrationScope scope, object pause)
    {
        this.scope = scope;
        this.pause = pause;
        OwnerId = scope.OwnerId;
        OwnerGeneration = scope.OwnerGeneration;
    }

    public void Dispose()
    {
        var previous = Interlocked.Exchange(ref scope, null);
        previous?.Registry.ResumeScope(previous, pause);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
