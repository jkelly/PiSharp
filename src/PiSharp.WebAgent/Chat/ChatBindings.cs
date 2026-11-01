// Decision 0011 (run W2, WU7): the chat binding, one row per chat in webagent.db (table chats, migration 200 of the chats range): the durable
// session id, the title, the provider, model, account and thinking level the chat runs with, the service tier (the composer's Speed), the
// branch shown (an agent of the session), and created, updated and archived times. The account is bound at creation and changes only by
// an explicit model or account change, which ChatService refuses while the chat runs; the device host's SessionStream hook reads
// {account, tier} from here for every request a session prepares, so the store keeps every row in memory and answers without blocking.
// request_id makes creation idempotent: a retried "new chat" POST finds the chat its first attempt made.
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Chat;

/// <summary>One chat's binding.</summary>
/// <param name="SessionId">The durable session's UUID; also the chat id (route <c>/chat/{SessionId}</c>).</param>
/// <param name="Title">The title shown in the sidebar (the session's name).</param>
/// <param name="Provider">The provider of future requests.</param>
/// <param name="Model">The model of future requests.</param>
/// <param name="AccountId">The account bound at creation (or by an explicit change), or null when the provider needs none.</param>
/// <param name="Thinking">The thinking level of future requests.</param>
/// <param name="Tier">The service tier (Speed) of future requests, or null for the provider's default.</param>
/// <param name="AgentId">The branch shown and sent to by default: Main (1), or a fork made by fork or edit-and-resend.</param>
/// <param name="Created">When the chat was created.</param>
/// <param name="Updated">When it last changed (a message, a rename, a branch switch).</param>
/// <param name="Archived">When it was deleted (archived), or null.</param>
/// <param name="RequestId">The browser request id that created it.</param>
public sealed record ChatBinding(string SessionId, string Title, string Provider, string Model, string? AccountId, string Thinking, string? Tier,
    long AgentId, DateTimeOffset Created, DateTimeOffset Updated, DateTimeOffset? Archived, string? RequestId)
{
    /// <summary>Whether the chat was deleted.</summary>
    public bool IsArchived => Archived is not null;
}

/// <summary>The chat bindings in webagent.db, cached in memory. A singleton; safe to call from many threads.</summary>
public sealed class ChatBindings(WebAgentDb db, TimeProvider? time = null)
{
    /// <summary>200: <c>chats</c>, the first migration of the chats range.</summary>
    public static SqlMigration Migration { get; } = new(200, "chats: bindings", """
        CREATE TABLE IF NOT EXISTS chats (
          session_id TEXT PRIMARY KEY,
          title TEXT NOT NULL,
          provider TEXT NOT NULL,
          model TEXT NOT NULL,
          account_id TEXT,
          thinking TEXT NOT NULL,
          tier TEXT,
          agent_id INTEGER NOT NULL DEFAULT 1,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          archived_at INTEGER,
          request_id TEXT UNIQUE
        );
        CREATE INDEX IF NOT EXISTS chats_recent ON chats (archived_at, updated_at);
        """);

    private const string Columns = "session_id, title, provider, model, account_id, thinking, tier, agent_id, created_at, updated_at, archived_at, request_id";

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Lock gate = new();
    private ConcurrentDictionary<string, ChatBinding>? cache;

    /// <summary>Raised after a binding is added or changed, outside any lock, with its session id.</summary>
    public event Action<string>? Changed;

    /// <summary>The binding of chat <paramref name="sessionId"/> (archived ones included), or null.</summary>
    public ChatBinding? Get(string sessionId) => sessionId is not null && Rows().TryGetValue(sessionId, out var binding) ? binding : null;

    /// <summary>The binding the browser request <paramref name="requestId"/> created, or null.</summary>
    public ChatBinding? FindByRequest(string requestId) => Rows().Values.FirstOrDefault(binding => binding.RequestId == requestId);

    /// <summary>Every binding, most recently updated first; archived ones only when asked.</summary>
    public IReadOnlyList<ChatBinding> List(bool includeArchived = false) =>
        [.. Rows().Values.Where(binding => includeArchived || !binding.IsArchived).OrderByDescending(binding => binding.Updated).ThenBy(binding => binding.SessionId, StringComparer.Ordinal)];

