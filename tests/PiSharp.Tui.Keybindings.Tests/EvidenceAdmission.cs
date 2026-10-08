using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Tui.Input;

internal sealed class EvidenceAdmission
{
    internal string Candidate { get; }
    internal string Tree { get; }
    internal string ReviewRoot { get; }
    internal string ManifestSha256 { get; }
    internal string ReceiptSha256 { get; }
    internal int SourceFilesVerified { get; }
    internal object[] Assemblies { get; }
    internal object Host { get; }

    // Six arguments: fresh report, explicit review root, sealed source manifest/hash, permitted build receipt/hash.
    internal EvidenceAdmission(string[] args)
    {
        if (args.Length != 6 || File.Exists(args[0]) || Directory.Exists(args[0]))
            throw new ArgumentException("Fresh report, review root, manifest/hash, permitted build receipt/hash required");
        ReviewRoot = Path.GetFullPath(args[1]);
        using var manifest = PinnedJson(args[2], args[3]);
        using var receipt = PinnedJson(args[4], args[5]);
        ManifestSha256 = args[3]; ReceiptSha256 = args[5];
        Candidate = manifest.RootElement.GetProperty("candidate").GetString()!;
        Tree = manifest.RootElement.GetProperty("tree").GetString()!;
        if (Candidate.Length != 40 || !Candidate.All(Uri.IsHexDigit) || Tree.Length != 40 || !Tree.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid immutable Git identity");
        var proof = receipt.RootElement;
        if (!proof.GetProperty("executionPermitted").GetBoolean() || proof.GetProperty("candidate").GetString() != Candidate ||
            proof.GetProperty("tree").GetString() != Tree || proof.GetProperty("sourceManifestSha256").GetString() != ManifestSha256 ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(proof.GetProperty("reviewRoot").GetString()!), ReviewRoot))
            throw new InvalidDataException("Permitted build receipt does not bind this candidate and review root");
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        if (files.Length == 0) throw new InvalidDataException("Empty source manifest");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var relative = file.GetProperty("relative").GetString()!;
            if (!names.Add(relative)) throw new InvalidDataException("Duplicate source pin");
            VerifyFile(InsideRoot(relative), file); // Producer absolute paths are provenance only.
        }
        SourceFilesVerified = files.Length;
        var actualAssemblies = new[] { "PiSharp.Agent", "PiSharp.AI", "PiSharp.Cli", "PiSharp.CodingAgent", "PiSharp.Contracts",
            "PiSharp.Extensions.Abstractions", "PiSharp.Extensions.Agent", "PiSharp.Extensions.Runtime", "PiSharp.Rpc", "PiSharp.Sessions",
            "PiSharp.Tools", "PiSharp.Tui" }.Select(name => Assembly.Load(name)).Append(Assembly.GetExecutingAssembly()).ToArray();
        var assemblyPins = proof.GetProperty("assemblies").EnumerateArray().ToArray();
        if (assemblyPins.Length != actualAssemblies.Length) throw new InvalidDataException("Exactly twelve product and one consumer DLL pins required");
        Assemblies = actualAssemblies.Select(assembly =>
        {
            var name = assembly.GetName().Name!;
            var pin = assemblyPins.Single(value => value.GetProperty("name").GetString() == name);
            var location = Path.GetFullPath(assembly.Location);
            if (!StringComparer.OrdinalIgnoreCase.Equals(location, InsideRoot(pin.GetProperty("relative").GetString()!)))
                throw new InvalidDataException("Executing DLL location differs from receipt");
            VerifyFile(location, pin);
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (version is null || !version.Contains(Candidate, StringComparison.OrdinalIgnoreCase) ||
                version != pin.GetProperty("informationalVersion").GetString())
                throw new InvalidDataException("Executing DLL informational version differs from candidate receipt");
            return (object)new { name, path = location, sha256 = Hash(location), informationalVersion = version };
        }).ToArray();
        var host = proof.GetProperty("host");
        var hostPath = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidDataException("Host path unavailable"));
        if (!StringComparer.OrdinalIgnoreCase.Equals(hostPath, Path.GetFullPath(host.GetProperty("path").GetString()!)))
            throw new InvalidDataException("Executing host location differs from receipt");
        VerifyFile(hostPath, host); Host = new { path = hostPath, sha256 = Hash(hostPath), bytes = new FileInfo(hostPath).Length };
        foreach (var name in new[] { "authored-keybinding-cases.json", "source-defaults-inventory.json", "authored-configuration-loading-cases.json" })
        {
            var source = InsideRoot("tests/PiSharp.Tui.Keybindings.Tests/fixtures/" + name);
            var output = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
            if (!names.Contains("tests/PiSharp.Tui.Keybindings.Tests/fixtures/" + name) || Hash(source) != Hash(output))
                throw new InvalidDataException("Executing fixture differs from sealed source");
        }
    }

    private string InsideRoot(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("Expected relative path");
        var full = Path.GetFullPath(Path.Combine(ReviewRoot, relative));
        var fromRoot = Path.GetRelativePath(ReviewRoot, full);
        if (fromRoot == ".." || fromRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(fromRoot))
            throw new InvalidDataException("Path escapes explicit review root");
        return full;
    }

    internal static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static JsonDocument PinnedJson(string file, string expected)
    {
        var bytes = File.ReadAllBytes(file);
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != expected) throw new InvalidDataException("Evidence pin mismatch");
        return JsonDocument.Parse(bytes);
    }
    private static void VerifyFile(string path, JsonElement pin)
    {
        if (new FileInfo(path).Length != pin.GetProperty("bytes").GetInt64() || Hash(path) != pin.GetProperty("sha256").GetString())
            throw new InvalidDataException("Physical pin mismatch: " + path);
    }
}
