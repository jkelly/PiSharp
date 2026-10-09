// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/autocomplete.ts.
using System.Diagnostics;
using System.Text;

namespace PiSharp.Tui.Pi;

public sealed record AutocompleteItem(string Value, string Label, string? Description = null);
public sealed record AutocompleteSuggestions(List<AutocompleteItem> Items, string Prefix);
public sealed record CompletionResult(List<string> Lines, int CursorLine, int CursorCol);

/// <summary>A slash command offered by autocomplete, with optional argument completions.</summary>
public sealed record SlashCommand(string Name, string? Description = null, string? ArgumentHint = null,
    Func<string, Task<List<AutocompleteItem>?>>? GetArgumentCompletions = null);

public interface IAutocompleteProvider
{
    /// <summary>Characters that trigger this provider at token boundaries.</summary>
    IReadOnlyList<string> TriggerCharacters => [];
    Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken);
    CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix);
    bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol) => true;
}

/// <summary>Token rules shared by the editor and autocomplete (utils.ts autocomplete regexes).</summary>
public static class AutocompleteTokens
{
    private const string Wrappers = "([{<`";
    public static bool IsSeparator(char c) => TextUtils.IsJsWhitespace(c) || TextUtils.IsCjkPunctuation(c.ToString());
    /// <summary><c>(?:^|separator)$</c> against <paramref name="text"/>.</summary>
    public static bool IsAtTokenStart(string text) => text.Length == 0 || IsSeparator(text[^1]);

    private static bool NoSeparators(string text, int from)
    {
        for (var i = from; i < text.Length; i++) if (IsSeparator(text[i])) return false;
        return true;
    }

    /// <summary>The editor's trigger pattern: a token at a boundary, optional opening wrappers, then <c>@"…</c> or a trigger character and an unquoted suffix.</summary>
    public static bool MatchesTrigger(string text, IReadOnlyCollection<string> triggers, bool debounce = false)
    {
        for (var start = 0; start <= text.Length; start++)
        {
            if (start > 0 && !IsSeparator(text[start - 1])) continue;
            var j = start;
            while (j < text.Length && Wrappers.Contains(text[j])) j++;
            if (j >= text.Length) continue;
            var c = text[j].ToString();
            if (c == "@")
            {
                if (j + 1 < text.Length && text[j + 1] == '"' && text.IndexOf('"', j + 2) == -1) return true;
                if (triggers.Contains("@") && NoSeparators(text, j + 1)) return true;
                continue;
            }
            if (triggers.Contains(c) && NoSeparators(text, j + 1)) return true;
        }
        return false;
    }
}

