// highlight.js 10.7.3 (BSD-3-Clause): lib/core.js (regex helpers: source, join, countMatchGroups, escape, langRe) — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>
/// highlight.js regex helpers plus a translator from ECMAScript (ES2020, no <c>u</c> flag, Annex B) patterns to .NET
/// patterns with identical semantics: ASCII <c>\d \w \b</c>, JS <c>\s</c>, <c>.</c> excluding JS line terminators,
/// <c>^ $</c> (with/without <c>m</c>) as lookarounds, JS identity escapes, legacy octal vs backreference resolution,
/// backreferences to non-participating groups matching empty, named groups renumbered positionally, and
/// case-insensitivity expanded explicitly with V8's Canonicalize table (no .NET IgnoreCase).
/// </summary>
internal static class HljsRegex
{
    const RegexOptions Options = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    static readonly ConcurrentDictionary<(string Source, bool IgnoreCase, bool Anchored, bool Compiled), Regex> Cache = new();

    /// <summary>Regex for a grammar pattern compiled the way hljs' <c>langRe</c> does (always <c>m</c>, optional <c>i</c>).
    /// <paramref name="anchored"/> wraps it in <c>\G(?:…)</c> for the <c>startsWith</c> checks; <paramref name="compiled"/>
    /// selects <see cref="RegexOptions.Compiled"/> (only for hot matchers: compiling costs milliseconds).</summary>
    public static Regex Get(string jsSource, bool ignoreCase, bool anchored = false, bool compiled = false) =>
        Cache.GetOrAdd((jsSource, ignoreCase, anchored, compiled), static k => Create(k.Source, true, k.IgnoreCase, k.Anchored, k.Compiled));

    public static Regex Create(string jsSource, bool multiline, bool ignoreCase, bool anchored, bool compiled = false)
    {
        var p = Translate(jsSource, multiline, ignoreCase);
        return new Regex(anchored ? @"\G(?:" + p + ")" : p, compiled ? Options | RegexOptions.Compiled : Options);
    }

    public static string Translate(string jsSource, bool multiline, bool ignoreCase) =>
        new Translator(jsSource, multiline, ignoreCase).Run();

    // ---- hljs lib/core.js regex utilities ----------------------------------------------------------------------

