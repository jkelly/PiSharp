using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Store;

// Authored, no upstream test file: packages/chord/src/next/store/schema.ts at Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT) has no
// test of its own (upstream's store tests reach it through openStore, which area cv2-store-engine ports with applyStoreSchema). The pins
// below are SHA-256 digests computed by reading schema.ts's text at that commit (the template literals with ${TABLES.x} substituted,
// never run): of JSON.stringify(MIGRATIONS) and of CURRENT_META. The second case runs the history in SQLite with an authored stand-in
// for applySqliteMigrations's version table (its CREATE TABLE as chord/sqlite's migrations.ts writes it) and reads the metadata row.
internal static partial class Program
{
    private static string StoreSha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IEnumerable<(string, Func<Task>)> StoreSchemaCases() =>
    [
        Case("store.schema.the-history-is-upstream-s-text-byte-for-byte", () =>
        {
            var history = new JsonArray([.. StoreInternal.StoreMigrations.Take(2).Select(migration => (JsonNode?)new JsonObject
            {
                ["version"] = migration.Version,
                ["statements"] = StoreInternal.Strings(migration.Statements),
            })]);
            Equal("cb78f873cf73d0685ef8c09a6ff2688bd2b0ed7a9ac3700aae77e47d1a5f4919", StoreSha256(ChordJson.Stringify(history)), "JSON.stringify(MIGRATIONS)");
            Equal("dcc6a14b9c8a3ee2305780942e104786a8fd78b39c26b9f778049fb49249ef3a", StoreSha256(StoreInternal.CurrentMetaSql), "CURRENT_META");
            Equal(3L, StoreInternal.StoreSchema, "STORE_SCHEMA");
            Sequence(["chord_meta", "chord_tables", "chord_documents", "chord_revisions", "chord_outbox"],
                [StoreInternal.StoreTables.Meta, StoreInternal.StoreTables.Tables, StoreInternal.StoreTables.Documents, StoreInternal.StoreTables.Revisions,
                    StoreInternal.StoreTables.Outbox], "TABLES");
        }),
        Case("store.schema.the-history-runs-in-sqlite-and-an-open-reads-the-meta-row-in-one-statement", () =>
        {
            using var db = new StoreSqlite();
            db.Run($"CREATE TABLE IF NOT EXISTS {StoreInternal.StoreSchemaTable} (\n\t\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n" +
                "\t\t\tversion INTEGER NOT NULL CHECK (version >= 0)\n\t\t) STRICT");
            db.Run($"INSERT OR IGNORE INTO {StoreInternal.StoreSchemaTable} (singleton, version) VALUES (1, 0)");
            foreach (var migration in StoreInternal.StoreMigrations)
            {
                foreach (var statement in migration.Statements) db.Run(statement);
                db.Run($"UPDATE {StoreInternal.StoreSchemaTable} SET version = ? WHERE singleton = 1", [migration.Version]);
            }
            var meta = db.Get(StoreInternal.MetaSql)!;
            Equal("""[0,2,0,0]""", StoreParams([meta["last_seq"], meta["next_id"], meta["outbox"], meta["outbox_floor"]]), "the metadata row");
            var current = db.Get(StoreInternal.CurrentMetaSql)!;
            Equal(StoreInternal.StoreSchema, (long)current["version"]!, "the schema version");
            Equal("UTF-8", (string)current["encoding"]!, "the encoding");
            Equal("""[0,2,0,0]""", StoreParams([current["last_seq"], current["next_id"], current["outbox"], current["outbox_floor"]]), "the metadata row in one read");
            // The partial unique index refuses two live records at one address.
            const string Document = "INSERT INTO chord_documents (owner, namespace, kind, key, history, created_at, retired_at, revision, version, fingerprint) " +
                "VALUES ('o', 'n', 'k', 'a', 'latest', ?, ?, 0, 1, 'f')";
            db.Run(Document, [1L, 2L]);
            db.Run(Document, [3L, null]);
            StoreThrows(() => db.Run(Document, [4L, null]), "UNIQUE", "a second live record");
        }),
    ];
}