/// <summary>Slash commands and file paths (<c>@</c> fuzzy search through fd, plain paths through the file system).</summary>
public sealed class CombinedAutocompleteProvider(IReadOnlyList<SlashCommand> commands, string basePath, string? fdPath = null) : IAutocompleteProvider
{
    private static readonly HashSet<char> PathDelimiters = [' ', '\t', '"', '\'', '='];
    private static readonly Dictionary<char, char> PathWrappers = new() { ['('] = ')', ['['] = ']', ['{'] = '}', ['<'] = '>', ['`'] = '`' };
    public IReadOnlyList<string> TriggerCharacters => [];
    /// <summary>The user's home directory used for <c>~</c> expansion.</summary>
    public string Home { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ToDisplayPath(string value) => value.Replace('\\', '/');

    private static string BuildFdPathQuery(string query)
    {
        var normalized = ToDisplayPath(query);
        if (!normalized.Contains('/')) return normalized;
        var trailing = normalized.EndsWith('/');
        var trimmed = normalized.Trim('/');
        if (trimmed.Length == 0) return normalized;
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(System.Text.RegularExpressions.Regex.Escape).ToList();
        if (segments.Count == 0) return normalized;
        const string separator = @"[\\/]";
        return string.Join(separator, segments) + (trailing ? separator : "");
    }

    private static int FindLastDelimiter(string text)
    {
        var last = -1;
        for (var i = 0; i < text.Length; i++) if (PathDelimiters.Contains(text[i]) || AutocompleteTokens.IsSeparator(text[i])) last = i;
        return last;
    }

    private static string StripLeadingWrappers(string token)
    {
        var result = token;
        while (result.Length > 0 && PathWrappers.TryGetValue(result[0], out var closer) && result.IndexOf(closer, 1) == -1) result = result[1..];
        return result;
    }

    private static int? FindUnclosedQuoteStart(string text)
    {
        var inQuotes = false; var start = -1;
        for (var i = 0; i < text.Length; i++) if (text[i] == '"') { inQuotes = !inQuotes; if (inQuotes) start = i; }
        return inQuotes ? start : null;
    }

    private static bool IsTokenStart(string text, int index)
    {
        var start = index;
        while (start > 0 && PathWrappers.ContainsKey(text[start - 1])) start--;
        return start > 0 && PathDelimiters.Contains(text[start - 1]) || AutocompleteTokens.IsAtTokenStart(text[..start]);
    }

    private static string? ExtractQuotedPrefix(string text)
    {
        if (FindUnclosedQuoteStart(text) is not { } quote) return null;
        if (quote > 0 && text[quote - 1] == '@') return IsTokenStart(text, quote - 1) ? text[(quote - 1)..] : null;
        return IsTokenStart(text, quote) ? text[quote..] : null;
    }

    private static (string Raw, bool At, bool Quoted) ParsePathPrefix(string prefix)
    {
        if (prefix.StartsWith("@\"", StringComparison.Ordinal)) return (prefix[2..], true, true);
        if (prefix.StartsWith('"')) return (prefix[1..], false, true);
        if (prefix.StartsWith('@')) return (prefix[1..], true, false);
        return (prefix, false, false);
    }

    private static string BuildCompletionValue(string path, bool atPrefix, bool quotedPrefix)
    {
        var needsQuotes = quotedPrefix || path.Any(AutocompleteTokens.IsSeparator);
        var prefix = atPrefix ? "@" : "";
        return needsQuotes ? prefix + "\"" + path + "\"" : prefix + path;
    }

    private static async Task<List<(string Path, bool IsDirectory)>> WalkWithFd(string baseDir, string fd, string query, int maxResults, CancellationToken token, int? maxDepth = null)
    {
        var args = new List<string> { "--base-directory", baseDir, "--max-results", maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--type", "f", "--type", "d", "--follow", "--hidden", "--exclude", ".git", "--exclude", ".git/*", "--exclude", ".git/**" };
        if (maxDepth is { } depth) args.AddRange(["--max-depth", depth.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (ToDisplayPath(query).Contains('/')) args.Add("--full-path");
        if (query.Length > 0) args.Add(BuildFdPathQuery(query));
        if (token.IsCancellationRequested) return [];
        try
        {
            var start = new ProcessStartInfo(fd) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null) return [];
            using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            if (token.IsCancellationRequested || process.ExitCode != 0 || stdout.Length == 0) return [];
            var results = new List<(string, bool)>();
            foreach (var line in stdout.Trim().Split('\n').Where(line => line.Length > 0))
            {
                var display = ToDisplayPath(line.TrimEnd('\r'));
                var trailing = display.EndsWith('/');
                var normalized = trailing ? display[..^1] : display;
                if (normalized == ".git" || normalized.StartsWith(".git/", StringComparison.Ordinal) || normalized.Contains("/.git/", StringComparison.Ordinal)) continue;
                results.Add((display, trailing));
            }
            return results;
        }
        catch { return []; }
    }

    public async Task<AutocompleteSuggestions?> GetSuggestionsAsync(IReadOnlyList<string> lines, int cursorLine, int cursorCol, bool force, CancellationToken cancellationToken)
    {
        var currentLine = cursorLine < lines.Count ? lines[cursorLine] : "";
        var before = currentLine[..Math.Min(cursorCol, currentLine.Length)];
        if (ExtractAtPrefix(before) is { } atPrefix)
        {
            var (raw, _, quoted) = ParsePathPrefix(atPrefix);
            var suggestions = await FuzzyFileSuggestions(raw, quoted, cancellationToken).ConfigureAwait(true);
            return suggestions.Count == 0 ? null : new(suggestions, atPrefix);
        }
        var commandText = TextUtils.JsTrimStart(before);
        if (!force && commandText.StartsWith('/'))
        {
            var space = commandText.IndexOf(' ');
            if (space == -1)
            {
                var prefix = commandText[1..];
                var items = commands.Select(command =>
                {
                    var description = command.Description ?? "";
                    var full = command.ArgumentHint is { Length: > 0 } hint ? description.Length > 0 ? hint + " — " + description : hint : description;
                    return (Name: command.Name, Description: full.Length > 0 ? full : null);
                }).ToList();
                var bare = Fuzzy.Filter(items, prefix, item => item.Name.StartsWith("skill:", StringComparison.Ordinal) ? item.Name["skill:".Length..] : item.Name);
                var bareSet = new HashSet<(string, string?)>(bare);
                var fullOnly = Fuzzy.Filter(items.Where(item => item.Name.StartsWith("skill:", StringComparison.Ordinal) && !bareSet.Contains(item)).ToList(), prefix, item => item.Name);
                var filtered = bare.Concat(fullOnly).Select(item => new AutocompleteItem(item.Name, item.Name, item.Description)).ToList();
                return filtered.Count == 0 ? null : new(filtered, commandText);
            }
            var name = commandText[1..space]; var argumentText = commandText[(space + 1)..];
            var command = commands.FirstOrDefault(c => c.Name == name);
            if (command?.GetArgumentCompletions is not { } complete) return null;
            var argumentSuggestions = await complete(argumentText).ConfigureAwait(true);
            return argumentSuggestions is { Count: > 0 } ? new(argumentSuggestions, argumentText) : null;
        }
        if (ExtractPathPrefix(before, force) is not { } pathPrefix) return null;
        var files = FileSuggestions(pathPrefix);
        return files.Count == 0 ? null : new(files, pathPrefix);
    }

    public CompletionResult ApplyCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol, AutocompleteItem item, string prefix)
    {
        var currentLine = cursorLine < lines.Count ? lines[cursorLine] : "";
        var beforePrefix = currentLine[..Math.Max(0, cursorCol - prefix.Length)];
        var afterCursor = currentLine[Math.Min(cursorCol, currentLine.Length)..];
        var quotedPrefix = prefix.StartsWith('"') || prefix.StartsWith("@\"", StringComparison.Ordinal);
        var adjustedAfter = quotedPrefix && item.Value.EndsWith('"') && afterCursor.StartsWith('"') ? afterCursor[1..] : afterCursor;
        var newLines = lines.ToList();
        var isSlashCommand = prefix.StartsWith('/') && TextUtils.JsTrim(beforePrefix).Length == 0 && !prefix[1..].Contains('/');
        if (isSlashCommand)
        {
            newLines[cursorLine] = beforePrefix + "/" + item.Value + " " + adjustedAfter;
            return new(newLines, cursorLine, beforePrefix.Length + item.Value.Length + 2);
        }
        var isDirectory = item.Label.EndsWith('/');
        var cursorOffset = isDirectory && item.Value.EndsWith('"') ? item.Value.Length - 1 : item.Value.Length;
        if (prefix.StartsWith('@'))
        {
            var suffix = isDirectory ? "" : " ";
            newLines[cursorLine] = beforePrefix + item.Value + suffix + adjustedAfter;
            return new(newLines, cursorLine, beforePrefix.Length + cursorOffset + suffix.Length);
        }
        newLines[cursorLine] = beforePrefix + item.Value + adjustedAfter;
        return new(newLines, cursorLine, beforePrefix.Length + cursorOffset);
    }

    private static string? ExtractAtPrefix(string text)
    {
        if (ExtractQuotedPrefix(text) is { } quoted && quoted.StartsWith("@\"", StringComparison.Ordinal)) return quoted;
        var last = FindLastDelimiter(text);
        var token = StripLeadingWrappers(last == -1 ? text : text[(last + 1)..]);
        return token.StartsWith('@') ? token : null;
    }

    private static string? ExtractPathPrefix(string text, bool force)
    {
        if (ExtractQuotedPrefix(text) is { } quoted) return quoted;
        var last = FindLastDelimiter(text);
        var pathPrefix = StripLeadingWrappers(last == -1 ? text : text[(last + 1)..]);
        if (force) return pathPrefix;
        if (pathPrefix.Contains('/') || pathPrefix.StartsWith('.') || pathPrefix.StartsWith("~/", StringComparison.Ordinal)) return pathPrefix;
        if (pathPrefix.Length == 0 && text.Length > 0 && AutocompleteTokens.IsAtTokenStart(text)) return pathPrefix;
        return null;
    }

    private string ExpandHome(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var expanded = Path.Join(Home, path[2..]);
            return path.EndsWith('/') && !expanded.EndsWith('/') && !expanded.EndsWith('\\') ? expanded + "/" : expanded;
        }
        return path == "~" ? Home : path;
    }

    private (string BaseDir, string Query, string DisplayBase)? ResolveScopedFuzzyQuery(string rawQuery)
    {
        var normalized = ToDisplayPath(rawQuery);
        var slash = normalized.LastIndexOf('/');
        if (slash == -1) return null;
        var displayBase = normalized[..(slash + 1)]; var query = normalized[(slash + 1)..];
        var baseDir = displayBase.StartsWith("~/", StringComparison.Ordinal) ? ExpandHome(displayBase) : displayBase.StartsWith('/') ? displayBase : Path.Join(basePath, displayBase);
        return Directory.Exists(baseDir) ? (baseDir, query, displayBase) : null;
    }

    private static string ScopedPathForDisplay(string displayBase, string relative) =>
        displayBase == "/" ? "/" + ToDisplayPath(relative) : ToDisplayPath(displayBase) + ToDisplayPath(relative);

    private static string JsDirname(string path)
    {
        var normalized = path.Replace('\\', '/');
        var trimmed = normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        var index = trimmed.LastIndexOf('/');
        if (index == -1) return ".";
        return index == 0 ? "/" : trimmed[..index];
    }
    private static string JsBasename(string path)
    {
        var normalized = path.Replace('\\', '/');
        var trimmed = normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        var index = trimmed.LastIndexOf('/');
        return index == -1 ? trimmed : trimmed[(index + 1)..];
    }
    /// <summary>Node path.join for display paths ("./" and "x/.." are normalized away).</summary>
    private static string JsJoin(string left, string right)
    {
        var parts = new List<string>();
        foreach (var part in (left + "/" + right).Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(part);
        }
        var joined = string.Join('/', parts);
        return left.StartsWith('/') ? "/" + joined : joined.Length == 0 ? "." : joined;
    }

    private List<AutocompleteItem> FileSuggestions(string prefix)
    {
        try
        {
            var (raw, at, quoted) = ParsePathPrefix(prefix);
            var expanded = raw.StartsWith('~') ? ExpandHome(raw) : raw;
            var absolute = raw.StartsWith('~') || expanded.StartsWith('/') || Path.IsPathRooted(expanded) && expanded.Length > 1 && expanded[1] == ':';
            string searchDir, searchPrefix;
            var rootPrefix = raw is "" or "./" or "../" or "~" or "~/" or "/" || at && raw.Length == 0;
            if (rootPrefix || raw.EndsWith('/')) { searchDir = absolute ? expanded : Path.Join(basePath, expanded); searchPrefix = ""; }
            else
            {
                var dir = Path.GetDirectoryName(expanded) is { Length: > 0 } d ? d : ".";
                searchDir = absolute ? dir : Path.Join(basePath, dir);
                searchPrefix = Path.GetFileName(expanded);
            }
            var suggestions = new List<AutocompleteItem>();
            foreach (var entry in new DirectoryInfo(searchDir).EnumerateFileSystemInfos())
            {
                if (!entry.Name.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                var isDirectory = entry is DirectoryInfo;
                if (!isDirectory && entry.LinkTarget is not null) try { isDirectory = Directory.Exists(entry.FullName); } catch { }
                string relative; var name = entry.Name;
                if (raw.EndsWith('/')) relative = raw + name;
                else if (raw.Contains('/') || raw.Contains('\\'))
                {
                    if (raw.StartsWith("~/", StringComparison.Ordinal)) { var dir = JsDirname(raw[2..]); relative = "~/" + (dir == "." ? name : JsJoin(dir, name)); }
                    else if (raw.StartsWith('/')) { var dir = JsDirname(raw); relative = dir == "/" ? "/" + name : dir + "/" + name; }
                    else
                    {
                        relative = JsJoin(JsDirname(raw), name);
                        if (raw.StartsWith("./", StringComparison.Ordinal) && !relative.StartsWith("./", StringComparison.Ordinal)) relative = "./" + relative;
                    }
                }
                else relative = raw.StartsWith('~') ? "~/" + name : name;
                relative = ToDisplayPath(relative);
                suggestions.Add(new(BuildCompletionValue(isDirectory ? relative + "/" : relative, at, quoted), name + (isDirectory ? "/" : "")));
            }
            return suggestions.OrderBy(item => item.Label.EndsWith('/') ? 0 : 1).ThenBy(item => item.Label, StringComparer.CurrentCulture).ToList();
        }
        catch { return []; }
    }

    private static int ScoreEntry(string filePath, string query, bool isDirectory)
    {
        var fileName = JsBasename(filePath).ToLowerInvariant(); var lowerQuery = query.ToLowerInvariant();
        var score = fileName == lowerQuery ? 100 : fileName.StartsWith(lowerQuery, StringComparison.Ordinal) ? 80 :
            fileName.Contains(lowerQuery, StringComparison.Ordinal) ? 50 : filePath.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal) ? 30 : 0;
        if (isDirectory && score > 0) score += 10;
        return score;
    }

    private async Task<List<AutocompleteItem>> FuzzyFileSuggestions(string query, bool quoted, CancellationToken token)
    {
        if (fdPath is null || token.IsCancellationRequested) return [];
        try
        {
            var scoped = ResolveScopedFuzzyQuery(query);
            var baseDir = scoped?.BaseDir ?? basePath; var fdQuery = scoped?.Query ?? query;
            var baseEntries = await WalkWithFd(baseDir, fdPath, fdQuery, 100, token, 1).ConfigureAwait(true);
            var recursive = await WalkWithFd(baseDir, fdPath, fdQuery, 100, token).ConfigureAwait(true);
            var seen = new HashSet<string>(baseEntries.Select(entry => entry.Path), StringComparer.Ordinal);
            var entries = baseEntries.Concat(recursive.Where(entry => seen.Add(entry.Path))).ToList();
            if (token.IsCancellationRequested) return [];
            var scored = entries.Select(entry => (entry.Path, entry.IsDirectory, Score: fdQuery.Length > 0 ? ScoreEntry(entry.Path, fdQuery, entry.IsDirectory) : 1))
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => ToDisplayPath(entry.Path).Split('/', StringSplitOptions.RemoveEmptyEntries).Length)
                .ThenBy(entry => entry.Path.Length)
                .ThenBy(entry => entry.Path, StringComparer.CurrentCulture).Take(20);
            var suggestions = new List<AutocompleteItem>();
            foreach (var (path, isDirectory, _) in scored)
            {
                var withoutSlash = isDirectory ? path[..^1] : path;
                var display = scoped is { } s ? ScopedPathForDisplay(s.DisplayBase, withoutSlash) : withoutSlash;
                var name = JsBasename(withoutSlash);
                suggestions.Add(new(BuildCompletionValue(isDirectory ? display + "/" : display, true, quoted), name + (isDirectory ? "/" : ""), display));
            }
            return suggestions;
        }
        catch { return []; }
    }

    public bool ShouldTriggerFileCompletion(IReadOnlyList<string> lines, int cursorLine, int cursorCol)
    {
        var currentLine = cursorLine < lines.Count ? lines[cursorLine] : "";
        var trimmed = TextUtils.JsTrim(currentLine[..Math.Min(cursorCol, currentLine.Length)]);
        return !(trimmed.StartsWith('/') && !trimmed.Contains(' '));
    }
}
