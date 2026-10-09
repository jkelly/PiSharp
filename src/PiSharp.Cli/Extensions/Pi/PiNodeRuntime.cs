// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (getAliases: extensions resolve
// Pi's own packages, and jiti loads them) and packages/coding-agent/package.json (the Pi 1.1.0 dependency set).
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>
/// The Pi packages extensions run against (owner decision, 2026-10-08): on first extension use the exact Pi 1.1.0 packages are
/// installed with the user's npm into <c>&lt;agentDir&gt;/pisharp/pi-runtime/1.1.0</c> (or <c>PISHARP_PI_RUNTIME_DIR</c>), every
/// installed package's lockfile integrity is checked against the npm registry's <c>dist.integrity</c>, and the result is recorded in
/// <c>provenance.json</c>. The Node host then loads extensions with Pi's own jiti and aliases, as upstream's loader does. Offline
/// (<c>PI_OFFLINE</c>) or without npm, PiSharp's built-in compatibility modules are the fallback, and the run says so.
/// </summary>
internal static class PiNodeRuntime
{
    internal const string PiVersion = "1.1.0";
    internal const string ProvenanceFile = "provenance.json";

    /// <summary>The pinned set: the packages upstream's getAliases resolves for extensions, their loader (jiti) and, through overrides,
    /// every @earendil-works package of the 1.1.0 release at exactly 1.1.0.</summary>
    internal static JsonObject PackageJson() => new()
    {
        ["name"] = "pisharp-pi-runtime", ["private"] = true,
        ["description"] = "Pi " + PiVersion + " packages for PiSharp extensions (installed by PiSharp; do not edit)",
        ["dependencies"] = new JsonObject
        {
            ["@earendil-works/pi-coding-agent"] = PiVersion, ["@earendil-works/pi-ai"] = PiVersion, ["@earendil-works/pi-tui"] = PiVersion,
            ["@earendil-works/pi-agent-core"] = PiVersion, ["typebox"] = "1.3.27", ["jiti"] = "2.7.0"
        },
        ["overrides"] = new JsonObject
        {
            ["@earendil-works/pi-ai"] = PiVersion, ["@earendil-works/pi-tui"] = PiVersion, ["@earendil-works/pi-agent-core"] = PiVersion,
            ["@earendil-works/pi-mcp"] = PiVersion, ["@earendil-works/pi-codemode"] = PiVersion, ["@earendil-works/chord"] = PiVersion,
            ["@earendil-works/pi-telemetry"] = PiVersion
        }
    };

    internal static string Directory(string agentDir, Func<string, string?> environment) =>
        environment("PISHARP_PI_RUNTIME_DIR") is { Length: > 0 } configured ? Path.GetFullPath(configured)
            : Path.Combine(agentDir, "pisharp", "pi-runtime", PiVersion);

