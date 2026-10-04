using System.Reflection;
using System.Runtime.Loader;

namespace PiSharp.Extensions.Runtime.Loading;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private ValidatedPublishedPackage? package;
    private AssemblyDependencyResolver? resolver;
    private PluginSystemImportProfile? systemImports;
    private readonly string owner;

    internal PluginLoadContext(string owner, ValidatedPublishedPackage package, PluginSystemImportProfile? systemImports = null) : base("PiSharp:" + owner, isCollectible: true)
    {
        this.owner = owner;
        this.package = package;
        this.systemImports = systemImports;
        try { resolver = new(package.EntryAssemblyPath); }
        catch { systemImports?.Dispose(); this.systemImports = null; this.package = null; Unload(); throw; }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var current = package ?? throw new PluginLoadException(PluginLoadFailure.InactiveLoader, owner, "resolve-managed");
        if (current.Shared.TryGetValue(assemblyName.Name ?? "", out var shared))
        {
            if (!PublishedPackageValidator.Exact(assemblyName, shared.GetName()))
                throw new PluginLoadException(PluginLoadFailure.UnsupportedAbi, owner, "resolve-shared-contract");
            return shared;
        }
        if (current.FrameworkNames.Contains(assemblyName.Name ?? "")) return null;
        if (!current.Images.TryGetValue(assemblyName.Name ?? "", out var image) || !PublishedPackageValidator.Exact(assemblyName, image.Identity))
            throw new PluginLoadException(PluginLoadFailure.MissingDependency, owner, "resolve-managed");
        var path = resolver!.ResolveAssemblyToPath(assemblyName);
        if (path is null || !IsOwnedManagedPath(path, image.Artifact.FullPath))
            throw new PluginLoadException(PluginLoadFailure.MissingDependency, owner, "resolve-managed-path");
        using var stream = new MemoryStream(image.Artifact.Bytes, writable: false);
        var assembly = LoadFromStream(stream);
        systemImports?.Attach(assembly, image);
        return assembly;
    }

    private static bool IsOwnedManagedPath(string resolved, string captured)
    {
        if (!OperatingSystem.IsWindows())
            return string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(captured), StringComparison.Ordinal);
        var left = LocalPath(resolved);
        var right = LocalPath(captured);
        return left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string? LocalPath(string value)
    {
        if (!Path.IsPathFullyQualified(value)) return null;
        var full = Path.GetFullPath(value);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            full = full[4..];
            // Extended paths do not have ordinary Win32 normalization semantics.
            // Only the already canonical local-drive spelling may lose this prefix.
            if (!Path.IsPathFullyQualified(full) || !string.Equals(full, Path.GetFullPath(full), StringComparison.Ordinal))
                return null;
        }
        return full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == '\\' ? full : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        throw new PluginLoadException(PluginLoadFailure.UnsupportedNativeDependency, owner, "resolve-native");

    internal Assembly LoadEntry(SnapshotArtifact entry)
    {
        using var stream = new MemoryStream(entry.Bytes, writable: false);
        return LoadFromStream(stream);
    }

    internal void ReleaseRootsAndRequestUnload()
    {
        systemImports?.Dispose();
        systemImports = null;
        package = null;
        resolver = null;
        Unload();
    }
}
