using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/claims.test.ts. Ported: the first two cases. The third,
// "an object claims its tables and documents when it declares them, and releases them at its end", serves an object on a host (loaded,
// defineState, self.state and self.table) and waits for createHost (phase 6) and the documents layer. A claimant is any IClaimant; upstream's
// object literals { label } are compared by identity, as these instances are.
internal static partial class Program
{
    /// <summary>A claimant of the claims cases (upstream's <c>{ label }</c>).</summary>
    private sealed class StoreClaimant(string label) : IClaimant
    {
        public string Label { get; } = label;
    }

    private static IEnumerable<(string, Func<Task>)> StoreClaimsCases() =>
    [
        Case("store.claims.claims-refuse-a-second-claimant-of-a-table-or-of-an-owner-s-namespace-one-claimant-may-claim-again", () =>
        {
            var claims = new Claims();
            var a = new StoreClaimant("A");
            var b = new StoreClaimant("B");
            claims.Table("x:edits", a);
            claims.Table("x:edits", a);
            claims.Documents("x", "chord", a);
            StoreThrows(() => claims.Table("x:edits", b), "claimed by A", "a second claimant of the table");
            StoreThrows(() => claims.Documents("x", "chord", b), "claimed by A", "a second claimant of the namespace");
            // Another namespace of the same owner, another table: no collision.
            claims.Documents("x", "session", b);
            claims.Table("x:entries", b);
            Equal(true, claims.Held, "held");
            claims.Release(a);
            claims.Table("x:edits", b);
            claims.Release(b);
            Equal(false, claims.Held, "released");
        }),
        Case("store.claims.claims-are-refused-while-locked-a-batch-applies-taken-again-once-unlocked", () =>
        {
            var claims = new Claims();
            var owner = new StoreClaimant("A");
            var unlock = claims.Lock();
            StoreThrows(() => claims.Table("x:edits", owner), "while a batch applies", "a table while locked");
            StoreThrows(() => claims.Documents("x", "chord", owner), "while a batch applies", "documents while locked");
            Equal(false, claims.Held, "nothing held while locked");
            unlock();
            claims.Table("x:edits", owner);
            Equal(true, claims.Held, "held once unlocked");
        }),
    ];
}
