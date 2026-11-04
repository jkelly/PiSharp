// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/store/tables.ts
//
// Durable's records as declared tables of the store (slice 6b D2, §2.2): every row carries its record's indexed columns and the whole
// record as one json(...) cell. Indexed strings that must stay lossless (kind, request_id) hold Durable's JSON encoding; index columns are
// never Opt, so absent values take sentinels no encoded value can be (0 for an absent owner, which is never an id; "" for an absent
// request id: an encoded string starts with "). A row is a map of column to JsonNode, as Chord v2's row codec takes it; the record cell is
// the record as a node whose strings are .NET strings (Node), so the store serializes it as JSON.stringify does, a lone surrogate escaped
// (a node parsed from JSON text that escapes one is write-only, and Chord's columns refuse it).
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Store;
using PiSharp.Contracts;
using PiSharp.Durable.Internal;
using static PiSharp.Chord.Contracts.Schemas;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

namespace PiSharp.Durable.Store;

/// <summary>StoreStorage's table definitions and rows (upstream <c>tables.ts</c>).</summary>
internal static class StoreTables
{
    /// <summary>Which record type owns an id: Durable's one namespace across its five record types.</summary>
    internal static readonly TableDefinition RecordIds = Define("record_ids", [("id", Int()), ("record_type", Str())]);

    internal static readonly TableDefinition Conversations = Define("conversations",
        [("id", Int()), ("owner_conversation_id", Int()), ("owner_task_id", Int()), ("record", Record())],
        ("by_owner_conversation", ["owner_conversation_id"]), ("by_owner_task", ["owner_task_id"]));

    internal static readonly TableDefinition Entries = Define("entries",
        [("id", Int()), ("conversation_id", Int()), ("commit_seq", Int()), ("record", Record())], ("by_conversation", ["conversation_id"]));

    /// <summary>The entries with a head marker, by conversation: what SqliteStorage's partial index on <c>head IS NOT NULL</c> is, as rows
    /// written only with such an entry, so a commit of an entry without one updates no second index.</summary>
    internal static readonly TableDefinition EntryHeads = Define("entry_heads", [("id", Int()), ("conversation_id", Int())],
        ("by_conversation", ["conversation_id"]));

    internal static readonly TableDefinition Tasks = Define("tasks",
        [("id", Int()), ("conversation_id", Int()), ("kind", Str()), ("status", Str()), ("abort_requested", Int()), ("background", Int()),
            ("record", Record())],
        ("by_status", ["status"]), ("by_conversation", ["conversation_id"]), ("by_kind", ["kind"]), ("by_abort_requested", ["abort_requested"]),
        ("by_background", ["background"]));

    internal static readonly TableDefinition Submissions = Define("submissions",
        [("id", Int()), ("conversation_id", Int()), ("request_id", Str()), ("status", Str()), ("record", Record())],
        ("by_request", ["conversation_id", "request_id"]), ("by_conversation", ["conversation_id"]), ("by_status", ["status"]));

    /// <summary>Durable's JSON encoding of an indexed string: lossless whatever the string (lone surrogates, NUL).</summary>
    internal static string EncodeIndexed(string value) => DurableJson.String(value).ToJson();

    /// <summary>The row of a conversation record.</summary>
    internal static Dictionary<string, JsonNode?> ConversationRow(ConversationRecord value) => new(StringComparer.Ordinal)
    {
        ["id"] = value.Id.Value,
        ["owner_conversation_id"] = value.Owner?.ConversationId.Value ?? 0,
        ["owner_task_id"] = value.Owner?.TaskId.Value ?? 0,
        ["record"] = Cell(value.ToJson()),
    };

    /// <summary>The row of an entry record; <c>commit_seq</c> is stamped by the step.</summary>
    internal static Dictionary<string, JsonNode?> EntryRow(EntryRecord value) => new(StringComparer.Ordinal)
    {
        ["id"] = value.Id.Value,
        ["conversation_id"] = value.ConversationId.Value,
        ["commit_seq"] = 0,
        ["record"] = Cell(value.ToJson()),
    };

    /// <summary>The row of a task record.</summary>
    internal static Dictionary<string, JsonNode?> TaskRow(TaskRecord value) => new(StringComparer.Ordinal)
    {
        ["id"] = value.Id.Value,
        ["conversation_id"] = value.ConversationId.Value,
        ["kind"] = EncodeIndexed(value.Kind),
        ["status"] = WireName.Of(value.State.Status),
        ["abort_requested"] = value.AbortRequested ? 1 : 0,
        ["background"] = value.Background ? 1 : 0,
        ["record"] = Cell(value.ToJson()),
    };

    /// <summary>The row of a submission record.</summary>
    internal static Dictionary<string, JsonNode?> SubmissionRow(SubmissionRecord value) => new(StringComparer.Ordinal)
    {
        ["id"] = value.Id.Value,
        ["conversation_id"] = value.ConversationId.Value,
        ["request_id"] = value.RequestId is null ? "" : EncodeIndexed(value.RequestId),
        ["status"] = WireName.Of(value.Status),
        ["record"] = Cell(value.ToJson()),
    };

    /// <summary>A record as one json(...) cell.</summary>
    private static JsonObject Record() => Tables.Json(Unknown(), new JsonColumnOptions { Version = 1 });

    /// <summary>A record's JSON as the cell's value.</summary>
    private static JsonNode? Cell(DurableJson record) => Node(record);

    /// <summary>A Durable JSON value as a mutable node of the same JSON: strings as .NET strings (a lone surrogate kept, escaped when the
    /// store serializes it), numbers as doubles, keys in the value's order.</summary>
    internal static JsonNode? Node(DurableJson value)
    {
        switch (value)
        {
            case PiSharp.Durable.Chord.JsonNullValue: return null;
            case PiSharp.Durable.Chord.JsonBooleanValue flag: return System.Text.Json.Nodes.JsonValue.Create(flag.Value);
            case PiSharp.Durable.Chord.JsonNumberValue number: return System.Text.Json.Nodes.JsonValue.Create(number.Value);
            case PiSharp.Durable.Chord.JsonStringValue text: return System.Text.Json.Nodes.JsonValue.Create(text.Value);
            case PiSharp.Durable.Chord.JsonArrayValue array: return new JsonArray([.. array.Select(Node)]);
            default:
                var result = new JsonObject();
                foreach (var (key, item) in (PiSharp.Durable.Chord.JsonObjectValue)value) result[key] = Node(item);
                return result;
        }
    }

    private static TableDefinition Define(string name, (string Name, Property Schema)[] columns, params (string Name, string[] Columns)[] indexes)
    {
        var declared = new OrderedDictionary<string, Property>(StringComparer.Ordinal);
        foreach (var (column, schema) in columns) declared.Add(column, schema);
        var keyed = new OrderedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (index, indexed) in indexes) keyed.Add(index, indexed);
        return Tables.DefineTable(name, new TableOptions { Version = 1, Columns = declared, Key = ["id"], Indexes = keyed });
    }
}
