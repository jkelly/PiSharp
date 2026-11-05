// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/sqlite/schema.ts and packages/chord/src/sqlite/schema.ts
//
// Upstream keeps Chord's prefix check in chord/sqlite and Durable's identifier map in its storage; both live here, since PiSharp's
// Chord SQLite layer stays in PiSharp.Durable.Sqlite. Upstream checks the prefix with /^[A-Za-z_][A-Za-z0-9_]*$/, whose $ matches only at
// the end of the text in JavaScript; the check here compares characters, so a trailing newline is refused as upstream refuses it.
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Sqlite;

/// <summary>Checks a prefix that becomes part of a SQLite identifier (upstream <c>validateSqliteSchemaPrefix</c>).</summary>
public static class SqliteSchemaPrefix
{
    /// <summary>Throws <see cref="ArgumentException"/> unless <paramref name="prefix"/> is a SQLite identifier (ASCII letters, digits and
    /// underscores, not starting with a digit) that does not use SQLite's reserved <c>sqlite_</c> prefix, in any letter case.</summary>
    public static void Validate(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!IsIdentifier(prefix)) throw new ArgumentException($"Invalid SQLite schema prefix {JsonValue.String(prefix).ToJson()}");
        if (prefix.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"SQLite schema prefix {JsonValue.String(prefix).ToJson()} is reserved");
    }

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !(char.IsAsciiLetter(text[0]) || text[0] == '_')) return false;
        foreach (var character in text)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_')) return false;
        }
        return true;
    }
}

/// <summary>
/// The identifiers every migration and storage query uses (upstream <c>SqliteSchema</c>), rendered with an optional prefix.
/// <para>The names are immutable history: never change or remove one. A rename adds a new name, so that historical migrations keep
/// rendering their original object names.</para>
/// </summary>
internal sealed class SqliteSchema
{
    private SqliteSchema(string prefix)
    {
        Tables = new(prefix);
        Indexes = new(prefix);
    }

    /// <summary>The tables (upstream <c>TABLE_NAMES</c>).</summary>
    public SqliteTableNames Tables { get; }

    /// <summary>The indexes (upstream <c>INDEX_NAMES</c>).</summary>
    public SqliteIndexNames Indexes { get; }

    /// <summary>The unprefixed schema (upstream <c>DEFAULT_SQLITE_SCHEMA</c>).</summary>
    public static SqliteSchema Default { get; } = Create();

    /// <summary>The identifiers with <paramref name="prefix"/> in front of each (upstream <c>createSqliteSchema</c>); null means none. A
    /// prefix is checked by <see cref="SqliteSchemaPrefix.Validate"/>.</summary>
    public static SqliteSchema Create(string? prefix = null)
    {
        if (prefix is not null) SqliteSchemaPrefix.Validate(prefix);
        return new(prefix ?? "");
    }
}

/// <summary>Durable's table names under one prefix.</summary>
internal sealed class SqliteTableNames(string prefix)
{
    public string DurableSchema { get; } = prefix + "durable_schema";
    public string DurableMetadata { get; } = prefix + "durable_metadata";
    public string RecordIds { get; } = prefix + "record_ids";
    public string Conversations { get; } = prefix + "conversations";
    public string Entries { get; } = prefix + "entries";
    public string Tasks { get; } = prefix + "tasks";
    public string Submissions { get; } = prefix + "submissions";
    public string Documents { get; } = prefix + "documents";
    public string DocumentRevisions { get; } = prefix + "document_revisions";
}

/// <summary>Durable's index names under one prefix.</summary>
internal sealed class SqliteIndexNames(string prefix)
{
    public string ConversationsByOwnerConversation { get; } = prefix + "conversations_by_owner_conversation";
    public string ConversationsByOwnerTask { get; } = prefix + "conversations_by_owner_task";
    public string EntriesByConversation { get; } = prefix + "entries_by_conversation";
    public string EntryHeadsByConversation { get; } = prefix + "entry_heads_by_conversation";
    public string TasksByStatus { get; } = prefix + "tasks_by_status";
    public string TasksByConversation { get; } = prefix + "tasks_by_conversation";
    public string TasksByKind { get; } = prefix + "tasks_by_kind";
    public string TasksByAbortRequested { get; } = prefix + "tasks_by_abort_requested";
    public string TasksByBackground { get; } = prefix + "tasks_by_background";
    public string SubmissionsByRequest { get; } = prefix + "submissions_by_request";
    public string SubmissionsByConversation { get; } = prefix + "submissions_by_conversation";
    public string SubmissionsByStatus { get; } = prefix + "submissions_by_status";
    public string DocumentsByAddress { get; } = prefix + "documents_by_address";
    public string DocumentsByScope { get; } = prefix + "documents_by_scope";
    public string DocumentsByScopeKind { get; } = prefix + "documents_by_scope_kind";
    public string DocumentRevisionsByKind { get; } = prefix + "document_revisions_by_kind";
}
