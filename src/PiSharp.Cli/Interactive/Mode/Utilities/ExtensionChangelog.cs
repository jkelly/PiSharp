// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (ExtensionChangelogUpdate,
// getExtensionChangelogUpdates, readPackageJson) and core/source-info.ts (getSourcePackageKey). The changelog parser is PiSharp's
// (Changelog.ParseChangelog/GetNewEntries), which also reads a fourth version part.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>Source ExtensionChangelogUpdate: one package's section under "Extension Updates".</summary>
internal sealed record ExtensionChangelogUpdate(string Name, string Version, string Markdown);

/// <summary>The string <c>name</c> and <c>version</c> of a package.json.</summary>
internal sealed record PackageVersion(string? Name, string? Version);

/// <summary>Extension package changelogs on a fresh interactive launch, like PiSharp's own "What's New".</summary>
internal static partial class ExtensionChangelog
{
    [GeneratedRegex(@"^##\s+")]
    private static partial Regex Heading();

    /// <summary>Source getExtensionChangelogUpdates: nothing for a resumed or continued session. Each package that loaded an extension
    /// and has a changelog counts once (by <see cref="PiSourceInfo.PackageKey"/>). A package seen for the first time records its
    /// <c>package.json</c> version without a notice; a later launch shows the entries newer than the recorded version, each heading
    /// prefixed with the package name, and records the installed version.</summary>
    public static IReadOnlyList<ExtensionChangelogUpdate> GetUpdates(int messageCount, IEnumerable<JsonObject> sourceInfos, InteractiveSettings settings)
    {
        if (messageCount > 0) return [];
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var updates = new List<ExtensionChangelogUpdate>();
        foreach (var sourceInfo in sourceInfos)
        {
            if (Str(sourceInfo["origin"]) != "package" || Str(sourceInfo["baseDir"]) is not { Length: > 0 } baseDir ||
                Str(sourceInfo["changelogPath"]) is not { Length: > 0 } changelogPath) continue;
            var source = Str(sourceInfo["source"]) ?? "";
            var key = PiSourceInfo.PackageKey(source, Str(sourceInfo["scope"]) ?? "");
            if (!seenKeys.Add(key)) continue;

            if (ReadPackageJson(baseDir) is not { Version: { Length: > 0 } version } package) continue;

            var lastVersion = settings.GetExtensionChangelogVersion(key);
            if (string.IsNullOrEmpty(lastVersion))
            {
                // Fresh install: record the version, don't show the changelog.
                settings.SetExtensionChangelogVersion(key, version);
                continue;
            }

            var newEntries = Changelog.GetNewEntries(Changelog.ParseChangelog(changelogPath), lastVersion);
            if (newEntries.Count == 0) continue;
            settings.SetExtensionChangelogVersion(key, version);
            var name = package.Name is { Length: > 0 } packageName ? packageName : source;
            // Each "## [x.y.z]" heading carries the package name so the entries render like PiSharp's own.
            var markdown = PiSharp.Tui.Pi.TextUtils.JsTrim(string.Join("\n\n", newEntries.Select(entry => Heading().Replace(entry.Content, $"## {name} ", 1))));
            updates.Add(new(name, version, markdown));
        }
        return updates;
    }

    /// <summary>Source readPackageJson: the string <c>name</c> and <c>version</c> of a package root's package.json (BOM stripped); null
    /// when it cannot be read or parsed.</summary>
    public static PackageVersion? ReadPackageJson(string packageRoot)
    {
        try
        {
            if (JsonNode.Parse(PiPaths.ReadText(Path.Join(packageRoot, "package.json"))) is not JsonObject package) return new(null, null);
            return new(Str(package["name"]), Str(package["version"]));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
