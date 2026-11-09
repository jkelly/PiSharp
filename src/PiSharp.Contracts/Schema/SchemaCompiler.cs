// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/compiler.ts, packages/schema/src/validator.ts
//
// The compiler: a schema to one check closure, built once. Keywords group by the type they constrain. Subschemas compile eagerly, so a
// malformed schema or a reference that names nothing fails the compile. The compile's caches are locked, because errors and coercion
// may compile further nodes (the explainer's annotation probes, a $dynamicRef target found at run time) while other threads check.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // the engine uses its own experimental types

using System.Runtime.CompilerServices;

namespace PiSharp.Contracts.Schema;

/// <summary>The options of one compile, on the JavaScript value model.</summary>
internal sealed class SchemaOptions
{
    public bool Draft7 { get; init; }
    public IReadOnlyDictionary<string, Func<string, bool>>? Formats { get; init; }
    public IReadOnlyList<KeyValuePair<string, object?>> Schemas { get; init; } = [];
    public bool Coerce { get; init; }
    public bool RejectUnresolvedRefs { get; init; }
}

/// <summary>One compile of a schema.</summary>
internal sealed class SchemaCompiler
{
    /// <summary>Keywords that never affect a check.</summary>
    public static readonly HashSet<string> NeutralKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$comment", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly", "$defs", "definitions",
        "contentEncoding", "contentMediaType", "minContains", "maxContains", "$vocabulary",
    };

    // The object keywords besides the core (properties, required, patternProperties, additionalProperties).
    private static readonly string[] ObjectExtras = ["propertyNames", "minProperties", "maxProperties", "dependentRequired", "dependentSchemas", "dependencies"];

    // The keywords an annotating check handles itself; every other keyword compiles into its plain part.
    private static readonly HashSet<string> AnnotatingKeywords = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "additionalProperties", "prefixItems", "items", "additionalItems", "contains", "minContains",
        "maxContains", "allOf", "anyOf", "oneOf", "if", "then", "else", "$ref", "$dynamicRef", "dependentSchemas", "unevaluatedProperties",
        "unevaluatedItems",
    };

    private readonly object? _schema;
    private readonly DynamicScope _scope = new();
    private readonly Dictionary<JsObject, SchemaCheck> _cache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsObject, AnnotatingCheck> _annotatingCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsObject, JsObject> _withoutUnevaluated = new(ReferenceEqualityComparer.Instance);

    private SchemaCompiler(object? schema, SchemaOptions options, bool dynamic)
    {
        _schema = schema;
        R = new SchemaResolver(schema, options, Sync);
        Draft7 = R.Draft7;
        Formats = options.Formats;
        Dynamic = dynamic;
    }

    /// <summary>The lock of this compile's caches, shared with its resolver.</summary>
    public object Sync { get; } = new();

    public SchemaResolver R { get; }

    public bool Draft7 { get; }

    public IReadOnlyDictionary<string, Func<string, bool>>? Formats { get; }

    /// <summary>Whether checks track the dynamic scope (the schema has a bookended <c>$dynamicRef</c>).</summary>
    public bool Dynamic { get; }

    /// <summary>
    /// The compile of <paramref name="schema"/>. Most schemas compile without tracking the dynamic scope; one with a bookended
    /// <c>$dynamicRef</c> compiles again with it.
    /// </summary>
    public static SchemaCompiler Create(object? schema, SchemaOptions options)
    {
        var plain = new SchemaCompiler(schema, options, false);
        try
        {
            plain.Root();
            return plain;
        }
        catch (RestartException)
        {
            var dynamic = new SchemaCompiler(schema, options, true);
            dynamic.Root();
            return dynamic;
        }
    }

    /// <summary>The check of the root schema.</summary>
    public SchemaCheck Root() => Check(_schema, R.RootBase);

    /// <summary>The check of a schema (cached by schema object; a recursive reference goes through an indirection).</summary>
    public SchemaCheck Check(object? node, string @base)
    {
        if (node is bool b) return b ? SchemaChecks.Always : SchemaChecks.Never;
        if (node is not JsObject o) throw Malformed(node);
        lock (Sync)
        {
            if (_cache.TryGetValue(o, out var cached)) return cached;
            RuntimeHelpers.EnsureSufficientExecutionStack();
            var slot = new StrongBox<SchemaCheck?>();
            _cache[o] = SchemaChecks.Indirect(slot);
            var check = Build(o, R.BaseFor(o, @base));
            slot.Value = check;
            _cache[o] = check;
            return check;
        }
    }

    private static JsonSchemaException Malformed(object? node) =>
        new($"A schema must be an object or a boolean, got {Js.Stringify(node, 0) ?? "undefined"}");

    private SchemaCheck Build(JsObject node, string @base)
    {
        if (Draft7 && node.GetOwn("$ref") is string draft7Ref) return Ref(draft7Ref, @base);
        if (!Draft7 && (Defined(node, "unevaluatedProperties") || Defined(node, "unevaluatedItems")))
        {
            return SchemaChecks.WithFreshAnnotations(Annotating(node, @base));
        }

        var check = Plain(node, @base);
        return Dynamic && R.IsResource(node, @base) ? SchemaChecks.Scoped(_scope, @base, check) : check;
    }

    /// <summary>Every keyword of <paramref name="node"/>, not tracking annotations.</summary>
    private SchemaCheck Plain(JsObject node, string @base)
    {
        var parts = new List<SchemaCheck>();
        var type = node.GetOwn("type");
        IReadOnlyList<object?>? types = type is JsUndefined ? null : type is JsArray list ? list : [type];
        if (types is { Count: 1 } && types[0] is "bytes")
        {
            parts.Add(SchemaChecks.BytesCheck(Number(node, "minLength") ?? 0, Number(node, "maxLength") ?? double.PositiveInfinity));
        }
        else
        {
            // The groups pass values of other types; the type check, when there is one, runs first.
            if (StringGroup(node) is { } stringGroup) parts.Add(stringGroup);
            if (NumberGroup(node) is { } numberGroup) parts.Add(numberGroup);
            if (ObjectGroup(node, @base) is { } objectGroup) parts.Add(objectGroup);
            if (ArrayGroup(node, @base) is { } arrayGroup) parts.Add(arrayGroup);
            if (types is not null) parts.Insert(0, SchemaChecks.TypeCheck(types));
        }

        InPlace(node, @base, parts);
        return SchemaChecks.And(parts);
    }

    // The keywords that apply to the value itself, whatever its type.
    private void InPlace(JsObject node, string @base, List<SchemaCheck> parts)
    {
        if (node.GetOwn("enum") is JsArray values) parts.Add(SchemaChecks.EnumCheck(values));
        if (node.HasOwn("const")) parts.Add(SchemaChecks.ConstCheck(node.GetOwn("const")));
        if (node.GetOwn("allOf") is JsArray allOf) parts.Add(SchemaChecks.And(allOf.Select(s => Check(s, @base)).ToList()));
        if (node.GetOwn("anyOf") is JsArray anyOf) parts.Add(SchemaChecks.AnyCheck(anyOf.Select(s => Check(s, @base)).ToList()));
        if (node.GetOwn("oneOf") is JsArray oneOf) parts.Add(SchemaChecks.OneCheck(oneOf.Select(s => Check(s, @base)).ToList()));
        if (Defined(node, "not")) parts.Add(SchemaChecks.NotCheck(Check(node.GetOwn("not"), @base)));
        if (Defined(node, "if") && (Defined(node, "then") || Defined(node, "else")))
        {
            var then = Defined(node, "then") ? Check(node.GetOwn("then"), @base) : SchemaChecks.Always;
            var otherwise = Defined(node, "else") ? Check(node.GetOwn("else"), @base) : SchemaChecks.Always;
            parts.Add(SchemaChecks.IfCheck(Check(node.GetOwn("if"), @base), then, otherwise));
        }

        if (node.GetOwn("$ref") is string @ref) parts.Add(Ref(@ref, @base));
        if (!Draft7 && node.GetOwn("$dynamicRef") is string dynamicRef) parts.Add(DynamicRef(dynamicRef, @base));
    }

    private SchemaCheck Ref(string @ref, string @base)
    {
        var target = R.Resolve(@ref, @base);
        var check = Check(target.Node, target.Base);
        return Dynamic && target.Base != @base ? SchemaChecks.Scoped(_scope, target.Base, check) : check;
    }

    private SchemaCheck DynamicRef(string @ref, string @base)
    {
        if (Bookended(@ref, @base) is not { } bookended) return Ref(@ref, @base);
        var (initial, name) = bookended;
        var fallback = Check(initial.Node, initial.Base);
        var lookup = DynamicLookup(name, Check);
        var scope = _scope;
        return v =>
        {
            var list = scope.Current;
            for (var i = 0; i < list.Count; i++)
            {
                if (lookup(list[i]) is { } check) return check(v);
            }

            return fallback(v);
        };
    }

    /// <summary>The initial target and anchor name of a <c>$dynamicRef</c> that resolves dynamically, or null for a plain reference.</summary>
    private (ResolvedSchema Initial, string Name)? Bookended(string @ref, string @base)
    {
        var initial = R.Resolve(@ref, @base);
        var uri = SchemaResolver.ResolveUri(@ref, @base);
        var document = SchemaResolver.DocumentOf(uri);
        var name = uri.Length > document.Length ? uri[(document.Length + 1)..] : "";
        var anchored = initial.Node is JsObject o && o.GetOwn("$dynamicAnchor") is string anchor && anchor == name;
        if (name.Length == 0 || name.StartsWith('/') || !anchored) return null;
        if (!Dynamic) throw new RestartException();
        return (initial, name);
    }

    // Resolved at run time against the dynamic scope, so a schema with a bookended $dynamicRef keeps its compiler.
    private Func<string, T?> DynamicLookup<T>(string name, Func<JsObject, string, T> compile) where T : class
    {
        var found = new Dictionary<string, T?>(StringComparer.Ordinal);
        return uri =>
        {
            lock (Sync)
            {
                var key = $"{uri}#{name}";
                if (found.TryGetValue(key, out var check)) return check;
                check = R.DynamicAnchors.TryGetValue(key, out var target)
                    ? compile(target, R.BaseOf.TryGetValue(target, out var targetBase) ? targetBase : uri)
                    : null;
                found[key] = check;
                return check;
            }
        };
    }

    private SchemaCheck? StringGroup(JsObject node)
    {
        var min = Number(node, "minLength") ?? 0;
        var max = Number(node, "maxLength") ?? double.PositiveInfinity;
        var re = node.GetOwn("pattern") is string pattern ? SchemaChecks.SharedRegex(pattern) : null;
        var format = FormatOf(node);
        if (min == 0 && double.IsPositiveInfinity(max) && re is null && format is null) return null;
        return SchemaChecks.StringCheck(min, max, re, format);
    }

    /// <summary>The check of the node's <c>format</c>, when the compile has formats and that one among them.</summary>
    public Func<string, bool>? FormatOf(JsObject node) =>
        Formats is not null && node.GetOwn("format") is string name && Formats.TryGetValue(name, out var check) ? check : null;

    private static SchemaCheck? NumberGroup(JsObject node)
    {
        var min = Number(node, "minimum");
        var max = Number(node, "maximum");
        var xmin = Number(node, "exclusiveMinimum");
        var xmax = Number(node, "exclusiveMaximum");
        var multipleOf = Number(node, "multipleOf");
        if (min is null && max is null && xmin is null && xmax is null && multipleOf is null) return null;
        return SchemaChecks.NumberCheck(min ?? double.NegativeInfinity, max ?? double.PositiveInfinity, xmin ?? double.NegativeInfinity,
            xmax ?? double.PositiveInfinity, multipleOf);
    }

    private SchemaCheck? ObjectGroup(JsObject node, string @base)
    {
        var onlyCore = ObjectExtras.All(k => !Defined(node, k));
        var core = ObjectCore(node, @base);
        if (core is not null && onlyCore) return core;
        var parts = new List<SchemaCheck>();
        if (core is not null) parts.Add(core);
        if (Defined(node, "propertyNames")) parts.Add(SchemaChecks.PropertyNamesCheck(Check(node.GetOwn("propertyNames"), @base)));
        var minProperties = Number(node, "minProperties");
        var maxProperties = Number(node, "maxProperties");
        if (minProperties is not null || maxProperties is not null)
        {
            parts.Add(SchemaChecks.PropertyCountCheck(minProperties ?? 0, maxProperties ?? double.PositiveInfinity));
        }

        var dependentRequired = new List<(string, IReadOnlyList<object?>)>();
        var dependentSchemas = new List<(string, SchemaCheck)>();
        if (!Draft7)
        {
            foreach (var (k, list) in Entries(node.GetOwn("dependentRequired"))) if (list is JsArray required) dependentRequired.Add((k, required));
            foreach (var (k, s) in Entries(node.GetOwn("dependentSchemas"))) dependentSchemas.Add((k, Check(s, @base)));
        }

        foreach (var (k, d) in Entries(node.GetOwn("dependencies")))
        {
            if (d is JsArray required) dependentRequired.Add((k, required));
            else dependentSchemas.Add((k, Check(d, @base)));
        }

        if (dependentRequired.Count > 0) parts.Add(SchemaChecks.DependentRequiredCheck(dependentRequired));
        if (dependentSchemas.Count > 0) parts.Add(SchemaChecks.DependentSchemasCheck(dependentSchemas));
        if (parts.Count == 0) return null;
        return SchemaChecks.GuardObject(SchemaChecks.And(parts));
    }

    private SchemaCheck? ObjectCore(JsObject node, string @base)
    {
        var properties = node.GetOwn("properties") as JsObject ?? new JsObject();
        var required = node.GetOwn("required") is JsArray list ? list.OfType<string>().ToList() : [];
        var patterns = node.GetOwn("patternProperties") as JsObject ?? new JsObject();
        var additional = node.GetOwn("additionalProperties");
        var keys = properties.Keys;
        var patternKeys = patterns.Keys;
        if (keys.Count + required.Count + patternKeys.Count == 0 && additional is JsUndefined or true) return null;
        var declared = new List<(string, SchemaCheck, bool)>();
        foreach (var k in keys) declared.Add((k, Check(properties.GetOwn(k), @base), required.Contains(k)));
        var extraRequired = required.Where(k => !properties.HasOwn(k)).ToList();
        var pats = patternKeys.Select(p => (SchemaChecks.SharedRegex(p), Check(patterns.GetOwn(p), @base))).ToList();
        var extra = additional is JsUndefined or true ? null : Check(additional, @base);
        return SchemaChecks.ObjectCore(declared, extraRequired, pats, extra);
    }

    private SchemaCheck? ArrayGroup(JsObject node, string @base)
    {
        IReadOnlyList<object?> prefix = [];
        object? rest;
        if (node.GetOwn("items") is JsArray items)
        {
            prefix = items;
            rest = node.GetOwn("additionalItems");
        }
        else
        {
            if (!Draft7 && node.GetOwn("prefixItems") is JsArray prefixItems) prefix = prefixItems;
            rest = node.GetOwn("items");
        }

        var prefixChecks = prefix.Select(s => Check(s, @base)).ToList();
        var restCheck = rest is JsUndefined or true ? null : Check(rest, @base);
        var minItems = Number(node, "minItems");
        var maxItems = Number(node, "maxItems");
        var parts = new List<SchemaCheck>();
        if (minItems is not null || maxItems is not null) parts.Add(SchemaChecks.LengthCheck(minItems ?? 0, maxItems ?? double.PositiveInfinity));
        if (prefixChecks.Count > 0 || restCheck is not null) parts.Add(SchemaChecks.ItemsCheck(prefixChecks, restCheck));
        if (Defined(node, "contains"))
        {
            var min = Draft7 ? 1 : Number(node, "minContains") ?? 1;
            var max = Draft7 ? double.PositiveInfinity : Number(node, "maxContains") ?? double.PositiveInfinity;
            parts.Add(SchemaChecks.ContainsCheck(Check(node.GetOwn("contains"), @base), min, max));
        }

        if (node.GetOwn("uniqueItems") is true) parts.Add(SchemaChecks.UniqueCheck);
        if (parts.Count == 0) return null;
        return SchemaChecks.GuardArray(SchemaChecks.And(parts));
    }

    /// <summary>The annotating check of a schema evaluated in place (only below <c>unevaluatedProperties</c>/<c>unevaluatedItems</c>).</summary>
    public AnnotatingCheck Annotating(object? node, string @base)
    {
        if (node is bool b) return b ? SchemaChecks.AnnotatingTrue : SchemaChecks.AnnotatingFalse;
        if (node is not JsObject o) throw Malformed(node);
        lock (Sync)
        {
            if (_annotatingCache.TryGetValue(o, out var cached)) return cached;
            RuntimeHelpers.EnsureSufficientExecutionStack();
            var slot = new StrongBox<AnnotatingCheck?>();
            _annotatingCache[o] = SchemaChecks.IndirectAnnotating(slot);
            var check = BuildAnnotating(o, R.BaseFor(o, @base));
            slot.Value = check;
            _annotatingCache[o] = check;
            return check;
        }
    }

    /// <summary><paramref name="node"/> without <c>unevaluatedProperties</c> and <c>unevaluatedItems</c> (one copy per node).</summary>
    public JsObject WithoutUnevaluated(JsObject node, string @base)
    {
        lock (Sync)
        {
            if (_withoutUnevaluated.TryGetValue(node, out var rest)) return rest;
            rest = new JsObject();
            foreach (var k in node.Keys)
            {
                if (k is not ("unevaluatedProperties" or "unevaluatedItems")) rest.Set(k, node.GetOwn(k));
            }

            // This is the same schema resource with fewer assertions. Preserve its resolved
            // base, rather than applying a relative $id again to this synthetic node.
            R.BaseOf[rest] = R.BaseFor(node, @base);

            return _withoutUnevaluated[node] = rest;
        }
    }

    private AnnotatingCheck BuildAnnotating(JsObject node, string @base)
    {
        var rest = new JsObject();
        var annotates = false;
        foreach (var k in node.Keys)
        {
            if (AnnotatingKeywords.Contains(k)) annotates = true;
            else rest.Set(k, node.GetOwn(k));
        }

        var plain = Plain(rest, @base);
        var parts = new List<AnnotatingCheck>();
        if (annotates) AnnotatingParts(node, @base, parts);
        var all = parts.Count == 0 ? SchemaChecks.IgnoreAnnotations(plain) : SchemaChecks.AnnotateAll(plain, parts);
        var own = Defined(node, "unevaluatedProperties") || Defined(node, "unevaluatedItems") ? SchemaChecks.IsolateAnnotations(all) : all;
        return Dynamic && R.IsResource(node, @base) ? SchemaChecks.ScopedAnnotating(_scope, @base, own) : own;
    }

    private void AnnotatingParts(JsObject node, string @base, List<AnnotatingCheck> parts)
    {
        if (Defined(node, "properties") || Defined(node, "patternProperties") || Defined(node, "additionalProperties"))
        {
            var props = Entries(node.GetOwn("properties")).Select(e => (e.Key, Check(e.Value, @base))).ToList();
            var pats = Entries(node.GetOwn("patternProperties")).Select(e => (SchemaChecks.SharedRegex(e.Key), Check(e.Value, @base))).ToList();
            var extra = Defined(node, "additionalProperties") ? Check(node.GetOwn("additionalProperties"), @base) : null;
            parts.Add(SchemaChecks.AnnotateObject(props, pats, extra));
        }

        if (Defined(node, "prefixItems") || Defined(node, "items") || Defined(node, "contains"))
        {
            IReadOnlyList<object?> prefix = node.GetOwn("prefixItems") as JsArray ?? node.GetOwn("items") as JsArray ?? new JsArray();
            var rest = node.GetOwn("items") is JsArray ? node.GetOwn("additionalItems") : node.GetOwn("items");
            parts.Add(SchemaChecks.AnnotateArray(
                prefix.Select(s => Check(s, @base)).ToList(),
                rest is JsUndefined ? null : Check(rest, @base),
                Defined(node, "contains") ? Check(node.GetOwn("contains"), @base) : null,
                Number(node, "minContains") ?? 1,
                Number(node, "maxContains") ?? double.PositiveInfinity));
        }

        if (node.GetOwn("allOf") is JsArray allOf) foreach (var s in allOf) parts.Add(Annotating(s, @base));
        if (node.GetOwn("anyOf") is JsArray anyOf) parts.Add(SchemaChecks.AnnotateAny(anyOf.Select(s => Annotating(s, @base)).ToList(), false));
        if (node.GetOwn("oneOf") is JsArray oneOf) parts.Add(SchemaChecks.AnnotateAny(oneOf.Select(s => Annotating(s, @base)).ToList(), true));
        if (Defined(node, "if"))
        {
            var then = Defined(node, "then") ? Annotating(node.GetOwn("then"), @base) : null;
            var otherwise = Defined(node, "else") ? Annotating(node.GetOwn("else"), @base) : null;
            parts.Add(SchemaChecks.AnnotateIf(Annotating(node.GetOwn("if"), @base), then, otherwise));
        }

        if (node.GetOwn("$ref") is string @ref) parts.Add(AnnotatingRef(@ref, @base));
        if (node.GetOwn("$dynamicRef") is string dynamicRef) parts.Add(AnnotatingDynamicRef(dynamicRef, @base));
        foreach (var (k, s) in Entries(node.GetOwn("dependentSchemas"))) parts.Add(SchemaChecks.AnnotateDependent(k, Annotating(s, @base)));
        if (Defined(node, "unevaluatedProperties")) parts.Add(SchemaChecks.UnevaluatedProperties(Check(node.GetOwn("unevaluatedProperties"), @base)));
        if (Defined(node, "unevaluatedItems")) parts.Add(SchemaChecks.UnevaluatedItems(Check(node.GetOwn("unevaluatedItems"), @base)));
    }

    private AnnotatingCheck AnnotatingRef(string @ref, string @base)
    {
        var target = R.Resolve(@ref, @base);
        var check = Annotating(target.Node, target.Base);
        return Dynamic && target.Base != @base ? SchemaChecks.ScopedAnnotating(_scope, target.Base, check) : check;
    }

    private AnnotatingCheck AnnotatingDynamicRef(string @ref, string @base)
    {
        if (Bookended(@ref, @base) is not { } bookended) return AnnotatingRef(@ref, @base);
        var (initial, name) = bookended;
        var fallback = Annotating(initial.Node, initial.Base);
        var lookup = DynamicLookup(name, Annotating);
        var scope = _scope;
        return (v, a) =>
        {
            var list = scope.Current;
            for (var i = 0; i < list.Count; i++)
            {
                if (lookup(list[i]) is { } check) return check(v, a);
            }

            return fallback(v, a);
        };
    }

    /// <summary><c>node[key] !== undefined</c>.</summary>
    public static bool Defined(JsObject node, string key) => node.GetOwn(key) is not JsUndefined;

    /// <summary>A keyword's value when it is a number.</summary>
    public static double? Number(JsObject node, string key) => node.GetOwn(key) is double d ? d : null;

    /// <summary><c>Object.entries(value)</c> of a schema object (not an array), else nothing.</summary>
    public static IEnumerable<KeyValuePair<string, object?>> Entries(object? value) =>
        value is JsObject o ? o.Keys.Select(k => new KeyValuePair<string, object?>(k, o.GetOwn(k))).ToList() : [];

    /// <summary>Thrown when a compile without dynamic scope tracking meets a bookended <c>$dynamicRef</c>: compile again with it.</summary>
    private sealed class RestartException : Exception;
}