    /// <summary>hljs <c>escape(value)</c>: the JS source of <c>new RegExp(value.replace(/[-/\\^$*+?.()|[\]{}]/g, '\\$&amp;'), 'm')</c>.</summary>
    public static string EscapeSource(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if ("-/\\^$*+?.()|[]{}".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>hljs <c>countMatchGroups</c>: number of capturing groups of the JS pattern.</summary>
    public static int CountMatchGroups(string jsSource) => new Translator(jsSource, true, false).CountGroups();

    /// <summary>hljs <c>join(regexps, '|')</c> including its backreference rewriting (BACKREF_RE semantics, bugs included).</summary>
    public static string Join(IReadOnlyList<string> regexps)
    {
        var numCaptures = 0;
        var outer = new StringBuilder();
        for (var r = 0; r < regexps.Count; r++)
        {
            numCaptures += 1;
            var offset = numCaptures;
            var re = regexps[r];
            var sb = new StringBuilder();
            var pos = 0;
            while (pos < re.Length)
            {
                var (index, length, backref) = BackrefExec(re, pos);
                if (index < 0) { sb.Append(re, pos, re.Length - pos); break; }
                sb.Append(re, pos, index - pos);
                var m0 = re.Substring(index, length);
                pos = index + length;
                if (m0[0] == '\\' && backref != null)
                    sb.Append('\\').Append(JsNumberToString(ParseJsNumber(backref) + offset));
                else
                {
                    sb.Append(m0);
                    if (m0 == "(") numCaptures++;
                }
            }
            if (r > 0) outer.Append('|');
            outer.Append('(').Append(sb).Append(')');
        }
        return outer.ToString();
    }

    static double ParseJsNumber(string digits) => double.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    static string JsNumberToString(double d) =>
        d < 1e21 ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("0.################e+0", CultureInfo.InvariantCulture);

    static bool IsJsLineTerminator(char c) => c is '\n' or '\r' or '\u2028' or '\u2029';

    /// <summary>Leftmost match of <c>/\[(?:[^\\\]]|\\.)*\]|\(\??|\\([1-9][0-9]*)|\\./</c> in <paramref name="s"/> from <paramref name="start"/>.</summary>
    static (int Index, int Length, string? Backref) BackrefExec(string s, int start)
    {
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '[')
            {
                var j = i + 1;
                while (j < s.Length)
                {
                    if (s[j] == ']') return (i, j - i + 1, null);
                    if (s[j] == '\\')
                    {
                        if (j + 1 < s.Length && !IsJsLineTerminator(s[j + 1])) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                continue; // first alternative failed; the others cannot match at '['
            }
            if (c == '(') return (i, i + 1 < s.Length && s[i + 1] == '?' ? 2 : 1, null);
            if (c == '\\' && i + 1 < s.Length)
            {
                if (s[i + 1] is >= '1' and <= '9')
                {
                    var j = i + 2;
                    while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
                    return (i, j - i, s.Substring(i + 1, j - i - 1));
                }
                if (!IsJsLineTerminator(s[i + 1])) return (i, 2, null);
            }
        }
        return (-1, 0, null);
    }

    // ---- character sets ---------------------------------------------------------------------------------------

    /// <summary>Sorted, merged, inclusive UTF-16 code unit ranges.</summary>
    sealed class CharSet
    {
        public readonly List<(int Lo, int Hi)> Ranges = [];
        public CharSet Add(int lo, int hi) { Ranges.Add((lo, hi)); return this; }
        public CharSet Add(int c) => Add(c, c);
        public CharSet AddSet(CharSet o) { Ranges.AddRange(o.Ranges); return this; }

        public CharSet Normalize()
        {
            if (Ranges.Count < 2) return this;
            Ranges.Sort((a, b) => a.Lo.CompareTo(b.Lo));
            var merged = new List<(int Lo, int Hi)>(Ranges.Count) { Ranges[0] };
            for (var i = 1; i < Ranges.Count; i++)
            {
                var (lo, hi) = Ranges[i];
                var last = merged[^1];
                if (lo <= last.Hi + 1) merged[^1] = (last.Lo, Math.Max(last.Hi, hi));
                else merged.Add((lo, hi));
            }
            Ranges.Clear();
            Ranges.AddRange(merged);
            return this;
        }

        public CharSet Complement()
        {
            Normalize();
            var r = new CharSet();
            var next = 0;
            foreach (var (lo, hi) in Ranges)
            {
                if (lo > next) r.Add(next, lo - 1);
                next = hi + 1;
            }
            if (next <= 0xFFFF) r.Add(next, 0xFFFF);
            return r;
        }
    }

    static CharSet Digits() => new CharSet().Add('0', '9');
    static CharSet Word() => new CharSet().Add('0', '9').Add('A', 'Z').Add('_').Add('a', 'z');
    static CharSet Space() => new CharSet().Add(9, 13).Add(' ').Add(0xA0).Add(0x1680).Add(0x2000, 0x200A).Add(0x2028, 0x2029)
        .Add(0x202F).Add(0x205F).Add(0x3000).Add(0xFEFF);
    static CharSet NonLineTerminator() => new CharSet().Add('\n').Add('\r').Add(0x2028, 0x2029).Normalize().Complement();

    static CharSet ClassEscapeSet(char e) => e switch
    {
        'd' => Digits(), 'D' => Digits().Normalize().Complement(),
        'w' => Word(), 'W' => Word().Normalize().Complement(),
        's' => Space(), 'S' => Space().Normalize().Complement(),
        _ => throw new InvalidOperationException(),
    };

    // ---- case folding (ES2020 Canonicalize, non-unicode) --------------------------------------------------------

    static volatile CaseTable? caseTable;

    sealed class CaseTable(int[] multi, Dictionary<int, int[]> members, int[] canon)
    {
        public readonly int[] Multi = multi;           // sorted code units whose equivalence class has more than one member
        public readonly Dictionary<int, int[]> Members = members; // canonical value -> members
        public readonly int[] Canon = canon;
    }

    /// <summary>Installs V8's Canonicalize exceptions (flat [ch, canonical, …]); must run before any /i translation.</summary>
    public static void SetCanonicalizeTable(IReadOnlyList<int> flat)
    {
        var canon = new int[0x10000];
        for (var c = 0; c < canon.Length; c++) canon[c] = c;
        for (var i = 0; i + 1 < flat.Count; i += 2) canon[flat[i]] = flat[i + 1];
        // A class with several members contains every exception mapping to it, plus its canonical value when that maps to itself.
        var groups = new Dictionary<int, SortedSet<int>>();
        for (var i = 0; i + 1 < flat.Count; i += 2)
        {
            if (!groups.TryGetValue(flat[i + 1], out var g)) groups[flat[i + 1]] = g = [];
            g.Add(flat[i]);
            if (canon[flat[i + 1]] == flat[i + 1]) g.Add(flat[i + 1]);
        }
        var members = new Dictionary<int, int[]>();
        var multi = new List<int>();
        foreach (var (k, g) in groups)
        {
            if (g.Count < 2) continue;
            members[k] = [.. g];
            multi.AddRange(g);
        }
        multi.Sort();
        caseTable = new CaseTable([.. multi], members, canon);
    }

    static CharSet CaseClose(CharSet set)
    {
        var t = caseTable ?? throw new InvalidOperationException("hljs canonicalize table not loaded");
        set.Normalize();
        var extra = new CharSet();
        foreach (var (lo, hi) in set.Ranges)
        {
            var i = Array.BinarySearch(t.Multi, lo);
            for (i = i < 0 ? ~i : i; i < t.Multi.Length && t.Multi[i] <= hi; i++)
                foreach (var d in t.Members[t.Canon[t.Multi[i]]]) extra.Add(d);
        }
        return extra.Ranges.Count == 0 ? set : set.AddSet(extra).Normalize();
    }

    // ---- translator --------------------------------------------------------------------------------------------

    sealed class Translator(string s, bool multiline, bool ignoreCase)
    {
        readonly StringBuilder sb = new(s.Length * 2);
        int pos;
        int totalGroups;
        readonly List<string?> groupNames = [];

        public int CountGroups() { Prescan(); return totalGroups; }

        void Prescan()
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '\\') { i++; continue; }
                if (c == '[')
                {
                    for (i++; i < s.Length && s[i] != ']'; i++) if (s[i] == '\\') i++;
                    continue;
                }
                if (c != '(') continue;
                if (i + 1 < s.Length && s[i + 1] == '?')
                {
                    if (i + 2 < s.Length && s[i + 2] == '<' && i + 3 < s.Length && s[i + 3] != '=' && s[i + 3] != '!')
                    {
                        var end = s.IndexOf('>', i + 3);
                        if (end < 0) throw Error("invalid capture group name");
                        groupNames.Add(s.Substring(i + 3, end - i - 3));
                        totalGroups++;
                    }
                    continue;
                }
                groupNames.Add(null);
                totalGroups++;
            }
        }

