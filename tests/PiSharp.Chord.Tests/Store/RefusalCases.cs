using Microsoft.Data.Sqlite;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/refusal.test.ts. Ported: "classification". Upstream's
// errors with node:sqlite's errcode are Microsoft.Data.Sqlite's SqliteException with its extended code (the property RefusalOf reads); a
// plain Error is an InvalidOperationException. Not ported: "Chord's owners" ("an int out of range fails only its transaction; the object
// stays live"), which serves an object on a host (loaded, self.table, self.tx) and waits for createHost (phase 6).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StoreRefusalCases() =>
    [
        Case("store.refusal.classification-a-check-failure-by-sqlite-s-extended-code-or-by-its-message-everything-else-broken-the-store-s-own-errors-pass", () =>
        {
            var node = new SqliteException("CHECK constraint failed: n", 19, 275);
            var other = new SqliteException("UNIQUE constraint failed", 19, 2067);
            var check = StoreInternal.RefusalOf(node, "x");
            Check(check is Refusal.Check { Error: { Code: "invalid", Reason: "args" } }, $"by its code: a check, invalid/args, actual {check}");
            Check(StoreInternal.RefusalOf(new InvalidOperationException("CHECK constraint failed: n"), "x") is Refusal.Check, "by its message: a check");
            var broken = StoreInternal.RefusalOf(other, "x");
            Check(broken is Refusal.Broken { Error.Code: "unavailable" }, $"another code: broken, unavailable, actual {broken}");
            var stopped = new ChordError("unavailable", "store: storage failed", "storage_failed");
            Equal(null, StoreInternal.RefusalOf(stopped, "x"), "the store's stop passes");
            Equal(null, StoreInternal.RefusalOf(new ChordError("released", "closed"), "x"), "a release passes");
        }),
    ];
}
