// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/env/src/list-files.ts (`listFilesOver`'s bound validation, abort checks and
// error mapping) and packages/env/daemon/src/list.rs (the walk, its defaults, bounds and truncation). Upstream implements `listFiles` only in
// pi-env (decision 0005 leaves pi-env out), whose LocalExecutionEnv lists through a daemon on the same machine; LocalFileSystem lists in
// process with the daemon's semantics instead.
//
// The daemon walks with the `ignore` crate 0.4.33 (ripgrep's, by Andrew Gallant; dual Unlicense/MIT, THIRD-PARTY-NOTICES.md), configured as
// `WalkBuilder::new(root).hidden(!hidden).ignore(false).parents(gitignore).git_ignore(gitignore).git_exclude(gitignore).git_global(gitignore)
// .require_git(true).follow_links(false).current_dir(root).sort_by_file_name(..).filter_entry(not .git and not excluded)`. This file ports
// what that configuration uses, at crates/ignore and crates/globset of BurntSushi/ripgrep commit 3fce3b5bb0236da2df6d99672afb8a719642eca7
// (the ignore-0.4.33 release): the single-threaded Walk over walkdir (src/walk.rs: depth first, a directory before its entries, siblings
// in byte order of their names, the root followed when it is a link, ignore checks before the entry filter); the per-directory Ignore chain
// (src/dir.rs: `add_parents`, `add_child`, `matched_dir_entry`, `matched_ignore` with its precedence of .gitignore over info/exclude over the
// global file, the innermost match winning, `require_git` with `.git` or `.jj` markers, the scan stopping above the first work tree,
// `resolve_git_commondir` for worktrees, a whitelist overriding the hidden check); `is_hidden_entry` (src/pathutil.rs); Gitignore parsing
// and matching (src/gitignore.rs: `add_line`, the last matching glob winning, directory-only globs, `gitconfig_excludes_path` and
// `parse_excludes_file`); and the glob syntax and its regular expressions (globset src/glob.rs: the Parser, `literal_separator`,
// `backslash_escape`, `allow_unclosed_class`, alternates, `**` forms; src/lib.rs: `Candidate` normalization, dot matching newlines). Globs
// match UTF-8 bytes as globset's byte regexes do: each byte is one character (U+0000-U+00FF) of the string the .NET regex sees, so `?`
// matches one byte. GlobSet's literal, basename, extension, prefix and suffix strategies are optimizations equivalent to these regexes.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Durable.Env.Local;

/// <summary>The daemon's <c>listFiles</c> (pi-env list.rs) over System.IO.</summary>
internal static class LocalFileListing
{
    private const long DefaultMaxEntries = 50_000;
    private const long DefaultMaxBytes = 4 * 1024 * 1024;
    private const long DefaultTimeoutMs = 10_000;
    /// <summary>The daemon's largest response payload (frame.rs <c>MAX_PAYLOAD</c>), which bounds <c>maxBytes</c>.</summary>
    private const long MaxPayload = 16 * 1024 * 1024 - 64 * 1024;
    private const double MaxSafeInteger = 9007199254740991;

    /// <summary><c>listFilesOver</c>'s check: each bound is a non-negative safe integer.</summary>
    public static FileError? Validate(ListFilesOptions? options, string path)
    {
        foreach (var (key, value) in new[] { ("maxEntries", options?.MaxEntries), ("maxBytes", options?.MaxBytes), ("timeoutMs", options?.TimeoutMs) })
        {
            if (value is { } number && (double.IsNaN(number) || double.IsInfinity(number) || Math.Floor(number) != number ||
                Math.Abs(number) > MaxSafeInteger || number < 0))
                return new FileError(FileErrorCode.Invalid, $"{key} must be a non-negative safe integer", path);
        }
        if (options is { Exclude: { IsDefault: false } exclude } && exclude.Any(name => name is null))
            return new FileError(FileErrorCode.Invalid, "invalid field exclude", path);
        return null;
    }

