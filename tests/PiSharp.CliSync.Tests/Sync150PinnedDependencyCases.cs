using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

// Pi 1.5.0 (f9a26365568127461c47a07a37dcb42c140a3ac5, MIT): coding-agent 1.3.0 pins every internal @earendil-works dependency to the
// exact release version (packages/coding-agent/package.json, scripts/sync-versions.js), and scripts/check-pinned-deps.mjs fails the
// build on any range. PiSharp's counterpart (cli.pinned-internal-dependencies, owner decision 2026-11-05): every NuGet SDK package
// depends on its PiSharp siblings at exactly [<version>], written by tools/release/PiSharp.NativeSdk.targets and enforced by
// Assert-PiSharpNativeSdkPackage (tools/release/native-sdk-package.ps1). Third-party pins are unchanged. Offline: the pack case runs
// `dotnet pack --no-restore --no-build` on libraries this suite's own build produced, into a temporary folder.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> Sync150PinnedDependencyCases() =>
    [
        ("sync150.native-sdk-pack-pins-siblings-exactly", NativeSdkPackPinsSiblingsExactly),
        ("sync150.native-sdk-validator-accepts-only-exact-sibling-pins", Sync(NativeSdkValidatorAcceptsOnlyExactSiblingPins)),
    ];

    /// <summary>The release script's pack of a library (build-release-packages.ps1: the same targets file and release properties),
    /// without restore or build: PiSharp.Agent's and PiSharp.Codemode's .nuspec depend on their PiSharp siblings at exactly the
    /// release version, and PiSharp.Codemode keeps Jint at the exact pin of its PackageReference.</summary>
    private static async Task NativeSdkPackPinsSiblingsExactly()
    {
        var repo = PinsRepoRoot();
        // This suite is built as bin/<Configuration>/net10.0; its project references are built the same way.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        const string version = "9.8.7-pins.1";
        var root = Temp("native-sdk-pins-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var provenance = Path.Combine(root, "provenance.json");
            File.WriteAllText(provenance, "{}");
            async Task<IReadOnlyDictionary<string, string>> Pack(string library)
            {
                var project = Path.Combine(repo, "src", library, library + ".csproj");
                var assembly = Path.Combine(repo, "src", library, "bin", configuration, "net10.0", library + ".dll");
                Check(File.Exists(assembly), $"{library} is not built at {assembly}; the suite's build produces it");
                var output = Path.Combine(root, "out");
                await RunDotnet(root,
                [
                    "pack", project, "-c", configuration, "--no-restore", "--no-build", "-o", output, "--nologo", "-nodeReuse:false", "--disable-build-servers",
                    "-p:VersionPrefix=9.8.7", "-p:VersionSuffix=pins.1", "-p:PiSharpReleaseVersion=" + version,
                    "-p:PiSharpSourceCommit=0123456789abcdef0123456789abcdef01234567", "-p:ContinuousIntegrationBuild=true",
                    "-p:CustomAfterMicrosoftCommonTargets=" + Path.Combine(repo, "tools", "release", "PiSharp.NativeSdk.targets"),
                    "-p:PiSharpProvenanceFile=" + provenance,
                    // The .nuspec goes to the temporary folder too, not the project's obj folder.
                    "-p:NuspecOutputPath=" + Path.Combine(root, "nuspec", library) + Path.DirectorySeparatorChar,
                ]);
                using var archive = ZipFile.OpenRead(Path.Combine(output, $"{library}.{version}.nupkg"));
                using var stream = archive.GetEntry(library + ".nuspec")!.Open();
                var metadata = XDocument.Load(stream).Root!.Elements().Single(element => element.Name.LocalName == "metadata");
                Equal(version, metadata.Elements().Single(element => element.Name.LocalName == "version").Value, library + " version");
                var groups = metadata.Elements().Single(element => element.Name.LocalName == "dependencies").Elements().ToList();
                Check(groups.Count == 1 && groups[0].Name.LocalName == "group" && (string?)groups[0].Attribute("targetFramework") == "net10.0", library + ": one net10.0 dependency group");
                return groups[0].Elements().ToDictionary(element => (string)element.Attribute("id")!, element => (string)element.Attribute("version")!, StringComparer.Ordinal);
            }

            // The sibling sets are native-sdk-package.ps1's $siblings for these two packages.
            var agent = await Pack("PiSharp.Agent");
            Names(["PiSharp.AI", "PiSharp.Contracts"], agent.Keys.Order(StringComparer.Ordinal), "PiSharp.Agent dependencies");
            var codemode = await Pack("PiSharp.Codemode");
            Names(["Jint", "PiSharp.Contracts", "PiSharp.Extensions.Abstractions"], codemode.Keys.Order(StringComparer.Ordinal), "PiSharp.Codemode dependencies");
            foreach (var (library, dependencies) in new[] { ("PiSharp.Agent", agent), ("PiSharp.Codemode", codemode) })
                foreach (var (id, range) in dependencies.Where(pair => pair.Key.StartsWith("PiSharp.", StringComparison.Ordinal)))
                    Equal("[" + version + "]", range, $"{library} -> {id}");
            var jint = XDocument.Load(Path.Combine(repo, "src", "PiSharp.Codemode", "PiSharp.Codemode.csproj")).Descendants("PackageReference")
                .Single(reference => (string?)reference.Attribute("Include") == "Jint").Attribute("Version")!.Value;
            Check(jint.StartsWith('[') && jint.EndsWith(']'), "Jint's PackageReference is an exact pin: " + jint);
            Equal(jint, codemode["Jint"], "PiSharp.Codemode -> Jint (third-party pin unchanged)");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>The checks around the pack: the targets rewrite PiSharp project references to [version] right after NuGet reads their
    /// versions, the release script packs every library with those targets and validates it, and the validator accepts only the
    /// exact [version] form for siblings (third-party packages keep their pinned versions).</summary>
    private static void NativeSdkValidatorAcceptsOnlyExactSiblingPins()
    {
        var repo = PinsRepoRoot();
        var targets = XDocument.Load(Path.Combine(repo, "tools", "release", "PiSharp.NativeSdk.targets")).Root!;
        var pin = targets.Elements("Target").Single(target => (string?)target.Attribute("Name") == "PinPiSharpSiblingVersions");
        Equal("_GetProjectReferenceVersions", (string?)pin.Attribute("AfterTargets"), "pin target runs after NuGet reads project reference versions");
        Equal("'$(PiSharpNativeSdkPackage)' == 'true'", (string?)pin.Attribute("Condition"), "pin target applies to the SDK packages");
        var projectVersion = pin.Descendants("_ProjectReferencesWithVersions").Elements("ProjectVersion").Single();
        Equal("[%(ProjectVersion)]", projectVersion.Value, "pinned ProjectVersion");
        Check(((string?)projectVersion.Attribute("Condition") ?? "").Contains("StartsWith('PiSharp.')", StringComparison.Ordinal), "only PiSharp project references are pinned");

        var script = File.ReadAllText(Path.Combine(repo, "tools", "release", "build-release-packages.ps1"));
        Check(script.Contains("\"-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $repo 'tools/release/PiSharp.NativeSdk.targets')\"", StringComparison.Ordinal),
            "the release script packs the libraries with PiSharp.NativeSdk.targets");
        Check(script.Contains("Assert-PiSharpNativeSdkPackage -Package $package", StringComparison.Ordinal), "the release script validates every library package");

        var allowed = File.ReadAllLines(Path.Combine(repo, "tools", "release", "native-sdk-package.ps1")).Select(line => line.Trim())
            .Where(line => line.StartsWith("$allowed=", StringComparison.Ordinal)).ToList();
        Equal(1, allowed.Count, "one dependency range rule");
        Equal("$allowed=if($external.ContainsKey($id)){@($external[$id],('['+$external[$id]+']'))}else{@('['+$Version+']')}", allowed[0],
            "siblings accept only [$Version]; third-party packages keep their pinned versions");
    }

    private static string PinsRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "tools", "release", "PiSharp.NativeSdk.targets"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Runs the dotnet CLI offline from a folder outside the repository, without MSBuild state inherited from the process that
    /// started this suite and without build servers left behind.</summary>
    private static async Task RunDotnet(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet")
        { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(name);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"; start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("dotnet " + string.Join(' ', arguments) + " timed out"); }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments[0]} exited {process.ExitCode}:\n{await stdout}\n{await stderr}");
    }
}
