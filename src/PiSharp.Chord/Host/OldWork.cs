// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/host/old-work.ts.
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

internal interface IOldWorkSource { bool Draining { get; } }
internal sealed class OldWorkState : IOldWorkSource { public bool Draining { get; set; } }
internal sealed record CallNode(IOldWorkSource On, CallNode? Up);

/// <summary>Provenance of work admitted before a reload, retained by every context derivation.</summary>
internal sealed class OldWork
{
    internal static readonly ContextKey<CallNode> Chain = new("chord.callChain");
    internal static readonly ContextKey<ChordObject> Opening = new("chord.opening");
    private sealed class RemoteSource : IOldWorkSource { public bool Draining => true; }
    private static readonly CallNode Remote = new(new RemoteSource(), null);
    internal bool Of(ChordContext context)
    {
        for (var node = context.Value(Chain); node is not null; node = node.Up)
            if (node.On.Draining) return true;
        return false;
    }
    internal static ChordContext AsRemote(ChordContext context) => ChordContext.WithValue(Chain, Remote, context);
    internal static HashSet<IOldWorkSource> Attempts(ChordContext context)
    {
        var found = new HashSet<IOldWorkSource>(ReferenceEqualityComparer.Instance);
        for (var node = context.Value(Chain); node is not null; node = node.Up)
            if (!ReferenceEquals(node, Remote)) found.Add(node.On);
        return found;
    }
}

internal sealed class ReloadCycles
{
    private sealed record Waiting(ChordObject Target, Contract Contract, HashSet<IOldWorkSource> Chain, TaskCompletionSource Cut);
    private readonly HashSet<Waiting> waits = [];
    internal async Task WaitAsync(Task task, ChordObject target, Contract contract, ChordContext context)
    {
        var chain = OldWork.Attempts(context);
        if (chain.Count == 0) { await task; return; }
        var wait = new Waiting(target, contract, chain, new(TaskCreationOptions.RunContinuationsAsynchronously));
        waits.Add(wait);
        try { Check(wait); var settled = await Task.WhenAny(task, wait.Cut.Task); await settled; }
        finally { waits.Remove(wait); }
    }
    internal void Recheck() { foreach (var wait in waits.ToArray()) Check(wait); }
    private void Check(Waiting wait)
    {
        if (wait.Target.OldServes(wait.Contract.Version, wait.Contract.Fingerprint)) return;
        var visited = new HashSet<ChordObject>(ReferenceEqualityComparer.Instance) { wait.Target };
        List<string>? Walk(ChordObject item)
        {
            foreach (var next in item.WaitEdges())
            {
                if (!visited.Add(next)) continue;
                if (next.OpenOldAttempt is { } old && wait.Chain.Contains(old.OldWorkState)) return [next.Label];
                if (Walk(next) is { } rest) { rest.Insert(0, next.Label); return rest; }
            }
            return null;
        }
        if (Walk(wait.Target) is not { } cycle) return;
        cycle.Insert(0, wait.Target.Label);
        wait.Cut.TrySetException(new ChordError("unavailable", $"{wait.Target.Label}: work its reload drains waits for it ({string.Join(" -> ", cycle)})", "reload_cycle"));
    }
}
