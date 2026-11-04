using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/store/table-history.test.ts. Every case is ported; the
// it.each rows of "refusals" are a case each. Upstream's toEqual of a plan compares its kind, versions (the definitions themselves) and
// first; a planning refusal is an InvalidOperationException (an Error upstream).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StoreTableHistoryCases()
    {
        var v1 = Tables.DefineTable("edits", new TableOptions { Version = 1, Columns = StoreColumns(("id", Str()), ("at", Int())), Key = ["id"] });
        var v2 = Tables.EvolveTable(v1, t => t.Add("dir", Str(@default: "")).Rows(_ => StorePatch()));
        var v3 = Tables.EvolveTable(v2, t => t.Index("by_at", ["at"]));
        static TableRecord Recorded(TableDefinition definition, TableState state = TableState.Ready) =>
            new("t", definition.Version, state, definition.Record, definition.Key, definition.Fingerprint, StoreIndexes());
        static TableDefinition Notes(string column, Property type) => Tables.DefineTable("notes", new TableOptions
        {
            Version = 1,
            Columns = StoreColumns(("id", Str()), ("meta", Tables.Json(Obj((column, type)), new JsonColumnOptions { Version = 1 }))),
            Key = ["id"],
        });
        var refusals = new (string Case, TableDefinition Definition, TableRecord Record, string Message)[]
        {
            ("a-newer-version-old-code", v2, Recorded(v3), "version 3, newer than the code's 2"),
            ("a-version-older-than-a-new-base", Tables.DefineTable("edits", new TableOptions { Version = 7, Columns = StoreColumns(("id", Str())), Key = ["id"] }),
                Recorded(v3), "older than the history's base 7"),
            ("a-history-edited-in-place", Tables.EvolveTable(v1, t => t.Add("dir", Str(@default: "x"))), Recorded(v2), "recorded with another definition"),
            ("a-json-column-with-another-schema-at-the-same-version-d9", Notes("b", Int()), Recorded(Notes("a", Int())), "recorded with another definition"),
            ("a-migrating-version-without-a-data-step", v3, Recorded(v3, TableState.Migrating), "has no data step to resume"),
        };
        return
        [
            Case("store.table-history.plans-create-a-table-the-store-does-not-have-use-one-recorded-at-the-newest-version", () =>
            {
                Check(StoreInternal.PlanTable(v3, null, "app.store") is TablePlan.Create, "create");
                Check(StoreInternal.PlanTable(v3, Recorded(v3), "app.store") is TablePlan.Ready, "ready");
            }),
            Case("store.table-history.plans-migrate-through-every-later-version-oldest-first-resume-a-version-recorded-migrating", () =>
            {
                var begin = (TablePlan.Migrate)StoreInternal.PlanTable(v3, Recorded(v1), "app.store");
                Check(begin.Versions.Count == 2 && ReferenceEquals(begin.Versions[0], v2) && ReferenceEquals(begin.Versions[1], v3), "versions 2 and 3");
                Equal(MigrationStart.Begin, begin.First, "from the start");
                var resume = (TablePlan.Migrate)StoreInternal.PlanTable(v3, Recorded(v2, TableState.Migrating), "app.store");
                Check(resume.Versions.Count == 2 && ReferenceEquals(resume.Versions[0], v2) && ReferenceEquals(resume.Versions[1], v3), "versions 2 and 3, resumed");
                Equal(MigrationStart.Resume, resume.First, "resumed");
            }),
            .. refusals.Select(row => Case($"store.table-history.refusals-{row.Case}", () =>
            {
                var error = Throws<InvalidOperationException>(() => StoreInternal.PlanTable(row.Definition, row.Record, "app.store"), row.Case);
                Check(error.Message.Contains(row.Message, StringComparison.Ordinal), $"{row.Case}: expected a message containing <{row.Message}>, actual <{error.Message}>.");
            })),
        ];
    }
}
