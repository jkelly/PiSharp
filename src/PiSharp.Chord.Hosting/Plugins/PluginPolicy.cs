// PiSharp-only: CLR plugin manifest, assembly metadata and dependency-admission adapter; see sync-1.5.2-plugin-contract.md.
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace PiSharp.Chord.Hosting.Plugins;

public sealed record AppApi(string Name, IReadOnlyList<string> Versions);
public sealed record PluginKindDescriptor(IReadOnlyList<Assembly> SharedAssemblies);
public sealed class PluginManagerOptions
{
    public AppApi? AppApi { get; init; }
    public string Kind { get; init; } = "server";
    public IReadOnlyDictionary<string, PluginKindDescriptor> Kinds { get; init; } = new Dictionary<string, PluginKindDescriptor>();
    public int MaxInputFiles { get; init; } = 10000;
}

/// <summary>Assembly and project dependency admission. This policy does not sandbox executing managed code.</summary>
internal static class PluginPolicy
{
    internal static void ValidateManifest(NativePlugin plugin, PluginManagerOptions? options)
    {
        if (options is null) return;
        if (plugin.Manifest.Kind is { } kind && kind != options.Kind) throw new InvalidDataException($"plugin kind '{kind}' cannot run as '{options.Kind}'");
        if (options.Kinds.Count != 0 && !options.Kinds.ContainsKey(options.Kind)) throw new InvalidDataException($"unknown plugin kind '{options.Kind}'");
        if (options.AppApi is { } api && (plugin.Manifest.Api != api.Name || plugin.Manifest.ApiVersion is not { } version || !api.Versions.Contains(version, StringComparer.Ordinal)))
            throw new InvalidDataException($"plugin requires an admitted version of API '{api.Name}'");
    }
    internal static BaseInputs Inputs(NativePlugin plugin, PluginManagerOptions? options)
    {
        var max = options?.MaxInputFiles ?? 10000;
        var directories = new HashSet<string>(BaseInputs.Paths) { plugin.Directory };
        var extraFiles = new HashSet<string>(BaseInputs.Paths);
        if (plugin.Manifest.Project is { } project)
        {
            var pending = new Stack<string>(); pending.Push(PluginScanner.ContainedPath(plugin.Directory, project));
            var seen = new HashSet<string>(BaseInputs.Paths);
            while (pending.TryPop(out var path))
            {
                if (!seen.Add(path)) continue;
                XDocument xml;
                try { xml = XDocument.Load(path, LoadOptions.SetLineInfo); }
                catch (System.Xml.XmlException) when (options is null) { continue; }
                var directory = Path.GetDirectoryName(path)!; directories.Add(directory);
                foreach (var item in xml.Descendants())
                {
                    var name = item.Name.LocalName;
                    if (options?.AppApi is not null && (name is "UsingTask" or "Exec" or "Import")) throw new InvalidDataException($"{path}: custom MSBuild execution/import is not admitted by plugin API policy");
                    if (name == "PackageReference" && options?.AppApi is not null && ((string?)item.Attribute("Include"))?.StartsWith("PiSharp.Chord", StringComparison.OrdinalIgnoreCase) == true)
                        throw new InvalidDataException($"{path}: direct Chord package dependency is not admitted");
                    if (name is not ("ProjectReference" or "HintPath" or "Compile")) continue;
                    var include = name == "HintPath" ? item.Value : (string?)item.Attribute("Include");
                    if (string.IsNullOrWhiteSpace(include)) continue;
                    if (include.Contains('$') || include.Contains('*') || include.Contains(';'))
                    { if (options?.AppApi is not null) throw new InvalidDataException($"{path}: nonliteral {name} is not admitted"); continue; }
                    var resolved = PluginScanner.RealPath(Path.Combine(directory, include));
                    var boundary = plugin.Root ?? plugin.Directory;
                    if (!PluginScanner.Inside(boundary, resolved) && options?.AppApi is not null && !Shared(options).Any(a => BaseInputs.Paths.Equals(a.Location, resolved))) throw new InvalidDataException($"{path}: {name} escapes plugin root");
                    if (name == "ProjectReference") pending.Push(resolved);
                    else if (File.Exists(resolved)) extraFiles.Add(resolved);
                }
            }
        }
        // Directory tracking includes newly added/deleted source inputs and transitive sibling project inputs.
        if (plugin.Manifest.Project is null)
        {
            var assembly = PluginScanner.ContainedPath(plugin.Directory, plugin.Manifest.Assembly);
            if (Directory.Exists(Path.GetDirectoryName(assembly))) directories.Add(Path.GetDirectoryName(assembly)!);
        }
        return BaseInputs.Capture(extraFiles, directories, max);
    }
    internal static IReadOnlyList<Assembly> Shared(PluginManagerOptions? options)
        => options is not null && options.Kinds.TryGetValue(options.Kind, out var kind) ? kind.SharedAssemblies : [];
    internal static void ValidateAssemblies(string directory, PluginManagerOptions? options)
    {
        if (options?.AppApi is null) return;
        var allowed = Shared(options).Select(assembly => assembly.GetName().Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var frameworks = new[] { Path.GetDirectoryName(typeof(object).Assembly.Location)!, Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Http.HttpContext).Assembly.Location)! };
        // Every emitted assembly is inspected, so a private intermediary cannot smuggle a forbidden direct dependency.
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Where(file => !BaseInputs.Excluded(Path.GetRelativePath(directory, file)) && !Path.GetRelativePath(directory, file).StartsWith(".build" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            try
            {
                using var stream = File.OpenRead(file); using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var metadata = pe.GetMetadataReader();
                foreach (var handle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetAssemblyReference(handle); var name = metadata.GetString(reference.Name);
                    if (name.StartsWith("PiSharp.Chord", StringComparison.OrdinalIgnoreCase) && !allowed.Contains(name))
                        throw new InvalidDataException($"{file}: direct dependency '{name}' is not admitted for '{options.Kind}'");
                    if (!allowed.Contains(name) && !local.Contains(name) && !frameworks.Any(root => File.Exists(Path.Combine(root, name + ".dll"))))
                        throw new InvalidDataException($"{file}: private dependency '{name}' is missing from the artifact");
                    if (allowed.Contains(name))
                    {
                        var actual = Shared(options).First(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase)).GetName();
                        var token = metadata.GetBlobBytes(reference.PublicKeyOrToken);
                        if ((reference.Flags & AssemblyFlags.PublicKey) != 0) token = System.Security.Cryptography.SHA1.HashData(token)[^8..].Reverse().ToArray();
                        if (actual.Version != reference.Version || !(actual.GetPublicKeyToken() ?? []).SequenceEqual(token) || (actual.CultureName ?? "") != metadata.GetString(reference.Culture)) throw new InvalidDataException($"{file}: shared dependency version mismatch for '{name}'");
                    }
                }
            }
            catch (BadImageFormatException) { /* Native runtime DLL, not managed metadata. */ }
        }
    }
}
