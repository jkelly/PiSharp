using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/allocator.test.ts. Every case is ported.
// Number.MAX_SAFE_INTEGER is StoreInternal.MaxSafeIntegerLong (2^53 - 1).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StoreAllocatorCases() =>
    [
        Case("store.allocator.the-allocator-mints-from-its-value-and-an-explicit-id-raises-it-above-itself-never-lowering-it", () =>
        {
            var ids = new IdAllocator(StoreInternal.FirstId);
            Sequence([2L, 3L], [ids.Mint(), ids.Mint()], "the first mints");
            ids.Raise(100);
            Equal(101L, ids.Mint(), "after a raise");
            ids.Raise(50);
            Equal(102L, ids.Value, "a lower raise keeps the value");
        }),
        Case("store.allocator.the-allocator-saturates-at-number-max-safe-integer-that-id-may-be-written-every-later-mint-is-refused", () =>
        {
            const long max = StoreInternal.MaxSafeIntegerLong;
            var ids = new IdAllocator(max - 1);
            Equal(max - 1, ids.Mint(), "the last mint");
            StoreThrows(() => ids.Mint(), "ID space is exhausted", "a mint past it");
            ids.Raise(max);
            Equal(max, ids.Value, "raised to the bound");
            StoreThrows(() => ids.Mint(), "ID space is exhausted", "a mint at the bound");
        }),
    ];
}
