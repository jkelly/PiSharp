// Decision 0011 (run W3, S2): the Spend Guard ledger's tables in webagent.db, in the spend range of migration ids (1000-1999, MigrationArea
// .Spend). The range is split so the Spend units never collide: 1000-1099 the ledger (this file), 1100-1199 the Guard, 1200-1299 the
// Pages, 1300-1399 Estimation. spend_caps holds the caps (one per target: scope, key and window); spend_reservations holds one row per
// provider request, keyed by its op, with what it reserved and how it ended. Amounts are micro-USD integers and times are UTC Unix
// milliseconds. Three columns repeat what Reservation already says, so the caps, totals and meters are plain SQL sums: counts
// (Reservation.CountsTowardCaps), counted_micros (CountedMicros) and list_micros (ListMicros), rewritten from the record on every change;
// cache_saved_micros and cache_missed_micros are the row's share of the prompt cache meter, worked out when it settles.
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

/// <summary>The ledger's webagent.db migrations (ids 1000-1099 of the spend range).</summary>
public static class SpendLedgerMigrations
{
    /// <summary>1000: <c>spend_caps</c>. Wire names for scope, window and action; a null key for <c>everything</c>; one cap per target.</summary>
    public static SqlMigration Caps { get; } = new(1000, "spend: caps", """
        CREATE TABLE IF NOT EXISTS spend_caps (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          scope TEXT NOT NULL CHECK (scope IN ('everything', 'provider', 'account', 'source')),
          key TEXT,
          period TEXT NOT NULL CHECK (period IN ('request', 'run', 'day', 'month')),
          limit_micros INTEGER NOT NULL CHECK (limit_micros > 0),
          action TEXT NOT NULL CHECK (action IN ('block', 'ask')),
          enabled INTEGER NOT NULL DEFAULT 1,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS spend_caps_target ON spend_caps (scope, COALESCE(key, ''), period);
        """);

    /// <summary>1001: <c>spend_reservations</c>, one row per provider request (id = op), with the indexes the caps, the ledger page and
    /// "typical" read.</summary>
    public static SqlMigration Reservations { get; } = new(1001, "spend: reservations", """
        CREATE TABLE IF NOT EXISTS spend_reservations (
          op TEXT PRIMARY KEY,
          provider TEXT NOT NULL,
          model TEXT NOT NULL,
          account TEXT,
          source TEXT NOT NULL,
          run TEXT,
          billing TEXT NOT NULL CHECK (billing IN ('metered', 'subscription', 'free', 'unknown')),
          state TEXT NOT NULL CHECK (state IN ('reserved', 'settled', 'released', 'uncertain')),
          reserved_micros INTEGER,
          actual_micros INTEGER,
          estimate_micros INTEGER,
          settlement TEXT CHECK (settlement IS NULL OR settlement IN ('actual', 'estimate', 'entered')),
          resolved_by_user INTEGER NOT NULL DEFAULT 0,
          counts INTEGER NOT NULL,
          counted_micros INTEGER NOT NULL,
          list_micros INTEGER NOT NULL,
          input_tokens REAL NOT NULL,
          output_tokens INTEGER NOT NULL,
          price TEXT,
          usage_input REAL,
          usage_output REAL,
          usage_cache_read REAL,
          usage_cache_write REAL,
          usage_cost_micros INTEGER,
          cache_saved_micros INTEGER NOT NULL DEFAULT 0,
          cache_missed_micros INTEGER NOT NULL DEFAULT 0,
          note TEXT,
          reason TEXT,
          created_at INTEGER NOT NULL,
          ended_at INTEGER
        );
        CREATE INDEX IF NOT EXISTS spend_reservations_created ON spend_reservations (created_at);
        CREATE INDEX IF NOT EXISTS spend_reservations_run ON spend_reservations (run, created_at);
        CREATE INDEX IF NOT EXISTS spend_reservations_state ON spend_reservations (state);
        CREATE INDEX IF NOT EXISTS spend_reservations_model ON spend_reservations (provider, model, created_at);
        """);

    /// <summary>Every ledger migration, lowest id first.</summary>
    public static IReadOnlyList<IWebAgentMigration> All { get; } = [Caps, Reservations];
}
