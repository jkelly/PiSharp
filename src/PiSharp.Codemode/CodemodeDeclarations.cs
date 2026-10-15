// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/codemode/src/declarations.ts.
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.Codemode;

/// <summary>TypeScript declarations of the script-visible API, rendered from JSON Schemas.</summary>
public static partial class CodemodeDeclarations
{
    private const string Indent = "  ";
    /// <summary>Largest rendered input type, in characters, before it becomes <c>unknown</c>.</summary>
    public const int DefaultInputSchemaMaxChars = 16_000;
    /// <summary>Local <c>$ref</c> expansions per rendered schema.</summary>
    private const int MaxRefExpansions = 32;

    [GeneratedRegex("\r?\n")] private static partial Regex LineBreak();
    /// <summary>Characters that end a <c>//</c> comment.</summary>
    [GeneratedRegex("[\r\n\u2028\u2029]")] private static partial Regex LineTerminators();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    /// <summary>JSON Schema keywords with a number or string value shown as <c>@keyword value</c> property tags.</summary>
    private static readonly string[] TagKeywords =
        ["minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength", "pattern", "format", "minItems", "maxItems"];
    /// <summary>Tag keywords whose values all hold at once, so only the largest (lower) or smallest (upper) matters.</summary>
    private static readonly HashSet<string> LowerBounds = new(StringComparer.Ordinal) { "minimum", "exclusiveMinimum", "minLength", "minItems" };
    private static readonly HashSet<string> UpperBounds = new(StringComparer.Ordinal) { "maximum", "exclusiveMaximum", "maxLength", "maxItems" };
    /// <summary>Longest property tag; longer ones (large defaults, long patterns) are left out.</summary>
    private const int MaxTagChars = 80;

    /// <summary>Tools become members of <c>declare const tools</c>, globals <c>declare function</c> statements, and
    /// <c>ns.member</c> globals members of <c>declare const ns</c>.</summary>
    public static string RenderDeclarations(IReadOnlyList<CodemodeTool>? tools = null, IReadOnlyList<CodemodeTool>? globals = null)
    {
        var sections = new List<string>();
        if (tools is { Count: > 0 })
            sections.Add("declare const tools: {\n" + string.Join("\n", tools.Select(tool => DocComment(tool.Description, Indent) + Indent + RenderToolSignature(tool))) + "\n};");
        var namespaces = new List<(string Name, List<string> Members)>();
        foreach (var global in globals ?? [])
        {
            var dot = global.Name.IndexOf('.');
            if (dot == -1) { sections.Add(RenderGlobal("declare function " + global.Name, global, "")); continue; }
            var name = global.Name[..dot];
            var members = namespaces.FirstOrDefault(entry => entry.Name == name).Members;
            if (members is null) { members = []; namespaces.Add((name, members)); }
            members.Add(RenderGlobal(global.Name[(dot + 1)..], global, Indent));
        }
        foreach (var (name, members) in namespaces) sections.Add($"declare const {name}: {{\n{string.Join("\n", members)}\n}};");
        return string.Join("\n\n", sections);
    }

    /// <summary><c>name(args: T): Promise&lt;R&gt;;</c> with the script identifier. Input types longer than
    /// <paramref name="inputMaxChars"/> render as <c>unknown</c>.</summary>
    public static string RenderToolSignature(CodemodeTool tool, int inputMaxChars = DefaultInputSchemaMaxChars)
    {
        var input = tool.InputSchema is null ? "unknown" : SchemaToType(tool.InputSchema.Value, inputMaxChars);
        return $"{CodemodeIdentifier.ToIdentifier(tool.Name)}(args: {input}): Promise<{RenderToolOutputType(tool.OutputSchema?.Value)}>;";
    }

    /// <summary>The description followed by the tool's declaration.</summary>
    public static string RenderToolSample(CodemodeTool tool, int inputMaxChars = DefaultInputSchemaMaxChars) =>
        $"{(tool.Description is null ? "" : Js.Trim(tool.Description))}\n\ncodemode tool declaration:\n```ts\ndeclare const tools: {{ {RenderToolSignature(tool, inputMaxChars)} }};\n```";

    /// <summary>The type a tool call resolves to: the output schema's type, and <c>unknown</c> without a schema.</summary>
    public static string RenderToolOutputType(JsonElement? schema) => schema is null ? "unknown" : SchemaToType(schema.Value);

