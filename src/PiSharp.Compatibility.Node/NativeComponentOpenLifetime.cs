using PiSharp.Extensions.Runtime;

namespace PiSharp.Compatibility.Node;

// Native-only ownership of the exact stop source for one actual custom open. Signal requests
// never join an enclosing callback; retirement directly joins the same Cancel original.
internal sealed class NativeComponentOpenLifetime(ExtensionRegistry.RegisteredExtensionComponent participant)
{
    private readonly object gate = new();
    private static readonly AsyncLocal<CancellationFrame?> current = new();
    [ThreadStatic] private static CancellationFrame? synchronous;
    private readonly CancellationTokenSource stop = new();
    private Task? cancellation, retirement;
    internal CancellationToken Token => stop.Token;
    internal Task? StopOriginal { get { lock (gate) return cancellation; } }

    internal Task RequestStop()
    {
        lock (gate)
        {
            if (retirement is not null) throw new InvalidOperationException("Native open stop admission is retired.");
            var asynchronousParent = current.Value;
            var synchronousParent = synchronous;
            return cancellation ??= Task.Run(() =>
            {
                using var frame = new CancellationFrame(this, asynchronousParent, synchronousParent);
                participant.CancelOwnedCallbackLifetime(stop);
            });
        }
    }
    internal Task RetireAndJoinStopAsync()
    {
        AssertCanRetire();
        lock (gate) return retirement ??= Retire(cancellation);
    }
    private void AssertCanRetire()
    {
        // Normal token.Register can restore a frame-free ExecutionContext during Cancel.
        var pending = new Stack<CancellationFrame>(); var visited = new HashSet<CancellationFrame>(ReferenceEqualityComparer.Instance);
        if (current.Value is { } asynchronous) pending.Push(asynchronous);
        if (synchronous is { } cleanup) pending.Push(cleanup);
        while (pending.TryPop(out var frame))
        {
            if (!visited.Add(frame)) continue;
            if (frame.Active && ReferenceEquals(frame.Owner, this)) throw new InvalidOperationException("A native stop callback cannot join its own or ancestor Cancel original.");
            if (frame.Parent is { } parent) pending.Push(parent);
            if (frame.SynchronousParent is { } synchronousParent) pending.Push(synchronousParent);
        }
    }
    private async Task Retire(Task? original)
    {
        // Start outside the owner's gate even for a synchronously completed Cancel task.
        await Task.Yield();
        var faults = new List<Exception>();
        if (original is not null)
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
        try { stop.Dispose(); } catch (Exception error) { faults.Add(error); }
        if (faults.Count != 0) throw new AggregateException("Native open stop/dispose originals failed.", faults);
    }
    private sealed class CancellationFrame : IDisposable
    {
        internal readonly NativeComponentOpenLifetime Owner;
        internal readonly CancellationFrame? Parent, SynchronousParent;
        private readonly CancellationFrame? restoreCurrent = current.Value, restoreSynchronous = synchronous;
        private int active = 1;
        internal bool Active => Volatile.Read(ref active) != 0;
        internal CancellationFrame(NativeComponentOpenLifetime owner, CancellationFrame? parent, CancellationFrame? synchronousParent)
        { Owner = owner; Parent = parent; SynchronousParent = synchronousParent; current.Value = this; synchronous = this; }
        public void Dispose() { Volatile.Write(ref active, 0); current.Value = restoreCurrent; synchronous = restoreSynchronous; }
    }
}
