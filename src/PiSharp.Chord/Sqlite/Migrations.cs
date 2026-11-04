// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/sqlite/migrations.ts and packages/chord/src/sqlite/schema.ts
// (validateSqliteSchemaPrefix)
//
// chord/sqlite's migration runner over Chord v2's database surface (Database.cs), as the store's schema runs it (applyStoreSchema,
// Store/Schema.cs). PiSharp ports chord/sqlite in PiSharp.Durable.Sqlite (Migrations.cs, Schema.cs), over that project's own
// ISqliteDatabase. PiSharp.Chord references no PiSharp.Durable project and packable PiSharp.Durable.Sqlite may reference no non-packable one
// (decision 0013), so neither copy can run on the other's interfaces: this is a second copy (area cv2-store-engine), with upstream's logic,
// SQL text and messages, and the exception types of Durable.Sqlite's copy (a bad table name ArgumentException, any other failure
// InvalidOperationException). Internal: in Chord v2 the store is its one caller.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Sqlite;

/// <summary>One step of a schema history: the version it reaches and its statements, run in order (upstream <c>SqliteMigration</c>).</summary>
internal sealed record SqliteMigration(long Version, IReadOnlyList<string> Statements);

/// <summary>The migration runner (upstream <c>applySqliteMigrations</c>) and its name check (upstream <c>validateSqliteSchemaPrefix</c>).</summary>
internal static class SqliteMigrations
{
    /// <summary>Applies every pending migration atomically, tracking the version in the metadata table <paramref name="table"/>: the
    /// name checked and the versions contiguous from 1 before any SQL runs; one transaction creates the table when it is missing and runs
    /// the pending migrations, so a failure leaves the database as it was.</summary>
    internal static async Task ApplyAsync(ISqliteDatabase database, IReadOnlyList<SqliteMigration> migrations, string table)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(migrations);
        ValidatePrefix(table);
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index]?.Version != index + 1)
                throw new InvalidOperationException("SQLite migrations must have contiguous versions starting at 1");
        }
        await database.TransactionAsync(async transaction =>
        {
            await transaction.ExecAsync(
                $"CREATE TABLE IF NOT EXISTS {table} (\n\t\t\tsingleton INTEGER PRIMARY KEY CHECK (singleton = 1),\n\t\t\tversion INTEGER NOT NULL CHECK (version >= 0)\n\t\t) STRICT")
                .ConfigureAwait(false);
            await transaction.RunAsync($"INSERT OR IGNORE INTO {table} (singleton, version) VALUES (1, 0)").ConfigureAwait(false);
            var row = await transaction.GetAsync($"SELECT version FROM {table} WHERE singleton = 1").ConfigureAwait(false) ??
                throw new InvalidOperationException("SQLite schema metadata is missing");
            var version = SqliteValues.Integer(row["version"]);
            var currentVersion = migrations.Count == 0 ? 0 : migrations[^1].Version;
            if (version > currentVersion)
                throw new InvalidOperationException($"SQLite schema version {version} is newer than supported version {currentVersion}");
            foreach (var migration in migrations)
            {
                if (migration.Version <= version) continue;
                foreach (var statement in migration.Statements) await transaction.ExecAsync(statement).ConfigureAwait(false);
                await transaction.RunAsync($"UPDATE {table} SET version = ? WHERE singleton = 1", migration.Version).ConfigureAwait(false);
            }
            return true;
        }).ConfigureAwait(false);
    }

    /// <summary>Throws unless <paramref name="prefix"/> is a SQLite identifier (<c>^[A-Za-z_][A-Za-z0-9_]*$</c>) outside SQLite's reserved
    /// <c>sqlite_</c> prefix, in any letter case.</summary>
    internal static void ValidatePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!IsIdentifier(prefix)) throw new ArgumentException($"Invalid SQLite schema prefix {ChordJson.Stringify(JsonValue.Create(prefix))}");
        if (prefix.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"SQLite schema prefix {ChordJson.Stringify(JsonValue.Create(prefix))} is reserved");
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

/// <summary>Reading SQLite result values: a column the facade returns as <see cref="long"/> (or <see cref="int"/> or an integral
/// <see cref="double"/>, as an adapter may) read as a count, a seq or an id.</summary>
internal static class SqliteValues
{
    /// <summary>The integer <paramref name="value"/> holds; anything else is the database's failure (memory and disk disagree).</summary>
    internal static long Integer(object? value) => value switch
    {
        long number => number,
        int number => number,
        double number when double.IsInteger(number) && Math.Abs(number) <= 9007199254740991 => (long)number,
        _ => throw new InvalidOperationException($"SQLite: expected an integer, got {value?.GetType().Name ?? "NULL"}"),
    };

    /// <summary><see cref="Integer"/>, or null for <c>NULL</c>.</summary>
    internal static long? NullableInteger(object? value) => value is null ? null : Integer(value);
}
