// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/text.ts
//
// Text in the store (decision "Text is plain UTF-8, in code point order"): Chord compares strings by code point, as SQLite's BINARY
// collation compares their UTF-8 bytes, never by .NET's (and JavaScript's) UTF-16 code units; every stored string is well-formed and free
// of NUL, and a prefix becomes a range by its upper bound. A .NET string is UTF-16 like a JavaScript one, so the functions below are
// upstream's, unit for unit.
using PiSharp.Chord.Data;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

/// <summary>The store's functions for apps (upstream <c>store/index.ts</c>'s value exports): each file of the layer adds its own.</summary>
public static partial class Tables
{
    /// <summary>Compares <paramref name="a"/> and <paramref name="b"/> by code point: negative, zero or positive (upstream
    /// <c>compareText</c>).</summary>
    public static int CompareText(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var length = Math.Min(a.Length, b.Length);
        for (var index = 0; index < length; index++)
        {
            var x = a[index];
            var y = b[index];
            if (x != y) return CodePointRank(x) - CodePointRank(y);
        }
        return a.Length - b.Length;
    }

    /// <summary>A UTF-16 code unit's rank in code point order: a surrogate starts a code point above U+FFFF, so it ranks above every unit
    /// from U+E000 on.</summary>
    private static int CodePointRank(char unit)
    {
        if (unit < 0xd800) return unit;
        return unit < 0xe000 ? unit + 0x2000 : unit - 0x800;
    }
}

/// <summary>The store's functions for Chord's other files (the store layer has no <c>internal.ts</c>: its modules import each other's
/// functions, which are the members of this class, each file adding its own).</summary>
internal static partial class StoreInternal
{
    /// <summary><paramref name="text"/> as the store keeps it: a lone surrogate replaced by U+FFFD (copied only when one occurs), NUL
    /// refused <c>invalid/args</c> (<paramref name="what"/> names the value). Plugin strings are a trust boundary (upstream
    /// <c>storedText</c>).</summary>
    internal static string StoredText(string text, string what)
    {
        if (text.Contains('\0')) throw DataInternal.Invalid("args", $"{what}: text may not contain NUL");
        return JsonUtf16.IsWellFormed(text) ? text : JsonUtf16.ToWellFormed(text);
    }

    /// <summary>The smallest string above every string starting with <paramref name="prefix"/>, in code point order: the next code point
    /// after its last, carrying past U+10FFFF; null when there is none (<c>""</c> and all-U+10FFFF prefixes). <paramref name="prefix"/> is
    /// well-formed (upstream <c>prefixUpperBound</c>).</summary>
    internal static string? PrefixUpperBound(string prefix)
    {
        // [...prefix]: the code points, a lone surrogate counting as one.
        var points = CodePoints(prefix);
        while (points.Count > 0)
        {
            var last = points[^1];
            points.RemoveAt(points.Count - 1);
            if (last == 0x10ffff) continue;
            // The surrogates are no code points of well-formed text: after U+D7FF comes U+E000.
            var next = last == 0xd7ff ? 0xe000 : last + 1;
            return string.Concat(points.Select(FromCodePoint)) + FromCodePoint(next);
        }
        return null;
    }

    /// <summary>The code points of <paramref name="text"/> as JavaScript's string iterator gives them: a surrogate pair is one, a lone
    /// surrogate is its own unit.</summary>
    internal static List<int> CodePoints(string text)
    {
        var points = new List<int>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                points.Add(char.ConvertToUtf32(text[index], text[index + 1]));
                index++;
            }
            else points.Add(text[index]);
        }
        return points;
    }

    /// <summary><c>String.fromCodePoint</c> of one code point (a surrogate code unit stays one unit).</summary>
    private static string FromCodePoint(int point) => point <= 0xffff ? ((char)point).ToString() : char.ConvertFromUtf32(point);
}
