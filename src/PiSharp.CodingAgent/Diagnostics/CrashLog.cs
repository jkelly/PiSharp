// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/crash-log.ts.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.CodingAgent.Usage;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary>A loaded extension as the diagnostics modules see it: <c>Extension.path</c>, <c>resolvedPath</c>, <c>hidden</c> and its
/// <c>sourceInfo</c> (<c>source</c>, <c>scope</c> "user"|"project"|"temporary", <c>origin</c> "package"|"top-level", <c>baseDir</c>).</summary>
public sealed record DiagnosticExtensionInfo(string Path, string ResolvedPath, string Source, string Scope, string Origin,
    string? BaseDir = null, bool Hidden = false);

/// <summary>One <c>crashes.json</c> record. The record keeps the stored JSON object as read, so rewriting the log (marking
/// records notified) preserves fields this version does not know, as the source does.</summary>
public sealed class CrashRecord
{
    public const string UncaughtException = "uncaught_exception", FatalError = "fatal_error";
    private readonly JsonObject _json;

    internal CrashRecord(JsonObject json) => _json = json;

    public string Timestamp => (string)_json["timestamp"]!;
    public string Message => (string)_json["message"]!;
    public string? Version => Text("version");
    /// <summary><c>"uncaught_exception"</c> or <c>"fatal_error"</c>.</summary>
    public string? Kind => Text("kind");
    public string? Stack => Text("stack");
    public string? SessionFile => Text("sessionFile");
    public string? Cwd => Text("cwd");
    /// <summary>JavaScript truthiness of the stored <c>notified</c> field.</summary>
    public bool Notified => _json["notified"] switch
    {
        null => false,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => ((string)value!).Length != 0,
            JsonValueKind.Number => value.GetValue<double>() is not (0 or double.NaN),
            _ => false
        },
        _ => true
    };

    /// <summary>The stored record, key order included.</summary>
    public JsonData ToJson() => JsonData.Parse(_json.ToJsonString());

    internal JsonObject Node => _json;

    private string? Text(string name) => _json[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? (string)value! : null;
}

public static class CrashLog
{
    public const string FileName = "crashes.json";
    public const int MaxCrashRecords = 5;
    public const double MaxAgeMs = 7 * 24 * 60 * 60 * 1000;
    /// <summary>config.ts <c>APP_NAME</c>, used by the crash texts.</summary>
    public const string AppName = "pi";

