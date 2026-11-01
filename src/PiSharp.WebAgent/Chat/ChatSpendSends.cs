// Decision 0011 (run W3, S5; W3 fix): the chat sends the Spend Guard let through, kept in webagent.db next to spend_approvals so a restart
// does not forget them. A send's key is its chat and request id ("new/<request id>" for a new chat). Once a key was admitted, a retried POST
// of it is never checked again (it reaches the durable engine's de-duplication and posts once), and the one-time approval it stored is
// stored once (approval_id, the spend_approvals row). Before this table ChatSpend kept both in memory (cleared above 2,000 keys), so after a
// restart a retried send of an admitted id was checked again and could be refused after it had been posted. The migration takes the first
// id of S5's part of the spend range (1400-1499: chat; the ledger has 1000-1099, the guard 1100-1199, the pages 1200-1299 and estimation
// 1300-1399). Rows older than Retention (the guard's 30 days) are pruned as new ones are written. Nothing here is a secret: keys are ids.
using System.Collections.Concurrent;
using System.Globalization;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Chat;

/// <summary>The chat sends the Spend Guard admitted, and the one-time approval each stored (webagent.db, migration 1400). A singleton; safe to
/// call from many threads. Without a database (a test that builds the chat services alone) it keeps them in memory.</summary>
public sealed class ChatSpendSends
{
    /// <summary>The longest key kept: a chat id or <c>new</c>, a slash and a request id of at most 128 characters.</summary>
    public const int MaxKeyLength = 300;

    /// <summary>How long a send's key is kept (the guard keeps its approvals and stops as long).</summary>
    public static readonly TimeSpan Retention = SpendApprovalStore.Retention;

    /// <summary>1400: <c>spend_chat_sends</c>, one row per admitted chat send (key = chat and request id).</summary>
    public static SqlMigration Migration { get; } = new(1400, "spend: chat sends", """
        CREATE TABLE IF NOT EXISTS spend_chat_sends (
          key TEXT PRIMARY KEY,
          approval_id INTEGER,
          admitted_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS spend_chat_sends_admitted ON spend_chat_sends (admitted_at);
        """);

    private readonly WebAgentDb? db;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, long?> memory = new(StringComparer.Ordinal);

    /// <summary>The sends in <paramref name="db"/> (which must hold <see cref="Migration"/>), or in memory when it is null.</summary>
    public ChatSpendSends(WebAgentDb? db, TimeProvider? time = null)
    {
        this.db = db;
        clock = time ?? TimeProvider.System;
    }

    /// <summary>Whether <paramref name="key"/> was admitted.</summary>
    public bool IsAdmitted(string key)
    {
        var name = Key(key);
        if (db is null) return memory.ContainsKey(name);
        return db.Query("SELECT 1 FROM spend_chat_sends WHERE key = $key;", reader => reader.GetInt64(0), ("$key", name)).Count > 0;
    }

    /// <summary>Records <paramref name="key"/> as admitted (again: nothing changes).</summary>
    public void Admit(string key)
    {
        var name = Key(key);
        if (db is null)
        {
            memory.TryAdd(name, null);
            return;
        }
        var now = clock.GetUtcNow();
        db.Transaction((connection, transaction) =>
        {
            WebAgentDb.Execute(connection, transaction, "DELETE FROM spend_chat_sends WHERE admitted_at < $old;", ("$old", (now - Retention).ToUnixTimeMilliseconds()));
            return WebAgentDb.Execute(connection, transaction, "INSERT INTO spend_chat_sends (key, admitted_at) VALUES ($key, $at) ON CONFLICT (key) DO NOTHING;",
                ("$key", name), ("$at", now.ToUnixTimeMilliseconds()));
        });
    }

    /// <summary>The id of the one-time approval <paramref name="key"/> stored, or null when it stored none.</summary>
    public long? ApprovalOf(string key)
    {
        var name = Key(key);
        if (db is null) return memory.TryGetValue(name, out var id) ? id : null;
        return db.Query("SELECT approval_id FROM spend_chat_sends WHERE key = $key AND approval_id IS NOT NULL;", reader => reader.GetInt64(0), ("$key", name))
            .Cast<long?>().FirstOrDefault();
    }

    /// <summary>Records that <paramref name="key"/> stored approval <paramref name="approvalId"/> (admitting it if it was not yet).</summary>
    public void Approved(string key, long approvalId)
    {
        var name = Key(key);
        if (db is null)
        {
            memory[name] = approvalId;
            return;
        }
        var at = clock.GetUtcNow().ToUnixTimeMilliseconds();
        db.Execute("""
            INSERT INTO spend_chat_sends (key, approval_id, admitted_at) VALUES ($key, $id, $at)
            ON CONFLICT (key) DO UPDATE SET approval_id = excluded.approval_id;
            """, ("$key", name), ("$id", approvalId), ("$at", at));
    }

    /// <summary>Clears the approval <paramref name="key"/> stored (the key stays admitted) and returns its id, or null when it stored none
    /// (W3 merge: ChatSpend retires it when the send failed or its queued message was withdrawn).</summary>
    public long? TakeApproval(string key)
    {
        var name = Key(key);
        if (db is null) return memory.TryGetValue(name, out var id) && id is not null && memory.TryUpdate(name, null, id) ? id : null;
        return db.Transaction((connection, transaction) =>
        {
            var found = WebAgentDb.Scalar(connection, transaction, "SELECT approval_id FROM spend_chat_sends WHERE key = $key AND approval_id IS NOT NULL;",
                ("$key", name));
            long? id = found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
            if (id is not null) WebAgentDb.Execute(connection, transaction, "UPDATE spend_chat_sends SET approval_id = NULL WHERE key = $key;", ("$key", name));
            return id;
        });
    }

    /// <summary>Forgets <paramref name="key"/> (a send that failed after it was admitted: its retry is checked again). Returns whether it was
    /// admitted.</summary>
    public bool Remove(string key)
    {
        var name = Key(key);
        if (db is null) return memory.TryRemove(name, out _);
        return db.Execute("DELETE FROM spend_chat_sends WHERE key = $key;", ("$key", name)) > 0;
    }

    /// <summary>How many sends are kept (diagnostics and tests).</summary>
    public long Count() => db is null ? memory.Count : Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM spend_chat_sends;") ?? 0L, CultureInfo.InvariantCulture);

    private static string Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var name = key.Trim();
        if (name.Length > MaxKeyLength) throw new ArgumentException($"A send key is at most {MaxKeyLength} characters.", nameof(key));
        return name;
    }
}
