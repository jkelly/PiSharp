// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): remaining tables and job from test/next/schema/declarations.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Jobs;
using PiSharp.Chord.Store;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class SchemaDeclarations
{
    internal static readonly TableDefinition Rows = Tables.DefineTable("fixture.switch.rows", new()
    {
        Version = 1,
        Columns = new()
        {
            ["id"] = Str(), ["n"] = Int(@default: 0), ["flag"] = Bool(@default: false), ["label"] = Opt(Str()),
            ["data"] = Tables.Json(Obj(("a", Int()), ("b", Opt(Arr(Str())))), new() { Version = 1 }),
            ["maybe"] = Opt(Tables.Json(Map(Num()), new() { Version = 1 })),
        },
        Key = ["id"], Indexes = new() { ["by_n"] = ["n", "id"] },
    });
    internal static readonly TableDefinition EditsV1 = Tables.DefineTable("fixture.switch.edits", new()
    {
        Version = 1, Columns = new() { ["id"] = Str(), ["file"] = Str(), ["at"] = Int() }, Key = ["id"],
    });
    internal static readonly TableDefinition EditsV2 = Tables.EvolveTable(EditsV1, table => table.Rename("file", "path")
        .Add("dir", Str(@default: "")).Rows(row =>
        {
            var path = row["path"]!.GetValue<string>();
            return new JsonObject { ["dir"] = path[..path.LastIndexOf('/')] };
        }));
    internal static readonly TableDefinition EditsV3 = Tables.EvolveTable(EditsV2, table => table.Drop("dir").Index("by_path", ["path", "at"]));
    private static readonly Lazy<JobDefinition> ImportJobDefinition = new(() => JobDefinition.Define("fixture.switch.import", new()
    {
        Version = 1, Args = Obj(("path", Str())), Result = Obj(("count", Int())), State = ImportState,
        Persist = new(JobResume.Rerun),
    }));
    // Cross-file static initializer order is unspecified; evaluate after the shared declarations initialized.
    internal static JobDefinition ImportJob => ImportJobDefinition.Value;
}
