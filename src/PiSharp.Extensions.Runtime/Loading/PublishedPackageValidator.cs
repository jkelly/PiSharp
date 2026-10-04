using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime.Discovery;

namespace PiSharp.Extensions.Runtime.Loading;

internal sealed record ManagedSnapshotImage(AssemblyName Identity, SnapshotArtifact Artifact);
internal sealed record ValidatedPublishedPackage(
    Dictionary<string, ManagedSnapshotImage> Images, Dictionary<string, Assembly> Shared,
    HashSet<string> FrameworkNames, string EntryAssemblyPath);

/// <summary>Reads metadata only AFTER explicit approved fixture execution trust. PEReader is not a hostile-PE sandbox.</summary>
internal static class PublishedPackageValidator
{
    internal static ValidatedPublishedPackage Validate(PluginPackageSnapshot snapshot, ExtensionManifest manifest, string rid)
    {
        var shared = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
        {
            [typeof(IPiSharpExtension).Assembly.GetName().Name!] = typeof(IPiSharpExtension).Assembly,
            [typeof(JsonData).Assembly.GetName().Name!] = typeof(JsonData).Assembly
        };
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator)
            .Where(path => string.Equals(Path.GetDirectoryName(path), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileNameWithoutExtension(path)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (framework.Count == 0) throw Fail(PluginLoadFailure.UnsupportedFramework, manifest.Id, "host-framework");
        ValidateRuntime(snapshot, manifest);
        ValidateDeps(snapshot, manifest, rid, shared.Keys, framework);
        var images = new Dictionary<string, ManagedSnapshotImage>(StringComparer.OrdinalIgnoreCase);
        var references = new List<AssemblyName>();
        foreach (var artifact in snapshot.Artifacts.Values.Where(item => item.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            using var image = new PEReader(new MemoryStream(artifact.Bytes, writable: false));
            if (!image.HasMetadata || image.PEHeaders.CorHeader is null ||
                (image.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) == 0 ||
                (image.PEHeaders.CorHeader.Flags & CorFlags.Requires32Bit) != 0 ||
                image.PEHeaders.CoffHeader.Machine is not (Machine.I386 or Machine.Amd64))
                throw Fail(PluginLoadFailure.UnsupportedNativeDependency, manifest.Id, "managed-image");
            var metadata = image.GetMetadataReader();
            if (!metadata.IsAssembly) throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "managed-image");
            var identity = Definition(metadata);
            if (!RegistrationPolicy.Identifier(identity.Name, 128)) throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "assembly-identity");
            if (shared.ContainsKey(identity.Name!) || framework.Contains(identity.Name!))
                throw Fail(PluginLoadFailure.UnsupportedAbi, manifest.Id, "private-shared-assembly");
            if (!images.TryAdd(identity.Name!, new(identity, artifact)))
                throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "duplicate-assembly-identity");
            if (metadata.AssemblyReferences.Count > 256 || metadata.TypeDefinitions.Count > 65_536)
                throw Fail(PluginLoadFailure.LimitExceeded, manifest.Id, "assembly-metadata");
            references.AddRange(metadata.AssemblyReferences.Select(handle => Reference(metadata, handle)));
            if (artifact.RelativePath == manifest.Assembly && !Entry(metadata, manifest.EntryType))
                throw Fail(PluginLoadFailure.InvalidEntryType, manifest.Id, "entry-type");
        }
        foreach (var reference in references)
        {
            if (!RegistrationPolicy.Identifier(reference.Name, 128)) throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "assembly-reference");
            if (shared.TryGetValue(reference.Name!, out var contract))
            {
                if (!Exact(reference, contract.GetName())) throw Fail(PluginLoadFailure.UnsupportedAbi, manifest.Id, "shared-contract-reference");
            }
            else if (images.TryGetValue(reference.Name!, out var dependency))
            {
                if (!Exact(reference, dependency.Identity)) throw Fail(PluginLoadFailure.MissingDependency, manifest.Id, "private-dependency-version");
            }
            else if (!framework.Contains(reference.Name!)) throw Fail(PluginLoadFailure.MissingDependency, manifest.Id, "private-dependency");
        }
        if (!images.Values.Any(item => item.Artifact.RelativePath == manifest.Assembly))
            throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "entry-assembly");
        return new(images, shared, framework, snapshot.Artifacts[manifest.Assembly].FullPath);
    }

    internal static bool Exact(AssemblyName requested, AssemblyName actual) =>
        string.Equals(requested.Name, actual.Name, StringComparison.OrdinalIgnoreCase) && requested.Version == actual.Version &&
        string.Equals(requested.CultureName ?? "", actual.CultureName ?? "", StringComparison.OrdinalIgnoreCase) &&
        (requested.GetPublicKeyToken() ?? []).SequenceEqual(actual.GetPublicKeyToken() ?? []);

    private static AssemblyName Definition(MetadataReader metadata)
    {
        var definition = metadata.GetAssemblyDefinition();
        var result = new AssemblyName { Name = metadata.GetString(definition.Name), Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : metadata.GetString(definition.Culture) };
        if (!definition.PublicKey.IsNil) result.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
        return result;
    }
    private static AssemblyName Reference(MetadataReader metadata, AssemblyReferenceHandle handle)
    {
        var reference = metadata.GetAssemblyReference(handle);
        var result = new AssemblyName { Name = metadata.GetString(reference.Name), Version = reference.Version,
            CultureName = reference.Culture.IsNil ? null : metadata.GetString(reference.Culture) };
        if (!reference.PublicKeyOrToken.IsNil)
        {
            var bytes = metadata.GetBlobBytes(reference.PublicKeyOrToken);
            if ((reference.Flags & AssemblyFlags.PublicKey) != 0) result.SetPublicKey(bytes); else result.SetPublicKeyToken(bytes);
        }
        return result;
    }

    private static bool Entry(MetadataReader metadata, string name)
    {
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var actual = definition.Namespace.IsNil ? metadata.GetString(definition.Name) :
                metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);
            // This first published profile supports top-level entry types only; no generic/inherited-contract fallback.
            if (actual != name || !definition.GetDeclaringType().IsNil) continue;
            if ((definition.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public ||
                (definition.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0 ||
                definition.GetGenericParameters().Count != 0) return false;
            var directContract = definition.GetInterfaceImplementations().Any(item =>
            {
                var implementation = metadata.GetInterfaceImplementation(item).Interface;
                if (implementation.Kind != HandleKind.TypeReference) return false;
                var reference = metadata.GetTypeReference((TypeReferenceHandle)implementation);
                return metadata.GetString(reference.Namespace) == "PiSharp.Extensions" && metadata.GetString(reference.Name) == nameof(IPiSharpExtension) &&
                    reference.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                    Reference(metadata, (AssemblyReferenceHandle)reference.ResolutionScope).Name == typeof(IPiSharpExtension).Assembly.GetName().Name;
            });
            return directContract && definition.GetMethods().Any(item =>
            {
                var method = metadata.GetMethodDefinition(item);
                if (metadata.GetString(method.Name) != ".ctor" || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public ||
                    (method.Attributes & MethodAttributes.Static) != 0) return false;
                var signature = metadata.GetBlobReader(method.Signature);
                var header = signature.ReadSignatureHeader();
                return header.IsInstance && !header.IsGeneric && signature.ReadCompressedInteger() == 0;
            });
        }
        return false;
    }

    private static JsonElement Json(PluginPackageSnapshot snapshot, string path, string owner)
    {
        if (!snapshot.Artifacts.TryGetValue(path, out var artifact) || artifact.Bytes.Length > 1_048_576)
            throw Fail(PluginLoadFailure.InvalidPublishedPackage, owner, "published-json");
        var data = JsonData.Parse(new UTF8Encoding(false, true).GetString(artifact.Bytes));
        if (!RegistrationPolicy.Json(data, new() { MaximumJsonCharacters = 1_048_576, MaximumJsonDepth = 32 }, requireObject: true))
            throw Fail(PluginLoadFailure.InvalidPublishedPackage, owner, "published-json");
        return data.Value;
    }

    private static void ValidateRuntime(PluginPackageSnapshot snapshot, ExtensionManifest manifest)
    {
        var path = manifest.Assembly[..^4] + ".runtimeconfig.json";
        var root = Json(snapshot, path, manifest.Id);
        if (root.EnumerateObject().Count() != 1 || !root.TryGetProperty("runtimeOptions", out var runtime) || runtime.ValueKind != JsonValueKind.Object ||
            !runtime.TryGetProperty("tfm", out var tfm) || tfm.GetString() != "net10.0" || runtime.TryGetProperty("includedFrameworks", out _))
            throw Fail(PluginLoadFailure.UnsupportedFramework, manifest.Id, "runtime-framework");
        JsonElement selected;
        if (runtime.TryGetProperty("framework", out var single)) selected = single;
        else if (runtime.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array && many.GetArrayLength() == 1) selected = many[0];
        else throw Fail(PluginLoadFailure.UnsupportedFramework, manifest.Id, "runtime-framework");
        if (runtime.TryGetProperty("framework", out _) && runtime.TryGetProperty("frameworks", out _) || selected.ValueKind != JsonValueKind.Object ||
            selected.EnumerateObject().Any(property => property.Name is not ("name" or "version")) ||
            !selected.TryGetProperty("name", out var name) || name.GetString() != "Microsoft.NETCore.App" ||
            !selected.TryGetProperty("version", out var version) || !Version.TryParse(version.GetString(), out var framework) || framework.Major != 10 || framework.Minor != 0 ||
            runtime.EnumerateObject().Any(property => property.Name is not ("tfm" or "framework" or "frameworks" or "configProperties" or "rollForward")))
            throw Fail(PluginLoadFailure.UnsupportedFramework, manifest.Id, "runtime-framework");
    }

    private static void ValidateDeps(PluginPackageSnapshot snapshot, ExtensionManifest manifest, string rid,
        IEnumerable<string> shared, HashSet<string> framework)
    {
        var root = Json(snapshot, manifest.Assembly[..^4] + ".deps.json", manifest.Id);
        if (root.EnumerateObject().Any(property => property.Name is not ("runtimeTarget" or "compilationOptions" or "targets" or "libraries" or "runtimes")) ||
            !root.TryGetProperty("runtimeTarget", out var runtime) || runtime.ValueKind != JsonValueKind.Object ||
            !runtime.TryGetProperty("name", out var targetName) || targetName.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object || libraries.EnumerateObject().Count() > 256)
            throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-format");
        var name = targetName.GetString()!;
        if (name != ".NETCoreApp,Version=v10.0" && name != ".NETCoreApp,Version=v10.0/" + rid)
            throw Fail(PluginLoadFailure.UnsupportedFramework, manifest.Id, "deps-target");
        if (!targets.TryGetProperty(name, out var target) || target.ValueKind != JsonValueKind.Object || targets.EnumerateObject().Count() > 2)
            throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-format");
        foreach (var library in libraries.EnumerateObject())
        {
            var parts = library.Name.Split('/');
            if (parts.Length != 2 || !RegistrationPolicy.Identifier(parts[0], 128) || !Version.TryParse(parts[1], out _) ||
                library.Value.ValueKind != JsonValueKind.Object || !library.Value.TryGetProperty("type", out var type) || type.GetString() is not ("project" or "reference") ||
                library.Value.EnumerateObject().Any(property => property.Name is not ("type" or "serviceable" or "sha512" or "path" or "hashPath")))
                throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-library");
        }
        foreach (var library in target.EnumerateObject())
        {
            if (!libraries.TryGetProperty(library.Name, out _) || library.Value.ValueKind != JsonValueKind.Object)
                throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-library");
            foreach (var section in library.Value.EnumerateObject())
            {
                if (section.Name == "dependencies")
                {
                    if (section.Value.ValueKind != JsonValueKind.Object || section.Value.EnumerateObject().Count() > 256 ||
                        section.Value.EnumerateObject().Any(property => !RegistrationPolicy.Identifier(property.Name, 128) ||
                            property.Value.ValueKind != JsonValueKind.String || !Version.TryParse(property.Value.GetString(), out _)))
                        throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-dependencies");
                    continue;
                }
                if (section.Value.ValueKind != JsonValueKind.Object) throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-assets");
                if (section.Name == "native" && section.Value.EnumerateObject().Any())
                    throw Fail(PluginLoadFailure.UnsupportedNativeDependency, manifest.Id, "native-assets");
                if (section.Name is not ("runtime" or "runtimeTargets" or "native" or "resources") ||
                    section.Name == "resources" && section.Value.EnumerateObject().Any())
                    throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-assets");
                foreach (var asset in section.Value.EnumerateObject())
                {
                    if (asset.Value.ValueKind != JsonValueKind.Object || asset.Value.EnumerateObject().Any(property =>
                        property.Name is not ("assemblyVersion" or "fileVersion" or "rid" or "assetType")))
                        throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-assets");
                    if (section.Name == "runtimeTargets")
                    {
                        if (asset.Value.ValueKind != JsonValueKind.Object || !asset.Value.TryGetProperty("assetType", out var kind) || kind.GetString() != "runtime")
                            throw Fail(PluginLoadFailure.UnsupportedNativeDependency, manifest.Id, "native-assets");
                        if (!asset.Value.TryGetProperty("rid", out var assetRid) || assetRid.ValueKind != JsonValueKind.String || assetRid.GetString() != rid)
                            throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-rid");
                    }
                    if (!ManifestPathPolicy.Relative(asset.Name) || !asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        throw Fail(PluginLoadFailure.InvalidPublishedPackage, manifest.Id, "deps-asset-path");
                    var simple = Path.GetFileNameWithoutExtension(asset.Name);
                    if (shared.Contains(simple, StringComparer.OrdinalIgnoreCase) || framework.Contains(simple)) continue;
                    if (!snapshot.Artifacts.ContainsKey(asset.Name)) throw Fail(PluginLoadFailure.MissingDependency, manifest.Id, "deps-asset");
                }
            }
        }
    }

    private static PluginLoadException Fail(PluginLoadFailure failure, string owner, string operation) => new(failure, owner, operation);
}