        bool HasNamedGroups => groupNames.Exists(n => n != null);

        ArgumentException Error(string message) => new($"Invalid regular expression /{s}/: {message}");

        public string Run()
        {
            Prescan();
            while (pos < s.Length)
            {
                var c = s[pos];
                switch (c)
                {
                    case '\\': Escape(); break;
                    case '[': Class(); break;
                    case '(': Group(); break;
                    case ')' or '|' or '*' or '+' or '?': sb.Append(c); pos++; break;
                    case '{':
                        if (TryBracedQuantifier(out var q)) { sb.Append(q); }
                        else { EmitChar('{'); pos++; }
                        break;
                    case '.': EmitSet(NonLineTerminator()); pos++; break;
                    case '^': sb.Append(multiline ? @"(?<![^\u000A\u000D\u2028\u2029])" : @"(?<![\u0000-\uFFFF])"); pos++; break;
                    case '$': sb.Append(multiline ? @"(?![^\u000A\u000D\u2028\u2029])" : @"(?![\u0000-\uFFFF])"); pos++; break;
                    default: EmitChar(c); pos++; break;
                }
            }
            return sb.ToString();
        }

        bool TryBracedQuantifier(out string q)
        {
            q = "";
            var i = pos + 1;
            var d1 = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            if (i == d1) return false;
            if (i < s.Length && s[i] == ',')
            {
                i++;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            }
            if (i >= s.Length || s[i] != '}') return false;
            q = s.Substring(pos, i - pos + 1);
            pos = i + 1;
            return true;
        }

