// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/contracts/snapshot.ts
//
// Schema snapshots: one schema-aware walk checks an app schema against Chord's vocabulary and produces a Chord-owned copy and its normal
// form (the fingerprint input). A schema is a JsonNode tree: an object, or a boolean. JsonNode trees have no cycles (a node has one
// parent), symbol keys, accessors or non-enumerable keys, so those refusals of upstream's walk have nothing to refuse here; anything that is
// no JsonNode (a MethodDeclaration, an Optional) is "expected a schema". Area cv2-contracts added this file because every declaration
// snapshots its schemas; area cv2-evolve, which owns snapshot.ts, ported snapshot.test.ts (SnapshotCases.cs).
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Contracts;

/// <summary>A Chord-owned copy of an app schema, its normal form, and whether it has a <c>bytes()</c> leaf (values crossing a link then
/// carry raw byte segments); immutable by convention.</summary>
internal sealed record Snapshot(JsonNode Schema, JsonNode Normal, bool Bytes, HostKind? KeyHost = null);

internal static partial class ContractsInternal
{
    /// <summary>Snapshot of a state, result or params schema. Throws an <see cref="ArgumentException"/> naming <paramref name="path"/>
    /// for anything outside the vocabulary.</summary>
    internal static Snapshot SnapshotSchema(object? source, string path) => Walk(source, path, closed: false);

    /// <summary>Snapshot of a method's arguments: every object schema inside must also be closed (records allowed).</summary>
    internal static Snapshot SnapshotArguments(object? source, string path) => Walk(source, path, closed: true);

    /// <summary>A copied keyword value, its normal form, and whether a schema inside it is a bytes leaf.</summary>
    private readonly record struct Copy(JsonNode? Schema, JsonNode? Normal, bool Bytes);

    /// <summary>How a keyword's value is walked.</summary>
    private enum Keyword { Schema, Schemas, Items, SchemaMap, Type, Required, Enum, Data, Annotation }

    /// <summary>Every supported keyword, grouped by how its value is walked.</summary>
    private static readonly Dictionary<string, Keyword> Keywords = new (Keyword Group, string Names)[]
    {
        (Keyword.Schema, "additionalProperties additionalItems"),
        (Keyword.Schemas, "anyOf allOf prefixItems"),
        (Keyword.Items, "items"),
        (Keyword.SchemaMap, "properties patternProperties $defs"),
        (Keyword.Type, "type"),
        (Keyword.Required, "required"),
        (Keyword.Enum, "enum"),
        (Keyword.Data, "const default $ref $id pattern format minLength maxLength minimum maximum exclusiveMinimum exclusiveMaximum " +
            "multipleOf minItems maxItems uniqueItems minProperties maxProperties readOnly writeOnly"),
        (Keyword.Annotation, "title description examples $comment deprecated"),
    }.SelectMany(group => group.Names.Split(' ').Select(name => (name, group.Group))).ToDictionary(entry => entry.name, entry => entry.Group, StringComparer.Ordinal);

    private static readonly HashSet<string> Types = new(["null", "boolean", "number", "integer", "string", "array", "object", "bytes"], StringComparer.Ordinal);

    private static readonly HashSet<string> BytesKeywords = new(["type", "minLength", "maxLength"], StringComparer.Ordinal);

    private static Snapshot Walk(object? source, string path, bool closed)
    {
        if (source is JsonValue boolean && (ChordJson.IsBoolean(boolean, true) || ChordJson.IsBoolean(boolean, false)))
        {
            var value = ChordJson.IsBoolean(boolean, true);
            return new Snapshot(JsonValue.Create(value), JsonValue.Create(value), false);
        }
        if (ChordJson.AsObject(source as JsonNode) is not { } node) throw new ArgumentException($"{path}: expected a schema");
        var isBytes = ChordJson.AsString(node["type"]) == "bytes";
        var schema = new JsonObject();
        var normal = new JsonObject();
        var bytes = isBytes;
        foreach (var (key, value) in OwnEntries(node))
        {
            if (!Keywords.TryGetValue(key, out var keyword) || (isBytes && keyword != Keyword.Annotation && !BytesKeywords.Contains(key)))
                throw new ArgumentException($"{path}: unsupported keyword \"{key}\"");
            var copy = WalkKeyword(keyword, value, $"{path}.{key}", closed);
            bytes |= copy.Bytes;
            schema[key] = copy.Schema;
            // Annotations are dropped on schema nodes only; inside data they are ordinary keys.
            if (keyword != Keyword.Annotation) normal[key] = copy.Normal;
        }
        if (closed && IsOpenObject(schema)) throw new ArgumentException($"{path}: argument objects must be closed (additionalProperties: false)");
        if (isBytes) CheckBytesBounds(schema, path);
        return new Snapshot(schema, normal, bytes);
    }

