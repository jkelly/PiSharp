// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/plugins/discover.ts, packages/chord/src/node/plugins/install.ts, packages/chord/src/node/plugins/testing.ts.
// Authored native regression; not an upstream execution capture.
using System.IO.Compression;
using System.Text.Json;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;

internal static class PluginDiscoveryTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "pisharp-plugin-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var baseDirectory = Path.Combine(root, "base"); Directory.CreateDirectory(baseDirectory);
            var file = Path.Combine(baseDirectory, "entry.cs"); File.WriteAllText(file, "original");
            var inputs = BaseInputs.Capture([file], [baseDirectory]);
            Require(!inputs.HasChanged(), "unchanged base");
            var roundtrip = JsonSerializer.Deserialize<BaseInputs>(JsonSerializer.Serialize(inputs))!; Require(!roundtrip.HasChanged(), "worker input record roundtrip");
            File.WriteAllText(file, "changed"); Require(inputs.HasChanged(), "changed bytes");
            File.WriteAllText(file, "original"); Require(!inputs.HasChanged(), "identical restoration clears");
            File.Delete(file); Require(inputs.HasChanged(), "deleted base input");
            File.WriteAllText(file, "original");
            var added = Path.Combine(baseDirectory, "new.cs"); File.WriteAllText(added, "new"); Require(inputs.HasChanged(), "new input detected"); File.Delete(added);
            Directory.CreateDirectory(Path.Combine(baseDirectory, "obj")); File.WriteAllText(Path.Combine(baseDirectory, "obj", "ignored.cs"), "ignored"); Require(!inputs.HasChanged(), "build output excluded");
            var fake = BaseInputs.FromHashes(new Dictionary<string, string?> { [file] = BaseInputs.ContentHash("different"u8) }); Require(fake.HasChanged(), "records actual read hashes");
            var missing = Path.Combine(root, "missing");
            var discovery = await PluginScanner.DiscoverAsync(new[] { new PluginRoot(missing, true) }); Require(discovery.Problems.Single().Required, "required missing root problem");
            discovery = await PluginScanner.DiscoverAsync(new[] { missing }); Require(discovery.Problems.Count == 0, "optional missing root ignored");
            var plugins = Path.Combine(root, "plugins"); var plugin = Path.Combine(plugins, "one"); Directory.CreateDirectory(plugin);
            await WriteManifest(plugin, "one");
            discovery = await PluginScanner.DiscoverAsync(new[] { new PluginRoot(plugins, true) });
            Require(discovery.Plugins.Single().Required && discovery.Plugins.Single().Root == plugins, "root metadata");
            discovery = await PluginScanner.DiscoverAsync(new[] { plugin }); Require(discovery.Plugins.Single().Directory == plugin, "root itself is plugin");
            var second = Path.Combine(root, "other", "one"); Directory.CreateDirectory(second); await WriteManifest(second, "one");
            discovery = await PluginScanner.DiscoverAsync(new[] { new PluginRoot(plugins), new PluginRoot(Path.GetDirectoryName(second)!, true) });
            Require(discovery.Plugins.Single().Directory == plugin && discovery.Problems.Single().Required, "duplicate retains first and marks required problem");
            var package = Path.Combine(root, "packages", "pack"); Directory.CreateDirectory(Path.Combine(package, "nested", "child"));
            await File.WriteAllTextAsync(Path.Combine(package, PluginScanner.PackageFile), "{\"plugins\":[\"nested\",\"nested/child\"]}");
            discovery = await PluginScanner.DiscoverAsync(new[] { Path.GetDirectoryName(package)! }); Require(discovery.Problems.Single().Error.Contains("overlapping", StringComparison.Ordinal), "overlap rejected");
            var builder = new PluginBuilder(new() { DotnetPath = "unused", ArtifactDirectory = Path.Combine(root, "artifacts") });
            await using var manager = new PluginManager(new[] { plugins }, builder);
            using var watching = manager.WatchBase(inputs);
            Require(!manager.State.BaseChanged, "base begins unchanged"); File.WriteAllText(file, "modified"); Require(manager.State.BaseChanged, "base changed is content derived"); File.WriteAllText(file, "original"); Require(!manager.State.BaseChanged, "base restored mark clears");
            var errors = new List<HostError>(); var host = Hosts.Create(new() { OnError = errors.Add }); var runtime = new PluginRuntime(host);
            try
            {
                try { await runtime.LoadAsync(new([new(new(new("required", "missing.dll", "Entry"), plugin, plugins, true), null, null, "build failed")], []), [], ChordContext.Background); throw new Exception("required load accepted"); }
                catch (PluginError) { }
                Require(host.Incarnation.Length != 0, "required load settles host before error");
            }
            finally { await host.CloseAsync(ChordContext.Background); await runtime.DisposeAsync(); }
            var policyBuilder = new PluginBuilder(new() { DotnetPath = "unused", ArtifactDirectory = Path.Combine(root, "policy-artifacts"), Policy = new() { AppApi = new("test.api", ["v2", "v1"]) } });
            var refused = await policyBuilder.BuildAsync(new(new("one", "plugin.dll", Api: "test.api", ApiVersion: "v0"), plugin, plugins));
            Require(refused.Error?.Contains("admitted version", StringComparison.Ordinal) == true, "API version refused before execution");
            var wrongKind = await policyBuilder.BuildAsync(new(new("one", "plugin.dll", Api: "test.api", ApiVersion: "v2", Kind: "worker"), plugin, plugins));
            Require(wrongKind.Error?.Contains("cannot run", StringComparison.Ordinal) == true, "wrong kind refused");
            await InstallChecks(root, builder);
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task InstallChecks(string root, PluginBuilder builder)
    {
        var destination = Path.Combine(root, "install"); Directory.CreateDirectory(destination);
        var zip = Path.Combine(root, "native.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        { var entry = archive.CreateEntry(PluginScanner.ManifestFile); await using var writer = new StreamWriter(entry.Open()); await writer.WriteAsync(JsonSerializer.Serialize(new PluginManifest("installed", "plugin.dll"))); }
        await using var manager = new PluginManager(new[] { destination }, builder);
        var names = await manager.InstallPluginAsync(new("installed", zip, Archive: true), destination, ChordContext.Background);
        Require(names.SequenceEqual(new[] { "installed" }) && manager.State.InstallChanged, "atomic native archive installation marks dirty");
        try { await manager.InstallPluginAsync(new("installed", zip, Archive: true), destination, ChordContext.Background); throw new Exception("collision accepted"); }
        catch (IOException) { }
        var malicious = Path.Combine(root, "bad.zip");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create)) { archive.CreateEntry("../escape"); }
        try { await manager.InstallPluginAsync(new("bad", malicious, Archive: true), destination, ChordContext.Background); throw new Exception("traversal accepted"); }
        catch (InvalidDataException) { }
        Require(!Directory.Exists(Path.Combine(destination, "bad")) && !Directory.EnumerateDirectories(destination, ".install-*").Any(), "owned failed staging cleaned");
    }
    private static Task WriteManifest(string directory, string name) => File.WriteAllTextAsync(Path.Combine(directory, PluginScanner.ManifestFile), JsonSerializer.Serialize(new PluginManifest(name, "plugin.dll")));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Plugin regression: " + message); }
}
