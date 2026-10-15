// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/heap-retained.ts. For benchmarks: the bytes live objects hold.
// Upstream sums the self sizes of a V8 heap snapshot, which collects garbage first, because heapUsed after gc() also counts what the
// collector has not compacted yet. The .NET counterpart is the managed heap after a blocking, compacting full collection (the large object
// heap included), which counts only reachable objects.
using System.Runtime;

namespace PiSharp.Durable.Benchmarks;

/// <summary>Upstream <c>heap-retained.ts</c>.</summary>
public static class HeapRetained
{
    /// <summary>Bytes reachable objects occupy on the managed heap, after a compacting full collection (upstream
    /// <c>retainedBytes</c>).</summary>
    public static Task<long> RetainedBytesAsync()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return Task.FromResult(GC.GetTotalMemory(forceFullCollection: false));
    }
}
