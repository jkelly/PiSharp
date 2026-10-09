using System.Text;
using PiSharp.Cli.Interactive.Mode.Utilities;
using static Expect;

/// <summary>The shipped CHANGELOG.md (What's New and /changelog read it) is built from the website's changelog pages in Pi's
/// <c>## [x.y.z] - date</c> format. Set PISHARP_WRITE_CHANGELOG=1 to regenerate it.</summary>
internal static class ChangelogSyncCases
{
    private const string Site = "https://pisharp.ai";

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("changelog.sync.generated-from-website", () =>
        {
            var root = RepoRoot();
            var path = Path.Combine(root, "CHANGELOG.md");
            var expected = Generate(Path.Combine(root, "website", "src", "content", "changelog"));
            if (Environment.GetEnvironmentVariable("PISHARP_WRITE_CHANGELOG") == "1") File.WriteAllText(path, expected, new UTF8Encoding(false));
            Check(File.Exists(path), "CHANGELOG.md exists at the repository root");
            Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal),
                "CHANGELOG.md matches website/src/content/changelog (regenerate with PISHARP_WRITE_CHANGELOG=1)");
            return Task.CompletedTask;
        });

        yield return ("changelog.sync.shipped-next-to-the-executable", () =>
        {
            var shipped = Changelog.GetChangelogPath(_ => null);
            Check(File.Exists(shipped), "CHANGELOG.md is copied to the output directory");
            var entries = Changelog.ParseChangelog(shipped);
            Check(entries.Count >= 3, "entries parsed");
            Equal("## [1.1.0.1] - 2026-10-08", entries.Single(entry => entry is { Major: 1, Minor: 1, Patch: 0, Revision: 1 }).Content.Split('\n')[0],
                "four-part header");
            Seq(["1.1.0.1"], Changelog.GetNewEntries(entries, "1.1.0").Select(Version), "newer than 1.1.0");
            Equal(0, Changelog.GetNewEntries(entries, "1.1.0.1").Count, "nothing newer than 1.1.0.1");
            Seq(["1.1.0.1", "1.1.0"], Changelog.GetNewEntries(entries, "0.99.1").Select(Version), "newer than 0.99.1");
            return Task.CompletedTask;
        });

        yield return ("changelog.parse.four-part-versions", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "pisharp-changelog-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "CHANGELOG.md");
                File.WriteAllText(path, "# Changelog\n\n## [1.1.0.2] - 2026-11-01\n\n- b\n\n## [1.1.0.1]\n\n- a\n\n## [1.1.0]\n\n- base\n");
                var entries = Changelog.ParseChangelog(path);
                Seq(["1.1.0.2", "1.1.0.1", "1.1.0"], entries.Select(Version), "versions");
                Seq(["1.1.0.2"], Changelog.GetNewEntries(entries, "1.1.0.1").Select(Version), "revision compared");
                Check(Changelog.CompareVersions(entries[0], entries[2]) > 0, "1.1.0.2 > 1.1.0");
                Contains(Changelog.NormalizeChangelogLinks("[notes](https://github.com/jkelly/PiSharp/blob/main/docs/x.md)", entries[0]),
                    "https://github.com/jkelly/PiSharp/blob/main/docs/x.md", "PiSharp links kept");
            }
            finally { Directory.Delete(dir, true); }
            return Task.CompletedTask;
        });
    }

    private static void Seq(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");

    private static string Version(ChangelogEntry entry) =>
        entry.Revision > 0 ? $"{entry.Major}.{entry.Minor}.{entry.Patch}.{entry.Revision}" : $"{entry.Major}.{entry.Minor}.{entry.Patch}";

    /// <summary>Every page, newest version first: <c>## [version] - date</c>, then the page body with site-relative links made absolute.</summary>
    internal static string Generate(string directory)
    {
        var pages = Directory.GetFiles(directory, "*.md").Select(Read).OrderByDescending(page => page.Key).ToList();
        var output = new StringBuilder("# Changelog\n");
        foreach (var page in pages)
            output.Append('\n').Append("## [").Append(page.Version).Append(']').Append(page.Date is null ? "" : " - " + page.Date).Append("\n\n")
                .Append(page.Body).Append('\n');
        return output.ToString();
    }

    private static (string Version, string? Date, string Body, Version Key) Read(string file)
    {
        var text = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new InvalidDataException(file + " has no front matter");
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        var fields = text[4..end].Split('\n').Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
        var version = fields["version"];
        var body = text[(end + 5)..].Trim().Replace("](/", "](" + Site + "/", StringComparison.Ordinal);
        return (version, fields.GetValueOrDefault("date"), body, System.Version.Parse(version.Split('.').Length == 3 ? version + ".0" : version));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PiSharp.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found");
    }
}