    private static Copy WalkKeyword(Keyword keyword, JsonNode? value, string path, bool closed)
    {
        switch (keyword)
        {
            case Keyword.Schema:
                return Copied(Walk(value, path, closed));
            case Keyword.Schemas:
                return WalkList(value, path, closed);
            case Keyword.Items:
                return ChordJson.AsArray(value) is not null ? WalkList(value, path, closed) : Copied(Walk(value, path, closed));
            case Keyword.SchemaMap:
            {
                if (ChordJson.AsObject(value) is not { } map) throw new ArgumentException($"{path}: expected an object of schemas");
                var entries = OwnEntries(map).Select(entry => (entry.Key, Item: Walk(entry.Value, $"{path}.{entry.Key}", closed))).ToList();
                var schema = new JsonObject();
                var normal = new JsonObject();
                foreach (var (key, item) in entries)
                {
                    schema[key] = item.Schema;
                    normal[key] = item.Normal;
                }
                return new Copy(schema, normal, entries.Any(entry => entry.Item.Bytes));
            }
            case Keyword.Type:
            {
                if (ChordJson.AsString(value) is not { } type || !Types.Contains(type))
                    throw new ArgumentException($"{path}: unsupported type {Stringified(value)}");
                return new Copy(JsonValue.Create(type), JsonValue.Create(type), false);
            }
            case Keyword.Required:
            {
                if (ChordJson.AsArray(value) is not { } list || !list.All(name => ChordJson.AsString(name) is not null))
                    throw new ArgumentException($"{path}: expected an array of strings");
                // Array.prototype.sort: strings in UTF-16 order.
                var names = list.Select(name => ChordJson.AsString(name)!).Order(StringComparer.Ordinal).ToList();
                return new Copy(Strings(names), Strings(names), false);
            }
            case Keyword.Enum:
            {
                var data = Data(value, path);
                if (data.Normal is not JsonArray values) throw new ArgumentException($"{path}: expected an array");
                // A stable sort by each value's JSON text, as upstream's sort with compare(JSON.stringify(a), JSON.stringify(b)).
                var sorted = values.Select(item => (Text: ChordJson.Stringify(item), Item: item)).OrderBy(entry => entry.Text, StringComparer.Ordinal)
                    .Select(entry => entry.Item).ToList();
                return new Copy(new JsonArray([.. sorted.Select(item => item?.DeepClone())]), new JsonArray([.. sorted.Select(item => item?.DeepClone())]), false);
            }
            default:
                return Data(value, path);
        }
    }

    private static Copy WalkList(JsonNode? value, string path, bool closed)
    {
        if (ChordJson.AsArray(value) is not { } list) throw new ArgumentException($"{path}: expected an array of schemas");
        var items = list.Select((item, index) => Walk(item, $"{path}[{index}]", closed)).ToList();
        return new Copy(new JsonArray([.. items.Select(item => (JsonNode?)item.Schema)]), new JsonArray([.. items.Select(item => (JsonNode?)item.Normal)]),
            items.Any(item => item.Bytes));
    }

    /// <summary>A JSON value as a copy with sorted keys; the same copy serves as snapshot and normal form (two nodes here, as a JsonNode
    /// has one parent).</summary>
    private static Copy Data(JsonNode? value, string path)
    {
        if (!ChordJson.IsJsonValue(value)) throw new ArgumentException($"{path}: expected JSON data (finite numbers, plain objects and arrays)");
        return new Copy(ChordJson.SortedCopy(value), ChordJson.SortedCopy(value), false);
    }

    private static Copy Copied(Snapshot snapshot) => new(snapshot.Schema, snapshot.Normal, snapshot.Bytes);

    private static JsonArray Strings(IEnumerable<string> names) => new([.. names.Select(name => (JsonNode?)JsonValue.Create(name))]);

    /// <summary>Entries sorted by key (UTF-16 order).</summary>
    private static List<KeyValuePair<string, JsonNode?>> OwnEntries(JsonObject source) => [.. source.OrderBy(entry => entry.Key, StringComparer.Ordinal)];

    /// <summary>An object schema that admits unknown fields. A record (pattern properties only) is allowed.</summary>
    private static bool IsOpenObject(JsonObject node)
    {
        var isRecord = node.ContainsKey("patternProperties") && !node.ContainsKey("properties");
        var isObject = ChordJson.AsString(node["type"]) == "object" || node.ContainsKey("properties");
        return isObject && !isRecord && !(node.TryGetPropertyValue("additionalProperties", out var additional) && ChordJson.IsBoolean(additional, false));
    }

    private static void CheckBytesBounds(JsonObject node, string path)
    {
        foreach (var name in new[] { "minLength", "maxLength" })
        {
            if (!node.TryGetPropertyValue(name, out var bound)) continue;
            // Number.isSafeInteger(bound) && bound >= 0.
            if (ChordJson.AsNumber(bound) is not { } number || !double.IsInteger(number) || number < 0 || number > 9007199254740991)
                throw new ArgumentException($"{path}: bytes bounds must be non-negative integers");
        }
    }

    /// <summary><c>JSON.stringify</c> of a keyword value in a message.</summary>
    private static string Stringified(JsonNode? value) => ChordJson.IsJsonValue(value) ? ChordJson.Stringify(value) : value?.ToJsonString() ?? "null";
}
