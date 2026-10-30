// Decision 0011 (run W4, unit R4): gitignore matching for temporary copies. A copy skips the default excludes, the extra excludes and
// "anything in .gitignore": the project's .gitignore files (each relative to its own folder, deeper files first, the last matching line of
// a file deciding), the .gitignore files of the folders above it up to the repository root, and the repository's .git/info/exclude.
// Patterns follow gitignore(5): '#' comments, '!' negation, a trailing '/' for folders only, a '/' at the start or in the middle anchors
// the pattern to its file's folder, '*' and '?' never match '/', '[...]' classes, and "**/", "/**" and "/**/" span folders. An excluded
// folder is never entered, so nothing below it can be re-included (as in git). Matching ignores case on Windows (core.ignorecase).
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.WebAgent.AgentCompare.Workspaces;

/// <summary>Whether a pattern list matched a path.</summary>
internal enum IgnoreMatch
{
    /// <summary>No pattern matched.</summary>
    None,

    /// <summary>The last matching pattern ignores the path.</summary>
    Ignore,

    /// <summary>The last matching pattern is a negation (<c>!</c>), which keeps the path.</summary>
    Keep,
}

/// <summary>One gitignore pattern.</summary>
internal sealed class IgnorePattern
{
    private static readonly RegexOptions Options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
    private readonly Regex regex;

    private IgnorePattern(Regex regex, bool negate, bool foldersOnly, bool basename)
    {
        this.regex = regex;
        Negate = negate;
        FoldersOnly = foldersOnly;
        Basename = basename;
    }

    /// <summary>A <c>!</c> pattern.</summary>
    public bool Negate { get; }

    /// <summary>A pattern with a trailing <c>/</c>.</summary>
    public bool FoldersOnly { get; }

    /// <summary>A pattern without a <c>/</c> (other than a trailing one), which matches a name at any depth.</summary>
    public bool Basename { get; }

    /// <summary>Whether the pattern matches <paramref name="path"/> (relative to the pattern's folder, '/' separators).</summary>
    public bool IsMatch(string path, bool isFolder)
    {
        if (FoldersOnly && !isFolder) return false;
        var subject = Basename ? path[(path.LastIndexOf('/') + 1)..] : path;
        return regex.IsMatch(subject);
    }

    /// <summary>Parses one line of a gitignore file; null for a blank line, a comment or a line with no pattern left.</summary>
    public static IgnorePattern? Parse(string line)
    {
        if (line.EndsWith('\r')) line = line[..^1];
        if (line.Length == 0 || line[0] == '#') return null;
        // Trailing spaces are dropped unless escaped with a backslash.
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !(end >= 2 && line[end - 2] == '\\')) end--;
        line = line[..end];
        var negate = false;
        if (line.StartsWith('!'))
        {
            negate = true;
            line = line[1..];
        }
        var foldersOnly = false;
        while (line.EndsWith('/') && !line.EndsWith("\\/", StringComparison.Ordinal))
        {
            foldersOnly = true;
            line = line[..^1];
        }
        if (line.Length == 0) return null;
        var anchored = line.Contains('/');
        if (line.StartsWith('/')) line = line.TrimStart('/');
        if (line.Length == 0) return null;
        return new IgnorePattern(new Regex(Glob(line), Options, TimeSpan.FromSeconds(1)), negate, foldersOnly, !anchored);
    }

    // gitignore's wildmatch with WM_PATHNAME, as a regular expression.
    private static string Glob(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var index = 0; index < pattern.Length;)
        {
            var c = pattern[index];
            if (c == '*')
            {
                var run = index;
                while (run < pattern.Length && pattern[run] == '*') run++;
                var atStart = index == 0 || pattern[index - 1] == '/';
                var atEnd = run == pattern.Length;
                var beforeSlash = run < pattern.Length && pattern[run] == '/';
                if (run - index == 2 && atStart && beforeSlash)
                {
                    regex.Append("(?:.*/)?"); // "**/": zero or more folders
                    index = run + 1;
                }
                else if (run - index == 2 && atStart && atEnd)
                {
                    regex.Append(".*"); // "/**" at the end (or "**" alone): everything inside
                    index = run;
                }
                else
                {
                    regex.Append("[^/]*"); // other runs of '*' are ordinary stars
                    index = run;
                }
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
                index++;
            }
            else if (c == '[' && ClassEnd(pattern, index) is var close and > 0)
            {
                var body = pattern[(index + 1)..close];
                var negated = body.Length > 0 && body[0] is '!' or '^';
                if (negated) body = body[1..];
                var set = new StringBuilder();
                for (var at = 0; at < body.Length; at++)
                {
                    var b = body[at];
                    if (b == '\\' && at + 1 < body.Length) b = body[++at];
                    set.Append(b is '\\' or ']' or '[' or '^' ? "\\" + b : b.ToString());
                }
                regex.Append("(?!/)[").Append(negated ? "^" : "").Append(set).Append(']');
                index = close + 1;
            }
            else if (c == '\\' && index + 1 < pattern.Length)
            {
                regex.Append(Regex.Escape(pattern[index + 1].ToString()));
                index += 2;
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
                index++;
            }
        }
        return regex.Append('$').ToString();
    }

    // The index of the ']' closing the class that opens at 'open', or -1 (then '[' is literal). A ']' right after "[", "[!" or "[^" is
    // part of the class.
    private static int ClassEnd(string pattern, int open)
    {
        var at = open + 1;
        if (at < pattern.Length && pattern[at] is '!' or '^') at++;
        if (at < pattern.Length && pattern[at] == ']') at++;
        for (; at < pattern.Length; at++)
        {
            if (pattern[at] == '\\') at++;
            else if (pattern[at] == ']') return at;
        }
        return -1;
    }
}

