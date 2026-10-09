// grok-mermaid 0.2.3 (Apache-2.0): ECMAScript string semantics shared by src/*.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
namespace PiSharp.Cli.Interactive.Mode.Mermaid;

/// <summary>
/// The JavaScript string operations the original relies on, with their exact semantics: <c>\s</c>, <c>trim</c> and
/// <c>split(/\s+/)</c> use ECMAScript WhiteSpace + LineTerminator (not <see cref="char.IsWhiteSpace(char)"/>), and
/// <c>[...s]</c> / <c>for (const c of s)</c> iterate code points, leaving lone surrogates as single elements.
/// </summary>
internal static class MermaidJs
{
    public static bool IsSpace(char c) => c switch
    {
        (char)0x09 or (char)0x0a or (char)0x0b or (char)0x0c or (char)0x0d or (char)0x20 or (char)0xa0 or (char)0x1680 => true,
        (char)0x2028 or (char)0x2029 or (char)0x202f or (char)0x205f or (char)0x3000 or (char)0xfeff => true,
        _ => c is >= (char)0x2000 and <= (char)0x200a,
    };

    /// <summary><c>/\s/.test(s)</c>.</summary>
    public static bool HasSpace(string s)
    {
        foreach (var c in s) if (IsSpace(c)) return true;
        return false;
    }

    public static string Trim(string s) => TrimStart(TrimEnd(s));

    public static string TrimStart(string s)
    {
        var i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;
        return i == 0 ? s : s[i..];
    }

    /// <summary><c>trimEnd()</c>, and equally <c>replace(/\s+$/, '')</c>.</summary>
    public static string TrimEnd(string s)
    {
        var e = s.Length;
        while (e > 0 && IsSpace(s[e - 1])) e--;
        return e == s.Length ? s : s[..e];
    }

    /// <summary><c>s.split(/\s+/).filter((w) =&gt; w !== '')</c>.</summary>
    public static List<string> Words(string s)
    {
        var words = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && IsSpace(s[i])) i++;
            var start = i;
            while (i < s.Length && !IsSpace(s[i])) i++;
            if (i > start) words.Add(s[start..i]);
        }
        return words;
    }

    /// <summary><c>[...s]</c>: one element per code point; a lone surrogate is its own element.</summary>
    public static string[] CodePoints(string s)
    {
        var list = new List<string>(s.Length);
        for (var i = 0; i < s.Length;)
        {
            var n = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            list.Add(s.Substring(i, n));
            i += n;
        }
        return [.. list];
    }

    /// <summary><c>s.codePointAt(i)</c> for an index known to be in range.</summary>
    public static int CodePointAt(string s, int i) =>
        char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];

    /// <summary><c>arr[i]</c> on a code point array, <c>null</c> standing in for <c>undefined</c>.</summary>
    public static string? At(string[] arr, int i) => i >= 0 && i < arr.Length ? arr[i] : null;

    /// <summary><c>arr.slice(start, end).join('')</c> with JS clamping.</summary>
    public static string Join(string[] arr, int start, int end = int.MaxValue)
    {
        start = Math.Clamp(start, 0, arr.Length);
        end = Math.Clamp(end, start, arr.Length);
        return string.Concat(arr.AsSpan(start, end - start));
    }

    /// <summary><c>Math.round</c>: ties toward +Infinity.</summary>
    public static double Round(double x)
    {
        var f = Math.Floor(x);
        return x - f >= 0.5 ? f + 1 : f;
    }

    /// <summary><c>Math.floor(n / 2)</c> for an integer.</summary>
    public static int Half(int n) => n >> 1;

    /// <summary><c>Math.ceil(n / 2)</c> for an integer.</summary>
    public static int CeilHalf(int n) => -(-n >> 1);

    /// <summary>Saturating subtraction; Rust's <c>usize</c> arithmetic never goes negative.</summary>
    public static int Sat(int a, int b) => Math.Max(0, a - b);

    /// <summary>A stable sort, as <c>Array.prototype.sort</c> is.</summary>
    public static void StableSort<T>(List<T> list, Comparison<T> compare)
    {
        var keyed = list.Select((item, i) => (item, i)).ToArray();
        Array.Sort(keyed, (a, b) =>
        {
            var c = compare(a.item, b.item);
            return c != 0 ? c : a.i.CompareTo(b.i);
        });
        for (var i = 0; i < keyed.Length; i++) list[i] = keyed[i].item;
    }
}
