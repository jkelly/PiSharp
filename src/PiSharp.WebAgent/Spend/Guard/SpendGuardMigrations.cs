// Decision 0011 (run W3, S3): the Spend Guard's own tables in webagent.db, in the Guard's part of the spend range (1100-1199; the ledger has
// 1000-1099). spend_approvals holds the one-time answers the user gave in the confirm modal (proceed once, or cap the output): each belongs
// to a run (a chat id, a Playground or Compare run id) and optionally one op, lives a few minutes, and is used up by the first call of that
// run the guard stops (used_at and used_by, the ledger op of the reservation it let through, so a recovered request finds the output cap it
// was sent with). spend_stops keeps what the guard decided about each call it stopped (keyed by the call's op), so the confirm modal can
// open the cause's pane for a call stopped inside a run. Times are UTC Unix milliseconds. Nothing here is a secret. 1102 (W3 fix, chat
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

    /// <summary>1102: <c>spend_approvals.submission</c>, the chat message (its key, never its text) a one-time approval of a send answers.</summary>
    public static SqlMigration ApprovalSubmissions { get; } = new(1102, "spend: approval submissions", """
        ALTER TABLE spend_approvals ADD COLUMN submission TEXT;
        """);

    /// <summary>Every Guard migration, lowest id first.</summary>
    public static IReadOnlyList<IWebAgentMigration> All { get; } = [Approvals, Stops, ApprovalSubmissions];
}
