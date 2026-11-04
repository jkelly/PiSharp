// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/json-column.ts
//
// json(schema, { version, migrate? }) table columns (decision "The `json(...)` column helper", W11): one JSON TEXT cell with its own
// version, typed when read, migrated on access (the result checked once, R8), never walked on write (R9). Tables.Json returns what
// upstream's json() returns, a schema carrying the column: a JsonObject { "json": <a JsonValue holding the JsonColumn> }, so Opt(...) takes
// it and a table reads its "json" field (upstream's JsonColumnSchema). A cell's value is a JsonNode (Rows.cs); migrate maps a parsed older
// cell to this version's value.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>What <see cref="Tables.Json"/> takes besides the schema (upstream <c>JsonColumnOptions</c>).</summary>
public sealed class JsonColumnOptions
{
    /// <summary>The version of the cells' shape; a cell stores the version it was written at.</summary>
    public required long Version { get; init; }

    /// <summary>Maps a cell of an older version (the parsed value and its version) to this version's shape; pure. Without it older cells
    /// are refused.</summary>
    public Func<JsonNode?, long, JsonNode?>? Migrate { get; init; }
}

public static partial class Tables
{
    /// <summary>A column holding one JSON value of <paramref name="schema"/> per row, at <paramref name="options"/>' version; wrap it in
    /// <c>Opt(...)</c> to make it nullable (upstream <c>json</c>). The result is a schema carrying the column: <c>{ "json": ... }</c>, which
    /// only a table's columns read.</summary>
    public static JsonObject Json(JsonNode schema, JsonColumnOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Typed as the cells' schema upstream, so opt(...) takes it; the table reads its json field.
        return new JsonObject { ["json"] = JsonValue.Create(new JsonColumn(schema, options.Version, options.Migrate)) };
    }
}

/// <summary>A <c>json(...)</c> column's definition: its schema snapshot, version and fingerprint, and the read of a stored cell (upstream
/// <c>JsonColumn</c>).</summary>
public sealed class JsonColumn
{
    private readonly Func<JsonNode?, long, JsonNode?>? migrate;

    internal JsonColumn(JsonNode schema, long version, Func<JsonNode?, long, JsonNode?>? migrate)
    {
        // Declaration policy: callers reach this check.
        if (version < 1 || version > StoreInternal.MaxSafeInteger) throw new ArgumentException($"json(...): version {version} is not a positive integer");
        var snapshot = ContractsInternal.SnapshotSchema(schema, "json(...)");
        Schema = snapshot.Schema;
        Version = version;
        Fingerprint = ContractsInternal.FingerprintOf(snapshot.Normal);
        this.migrate = migrate;
    }

    /// <summary>The Chord-owned snapshot of the cells' schema.</summary>
    internal JsonNode Schema { get; }

    /// <summary>The version of the cells' shape.</summary>
    public long Version { get; }

    /// <summary>The fingerprint of the schema's normal form, recorded with the table: the same version with another is <c>broken</c>.</summary>
    public string Fingerprint { get; }

    /// <summary>The value of a stored cell written at <paramref name="version"/>: parsed; an older one migrated and checked once (R8,
    /// counted). A newer version, a missing or throwing migrate and an invalid result throw <c>unavailable</c> (<paramref name="what"/> names
    /// the cell); the caller fails the owner (slice 6a §1.4), the data stays.</summary>
    internal JsonNode? Read(string text, long version, string what, ChordContext context)
    {
        var value = JsonUtf16.MutableNode(text);
        if (version == Version) return value;
        if (version > Version) throw Refused($"{what}: stored at version {version}, newer than {Version}");
        if (migrate is null) throw Refused($"{what}: no migrate from version {version} to {Version}");
        JsonNode? migrated;
        try
        {
            migrated = migrate(value, version);
        }
        catch (Exception error)
        {
            throw Refused($"{what}: migrate from version {version} threw", error);
        }
        context.Count("chord.data.validations");
        if (!DataInternal.ValidatorFor(this, Schema, context).Check(migrated))
            throw Refused($"{what}: migrate from version {version} does not match the schema");
        return migrated;
    }

    /// <summary>The error of a cell the code cannot read: the owner fails <c>broken</c> with it.</summary>
    private static ChordError Refused(string message, Exception? cause = null) => new("unavailable", message, cause: cause);
}
