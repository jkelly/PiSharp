// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/resolver.ts
// Relative identifier correction: Pi 7d073328e44337d3292a7db9b0f0308f679c5e97.
//
// Reference resolution of one compile: schema resources, anchors and JSON Pointers over the root schema and the registered documents.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // the engine uses its own experimental types

namespace PiSharp.Contracts.Schema;

/// <summary>A schema and the base URI its references resolve against.</summary>
internal readonly record struct ResolvedSchema(object? Node, string Base);

/// <summary>
/// Resolves references in the root schema and the registered documents. The schema is indexed on the first reference or identifier,
/// so a schema with neither is never walked for identifiers. Every member runs under the compile's lock.
/// </summary>
internal sealed class SchemaResolver
{
    /// <summary>The base URI of a root schema without <c>$id</c>; relative references resolve against it.</summary>
    public const string DefaultBase = "https://json-schema.invalid/root.json";

    private static readonly string[] SchemaKeywords =
    [
        "additionalProperties", "propertyNames", "contains", "not", "if", "then", "else", "unevaluatedProperties", "unevaluatedItems",
        "additionalItems", "contentSchema", "items",
    ];

    private static readonly string[] MapKeywords = ["properties", "patternProperties", "$defs", "definitions", "dependentSchemas", "dependencies"];
    private static readonly string[] ArrayKeywords = ["allOf", "anyOf", "oneOf", "prefixItems", "items"];

    private readonly object _sync;
    private readonly object? _root;
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _schemas;
    private readonly Dictionary<string, object?> _registry = new(StringComparer.Ordinal);
    private readonly bool _rejectUnresolved;
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsObject> _anchors = new(StringComparer.Ordinal);
    private bool _indexed;

    public SchemaResolver(object? root, SchemaOptions options, object sync)
    {
        _sync = sync;
        Draft7 = options.Draft7;
        _root = root;
        _schemas = options.Schemas;
        _rejectUnresolved = options.RejectUnresolvedRefs;
    }

    public bool Draft7 { get; }

    /// <summary>The starting base; the root's own identifier is applied by <see cref="BaseFor"/>.</summary>
    public string RootBase => DefaultBase;

