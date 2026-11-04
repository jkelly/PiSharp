// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/testing/commit-writes.ts (exported from testing/index.ts).
// One batch through IStorage.CommitAsync, for tests and suites that seed or write a storage directly. Upstream rejects with the refusal,
// whatever value it is; here a refusal is an exception, rethrown with its own stack trace.
using System.Runtime.ExceptionServices;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Testing;

/// <summary>Commits one batch through the batched <see cref="IStorage.CommitAsync"/> (upstream <c>commitWrites</c>).</summary>
public static class CommitWrites
{
    /// <summary>Commits <paramref name="writes"/> as one batch and returns its seq; throws the refusal if the storage refused it.</summary>
    public static async Task<Seq> CommitWritesAsync(this IStorage storage, IReadOnlyList<StorageWrite> writes, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(writes);
        Seq? stored = null;
        var result = await storage.CommitAsync([writes], (_, seq) => stored = seq, context).ConfigureAwait(false);
        if (result.Stored == 0)
        {
            var refused = result.Refused ?? new InvalidOperationException("Storage refused the batch without a reason");
            ExceptionDispatchInfo.Throw(refused);
        }
        return stored ?? throw new InvalidOperationException("Storage stored the batch without a receipt");
    }
}