        void Group()
        {
            if (pos + 1 < s.Length && s[pos + 1] == '?')
            {
                var rest = s.AsSpan(pos + 2);
                if (rest.StartsWith(":") || rest.StartsWith("=") || rest.StartsWith("!")) { sb.Append(s, pos, 3); pos += 3; return; }
                if (rest.StartsWith("<=") || rest.StartsWith("<!")) { sb.Append(s, pos, 4); pos += 4; return; }
                if (rest.StartsWith("<"))
                {
                    var end = s.IndexOf('>', pos + 3);
                    sb.Append('(');
                    pos = end + 1;
                    return;
                }
                throw Error("invalid group");
            }
            sb.Append('(');
            pos++;
        }

        void Backreference(int n) =>
            sb.Append("(?(").Append(n).Append(')').Append(ignoreCase ? "(?i:\\k<" : "\\k<").Append(n).Append(ignoreCase ? ">))" : ">)");

        void Escape()
        {
            pos++;
            if (pos >= s.Length) throw Error("\\ at end of pattern");
            var e = s[pos++];
            switch (e)
            {
                case 'd' or 'D' or 'w' or 'W' or 's' or 'S': EmitSet(ClassEscapeSet(e)); return;
                // RegexOptions.ECMAScript's \b is ASCII except that it also counts U+0130 as a word character; JS does not.
                case 'b': sb.Append(@"(?:(?<!\u0130)(?!\u0130)\b|(?<=\u0130)(?=[0-9A-Z_a-z])|(?<=[0-9A-Z_a-z])(?=\u0130))"); return;
                case 'B': sb.Append(@"(?:(?<!\u0130)(?!\u0130)\B|(?<=\u0130)(?![0-9A-Z_a-z])|(?<![0-9A-Z_a-z])(?=\u0130))"); return;
                case 'k' when HasNamedGroups:
                    {
                        if (pos >= s.Length || s[pos] != '<') throw Error("invalid named reference");
                        var end = s.IndexOf('>', pos);
                        if (end < 0) throw Error("invalid named reference");
                        var idx = groupNames.IndexOf(s.Substring(pos + 1, end - pos - 1));
                        if (idx < 0) throw Error("invalid named capture referenced");
                        pos = end + 1;
                        Backreference(idx + 1);
                        return;
                    }
                case >= '1' and <= '9':
                    {
                        var start = pos - 1;
                        var i = start;
                        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                        var n = ParseJsNumber(s.Substring(start, i - start));
                        if (n <= totalGroups) { pos = i; Backreference((int)n); return; }
                        if (e is '8' or '9') { EmitChar(e); return; }
                        pos = start;
                        EmitChar(LegacyOctal());
                        return;
                    }
                case 'c' when pos < s.Length && char.IsAsciiLetter(s[pos]): EmitChar((char)(s[pos++] % 32)); return;
                case 'c': pos--; EmitChar('\\'); return; // Annex B: "\c" not followed by a letter is a literal backslash
                default: EmitChar(CharacterEscape(e)); return;
            }
        }

        /// <summary>CharacterEscape after the escaped char <paramref name="e"/> was consumed (shared with classes).</summary>
        char CharacterEscape(char e)
        {
            switch (e)
            {
                case 'f': return '\f';
                case 'n': return '\n';
                case 'r': return '\r';
                case 't': return '\t';
                case 'v': return '\v';
                case '0' when pos >= s.Length || !char.IsAsciiDigit(s[pos]): return '\0';
                case >= '0' and <= '7': pos--; return LegacyOctal();
                case 'x' when TryHex(2, out var x): return x;
                case 'u' when TryHex(4, out var u): return u;
                default: return e; // identity escape (includes \/ \- \8 \9 \k \x \u and letters)
            }
        }

