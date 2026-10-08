using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Discovery;
using PiSharp.Extensions.Runtime.Loading;

internal static class PublishedSystemImportTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("supervisor system import requires exact separate grant before compiled module/constructor effects", GrantAdmission),
        ("approved private supervisor import does not grant another private assembly or unknown native dependency", Isolation),
        ("approved system handle survives callback/plugin joins and releases after shared disposal", Lifetime)
    ];

    private static async Task GrantAdmission()
    {
        using var files = await Files.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(files.Options with { MaximumOwnedPackages = 1 });
        var approved = files.Execution;
        var grant = approved.SystemImportApprovals[0];
        foreach (var decision in new[]
        {
            approved with { SystemImportApprovals = [] },
            approved with { SystemImportApprovals = default },
            approved with { SystemImportApprovals = [grant with { Profile = "windows-worker-directory-1" }] },
            approved with { SystemImportApprovals = [grant with { Artifact = "PublishedFixture.SystemImport.dll" }] },
            approved with { SystemImportApprovals = [grant with { Sha256 = new string('0', 64) }] },
            approved with { SystemImportApprovals = [grant, grant] }
        })
        {
            await Fails(files.Load(loader, registry, decision), PluginLoadFailure.UnsupportedNativeDependency);
            files.NoAdmissionEffects(registry);
        }
        foreach (var decision in new[]
        {
            approved with { EffectiveScopeId = "other-scope" },
            approved with { SourceScope = ExtensionSourceScope.Project },
            approved with { ManifestValueSha256 = new string('0', 64) },
            approved with { ArtifactHashes = [] },
            approved with { PolicyRevision = "obsolete-policy" },
            approved with { HostGeneration = 2 }
        })
        {
            await Fails(files.Load(loader, registry, decision), PluginLoadFailure.ExecutionTrustBindingMismatch);
            files.NoAdmissionEffects(registry);
        }
        // Repeated failures must release their full reservation and snapshot; the actual approved generation still loads.
        var loaded = await files.Load(loader, registry);
        Equal(1, files.Count("module")); Equal(1, files.Count("constructor")); Equal(1, files.Count("initialize"));
        await registry.InvokeToolAsync(registry.CaptureSnapshot(), "fixture.system.import", Action("approved"));
        True(Directory.Exists(files.Child("approved")));
        await loaded.DisposeAsync();
        Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }

    private static async Task Isolation()
    {
        using var files = await Files.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(files.Options);
        var loaded = await files.Load(loader, registry);
        var captured = registry.CaptureSnapshot();
        var result = await registry.InvokeToolAsync(captured, "fixture.system.import", Action("approved"));
        True(result.Value.GetProperty("created").GetBoolean());
        True(result.Value.GetProperty("privateHost").GetBoolean());
        Equal("PiSharp:fixture.system-import", result.Value.GetProperty("context").GetString());
        True(result.Value.GetProperty("sharedContracts").GetBoolean());
        True(result.Value.GetProperty("sharedAbstractions").GetBoolean());
        True(Directory.Exists(files.Child("approved")), "The approved import must perform actual Win32 directory creation.");
        await ResolutionFailure(() => registry.InvokeToolAsync(captured, "fixture.system.import", Action("other")).AsTask());
        False(Path.Exists(files.Child("other")), "The other private assembly must not acquire the already used supervisor grant.");
        await ResolutionFailure(() => registry.InvokeToolAsync(captured, "fixture.system.import", Action("unknown")).AsTask());
        var markerCount = files.MarkerCount;
        await Fails(files.Load(loader, registry), PluginLoadFailure.DuplicateOwner);
        Equal(markerCount, files.MarkerCount);
        await loaded.DisposeAsync();
        True(Directory.Exists(files.Child("dispose-native")), "The actual approved import must remain usable during plugin shutdown.");
        True(loaded.UnloadRequested); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
        Equal(0, registry.CaptureSnapshot().Registrations.Length);
        await Throws<ExtensionRegistrationException>(() => registry.InvokeToolAsync(captured, "fixture.system.import", Action("approved")).AsTask());
        Equal(1, files.Count("dispose-entered")); Equal(1, files.Count("dispose-closed"));
    }

    private static async Task Lifetime()
    {
        using var files = await Files.CreateAsync();
        await using var registry = new ExtensionRegistry();
        await using var loader = new PluginAssemblyLoader(files.Options);
        var callbackEntered = files.AddGate("callback-entered");
        var callbackRelease = files.AddGate("callback-release");
        var disposeEntered = files.AddGate("dispose-entered");
        var disposeRelease = files.AddGate("dispose-release");
        var loaded = await files.Load(loader, registry);
        var captured = registry.CaptureSnapshot();
        var dispatch = registry.InvokeToolAsync(captured, "fixture.system.import", Action("hold")).AsTask();
        await Reach(callbackEntered.Task, dispatch);
        True(Directory.Exists(files.Child("hold")));
        var profile = OwnedProfile(loaded);
        True(Handle(profile) != 0, "The approved import must own a real native library reference.");
        var closing = loaded.DisposeAsync().AsTask();
        try
        {
            False(closing.IsCompleted); False(disposeEntered.Task.IsCompleted);
            False(loaded.UnloadRequested); True(Directory.Exists(loaded.SnapshotDirectory));
            True(Handle(profile) != 0);
            callbackRelease.TrySetResult();
            await Throws<OperationCanceledException>(() => dispatch);
            await Reach(disposeEntered.Task, closing);
            True(Directory.Exists(files.Child("dispose-native")));
            True(Handle(profile) != 0, "Plugin shutdown must complete before the owned system handle is released.");
            False(loaded.UnloadRequested); True(Directory.Exists(loaded.SnapshotDirectory));
            var same = loaded.DisposeAsync().AsTask();
            True(ReferenceEquals(closing, same)); False(same.IsCompleted);
            disposeRelease.TrySetResult();
            await closing; await same;
            Equal((nint)0, Handle(profile));
            True(loaded.UnloadRequested); False(Directory.Exists(loaded.SnapshotDirectory));
            Equal(1, files.Count("callback-closed")); Equal(1, files.Count("dispose-entered")); Equal(1, files.Count("dispose-closed"));
            Equal(0, registry.CaptureSnapshot().Registrations.Length);
        }
        finally
        {
            callbackRelease.TrySetResult(); disposeRelease.TrySetResult();
            try { await dispatch; } catch (OperationCanceledException) { }
            await closing;
        }
    }

    private static object OwnedProfile(LoadedExtension loaded)
    {
        var context = typeof(LoadedExtension).GetField("context", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loaded)!;
        return context.GetType().GetField("systemImports", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)
            ?? throw new InvalidOperationException("The admitted profile owner is absent.");
    }
    private static nint Handle(object profile) => (nint)profile.GetType().GetField("handle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(profile)!;
    private static JsonData Action(string action) => JsonData.Parse(JsonSerializer.Serialize(new { action }));
    private static async Task Reach(Task gate, Task operation)
    {
        await Task.WhenAny(gate, operation).WaitAsync(TimeSpan.FromSeconds(10));
        if (!gate.IsCompleted) { await operation; throw new InvalidOperationException("The actual import operation did not reach its gate."); }
        await gate;
    }
    private static async Task Fails(Task task, PluginLoadFailure expected) => Equal(expected, (await Throws<PluginLoadException>(() => task)).Failure);
    private static async Task ResolutionFailure(Func<Task> action)
    {
        var error = await Throws<Exception>(action);
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PluginLoadException failure) { Equal(PluginLoadFailure.UnsupportedNativeDependency, failure.Failure); return; }
        throw new InvalidOperationException("Actual native import did not return the bounded native-dependency rejection.");
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static void True(bool value, string message = "Expected true.") { if (!value) throw new InvalidOperationException(message); }
    private static void False(bool value, string message = "Expected false.") => True(!value, message);
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }

    private sealed class Files : IDisposable
    {
        private const string Scope = "approved-system-import-suite";
        private readonly string root = Path.Combine(Path.GetTempPath(), "PiSharp-SystemImport-" + Guid.NewGuid().ToString("N"));
        private readonly string? previousRoot = Environment.GetEnvironmentVariable("PISHARP_SYSTEM_IMPORT_ROOT");
        private readonly object? previousGates = AppContext.GetData("PiSharp.SystemImport.Gates");
        private readonly Dictionary<string, TaskCompletionSource> gates = new(StringComparer.Ordinal);
        private string Package => Path.Combine(root, "package");
        private string Effects => Path.Combine(root, "effects");
        internal string Snapshots => Path.Combine(root, "snapshots");
        internal PluginAssemblyLoaderOptions Options => new() { SnapshotParentDirectory = Snapshots };
        private JsonData metadata = JsonData.EmptyObject;
        private ExtensionManifestReadResult Parsed => new ExtensionManifestReader().Read(metadata);
        internal int MarkerCount => File.Exists(Child("effects.markers")) ? File.ReadAllLines(Child("effects.markers")).Length : 0;
        internal int Count(string stage) => File.Exists(Child("effects.markers")) ? File.ReadAllLines(Child("effects.markers")).Count(line => line == stage) : 0;
        internal string Child(string name) => Path.Combine(Effects, name);
        internal PluginExecutionDecision Execution => new(PluginExecutionDisposition.ApprovePublishedFixtureExecution, Package,
            Parsed.ManifestValueSha256!, Parsed.Manifest!.ArtifactHashes, ExtensionSourceScope.Explicit, Scope, "experimental-policy-0", 1)
        {
            SystemImportApprovals = [new("windows-worker-directory-0", "PiSharp.ExtensionHost.dll",
                Parsed.Manifest!.ArtifactHashes.Single(hash => hash.RelativePath == "PiSharp.ExtensionHost.dll").Sha256)]
        };
        internal Task<LoadedExtension> Load(PluginAssemblyLoader loader, ExtensionRegistry registry, PluginExecutionDecision? execution = null) =>
            loader.LoadAsync(metadata, Package, ExtensionSourceScope.Explicit, Scope,
                new(ExtensionTrustDisposition.ApproveMetadataInspection, Package, Parsed.ManifestValueSha256!, ExtensionSourceScope.Explicit, Scope, "experimental-policy-0"),
                execution ?? Execution, registry);
        internal TaskCompletionSource AddGate(string key) { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); gates.Add(key, value); return value; }
        internal void NoAdmissionEffects(ExtensionRegistry registry)
        {
            Equal(0, MarkerCount); Equal(0, Directory.GetFileSystemEntries(Effects).Length);
            Equal(0, Directory.GetDirectories(Snapshots).Length); Equal(0, registry.CaptureSnapshot().Registrations.Length);
        }
        internal static async Task<Files> CreateAsync()
        {
            if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                throw new PlatformNotSupportedException("The system import profile requires Windows x64.");
            var files = new Files();
            Directory.CreateDirectory(files.Package); Directory.CreateDirectory(files.Snapshots); Directory.CreateDirectory(files.Effects);
            Environment.SetEnvironmentVariable("PISHARP_SYSTEM_IMPORT_ROOT", files.Effects);
            AppContext.SetData("PiSharp.SystemImport.Gates", files.gates);
            try
            {
                var published = Environment.GetEnvironmentVariable("PISHARP_PUBLISHED_EXTENSION_FIXTURES");
                if (published is null)
                {
                    var directory = new DirectoryInfo(AppContext.BaseDirectory);
                    while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent ?? throw new InvalidOperationException("Cannot locate reviewed published fixture outputs.");
                    published = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures");
                }
                var source = Path.Combine(published, "system-import");
                if (!File.Exists(Path.Combine(source, "PublishedFixture.SystemImport.dll"))) throw new InvalidOperationException("Root must publish the approved system-import fixture.");
                foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(files.Package, Path.GetRelativePath(source, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
                }
                False(File.Exists(Path.Combine(files.Package, "PiSharp.Contracts.dll")));
                False(File.Exists(Path.Combine(files.Package, "PiSharp.Extensions.Abstractions.dll")));
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    hashes.Add(Path.GetRelativePath(files.Package, path).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
                files.metadata = JsonData.Parse(JsonSerializer.Serialize(new
                {
                    schemaVersion = 0, id = "fixture.system-import", packageVersion = "0.0.1",
                    hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" }, runtimeKind = "native",
                    assembly = "PublishedFixture.SystemImport.dll", entryType = "PublishedSystemImportFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = new[] { "owned-descriptor-callbacks" }, declaredCapabilities = new[] { "tools" },
                    resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.SystemImport.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes
                }));
                True(files.Parsed.IsValid); return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            foreach (var gate in gates.Values) gate.TrySetResult();
            Environment.SetEnvironmentVariable("PISHARP_SYSTEM_IMPORT_ROOT", previousRoot);
            AppContext.SetData("PiSharp.SystemImport.Gates", previousGates);
            var full = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.Ordinal) || !Path.GetFileName(full).StartsWith("PiSharp-SystemImport-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup left its exact owned temporary root.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }
}
