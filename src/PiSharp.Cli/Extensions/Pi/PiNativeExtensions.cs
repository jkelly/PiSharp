// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (loadExtension: the module is
// imported fresh for every load, its default export is the factory, a failure is "Failed to load extension"). Native C# extensions are
// PiSharp's own (owner decision 10): they are discovered as Pi discovers extensions and load into their own assembly load context.
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>
/// A native C# extension of a Pi-style run: a <c>pisharp-extension.json</c> manifest naming its <c>assembly</c> (relative to the
/// manifest) and its <c>entryType</c>, a public <see cref="IPiSharpExtension"/> with a parameterless constructor. It is discovered like a
/// Pi extension (global and project extension folders, settings entries, packages, <c>-e</c>), gated only by project trust, and needs
/// no approval or preflight document. The assembly and its private dependencies load from the extension's folder into a collectible
/// load context per load (a reload loads fresh code); assemblies the host provides (PiSharp's contracts, the framework) are shared.
/// </summary>
internal sealed class PiNativeExtension
{
    internal const string ManifestName = "pisharp-extension.json";

    private PiNativeExtension(string path, string assemblyPath, Type type, PiNativeLoadContext context, int index, int generation)
    { Path = path; AssemblyPath = assemblyPath; Type = type; Context = context; Index = index; Generation = generation; }

    /// <summary>The manifest path (the extension's path in diagnostics, sourceInfo and MCP ownership).</summary>
    internal string Path { get; }
    internal string AssemblyPath { get; }
    internal Type Type { get; }
    internal PiNativeLoadContext Context { get; }
    internal int Index { get; }
    internal int Generation { get; }
    internal string OwnerId => "pi-native-" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        (Generation == 0 ? "" : "-r" + Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The session's registry owner of this extension (null before activation).</summary>
    internal RegistrationScope? Scope { get; set; }
    /// <summary>Tool registrations of the owner that the session's catalog holds.</summary>
    internal ImmutableArray<string> PublishedToolIds { get; set; } = [];
    internal ImmutableHashSet<string> PublishedToolNames { get; set; } = ImmutableHashSet<string>.Empty;
    /// <summary>Activated after the session bound (a reload): its tools reach the catalog through a catalog replacement.</summary>
    internal bool LateActivation { get; set; }
    /// <summary>The tool names the session's catalog holds for this extension when a reload retired it.</summary>
    internal ImmutableHashSet<string> CatalogToolNames { get; set; } = ImmutableHashSet<string>.Empty;
    internal bool Retired { get; private set; }

    internal static bool IsManifest(string path) => string.Equals(System.IO.Path.GetFileName(path), ManifestName, StringComparison.Ordinal);

    /// <summary>A fresh instance of the entry type (the extension's factory; every registry that activates it gets its own).</summary>
    internal IPiSharpExtension CreateInstance() => (IPiSharpExtension)Activator.CreateInstance(Type)!;

    /// <summary>Loads the manifest's assembly and entry type. Throws <see cref="InvalidOperationException"/> with the load error.</summary>
    internal static PiNativeExtension Load(string manifestPath, int index, int generation)
    {
        JsonElement manifest;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { MaxDepth = 32 });
            manifest = document.RootElement.Clone();
        }
        catch (JsonException error) { throw new InvalidOperationException($"Invalid {ManifestName}: {error.Message}"); }
        catch (IOException error) { throw new InvalidOperationException(error.Message); }
        string Text(string name) => manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text : throw new InvalidOperationException($"{ManifestName} must name its \"{name}\".");
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifestPath))!;
        var assemblyPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, Text("assembly").Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var entryType = Text("entryType");
        if (!File.Exists(assemblyPath)) throw new InvalidOperationException($"Extension assembly does not exist: {assemblyPath}");
        var context = new PiNativeLoadContext(assemblyPath);
        try
        {
            var type = context.LoadEntry().GetType(entryType, throwOnError: false, ignoreCase: false)
                ?? throw new InvalidOperationException($"Type \"{entryType}\" not found in {System.IO.Path.GetFileName(assemblyPath)}");
            if (!type.IsPublic && !type.IsNestedPublic || type.IsAbstract || type.ContainsGenericParameters || !typeof(IPiSharpExtension).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Type \"{entryType}\" must be a public {nameof(IPiSharpExtension)} with a parameterless constructor");
            return new(System.IO.Path.GetFullPath(manifestPath), assemblyPath, type, context, index, generation);
        }
        catch (Exception error) when (error is not InvalidOperationException)
        {
            context.Unload();
            throw new InvalidOperationException(error is ReflectionTypeLoadException or BadImageFormatException or FileLoadException or FileNotFoundException
                ? error.Message : error.GetType().Name + ": " + error.Message);
        }
        catch { context.Unload(); throw; }
    }

    /// <summary>The runtime this extension belongs to was replaced (reload): its registrations other than tools leave at once and its
    /// MCP servers are unregistered; the tools leave with the catalog replacement.</summary>
    internal void Retire()
    {
        Retired = true;
        if (Scope is not { } scope) return;
        try { foreach (var server in scope.GetMcpServers().Where(server => server.ExtensionPath == Path)) scope.UnregisterMcpServer(server.Name); }
        catch (Exception error) when (error is NotSupportedException or ObjectDisposedException or InvalidOperationException) { }
        scope.WithdrawRegistrations(keepTools: false);
    }
}

/// <summary>The extension's own collectible load context: its assembly and private dependencies from its folder (by its
/// <c>.deps.json</c> when present); an assembly the host resolves is shared with the host.</summary>
internal sealed class PiNativeLoadContext(string entryPath) : AssemblyLoadContext("PiSharp.PiNative:" + System.IO.Path.GetFileName(entryPath), isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryPath);

    internal Assembly LoadEntry() => LoadCopy(entryPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // The host's assemblies (contracts the extension implements, the framework) are shared.
        try { return Default.LoadFromAssemblyName(assemblyName); }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException) { }
        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadCopy(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;

    /// <summary>Loads from the file's bytes so the extension's files stay replaceable while it runs (a rebuild, then /reload).</summary>
    private Assembly LoadCopy(string path)
    {
        using var image = new MemoryStream(File.ReadAllBytes(path), writable: false);
        var symbols = System.IO.Path.ChangeExtension(path, ".pdb");
        if (!File.Exists(symbols)) return LoadFromStream(image);
        using var pdb = new MemoryStream(File.ReadAllBytes(symbols), writable: false);
        return LoadFromStream(image, pdb);
    }
}
