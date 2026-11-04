// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/archive.ts
//
// Archives and recorded commits as they cross a trust boundary (slice 6a §13, slice 6b §1.6; decisions "Export archives are operations",
// "Writes carry their versions; archives are lossless"): the parts an export yields and an import takes, their order, and the compiled
// schemas every imported part and every applied write is checked against first. Area cv2-store-engine added this file with the write
// schema batch apply needs (WRITE, checkWrite); area cv2-store-archive, which owns archive.ts, adds the archive's format, its part schema
// and checkPart. An archive part is its JSON here (a JsonObject in upstream's data form), as the outbox's recorded writes are: upstream's
// ArchivePart and ArchiveManifest types describe that JSON, which export.ts builds and import.ts reads after checkPart (Export.cs,
// Import.cs; Index.cs says so). The schemas are built with Chord's schema helpers (Contracts/Schema.cs), as upstream builds them with its
// own.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.Store;

/// <summary>An archive's first part, as import reads it: the format, the store's schema version, its <c>lastSeq</c> and the allocator's
/// next id (rule 24, D3; upstream <c>ArchiveManifest</c>, whose JSON is <c>{ kind: "manifest", format, schema, lastSeq, nextId }</c>).</summary>
internal sealed record ArchiveManifest(long Format, long Schema, long LastSeq, long NextId)
{
    /// <summary><c>{ kind: "manifest", format, schema, lastSeq, nextId }</c>.</summary>
    internal JsonObject ToJson() => new() { ["kind"] = "manifest", ["format"] = Format, ["schema"] = Schema, ["lastSeq"] = LastSeq, ["nextId"] = NextId };
}

internal static partial class StoreInternal
{
    /// <summary>The archive format export writes; import takes this one only (upstream <c>ARCHIVE_FORMAT</c>).</summary>
    internal const long ArchiveFormat = 1;

    /// <summary>An integer from <paramref name="min"/> up to 2^53 - 1: seqs, ids, versions and counts stay exact (a seq after 2^53 would
    /// repeat; upstream <c>safe</c>).</summary>
    private static JsonObject Safe(double min) => Int(min: min, max: MaxSafeIntegerLong);

    /// <summary>A recorded column (upstream <c>column</c>).</summary>
    private static JsonObject ArchiveColumn() => Obj(
        ("type", Union(Lit("str"), Lit("int"), Lit("num"), Lit("bool"), Lit("json"))),
        ("optional", Opt(Lit(true))),
        ("default", Opt(Union(Str(), Num(), Bool()))),
        ("version", Opt(Safe(1))),
        ("fingerprint", Opt(Str())));

    /// <summary>A <c>table</c> write (upstream <c>TABLE</c>).</summary>
    private static JsonObject ArchiveTable() => Obj(
        ("kind", Lit("table")),
        ("table", Str()),
        ("version", Safe(1)),
        ("state", Union(Lit("ready"), Lit("migrating"))),
        ("columns", Map(ArchiveColumn())),
        ("key", Arr(Str())),
        ("fingerprint", Str()));

    /// <summary>A <c>put</c> write (upstream <c>PUT</c>).</summary>
    private static JsonObject ArchivePut() => Obj(
        ("kind", Lit("put")),
        ("table", Str()),
        ("row", Map(Unknown())),
        ("version", Safe(1)),
        ("columns", Map(Safe(1))));

    /// <summary>An <c>index</c> write (upstream <c>INDEX</c>).</summary>
    private static JsonObject ArchiveIndex() => Obj(("kind", Lit("index")), ("table", Str()), ("name", Str()), ("columns", Arr(Str())));

    /// <summary>A document record (upstream <c>RECORD</c>).</summary>
    private static JsonObject ArchiveRecord() => Obj(
        ("id", Safe(1)),
        ("owner", Str()),
        ("namespace", Str()),
        ("kind", Str()),
        // Absent for a singleton.
        ("key", Opt(Str())),
        ("history", Union(Lit("none"), Lit("latest"), Lit("rewindable"))),
        ("createdAt", Safe(1)),
        ("retiredAt", Opt(Safe(1))),
        ("meta", Opt(Str())),
        ("revision", Safe(1)),
        ("version", Safe(1)),
        ("fingerprint", Str()));

    /// <summary>One part of an archive, in this order: the manifest; per table (by SQL name) its <c>table</c> write, its <c>put</c>s and
    /// its <c>index</c> writes; per document record (by id, live and retired) its <c>document.create</c> without a base, then its kept
    /// revisions in seq order; <c>end</c> with the count of the parts between (upstream <c>PART</c>, the schema of <c>ArchivePart</c>).</summary>
    private static readonly JsonObject PartSchema = BuildPartSchema();

