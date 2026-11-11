// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/data/principal.ts
//
// Principals: the app's caller identities, and the keys Chord derives from them. A principal is plain JSON with a "kind", a JsonObject
// here; Chord never persists it. Its keys are cached per principal object and, for a custom key, per function too, in weak tables (as
// upstream's WeakMaps), so a live principal never retains a dropped function and a dropped principal is never retained.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.Chord.Data;

/// <summary>An app's own principal key (upstream <c>PrincipalKeyFunction</c>); it must be the same function on every host: a different
/// one silently splits identities.</summary>
public delegate string PrincipalKeyFunction(JsonObject principal);

public static class Principals
{
    /// <summary>Canonical principal equality, independent of a host's custom identity function.</summary>
    public static bool Same(JsonObject? principal, JsonObject other, ChordContext context) =>
        principal is not null && DataInternal.PrincipalKey(principal, context) == DataInternal.PrincipalKey(other, context);
}

internal static partial class DataInternal
{
    private static readonly ConditionalWeakTable<JsonObject, string> DefaultKeys = new();

    private static readonly ConditionalWeakTable<PrincipalKeyFunction, ConditionalWeakTable<JsonObject, string>> CustomKeys = new();

    /// <summary>The key of a principal: its canonical JSON, or <paramref name="customKey"/>'s (upstream <c>principalKey</c>). Cached per
    /// principal object; a custom key's cache is keyed by the function too.</summary>
    internal static string PrincipalKey(JsonObject principal, ChordContext context, PrincipalKeyFunction? customKey = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var cache = customKey is null ? DefaultKeys : CustomKeys.GetValue(customKey, _ => new ConditionalWeakTable<JsonObject, string>());
        if (!cache.TryGetValue(principal, out var key))
        {
            key = customKey is null ? CanonicalJson(principal, context) : customKey(principal);
            cache.AddOrUpdate(principal, key);
        }
        return key;
    }

    /// <summary>Canonical JSON: sorted object keys (UTF-16 order, as JavaScript's sort), <c>-0</c> written as <c>0</c>. Counts one walk
    /// (upstream <c>canonicalJson</c>).</summary>
    internal static string CanonicalJson(JsonNode? value, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Count("chord.data.walks");
        var text = new StringBuilder();
        Canonical(text, value);
        return text.ToString();
    }

    private static void Canonical(StringBuilder text, JsonNode? value)
    {
        if (ChordJson.AsObject(value) is { } node)
        {
            text.Append('{');
            var first = true;
            foreach (var key in node.Select(entry => entry.Key).Order(StringComparer.Ordinal))
            {
                if (!first) text.Append(',');
                first = false;
                JsonUtf16.Quote(text, key);
                text.Append(':');
                Canonical(text, node[key]);
            }
            text.Append('}');
            return;
        }
        if (ChordJson.AsArray(value) is { } array)
        {
            text.Append('[');
            for (var index = 0; index < array.Count; index++)
            {
                if (index > 0) text.Append(',');
                Canonical(text, array[index]);
            }
            text.Append(']');
            return;
        }
        // JSON.stringify writes -0 as 0.
        ChordJson.Stringify(text, value);
    }
}
