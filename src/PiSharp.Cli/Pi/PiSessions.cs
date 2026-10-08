// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-manager.ts (createSessionId,
// assertValidSessionId, getDefaultSessionDir, readSessionHeader, findMostRecentSession, buildSessionInfo, SessionManager.create, open,
// continueRecent, forkFrom, findById, list, listAll) and packages/coding-agent/src/core/session-cwd.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Pi;

internal sealed record PiSessionInfo(string Path, string Id, string Cwd, string? Name, string? ParentSessionPath, DateTimeOffset Created,
    DateTimeOffset Modified, int MessageCount, string FirstMessage, string AllMessagesText);

internal sealed class PiSessionException(string message) : Exception(message);

/// <summary>Source session storage layout: <c>&lt;agentDir&gt;/sessions/--&lt;encoded cwd&gt;--/&lt;timestamp&gt;_&lt;uuidv7&gt;.jsonl</c>.</summary>
internal static partial class PiSessions
{
    internal const int CurrentSessionVersion = 3;
    private const int MaximumHeaderScanBytes = 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    /// <summary>Source assertValidSessionId, with its error text.</summary>
    internal static void AssertValidSessionId(string id)
    {
        if (!SessionIdPattern().IsMatch(id))
            throw new PiSessionException("Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character");
    }

