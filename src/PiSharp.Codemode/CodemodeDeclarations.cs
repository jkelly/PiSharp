// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/declarations.ts.
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
    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();

    [GeneratedRegex("\r?\n")] private static partial Regex LineBreak();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    /// <summary>TypeScript types for MCP results, from the MCP <c>CallToolResult</c> schema.</summary>
    public const string McpTypeScriptPreamble = """
        type Role = "user" | "assistant";
        type MetaObject = Record<string, unknown>;
        type Annotations = {
          audience?: Role[];
          priority?: number;
          lastModified?: string;
        };
        type Icon = {
          src: string;
          mimeType?: string;
          sizes?: string[];
          theme?: "light" | "dark";
        };
        type TextResourceContents = {
          uri: string;
          mimeType?: string;
          _meta?: MetaObject;
          text: string;
        };
        type BlobResourceContents = {
          uri: string;
          mimeType?: string;
          _meta?: MetaObject;
          blob: string;
        };
        type TextContent = {
          type: "text";
          text: string;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type ImageContent = {
          type: "image";
          data: string;
          mimeType: string;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type AudioContent = {
          type: "audio";
          data: string;
          mimeType: string;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type ResourceLink = {
          icons?: Icon[];
          name: string;
          title?: string;
          uri: string;
          description?: string;
          mimeType?: string;
          annotations?: Annotations;
          size?: number;
          _meta?: MetaObject;
          type: "resource_link";
        };
        type EmbeddedResource = {
          type: "resource";
          resource: TextResourceContents | BlobResourceContents;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type ContentBlock =
          | TextContent
          | ImageContent
          | AudioContent
          | ResourceLink
          | EmbeddedResource;
        type CallToolResult<TStructured = { [key: string]: unknown }> = {
          _meta?: MetaObject;
          content: ContentBlock[];
          isError?: boolean;
          structuredContent?: TStructured;
          [key: string]: unknown;
        };
        """;

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

    /// <summary>The <c>structuredContent</c> schema of an MCP <c>CallToolResult</c> output schema, <c>true</c> when it declares
    /// none, or null when the schema is not a <c>CallToolResult</c>.</summary>
    public static JsonElement? McpStructuredContentSchema(JsonElement? schema)
    {
        if (schema is not { ValueKind: JsonValueKind.Object } value || !Get(value, "properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return null;
        if (!Get(properties, "content", out var content) || content.ValueKind != JsonValueKind.Object || !TypeIs(content, "array") ||
            !Get(content, "items", out var items) || items.ValueKind != JsonValueKind.Object || !TypeIs(items, "object")) return null;
        if (!Get(properties, "isError", out var isError) || isError.ValueKind != JsonValueKind.Object || !TypeIs(isError, "boolean") ||
            !Get(properties, "_meta", out var meta) || meta.ValueKind != JsonValueKind.Object || !TypeIs(meta, "object")) return null;
        return Get(properties, "structuredContent", out var structured) && structured.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False
            ? structured : True;
    }

    /// <summary><c>CallToolResult&lt;T&gt;</c> for MCP output schemas, the schema's type otherwise, <c>unknown</c> without one.</summary>
    public static string RenderToolOutputType(JsonElement? schema)
    {
        if (McpStructuredContentSchema(schema) is { } structured)
        {
            var type = SchemaToType(structured);
            return type == "unknown" ? "CallToolResult" : $"CallToolResult<{type}>";
        }
        return schema is null ? "unknown" : SchemaToType(schema.Value);
    }

    /// <summary>A JSON Schema as a TypeScript type expression. A result longer than <paramref name="maxChars"/> is <c>unknown</c>.</summary>
    public static string SchemaToType(JsonElement schema, int? maxChars = null)
    {
        var type = ToType(schema, new Context(schema));
        return maxChars is { } max && type.Length > max ? "unknown" : type;
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

    private sealed class Context(JsonElement root)
    {
        public JsonElement Root { get; } = root;
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
        if (!names.Any(name => DescriptionOf(Property(name)).Length > 0)) return "{ " + string.Join(" ", members) + " }";
        var lines = new List<string> { "{" };
        for (var index = 0; index < names.Count; index++)
        {
            foreach (var line in LineBreak().Split(DescriptionOf(Property(names[index]))))
                if (Js.Trim(line).Length > 0) lines.Add($"{Indent}// {Js.Trim(line)}");
            lines.Add(Indent + members[index].Replace("\n", "\n" + Indent, StringComparison.Ordinal));
        }
        foreach (var member in members.Skip(names.Count)) lines.Add(Indent + member);
        lines.Add("}");
        return string.Join("\n", lines);
    }
}
