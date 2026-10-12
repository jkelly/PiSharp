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
            Equal(DurableUpstream.Commit, root.GetProperty("upstreamCommit").GetString(), "port map commit");
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
    ];
}