    /// <summary>Source createSessionId: a UUID version 7 (48-bit millisecond time, random rest).</summary>
    internal static string CreateSessionId(DateTimeOffset? now = null)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var milliseconds = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        for (var index = 0; index < 6; index++) bytes[index] = (byte)(milliseconds >> (8 * (5 - index)));
        bytes[6] = (byte)(0x70 | (bytes[6] & 0x0F));
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F));
        var hex = Convert.ToHexStringLower(bytes);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>Source generateId: eight hex characters not used by <paramref name="taken"/>.</summary>
    internal static string GenerateEntryId(Func<string, bool> taken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!taken(id)) return id;
        }
        return Guid.NewGuid().ToString("D");
    }

    /// <summary>JavaScript <c>new Date().toISOString()</c>.</summary>
    internal static string IsoTimestamp(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    /// <summary>The file name stamp: the ISO timestamp with <c>:</c> and <c>.</c> replaced by <c>-</c>.</summary>
    internal static string FileTimestamp(string isoTimestamp) => isoTimestamp.Replace(':', '-').Replace('.', '-');

    /// <summary>Source getDefaultSessionDirPath: the resolved cwd without its leading separator, every <c>/</c>, <c>\</c> and <c>:</c>
    /// replaced by <c>-</c>, wrapped in <c>--</c>.</summary>
    internal static string DefaultSessionDirectoryPath(string cwd, string agentDir)
    {
        var resolved = Path.GetFullPath(cwd);
        var trimmed = resolved.Length > 0 && (resolved[0] == '/' || resolved[0] == '\\') ? resolved[1..] : resolved;
        var safe = "--" + trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-') + "--";
        return Path.Join(Path.GetFullPath(agentDir), "sessions", safe);
    }

    /// <summary>Source getDefaultSessionDir: the path, created when missing.</summary>
    internal static string DefaultSessionDirectory(string cwd, string agentDir)
    {
        var directory = DefaultSessionDirectoryPath(cwd, agentDir);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>A new session file name in <paramref name="directory"/> (source newSession).</summary>
    internal static (string Path, string Id, string Timestamp) NewSessionFile(string directory, string? id = null, DateTimeOffset? now = null)
    {
        var time = now ?? DateTimeOffset.UtcNow;
        var sessionId = id ?? CreateSessionId(time);
        var timestamp = IsoTimestamp(time);
        return (Path.Join(directory, $"{FileTimestamp(timestamp)}_{sessionId}.jsonl"), sessionId, timestamp);
    }

    /// <summary>Source readSessionHeader: the first parsed entry when it is a session header (blank and malformed lines are skipped);
    /// null for a non-header first entry, an unreadable file or a header beyond the 1 MiB scan.</summary>
    internal static JsonObject? ReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
            long scanned = 0;
            while (reader.ReadLine() is { } line)
            {
                scanned += Encoding.UTF8.GetByteCount(line) + 1;
                if (scanned > MaximumHeaderScanBytes + 1) return null;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
                return node is JsonObject entry && Text(entry, "type") == "session" && entry["id"] is JsonValue id && id.GetValueKind() == JsonValueKind.String ? entry : null;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? Text(JsonObject entry, string name) => entry[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static bool CwdMatches(string? cwd, string resolvedCwd) => !string.IsNullOrEmpty(cwd) && Path.GetFullPath(cwd) == resolvedCwd;

    /// <summary>Source findMostRecentSession: the newest <c>.jsonl</c> by modification time with a valid header (matching the cwd when one is given).</summary>
    internal static string? FindMostRecentSession(string sessionDir, string? cwd)
    {
        var resolvedCwd = cwd is null ? null : Path.GetFullPath(cwd);
        try
        {
            foreach (var path in Directory.EnumerateFiles(sessionDir, "*.jsonl").Select(path => (path, File.GetLastWriteTimeUtc(path))).OrderByDescending(item => item.Item2).Select(item => item.path))
            {
                var header = ReadHeader(path);
                if (header is not null && (resolvedCwd is null || CwdMatches(Text(header, "cwd"), resolvedCwd))) return path;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool FilterCwd(string? sessionDir, string dir, string cwd, string agentDir) =>
        sessionDir is not null && !PiPaths.Comparer.Equals(dir, DefaultSessionDirectoryPath(cwd, agentDir));

    private static string Directory_(string cwd, string? sessionDir, string agentDir) =>
        sessionDir is not null ? sessionDir : DefaultSessionDirectory(cwd, agentDir);

    /// <summary>Source SessionManager.findById: an exact header id in the cwd's session directory.</summary>
    internal static string? FindById(string cwd, string id, string? sessionDir, string agentDir)
    {
        var dir = Directory_(cwd, sessionDir, agentDir);
        var filter = FilterCwd(sessionDir, dir, cwd, agentDir); var resolvedCwd = Path.GetFullPath(cwd);
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                var header = ReadHeader(path);
                if (header is null || Text(header, "id") != id) continue;
                if (filter && !CwdMatches(Text(header, "cwd"), resolvedCwd)) continue;
                return path;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Source SessionManager.continueRecent's choice: the most recent session file, or null for a new session.</summary>
    internal static string? ContinueRecent(string cwd, string? sessionDir, string agentDir)
    {
        var dir = Directory_(cwd, sessionDir, agentDir);
        return FindMostRecentSession(dir, FilterCwd(sessionDir, dir, cwd, agentDir) ? cwd : null);
    }

    /// <summary>Source SessionManager.list: the cwd's sessions, newest activity first.</summary>
    internal static ImmutableArray<PiSessionInfo> List(string cwd, string? sessionDir, string agentDir)
    {
        var dir = Directory_(cwd, sessionDir, agentDir);
        var filter = FilterCwd(sessionDir, dir, cwd, agentDir); var resolvedCwd = Path.GetFullPath(cwd);
        return Sort(ListDirectory(dir).Where(info => !filter || CwdMatches(info.Cwd, resolvedCwd)));
    }

    /// <summary>Source SessionManager.listAll: every project's sessions under the sessions directory, or one custom directory.</summary>
    internal static ImmutableArray<PiSessionInfo> ListAll(string? sessionDir, string agentDir)
    {
        if (sessionDir is not null) return Sort(ListDirectory(sessionDir));
        var root = Path.Join(agentDir, "sessions");
        if (!Directory.Exists(root)) return [];
        try { return Sort(Directory.EnumerateDirectories(root).SelectMany(ListDirectory)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static ImmutableArray<PiSessionInfo> Sort(IEnumerable<PiSessionInfo> sessions) =>
        [.. sessions.OrderByDescending(info => info.Modified)];

    private static IEnumerable<PiSessionInfo> ListDirectory(string directory)
    {
        string[] files;
        try { files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.jsonl") : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { files = []; }
        foreach (var file in files) if (BuildSessionInfo(file) is { } info) yield return info;
    }

    /// <summary>Source buildSessionInfo.</summary>
    internal static PiSessionInfo? BuildSessionInfo(string path)
    {
        try
        {
            JsonObject? header = null; string? name = null; var count = 0; var first = ""; var all = new List<string>(); long? lastActivity = null;
            foreach (var line in File.ReadLines(path, new UTF8Encoding(false, false)))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonObject? entry;
                try { entry = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
                if (entry is null) continue;
                if (header is null) { if (Text(entry, "type") != "session") return null; header = entry; continue; }
                if (Text(entry, "type") == "session_info") name = Text(entry, "name") is { } raw && PiArgs.JsTrim(raw).Length > 0 ? PiArgs.JsTrim(raw) : null;
                if (Text(entry, "type") != "message" || entry["message"] is not JsonObject message) continue;
                count++;
                var role = Text(message, "role");
                if (role is not ("user" or "assistant") || !message.ContainsKey("content")) continue;
                if (message["timestamp"] is JsonValue stamp && stamp.GetValueKind() == JsonValueKind.Number) lastActivity = Math.Max(lastActivity ?? 0, (long)stamp.GetValue<double>());
                else if (Text(entry, "timestamp") is { } entryTime && DateTimeOffset.TryParse(entryTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    lastActivity = Math.Max(lastActivity ?? 0, parsed.ToUnixTimeMilliseconds());
                var text = message["content"] switch
                {
                    JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
                    JsonArray blocks => string.Join(" ", blocks.OfType<JsonObject>().Where(block => Text(block, "type") == "text").Select(block => Text(block, "text") ?? "")),
                    _ => ""
                };
                if (text.Length == 0) continue;
                all.Add(text);
                if (first.Length == 0 && role == "user") first = text;
            }
            if (header is null) return null;
            var headerTime = DateTimeOffset.TryParse(Text(header, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : (DateTimeOffset?)null;
            var modified = lastActivity is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastActivity.Value) : headerTime ?? File.GetLastWriteTimeUtc(path);
            return new(path, Text(header, "id")!, Text(header, "cwd") ?? "", name, Text(header, "parentSession"), headerTime ?? modified, modified, count,
                first.Length == 0 ? "(no messages)" : first, string.Join(" ", all));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Source SessionManager.forkFrom: a new session in the target cwd's directory whose header names the source as parent,
    /// followed by every non-header entry of the source. Returns the new file.</summary>
    internal static string ForkFrom(string sourcePath, string targetCwd, string? sessionDir, string agentDir, string? id = null, DateTimeOffset? now = null)
    {
        var source = Path.GetFullPath(sourcePath); var target = Path.GetFullPath(targetCwd);
        var lines = File.Exists(source) ? File.ReadAllLines(source, new UTF8Encoding(false, false)) : [];
        var entries = new List<JsonObject>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonNode.Parse(line) is JsonObject entry) entries.Add(entry); } catch (JsonException) { }
        }
        if (entries.Count == 0 || Text(entries[0], "type") != "session" || entries[0]["id"] is not JsonValue)
            throw new PiSessionException($"Cannot fork: source session file is empty or invalid: {source}");
        var dir = sessionDir ?? DefaultSessionDirectory(target, agentDir);
        Directory.CreateDirectory(dir);
        if (id is not null) AssertValidSessionId(id);
        var (file, sessionId, timestamp) = NewSessionFile(dir, id, now);
        var header = new JsonObject
        {
            ["type"] = "session", ["version"] = CurrentSessionVersion, ["id"] = sessionId, ["timestamp"] = timestamp,
            ["cwd"] = target, ["parentSession"] = source
        };
        var text = new StringBuilder(PiJson.Stringify(header)).Append('\n');
        foreach (var entry in entries.Where(entry => Text(entry, "type") != "session")) text.Append(PiJson.Stringify(entry)).Append('\n');
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text.ToString());
        return file;
    }

    /// <summary>Source formatMissingSessionCwdError.</summary>
    internal static string MissingCwdError(string sessionCwd, string? sessionFile, string fallbackCwd) =>
        $"Stored session working directory does not exist: {sessionCwd}{(sessionFile is null ? "" : $"\nSession file: {sessionFile}")}\nCurrent working directory: {fallbackCwd}";

    /// <summary>Source formatMissingSessionCwdPrompt (IMPL-I shows it with Continue and Cancel).</summary>
    internal static string MissingCwdPrompt(string sessionCwd, string fallbackCwd) =>
        $"cwd from session file does not exist\n{sessionCwd}\n\ncontinue in current cwd\n{fallbackCwd}";
}
