// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (resolveExtensionEntries,
// discoverExtensionsInDir, discoverAndLoadExtensions), packages/coding-agent/src/core/pi-manifest.ts (readPiManifest),
// packages/coding-agent/src/core/package-manager.ts (resolve: settings extension entries and auto-discovered extension folders with
// resourcePrecedenceRank; resolveExtensionSources for -e) and packages/coding-agent/src/core/resource-loader.ts (mergePaths: CLI
// extensions first, then the resolved ones, each path once).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>An extension entry point and where it came from: scope <c>user</c>, <c>project</c> or <c>temporary</c> (CLI).</summary>
internal sealed record PiExtensionSource(string Path, string Scope, string Source);

internal static class PiExtensionDiscovery
{
    /// <summary>Source readPiManifest: the <c>pi</c> object of a package.json and its string-array resource fields, or null.</summary>
    internal static JsonObject? ReadPiManifest(string packageJsonPath)
    {
        try
        {
            if (JsonNode.Parse(PiPaths.ReadText(packageJsonPath)) is not JsonObject package || package["pi"] is not JsonObject pi) return null;
            var manifest = new JsonObject();
            foreach (var field in new[] { "extensions", "skills", "prompts", "themes" })
                if (pi[field] is JsonArray entries && entries.All(entry => entry is JsonValue value && value.GetValueKind() == JsonValueKind.String))
                    manifest[field] = entries.DeepClone();
            return manifest;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { return null; }
    }

    private static bool IsExtensionFile(string name) => name.EndsWith(".ts", StringComparison.Ordinal) || name.EndsWith(".js", StringComparison.Ordinal);

    /// <summary>Source resolveExtensionEntries: package.json <c>pi.extensions</c> entries that exist, else index.ts, else index.js.</summary>
    internal static ImmutableArray<string>? ResolveExtensionEntries(string directory)
    {
        var packageJson = Path.Join(directory, "package.json");
        if (File.Exists(packageJson) && ReadPiManifest(packageJson)?["extensions"] is JsonArray declared && declared.Count > 0)
        {
            var entries = declared.Select(entry => Path.GetFullPath(Path.Combine(directory, entry!.GetValue<string>()))).Where(Path.Exists).ToImmutableArray();
            if (entries.Length > 0) return entries;
        }
        var indexTs = Path.Join(directory, "index.ts"); var indexJs = Path.Join(directory, "index.js");
        if (File.Exists(indexTs)) return [indexTs];
        if (File.Exists(indexJs)) return [indexJs];
        return null;
    }

    /// <summary>Source discoverExtensionsInDir: direct <c>*.ts</c>/<c>*.js</c> files, then one level of subdirectories with an index or
    /// a pi manifest. No deeper recursion.</summary>
    internal static ImmutableArray<string> DiscoverExtensionsInDir(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var discovered = ImmutableArray.CreateBuilder<string>();
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                var isLink = entry.LinkTarget is not null;
                if ((entry is FileInfo || isLink && File.Exists(entry.FullName)) && IsExtensionFile(entry.Name)) { discovered.Add(entry.FullName); continue; }
                if (entry is DirectoryInfo || isLink && Directory.Exists(entry.FullName))
                    if (ResolveExtensionEntries(entry.FullName) is { } entries) discovered.AddRange(entries);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        return discovered.ToImmutable();
    }

    /// <summary>A configured extension path (settings entry or <c>-e</c>): a directory with entries, a directory of extension files, or
    /// a file.</summary>
    internal static ImmutableArray<string> ExpandPath(string resolved)
    {
        if (Directory.Exists(resolved)) return ResolveExtensionEntries(resolved) ?? DiscoverExtensionsInDir(resolved);
        return [resolved];
    }

    /// <summary>
    /// The run's extension entry points in load order: CLI (<c>-e</c>) paths first, then the settings and auto-discovered extensions in
    /// upstream's precedence (project settings, project <c>.pi/extensions</c>, user settings, <c>agentDir/extensions</c>, packages).
    /// Project entries need a trusted project; <c>--no-extensions</c> keeps only the CLI paths. Override patterns (<c>+</c>/<c>-</c>/<c>!</c>)
    /// in the settings disable or force paths as the package manager applies them.
    /// </summary>
    internal static ImmutableArray<PiExtensionSource> Resolve(string cwd, string agentDir, string home, PiSettings settings, bool projectTrusted,
        IReadOnlyList<string> cliPaths, bool noExtensions, IReadOnlyList<PiExtensionSource>? packageExtensions = null)
    {
        var ordered = new List<PiExtensionSource>();
        foreach (var path in cliPaths)
        {
            var resolved = PiPaths.IsLocalPath(path) ? PiPaths.ResolvePath(path, cwd, home, trim: true) : path;
            foreach (var entry in ExpandPath(resolved)) ordered.Add(new(entry, "temporary", "cli"));
        }
        if (!noExtensions)
        {
            var projectBase = Path.Join(cwd, PiConfig.ConfigDirName);
            var disabled = new HashSet<string>(PiPaths.Comparer);
            void Settings(JsonObject layer, string baseDir, string scope)
            {
                foreach (var entry in Strings(layer, "extensions"))
                {
                    if (entry.StartsWith('-') || entry.StartsWith('!')) { disabled.Add(PiPaths.ResolvePath(entry[1..], baseDir, home, trim: true)); continue; }
                    var target = entry.StartsWith('+') ? entry[1..] : entry;
                    if (!PiPaths.IsLocalPath(target)) continue;
                    foreach (var path in ExpandPath(PiPaths.ResolvePath(target, baseDir, home, trim: true))) ordered.Add(new(path, scope, "local"));
                }
            }
            if (projectTrusted)
            {
                Settings(settings.Project, projectBase, "project");
                foreach (var path in DiscoverExtensionsInDir(Path.Join(projectBase, "extensions"))) ordered.Add(new(path, "project", "auto"));
            }
            Settings(settings.Global, agentDir, "user");
            foreach (var path in DiscoverExtensionsInDir(Path.Join(agentDir, "extensions"))) ordered.Add(new(path, "user", "auto"));
            if (packageExtensions is not null) ordered.AddRange(packageExtensions.Where(item => projectTrusted || item.Scope != "project"));
            ordered.RemoveAll(item => item.Scope != "temporary" && disabled.Contains(Path.GetFullPath(item.Path)));
        }
        var seen = new HashSet<string>(PiPaths.Comparer);
        return [.. ordered.Where(item => seen.Add(Path.GetFullPath(item.Path)))];
    }

    private static IEnumerable<string> Strings(JsonObject layer, string name) =>
        layer[name] is JsonArray array ? array.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var text) ? text : null).OfType<string>() : [];
}