    /// <summary>list.rs <c>list</c>: <paramref name="root"/> depth first, siblings in byte order of their names. Stops with
    /// <see cref="FileListing.Truncated"/> at the first entry beyond a bound; fails with aborted once <paramref name="aborted"/> is true.
    /// Entries that cannot be read are skipped.</summary>
    public static Result<FileListing, FileError> List(string root, ListFilesOptions? options, Func<bool> aborted)
    {
        var hidden = options?.Hidden ?? true;
        var gitignore = options?.Gitignore ?? true;
        var exclude = options is { Exclude.IsDefault: false } ? options.Exclude : [];
        var maxEntries = (long)(options?.MaxEntries ?? DefaultMaxEntries);
        var maxBytes = Math.Min((long)(options?.MaxBytes ?? DefaultMaxBytes), MaxPayload);
        var timeoutMs = (long)(options?.TimeoutMs ?? DefaultTimeoutMs);
        var timeout = timeoutMs >= (long)TimeSpan.MaxValue.TotalMilliseconds ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(timeoutMs);

        // fs::metadata(root), following links.
        switch (StatFollowing(root))
        {
            case null:
                return new FileError(FileErrorCode.NotFound, $"ENOENT: no such file or directory, scandir '{root}'", root);
            case Kind.Denied:
                return new FileError(FileErrorCode.PermissionDenied, $"EACCES: permission denied, scandir '{root}'", root);
            case not Kind.Directory:
                return new FileError(FileErrorCode.NotDirectory, $"ENOTDIR: not a directory, scandir '{root}'", root);
        }

        var walk = new Walk(root, skipHidden: !hidden, gitignore, exclude);
        var started = Stopwatch.StartNew();
        var entries = ImmutableArray.CreateBuilder<FileListEntry>();
        long bytes = 0;
        var truncated = false;
        foreach (var entry in walk.Entries())
        {
            if (aborted()) return new FileError(FileErrorCode.Aborted, "aborted", root);
            var size = Encoding.UTF8.GetByteCount(entry.Path) + 2L;
            if (entries.Count >= maxEntries || bytes + size > maxBytes || started.Elapsed > timeout)
            {
                truncated = true;
                break;
            }
            entries.Add(entry);
            bytes += size;
        }
        return new FileListing(entries.ToImmutable(), truncated);
    }

    /// <summary>What a path is when links are followed (Rust's <c>fs::metadata</c>), or null when it does not exist.</summary>
    private enum Kind { File, Directory, Other, Denied }

