// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/package-manager.ts (isExactNpmVersion,
// getNpmVersionRange, the semver calls valid, validRange, satisfies, maxSatisfying, gt and rcompare of semver 7.8.5).
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Packages;

/// <summary>A parsed semantic version (node-semver strict parsing: an optional leading <c>v</c>, no leading zeros).</summary>
internal sealed record PiSemverVersion(long Major, long Minor, long Patch, IReadOnlyList<string> Prerelease)
{
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease.Count > 0 ? "-" + string.Join('.', Prerelease) : "");
}

/// <summary>The node-semver subset the package manager uses: versions, ranges (<c>^</c>, <c>~</c>, x-ranges, hyphen ranges,
/// comparators and <c>||</c>) with node-semver's prerelease rule, comparison and the lookups built on them.</summary>
internal static partial class PiSemver
{
    private const string Numeric = @"0|[1-9]\d*";
    private const string Ident = @"(?:0|[1-9]\d*|\d*[a-zA-Z-][a-zA-Z0-9-]*)";
    [GeneratedRegex(@"^v?(" + Numeric + @")\.(" + Numeric + @")\.(" + Numeric + @")(?:-(" + Ident + @"(?:\." + Ident + @")*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Full();
    private const string XrPart = @"0|[1-9]\d*|[xX*]";
    private const string XRange = @"[v=\s]*(" + XrPart + @")(?:\.(" + XrPart + @")(?:\.(" + XrPart + @")(?:-?(" + Ident + @"(?:\." + Ident + @")*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)?)?";
    [GeneratedRegex(@"^((?:<|>)?=?)\s*" + XRange + "$", RegexOptions.CultureInvariant)]
    private static partial Regex XRangeRegex();
    [GeneratedRegex(@"^(?:~>?)" + XRange + "$", RegexOptions.CultureInvariant)]
    private static partial Regex TildeRegex();
    [GeneratedRegex(@"^\^" + XRange + "$", RegexOptions.CultureInvariant)]
    private static partial Regex CaretRegex();
    [GeneratedRegex(@"^\s*" + XRange + @"\s+-\s+" + XRange + @"\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenRegex();
    [GeneratedRegex(@"(<|>)?=?\s*(?=[v=\s]*[0-9xX*])", RegexOptions.CultureInvariant)]
    private static partial Regex OperatorSpace();
    [GeneratedRegex(@"(\s*)((?:<|>)?=?)\s*([v=\s]*(?:" + XrPart + @")(?:\.(?:" + XrPart + @")(?:\.(?:" + XrPart + @")(?:-?(?:" + Ident + @"(?:\." + Ident + @")*))?)?)?)", RegexOptions.CultureInvariant)]
    private static partial Regex ComparatorTrim();
    [GeneratedRegex(@"(\s*)~>?\s+", RegexOptions.CultureInvariant)]
    private static partial Regex TildeTrim();
    [GeneratedRegex(@"(\s*)\^\s+", RegexOptions.CultureInvariant)]
    private static partial Regex CaretTrim();
    [GeneratedRegex(@"^((?:<|>)?=?)\s*(v?(?:" + Numeric + @")\.(?:" + Numeric + @")\.(?:" + Numeric + @")(?:-(?:" + Ident + @"(?:\." + Ident + @")*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)$|^$", RegexOptions.CultureInvariant)]
    private static partial Regex ComparatorRegex();

    /// <summary>semver.parse: null for anything that is not a strict version.</summary>
    internal static PiSemverVersion? Parse(string? version)
    {
        if (version is null || version.Length > 256) return null;
        var match = Full().Match(version.Trim());
        if (!match.Success) return null;
        if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch) ||
            major > 9007199254740991 || minor > 9007199254740991 || patch > 9007199254740991) return null;
        return new(major, minor, patch, match.Groups[4].Success ? match.Groups[4].Value.Split('.') : []);
    }

    /// <summary>semver.valid.</summary>
    internal static string? Valid(string? version) => Parse(version)?.ToString();

    /// <summary>semver.compare.</summary>
    internal static int Compare(PiSemverVersion left, PiSemverVersion right)
    {
        var main = left.Major != right.Major ? left.Major.CompareTo(right.Major) : left.Minor != right.Minor ? left.Minor.CompareTo(right.Minor) : left.Patch.CompareTo(right.Patch);
        if (main != 0) return main;
        if (left.Prerelease.Count > 0 && right.Prerelease.Count == 0) return -1;
        if (left.Prerelease.Count == 0 && right.Prerelease.Count > 0) return 1;
        for (var index = 0; ; index++)
        {
            if (index >= left.Prerelease.Count && index >= right.Prerelease.Count) return 0;
            if (index >= right.Prerelease.Count) return 1;
            if (index >= left.Prerelease.Count) return -1;
            var a = left.Prerelease[index]; var b = right.Prerelease[index];
            if (a == b) continue;
            var aNumeric = a.All(char.IsAsciiDigit); var bNumeric = b.All(char.IsAsciiDigit);
            if (aNumeric && bNumeric) return decimal.Parse(a, CultureInfo.InvariantCulture).CompareTo(decimal.Parse(b, CultureInfo.InvariantCulture));
            if (aNumeric) return -1;
            if (bNumeric) return 1;
            return string.CompareOrdinal(a, b) < 0 ? -1 : 1;
        }
    }

    private static PiSemverVersion Require(string version) => Parse(version) ?? throw new ArgumentException($"Invalid Version: {version}");
    /// <summary>semver.gt (throws for invalid versions, as node-semver does).</summary>
    internal static bool Gt(string left, string right) => Compare(Require(left), Require(right)) > 0;
    /// <summary>semver.rcompare.</summary>
    internal static int RCompare(string left, string right) => Compare(Require(right), Require(left));

    private sealed record Comparator(string Operator, PiSemverVersion? Version)
    {
        internal bool Test(PiSemverVersion version)
        {
            if (Version is null) return true;
            var order = Compare(version, Version);
            return Operator switch { "<" => order < 0, "<=" => order <= 0, ">" => order > 0, ">=" => order >= 0, _ => order == 0 };
        }
    }

    /// <summary>semver.validRange: the normalized range, or null when it does not parse.</summary>
    internal static string? ValidRange(string? range)
    {
        var sets = ParseRange(range);
        if (sets is null) return null;
        var text = string.Join("||", sets.Select(set => string.Join(' ', set.Select(item => item.Version is null ? "" : item.Operator + item.Version))).Select(part => part.Trim()));
        return text.Length == 0 ? "*" : text;
    }

    /// <summary>semver.satisfies (without includePrerelease): a prerelease version needs a comparator of its own tuple with a prerelease.</summary>
    internal static bool Satisfies(string version, string range)
    {
        var parsed = Parse(version); var sets = ParseRange(range);
        if (parsed is null || sets is null) return false;
        return sets.Any(set => TestSet(set, parsed));
    }

    /// <summary>semver.maxSatisfying.</summary>
    internal static string? MaxSatisfying(IEnumerable<string> versions, string range)
    {
        var sets = ParseRange(range);
        if (sets is null) return null;
        string? best = null; PiSemverVersion? bestVersion = null;
        foreach (var version in versions)
        {
            if (Parse(version) is not { } parsed || !sets.Any(set => TestSet(set, parsed))) continue;
            if (bestVersion is null || Compare(bestVersion, parsed) < 0) { best = version; bestVersion = parsed; }
        }
        return best;
    }

    private static bool TestSet(List<Comparator> set, PiSemverVersion version)
    {
        if (!set.All(comparator => comparator.Test(version))) return false;
        if (version.Prerelease.Count == 0) return true;
        return set.Any(comparator => comparator.Version is { } bound && bound.Prerelease.Count > 0 &&
            bound.Major == version.Major && bound.Minor == version.Minor && bound.Patch == version.Patch);
    }

    private static List<List<Comparator>>? ParseRange(string? range)
    {
        if (range is null) return null;
        var sets = new List<List<Comparator>>();
        foreach (var raw in Regex.Split(range.Trim(), @"\s*\|\|\s*"))
        {
            var set = ParseSet(raw);
            if (set is null) return null;
            sets.Add(set);
        }
        // node-semver drops null sets when others exist; a set matching nothing (<0.0.0-0) stays.
        return sets.Count == 0 ? [[new("", null)]] : sets;
    }

    private static bool IsX(string? part) => string.IsNullOrEmpty(part) || part is "x" or "X" or "*";

    private static List<Comparator>? ParseSet(string raw)
    {
        var text = raw.Trim();
        var hyphen = HyphenRegex().Match(text);
        if (hyphen.Success) text = Hyphen(hyphen);
        text = ComparatorTrim().Replace(text, "$1$2$3");
        text = TildeTrim().Replace(text, "$1~");
        text = CaretTrim().Replace(text, "$1^");
        var result = new List<Comparator>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var desugared = Desugar(token);
            if (desugared is null) return null;
            foreach (var part in desugared.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = ComparatorRegex().Match(part);
                if (!match.Success) return null;
                if (match.Groups[2].Value.Length == 0) { result.Add(new("", null)); continue; }
                var op = match.Groups[1].Value == "=" ? "" : match.Groups[1].Value;
                result.Add(new(op, Parse(match.Groups[2].Value)));
            }
        }
        if (result.Count == 0) result.Add(new("", null));
        // A set with ANY next to real comparators keeps only the real ones (node-semver filters "" comparators when others exist).
        if (result.Count > 1 && result.Any(item => item.Version is not null)) result.RemoveAll(item => item.Version is null);
        return result;
    }

    private static string Hyphen(Match match)
    {
        string from, to;
        string M(int index) => match.Groups[index].Value;
        if (IsX(M(1))) from = "";
        else if (IsX(M(2))) from = $">={M(1)}.0.0";
        else if (IsX(M(3))) from = $">={M(1)}.{M(2)}.0";
        else from = ">=" + M(1) + "." + M(2) + "." + M(3) + (match.Groups[4].Success ? "-" + M(4) : "");
        if (IsX(M(5))) to = "";
        else if (IsX(M(6))) to = $"<{long.Parse(M(5), CultureInfo.InvariantCulture) + 1}.0.0-0";
        else if (IsX(M(7))) to = $"<{M(5)}.{long.Parse(M(6), CultureInfo.InvariantCulture) + 1}.0-0";
        else if (match.Groups[8].Success) to = $"<={M(5)}.{M(6)}.{M(7)}-{M(8)}";
        else to = $"<={M(5)}.{M(6)}.{M(7)}";
        return (from + " " + to).Trim();
    }

    private static string? Desugar(string token)
    {
        if (token is "*" or "x" or "X" or "") return "";
        Match match;
        if ((match = CaretRegex().Match(token)).Success) return Caret(match);
        if ((match = TildeRegex().Match(token)).Success) return Tilde(match);
        if ((match = XRangeRegex().Match(token)).Success) return XRangeComparator(match);
        return token.StartsWith('<') || token.StartsWith('>') || token.StartsWith('=') || char.IsAsciiDigit(token[0]) || token[0] == 'v' ? token : null;
    }

    private static long N(Match match, int index) => long.Parse(match.Groups[index].Value, CultureInfo.InvariantCulture);

    private static string Tilde(Match m)
    {
        string g(int i) => m.Groups[i].Value;
        if (IsX(g(1))) return "";
        if (IsX(g(2))) return $">={g(1)}.0.0 <{N(m, 1) + 1}.0.0-0";
        if (IsX(g(3))) return $">={g(1)}.{g(2)}.0 <{g(1)}.{N(m, 2) + 1}.0-0";
        var pre = m.Groups[4].Success ? "-" + g(4) : "";
        return $">={g(1)}.{g(2)}.{g(3)}{pre} <{g(1)}.{N(m, 2) + 1}.0-0";
    }

    private static string Caret(Match m)
    {
        string g(int i) => m.Groups[i].Value;
        if (IsX(g(1))) return "";
        if (IsX(g(2))) return $">={g(1)}.0.0 <{N(m, 1) + 1}.0.0-0";
        if (IsX(g(3)))
            return g(1) == "0" ? $">={g(1)}.{g(2)}.0 <{g(1)}.{N(m, 2) + 1}.0-0" : $">={g(1)}.{g(2)}.0 <{N(m, 1) + 1}.0.0-0";
        var pre = m.Groups[4].Success ? "-" + g(4) : "";
        var lower = $">={g(1)}.{g(2)}.{g(3)}{pre}";
        if (g(1) == "0")
            return g(2) == "0" ? $"{lower} <{g(1)}.{g(2)}.{N(m, 3) + 1}-0" : $"{lower} <{g(1)}.{N(m, 2) + 1}.0-0";
        return $"{lower} <{N(m, 1) + 1}.0.0-0";
    }

    private static string XRangeComparator(Match m)
    {
        string g(int i) => m.Groups[i].Value;
        var op = g(1);
        if (op == "=") op = "";
        bool xM = IsX(g(2)), xm = xM || IsX(g(3)), xp = xm || IsX(g(4));
        var pre = m.Groups[5].Success ? "-" + g(5) : "";
        if (xM) return op is ">" or "<" ? "<0.0.0-0" : "";
        if (op.Length > 0 && xp)
        {
            long major = N(m, 2), minor = xm ? 0 : N(m, 3), patch = 0;
            if (op == ">")
            {
                op = ">=";
                if (xm) { major++; minor = 0; } else minor++;
            }
            else if (op == "<=")
            {
                op = "<";
                if (xm) major++; else minor++;
            }
            if (op == "<") pre = "-0";
            return $"{op}{major}.{minor}.{patch}{pre}";
        }
        if (xm) return $">={g(2)}.0.0 <{N(m, 2) + 1}.0.0-0";
        if (xp) return $">={g(2)}.{g(3)}.0 <{g(2)}.{N(m, 3) + 1}.0-0";
        return op + g(2) + "." + g(3) + "." + g(4) + pre;
    }
}
