// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/src/explain.ts
//
// Errors of a failed value: a walk over the schema that reports each failing keyword at the value it failed on. It runs only for values
// that failed the check, using the compiler's checks for subschemas. One explainer serves one errors() call.
#nullable enable
#pragma warning disable PISHARP_SCHEMA001 // the engine uses its own experimental types

using System.Runtime.CompilerServices;

namespace PiSharp.Contracts.Schema;

internal sealed class SchemaExplainer(SchemaCompiler c, object? schema)
{
    // Enum errors name the allowed values; a long list (an enum of thousands) is cut, so the message stays readable.
    private const int ListedValues = 20;

    /// <summary>
    /// The errors of $ref targets for object values: a recursive union explains each branch to pick the one to blame, and every branch
    /// would explain the same children again, exponentially in the depth.
    /// </summary>
    private Dictionary<JsObject, Dictionary<object, (string Path, List<JsonSchemaValidationError> Errors)>>? _memo;

    /// <summary>The errors of <paramref name="value"/>, at least one (<paramref name="value"/> failed the check).</summary>
    public List<JsonSchemaValidationError> Errors(object? value)
    {
        var output = new List<JsonSchemaValidationError>();
        Walk(schema, c.R.RootBase, value, "", output);
        if (output.Count == 0) output.Add(new JsonSchemaValidationError("", "schema", "invalid"));
        return output;
    }

    private bool Ok(object? node, string @base, object? v) => c.Check(node, @base)(v);

    // For $dynamicRef schemas this check runs outside the original dynamic scope, so their errors are approximate.
    private void Walk(object? node, string @base, object? v, string path, List<JsonSchemaValidationError> output)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (node is true) return;
        if (node is false)
        {
            output.Add(new JsonSchemaValidationError(path, "false", "no value is allowed here"));
            return;
        }

        if (node is not JsObject n) return;
        var b = c.R.BaseFor(n, @base);
        if (Ok(n, b, v)) return;
        var start = output.Count;
        void Push(string keyword, string message, string? at = null) => output.Add(new JsonSchemaValidationError(at ?? path, keyword, message));
        if (c.Draft7 && n.GetOwn("$ref") is string draft7Ref)
        {
            WalkRef(draft7Ref, b, v, path, output);
            return;
        }

        var type = n.GetOwn("type");
        if (type is not JsUndefined)
        {
            IReadOnlyList<object?> types = type is JsArray list ? list : [type];
            var checks = types.Select(t => t is string name && SchemaChecks.TypeChecks.TryGetValue(name, out var check) ? check : SchemaChecks.Never);
            if (!checks.Any(check => check(v)))
            {
                Push("type", $"expected {SchemaChecks.JsJoin(types, " or ")}, got {TypeName(v)}");
                return;
            }
        }

        if (n.GetOwn("enum") is JsArray values && !SchemaChecks.EnumCheck(values)(v)) Push("enum", $"expected one of {ListValues(values)}");
        if (n.HasOwn("const") && !SchemaChecks.ConstCheck(n.GetOwn("const"))(v)) Push("const", $"expected {Js.Stringify(n.GetOwn("const"), 0) ?? "undefined"}");
        if (v is string s && type is not "bytes") WalkString(n, s, Push);
        if (v is byte[] bytes && type is "bytes") WalkBytes(n, bytes, Push);
        if (v is double d) WalkNumber(n, d, Push);
        if (v is JsObject o) WalkObject(n, b, o, path, output, Push);
        if (v is JsArray a) WalkArray(n, b, a, path, output, Push);
        if (n.GetOwn("allOf") is JsArray allOf) foreach (var sub in allOf) Walk(sub, b, v, path, output);
        if (n.GetOwn("anyOf") is JsArray anyOf) WalkUnion(anyOf, b, v, path, output, false);
        if (n.GetOwn("oneOf") is JsArray oneOf) WalkUnion(oneOf, b, v, path, output, true);
        if (SchemaCompiler.Defined(n, "not") && Ok(n.GetOwn("not"), b, v)) Push("not", "must not match the schema in not");
        if (SchemaCompiler.Defined(n, "if"))
        {
            var branch = Ok(n.GetOwn("if"), b, v) ? n.GetOwn("then") : n.GetOwn("else");
            if (branch is not JsUndefined) Walk(branch, b, v, path, output);
        }

        if (n.GetOwn("$ref") is string @ref) WalkRef(@ref, b, v, path, output);
        if (output.Count == start && (SchemaCompiler.Defined(n, "unevaluatedProperties") || SchemaCompiler.Defined(n, "unevaluatedItems")))
        {
            WalkUnevaluated(n, b, v, path, output);
        }

