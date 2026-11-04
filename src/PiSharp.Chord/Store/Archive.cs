// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/archive.ts (the write schema and checkWrite)
//
// Archives and recorded commits as they cross a trust boundary (slice 6a §13, slice 6b §1.6; decisions "Export archives are operations",
// "Writes carry their versions; archives are lossless"). Area cv2-store-engine added this file with the part batch apply needs: the
// compiled schema every applied write is checked against first (upstream WRITE) and checkWrite. The archive's format, its parts and their
// schema (ARCHIVE_FORMAT, ArchiveManifest, ArchivePart, PART, checkPart) come with export and import (area cv2-store-archive, which owns
// archive.ts and brings this file to the whole of it). The schemas are built with Chord's schema helpers (Contracts/Schema.cs), as
// upstream builds them with its own; a write is checked as JSON, the shape the outbox records.
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using static PiSharp.Chord.Contracts.Schemas;

namespace PiSharp.Chord.Store;

internal static partial class StoreInternal
{
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

    /// <summary>Checks one write of a batch's commit against the write schema (a trust boundary); <paramref name="what"/> names its place
    /// (upstream <c>checkWrite</c>). The check establishes the shape, the nodes aside (the applier checks them).</summary>
    internal static JsonObject CheckWrite(JsonNode? write, string what, ChordContext context)
    {
        context.Count("chord.data.validations");
        if (!DataInternal.ValidatorFor(WriteSchemaOwner, WriteSchema, context).Check(write)) throw DataInternal.Invalid("args", $"{what}: not a store write");
        return (JsonObject)write!;
    }
}
