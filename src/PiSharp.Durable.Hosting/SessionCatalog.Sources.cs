// PiSharp-only (decision 0010): catalog version 6, the source monitoring records of M3. Sources and their items are device-wide; a deleted
// source keeps its row as a tombstone and its items stay, so recreating it does not announce old items again.
namespace PiSharp.Durable.Hosting;

internal sealed partial class SessionCatalog
{
    private const string SchemaV6 = """
        CREATE TABLE sources (
          id TEXT PRIMARY KEY, spec TEXT NOT NULL, revision INTEGER NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, deleted_at INTEGER
        );
        CREATE TABLE source_state (
          source_id TEXT PRIMARY KEY, content_hash TEXT, last_checked_at INTEGER, last_changed_at INTEGER, last_success_at INTEGER,
          last_error TEXT, last_error_at INTEGER, failures INTEGER NOT NULL, next_eligible_at INTEGER, last_run_key TEXT
        );
        CREATE TABLE source_items (
          source_id TEXT NOT NULL, item_id TEXT NOT NULL, url TEXT, title TEXT NOT NULL, published_at INTEGER, content_hash TEXT NOT NULL,
          parser TEXT NOT NULL, text_hash TEXT, version INTEGER NOT NULL, first_seen_at INTEGER NOT NULL, last_seen_at INTEGER NOT NULL,
          changed_at INTEGER NOT NULL, absent_at INTEGER, run_key TEXT,
          PRIMARY KEY (source_id, item_id)
        );
        CREATE INDEX source_items_changed ON source_items(source_id, changed_at);
        CREATE TABLE fetch_receipts (
          operation_id TEXT PRIMARY KEY, source_id TEXT NOT NULL, run_key TEXT NOT NULL, provider TEXT NOT NULL, url TEXT NOT NULL, status TEXT NOT NULL,
          http_status INTEGER, blob_hash TEXT, content_type TEXT, etag TEXT, last_modified TEXT, link TEXT, final_url TEXT, error TEXT,
          created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
        );
        CREATE INDEX fetch_receipts_source ON fetch_receipts(source_id, created_at);
        CREATE TABLE retrieval_cache (
          key TEXT PRIMARY KEY, provider TEXT NOT NULL, url TEXT NOT NULL, identity TEXT NOT NULL, etag TEXT, last_modified TEXT, blob_hash TEXT,
          content_type TEXT, fetched_at INTEGER NOT NULL
        );
        CREATE TABLE retrieval_health (
          provider TEXT NOT NULL, origin TEXT NOT NULL, failures INTEGER NOT NULL, cooldown_until INTEGER, retry_after_until INTEGER,
          last_status INTEGER, last_failure TEXT, updated_at INTEGER NOT NULL,
          PRIMARY KEY (provider, origin)
        );
        """;
}
