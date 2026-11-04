using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PiSharp.Chord.Data;
using PiSharp.Chord.Store;

// Authored test support of the store's query and SQL cases (area cv2-store-sql); no upstream file. Upstream's store tests assert
// expect.objectContaining({ code: "invalid", reason: "args" }), compare rows with toEqual and open node:sqlite's
// openNodeSqliteDatabase(":memory:") (packages/chord/src/sqlite/node.ts); test/next/support.ts's store helpers (memoryStore and the rest)
// come with the store engine (area cv2-store-engine). A row here is a map of column to JsonNode (Rows.cs): an absent key is NULL.
internal static partial class Program
{
    /// <summary>expect(run).toThrow(expect.objectContaining({ code: "invalid", reason: "args" })), with a message containing
    /// <paramref name="message"/> when given.</summary>
    internal static ChordError StoreArgs(Action run, string? message = null, string? what = null) => StoreRefused(run, "invalid", "args", message, what);

    /// <summary>expect(run).toThrow(expect.objectContaining({ code, reason })): a <see cref="ChordError"/>.</summary>
    internal static ChordError StoreRefused(Action run, string code, string? reason, string? message = null, string? what = null)
    {
        var label = what ?? message ?? $"{code}/{reason}";
        var error = Throws<ChordError>(run, label);
        Equal(code, error.Code, label + ": the code");
        if (reason is not null) Equal(reason, error.Reason, label + ": the reason");
        if (message is not null)
            Check(error.Message.Contains(message, StringComparison.Ordinal), $"{label}: expected a message containing <{message}>, actual <{error.Message}>.");
        return error;
    }

    /// <summary>expect(run).toThrow(message) where upstream's error may be an Error or a ChordError: any exception whose message contains
    /// <paramref name="message"/>.</summary>
    internal static Exception StoreThrows(Action run, string message, string? what = null)
    {
        try
        {
            run();
        }
        catch (Exception error)
        {
            Check(error.Message.Contains(message, StringComparison.Ordinal), $"{what ?? message}: expected a message containing <{message}>, actual {error.GetType().Name} <{error.Message}>.");
            return error;
        }
        throw new InvalidOperationException($"{what ?? message}: expected an error.");
    }

    /// <summary>A row from JSON text: each field its own node (no parent), in the text's order.</summary>
    internal static OrderedDictionary<string, JsonNode?> StoreRow(string json)
    {
        var row = new OrderedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (column, value) in JsonNode.Parse(json)!.AsObject()) row[column] = value?.DeepClone();
        return row;
    }

    /// <summary>A row as a JSON object (copies of its cells), for <see cref="Json"/>'s toEqual.</summary>
    internal static JsonObject StoreJson(IReadOnlyDictionary<string, JsonNode?> row)
    {
        var json = new JsonObject();
        foreach (var (column, value) in row) json[column] = value?.DeepClone();
        return json;
    }

    /// <summary>Rows as a JSON array, for <see cref="Json"/>'s toEqual.</summary>
    internal static JsonArray StoreJson(IEnumerable<IReadOnlyDictionary<string, JsonNode?>> rows) => new([.. rows.Select(row => (JsonNode?)StoreJson(row))]);

    /// <summary>SQLite values as JSON text, numbers as JavaScript writes them (a double 7 and a long 7 alike): toEqual on parameters.</summary>
    internal static string StoreParams(IEnumerable<object?> values) => StoreInternal.SqliteJson(values);
}

/// <summary>An in-memory SQLite database over Microsoft.Data.Sqlite (the test project's reference, not PiSharp.Chord's), standing in for
/// upstream's openNodeSqliteDatabase(":memory:"): positional <c>?</c> parameters bound in order, rows as maps of column name to SQLite
/// value (null, long, double, string or byte[]).</summary>
internal sealed class StoreSqlite : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    internal StoreSqlite() => connection.Open();

    /// <summary>Runs one statement; the rows it changed.</summary>
    internal int Run(string sql, IEnumerable<object?>? parameters = null)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>The rows of a query.</summary>
    internal List<Dictionary<string, object?>> All(string sql, IEnumerable<object?>? parameters = null)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                row[reader.GetName(index)] = value is DBNull ? null : value;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The first row of a query; null for none.</summary>
    internal Dictionary<string, object?>? Get(string sql, IEnumerable<object?>? parameters = null) => All(sql, parameters).FirstOrDefault();

    private SqliteCommand Command(string sql, IEnumerable<object?>? parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = Numbered(sql);
        var index = 0;
        foreach (var parameter in parameters ?? [])
        {
            index++;
            command.Parameters.Add(new SqliteParameter("?" + index.ToString(CultureInfo.InvariantCulture), parameter switch
            {
                null => DBNull.Value,
                int integer => (long)integer,
                var value => value,
            }));
        }
        return command;
    }

    /// <summary>Numbers each <c>?</c> outside quoted text and identifiers (the store's SQL has no comments).</summary>
    private static string Numbered(string sql)
    {
        var text = new StringBuilder(sql.Length + 8);
        var number = 0;
        char? quote = null;
        foreach (var character in sql)
        {
            if (quote is { } open)
            {
                if (character == open) quote = null;
                text.Append(character);
            }
            else if (character is '\'' or '"')
            {
                quote = character;
                text.Append(character);
            }
            else if (character == '?') text.Append('?').Append((++number).ToString(CultureInfo.InvariantCulture));
            else text.Append(character);
        }
        return text.ToString();
    }

    public void Dispose() => connection.Dispose();
}