    /// <summary>A JSON Schema as a TypeScript type expression: objects on one line, or one property per line with <c>//</c> comments
    /// when a property has a description or constraints the type cannot express (<c>// @integer @minimum 1 @default 20</c>). A result
    /// longer than <paramref name="maxChars"/> is rendered again without the constraint tags, and is <c>unknown</c> if it is still
    /// too long.</summary>
    public static string SchemaToType(JsonElement schema, int? maxChars = null)
    {
        string Render(bool tags) => ToType(schema, new Context(schema, tags));
        var type = Render(true);
        if (maxChars is not { } max || type.Length <= max) return type;
        var untagged = Render(false);
        return untagged.Length <= max ? untagged : "unknown";
    }

    /// <summary>The output type collapsed to one line (tool.ts describeOutput's fallback).</summary>
    internal static string OneLine(string type) => Whitespace().Replace(type, " ");

    private static string RenderGlobal(string head, CodemodeTool global, string indent)
    {
        if (global.Signature is not null) return $"{DocComment(global.Description, indent)}{indent}{head}{global.Signature};";
        var input = global.InputSchema is null ? "unknown" : SchemaToType(global.InputSchema.Value);
        var output = global.OutputSchema is null ? "unknown" : SchemaToType(global.OutputSchema.Value);
        return $"{DocComment(global.Description, indent)}{indent}{head}(args: {input}): Promise<{output}>;";
    }

    private static string DocComment(string? description, string indent)
    {
        var text = description is null ? "" : Js.Trim(description);
        if (text.Length == 0) return "";
        var lines = LineBreak().Split(text.Replace("*/", "*\\/", StringComparison.Ordinal));
        if (lines.Length == 1) return $"{indent}/** {lines[0]} */\n";
        return $"{indent}/**\n{string.Join("\n", lines.Select(line => $"{indent} *{(line.Length > 0 ? " " + line : "")}"))}\n{indent} */\n";
    }

    private static string PropertyKey(string name) => CodemodeIdentifier.IsIdentifier(name) ? name : Js.Stringify(name);