        bool TryHex(int digits, out char value)
        {
            value = '\0';
            if (pos + digits > s.Length) return false;
            var v = 0;
            for (var i = 0; i < digits; i++)
            {
                var h = s[pos + i];
                var d = char.IsAsciiDigit(h) ? h - '0' : h is >= 'a' and <= 'f' ? h - 'a' + 10 : h is >= 'A' and <= 'F' ? h - 'A' + 10 : -1;
                if (d < 0) return false;
                v = v * 16 + d;
            }
            pos += digits;
            value = (char)v;
            return true;
        }

        static bool IsOctal(char c) => c is >= '0' and <= '7';

        /// <summary>Annex B LegacyOctalEscapeSequence starting at <see cref="pos"/> (first digit is octal).</summary>
        char LegacyOctal()
        {
            var d1 = s[pos++] - '0';
            if (pos >= s.Length || !IsOctal(s[pos])) return (char)d1;
            var v = d1 * 8 + (s[pos++] - '0');
            if (d1 <= 3 && pos < s.Length && IsOctal(s[pos])) v = v * 8 + (s[pos++] - '0');
            return (char)v;
        }

        void Class()
        {
            pos++;
            var negated = pos < s.Length && s[pos] == '^';
            if (negated) pos++;
            var set = new CharSet();
            while (true)
            {
                if (pos >= s.Length) throw Error("missing /");
                if (s[pos] == ']') { pos++; break; }
                var a = ClassAtom();
                if (pos + 1 < s.Length && s[pos] == '-' && s[pos + 1] != ']')
                {
                    pos++;
                    var b = ClassAtom();
                    if (a.Set != null || b.Set != null)
                    {
                        Add(set, a);
                        set.Add('-');
                        Add(set, b);
                    }
                    else
                    {
                        if (a.Char > b.Char) throw Error("range out of order in character class");
                        set.Add(a.Char, b.Char);
                    }
                }
                else Add(set, a);
            }
            EmitSet(set, negated);

            static void Add(CharSet set, (int Char, CharSet? Set) atom)
            {
                if (atom.Set != null) set.AddSet(atom.Set);
                else set.Add(atom.Char);
            }
        }

        (int Char, CharSet? Set) ClassAtom()
        {
            var c = s[pos++];
            if (c != '\\') return (c, null);
            if (pos >= s.Length) throw Error("\\ at end of pattern");
            var e = s[pos++];
            switch (e)
            {
                case 'd' or 'D' or 'w' or 'W' or 's' or 'S': return (0, ClassEscapeSet(e));
                case 'b': return ('\b', null);
                case 'c' when pos < s.Length && (char.IsAsciiLetterOrDigit(s[pos]) || s[pos] == '_'): return (s[pos++] % 32, null);
                case 'c': pos--; return ('\\', null);
                case 'k' when HasNamedGroups: throw Error("invalid escape");
                default: return (CharacterEscape(e), null);
            }
        }

        void EmitChar(char c) => EmitSet(new CharSet().Add(c));

        void EmitSet(CharSet set, bool negated = false)
        {
            if (ignoreCase) set = CaseClose(set);
            set.Normalize();
            if (negated) set = set.Complement();
            var ranges = set.Ranges;
            if (ranges.Count == 0) { sb.Append("(?!)"); return; }
            if (ranges.Count == 1 && ranges[0].Lo == ranges[0].Hi) { AppendChar(sb, ranges[0].Lo, false); return; }
            var complement = set.Complement().Ranges;
            var useNegation = complement.Count < ranges.Count;
            sb.Append(useNegation ? "[^" : "[");
            foreach (var (lo, hi) in useNegation ? complement : ranges)
            {
                AppendChar(sb, lo, true);
                if (hi == lo) continue;
                if (hi > lo + 1) sb.Append('-');
                AppendChar(sb, hi, true);
            }
            sb.Append(']');
        }

        static void AppendChar(StringBuilder sb, int c, bool inClass)
        {
            if (c < 128 && (char.IsAsciiLetterOrDigit((char)c) || (!inClass && c is '_' or ',' or ':' or ';' or '\'' or '"' or '<' or '>' or '=' or '!' or '@' or '%' or '&' or '/' or '~' or '`' or '-' or ' ')))
                sb.Append((char)c);
            else
                sb.Append("\\u").Append(c.ToString("X4", CultureInfo.InvariantCulture));
        }
    }
}
