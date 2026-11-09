using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Contracts.Schema;

namespace PiSharp.Chord.Legacy;

/// <summary>Legacy schema normalization and coverage. Hashes UTF-16 code units, as src/schema.ts does.</summary>
public static class LegacySchema
{
    private static readonly ConditionalWeakTable<JsonNode, SchemaValidator> Validators = new();
    private static readonly ConditionalWeakTable<JsonNode, SchemaValidator> CallableValidators = new();
    public static string CanonicalJson(JsonNode? value)
    {
        var text = new StringBuilder();
        void Write(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                text.Append('{'); var first = true;
                foreach (var (key, item) in obj.OrderBy(x => x.Key, StringComparer.Ordinal))
                { if (!first) text.Append(','); first = false; ChordJson.Stringify(text, JsonValue.Create(key)); text.Append(':'); Write(item); }
                text.Append('}');
            }
            else if (node is JsonArray arr)
            { text.Append('['); for (var i = 0; i < arr.Count; i++) { if (i > 0) text.Append(','); Write(arr[i]); } text.Append(']'); }
            else ChordJson.Stringify(text, node);
        }
        Write(value); return text.ToString();
    }
    public static string Hash64(string text)
    {
        var a = 0x811c9dc5u; var b = 0x01000193u ^ 0x5bd1e995u;
        foreach (var code in text) { a = unchecked((a ^ code) * 0x01000193u); b = unchecked((b ^ code) * 0x5bd1e995u) ^ (b >> 13); }
        return a.ToString("x8", CultureInfo.InvariantCulture) + b.ToString("x8", CultureInfo.InvariantCulture);
    }
    public static bool Equal(JsonNode? a, JsonNode? b) => CanonicalJson(a) == CanonicalJson(b);
    public static bool IsValid(JsonNode schema, JsonNode? value) => Validators.GetValue(schema, s => SchemaValidator.Compile(Js.FromNode(s), new SchemaOptions())).Check(Data.DataInternal.ToSchemaValue(value));
    public static void Assert(JsonNode schema, JsonNode? value, string reason, string what)
    {
        var validator = Validators.GetValue(schema, s => SchemaValidator.Compile(Js.FromNode(s), new SchemaOptions()));
        var data = Data.DataInternal.ToSchemaValue(value);
        if (validator.Check(data)) return;
        var errors = validator.Errors(data).Take(3).ToArray();
        throw ChordException.Invalid(reason, what + ": " + string.Join("; ", errors.Select(e => $"{(e.Path.Length == 0 ? "/" : e.Path)} {e.Message}")), errors);
    }
    internal static void AssertNative(JsonNode schema, object? value, string reason, string what)
    {
        object? Convert(object? item)
        {
            if (item is JsonNode node) return Data.DataInternal.ToSchemaValue(node);
            if (item is Delegate) return JsFunction.Get("legacy-native");
            if (item is IReadOnlyDictionary<string, object?> dictionary) { var obj = new JsObject(); foreach (var (key, child) in dictionary) obj.Set(key, Convert(child)); return obj; }
            if (item is System.Collections.IEnumerable sequence && item is not string && item is not byte[]) return new JsArray(sequence.Cast<object?>().Select(Convert));
            if (item is sbyte or byte or short or ushort or int or uint or long or ulong or float or decimal) return System.Convert.ToDouble(item, CultureInfo.InvariantCulture);
            return item;
        }
        var converted = Convert(value);
        // pi-schema validates JSON types and bytes; its JsFunction values only model inherited JS methods. A local
        // contract's TypeBox-style callable leaf is an explicit CLR mapping: require a delegate, then let the normal
        // validator enforce every remaining constraint. Parameter/returnType declarations describe coverage and do
        // not call the delegate while checking it. Never relax a non-callable schema for a delegate value.
        var callable = converted is JsFunction && schema is JsonObject obj &&
            (obj["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "function" ||
             obj["type"] is JsonArray types && types.Any(typeNode => typeNode is JsonValue entry && entry.TryGetValue<string>(out var item) && item == "function"));
        var validator = callable
            ? CallableValidators.GetValue(schema, s =>
            {
                var check = s.DeepClone().AsObject(); check.Remove("type");
                return SchemaValidator.Compile(Js.FromNode(check), new SchemaOptions());
            })
            : Validators.GetValue(schema, s => SchemaValidator.Compile(Js.FromNode(s), new SchemaOptions()));
        if (validator.Check(converted)) return;
        var errors = validator.Errors(converted).Take(3).ToArray();
        throw ChordException.Invalid(reason, what + ": " + string.Join("; ", errors.Select(e => e.Message)), errors);
    }
    public static JsonNode? Normalize(JsonNode schema, JsonNode? value, string what)
    { var result = CleanCore(schema, DefaultCore(schema, value?.DeepClone() ?? new JsonObject())); Assert(schema, result, "input", what); return result; }
    public static JsonNode? Clean(JsonNode schema, JsonNode? value) => CleanCore(schema, value?.DeepClone());
    public static string Identity(JsonNode schema, JsonNode? value) => CanonicalJson(StripDefaults(schema, value));
    private static JsonObject AsObject(JsonNode? schema) => schema as JsonObject ?? (schema is JsonValue j && j.TryGetValue<bool>(out var b) && !b ? new JsonObject { ["not"] = new JsonObject() } : new JsonObject());
    private static JsonObject? Merge(JsonObject schema)
    {
        if (schema["allOf"] is not JsonArray branches || !branches.All(x => x is JsonObject o && o["properties"] is JsonObject)) return null;
        var properties = new JsonObject(); var required = new HashSet<string>();
        foreach (var branch in branches.Cast<JsonObject>()) { foreach (var (key, value) in branch["properties"]!.AsObject()) properties[key] = value?.DeepClone(); foreach (var key in Strings(branch["required"])) required.Add(key); }
        var result = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
        if (schema.ContainsKey("unevaluatedProperties")) result["additionalProperties"] = schema["unevaluatedProperties"]?.DeepClone();
        return result;
    }
    private static JsonNode? DefaultCore(JsonNode? node, JsonNode? value)
    {
        var schema = AsObject(node); if (Merge(schema) is { } merged) return DefaultCore(merged, value);
        if (schema["anyOf"] is JsonArray union)
            foreach (var branch in union) { var candidate = DefaultCore(branch, value?.DeepClone()); if (branch is not null && IsValid(branch, candidate)) return candidate; }
        if (value is JsonArray array)
        {
            var prefix = schema["items"] as JsonArray ?? schema["prefixItems"] as JsonArray;
            if (prefix is not null)
            { for (var i = 0; i < prefix.Count; i++) { var entry = i < array.Count ? array[i]?.DeepClone() : AsObject(prefix[i])["default"]?.DeepClone(); if (i < array.Count) array[i] = DefaultCore(prefix[i], entry); else if (entry is not null) array.Add(DefaultCore(prefix[i], entry)); } }
            else if (schema["items"] is JsonObject items) for (var i = 0; i < array.Count; i++) array[i] = DefaultCore(items, array[i]?.DeepClone());
        }
        if (value is JsonObject obj)
        {
            if (schema["properties"] is JsonObject props)
            {
                var required = Strings(schema["required"]).ToHashSet();
                foreach (var (key, property) in props)
                    if (obj.ContainsKey(key)) obj[key] = DefaultCore(property, obj[key]?.DeepClone());
                    else if (required.Contains(key) && AsObject(property).ContainsKey("default")) obj[key] = AsObject(property)["default"]?.DeepClone();
            }
            foreach (var key in obj.Select(x => x.Key).ToArray())
            { var child = Property(schema, key); if (child is JsonObject) obj[key] = DefaultCore(child, obj[key]?.DeepClone()); }
        }
        return value;
    }
    private static JsonNode? Property(JsonObject schema, string key)
    {
        if (schema["properties"] is JsonObject props && props.TryGetPropertyValue(key, out var known)) return known;
        if (schema["patternProperties"] is JsonObject patterns) foreach (var (pattern, value) in patterns) if (Regex.IsMatch(key, pattern)) return value;
        return schema["additionalProperties"];
    }
    private static JsonNode? CleanCore(JsonNode? node, JsonNode? value)
    {
        var schema = AsObject(node); if (Merge(schema) is { } merged) return CleanCore(merged, value);
        if (schema["anyOf"] is JsonArray union)
            foreach (var branch in union) { var candidate = CleanCore(branch, value?.DeepClone()); if (branch is not null && IsValid(branch, candidate)) return candidate; }
        if (value is JsonArray arr)
        {
            var prefix = schema["items"] as JsonArray ?? schema["prefixItems"] as JsonArray;
            if (prefix is not null) { while (arr.Count > prefix.Count) arr.RemoveAt(arr.Count - 1); for (var i = 0; i < arr.Count; i++) arr[i] = CleanCore(prefix[i], arr[i]?.DeepClone()); }
            else if (schema["items"] is JsonObject items) for (var i = 0; i < arr.Count; i++) arr[i] = CleanCore(items, arr[i]?.DeepClone());
        }
        if (value is JsonObject obj && (schema["properties"] is JsonObject || schema["patternProperties"] is JsonObject || schema["additionalProperties"] is JsonObject))
            foreach (var key in obj.Select(x => x.Key).ToArray())
            {
                var child = Property(schema, key);
                if (child is JsonObject childSchema) { if (schema["additionalProperties"] == child && !IsValid(childSchema, obj[key])) obj.Remove(key); else obj[key] = CleanCore(child, obj[key]?.DeepClone()); }
                else if (child is not JsonValue j || !j.TryGetValue<bool>(out var keep) || !keep) obj.Remove(key);
            }
        return value;
    }
    private static JsonNode? StripDefaults(JsonNode? node, JsonNode? value)
    {
        var schema = AsObject(node);
        if (value is JsonArray array) return new JsonArray(array.Select((v, i) => StripDefaults(schema["items"] is JsonArray tuple && i < tuple.Count ? tuple[i] : schema["items"], v)).ToArray());
        if (value is not JsonObject obj) return value?.DeepClone();
        var result = new JsonObject(); var props = AsObject(schema["properties"]);
        foreach (var (key, item) in obj) { var property = AsObject(props[key]); if (property.ContainsKey("default") && Equal(property["default"], item)) continue; result[key] = StripDefaults(property, item); }
        return result;
    }
    private static readonly HashSet<string> Annotations = ["title", "description", "default", "examples", "$id", "$schema", "$comment", "readOnly", "writeOnly", "deprecated"];
    private static readonly string[] Constraints = ["minLength", "maxLength", "pattern", "format", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties"];
    private static readonly HashSet<string> Known = new(Constraints.Concat(["type", "properties", "required", "additionalProperties", "patternProperties", "items", "additionalItems", "const", "enum", "anyOf", "parameters", "returnType"]));
    private static IEnumerable<string> Strings(JsonNode? value) => value is JsonArray arr ? arr.Select(x => x!.GetValue<string>()) : value is JsonValue j && j.TryGetValue<string>(out var s) ? [s] : [];
    public static bool Fits(JsonNode? subSchema, JsonNode? supSchema)
    {
        JsonObject Without(JsonNode? node) { var copy = AsObject(node).DeepClone().AsObject(); foreach (var key in Annotations) copy.Remove(key); return copy; }
        var sub = Without(subSchema); var sup = Without(supSchema);
        if (sup.Count == 0 || Equal(sub, sup)) return true;
        if (sup.Any(x => !Known.Contains(x.Key)) || sup["additionalProperties"] is JsonObject) return false;
        if (sub["anyOf"] is JsonArray su) return su.All(x => Fits(x, sup));
        if (sup["anyOf"] is JsonArray sp) return sp.Any(x => Fits(sub, x));
        if (sup.ContainsKey("const")) return sub.ContainsKey("const") && Equal(sub["const"], sup["const"]);
        if (sup["enum"] is JsonArray allowed) { var values = sub.ContainsKey("const") ? new[] { sub["const"] } : sub["enum"] is JsonArray entries ? entries.ToArray() : null; return values is not null && values.All(x => allowed.Any(y => Equal(x, y))); }
        if (Constraints.Any(key => sup.ContainsKey(key) && !Equal(sub[key], sup[key]))) return false;
        var st = Strings(sub["type"]).ToArray(); var pt = Strings(sup["type"]).ToArray();
        if (st.Length == 0 || pt.Length == 0 || st.Any(type => !pt.Contains(type) && !(type == "integer" && pt.Contains("number")))) return false;
        if (sub.ContainsKey("const") || sub["enum"] is JsonArray) return true;
        if (st.Contains("object"))
        {
            var so = AsObject(sub["properties"]); var po = AsObject(sup["properties"]); var sr = Strings(sub["required"]).ToHashSet();
            if (Strings(sup["required"]).Any(x => !sr.Contains(x))) return false;
            foreach (var (key, property) in po) if (so.ContainsKey(key) ? !Fits(so[key], property) : sub["additionalProperties"] is JsonObject extra && !Fits(extra, property)) return false;
            if (sup["additionalProperties"] is JsonValue closed && closed.TryGetValue<bool>(out var b) && !b)
                if (!(sub["additionalProperties"] is JsonValue sc && sc.TryGetValue<bool>(out var sb) && !sb) || so.Any(x => !po.ContainsKey(x.Key))) return false;
            var patterns = AsObject(sup["patternProperties"]); var subPatterns = AsObject(sub["patternProperties"]);
            if (patterns.Count > 0)
            {
                if (!(sub["additionalProperties"] is JsonValue ap && ap.TryGetValue<bool>(out var ab) && !ab) && subPatterns.Count == 0) return false;
                foreach (var (pattern, property) in patterns)
                {
                    foreach (var (key, value) in so) if (!po.ContainsKey(key) && Regex.IsMatch(key, pattern) && !Fits(value, property)) return false;
                    if (subPatterns.Count > 0 && (!subPatterns.ContainsKey(pattern) || !Fits(subPatterns[pattern], property))) return false;
                }
                if (subPatterns.Any(x => !patterns.ContainsKey(x.Key))) return false;
            }
        }
        if (st.Contains("array"))
        {
            if (sup["items"] is JsonArray tuple) return sub["items"] is JsonArray from && from.Count == tuple.Count && from.Select((x, i) => Fits(x, tuple[i])).All(x => x);
            if (sub["items"] is JsonArray items) return items.All(x => Fits(x, sup["items"]));
            return Fits(sub["items"], sup["items"]);
        }
        if (st.Contains("function")) { var a = sub["parameters"] as JsonArray ?? []; var b = sup["parameters"] as JsonArray ?? []; return a.Count <= b.Count && a.Select((x, i) => Fits(b[i], x)).All(x => x) && Fits(sub["returnType"], sup["returnType"]); }
        return true;
    }
}