    /// <summary>Adds <paramref name="binding"/>. When its session or request id already has one, nothing changes and that binding is
    /// returned, so a retried creation keeps the first attempt's binding.</summary>
    public ChatBinding Add(ChatBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ChatBinding stored;
        var rows = Rows();
        lock (gate)
        {
            if (rows.TryGetValue(binding.SessionId, out var existing)) return existing;
            if (binding.RequestId is { } request && rows.Values.FirstOrDefault(row => row.RequestId == request) is { } earlier) return earlier;
            db.Execute($"INSERT INTO chats ({Columns}) VALUES ($s, $title, $provider, $model, $account, $thinking, $tier, $agent, $created, $updated, $archived, $request);",
                Parameters(binding));
            rows[binding.SessionId] = binding;
            stored = binding;
        }
        Changed?.Invoke(stored.SessionId);
        return stored;
    }

    /// <summary>Changes the binding of <paramref name="sessionId"/> with <paramref name="change"/> and stores it; returns the new binding.</summary>
    /// <exception cref="ChatException">No such chat (404).</exception>
    public ChatBinding Update(string sessionId, Func<ChatBinding, ChatBinding> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ChatBinding next;
        var rows = Rows();
        lock (gate)
        {
            if (!rows.TryGetValue(sessionId, out var current)) throw ChatException.NotFound($"No chat {sessionId}.");
            next = change(current) with { SessionId = current.SessionId, Created = current.Created, RequestId = current.RequestId };
            if (next == current) return current;
            db.Execute("""
                UPDATE chats SET title = $title, provider = $provider, model = $model, account_id = $account, thinking = $thinking, tier = $tier,
                  agent_id = $agent, updated_at = $updated, archived_at = $archived WHERE session_id = $s;
                """, Parameters(next));
            rows[sessionId] = next;
        }
        Changed?.Invoke(sessionId);
        return next;
    }

    /// <summary>Marks the chat as changed now (it moves to the top of the sidebar).</summary>
    public ChatBinding Touch(string sessionId) => Update(sessionId, binding => binding with { Updated = Now() });

    /// <summary>The current time, at millisecond precision (as stored).</summary>
    public DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

    /// <summary>The <c>metadata.pisharp</c> fields of the session's next request: <c>account</c> and <c>tier</c> from its binding; null
    /// for a session without a binding. The device host's <see cref="DurableDeviceOptions.SessionStream"/>; cheap and non-blocking.</summary>
    public JsonObjectValue? StreamFields(string sessionId)
    {
        var binding = Get(sessionId);
        return binding is null ? null : SessionStreamMetadata.Fields(("account", binding.AccountId), ("tier", binding.Tier));
    }

    private ConcurrentDictionary<string, ChatBinding> Rows()
    {
        if (Volatile.Read(ref cache) is { } loaded) return loaded;
        lock (gate)
        {
            if (cache is not null) return cache;
            var rows = db.Query($"SELECT {Columns} FROM chats;", Read);
            cache = new ConcurrentDictionary<string, ChatBinding>(rows.ToDictionary(row => row.SessionId, StringComparer.Ordinal), StringComparer.Ordinal);
            return cache;
        }
    }

    private static ChatBinding Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
        reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)), reader.IsDBNull(11) ? null : reader.GetString(11));

    private static (string, object?)[] Parameters(ChatBinding binding) =>
    [
        ("$s", binding.SessionId), ("$title", binding.Title), ("$provider", binding.Provider), ("$model", binding.Model),
        ("$account", binding.AccountId), ("$thinking", binding.Thinking), ("$tier", binding.Tier), ("$agent", binding.AgentId),
        ("$created", binding.Created.ToUnixTimeMilliseconds()), ("$updated", binding.Updated.ToUnixTimeMilliseconds()),
        ("$archived", binding.Archived?.ToUnixTimeMilliseconds()), ("$request", binding.RequestId),
    ];
}