    /// <summary><c>join(getAgentDir(), "crashes.json")</c>.</summary>
    public static string CrashLogPath(string agentDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentDirectory);
        return Path.Combine(agentDirectory, FileName);
    }

    /// <summary>The stored records that are objects with string <c>timestamp</c> and <c>message</c>; empty when the file is
    /// missing, unreadable or not a JSON array.</summary>
    public static IReadOnlyList<CrashRecord> ReadCrashLog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var records = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8));
            return records is JsonArray array
                ? [.. array.OfType<JsonObject>().Where(record => IsString(record["timestamp"]) && IsString(record["message"]))
                    .Select(record => new CrashRecord((JsonObject)record.DeepClone()))]
                : [];
        }
        catch (Exception) { return []; }
        static bool IsString(JsonNode? value) => value is JsonValue text && text.GetValueKind() == JsonValueKind.String;
    }

    private static void WriteCrashLog(IEnumerable<CrashRecord> records, string path)
    {
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        var array = new JsonArray([.. records.Select(record => (JsonNode)record.Node.DeepClone())]);
        File.WriteAllText(path, JsJson.Stringify(array, 2) + "\n", new UTF8Encoding(false));
    }

    /// <summary>Best-effort persistence for callers that are already crashing: appends the record and keeps the newest
    /// <see cref="MaxCrashRecords"/>. Returns null when nothing was written. A .NET exception's message is
    /// <see cref="Exception.Message"/> (its type name when empty) and its stack is <see cref="Exception.ToString"/>, which
    /// starts with the type and message as a JavaScript <c>error.stack</c> does; any other value is stringified.</summary>
    public static CrashRecord? RecordCrash(string kind, object? error, string? sessionFile, string cwd, string path, string version = "1.1.0",
        TimeProvider? timeProvider = null)
    {
        try
        {
            var record = new JsonObject
            {
                ["timestamp"] = JsDate.ToIsoString((timeProvider ?? TimeProvider.System).GetUtcNow()),
                ["version"] = version,
                ["kind"] = kind,
                ["message"] = error is Exception exception ? (string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message)
                    : error is null ? "null" : error.ToString() ?? "",
                ["stack"] = error is Exception thrown ? thrown.ToString() : null,
                ["sessionFile"] = sessionFile,
                ["cwd"] = cwd,
            };
            var created = new CrashRecord(record);
            WriteCrashLog(ReadCrashLog(path).Append(created).TakeLast(MaxCrashRecords), path);
            return created;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Return the newest crash within <see cref="MaxAgeMs"/> that was not announced yet, marking every pending record
    /// as notified. <paramref name="now"/> is epoch milliseconds (default: the system clock).</summary>
    public static CrashRecord? TakeUnnotifiedCrash(string path, double? now = null)
    {
        var records = ReadCrashLog(path);
        var current = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var crash = records.Reverse().FirstOrDefault(record => !record.Notified && current - JsDate.Parse(record.Timestamp) <= MaxAgeMs);
        if (crash is null) return null;
        try
        {
            WriteCrashLog(records.Select(record =>
            {
                if (record.Notified) return record;
                var copy = (JsonObject)record.Node.DeepClone(); copy["notified"] = true; return new CrashRecord(copy);
            }), path);
        }
        catch (Exception) { /* Showing the notice again is harmless. */ }
        return crash;
    }

    public static void ClearCrashLog(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* The records can be attached again if cleanup fails. */ }
    }

    /// <summary>Find loaded extensions with source files in a stack trace. Only frame lines (<c>at ...</c>) after the first line
    /// are searched, URI-decoded independently, with backslashes as slashes; Windows drive paths compare case-insensitively.</summary>
    public static IReadOnlyList<string> FindExtensionStackMatches(string? stack, IEnumerable<DiagnosticExtensionInfo> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (string.IsNullOrEmpty(stack)) return [];
        var normalizedStack = string.Join("\n", stack.Split('\n').Skip(1).Where(line => FrameLine.IsMatch(line))
            .Select(line => { try { return DecodeUri(line); } catch (FormatException) { return line; } })).Replace('\\', '/');
        var matches = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            var resolvedPath = NormalizeStackPath(extension.ResolvedPath);
            var singleFilePackage = extension.Origin == "package" && !RemoteSource.IsMatch(extension.Source) && ScriptFile.IsMatch(extension.Source);
            var packageRoot = extension.Origin == "package" && !singleFilePackage && !string.IsNullOrEmpty(extension.BaseDir) ? extension.BaseDir : null;
            var slashIndex = resolvedPath.LastIndexOf('/');
            var directoryEntry = IndexFile.IsMatch(resolvedPath);
            var matched = packageRoot is not null ? StackContainsPath(normalizedStack, packageRoot, true)
                : directoryEntry && slashIndex != -1 ? StackContainsPath(normalizedStack, resolvedPath[..slashIndex], true)
                : StackContainsPath(normalizedStack, resolvedPath, false);
            if (!matched) continue;
            var label = extension.Origin == "package" && !string.IsNullOrEmpty(extension.Source) ? extension.Source : extension.Path;
            if (seen.Add(label)) matches.Add(label);
        }
        return matches;
    }

    /// <summary>interactive-mode.ts <c>formatCrashExtensionHint</c>: the warning shown under a crash when frames came from
    /// loaded extensions; null when none did.</summary>
    public static string? FormatCrashExtensionHint(IReadOnlyList<string>? extensionMatches)
    {
        var matches = (extensionMatches ?? []).Where(match => !string.IsNullOrEmpty(match)).ToList();
        if (matches.Count == 0) return null;
        var quoted = matches.Select(match => $"`{match}`").ToList();
        var labels = quoted.Count == 1 ? quoted[0] : quoted.Count == 2 ? string.Join(" and ", quoted)
            : $"{string.Join(", ", quoted.Take(quoted.Count - 1))}, and {quoted[^1]}";
        var noun = matches.Count == 1 ? "extension" : "extensions";
        var pronoun = matches.Count == 1 ? "it" : "them";
        return $"A stack frame came from loaded {noun} {labels}, which may be involved. Try disabling {pronoun} with `{AppName} config`, or run `{AppName} -ne` to confirm.";
    }

    /// <summary>interactive-mode.ts <c>crashReportInstructions</c>.</summary>
    public static string CrashReportInstructions(string? sessionFile)
    {
        var resume = !string.IsNullOrEmpty(sessionFile) ? $"run `{AppName} -r` to resume the session, then" : "start pi and";
        return $"To report this crash: {resume} run /bug. The crash details are attached automatically.";
    }

    /// <summary>interactive-mode.ts startup notice for <see cref="TakeUnnotifiedCrash"/>; <paramref name="when"/> is the host's
    /// <c>new Date(crash.timestamp).toLocaleString()</c>.</summary>
    public static string CrashNotice(CrashRecord crash, string when)
    {
        ArgumentNullException.ThrowIfNull(crash);
        return $"{AppName} crashed on {when} ({crash.Message}). Run /bug to report it; the crash details are attached automatically.";
    }

    private const string JsSpace = @"[\t\n\v\f\r\p{Zs}\p{Zl}\p{Zp}" + "\xFEFF" + "]";
    private static readonly Regex FrameLine = new("^" + JsSpace + "+at" + JsSpace, RegexOptions.CultureInvariant);
    private static readonly Regex RemoteSource = new(@"^(?:npm:|git:|https?://|ssh://)", RegexOptions.CultureInvariant);
    private static readonly Regex ScriptFile = new(@"\.[cm]?[jt]s$", RegexOptions.CultureInvariant);
    private static readonly Regex IndexFile = new(@"/index\.[cm]?[jt]s$", RegexOptions.CultureInvariant);
    private static readonly Regex DrivePath = new(@"^[a-z]:/", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string NormalizeStackPath(string value) => Regex.Replace(value.Replace('\\', '/'), "/+$", "", RegexOptions.CultureInvariant);

    private static bool StackContainsPath(string stack, string targetPath, bool includeDescendants)
    {
        var target = NormalizeStackPath(targetPath);
        if (target.Length == 0 || IsSyntheticPath(target)) return false;
        var caseInsensitive = DrivePath.IsMatch(target);
        var haystack = caseInsensitive ? stack.ToLowerInvariant() : stack;
        var needle = caseInsensitive ? target.ToLowerInvariant() : target;
        if (includeDescendants) return haystack.Contains(needle + "/", StringComparison.Ordinal);
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index != -1)
        {
            var end = index + needle.Length;
            if (end >= haystack.Length || haystack[end] is ':' or ')' || JsJson.IsWhiteSpace(haystack[end])) return true;
            index = haystack.IndexOf(needle, end, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>source-info.ts <c>isSyntheticPath</c>: <c>builtin:</c> paths and angle-bracket paths name no file.</summary>
    internal static bool IsSyntheticPath(string path) => path.StartsWith("builtin:", StringComparison.Ordinal) || path.StartsWith('<');

    /// <summary>ECMAScript <c>decodeURI</c>: escapes of the reserved set <c>;/?:@&amp;=+$,#</c> stay encoded. Throws
    /// <see cref="FormatException"/> where <c>decodeURI</c> throws <c>URIError</c>.</summary>
    internal static string DecodeUri(string value)
    {
        if (!value.Contains('%')) return value;
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') { output.Append(value[index]); continue; }
            var start = index; var first = Byte(value, index); index += 2;
            if (first < 0x80)
            {
                var character = (char)first;
                if (";/?:@&=+$,#".Contains(character)) output.Append(value, start, 3); else output.Append(character);
                continue;
            }
            var length = (first & 0xE0) == 0xC0 ? 2 : (first & 0xF0) == 0xE0 ? 3 : (first & 0xF8) == 0xF0 ? 4 : throw new FormatException();
            var codePoint = first & (length == 2 ? 0x1F : length == 3 ? 0x0F : 0x07);
            for (var continuation = 1; continuation < length; continuation++)
            {
                index++;
                if (index >= value.Length || value[index] != '%') throw new FormatException();
                var next = Byte(value, index); index += 2;
                if ((next & 0xC0) != 0x80) throw new FormatException();
                codePoint = (codePoint << 6) | (next & 0x3F);
            }
            var minimum = length == 2 ? 0x80 : length == 3 ? 0x800 : 0x10000;
            if (codePoint < minimum || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF) throw new FormatException();
            output.Append(char.ConvertFromUtf32(codePoint));
        }
        return output.ToString();

        static int Byte(string text, int at) => at + 2 < text.Length && Uri.IsHexDigit(text[at + 1]) && Uri.IsHexDigit(text[at + 2])
            ? Convert.ToInt32(text.Substring(at + 1, 2), 16) : throw new FormatException();
    }
}
