// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/allocator.ts
//
// The store's id allocator (slice 6b D3, §1.2): one counter for every id in the store (Durable's mintId, Chord's document ids),
// synchronous, raised by explicit ids, saturating at Number.MAX_SAFE_INTEGER. Upstream's allocator lives on one JavaScript thread; here a
// mint from the app may meet the commit step's on another thread, so the counter takes a short lock.
namespace PiSharp.Chord.Store;

/// <summary>The allocator: <see cref="Value"/> is the next id it gives (upstream <c>IdAllocator</c>, which <c>store.ids</c> exposes to
/// adapters). An id raised to <c>Number.MAX_SAFE_INTEGER</c> is accepted, the value stays there, and every later <see cref="Mint"/> throws:
/// no id past the safe range (it would stop counting exactly and repeat).</summary>
public sealed class IdAllocator
{
    private readonly Lock gate = new();
    private long next;

    internal IdAllocator(long next) => this.next = next;

    /// <summary>The next id <see cref="Mint"/> gives; what each commit persists.</summary>
    public long Value
    {
        get
        {
            lock (gate) return next;
        }
    }

    /// <summary>Takes the next id; throws <c>ID space is exhausted</c> once saturated (Durable's message).</summary>
    public long Mint()
    {
        lock (gate)
        {
            if (next >= StoreInternal.MaxSafeIntegerLong) throw new InvalidOperationException("ID space is exhausted");
            return next++;
        }
    }

    /// <summary>Raises the counter above <paramref name="id"/>, an id written explicitly (Durable mints ahead), saturating.</summary>
    public void Raise(long id)
    {
        lock (gate)
        {
            if (id >= next) next = id >= StoreInternal.MaxSafeIntegerLong ? StoreInternal.MaxSafeIntegerLong : id + 1;
        }
    }
}

internal static partial class StoreInternal
{
    /// <summary>The first id the allocator gives: id 1 is Durable's root conversation (upstream <c>FIRST_ID</c>).</summary>
    internal const long FirstId = 2;

    /// <summary><c>Number.MAX_SAFE_INTEGER</c> as a seq, an id or a revision: 2^53 - 1.</summary>
    internal const long MaxSafeIntegerLong = 9007199254740991;

    /// <summary><c>Number.isSafeInteger</c> of a count, seq or id held as a long.</summary>
    internal static bool IsSafeInteger(long value) => value is >= -MaxSafeIntegerLong and <= MaxSafeIntegerLong;
}
