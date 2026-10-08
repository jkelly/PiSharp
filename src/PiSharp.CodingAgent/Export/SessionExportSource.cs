// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts (SessionManager.open,
// loadEntriesFromFile, parseSessionEntryLine, migrateToCurrentVersion, _buildIndex, getHeader, getEntries, getLeafId, getBranch, getCwd,
// getSessionId, getSessionFile) for the read-only view the exporters use.
using System.Security.Cryptography;
using System.Text;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Export;

/// <summary>
/// The SessionManager state the exporters read: file entries as JSON.parse produced them, the leaf, the session id and cwd. Entry
/// values are <see cref="JsObject"/> trees; the raw stored JSON is preserved through JSON.parse semantics.
/// </summary>
public sealed class SessionExportSource
{
    private readonly List<object?> fileEntries;
    private readonly Dictionary<string, JsObject> byId = new(StringComparer.Ordinal);

    /// <summary>getSessionFile(); null for an in-memory session.</summary>
    public string? SessionFile { get; }
    public string SessionId { get; }
    /// <summary>getCwd(): the resolved session cwd.</summary>
    public string Cwd { get; }
    /// <summary>getLeafId(): a string, null, or <see cref="Js.Undefined"/> when the last entry has no id.</summary>
    public object? LeafId { get; private set; }

    private SessionExportSource(List<object?> entries, string? sessionFile, string sessionId, string cwd, object? leafId, bool buildIndex)
    {
        fileEntries = entries; SessionFile = sessionFile; SessionId = sessionId; Cwd = cwd; LeafId = leafId;
        foreach (var entry in fileEntries)
        {
            if (entry is not JsObject obj || obj["type"] is "session") continue;
            if (obj["id"] is string id) byId[id] = obj;
            if (buildIndex) LeafId = obj["id"];
        }
    }

    /// <summary>getHeader(): the first session entry, or null.</summary>
    public JsObject? Header => fileEntries.OfType<JsObject>().FirstOrDefault(entry => entry["type"] is "session");

    /// <summary>getEntries(): every non-header entry in file order.</summary>
    public IReadOnlyList<object?> Entries => fileEntries.Where(entry => !(entry is JsObject obj && obj["type"] is "session")).ToList();

    /// <summary>getBranch(): from the leaf to the root, returned root first.</summary>
    public IReadOnlyList<JsObject> GetBranch(string? fromId = null)
    {
        var path = new List<JsObject>(); var seen = new HashSet<JsObject>(ReferenceEqualityComparer.Instance);
        var startId = fromId ?? LeafId as string;
        var current = startId is not null && byId.TryGetValue(startId, out var start) ? start : null;
        while (current is not null && seen.Add(current))
        {
            path.Add(current);
            current = Js.Truthy(current["parentId"]) && current["parentId"] is string parent && byId.TryGetValue(parent, out var next) ? next : null;
        }
        path.Reverse();
        return path;
    }

    /// <summary>A captured PiSharp session log: header and entries keep their stored JSON; <paramref name="leafId"/> is the selected leaf.</summary>
    public static SessionExportSource FromLog(SessionEntry header, IEnumerable<SessionEntry> entries, string? leafId, string? sessionFile)
    {
        ArgumentNullException.ThrowIfNull(header); ArgumentNullException.ThrowIfNull(entries);
        var parsedHeader = (JsObject)Js.ParseJson(header.WireBody.ToString())!;
        var list = new List<object?> { parsedHeader };
        list.AddRange(entries.Select(entry => Js.ParseJson(entry.WireBody.ToString())));
        var cwd = parsedHeader["cwd"] is string headerCwd ? ExportPaths.ResolvePath(headerCwd) : ExportPaths.ResolvePath(Environment.CurrentDirectory);
        return new(list, sessionFile, header.Id, cwd, leafId, false);
    }

    /// <summary>
    /// SessionManager.open(path) for export: lines that JSON.parse rejects are skipped, legacy versions are migrated in memory, a file
    /// without a header gets a new one, the leaf is the last entry. Unlike upstream the source file is never rewritten.
    /// </summary>
    public static SessionExportSource Open(string path, string? workingDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        var cwdBase = workingDirectory ?? Environment.CurrentDirectory;
        var resolved = ExportPaths.ResolvePath(path, cwdBase);
        var now = clock ?? (() => DateTimeOffset.UtcNow);
        if (!File.Exists(resolved))
            return New(resolved, ExportPaths.ResolvePath(cwdBase), now(), []);
        var bytes = File.ReadAllBytes(resolved);
        var entries = new List<object?>();
        foreach (var line in new UTF8Encoding(false, false).GetString(bytes).Split('\n'))
        {
            if (JsTrimmedEmpty(line)) continue;
            try { entries.Add(Js.ParseJson(line)); } catch (FormatException) { }
        }
        if (entries.Any(entry => entry is null)) throw new InvalidOperationException("Cannot read properties of null (reading 'type')");
        if (entries.Count == 0)
        {
            if (bytes.Length > 0) throw new InvalidOperationException($"Session file is not a valid {SessionHtmlExport.AppName} session: {resolved}");
            return New(resolved, ExportPaths.ResolvePath(cwdBase), now(), []);
        }
        var header = entries.OfType<JsObject>().FirstOrDefault(entry => entry["type"] is "session");
        var cwd = header?["cwd"] is string headerCwd ? ExportPaths.ResolvePath(headerCwd, cwdBase) : ExportPaths.ResolvePath(cwdBase);
        if (header is null) return New(resolved, cwd, now(), entries);
        Migrate(entries, header);
        return new(entries, resolved, Js.ToJsString(header["id"]), cwd, null, true);
    }