    /// <summary>Schema object to the base URI it was indexed under.</summary>
    public Dictionary<JsObject, string> BaseOf { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>URI (with a <c>#name</c> fragment) of every <c>$dynamicAnchor</c>, to its schema.</summary>
    public Dictionary<string, JsObject> DynamicAnchors { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The base URI of <paramref name="node"/>, whose parent has <paramref name="base"/>: its <c>$id</c> changes it, unless draft 7 makes
    /// the <c>$id</c> inert. Once the schema is indexed, the indexed base.
    /// </summary>
    public string BaseFor(JsObject node, string @base)
    {
        lock (_sync)
        {
            if (BaseOf.TryGetValue(node, out var indexed)) return indexed;
            if (node.GetOwn("$id") is not string id) return @base;
            // The caller may already carry this node's base (reference targets and annotation
            // probes do). Index from the original document base before applying an identifier.
            if (!_indexed)
            {
                EnsureIndexed();
                if (BaseOf.TryGetValue(node, out indexed)) return indexed;
            }
            if (Draft7 && (node.GetOwn("$ref") is string || id.StartsWith('#'))) return @base;
            return DocumentOf(ResolveUri(id, @base));
        }
    }

    /// <summary>The indexed base of <paramref name="node"/> when it is a schema object, else <paramref name="base"/>.</summary>
    public string IndexedBaseOr(object? node, string @base)
    {
        lock (_sync) return node is JsObject o && BaseOf.TryGetValue(o, out var b) ? b : @base;
    }

    private void EnsureIndexed()
    {
        if (_indexed) return;
        _indexed = true;
        foreach (var (uri, schema) in _schemas) _registry[DocumentOf(ResolveUri(uri, DefaultBase))] = schema;
        _resources[DefaultBase] = _root;
        Index(_root, DefaultBase);
    }

    /// <summary>
    /// The schema a reference names, with the base URI it compiles under. Throws if it names nothing, or, with unresolved references
    /// rejected, resolves to <c>false</c>, the schema no value passes.
    /// </summary>
    public ResolvedSchema Resolve(string @ref, string @base)
    {
        lock (_sync)
        {
            EnsureIndexed();
            if (!_rejectUnresolved) return Find(@ref, @base);
            try
            {
                return Find(@ref, @base);
            }
            catch (JsonSchemaException)
            {
                _unresolved.Add(ResolveUri(@ref, @base));
                return new ResolvedSchema(false, @base);
            }
        }
    }

    /// <summary>Whether <paramref name="ref"/>, from <paramref name="base"/>, named nothing and resolved to <c>false</c>.</summary>
    public bool IsUnresolved(string @ref, string @base)
    {
        lock (_sync) return _unresolved.Contains(ResolveUri(@ref, @base));
    }

    private ResolvedSchema Find(string @ref, string @base)
    {
        var uri = ResolveUri(@ref, @base);
        var doc = DocumentOf(uri);
        string fragment;
        try
        {
            fragment = uri.Length > doc.Length ? JsUrl.DecodeUriComponent(uri[(doc.Length + 1)..]) : "";
        }
        catch (FormatException)
        {
            throw new JsonSchemaException("URI malformed"); // decodeURIComponent's URIError
        }

        var found = _resources.TryGetValue(doc, out var root) || Load(doc, out root);
        if (!found) throw new JsonSchemaException($"Unresolved $ref {@ref} (no schema registered for {doc})");
        if (fragment.Length == 0) return new ResolvedSchema(root, doc);
        if (fragment.StartsWith('/')) return Pointer(root, doc, fragment);
        if (!_anchors.TryGetValue($"{doc}#{fragment}", out var target))
            throw new JsonSchemaException($"Unresolved $ref {@ref} (no anchor {fragment} in {doc})");
        return new ResolvedSchema(target, BaseOf.TryGetValue(target, out var b) ? b : doc);
    }

    /// <summary>Whether <paramref name="node"/> is the root of the schema resource at <paramref name="base"/>.</summary>
    public bool IsResource(JsObject node, string @base)
    {
        lock (_sync)
        {
            EnsureIndexed();
            return _resources.TryGetValue(@base, out var resource) && ReferenceEquals(resource, node);
        }
    }

    // Walks only keywords that hold subschemas: an $id inside enum, const or an unknown keyword names nothing.
    private void Index(object? value, string @base)
    {
        if (value is not JsObject node || BaseOf.ContainsKey(node)) return;
        var b = @base;
        // In draft 7 a $ref makes every sibling keyword, $id included, inert.
        var refOnly = Draft7 && node.GetOwn("$ref") is string;
        if (!refOnly && node.GetOwn("$id") is string id)
        {
            if (Draft7 && id.StartsWith('#')) _anchors[DocumentOf(b) + id] = node;
            else
            {
                var uri = ResolveUri(id, b);
                b = DocumentOf(uri);
                _resources[b] = node;
                if (uri.Length > b.Length + 1) _anchors[uri] = node;
            }
        }

        BaseOf[node] = b;
        if (refOnly) return;
        if (!Draft7)
        {
            if (node.GetOwn("$anchor") is string anchor) _anchors[$"{b}#{anchor}"] = node;
            if (node.GetOwn("$dynamicAnchor") is string dynamicAnchor)
            {
                _anchors[$"{b}#{dynamicAnchor}"] = node;
                DynamicAnchors[$"{b}#{dynamicAnchor}"] = node;
            }
        }

        foreach (var key in SchemaKeywords) Index(node.GetOwn(key), b);
        foreach (var key in MapKeywords)
        {
            if (node.GetOwn(key) is JsObject map) foreach (var k in map.Keys) Index(map.GetOwn(k), b);
        }

        foreach (var key in ArrayKeywords)
        {
            if (node.GetOwn(key) is JsArray list) foreach (var s in list) Index(s, b);
        }
    }

    private bool Load(string doc, out object? schema)
    {
        if (!_registry.TryGetValue(doc, out schema)) return false;
        _resources[doc] = schema;
        Index(schema, doc);
        return true;
    }

    private ResolvedSchema Pointer(object? root, string @base, string fragment)
    {
        var node = root;
        var b = @base;
        foreach (var raw in fragment.Split('/').Skip(1))
        {
            var key = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsObject o when o.HasOwn(key) => o.GetOwn(key),
                JsArray a when key == "length" => (double)a.Count,
                JsArray a when Js.IsArrayIndex(key, out var i) && i < a.Count => a[(int)i],
                _ => Js.Undefined,
            };
            if (node is JsUndefined) throw new JsonSchemaException($"Unresolved $ref #{fragment} in {@base}");
            if (node is JsObject child && BaseOf.TryGetValue(child, out var childBase)) b = childBase;
        }

        return new ResolvedSchema(node, b);
    }

    /// <summary><paramref name="ref"/> resolved against <paramref name="base"/>. URNs and other bases a URL parser rejects take fragment-only references.</summary>
    public static string ResolveUri(string @ref, string @base) =>
        JsUrl.Href(@ref, @base) ?? (@ref.StartsWith('#') ? DocumentOf(@base) + @ref : @ref);

    /// <summary><paramref name="uri"/> without its fragment.</summary>
    public static string DocumentOf(string uri)
    {
        var hash = uri.IndexOf('#');
        return hash < 0 ? uri : uri[..hash];
    }
}
