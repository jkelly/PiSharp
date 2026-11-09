using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Chord.Hosting.Plugins;

/// <summary>A native C# facet, or a contracts-only project when Type is omitted.</summary>
public sealed record PluginManifest(string Name, string Assembly, string? Type = null, string? Project = null);
public sealed record NativePlugin(PluginManifest Manifest, string Directory);
public sealed record DiscoveryProblem(string Path, string Error);
public sealed record PluginDiscovery(IReadOnlyList<NativePlugin> Plugins, IReadOnlyList<DiscoveryProblem> Problems);

/// <summary>Reads manifests without evaluating plugin code. Roots are searched in order; first name wins.</summary>
public static partial class PluginScanner
{
    public const string ManifestFile = "chord.plugin.json";
    [GeneratedRegex("^(?:@[a-z0-9~-][a-z0-9._~-]*/)?[a-z0-9~-][a-z0-9._~-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Names();
    public static bool IsPluginName(string name) => Names().IsMatch(name);

    public static async Task<PluginDiscovery> DiscoverAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, NativePlugin>(StringComparer.Ordinal);
        var problems = new List<DiscoveryProblem>();
        foreach (var root in roots)
        {
            if (!System.IO.Directory.Exists(root)) continue;
            foreach (var entry in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(entry).StartsWith('.') || Path.GetFileName(entry) is "node_modules" or "bin" or "obj") continue;
                var directory = new DirectoryInfo(entry).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(entry);
                try
                {
                    var json = await File.ReadAllTextAsync(Path.Combine(directory, ManifestFile), cancellationToken).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                        ?? throw new InvalidDataException("empty manifest");
                    if (string.IsNullOrEmpty(manifest.Name) || !IsPluginName(manifest.Name)) throw new InvalidDataException("invalid plugin name");
                    if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetExtension(manifest.Assembly) != ".dll") throw new InvalidDataException("assembly must name a .dll");
                    ContainedPath(directory, manifest.Assembly);
                    if (manifest.Project is { } project) { ContainedPath(directory, project); if (Path.GetExtension(project) != ".csproj") throw new InvalidDataException("project must name a .csproj"); }
                    if (!found.TryAdd(manifest.Name, new(manifest, directory))) throw new InvalidDataException($"plugin '{manifest.Name}' already discovered in {found[manifest.Name].Directory}");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
                { problems.Add(new(directory, error.Message)); }
            }
        }
        return new(found.Values.OrderBy(x => x.Manifest.Name, StringComparer.Ordinal).ToArray(), problems);
    }

    /// <summary>Resolves a plugin-relative data file, rejecting lexical escapes and symlink escapes.</summary>
    public static string ContainedPath(string directory, string relative)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Inside(root, full)) throw new ArgumentException("plugin path escapes its directory", nameof(relative));
        var current = root;
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo item = System.IO.Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.Exists && item.LinkTarget is not null)
            {
                current = item.ResolveLinkTarget(true)!.FullName;
                if (!Inside(root, current)) throw new ArgumentException("plugin path follows a link outside its directory", nameof(relative));
            }
        }
        return full;
    }
    private static bool Inside(string root, string path)
    { var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; return string.Equals(root, path, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison); }
}
