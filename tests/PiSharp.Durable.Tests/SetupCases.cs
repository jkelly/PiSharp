using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using PiSharp.Durable;

// Decision 0005 setup: the port is experimental throughout and follows the pinned upstream commit.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SetupCases() =>
    [
        Case("setup.assemblies-are-experimental", () =>
        {
            foreach (var name in new[] { "PiSharp.Durable", "PiSharp.Durable.Sqlite", "PiSharp.Durable.Tools" })
            {
                var assembly = System.Reflection.Assembly.Load(name);
                var marker = assembly.GetCustomAttributes(typeof(ExperimentalAttribute), false).Cast<ExperimentalAttribute>().SingleOrDefault();
                Equal("PISHARP_DURABLE001", marker?.DiagnosticId, name + " experimental diagnostic");
            }
        }),
        Case("setup.port-map-follows-the-pinned-commit", () =>
        {
            using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "compatibility", "durable-port-map.json")));
            var root = map.RootElement;
            // The row-level map remains immutable v1.5.0 evidence. Its current candidate links the separately audited overlay.
            Equal("f9a26365568127461c47a07a37dcb42c140a3ac5", root.GetProperty("upstreamCommit").GetString(), "historical port map commit");
            Equal("1.5.0", root.GetProperty("upstreamVersion").GetString(), "historical port map version");
            var candidate = root.GetProperty("currentImplementation").GetProperty("candidate");
            Equal(DurableUpstream.Commit, candidate.GetProperty("source").GetProperty("commit").GetString(), "current candidate commit");
            Equal("v1.7.0", candidate.GetProperty("source").GetProperty("tag").GetString(), "current candidate tag");
            Equal("compatibility/durable-sync-1.7.0-overlay.json", candidate.GetProperty("overlay").GetString(), "current overlay");
            var statuses = root.GetProperty("statusMeaning").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var upstream = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in root.GetProperty("files").EnumerateArray())
            {
                var path = row.GetProperty("upstream").GetString()!;
                Check(upstream.Add(path), "duplicate upstream file " + path);
                var status = row.GetProperty("status").GetString()!;
                Check(statuses.Contains(status), path + " has an unknown status " + status);
                Check(row.GetProperty("blobSha1").GetString() is { Length: 40 }, path + " has no upstream blob");
                if (status is "excluded" or "skipped") Check(row.TryGetProperty("note", out _), path + " is " + status + " without a reason");
                else Check(row.GetProperty("pisharp").GetString() is { Length: > 0 } && row.GetProperty("phase").GetString() is { Length: > 0 },
                    path + " has no C# target or phase");
            }
            Check(upstream.Contains("packages/durable/docs/spec.md") is false && upstream.Count > 150, "the map lists upstream sources and tests");
        }),

        // Pi v1.6.0 sync: the row-level map stays v1.5.0 evidence, so the files the range added have no row there. Their native locations
        // are tools/DurablePortMap/sync-1.6.0-native-map.json, which the overlay's generator copies into each row as nativeMapping.
        Case("setup.sync160-native-map-names-the-new-durable-files", () =>
        {
            var repository = RepositoryRoot();
            using var native = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tools", "DurablePortMap", "sync-1.6.0-native-map.json")));
            using var overlay = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "compatibility", "durable-sync-1.6.0-overlay.json")));
            Equal("faac585884ae2c013a6b1400f3e9e77f10b34bac", native.RootElement.GetProperty("source").GetProperty("commit").GetString(), "the native map's commit");
            var files = native.RootElement.GetProperty("files");
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in files.EnumerateObject())
            {
                listed.Add(entry.Name);
                var paths = new List<string>();
                foreach (var key in new[] { "pisharp", "tests" })
                    if (entry.Value.TryGetProperty(key, out var list)) paths.AddRange(list.EnumerateArray().Select(item => item.GetString()!));
                Check(paths.Count > 0, entry.Name + " names no native location");
                foreach (var path in paths) Check(File.Exists(Path.Combine(repository, path)), entry.Name + " names a missing file " + path);
            }
            var rows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in overlay.RootElement.GetProperty("rows").EnumerateArray())
            {
                var upstream = row.GetProperty("upstream")[0].GetString()!;
                rows.Add(upstream);
                var mapped = row.TryGetProperty("nativeMapping", out var mapping);
                Equal(listed.Contains(upstream), mapped, upstream + ": the overlay row carries the native map's entry");
                if (mapped) Equal(files.GetProperty(upstream).GetRawText().Replace(" ", "").Replace("\n", ""),
                    mapping.GetRawText().Replace(" ", "").Replace("\n", ""), upstream + ": the same entry");
                // Every Durable source, test or example the range added has a native location.
                if (row.GetProperty("change").GetString() == "A" && upstream.StartsWith("packages/durable/", StringComparison.Ordinal) && upstream.EndsWith(".ts", StringComparison.Ordinal))
                    Check(mapped, upstream + " was added in v1.6.0 and has no native location");
            }
            foreach (var upstream in listed) Check(rows.Contains(upstream), upstream + " is not a file the v1.6.0 range changed");
        }),
    ];
}
