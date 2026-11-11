using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;

// Pi 1.5.0 sync, bridge.undici-gondolin-advisories (owner decision 2026-11-05, "Use safe defaults"): Pi 1.5.0's
// packages/coding-agent/package.json (f9a26365568127461c47a07a37dcb42c140a3ac5) moves undici from 8.10.2 to 8.12.3 for security
// advisories. The approved upgrade now uses Pi 1.5.2 with default attribution suppressed by pi-modules.mjs,
// and overrides undici to exactly 8.12.3. Offline: these cases read the manifest and a runtime folder written here; no
// npm and no registry.
// The original case IDs remain stable; current-package assertions follow the 1.5.2 candidate under decision 0014.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150RuntimeCases() =>
    [
        ("sync150.pi-runtime-manifest-pins-undici-and-pi-1.5.0", Sync(RuntimeManifestPins)),
        ("sync150.pi-runtime-without-the-undici-override-is-reinstalled", Sync(RuntimeWithoutOverrideIsStale)),
    ];

    private static void RuntimeManifestPins()
    {
        Equal("1.5.2", PiNodeRuntime.PiVersion, "runtime Pi version");
        Equal("8.12.3", PiNodeRuntime.UndiciVersion, "undici version");
        var manifest = PiNodeRuntime.PackageJson();
        Equal("8.12.3", manifest["overrides"]!["undici"]!.GetValue<string>(), "undici override");
        Check(manifest["dependencies"]!["undici"] is null, "undici is an override, not a direct dependency");
        var pi = manifest["dependencies"]!.AsObject().Concat(manifest["overrides"]!.AsObject())
            .Where(item => item.Key.StartsWith("@earendil-works/", StringComparison.Ordinal)).ToList();
        Equal(10, pi.Count, "Pi packages pinned (4 dependencies, 6 overrides)");
        Check(pi.All(item => item.Value!.GetValue<string>() == "1.5.2"), "every @earendil-works package at exactly 1.5.2: " + manifest.ToJsonString());
        Equal("Pi 1.5.2 packages for PiSharp extensions, with undici 8.12.3 (installed by PiSharp; do not edit)", manifest["description"]!.GetValue<string>(), "description");
    }

    // IsInstalled compares the runtime's package.json with the current manifest: a verified 1.5.2 runtime an earlier PiSharp installed
    // (no undici override) is not installed, so EnsureAsync installs it again; one installed from this manifest is.
    private static void RuntimeWithoutOverrideIsStale()
    {
        var directory = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-ext-parity-stale-runtime-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "node_modules", "@earendil-works", "pi-coding-agent"));
            File.WriteAllText(Path.Combine(directory, "node_modules", "@earendil-works", "pi-coding-agent", "package.json"), """{"name":"@earendil-works/pi-coding-agent","version":"1.5.2"}""");
            File.WriteAllText(Path.Combine(directory, PiNodeRuntime.ProvenanceFile), """{"pi":"1.5.2","policy":"no-default-attribution-v1","verified":true,"packages":[]}""");
            var earlier = PiNodeRuntime.PackageJson();
            earlier["description"] = "Pi 1.5.2 packages for PiSharp extensions (installed by PiSharp; do not edit)";
            earlier["overrides"]!.AsObject().Remove("undici");
            File.WriteAllText(Path.Combine(directory, "package.json"), earlier.ToJsonString() + "\n");
            Check(!PiNodeRuntime.IsInstalled(directory), "a runtime without the undici override is installed again");
            File.Delete(Path.Combine(directory, "package.json"));
            Check(!PiNodeRuntime.IsInstalled(directory), "a runtime without a manifest is installed again");
            // EnsureAsync writes the manifest indented; the comparison is by JSON value.
            File.WriteAllText(Path.Combine(directory, "package.json"), PiNodeRuntime.PackageJson().ToJsonString(new() { WriteIndented = true }).ReplaceLineEndings("\r\n") + "\r\n");
            Check(PiNodeRuntime.IsInstalled(directory), "a verified runtime installed from the current manifest is installed");
            var other = PiNodeRuntime.PackageJson(); other["overrides"]!["undici"] = "8.10.2";
            File.WriteAllText(Path.Combine(directory, "package.json"), other.ToJsonString());
            Check(!PiNodeRuntime.IsInstalled(directory), "a runtime with undici at another version is installed again");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
