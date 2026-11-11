using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Chord.Hosting.Plugins;

/// <summary>A native C# facet, or a contracts-only project when Type is omitted.</summary>
public sealed record PluginManifest(string Name, string Assembly, string? Type = null, string? Project = null,
    string? Api = null, string? ApiVersion = null, string? Kind = null);
public sealed record PluginRoot(string Path, bool Required = false);
public sealed record NativePlugin(PluginManifest Manifest, string Directory, string? Root = null, bool Required = false, string Layout = "directory");
public sealed record DiscoveryProblem(string Path, string Error, string? Root = null, bool Required = false, string Layout = "directory");
public sealed record PluginDiscovery(IReadOnlyList<NativePlugin> Plugins, IReadOnlyList<DiscoveryProblem> Problems);
public sealed class PluginError(string message) : Exception(message);
/// <summary>Native package layout, deliberately distinct from npm package metadata.</summary>
public sealed record PluginPackage(IReadOnlyList<string> Plugins, IReadOnlyList<string>? Exclude = null);

/// <summary>Reads manifests without evaluating code. First root wins duplicate names.</summary>
public static partial class PluginScanner
{
    public const string ManifestFile = "chord.plugin.json";
    public const string PackageFile = "chord.plugins.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    [GeneratedRegex("^(?:@[a-z0-9~-][a-z0-9._~-]*/)?[a-z0-9~-][a-z0-9._~-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Names();
    public static bool IsPluginName(string name) => name is not null && Names().IsMatch(name);
    public static Task<PluginDiscovery> DiscoverAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default)
        => DiscoverAsync(roots.Select(root => new PluginRoot(root)), cancellationToken);
    public static async Task<PluginDiscovery> DiscoverAsync(IEnumerable<PluginRoot> roots, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, NativePlugin>(StringComparer.Ordinal);
        var problems = new List<DiscoveryProblem>();
        foreach (var specification in roots)
        {
            var root = Path.GetFullPath(specification.Path);
            try
            {
                if (!System.IO.Directory.Exists(root)) { if (specification.Required) problems.Add(new(root, "required plugin root does not exist", root, true)); continue; }
                root = RealPath(root);
                var entries = (File.Exists(Path.Combine(root, ManifestFile)) || File.Exists(Path.Combine(root, PackageFile))) ? new[] { root } : System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).ToArray();
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry != root && (Path.GetFileName(entry).StartsWith('.') || BaseInputs.Excluded(entry))) continue;
                    var directory = entry;
                    try
                    {
                        directory = RealPath(entry);
                        if (!Inside(root, directory)) throw new InvalidDataException("plugin link escapes its root");
                        var packagePath = Path.Combine(directory, PackageFile);
                        var folders = new[] { directory };
                        if (File.Exists(packagePath))
                        {
                            var package = JsonSerializer.Deserialize<PluginPackage>(await File.ReadAllTextAsync(packagePath, cancellationToken).ConfigureAwait(false), Json) ?? throw new InvalidDataException("empty package");
                            folders = PackageFolders(directory, package);
                            if (folders.Distinct(BaseInputs.Paths).Count() != folders.Length || folders.Any(a => folders.Any(b => !BaseInputs.Paths.Equals(a, b) && Inside(a, b)))) throw new InvalidDataException("duplicate or overlapping plugin folders");
                        }
                        foreach (var folder in folders)
                        {
                            try
                            {
                                var json = await File.ReadAllTextAsync(Path.Combine(folder, ManifestFile), cancellationToken).ConfigureAwait(false);
                                var manifest = JsonSerializer.Deserialize<PluginManifest>(json, Json) ?? throw new InvalidDataException("empty manifest");
                                if (string.IsNullOrEmpty(manifest.Name) || !IsPluginName(manifest.Name)) throw new InvalidDataException("invalid plugin name");
                                if (string.IsNullOrWhiteSpace(manifest.Assembly) || Path.GetExtension(manifest.Assembly) != ".dll") throw new InvalidDataException("assembly must name a .dll");
                                ContainedPath(folder, manifest.Assembly);
                                if (manifest.Project is { } project) { ContainedPath(folder, project); if (Path.GetExtension(project) != ".csproj") throw new InvalidDataException("project must name a .csproj"); }
                                if (!found.TryAdd(manifest.Name, new(manifest, folder, root, specification.Required, File.Exists(packagePath) ? "package" : "directory"))) throw new InvalidDataException($"plugin '{manifest.Name}' already discovered in {found[manifest.Name].Directory}");
                            }
                            catch (Exception error) when (DiscoveryError(error)) { problems.Add(new(folder, error.Message, root, specification.Required)); }
                        }
                    }
                    catch (Exception error) when (DiscoveryError(error)) { problems.Add(new(directory, error.Message, root, specification.Required)); }
                }
            }
            catch (Exception error) when (DiscoveryError(error)) { problems.Add(new(root, error.Message, root, specification.Required)); }
        }
        return new(found.Values.OrderBy(x => x.Manifest.Name, StringComparer.Ordinal).ToArray(), problems);
    }
    private static string[] PackageFolders(string directory, PluginPackage package)
    {
        static Regex Pattern(string value)
        {
            if (Path.IsPathRooted(value) || value.Split('/', '\\').Any(part => part is "." or "..")) throw new InvalidDataException("invalid plugin folder pattern");
            var escaped = Regex.Escape(value.Replace('\\', '/')).Replace("\\*\\*", "\u0001").Replace("\\*", "[^/]*").Replace("\\?", "[^/]").Replace("\u0001", ".*");
            return new Regex("^" + escaped + "$", OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant);
        }
        if (package.Plugins is null) throw new InvalidDataException("package plugins must be a list");
        var excludes = (package.Exclude ?? []).Select(Pattern).ToArray();
        var all = new List<string>();
        if (package.Plugins.Any(pattern => pattern.Contains('*') || pattern.Contains('?')))
        {
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.TryPop(out var current)) foreach (var child in System.IO.Directory.EnumerateDirectories(current))
            {
                if (BaseInputs.Excluded(Path.GetRelativePath(directory, child))) continue;
                if (new DirectoryInfo(child).LinkTarget is not null) throw new InvalidDataException("plugin glob follows a directory link");
                if (all.Count >= 10000) throw new InvalidDataException("plugin directory scan limit exceeded");
                all.Add(child); pending.Push(child);
            }
        }
        var selected = new List<string>();
        foreach (var pattern in package.Plugins)
        {
            var match = Pattern(pattern);
            var paths = pattern.Contains('*') || pattern.Contains('?') ? all.Where(path => match.IsMatch(Path.GetRelativePath(directory, path).Replace('\\', '/'))) : new[] { ContainedPath(directory, pattern) };
            selected.AddRange(paths.Where(path => !excludes.Any(exclude => exclude.IsMatch(Path.GetRelativePath(directory, path).Replace('\\', '/')))));
        }
        return selected.ToArray();
    }
    private static bool DiscoveryError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            current = Path.Combine(current, part);
            FileSystemInfo item = System.IO.Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (item.Exists && item.LinkTarget is not null) current = item.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }
    public static string ContainedPath(string directory, string relative)
    {
        var root = Path.TrimEndingDirectorySeparator(RealPath(directory));
        if (Path.IsPathRooted(relative)) throw new ArgumentException("plugin path must be relative", nameof(relative));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!Inside(root, full)) throw new ArgumentException("plugin path escapes its directory", nameof(relative));
        var real = RealPath(full);
        if (!Inside(root, real)) throw new ArgumentException("plugin path follows a link outside its directory", nameof(relative));
        return real;
    }
    internal static bool Inside(string root, string path)
    { var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal; return string.Equals(root, path, comparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison); }
}
