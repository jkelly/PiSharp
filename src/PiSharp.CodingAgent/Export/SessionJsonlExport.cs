// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/session-export.ts.
using System.Text;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Export;

/// <summary>Export-only entries appended after the branch: (parentId, timestamp) to JS objects.</summary>
public delegate IReadOnlyList<object?> SessionTrailingEntries(string? parentId, string timestamp);

/// <summary>The current branch as JSONL: a fresh header, the branch re-linked root first, then optional export-only entries.</summary>
public static class SessionJsonlExport
{
    /// <summary>serializeSessionBranch.</summary>
    public static string SerializeSessionBranch(SessionExportSource source, SessionTrailingEntries? createTrailingEntries = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var timestamp = Js.IsoString((clock ?? (() => DateTimeOffset.UtcNow))());
        var header = new JsObject().Set("type", "session").Set("version", (double)SessionEntryCodec.CurrentVersion).Set("id", source.SessionId)
            .Set("timestamp", timestamp).Set("cwd", source.Cwd);
        var entries = new List<object?> { header };
        string? parentId = null;
        foreach (var entry in source.GetBranch())
        {
            var copy = entry.Clone(); copy["parentId"] = parentId; entries.Add(copy);
            parentId = entry["id"] as string;
        }
        entries.AddRange(createTrailingEntries?.Invoke(parentId, timestamp) ?? []);
        return string.Join("\n", entries.Select(Js.Stringify)) + "\n";
    }

    /// <summary>exportSessionToJsonl: resolves the path (default <c>session-&lt;ISO time with : and . as -&gt;.jsonl</c>) against the
    /// working directory, creates its directory, writes the branch and returns the resolved path.</summary>
    public static string ExportSessionToJsonl(SessionExportSource source, string? outputPath = null, SessionTrailingEntries? createTrailingEntries = null,
        Func<DateTimeOffset>? clock = null, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var now = clock ?? (() => DateTimeOffset.UtcNow);
        var filePath = ExportPaths.ResolvePath(outputPath ?? $"session-{Js.IsoString(now()).Replace(':', '-').Replace('.', '-')}.jsonl",
            workingDirectory ?? Environment.CurrentDirectory);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, SerializeSessionBranch(source, createTrailingEntries, now), new UTF8Encoding(false));
        return filePath;
    }
}
