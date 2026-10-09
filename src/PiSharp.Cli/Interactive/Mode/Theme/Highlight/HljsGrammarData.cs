// highlight.js 10.7.3 (BSD-3-Clause): lib/languages/*.js (grammar data, see tools/HljsGrammars/extract-grammars.mjs) — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
using System.IO.Compression;
using System.Text.Json;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>JS <c>undefined</c> stored explicitly (hljs <c>inherit</c> copies keys whose value is undefined).</summary>
internal sealed class HljsUndefined
{
    public static readonly HljsUndefined Value = new();
    HljsUndefined() { }
}

/// <summary>A RegExp value of a grammar; hljs only ever uses its <c>source</c>.</summary>
internal sealed record HljsRegexLiteral(string Source);

/// <summary>A grammar function (callback or compiler extension), identified by the extractor.</summary>
internal sealed record HljsFunctionRef(string Id);

/// <summary>
/// A mutable JS plain object (mode, keyword table, ...). hljs' compiler mutates the grammar objects in place and clones
/// them with <c>inherit</c>, so compilation runs on these property bags with JS semantics (key order, undefined vs
/// absent, frozen objects ignoring writes).
/// </summary>
internal sealed class HljsObject
{
    readonly List<string> keys = [];
    readonly List<object?> values = [];

    public bool Frozen { get; private set; }

    /// <summary>JS <c>Object.freeze</c> (only applied to hljs' shared MODES objects).</summary>
    public void Freeze() => Frozen = true;

    public IReadOnlyList<string> Keys => keys;

    /// <summary>Gets a property (absent → <see cref="HljsUndefined.Value"/>); sets it (ignored on frozen objects).</summary>
    public object? this[string key]
    {
        get
        {
            var i = keys.IndexOf(key);
            return i < 0 ? HljsUndefined.Value : values[i];
        }
        set
        {
            if (Frozen) return;
            var i = keys.IndexOf(key);
            if (i >= 0) values[i] = value;
            else { keys.Add(key); values.Add(value); }
        }
    }

    public void Delete(string key)
    {
        if (Frozen) return;
        var i = keys.IndexOf(key);
        if (i < 0) return;
        keys.RemoveAt(i);
        values.RemoveAt(i);
    }

    /// <summary>JS <c>for (const key in this) target[key] = this[key]</c>.</summary>
    public void CopyTo(HljsObject target)
    {
        for (var i = 0; i < keys.Count; i++) target[keys[i]] = values[i];
    }
}

/// <summary>JS value semantics used by the ported compiler.</summary>
internal static class HljsJs
{
    public static bool IsUndefined(object? v) => v is HljsUndefined;

    public static bool Truthy(object? v) => v switch
    {
        null or HljsUndefined => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>hljs <c>source(re)</c>: falsy → null, string → itself, RegExp → source, anything else → undefined (null).</summary>
    public static string? Source(object? v) => !Truthy(v) ? null : v switch
    {
        string s => s,
        HljsRegexLiteral r => r.Source,
        _ => null,
    };

    /// <summary>JS <c>Number(value)</c> for the values grammars use.</summary>
    public static double ToNumber(object? v) => v switch
    {
        null => 0,
        HljsUndefined => double.NaN,
        double d => d,
        bool b => b ? 1 : 0,
        string s => StringToNumber(s),
        _ => double.NaN,
    };

    static double StringToNumber(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return 0;
        if (t.Length > 2 && t[0] == '0' && (t[1] | 0x20) is 'x' or 'o' or 'b')
        {
            var radix = (t[1] | 0x20) switch { 'x' => 16, 'o' => 8, _ => 2 };
            double r = 0;
            foreach (var ch in t.AsSpan(2))
            {
                var d = char.IsAsciiDigit(ch) ? ch - '0' : char.IsAsciiLetter(ch) ? (ch | 0x20) - 'a' + 10 : 99;
                if (d >= radix) return double.NaN;
                r = r * radix + d;
            }
            return r;
        }
        if (t is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (t == "-Infinity") return double.NegativeInfinity;
        foreach (var ch in t) if (!(char.IsAsciiDigit(ch) || ch is '.' or 'e' or 'E' or '+' or '-')) return double.NaN;
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r2)
            ? r2 : double.NaN;
    }
}

/// <summary>The extracted grammar resource: one immutable template per language, instantiated per registration.</summary>
internal sealed class HljsGrammarData
{
    public const string ResourceName = "PiSharp.Cli.Hljs.grammars.json.gz";