    private static SessionExportSource New(string sessionFile, string cwd, DateTimeOffset now, List<object?> entries)
    {
        var id = UuidV7(now);
        var header = new JsObject().Set("type", "session").Set("version", (double)SessionEntryCodec.CurrentVersion).Set("id", id)
            .Set("timestamp", Js.IsoString(now)).Set("cwd", cwd).Set("parentSession", Js.Undefined);
        return new([header, .. entries], sessionFile, id, cwd, null, true);
    }

    private static bool JsTrimmedEmpty(string line) => line.All(character => char.IsWhiteSpace(character) || character == '\ufeff');

    /// <summary>migrateToCurrentVersion: v1 gains ids and parent links (compaction indices become ids), v2 renames hookMessage to custom.</summary>
    private static void Migrate(List<object?> entries, JsObject header)
    {
        var version = header["version"] is double number ? number : 1;
        if (version >= SessionEntryCodec.CurrentVersion) return;
        if (version < 2)
        {
            object? previous = null;
            foreach (var entry in entries.OfType<JsObject>())
            {
                if (entry["type"] is "session") { entry["version"] = 2d; continue; }
                entry["id"] = Guid.NewGuid().ToString("D")[..8];
                entry["parentId"] = previous; previous = entry["id"];
                if (entry["type"] is "compaction" && entry["firstKeptEntryIndex"] is double index)
                {
                    if (index >= 0 && index == Math.Floor(index) && index < entries.Count && entries[(int)index] is JsObject target && target["type"] is not "session")
                        entry["firstKeptEntryId"] = target["id"];
                    entry.Remove("firstKeptEntryIndex");
                }
            }
        }
        if (version < 3)
            foreach (var entry in entries.OfType<JsObject>())
            {
                if (entry["type"] is "session") { entry["version"] = 3d; continue; }
                if (entry["type"] is "message" && entry["message"] is JsObject message && message["role"] is "hookMessage") message["role"] = "custom";
            }
    }

    /// <summary>uuidv7(): 48-bit Unix milliseconds, version 7, variant 10, random bits.</summary>
    internal static string UuidV7(DateTimeOffset now)
    {
        Span<byte> bytes = stackalloc byte[16]; RandomNumberGenerator.Fill(bytes);
        var ms = now.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++) bytes[i] = (byte)(ms >> (40 - 8 * i));
        bytes[6] = (byte)(0x70 | bytes[6] & 0x0f); bytes[8] = (byte)(0x80 | bytes[8] & 0x3f);
        var hex = Convert.ToHexStringLower(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}

/// <summary>utils/paths.ts normalizePath and resolvePath, and node path.basename/path.resolve as the exporters use them.</summary>
public static class ExportPaths
{
    /// <summary>normalizeWindowsShellPath (win32): /c/x, /mnt/c/x and /cygdrive/c/x become C:\x.</summary>
    public static string NormalizeWindowsShellPath(string filePath)
    {
        if (!filePath.StartsWith('/') || filePath.StartsWith("//", StringComparison.Ordinal) || filePath.Contains('\\')) return filePath;
        var match = System.Text.RegularExpressions.Regex.Match(filePath, @"^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?\z",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) return filePath;
        var suffix = match.Groups[2].Success ? match.Groups[2].Value.Replace('/', '\\') : null;
        return $"{char.ToUpperInvariant(match.Groups[1].Value[0])}:\\{suffix ?? ""}";
    }

    /// <summary>normalizePath(input): shell drive paths on Windows, ~ expansion and file:// URLs; relative paths stay relative.</summary>
    public static string NormalizePath(string input, string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = OperatingSystem.IsWindows() ? NormalizeWindowsShellPath(input) : input;
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (normalized == "~") return home;
        if (normalized.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && normalized.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(home, normalized[2..]);
        if (normalized.StartsWith("file://", StringComparison.Ordinal)) return new Uri(normalized).LocalPath;
        return normalized;
    }

    /// <summary>resolvePath(input, baseDir): normalized, then node path.resolve against the base directory.</summary>
    public static string ResolvePath(string input, string? baseDirectory = null)
    {
        var normalized = NormalizePath(input);
        var baseDir = NormalizePath(baseDirectory ?? Environment.CurrentDirectory);
        return NodeResolve(Path.IsPathRooted(normalized) ? normalized : Path.Combine(Path.GetFullPath(baseDir), normalized));
    }

    private static string NodeResolve(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>path.basename(path, ext).</summary>
    public static string Basename(string path, string extension)
    {
        var name = Path.GetFileName(path.TrimEnd('/', Path.DirectorySeparatorChar));
        return name.Length > extension.Length && name.EndsWith(extension, StringComparison.Ordinal) ? name[..^extension.Length] : name;
    }
}