    private static Kind? StatFollowing(string path)
    {
        if (NativeFiles.Available && NativeFiles.TryGet(path, follow: true) is { } status)
            return status.Type switch { NativeFileType.Directory => Kind.Directory, NativeFileType.Regular => Kind.File, _ => Kind.Other };
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) return null;
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !target.Exists) return null;
                return target is DirectoryInfo ? Kind.Directory : Kind.File;
            }
            if (NativeFiles.Available) return Kind.Denied; // It exists, but the operating system would not examine it.
            return info is DirectoryInfo ? Kind.Directory : Kind.File;
        }
        catch (UnauthorizedAccessException) { return Kind.Denied; }
        catch (IOException) { return null; }
    }

    /// <summary>Rust's <c>Path::exists</c>: true when metadata, following links, can be read.</summary>
    private static bool ExistsFollowing(string path) => StatFollowing(path) is { } kind && kind != Kind.Denied;

    /// <summary>The walk: ignore's <c>Walk</c> over walkdir with the daemon's options.</summary>
    private sealed class Walk(string root, bool skipHidden, bool gitignore, ImmutableArray<string> exclude)
    {
        public IEnumerable<FileListEntry> Entries()
        {
            // add_parents: the ancestors of the canonical root, outermost first, each with its own ignore files.
            IgnoreNode? chain = null;
            if (gitignore && CanonicalAncestors(root) is var (canonical, ancestors))
            {
                foreach (var ancestor in ancestors)
                {
                    chain = IgnoreNode.ForDirectory(chain, ancestor, gitignore, absoluteParent: true, relativeDirectory: "");
                    chain.HasGit = ExistsFollowing(Path.Join(ancestor, ".git")) || ExistsFollowing(Path.Join(ancestor, ".jj"));
                    chain.FromAncestor = RelativeTo(ancestor, canonical);
                }
            }
            // add_child for the root itself, at depth 0, which is never skipped or listed.
            var rootNode = IgnoreNode.ForDirectory(chain, root, gitignore, absoluteParent: false, relativeDirectory: "");
            foreach (var entry in Visit(rootNode, root, ""))
                yield return entry;

            IEnumerable<FileListEntry> Visit(IgnoreNode node, string directory, string relativeDirectory)
            {
                foreach (var (name, path, kind, isHiddenAttribute) in ReadSorted(directory))
                {
                    var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;
                    var isDirectory = kind == FileKind.Directory;
                    // skip_entry: the ignore rules and the hidden check, then the entry filter.
                    if (ShouldSkip(node, relative, name, isDirectory, isHiddenAttribute)) continue;
                    if (name == ".git" || exclude.Contains(name, StringComparer.Ordinal)) continue;
                    if (kind is null) continue; // Neither a file, a directory nor a link (list.rs skips it after the filters).
                    yield return new FileListEntry(Lossy(relative), kind.Value);
                    if (!isDirectory) continue;
                    var child = IgnoreNode.ForDirectory(node, path, gitignore, absoluteParent: false, relative);
                    foreach (var entry in Visit(child, path, relative))
                        yield return entry;
                }
            }
        }

        /// <summary>matched_dir_entry and should_skip_entry: an ignore match skips, a whitelist match keeps (even a hidden entry), and
        /// otherwise a hidden entry is skipped when hidden entries are.</summary>
        private bool ShouldSkip(IgnoreNode node, string relative, string name, bool isDirectory, bool isHiddenAttribute)
        {
            var match = gitignore ? MatchedIgnore(node, relative, isDirectory) : IgnoreMatch.None;
            if (match == IgnoreMatch.None && skipHidden && IsHidden(name, isHiddenAttribute)) return true;
            return match == IgnoreMatch.Ignore;
        }

        /// <summary>dir.rs <c>matched_ignore</c> for an entry at <paramref name="relative"/> below the root: the innermost .gitignore match,
        /// else the innermost info/exclude match, else the global file's, consulting no directory above the first work tree; none at all
        /// unless the entry is inside a work tree.</summary>
        private IgnoreMatch MatchedIgnore(IgnoreNode node, string relative, bool isDirectory)
        {
            var anyGit = false;
            for (var current = node; current is not null; current = current.Parent) anyGit |= current.HasGit;
            IgnoreMatch gitignoreMatch = IgnoreMatch.None, excludeMatch = IgnoreMatch.None;
            var sawGit = false;
            for (var current = node; current is not null; current = current.Parent)
            {
                // Walk nodes see the path relative to their directory; parents of the root see it relative to theirs, through the
                // canonical root.
                var path = current.AbsoluteParent
                    ? current.FromAncestor.Length == 0 ? relative : current.FromAncestor + "/" + relative
                    : current.RelativeDirectory.Length == 0 ? relative : relative[(current.RelativeDirectory.Length + 1)..];
                if (anyGit && !sawGit && gitignoreMatch == IgnoreMatch.None) gitignoreMatch = current.GitIgnore.MatchedStripped(path, isDirectory);
                if (anyGit && !sawGit && excludeMatch == IgnoreMatch.None) excludeMatch = current.Exclude.MatchedStripped(path, isDirectory);
                sawGit |= current.HasGit;
            }
            var globalMatch = anyGit ? global.MatchedStripped(relative, isDirectory) : IgnoreMatch.None;
            return gitignoreMatch != IgnoreMatch.None ? gitignoreMatch : excludeMatch != IgnoreMatch.None ? excludeMatch : globalMatch;
        }

        /// <summary>IgnoreBuilder::build: the global excludes file, relative to the listed directory (<c>current_dir(root)</c>).</summary>
        private readonly GitignoreMatcher global = gitignore ? GitignoreMatcher.Global(root) : GitignoreMatcher.Empty;

        /// <summary>pathutil.rs <c>is_hidden_entry</c>: a name starting with <c>.</c> (on Unix not one ending with <c>.</c>, which
        /// <c>file_name</c> does not report), or on Windows the hidden attribute of the entry itself.</summary>
        private static bool IsHidden(string name, bool hiddenAttribute)
        {
            if (OperatingSystem.IsWindows()) return hiddenAttribute || name.StartsWith('.');
            return name.StartsWith('.') && !name.EndsWith('.');
        }
    }

    /// <summary>The canonical root (Rust's <c>canonicalize</c>) and its ancestors, outermost first; null when it cannot be resolved.</summary>
    private static (string Canonical, List<string> Ancestors)? CanonicalAncestors(string root)
    {
        string canonical;
        try { canonical = LocalFileSystem.RealPath(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        var ancestors = new List<string>();
        for (var parent = Path.GetDirectoryName(canonical); parent is not null; parent = Path.GetDirectoryName(parent)) ancestors.Add(parent);
        ancestors.Reverse();
        return (canonical, ancestors);
    }

    /// <summary><paramref name="path"/> below <paramref name="ancestor"/>, its components joined with <c>/</c>.</summary>
    private static string RelativeTo(string ancestor, string path)
    {
        var rest = path[ancestor.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? rest.Replace('\\', '/') : rest;
    }

    /// <summary>One directory's entries sorted by name bytes (walkdir with <c>sort_by_file_name</c>); an unreadable directory has none,
    /// and an entry that fails to read ends the directory's list.</summary>
    private static List<(string Name, string Path, FileKind? Kind, bool HiddenAttribute)> ReadSorted(string directory)
    {
        var entries = new List<(string, string, FileKind?, bool)>();
        try
        {
            var enumerable = new FileSystemEnumerable<(string, string, bool, FileAttributes)>(directory,
                (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.ToFullPath(), entry.IsDirectory, entry.Attributes),
                new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false });
            foreach (var (name, path, isDirectory, attributes) in enumerable)
                entries.Add((name, path, EntryKind(path, isDirectory, attributes), (attributes & FileAttributes.Hidden) != 0));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        entries.Sort((left, right) => CompareNames(left.Item1, right.Item1));
        return entries;
    }

    /// <summary>The entry's own type (lstat): a symbolic link or junction (Rust's name-surrogate reparse points), a directory, a regular
    /// file, or null for anything else (FIFOs, sockets, devices).</summary>
    private static FileKind? EntryKind(string path, bool isDirectory, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // On Unix the flag marks symbolic links; on Windows other reparse points (cloud files, deduplicated files) are no links.
            if (!OperatingSystem.IsWindows()) return FileKind.Symlink;
            try
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
                if (info.LinkTarget is not null) return FileKind.Symlink;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsWindows() ? (attributes & FileAttributes.Directory) != 0 : isDirectory) return FileKind.Directory;
        if (OperatingSystem.IsWindows()) return (attributes & FileAttributes.Device) != 0 ? null : FileKind.File;
        return NativeFiles.TryGet(path, follow: false) is { Type: NativeFileType.Other } ? null : FileKind.File;
    }

    /// <summary>Byte order of UTF-8 names (OsStr comparison; on Windows WTF-8, where a lone surrogate sorts as its code point).</summary>
    internal static int CompareNames(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            var a = CodePoint(left, ref i);
            var b = CodePoint(right, ref j);
            if (a != b) return a.CompareTo(b);
        }
        return (left.Length - i).CompareTo(right.Length - j);

        static int CodePoint(string text, ref int index)
        {
            var c = text[index++];
            if (char.IsHighSurrogate(c) && index < text.Length && char.IsLowSurrogate(text[index])) return char.ConvertToUtf32(c, text[index++]);
            return c;
        }
    }

    /// <summary><c>to_string_lossy</c>: lone surrogates of a Windows name become U+FFFD.</summary>
    private static string Lossy(string path)
    {
        foreach (var c in path)
            if (char.IsSurrogate(c)) return Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(path));
        return path;
    }

    /// <summary>One directory's ignore matchers in the chain of a walked directory and its parents (dir.rs <c>IgnoreInner</c>).</summary>
    private sealed class IgnoreNode
    {
        public IgnoreNode? Parent { get; private init; }
        /// <summary>An ancestor of the canonical root (<c>is_absolute_parent</c>).</summary>
        public bool AbsoluteParent { get; private init; }
        /// <summary>For a walked directory, its path below the root with <c>/</c> separators.</summary>
        public string RelativeDirectory { get; private init; } = "";
        /// <summary>For an ancestor, the canonical root below it with <c>/</c> separators.</summary>
        public string FromAncestor { get; set; } = "";
        public GitignoreMatcher GitIgnore { get; private init; } = GitignoreMatcher.Empty;
        public GitignoreMatcher Exclude { get; private init; } = GitignoreMatcher.Empty;
        public bool HasGit { get; set; }

        /// <summary><c>add_child_path</c>: the directory's <c>.gitignore</c>, its work tree's <c>info/exclude</c> and whether it is a work
        /// tree (<c>.git</c> or <c>.jj</c>).</summary>
        public static IgnoreNode ForDirectory(IgnoreNode? parent, string directory, bool gitignore, bool absoluteParent, string relativeDirectory)
        {
            if (!gitignore) return new IgnoreNode { Parent = parent, AbsoluteParent = absoluteParent, RelativeDirectory = relativeDirectory };
            var gitPath = Path.Join(directory, ".git");
            var gitKind = StatFollowing(gitPath);
            if (gitKind == Kind.Denied) gitKind = null;
            var hasJj = ExistsFollowing(Path.Join(directory, ".jj"));
            var exclude = ResolveGitCommonDir(directory, gitKind) is { } gitDirectory
                ? GitignoreMatcher.FromFile(directory, Path.Join(gitDirectory, "info", "exclude"))
                : GitignoreMatcher.Empty;
            return new IgnoreNode
            {
                Parent = parent,
                AbsoluteParent = absoluteParent,
                RelativeDirectory = relativeDirectory,
                GitIgnore = GitignoreMatcher.FromFile(directory, Path.Join(directory, ".gitignore")),
                Exclude = exclude,
                HasGit = gitKind is not null || hasJj,
            };
        }

        /// <summary>dir.rs <c>resolve_git_commondir</c>: <c>.git</c> itself unless it is a file; then the common directory its
        /// <c>gitdir:</c> line names through that directory's <c>commondir</c> file, or none.</summary>
        private static string? ResolveGitCommonDir(string directory, Kind? gitKind)
        {
            var gitPath = Path.Join(directory, ".git");
            if (gitKind != Kind.File) return gitPath;
            try
            {
                if (FirstLine(gitPath) is not { } dotGit || !dotGit.StartsWith("gitdir: ", StringComparison.Ordinal)) return null;
                var realGitDirectory = dotGit["gitdir: ".Length..];
                string? common;
                try { common = FirstLine(Path.Join(realGitDirectory, "commondir")); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
                if (common is null) return null;
                // PathBuf::from of a relative line resolves against the process's directory, as the daemon's would.
                return common.StartsWith('.') ? Path.Join(realGitDirectory, common) : common;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
            {
                return null;
            }
        }

        /// <summary>BufRead's first line: up to the first newline, without it or a CRLF; null for an empty file.</summary>
        private static string? FirstLine(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            var end = Array.IndexOf(bytes, (byte)'\n');
            var line = end < 0 ? bytes.AsSpan() : bytes.AsSpan(0, end);
            if (end >= 0 && line.Length > 0 && line[^1] == '\r') line = line[..^1];
            return GitignoreMatcher.StrictUtf8.GetString(line);
        }
    }
}

/// <summary>The outcome of matching a path against ignore rules (ignore's <c>Match</c>).</summary>
internal enum IgnoreMatch { None, Ignore, Whitelist }

/// <summary>The globs of one gitignore file, relative to its directory (ignore's <c>Gitignore</c>).</summary>
internal sealed class GitignoreMatcher
{
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    private readonly string root;
    private readonly List<Rule> rules;

    private readonly record struct Rule(Regex Regex, bool Whitelist, bool OnlyDirectory);

    private GitignoreMatcher(string root, List<Rule> rules)
    {
        // GitignoreBuilder::new strips a leading ./ from the root.
        this.root = root.StartsWith("./", StringComparison.Ordinal) ? root[2..] : root;
        this.rules = rules;
    }

    public static GitignoreMatcher Empty { get; } = new("", []);

    public bool IsEmpty => rules.Count == 0;

    /// <summary>A matcher of the given gitignore lines (the crate's test helper <c>add_str</c>); a line whose glob does not parse fails.</summary>
    public static GitignoreMatcher FromLines(string root, string text)
    {
        var rules = new List<Rule>();
        foreach (var line in text.Split('\n'))
        {
            var withoutReturn = line.EndsWith('\r') ? line[..^1] : line;
            if (!AddLine(rules, withoutReturn)) throw new ArgumentException("Invalid gitignore glob: " + withoutReturn);
        }
        // str::lines yields no final empty line; an empty line adds nothing anyway.
        return new GitignoreMatcher(root, rules);
    }

    /// <summary><c>create_gitignore</c> of one file: its lines up to the first one that is not UTF-8, lines whose glob does not parse
    /// skipped; empty when the file cannot be read.</summary>
    public static GitignoreMatcher FromFile(string root, string file)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Empty;
        }
        var rules = new List<Rule>();
        var start = 0;
        for (var index = 0; start < bytes.Length; index++)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            var line = bytes.AsSpan(start, (end < 0 ? bytes.Length : end) - start);
            start = end < 0 ? bytes.Length : end + 1;
            if (end >= 0 && line.Length > 0 && line[^1] == '\r') line = line[..^1];
            string text;
            try { text = StrictUtf8.GetString(line); }
            catch (DecoderFallbackException) { break; }
            // Git's handling of a byte-order mark at the start of the file.
            if (index == 0) text = text.TrimStart((char)0xFEFF);
            AddLine(rules, text);
        }
        return rules.Count == 0 ? Empty : new GitignoreMatcher(root, rules);
    }

    /// <summary><c>build_global</c>: the file <c>core.excludesFile</c> names in the git configuration, or the default
    /// <c>$XDG_CONFIG_HOME/git/ignore</c>; relative to <paramref name="root"/>.</summary>
    public static GitignoreMatcher Global(string root)
    {
        var path = GitconfigExcludesPath();
        if (path is null || !File.Exists(path) || Directory.Exists(path)) return Empty;
        return FromFile(root, path);
    }

    /// <summary>Gitignore <c>matched</c>: strips a leading <c>./</c> and this file's directory from <paramref name="path"/> first.</summary>
    public IgnoreMatch Matched(string path, bool isDirectory)
    {
        if (IsEmpty) return IgnoreMatch.None;
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (root != "." && !IsFileName(path) && StripPrefix(root, path) is { } stripped)
            path = stripped.StartsWith('/') ? stripped[1..] : stripped;
        return MatchedStripped(path, isDirectory);
    }

    /// <summary><c>matched_stripped</c>: the last glob that matches decides, skipping directory-only globs for anything else.</summary>
    public IgnoreMatch MatchedStripped(string path, bool isDirectory)
    {
        if (IsEmpty) return IgnoreMatch.None;
        string? candidate = null;
        for (var index = rules.Count - 1; index >= 0; index--)
        {
            var rule = rules[index];
            if (rule.OnlyDirectory && !isDirectory) continue;
            candidate ??= Candidate(path);
            if (rule.Regex.IsMatch(candidate)) return rule.Whitelist ? IgnoreMatch.Whitelist : IgnoreMatch.Ignore;
        }
        return IgnoreMatch.None;
    }

    /// <summary>globset's <c>Candidate</c>: the path's UTF-8 bytes, one character each, with Windows separators made <c>/</c>.</summary>
    private static string Candidate(string path)
    {
        if (OperatingSystem.IsWindows()) path = path.Replace('\\', '/');
        var bytes = Encoding.UTF8.GetBytes(path);
        return string.Create(bytes.Length, bytes, (span, source) => { for (var i = 0; i < source.Length; i++) span[i] = (char)source[i]; });
    }

    private static bool IsFileName(string path) =>
        OperatingSystem.IsWindows() ? path.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(path) : !path.Contains('/');

    /// <summary>pathutil.rs <c>strip_prefix</c>: a byte prefix on Unix, whole components on Windows.</summary>
    private static string? StripPrefix(string prefix, string path)
    {
        if (!OperatingSystem.IsWindows()) return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : null;
        var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');
        var normalizedPath = path.Replace('\\', '/');
        if (normalizedPrefix.Length == 0) return path;
        if (normalizedPath == normalizedPrefix) return "";
        return normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal) ? normalizedPath[(normalizedPrefix.Length + 1)..] : null;
    }

    /// <summary>GitignoreBuilder <c>add_line</c>; false when the glob does not parse (the line adds nothing).</summary>
    private static bool AddLine(List<Rule> rules, string line)
    {
        if (line.StartsWith('#')) return true;
        if (!line.EndsWith("\\ ", StringComparison.Ordinal)) line = TrimEndWhiteSpace(line);
        if (line.Length == 0) return true;
        var whitelist = false;
        var absolute = false;
        if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
        {
            line = line[1..];
            absolute = line.StartsWith('/');
        }
        else
        {
            if (line.StartsWith('!'))
            {
                whitelist = true;
                line = line[1..];
            }
            if (line.StartsWith('/'))
            {
                // A leading slash anchors the glob to the gitignore's directory: wildcards never match a slash.
                line = line[1..];
                absolute = true;
            }
        }
        var onlyDirectory = false;
        if (line.EndsWith('/'))
        {
            onlyDirectory = true;
            line = line[..^1];
            // An escaped trailing slash loses its escape too.
            if (line.EndsWith('\\')) line = line[..^1];
        }
        var actual = line;
        // A glob without a slash matches at any depth.
        if (!absolute && !line.Contains('/') && !(actual.StartsWith("**/", StringComparison.Ordinal) || actual == "**")) actual = "**/" + actual;
        // `dir/**` matches what is inside the directory, not the directory itself.
        if (actual.EndsWith("/**", StringComparison.Ordinal)) actual += "/*";
        if (GlobRegex.Translate(actual) is not { } pattern) return false;
        rules.Add(new Rule(Compile(pattern), whitelist, onlyDirectory));
        return true;
    }

    private static Regex Compile(string pattern)
    {
        if (Compiled.TryGetValue(pattern, out var regex)) return regex;
        if (Compiled.Count > 4096) Compiled.Clear();
        // Byte regexes with dots matching newlines (globset's new_regex); no backtracking, as globset's automata.
        regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
        return Compiled.GetOrAdd(pattern, regex);
    }

    /// <summary>Rust's <c>trim_end</c>: trailing Unicode White_Space.</summary>
    private static string TrimEndWhiteSpace(string text)
    {
        var end = text.Length;
        while (end > 0 && IsWhiteSpace(text[end - 1])) end--;
        return text[..end];

        static bool IsWhiteSpace(char c) => (int)c is (>= 0x09 and <= 0x0D) or 0x20 or 0x85 or 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A)
            or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;
    }

    /// <summary><c>gitconfig_excludes_path</c>: <c>core.excludesFile</c> from <c>$GIT_CONFIG_GLOBAL</c>, <c>~/.gitconfig</c>,
    /// <c>$XDG_CONFIG_HOME/git/config</c> and the system configuration in that order, else <c>$XDG_CONFIG_HOME/git/ignore</c>.</summary>
    internal static string? GitconfigExcludesPath()
    {
        var home = HomeDirectory();
        string? Xdg(string leaf)
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured ? configured
                : home is null ? null : Path.Join(home, ".config");
            return xdg is null ? null : Path.Join(xdg, "git", leaf);
        }
        string? FromConfig(string? path) => path is not null && ReadAll(path) is { } data ? ParseExcludesFile(data, home) : null;
        var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") is { Length: > 0 } configuredGlobal ? configuredGlobal : null;
        var system = Environment.GetEnvironmentVariable("GIT_CONFIG_SYSTEM") is { Length: > 0 } configuredSystem ? configuredSystem : "/etc/gitconfig";
        return FromConfig(global) ?? FromConfig(home is null ? null : Path.Join(home, ".gitconfig")) ?? FromConfig(Xdg("config"))
            ?? FromConfig(system) ?? Xdg("ignore");

        static byte[]? ReadAll(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        }
    }

    private static readonly Regex ExcludesFile = new(
        @"^[\t\n\v\f\r ]*excludesfile[\t\n\v\f\r ]*=[\t\n\v\f\r ]*""?[\t\n\v\f\r ]*([^\t\n\v\f\r ]+?)[\t\n\v\f\r ]*""?[\t\n\v\f\r ]*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary><c>parse_excludes_file</c>: the lazy <c>(?im-u)^\s*excludesfile\s*=\s*"?\s*(\S+?)\s*"?\s*$</c> over the configuration's bytes,
    /// with <c>~</c> replaced by the home directory.</summary>
    internal static string? ParseExcludesFile(byte[] data, string? home)
    {
        var text = string.Create(data.Length, data, (span, source) => { for (var i = 0; i < source.Length; i++) span[i] = (char)source[i]; });
        var match = ExcludesFile.Match(text);
        if (!match.Success) return null;
        var bytes = match.Groups[1].Value.Select(c => (byte)c).ToArray();
        string value;
        try { value = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
        return home is null ? value : value.Replace("~", home, StringComparison.Ordinal);
    }

    /// <summary>Rust's <c>std::env::home_dir</c>.</summary>
    internal static string? HomeDirectory()
    {
        var variable = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (variable is { Length: > 0 }) return variable;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? profile : null;
    }
}

/// <summary>globset's glob syntax with the options gitignore uses (<c>literal_separator</c>, <c>backslash_escape</c>,
/// <c>allow_unclosed_class</c>, case sensitive, no empty alternates), translated to a .NET regex over byte characters.</summary>
internal static class GlobRegex
{
    private abstract record Token;
    private sealed record Literal(int CodePoint) : Token;
    private sealed record Any : Token;
    private sealed record ZeroOrMore : Token;
    private sealed record RecursivePrefix : Token;
    private sealed record RecursiveSuffix : Token;
    private sealed record RecursiveZeroOrMore : Token;
    private sealed record Class(bool Negated, List<(int Start, int End)> Ranges) : Token;
    private sealed record Alternates(List<List<Token>> Branches) : Token;

    /// <summary>The regex of <paramref name="glob"/> (globset <c>to_regex_with</c>), or null when the glob does not parse.</summary>
    public static string? Translate(string glob)
    {
        var tokens = new Parser(glob).Parse();
        if (tokens is null) return null;
        var regex = new StringBuilder(@"\A");
        // The glob `**` alone matches everything.
        if (tokens is [RecursivePrefix]) regex.Append(".*");
        else Append(tokens, regex);
        return regex.Append(@"\z").ToString();
    }

    private static void Append(List<Token> tokens, StringBuilder regex)
    {
        foreach (var token in tokens)
        {
            switch (token)
            {
                case Literal literal: AppendLiteral(literal.CodePoint, regex); break;
                case Any: regex.Append("[^/]"); break;
                case ZeroOrMore: regex.Append("[^/]*"); break;
                case RecursivePrefix: regex.Append("(?:/?|.*/)"); break;
                case RecursiveSuffix: regex.Append("/.*"); break;
                case RecursiveZeroOrMore: regex.Append("(?:/|/.*/)"); break;
                case Class @class:
                    regex.Append('[');
                    if (@class.Negated) regex.Append('^');
                    foreach (var (start, end) in @class.Ranges)
                    {
                        AppendLiteral(start, regex);
                        if (start == end) continue;
                        regex.Append('-');
                        AppendLiteral(end, regex);
                    }
                    regex.Append(']');
                    break;
                case Alternates alternates:
                    var parts = new List<string>();
                    foreach (var branch in alternates.Branches)
                    {
                        var part = new StringBuilder();
                        Append(branch, part);
                        if (part.Length > 0) parts.Add(part.ToString());
                    }
                    if (parts.Count > 0) regex.Append("(?:").Append(string.Join('|', parts)).Append(')');
                    break;
            }
        }
    }

    /// <summary><c>char_to_escaped_literal</c>: the character's UTF-8 bytes, each one character of the byte string.</summary>
    private static void AppendLiteral(int codePoint, StringBuilder regex)
    {
        Span<byte> buffer = stackalloc byte[4];
        var length = new Rune(codePoint).EncodeToUtf8(buffer);
        foreach (var b in buffer[..length])
        {
            if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')) regex.Append((char)b);
            else regex.Append(@"\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static bool IsSeparator(int c) => c == '/' || (OperatingSystem.IsWindows() && c == '\\');

    /// <summary>globset's <c>Parser</c> over the glob's Unicode scalar values.</summary>
    private sealed class Parser
    {
        private readonly int[] chars;
        private int next;
        private int? previous, current;
        private bool foundUnclosedClass;
        private readonly List<int> alternatesStack = [];
        private readonly List<List<Token>> branches = [[]];

        public Parser(string glob)
        {
            var points = new List<int>();
            foreach (var rune in glob.EnumerateRunes()) points.Add(rune.Value);
            chars = [.. points];
        }

        /// <summary>The tokens, or null for an unclosed or unopened alternate, a dangling escape or an invalid range.</summary>
        public List<Token>? Parse()
        {
            while (Bump() is { } c)
            {
                var ok = c switch
                {
                    '?' => Push(new Any()),
                    '*' => ParseStar(),
                    '[' when !foundUnclosedClass => ParseClass(),
                    '{' => PushAlternate(),
                    '}' => PopAlternate(),
                    ',' => ParseComma(),
                    '\\' => ParseBackslash(),
                    _ => Push(new Literal(c)),
                };
                if (!ok) return null;
            }
            return branches.Count == 1 ? branches[0] : null;
        }

        private int? Bump()
        {
            previous = current;
            current = next < chars.Length ? chars[next++] : null;
            return current;
        }

        private int? Peek() => next < chars.Length ? chars[next] : null;

        private bool Push(Token token)
        {
            if (branches.Count == 0) return false;
            branches[^1].Add(token);
            return true;
        }

        private Token Pop()
        {
            var branch = branches[^1];
            var token = branch[^1];
            branch.RemoveAt(branch.Count - 1);
            return token;
        }

        private bool PushAlternate()
        {
            alternatesStack.Add(branches.Count);
            branches.Add([]);
            return true;
        }

        private bool PopAlternate()
        {
            if (alternatesStack.Count == 0) return false;
            var start = alternatesStack[^1];
            alternatesStack.RemoveAt(alternatesStack.Count - 1);
            var alternates = new Alternates(branches.GetRange(start, branches.Count - start));
            branches.RemoveRange(start, branches.Count - start);
            return Push(alternates);
        }

        private bool ParseComma()
        {
            // Outside an alternation a comma is literal; inside it starts the next branch.
            if (alternatesStack.Count == 0) return Push(new Literal(','));
            branches.Add([]);
            return true;
        }

        private bool ParseBackslash() => Bump() is { } c && Push(new Literal(c));

        private bool ParseStar()
        {
            var before = previous;
            if (Peek() != '*') return Push(new ZeroOrMore());
            Bump();
            if (branches[^1].Count == 0)
            {
                if (Peek() is { } following && !IsSeparator(following)) return Push(new ZeroOrMore()) && Push(new ZeroOrMore());
                Push(new RecursivePrefix());
                Bump();
                return true;
            }
            if (!(before is { } separator && IsSeparator(separator)))
            {
                if (branches.Count <= 1 || (before != ',' && before != '{')) return Push(new ZeroOrMore()) && Push(new ZeroOrMore());
            }
            bool isSuffix;
            switch (Peek())
            {
                case null:
                    Bump();
                    isSuffix = true;
                    break;
                case ',' or '}' when branches.Count >= 2:
                    isSuffix = true;
                    break;
                case { } c when IsSeparator(c):
                    Bump();
                    isSuffix = false;
                    break;
                default:
                    return Push(new ZeroOrMore()) && Push(new ZeroOrMore());
            }
            return Pop() switch
            {
                RecursivePrefix => Push(new RecursivePrefix()),
                RecursiveSuffix => Push(new RecursiveSuffix()),
                _ => isSuffix ? Push(new RecursiveSuffix()) : Push(new RecursiveZeroOrMore()),
            };
        }

        private bool ParseClass()
        {
            var (savedNext, savedPrevious, savedCurrent) = (next, previous, current);
            var ranges = new List<(int Start, int End)>();
            var negated = false;
            if (Peek() is '!' or '^')
            {
                Bump();
                negated = true;
            }
            var first = true;
            var inRange = false;
            while (true)
            {
                if (Bump() is not { } c)
                {
                    // An unclosed class is a literal `[`; every later `[` is literal too.
                    (next, previous, current) = (savedNext, savedPrevious, savedCurrent);
                    foundUnclosedClass = true;
                    return Push(new Literal('['));
                }
                if (c == ']')
                {
                    if (!first) break;
                    ranges.Add((']', ']'));
                }
                else if (c == '-')
                {
                    if (first) ranges.Add(('-', '-'));
                    else if (inRange)
                    {
                        if (!Extend('-')) return false;
                        inRange = false;
                    }
                    else inRange = true;
                }
                else
                {
                    if (inRange)
                    {
                        if (!Extend(c)) return false;
                    }
                    else ranges.Add((c, c));
                    inRange = false;
                }
                first = false;
            }
            if (inRange) ranges.Add(('-', '-'));
            return Push(new Class(negated, ranges));

            bool Extend(int end)
            {
                var (start, _) = ranges[^1];
                ranges[^1] = (start, end);
                return end >= start;
            }
        }
    }
}