    static readonly Lazy<HljsGrammarData> LazyDefault = new(() => Load(OpenResource()), LazyThreadSafetyMode.ExecutionAndPublication);
    public static HljsGrammarData Default => LazyDefault.Value;

    sealed record Ref(int Id);
    sealed record Template(bool Frozen, (string Key, object? Value)[] Props);

    readonly Dictionary<string, Template[]> templates = new(StringComparer.Ordinal);

    public IReadOnlyList<string> EagerOrder { get; private set; } = [];
    public IReadOnlyList<string> IndexOrder { get; private set; } = [];
    public HashSet<string> MathematicaSystemSymbols { get; } = new(StringComparer.Ordinal);

    static Stream OpenResource() =>
        typeof(HljsGrammarData).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

    public static HljsGrammarData Load(Stream gzipStream)
    {
        using var gz = new GZipStream(gzipStream, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
        var root = doc.RootElement;
        var data = new HljsGrammarData
        {
            EagerOrder = [.. root.GetProperty("eager").EnumerateArray().Select(e => e.GetString()!)],
            IndexOrder = [.. root.GetProperty("order").EnumerateArray().Select(e => e.GetString()!)],
        };
        var extra = root.GetProperty("data");
        foreach (var s in extra.GetProperty("mathematica.SYSTEM_SYMBOLS").EnumerateArray()) data.MathematicaSystemSymbols.Add(s.GetString()!);
        HljsRegex.SetCanonicalizeTable([.. extra.GetProperty("canonicalize").EnumerateArray().Select(e => e.GetInt32())]);
        foreach (var lang in root.GetProperty("languages").EnumerateObject())
        {
            var objs = new List<Template>();
            foreach (var o in lang.Value.EnumerateArray())
            {
                var frozen = false;
                var props = new List<(string, object?)>();
                foreach (var p in o.EnumerateObject())
                {
                    if (p.Name == "#f") { frozen = true; continue; }
                    props.Add((p.Name, Decode(p.Value)));
                }
                objs.Add(new Template(frozen, [.. props]));
            }
            data.templates[lang.Name] = [.. objs];
        }
        return data;
    }

    static object? Decode(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Array => e.EnumerateArray().Select(Decode).ToArray(),
        JsonValueKind.Object when e.TryGetProperty("o", out var o) => new Ref(o.GetInt32()),
        JsonValueKind.Object when e.TryGetProperty("r", out var r) => new HljsRegexLiteral(r.GetString()!),
        JsonValueKind.Object when e.TryGetProperty("c", out var c) => new HljsFunctionRef(c.GetString()!),
        JsonValueKind.Object when e.TryGetProperty("u", out _) => HljsUndefined.Value,
        _ => throw new InvalidDataException("unexpected grammar value " + e.GetRawText()),
    };

    /// <summary>Runs the language definition: a fresh, uncompiled object graph (JS: <c>languageDefinition(hljs)</c>).</summary>
    public HljsObject Instantiate(string name)
    {
        var t = templates[name];
        var objs = new HljsObject[t.Length];
        for (var i = 0; i < t.Length; i++) objs[i] = new HljsObject();
        for (var i = 0; i < t.Length; i++)
            foreach (var (k, v) in t[i].Props) objs[i][k] = Convert(v);
        for (var i = 0; i < t.Length; i++)
            if (t[i].Frozen) objs[i].Freeze();
        return objs[0];

        object? Convert(object? v) => v switch
        {
            Ref r => objs[r.Id],
            object?[] a => a.Select(Convert).ToList(),
            _ => v,
        };
    }
}