    /// <summary>The owner of the part schema's compiled validator (upstream's <c>PART_SCHEMA</c> object).</summary>
    private static readonly object PartSchemaOwner = new();

    private static JsonObject BuildPartSchema()
    {
        var version = Safe(1);
        // A delta's ops; their shapes are checked where they are applied.
        var ops = Arr(Unknown());
        return Union(
            Obj(("kind", Lit("manifest")), ("format", Safe(1)), ("schema", Safe(1)), ("lastSeq", Safe(0)), ("nextId", Safe(1))),
            ArchiveTable(),
            ArchivePut(),
            ArchiveIndex(),
            Obj(("kind", Lit("document.create")), ("record", ArchiveRecord())),
            Obj(("kind", Lit("document.revision")), ("id", Safe(1)), ("seq", Safe(1)), ("content", Union(
                Obj(("kind", Lit("base")), ("version", version), ("value", Unknown())),
                Obj(("kind", Lit("delta")), ("version", version), ("ops", ops))))),
            Obj(("kind", Lit("end")), ("writes", Safe(0))));
    }

    /// <summary>One recorded write of a commit (a batch's); <c>where</c> and <c>set</c> nodes are checked by the applier (upstream
    /// <c>WRITE</c>).</summary>
    private static readonly JsonObject WriteSchema = BuildWriteSchema();

    /// <summary>The owner of the write schema's compiled validator (upstream's <c>WRITE_SCHEMA</c> object).</summary>
    private static readonly object WriteSchemaOwner = new();

    private static JsonObject BuildWriteSchema()
    {
        var version = Safe(1);
        var count = Safe(0);
        var edit = Union(
            Obj(("kind", Lit("rename")), ("from", Str()), ("to", Str())),
            Obj(("kind", Lit("add")), ("name", Str()), ("column", ArchiveColumn())),
            Obj(("kind", Lit("drop")), ("name", Str())),
            Obj(("kind", Lit("json")), ("name", Str()), ("column", ArchiveColumn())));
        var key = Map(Union(Str(), Num(), Bool()));
        // A delta's ops; their shapes are checked where they are applied.
        var ops = Arr(Unknown());
        return Union(
            ArchiveTable(),
            ArchivePut(),
            ArchiveIndex(),
            Obj(("kind", Lit("dropIndex")), ("table", Str()), ("name", Str())),
            Obj(("kind", Lit("alter")), ("table", Str()), ("version", version), ("edit", edit)),
            Obj(("kind", Lit("update")), ("table", Str()), ("key", key), ("set", Unknown()), ("columns", Map(version)), ("count", count),
                ("version", Opt(version))),
            Obj(("kind", Lit("delete")), ("table", Str()), ("key", key), ("count", count)),
            Obj(("kind", Lit("updateWhere")), ("table", Str()), ("where", Unknown()), ("set", Unknown()), ("columns", Map(version)),
                ("count", count), ("version", Opt(version))),
            Obj(("kind", Lit("deleteWhere")), ("table", Str()), ("where", Unknown()), ("count", count)),
            Obj(("kind", Lit("document.create")), ("record", ArchiveRecord()), ("base", Obj(("version", version), ("fingerprint", Str()), ("value", Unknown())))),
            Obj(("kind", Lit("document.change")), ("id", Safe(1)), ("revision", Safe(1)), ("content", Union(
                Obj(("kind", Lit("base")), ("version", version), ("fingerprint", Str()), ("value", Unknown())),
                Obj(("kind", Lit("delta")), ("version", version), ("ops", ops))))),
            Obj(("kind", Lit("document.retire")), ("id", Safe(1)), ("revision", Safe(1))));
    }

    /// <summary>Checks one imported part against the archive schema (a trust boundary); <c>invalid/args</c> naming its
    /// <paramref name="position"/> (upstream <c>checkPart</c>). The check establishes the shape; the part itself is returned.</summary>
    internal static JsonObject CheckPart(JsonNode? part, long position, ChordContext context)
    {
        context.Count("chord.data.validations");
        if (!DataInternal.ValidatorFor(PartSchemaOwner, PartSchema, context).Check(part))
            throw DataInternal.Invalid("args", $"import: part {position}: not an archive part");
        return (JsonObject)part!;
    }

    /// <summary>Checks one write of a batch's commit against the write schema (a trust boundary); <paramref name="what"/> names its place
    /// (upstream <c>checkWrite</c>). The check establishes the shape, the nodes aside (the applier checks them).</summary>
    internal static JsonObject CheckWrite(JsonNode? write, string what, ChordContext context)
    {
        context.Count("chord.data.validations");
        if (!DataInternal.ValidatorFor(WriteSchemaOwner, WriteSchema, context).Check(write)) throw DataInternal.Invalid("args", $"{what}: not a store write");
        return (JsonObject)write!;
    }
}
