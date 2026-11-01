// Decision 0011 (run W3, S3): the Spend Guard's own tables in webagent.db, in the Guard's part of the spend range (1100-1199; the ledger has
// 1000-1099). spend_approvals holds the one-time answers the user gave in the confirm modal (proceed once, or cap the output): each belongs
// to a run (a chat id, a Playground or Compare run id) and optionally one op, lives a few minutes, and is used up by the first call of that
// run the guard stops (used_at and used_by, the first ledger op it let through). spend_approval_uses records every reservation an approval
// let through (one row per ledger op: one for a single call, one per column of a Compare group), so each recovered request finds the output
// cap it was sent with. spend_stops keeps what the guard decided about each call it stopped (keyed by the call's op), so the confirm modal
// can open the cause's pane for a call stopped inside a run. Times are UTC Unix milliseconds. Nothing here is a secret. 1103 (W3 fix, chat
// spend) adds spend_approvals.submission: an approval of a chat send answers only the call that sends that message (a hash of its text,
// never the text), so a running turn's next call, a compaction or another send can't use it.
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Spend;

/// <summary>The Guard's webagent.db migrations (ids 1100-1199 of the spend range).</summary>
public static class SpendGuardMigrations
{
    /// <summary>1100: <c>spend_approvals</c>, the user's one-time answers to an Ask (wire names for the choice).</summary>
    public static SqlMigration Approvals { get; } = new(1100, "spend: approvals", """
        CREATE TABLE IF NOT EXISTS spend_approvals (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          run TEXT NOT NULL,
          op TEXT,
          choice TEXT NOT NULL CHECK (choice IN ('proceed-once', 'cap-output')),
          max_output_tokens INTEGER CHECK (max_output_tokens IS NULL OR max_output_tokens >= 1),
          created_at INTEGER NOT NULL,
          expires_at INTEGER NOT NULL,
          used_at INTEGER,
          used_by TEXT
        );
        CREATE INDEX IF NOT EXISTS spend_approvals_run ON spend_approvals (run, used_at, expires_at);
        CREATE INDEX IF NOT EXISTS spend_approvals_used_by ON spend_approvals (used_by);
        """);

    /// <summary>1101: <c>spend_stops</c>, the decision on each call the guard stopped, keyed by the call's op.</summary>
    public static SqlMigration Stops { get; } = new(1101, "spend: stops", """
        CREATE TABLE IF NOT EXISTS spend_stops (
          op TEXT PRIMARY KEY,
          run TEXT,
          source TEXT NOT NULL,
          provider TEXT NOT NULL,
          model TEXT NOT NULL,
          account TEXT,
          code TEXT NOT NULL CHECK (code IN ('spend_blocked', 'spend_ask', 'spend_unknown_price')),
          decision TEXT NOT NULL,
          input_bytes INTEGER NOT NULL,
          max_tokens INTEGER,
          created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS spend_stops_run ON spend_stops (run, created_at);
        """);

    /// <summary>1102: <c>spend_approval_uses</c>, every reservation (ledger op) an approval let through, so every column of an approved
    /// Compare group, not only the first, is resent with the chosen output cap after a restart. Approvals used before it are carried over
    /// from <c>used_by</c>.</summary>
    public static SqlMigration ApprovalUses { get; } = new(1102, "spend: approval uses", $$"""
        CREATE TABLE IF NOT EXISTS spend_approval_uses (
          ledger_op TEXT PRIMARY KEY,
          approval_id INTEGER NOT NULL,
          recorded_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS spend_approval_uses_approval ON spend_approval_uses (approval_id);
        INSERT OR IGNORE INTO spend_approval_uses (ledger_op, approval_id, recorded_at)
          SELECT used_by, id, used_at FROM spend_approvals
          WHERE used_at IS NOT NULL AND used_by IS NOT NULL AND used_by NOT IN ('{{SpendApprovalStore.Replaced}}', '{{SpendApprovalStore.Refused}}')
          ORDER BY id DESC;
        """);

    /// <summary>1103: <c>spend_approvals.submission</c>, the chat message (its key, never its text) a one-time approval of a send answers.</summary>
    public static SqlMigration ApprovalSubmissions { get; } = new(1103, "spend: approval submissions", """
        ALTER TABLE spend_approvals ADD COLUMN submission TEXT;
        """);

    /// <summary>Every Guard migration, lowest id first.</summary>
    public static IReadOnlyList<IWebAgentMigration> All { get; } = [Approvals, Stops, ApprovalUses, ApprovalSubmissions];
}