        if (output.Count == start && n.GetOwn("$dynamicRef") is string) Push("$dynamicRef", "does not match the dynamically referenced schema");
    }

    private void WalkRef(string @ref, string @base, object? v, string path, List<JsonSchemaValidationError> output)
    {
        var target = c.R.Resolve(@ref, @base);
        if (target.Node is false && c.R.IsUnresolved(@ref, @base))
        {
            output.Add(new JsonSchemaValidationError(path, "$ref", $"unresolved reference {@ref}"));
            return;
        }

        if (target.Node is not JsObject node || v is not (JsObject or JsArray or byte[]))
        {
            Walk(target.Node, target.Base, v, path, output);
            return;
        }

        _memo ??= new Dictionary<JsObject, Dictionary<object, (string, List<JsonSchemaValidationError>)>>(ReferenceEqualityComparer.Instance);
        _memo.TryGetValue(node, out var known);
        if (known is not null && known.TryGetValue(v, out var hit) && hit.Path == path)
        {
            output.AddRange(hit.Errors);
            return;
        }

        var errors = new List<JsonSchemaValidationError>();
        Walk(node, target.Base, v, path, errors);
        if (known is null) _memo[node] = known = new Dictionary<object, (string, List<JsonSchemaValidationError>)>(ReferenceEqualityComparer.Instance);
        known[v] = (path, errors);
        output.AddRange(errors);
    }

    private void WalkString(JsObject node, string v, Action<string, string, string?> push)
    {
        var n = SchemaChecks.CodePoints(v);
        if (SchemaCompiler.Number(node, "minLength") is { } minLength && n < minLength) push("minLength", $"expected at least {Num(minLength)} characters", null);
        if (SchemaCompiler.Number(node, "maxLength") is { } maxLength && n > maxLength) push("maxLength", $"expected at most {Num(maxLength)} characters", null);
        if (node.GetOwn("pattern") is string pattern && !SchemaChecks.SharedRegex(pattern).IsMatch(v)) push("pattern", $"expected to match {pattern}", null);
        if (c.FormatOf(node) is { } format && !format(v)) push("format", $"expected format {node.GetOwn("format")}", null);
    }

    private static void WalkBytes(JsObject node, byte[] v, Action<string, string, string?> push)
    {
        if (SchemaCompiler.Number(node, "minLength") is { } minLength && v.Length < minLength) push("minLength", $"expected at least {Num(minLength)} bytes", null);
        if (SchemaCompiler.Number(node, "maxLength") is { } maxLength && v.Length > maxLength) push("maxLength", $"expected at most {Num(maxLength)} bytes", null);
    }

    private static void WalkNumber(JsObject node, double v, Action<string, string, string?> push)
    {
        if (SchemaCompiler.Number(node, "minimum") is { } minimum && v < minimum) push("minimum", $"expected >= {Num(minimum)}", null);
        if (SchemaCompiler.Number(node, "maximum") is { } maximum && v > maximum) push("maximum", $"expected <= {Num(maximum)}", null);
        if (SchemaCompiler.Number(node, "exclusiveMinimum") is { } xmin && v <= xmin) push("exclusiveMinimum", $"expected > {Num(xmin)}", null);
        if (SchemaCompiler.Number(node, "exclusiveMaximum") is { } xmax && v >= xmax) push("exclusiveMaximum", $"expected < {Num(xmax)}", null);
        if (SchemaCompiler.Number(node, "multipleOf") is { } multipleOf && !SchemaChecks.IsMultiple(v, multipleOf)) push("multipleOf", $"expected a multiple of {Num(multipleOf)}", null);
    }

    private void WalkObject(JsObject node, string b, JsObject v, string path, List<JsonSchemaValidationError> output, Action<string, string, string?> push)
    {
        var properties = node.GetOwn("properties") as JsObject ?? new JsObject();
        if (node.GetOwn("required") is JsArray required)
        {
            foreach (var k in required)
            {
                if (k is string key && v.GetOwn(key) is JsUndefined) push("required", "missing", Pointer(path, key));
            }
        }

        foreach (var k in properties.Keys)
        {
            if (v.GetOwn(k) is not JsUndefined) Walk(properties.GetOwn(k), b, v.GetOwn(k), Pointer(path, k), output);
        }

        var patterns = SchemaCompiler.Entries(node.GetOwn("patternProperties")).ToList();
        var additional = node.GetOwn("additionalProperties");
        var propertyNames = node.GetOwn("propertyNames");
        foreach (var k in v.Keys)
        {
            var matched = properties.HasOwn(k);
            foreach (var (p, s) in patterns)
            {
                if (!SchemaChecks.SharedRegex(p).IsMatch(k)) continue;
                matched = true;
                Walk(s, b, v.GetOwn(k), Pointer(path, k), output);
            }

            if (!matched && additional is not JsUndefined)
            {
                if (additional is false) push("additionalProperties", "unknown field", Pointer(path, k));
                else Walk(additional, b, v.GetOwn(k), Pointer(path, k), output);
            }

            if (propertyNames is not JsUndefined && !Ok(propertyNames, b, k)) push("propertyNames", "invalid property name", Pointer(path, k));
        }

        var count = v.Count;
        if (SchemaCompiler.Number(node, "minProperties") is { } minProperties && count < minProperties) push("minProperties", $"expected at least {Num(minProperties)} properties", null);
        if (SchemaCompiler.Number(node, "maxProperties") is { } maxProperties && count > maxProperties) push("maxProperties", $"expected at most {Num(maxProperties)} properties", null);
        // { ...dependentRequired, ...dependencies }: a key in both takes the value of dependencies at the position of dependentRequired.
        var dependencies = new JsObject();
        if (!c.Draft7) foreach (var (k, d) in SchemaCompiler.Entries(node.GetOwn("dependentRequired"))) dependencies.Set(k, d);
        foreach (var (k, d) in SchemaCompiler.Entries(node.GetOwn("dependencies"))) dependencies.Set(k, d);
        foreach (var k in dependencies.Keys)
        {
            if (!v.HasOwn(k)) continue;
            var d = dependencies.GetOwn(k);
            if (d is not JsArray list) Walk(d, b, v, path, output);
            else
            {
                foreach (var r in list)
                {
                    if (!SchemaChecks.HasOwnKey(v, r)) push("dependentRequired", $"required when {k} is present", Pointer(path, SchemaChecks.JsString(r)));
                }
            }
        }

        if (!c.Draft7 && node.GetOwn("dependentSchemas") is JsObject dependentSchemas)
        {
            foreach (var k in dependentSchemas.Keys) if (v.HasOwn(k)) Walk(dependentSchemas.GetOwn(k), b, v, path, output);
        }
    }

    private void WalkArray(JsObject node, string b, JsArray v, string path, List<JsonSchemaValidationError> output, Action<string, string, string?> push)
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
            if (!c.Draft7 && node.GetOwn("prefixItems") is JsArray prefixItems) prefix = prefixItems;
            rest = node.GetOwn("items");
        }

        for (var i = 0; i < v.Count; i++)
        {
            var s = i < prefix.Count ? prefix[i] : rest;
            if (s is not JsUndefined) Walk(s, b, v[i], $"{path}/{i}", output);
        }

        if (SchemaCompiler.Number(node, "minItems") is { } minItems && v.Count < minItems) push("minItems", $"expected at least {Num(minItems)} items", null);
        if (SchemaCompiler.Number(node, "maxItems") is { } maxItems && v.Count > maxItems) push("maxItems", $"expected at most {Num(maxItems)} items", null);
        if (node.GetOwn("uniqueItems") is true && !SchemaChecks.UniqueCheck(v)) push("uniqueItems", "expected unique items", null);
        if (SchemaCompiler.Defined(node, "contains"))
        {
            var min = c.Draft7 || SchemaCompiler.Number(node, "minContains") is not { } minContains ? 1 : minContains;
            var max = c.Draft7 || SchemaCompiler.Number(node, "maxContains") is not { } maxContains ? double.PositiveInfinity : maxContains;
            if (!SchemaChecks.ContainsCheck(c.Check(node.GetOwn("contains"), b), min, max)(v)) push("contains", ContainsMessage(min, max), null);
        }
    }

    private void WalkUnion(JsArray branches, string b, object? v, string path, List<JsonSchemaValidationError> output, bool exactlyOne)
    {
        var matches = branches.Count(s => Ok(s, b, v));
        if (matches == 1 || (!exactlyOne && matches > 0)) return;
        var keyword = exactlyOne ? "oneOf" : "anyOf";
        if (matches > 1)
        {
            output.Add(new JsonSchemaValidationError(path, keyword, "matches more than one alternative"));
            return;
        }

        // A tagged union reports the branch its tag selects; otherwise the branch whose first error is deepest.
        var tag = TagOf(branches, b);
        if (tag is { } t && v is JsObject o)
        {
            var value = Js.Get(o, t.Key);
            var i = t.Values.FindIndex(x => Js.StrictEquals(x, value));
            if (i < 0) output.Add(new JsonSchemaValidationError(Pointer(path, t.Key), keyword, $"expected one of {ListValues(t.Values)}"));
            else Walk(branches[i], b, v, path, output);
            return;
        }

        // An object is blamed on the branch that declares most of its properties (the one it was most likely meant for), then on the
        // branch whose first error is deepest.
        List<JsonSchemaValidationError>? bestErrors = null;
        var bestDeclared = 0;
        foreach (var s in branches)
        {
            var errors = new List<JsonSchemaValidationError>();
            Walk(s, b, v, path, errors);
            if (errors.Count == 0) continue;
            var declared = v is JsObject value ? DeclaredKeys(s, b, value) : 0;
            var better = bestErrors is null || declared > bestDeclared || (declared == bestDeclared && errors[0].Path.Length > bestErrors[0].Path.Length);
            if (!better) continue;
            bestErrors = errors;
            bestDeclared = declared;
        }

        output.AddRange(bestErrors ?? [new JsonSchemaValidationError(path, keyword, "matches no alternative")]);
    }

    /// <summary>How many of the keys of <paramref name="v"/> the branch <paramref name="s"/> (through a <c>$ref</c>) declares in <c>properties</c>.</summary>
    private int DeclaredKeys(object? s, string b, JsObject v)
    {
        var node = s is JsObject o && o.GetOwn("$ref") is string @ref ? c.R.Resolve(@ref, b).Node : s;
        var properties = node is JsObject n && n.GetOwn("properties") is JsObject p ? p : new JsObject();
        return v.Keys.Count(properties.HasOwn);
    }

    private (string Key, List<object?> Values)? TagOf(JsArray branches, string b)
    {
        var resolved = branches.Select(s => s is JsObject o && o.GetOwn("$ref") is string @ref ? c.R.Resolve(@ref, b).Node : s).ToList();
        if (resolved.Count == 0 || resolved[0] is not JsObject first || first.GetOwn("properties") is not JsObject firstProperties) return null;
        foreach (var key in firstProperties.Keys)
        {
            var values = resolved.Select(s =>
            {
                var tag = s is JsObject o && o.GetOwn("properties") is JsObject properties ? properties.GetOwn(key) : Js.Undefined;
                return tag is JsObject t ? t.GetOwn("const") : Js.Undefined;
            }).ToList();
            var primitive = values.All(x => x is not (JsUndefined or JsObject or JsArray or byte[]));
            if (primitive && Distinct(values)) return (key, values);
        }

        return null;
    }

    // new Set(values).size === values.length: SameValueZero over primitives.
    private static bool Distinct(List<object?> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (Js.StrictEquals(values[i], values[j]) || (values[i] is double x && values[j] is double y && double.IsNaN(x) && double.IsNaN(y))) return false;
            }
        }

        return true;
    }

    private void WalkUnevaluated(JsObject node, string b, object? v, string path, List<JsonSchemaValidationError> output)
    {
        var unevaluatedProperties = node.GetOwn("unevaluatedProperties");
        var unevaluatedItems = node.GetOwn("unevaluatedItems");
        var a = new SchemaAnnotations();
        c.Annotating(c.WithoutUnevaluated(node), b)(v, a);
        if (v is JsObject o && unevaluatedProperties is not JsUndefined && !a.AllProps)
        {
            foreach (var k in o.Keys)
            {
                if (a.Props?.Contains(k) ?? false) continue;
                if (unevaluatedProperties is false) output.Add(new JsonSchemaValidationError(Pointer(path, k), "unevaluatedProperties", "unknown field"));
                else Walk(unevaluatedProperties, b, o.GetOwn(k), Pointer(path, k), output);
            }
        }

        if (v is JsArray list && unevaluatedItems is not JsUndefined && !a.AllItems)
        {
            for (var i = a.Items; i < list.Count; i++)
            {
                if (a.Indexes?.Contains(i) ?? false) continue;
                if (unevaluatedItems is false) output.Add(new JsonSchemaValidationError($"{path}/{i}", "unevaluatedItems", "unexpected item"));
                else Walk(unevaluatedItems, b, list[i], $"{path}/{i}", output);
            }
        }
    }

    private static string Num(double d) => Js.NumberToString(d);

    /// <summary><paramref name="values"/> as JSON, at most 20 of them, then how many more there are.</summary>
    private static string ListValues(IReadOnlyList<object?> values)
    {
        if (values.Count <= ListedValues) return Js.Stringify(new JsArray(values), 0)!;
        var listed = Js.Stringify(new JsArray(values.Take(ListedValues)), 0)!;
        return $"{listed[..^1]}, ... and {values.Count - ListedValues} more]";
    }

    private static string ContainsMessage(double min, double max)
    {
        if (double.IsPositiveInfinity(max)) return $"expected at least {Num(min)} matching {(min == 1 ? "item" : "items")}";
        if (min == 0) return $"expected at most {Num(max)} matching {(max == 1 ? "item" : "items")}";
        return $"expected between {Num(min)} and {Num(max)} matching items";
    }

    private static string Pointer(string path, string key) => $"{path}/{key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    private static string TypeName(object? v) => v switch
    {
        null => "null",
        JsArray => "array",
        byte[] => "bytes",
        double d when Js.IsInteger(d) => "integer",
        double => "number",
        string => "string",
        bool => "boolean",
        JsUndefined => "undefined",
        JsFunction => "function",
        _ => "object",
    };
}