    /// <summary>The installed and verified <c>node_modules</c>, installing first when needed. Null with a reason when the run falls
    /// back to the built-in compatibility modules.</summary>
    internal static async Task<(string? NodeModules, string? Fallback)> EnsureAsync(string agentDir, Func<string, string?> environment,
        TextWriter output, PiSharp.Cli.Packages.PiPackageProcesses? processes, Func<HttpClient>? createHttp, CancellationToken token)
    {
        var directory = Directory(agentDir, environment);
        var nodeModules = Path.Combine(directory, "node_modules");
        if (IsInstalled(directory)) return (nodeModules, null);
        if (PiCommand.IsTruthyEnvFlag(environment("PI_OFFLINE")))
            return (null, "offline (PI_OFFLINE): Pi " + PiVersion + " packages are not installed; extensions use PiSharp's built-in compatibility modules");
        processes ??= new() { Output = output, ErrorOutput = output };
        var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        try { processes.RunSync(npm, ["--version"]); }
        catch (Exception error) when (error is PiSharp.Cli.Packages.PiPackageException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return (null, "npm is not available: Pi " + PiVersion + " packages cannot be installed; extensions use PiSharp's built-in compatibility modules"); }
        // Installed into a staging folder and moved into place, so a concurrent or interrupted install never leaves a partial runtime.
        var staging = directory + ".staging-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "package.json"), PackageJson().ToJsonString(Indented) + "\n", token).ConfigureAwait(false);
            await output.WriteLineAsync($"Installing Pi {PiVersion} packages for extensions into {directory} (npm)...").ConfigureAwait(false);
            await processes.RunAsync(npm, ["install", "--no-audit", "--no-fund", "--loglevel=error"], staging, token).ConfigureAwait(false);
            var provenance = await VerifyAsync(staging, createHttp ?? (() => new HttpClient()), token).ConfigureAwait(false);
            provenance["directory"] = directory;
            provenance["node"] = Capture(processes, OperatingSystem.IsWindows() ? "node.exe" : "node");
            provenance["npm"] = Capture(processes, npm);
            await File.WriteAllTextAsync(Path.Combine(staging, ProvenanceFile), provenance.ToJsonString(Indented) + "\n", token).ConfigureAwait(false);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            if (System.IO.Directory.Exists(directory) && !IsInstalled(directory)) System.IO.Directory.Delete(directory, true);
            if (!System.IO.Directory.Exists(directory)) System.IO.Directory.Move(staging, directory);
            if (!IsInstalled(directory)) throw new InvalidDataException("the installed packages could not be moved into " + directory);
            var packages = provenance["packages"]!.AsArray().Count;
            await output.WriteLineAsync($"Installed Pi {PiVersion} packages ({packages} packages, integrity verified against {string.Join(", ", provenance["registries"]!.AsArray().Select(item => item!.GetValue<string>()))}); provenance: {Path.Combine(directory, ProvenanceFile)}").ConfigureAwait(false);
            return (nodeModules, null);
        }
        catch (Exception error) when (error is PiSharp.Cli.Packages.PiPackageException or IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            if (IsInstalled(directory)) return (nodeModules, null);
            return (null, $"installing Pi {PiVersion} packages failed ({error.Message}); extensions use PiSharp's built-in compatibility modules");
        }
        finally
        {
            try { if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string? Capture(PiSharp.Cli.Packages.PiPackageProcesses processes, string command)
    {
        try { return processes.RunSync(command, ["--version"]); }
        catch (Exception error) when (error is PiSharp.Cli.Packages.PiPackageException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>Installed: a provenance record of this version whose packages verified, and the coding agent present.</summary>
    internal static bool IsInstalled(string directory)
    {
        try
        {
            var provenance = Path.Combine(directory, ProvenanceFile);
            if (!File.Exists(provenance) || JsonNode.Parse(File.ReadAllText(provenance)) is not JsonObject record) return false;
            return record["pi"]?.GetValue<string>() == PiVersion && record["verified"]?.GetValue<bool>() == true &&
                File.Exists(Path.Combine(directory, "node_modules", "@earendil-works", "pi-coding-agent", "package.json"));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Every package of the npm lockfile: its integrity must equal the registry's <c>dist.integrity</c> of that version (the
    /// registry the package was resolved from). Returns the provenance record; a mismatch throws.</summary>
    internal static async Task<JsonObject> VerifyAsync(string directory, Func<HttpClient> createHttp, CancellationToken token)
    {
        var lockfile = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "package-lock.json"), token).ConfigureAwait(false))!.AsObject();
        var rows = new List<(string Name, string Version, string Resolved, string Integrity)>();
        foreach (var (key, value) in lockfile["packages"]!.AsObject())
        {
            // Bundled dependencies ship inside their parent's verified tarball; links are not packages.
            if (key.Length == 0 || value is not JsonObject entry || entry["link"] is not null || entry["inBundle"] is not null) continue;
            var name = key[(key.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];
            var version = entry["version"]?.GetValue<string>(); var resolved = entry["resolved"]?.GetValue<string>(); var integrity = entry["integrity"]?.GetValue<string>();
            if (version is null || resolved is null || integrity is null) throw new InvalidDataException($"{name} has no registry provenance in package-lock.json");
            rows.Add((name, version, resolved, integrity));
        }
        using var http = createHttp();
        using var gate = new SemaphoreSlim(8);
        var checks = rows.DistinctBy(row => (row.Name, row.Version)).Select(async row =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var marker = "/" + row.Name + "/-/";
                var index = row.Resolved.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) throw new InvalidDataException($"{row.Name}@{row.Version} was not resolved from a registry: {row.Resolved}");
                var registry = row.Resolved[..index];
                var metadata = await http.GetFromJsonAsync<JsonElement>($"{registry}/{row.Name.Replace("/", "%2f", StringComparison.Ordinal)}/{row.Version}", token).ConfigureAwait(false);
                var expected = metadata.GetProperty("dist").GetProperty("integrity").GetString();
                if (expected != row.Integrity) throw new InvalidDataException($"{row.Name}@{row.Version} integrity differs from the registry ({row.Integrity} vs {expected})");
                return registry;
            }
            finally { gate.Release(); }
        }).ToList();
        var registries = (await Task.WhenAll(checks).ConfigureAwait(false)).Distinct(StringComparer.Ordinal).ToList();
        return new JsonObject
        {
            ["pi"] = PiVersion, ["verified"] = true, ["installedAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["registries"] = new JsonArray([.. registries.Select(item => (JsonNode)item)]),
            ["verification"] = "package-lock.json integrity of every package equals the registry's dist.integrity for that version",
            ["packages"] = new JsonArray([.. rows.OrderBy(row => row.Name, StringComparer.Ordinal).Select(row => (JsonNode)new JsonObject
            { ["name"] = row.Name, ["version"] = row.Version, ["resolved"] = row.Resolved, ["integrity"] = row.Integrity })])
        };
    }
}

/// <summary>Install progress and npm output, line by line, to the run's standard error (or this process's when the run has none).</summary>
internal sealed class PiNodeRuntimeOutput(Action<string>? line) : TextWriter
{
    private readonly System.Text.StringBuilder _pending = new();
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(char value)
    {
        lock (_pending)
        {
            if (value == '\r') return;
            if (value != '\n') { _pending.Append(value); return; }
            var text = _pending.ToString(); _pending.Clear();
            if (line is null) Console.Error.WriteLine(text); else line(text);
        }
    }
}
