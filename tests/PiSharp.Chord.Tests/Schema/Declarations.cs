using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using static PiSharp.Chord.Contracts.Schemas;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/test/next/schema/declarations.ts, the declarations of the schema
// switch's proofs (TypeBox to pi-schema): one of every schema helper and option in a contract. Ported here: the `everything` schema and
// the contracts Everything, Account and Owner, with the host kind Account's address names; area cv2-documents-core added the persisted
// documents Profile, Notes and ImportState (StateDefinition.Define). The rest needs layers later areas bring: the tables Rows and EditsV1
// to EditsV3 with their json(...) columns (defineTable, evolveTable, json; the store-tables area) and the persisted job ImportJob
// (defineJob, whose state is ImportState; jobs, phase 6). A schema is a JsonNode with one parent, so `everything` is a function that makes
// it afresh.
internal static class SchemaDeclarations
{
    /// <summary>A schema with every helper and option a declaration can use (upstream <c>everything</c>).</summary>
    internal static JsonObject EverythingSchema() => Obj(
        ("name", Str(min: 1, max: 40, pattern: "^[a-z ]+$")),
        ("nick", Str(@default: "x")),
        ("age", Int(min: 0, max: 150)),
        ("count", Int(@default: 0)),
        ("score", Num(min: 0, max: 100)),
        ("ratio", Num(@default: 0.5)),
        ("active", Bool()),
        ("verified", Bool(@default: false)),
        ("role", OneOf("admin", "user", "guest")),
        ("level", OneOf(1, 2, 3)),
        ("kind", Lit("person")),
        ("one", Lit(1)),
        ("yes", Lit(true)),
        ("tags", Arr(Str(), min: 0, max: 5)),
        ("points", Arr(Int())),
        ("extra", Map(Int())),
        ("note", Opt(Str())),
        ("nothing", Nul()),
        ("choice", Union(Str(), Int(), Obj(("deep", Arr(Obj(("x", Num()))))))),
        ("nested", Obj(("inner", Opt(Obj(("v", Str())))))));

    /// <summary>A local contract with state, keyed state and methods of every argument shape.</summary>
    internal static readonly Contract Everything = Contract.Local("fixture.switch.everything", new()
    {
        Version = 1,
        Params = new() { ["id"] = Str(), ["shard"] = Int() },
        Members = new()
        {
            ["profile"] = EverythingSchema(),
            ["notes"] = Keyed(Obj(("body", Str()))),
            ["plain"] = Method(),
            ["args"] = Method(Obj(("a", Str()), ("b", Opt(Int())), ("any", Unknown()))),
            ["result"] = Method(null, Arr(Str())),
            ["both"] = Method(Obj(("when", Int(min: 0))), Union(Lit("ok"), Obj(("error", Str())))),
            ["blob"] = Method(Obj(("data", Bytes(max: 1024))), Bytes()),
        },
    });

    private static readonly HostKind Server = new("fixture.switch.server", new() { ["region"] = Str() });

    /// <summary>A remote contract with variants and a host address.</summary>
    internal static readonly Contract Account = Contract.Remote("fixture.switch.account", new()
    {
        Version = 1,
        Params = new() { ["user"] = Str(min: 1) },
        HostAddress = _ => Server.Address(("region", "eu")),
        Variants = new()
        {
            ["owner"] = new() { State = { ["balance"] = Num() }, Methods = { ["pay"] = Method(Obj(("amount", Num(min: 0))), Bool()) } },
            ["guest"] = new() { State = { ["balance"] = Num() } },
        },
    });

    /// <summary>The object owning the persisted documents and the tables (a local type with empty params, as tables need).</summary>
    internal static readonly Contract Owner = Contract.Local("fixture.switch.owner", new()
    {
        Version = 1,
        Params = new(),
        Members = new()
        {
            ["profile"] = EverythingSchema(),
            ["write"] = Method(Obj(("id", Str()), ("body", Str()))),
            ["read"] = Method(Str(), Str()),
            ["dump"] = Method(Obj(), Unknown()),
        },
    });

    /// <summary>A persisted singleton document of <c>everything</c>.</summary>
    internal static readonly StateDefinition Profile = StateDefinition.Define("fixture.switch.profile", new()
    {
        Schema = EverythingSchema(),
        Version = 1,
        Initial = () => new()
        {
            ["name"] = "ada",
            ["nick"] = "x",
            ["age"] = 36,
            ["count"] = 0,
            ["score"] = 1,
            ["ratio"] = 0.5,
            ["active"] = true,
            ["verified"] = false,
            ["role"] = "admin",
            ["level"] = 1,
            ["kind"] = "person",
            ["one"] = 1,
            ["yes"] = true,
            ["tags"] = new JsonArray(),
            ["points"] = new JsonArray(),
            ["extra"] = new JsonObject(),
            ["nothing"] = null,
            ["choice"] = "a",
            ["nested"] = new JsonObject(),
        },
        Persist = true,
        CheckpointWhen = (_, _, info) => info.DeltasSinceBase >= 2,
    });

    /// <summary>A persisted keyed document.</summary>
    internal static readonly StateDefinition Notes = StateDefinition.Define("fixture.switch.notes", new()
    {
        Schema = Obj(("body", Str()), ("tags", Opt(Arr(Str())))),
        Version = 1,
        Initial = () => new() { ["body"] = "" },
        Keyed = true,
        Persist = true,
        CheckpointWhen = (_, _, _) => false,
    });

    /// <summary>The state document of the persisted job ImportJob (phase 6).</summary>
    internal static readonly StateDefinition ImportState = StateDefinition.Define("fixture.switch.import.state", new()
    {
        Schema = Obj(("done", Int())),
        Version = 1,
        Initial = () => new() { ["done"] = 0 },
        Persist = true,
        CheckpointWhen = (_, _, _) => false,
    });
}