    private static bool Get(JsonElement value, string name, out JsonElement property)
    {
        property = default;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        // Duplicate keys: the last one wins, as in JSON.parse.
        foreach (var candidate in value.EnumerateObject()) if (candidate.Name == name) { property = candidate.Value; found = true; }
        return found;
    }
    private static bool TypeIs(JsonElement value, string type) => Get(value, "type", out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == type;
    private static List<KeyValuePair<string, JsonElement>> Properties(JsonElement value)
    {
        var properties = new List<KeyValuePair<string, JsonElement>>();
        foreach (var property in value.EnumerateObject())
        {
            var index = properties.FindIndex(entry => entry.Key == property.Name);
            if (index >= 0) properties[index] = new(property.Name, property.Value); else properties.Add(new(property.Name, property.Value));
        }
        return properties;
    }

    private static string Union(IEnumerable<string> types)
    {
        var unique = types.Distinct(StringComparer.Ordinal).ToList();
        if (unique.Contains("unknown")) return "unknown";
        return unique.Count == 0 ? "never" : string.Join(" | ", unique);
    }

    private sealed class Context(JsonElement root, bool tags)
    {
        public JsonElement Root { get; } = root;
        /// <summary>Whether property comments include constraint tags.</summary>
        public bool Tags { get; } = tags;
        public HashSet<string> Resolving { get; } = new(StringComparer.Ordinal);
        public int Expansions { get; set; }
    }

    private static JsonElement? ResolveRef(string reference, JsonElement root)
    {
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var current = root;
        foreach (var segment in (reference.Length > 2 ? reference[2..] : "").Split('/').Where(segment => segment.Length > 0))
        {
            string key;
            try { key = Uri.UnescapeDataString(segment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal); }
            catch (UriFormatException) { return null; }
            if (current.ValueKind != JsonValueKind.Object || !Get(current, key, out var next)) return null;
            current = next;
        }
        return current.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Object ? current : null;
    }

    private static string ToType(JsonElement schema, Context context, string? typeOverride = null)
    {
        if (schema.ValueKind == JsonValueKind.True) return "unknown";
        if (schema.ValueKind == JsonValueKind.False) return "never";
        if (schema.ValueKind != JsonValueKind.Object) return "unknown";
        if (typeOverride is null && Get(schema, "$ref", out var refValue) && refValue.ValueKind == JsonValueKind.String)
        {
            var reference = refValue.GetString()!;
            if (context.Resolving.Contains(reference) || context.Expansions >= MaxRefExpansions) return "unknown";
            if (ResolveRef(reference, context.Root) is not { } target) return "unknown";
            context.Expansions++; context.Resolving.Add(reference);
            try { return ToType(target, context); }
            finally { context.Resolving.Remove(reference); }
        }
        if (Get(schema, "const", out var constant)) return Js.Stringify(constant);
        if (Get(schema, "enum", out var values) && values.ValueKind == JsonValueKind.Array) return Union(values.EnumerateArray().Select(Js.Stringify));

        JsonElement variants = default; var hasVariants = Get(schema, "anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array;
        if (hasVariants) variants = anyOf;
        else if (Get(schema, "oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array) { variants = oneOf; hasVariants = true; }
        if (hasVariants) return Union(variants.EnumerateArray().Select(variant => ToType(variant, context)).ToList());
        if (Get(schema, "allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            var parts = allOf.EnumerateArray().Select(part => ToType(part, context)).Where(part => part != "unknown").ToList();
            return parts.Count == 0 ? "unknown" : string.Join(" & ", parts.Select(part => part.Contains(" | ", StringComparison.Ordinal) ? $"({part})" : part));
        }

        string? type = typeOverride;
        var hasType = typeOverride is not null || Get(schema, "type", out _);
        if (typeOverride is null && Get(schema, "type", out var declared))
        {
            if (declared.ValueKind == JsonValueKind.Array)
                return Union(declared.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.String ? ToType(schema, context, entry.GetString()) : "unknown").ToList());
            type = declared.ValueKind == JsonValueKind.String ? declared.GetString() : "\0other";
        }
        switch (type)
        {
            case "string": return "string";
            case "number": case "integer": return "number";
            case "boolean": return "boolean";
            case "null": return "null";
            case "array": return ArrayType(schema, context);
            case "object": return ObjectType(schema, context);
            case null when !hasType:
                if (Get(schema, "properties", out _) || Get(schema, "additionalProperties", out _) || Get(schema, "required", out _)) return ObjectType(schema, context);
                if (Get(schema, "items", out _) || Get(schema, "prefixItems", out _)) return ArrayType(schema, context);
                return "unknown";
            default: return "unknown";
        }
    }

    private static string ArrayType(JsonElement schema, Context context)
    {
        var hasItems = Get(schema, "items", out var items);
        if (hasItems && items.ValueKind != JsonValueKind.Array) return $"Array<{ToType(items, context)}>";
        JsonElement? tuple = Get(schema, "prefixItems", out var prefix) && prefix.ValueKind == JsonValueKind.Array ? prefix
            : hasItems && items.ValueKind == JsonValueKind.Array ? items : null;
        if (tuple is { } entries && entries.GetArrayLength() > 0) return $"[{string.Join(", ", entries.EnumerateArray().Select(item => ToType(item, context)))}]";
        return "unknown[]";
    }

    private static string DescriptionOf(JsonElement property) =>
        property.ValueKind == JsonValueKind.Object && Get(property, "description", out var description) && description.ValueKind == JsonValueKind.String
            ? Js.Trim(description.GetString()!) : "";

    /// <summary>Constraints of a property that its TypeScript type cannot express, as JSDoc-style tags:
    /// <c>@integer @minimum 1 @maximum 100 @default 20</c>. Empty when there are none.</summary>
    private static string ConstraintTags(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object) return "";
        // Optional fields such as Pydantic's `int | None` keep their constraints in the non-null branch.
        // The branch's constraints apply together with the property's, so both are read.
        JsonElement? variants = Get(property, "anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array ? anyOf
            : Get(property, "oneOf", out var oneOf) ? oneOf : null;
        var nonNull = variants is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(variant => !(Get(variant, "type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "null")).ToList()
            : [];
        JsonElement[] branches = nonNull.Count == 1 && nonNull[0].ValueKind == JsonValueKind.Object ? [property, nonNull[0]] : [property];
        // Draft-04 and OpenAPI 3.0 make `minimum`/`maximum` exclusive with a boolean `exclusiveMinimum`/`exclusiveMaximum`.
        var schemas = branches.Select(schema =>
        {
            var view = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            foreach (var key in TagKeywords.Append("type").Append("uniqueItems")) view[key] = Get(schema, key, out var value) ? value : null;
            if (view["exclusiveMinimum"] is { ValueKind: JsonValueKind.True }) { view["exclusiveMinimum"] = view["minimum"]; view["minimum"] = null; }
            if (view["exclusiveMaximum"] is { ValueKind: JsonValueKind.True }) { view["exclusiveMaximum"] = view["maximum"]; view["maximum"] = null; }
            return view;
        }).ToList();

        var tags = new List<string>();
        static bool IsInteger(JsonElement? type) => type is { ValueKind: JsonValueKind.String } name ? name.GetString() == "integer"
            : type is { ValueKind: JsonValueKind.Array } names && names.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.String && entry.GetString() == "integer");
        if (schemas.Any(schema => IsInteger(schema["type"]))) tags.Add("@integer");
        foreach (var keyword in TagKeywords)
        {
            var values = schemas.Select(schema => schema[keyword]).ToList();
            if (LowerBounds.Contains(keyword) || UpperBounds.Contains(keyword))
            {
                var numbers = values.Where(value => value is { ValueKind: JsonValueKind.Number }).Select(value => Js.NumberOf(value!.Value)).ToList();
                if (numbers.Count == 0) continue;
                tags.Add($"@{keyword} {Js.NumberText(LowerBounds.Contains(keyword) ? numbers.Aggregate(Math.Max) : numbers.Aggregate(Math.Min))}");
                continue;
            }
            // new Set(values): numbers by value, strings by content, in first-seen order.
            var seen = new List<object>();
            foreach (var value in values)
            {
                object? item = value switch
                {
                    { ValueKind: JsonValueKind.Number } numberValue => Js.NumberOf(numberValue),
                    { ValueKind: JsonValueKind.String } text => text.GetString()!,
                    _ => null
                };
                if (item is null || seen.Any(existing => existing.Equals(item))) continue;
                seen.Add(item);
                tags.Add($"@{keyword} {(item is double number ? Js.NumberText(number) : (string)item)}");
            }
        }
        if (schemas.Any(schema => schema["uniqueItems"] is { ValueKind: JsonValueKind.True })) tags.Add("@uniqueItems");
        // Annotations, unlike constraints, come from the property itself.
        if (Get(property, "default", out var fallback) && fallback.ValueKind != JsonValueKind.Null) tags.Add($"@default {Js.Stringify(fallback)}");
        if (Get(property, "deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True) tags.Add("@deprecated");
        return string.Join(" ", tags.Where(tag => tag.Length <= MaxTagChars)
            .Select(tag => LineTerminators().Replace(tag, match => "\\u" + ((int)match.Value[0]).ToString("x4", System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static string ObjectType(JsonElement schema, Context context)
    {
        var properties = Get(schema, "properties", out var declared) && declared.ValueKind == JsonValueKind.Object ? Properties(declared) : [];
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (Get(schema, "required", out var requiredList) && requiredList.ValueKind == JsonValueKind.Array)
            foreach (var name in requiredList.EnumerateArray()) if (name.ValueKind == JsonValueKind.String) required.Add(name.GetString()!);
        var names = properties.Select(property => property.Key).Order(StringComparer.Ordinal).ToList();
        JsonElement Property(string name) => properties.First(property => property.Key == name).Value;
        var members = names.Select(name => $"{PropertyKey(name)}{(required.Contains(name) ? "" : "?")}: {ToType(Property(name), context)};").ToList();
        var hasAdditional = Get(schema, "additionalProperties", out var additional);
        if (hasAdditional && additional.ValueKind != JsonValueKind.False)
            members.Add($"[key: string]: {(additional.ValueKind == JsonValueKind.True ? "unknown" : ToType(additional, context))};");
        else if (!hasAdditional && names.Count == 0) members.Add("[key: string]: unknown;");
        if (members.Count == 0) return "{}";
        var comments = names.Select(name =>
        {
            var lines = LineTerminators().Split(DescriptionOf(Property(name))).Select(Js.Trim).Where(line => line.Length > 0).ToList();
            var tags = context.Tags ? ConstraintTags(Property(name)) : "";
            if (tags.Length > 0) lines.Add(tags);
            return lines;
        }).ToList();
        if (!comments.Any(comment => comment.Count > 0)) return "{ " + string.Join(" ", members) + " }";
        var lines = new List<string> { "{" };
        for (var index = 0; index < names.Count; index++)
        {
            foreach (var line in comments[index]) lines.Add($"{Indent}// {line}");
            lines.Add(Indent + members[index].Replace("\n", "\n" + Indent, StringComparison.Ordinal));
        }
        foreach (var member in members.Skip(names.Count)) lines.Add(Indent + member);
        lines.Add("}");
        return string.Join("\n", lines);
    }
}