/// <summary>The patterns of one source (a .gitignore file, info/exclude, or a list of excludes), and where they apply.</summary>
/// <param name="patterns">In file order.</param>
/// <param name="folder">The folder of the source relative to the walked root ('/'-terminated, empty for the root): paths outside it never
/// match.</param>
/// <param name="prefix">For a source above the walked root: the walked root's path relative to the source's folder ('/'-terminated).</param>
internal sealed class IgnoreList(IReadOnlyList<IgnorePattern> patterns, string folder = "", string prefix = "")
{
    /// <summary>An empty list.</summary>
    public static IgnoreList Empty { get; } = new([]);

    /// <summary>Whether there are no patterns.</summary>
    public bool IsEmpty => patterns.Count == 0;

    /// <summary>Parses <paramref name="lines"/>.</summary>
    public static IgnoreList Parse(IEnumerable<string> lines, string folder = "", string prefix = "") =>
        new([.. lines.Select(IgnorePattern.Parse).OfType<IgnorePattern>()], folder, prefix);

    /// <summary>Reads a gitignore file; an empty list when it is missing or unreadable.</summary>
    public static IgnoreList Read(string file, string folder = "", string prefix = "")
    {
        try { return File.Exists(file) ? Parse(File.ReadAllLines(file), folder, prefix) : Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Empty; }
    }

    /// <summary>The last matching pattern's verdict for <paramref name="path"/> (relative to the walked root).</summary>
    public IgnoreMatch Match(string path, bool isFolder)
    {
        if (patterns.Count == 0) return IgnoreMatch.None;
        if (folder.Length > 0)
        {
            if (!path.StartsWith(folder, Comparison)) return IgnoreMatch.None;
            path = path[folder.Length..];
        }
        path = prefix + path;
        for (var index = patterns.Count - 1; index >= 0; index--)
            if (patterns[index].IsMatch(path, isFolder)) return patterns[index].Negate ? IgnoreMatch.Keep : IgnoreMatch.Ignore;
        return IgnoreMatch.None;
    }

    /// <summary>Whether <paramref name="path"/> or one of its parent folders is ignored (for a path list rather than a walk).</summary>
    public bool IsExcluded(string path)
    {
        var parts = path.Split('/');
        for (var depth = 1; depth <= parts.Length; depth++)
        {
            var isFolder = depth < parts.Length;
            if (Match(string.Join('/', parts.Take(depth)), isFolder) == IgnoreMatch.Ignore) return true;
        }
        return false;
    }

    internal static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>The gitignore sources in force in one folder: its own .gitignore over its parents', over info/exclude.</summary>
internal sealed class IgnoreStack
{
    private readonly IgnoreStack? parent;
    private readonly IgnoreList list;

    private IgnoreStack(IgnoreStack? parent, IgnoreList list)
    {
        this.parent = parent;
        this.list = list;
    }

    /// <summary>No rules.</summary>
    public static IgnoreStack Empty { get; } = new(null, IgnoreList.Empty);

    /// <summary>This stack with <paramref name="inner"/> on top (a deeper source, which wins).</summary>
    public IgnoreStack Push(IgnoreList inner) => inner.IsEmpty ? this : new IgnoreStack(this, inner);

    /// <summary>Whether the innermost source with a matching pattern ignores <paramref name="path"/>.</summary>
    public bool IsIgnored(string path, bool isFolder)
    {
        for (var current = this; current is not null; current = current.parent)
        {
            var match = current.list.Match(path, isFolder);
            if (match != IgnoreMatch.None) return match == IgnoreMatch.Ignore;
        }
        return false;
    }
}
