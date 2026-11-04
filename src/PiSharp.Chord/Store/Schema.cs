// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/schema.ts
//
// The store's own SQL schema (slice 6a §2): its metadata, the recorded table definitions, the documents engine's records and revisions,
// and the outbox; versioned by applySqliteMigrations. Declared tables are named "<owner>:<table>", so no name here can meet one of them.
// This file ports the schema's data: the tables, the migration history (the statements byte for byte as upstream writes them, tabs and
// newlines included, since the DDL is what a store database records), the schema version, the metadata row and the one-read query of
// an open. Upstream's applyStoreSchema(db) and currentMeta(db) run them over a SqliteDatabase and applySqliteMigrations from
// chord/sqlite, which PiSharp ports in PiSharp.Durable.Sqlite (area sqlite); PiSharp.Chord references no PiSharp.Durable project, so the
// two functions come with the store's backend (Backend.cs, area cv2-store-engine) and its SQLite seam.
namespace PiSharp.Chord.Store;

/// <summary>One entry of a schema history: its version and the statements that reach it (upstream <c>SqliteMigration</c>).</summary>
internal sealed record StoreMigration(long Version, IReadOnlyList<string> Statements);

/// <summary>The store's metadata row, as <c>openStore</c> reads it once (upstream <c>MetaRow</c>).</summary>
/// <param name="LastSeq"><c>last_seq</c>: the last committed seq.</param>
/// <param name="NextId"><c>next_id</c>: the allocator's next id (D3; from 2, id 1 is Durable's root conversation).</param>
/// <param name="Outbox"><c>outbox</c>: the outbox setting, 0 or 1.</param>
/// <param name="OutboxFloor"><c>outbox_floor</c>: the seq the outbox starts after, advanced by <c>trim</c>.</param>
internal sealed record MetaRow(long LastSeq, long NextId, long Outbox, long OutboxFloor);

internal static partial class StoreInternal
{
    /// <summary>The store's tables (upstream <c>TABLES</c>).</summary>
    internal static class StoreTables
    {
        internal const string Meta = "chord_meta";
        internal const string Tables = "chord_tables";
        internal const string Documents = "chord_documents";
        internal const string Revisions = "chord_revisions";
        internal const string Outbox = "chord_outbox";
    }

    /// <summary>The table <c>applySqliteMigrations</c> records the store's schema version in.</summary>
    internal const string StoreSchemaTable = "chord_store_schema";

    /// <summary>The schema history; never change an entry, append one (nothing is deployed yet: slice 6b changed entry 1 in place).
    /// <c>chord_meta</c> is one row: <c>last_seq</c>, the allocator's next id (D3; from 2, id 1 is Durable's root conversation), and the
    /// outbox setting with its floor (the seq it starts after, advanced by <c>trim</c>). A document record keeps its history rule (R1), its
    /// current base's version and fingerprint and a <c>revision</c> counter for the conditional <c>UPDATE</c>; the partial unique index
    /// refuses two live records at one address. Entry 2 adds the indexes of reads at a point (slice 6b §1.5): an address's incarnations by
    /// creation, an owner's namespace by id, a document's bases (a partial index: deltas write no entry) (upstream <c>MIGRATIONS</c>).</summary>
    internal static readonly IReadOnlyList<StoreMigration> StoreMigrations =
    [
        new(1,
        [
            "CREATE TABLE chord_meta (\n" +
            "\t\t\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n" +
            "\t\t\t\tlast_seq INTEGER NOT NULL,\n" +
            "\t\t\t\tnext_id INTEGER NOT NULL,\n" +
            "\t\t\t\toutbox INTEGER NOT NULL CHECK (outbox IN (0, 1)),\n" +
            "\t\t\t\toutbox_floor INTEGER NOT NULL\n" +
            "\t\t\t) STRICT",
            "INSERT INTO chord_meta (singleton, last_seq, next_id, outbox, outbox_floor) VALUES (1, 0, 2, 0, 0)",
            "CREATE TABLE chord_tables (name TEXT PRIMARY KEY, definition TEXT NOT NULL) STRICT, WITHOUT ROWID",
            "CREATE TABLE chord_documents (\n" +
            "\t\t\t\tid INTEGER PRIMARY KEY,\n" +
            "\t\t\t\towner TEXT NOT NULL,\n" +
            "\t\t\t\tnamespace TEXT NOT NULL,\n" +
            "\t\t\t\tkind TEXT NOT NULL,\n" +
            "\t\t\t\tkey TEXT NOT NULL,\n" +
            "\t\t\t\thistory TEXT NOT NULL CHECK (history IN ('none', 'latest', 'rewindable')),\n" +
            "\t\t\t\tcreated_at INTEGER NOT NULL,\n" +
            "\t\t\t\tretired_at INTEGER,\n" +
            "\t\t\t\tmeta TEXT,\n" +
            "\t\t\t\trevision INTEGER NOT NULL,\n" +
            "\t\t\t\tversion INTEGER NOT NULL,\n" +
            "\t\t\t\tfingerprint TEXT NOT NULL\n" +
            "\t\t\t) STRICT",
            "CREATE UNIQUE INDEX chord_documents_live ON chord_documents (owner, namespace, kind, key)\n" +
            "\t\t\t\tWHERE retired_at IS NULL",
            "CREATE TABLE chord_revisions (\n" +
            "\t\t\t\tdocument_id INTEGER NOT NULL,\n" +
            "\t\t\t\tseq INTEGER NOT NULL,\n" +
            "\t\t\t\tkind TEXT NOT NULL CHECK (kind IN ('base', 'delta')),\n" +
            "\t\t\t\tversion INTEGER NOT NULL,\n" +
            "\t\t\t\tcontent TEXT NOT NULL,\n" +
            "\t\t\t\tPRIMARY KEY (document_id, seq)\n" +
            "\t\t\t) STRICT, WITHOUT ROWID",
            "CREATE TABLE chord_outbox (\n" +
            "\t\t\t\tseq INTEGER PRIMARY KEY,\n" +
            "\t\t\t\tformat INTEGER NOT NULL,\n" +
            "\t\t\t\tnext_id INTEGER NOT NULL,\n" +
            "\t\t\t\twrites TEXT NOT NULL\n" +
            "\t\t\t) STRICT",
        ]),
        new(2,
        [
            "CREATE INDEX chord_documents_address ON chord_documents (owner, namespace, kind, key, created_at)",
            "CREATE INDEX chord_documents_scope ON chord_documents (owner, namespace, id)",
            // Only bases are looked up by it (a load's newest base at or below a point): deltas write no entry.
            "CREATE INDEX chord_revisions_bases ON chord_revisions (document_id, seq) WHERE kind = 'base'",
        ]),
    ];

    /// <summary>The store's schema version: the newest entry of its history (archives carry it; upstream <c>STORE_SCHEMA</c>).</summary>
    internal static long StoreSchema => StoreMigrations[^1].Version;

    /// <summary>The metadata row's read after the migrations (upstream <c>applyStoreSchema</c>'s query).</summary>
    internal const string MetaSql = "SELECT last_seq, next_id, outbox, outbox_floor FROM chord_meta WHERE singleton = 1";

    /// <summary>The schema version, the encoding and the metadata row of a store, in one read (upstream <c>CURRENT_META</c>).</summary>
    internal const string CurrentMetaSql =
        "SELECT s.version, e.encoding, m.last_seq, m.next_id, m.outbox, m.outbox_floor\n" +
        "\tFROM chord_store_schema s, pragma_encoding e, chord_meta m WHERE s.singleton = 1 AND m.singleton = 1";
}
