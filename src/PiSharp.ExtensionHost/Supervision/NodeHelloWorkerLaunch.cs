using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PiSharp.ExtensionHost.Supervision;

/// <summary>Closed Hello successor; composes the predecessor's read-only qualification without starting its worker.</summary>
public sealed class NodeHelloWorkerLaunch
{
    public const string EntryRelativePath = "tools/NodeHelloBridge/worker.mjs";
    public const string EntrySha256 = "8694db158d7ff6b0160e3f730d408027c2b3be3b9e4eae7c57db906b9a8f6571";
    public const string PlanSha256 = "53882f5851f96528397c8e5fd3d7c06c4d6bde973ae8ad8ce00c822950aea858";
    public const string PreparationPlanSha256 = "74e3b17b65e024fa07d77f5615acbcaa2c18b5fff31f21700c5e46196d64e19c";
    public const string PreparationManifestSha256 = "d241bc12bd2caeb63d44b51bebf38efda21b4d60e18073fed2dd8157bcc53c37";
    public const string PreparationExpectedSha256 = "f005d29a78a238bd30c179ffe27a3eb1a429619edab8eb549004aaa0b78112ae";
    private readonly NodeExtensionWorkerLaunch predecessor;
    private readonly Dictionary<string, (long Bytes, string Sha)> pins = new(StringComparer.OrdinalIgnoreCase);
    public string NodePath => predecessor.NodePath;
    public string RepositoryRoot => predecessor.RepositoryRoot;
    public string EntryPath => Path.Combine(RepositoryRoot, EntryRelativePath.Replace('/', Path.DirectorySeparatorChar));
    public string RunRoot => predecessor.RunRoot;
    public string OracleRoot => predecessor.OracleRoot;
    public string JitiRoot => predecessor.JitiRoot;
    public string ReferenceRoot => predecessor.ReferenceRoot;
    public string PreparationReferenceRoot { get; }
    public long WorkerGeneration => predecessor.WorkerGeneration;
    public long SessionGeneration => predecessor.SessionGeneration;
    private NodeHelloWorkerLaunch(NodeExtensionWorkerLaunch qualifiedInputs, string preparationReference)
    { predecessor = qualifiedInputs; PreparationReferenceRoot = preparationReference; }

    public static NodeHelloWorkerLaunch ForHello(string nodeExecutable, string repositoryRoot, string freshRunRoot,
        string oracleRoot, string jitiRoot, string referenceRoot, string helloPreparationReference,
        long workerGeneration, long sessionGeneration)
    {
        var inputs = NodeExtensionWorkerLaunch.ForProtectedPaths(nodeExecutable, repositoryRoot, freshRunRoot,
            oracleRoot, jitiRoot, referenceRoot, workerGeneration, sessionGeneration);
        var preparation = NodeWorkerLaunch.Absolute(helloPreparationReference); NodeWorkerLaunch.NoLinks(preparation);
        if (!Directory.Exists(preparation) || NodeWorkerLaunch.Overlaps(inputs.RunRoot, preparation))
            throw new IOException("Fresh Hello run root overlaps or lacks the explicit preparation reference.");
        var launch = new NodeHelloWorkerLaunch(inputs, preparation); launch.VerifyImmutable(); return launch;
    }
    internal NodeWorkerLaunch AsWorkerLaunch() => NodeWorkerLaunch.FromHelloExtension(this);
    internal void Configure(ProcessStartInfo info)
    {
        predecessor.Configure(info);
        info.ArgumentList.Add("--hello-reference"); info.ArgumentList.Add(PreparationReferenceRoot);
    }
    public void VerifyImmutable()
    {
        predecessor.VerifyImmutable();
        var planPath = Path.Combine(RepositoryRoot, "compatibility/node/hello-bridge.plan.json"); Pin(planPath, PlanSha256);
        Pin(Path.Combine(PreparationReferenceRoot, "compatibility/node/hello-preparation-reference.plan.json"), PreparationPlanSha256);
        Pin(Path.Combine(PreparationReferenceRoot, "fixtures/reference/node-hello-preparation/manifest.json"), PreparationManifestSha256);
        Pin(Path.Combine(PreparationReferenceRoot, "fixtures/reference/node-hello-preparation/expected.json"), PreparationExpectedSha256);
        using var plan = Read(planPath); var value = plan.RootElement;
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("profile").GetString() != "real-hello-tool-0" ||
            value.GetProperty("sourceCommit").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" ||
            value.GetProperty("preparationExpectedSha256").GetString() != PreparationExpectedSha256)
            throw new IOException("Closed Hello profile identity differs.");
        foreach (var row in value.GetProperty("helpers").EnumerateArray()) CheckRow(RepositoryRoot, row);
        foreach (var row in value.GetProperty("preparationReference").EnumerateArray()) CheckRow(PreparationReferenceRoot, row);
        using var manifest = Read(Path.Combine(PreparationReferenceRoot, "fixtures/reference/node-hello-preparation/manifest.json"));
        foreach (var row in manifest.RootElement.GetProperty("inputs").GetProperty("harness").EnumerateArray()) CheckRow(PreparationReferenceRoot, row);
        foreach (var row in value.GetProperty("sourcePins").EnumerateArray()) CheckRow(Path.Combine(OracleRoot, "upstream"), row);
    }
    internal NodeExtensionWorkerLaunch.HeldPins AcquireHeldPins()
    {
        VerifyImmutable(); var held = predecessor.AcquireHeldPins(); var files = new List<(FileStream File, string Sha)>();
        try
        {
            foreach (var pair in pins) files.Add((NodeWorkerLaunch.OpenPinned(pair.Key, pair.Value.Sha), pair.Value.Sha));
            return new(files, held);
        }
        catch { foreach (var pair in files) pair.File.Dispose(); held.Dispose(); throw; }
    }
    private void Pin(string path, string sha, long? bytes = null)
    {
        NodeWorkerLaunch.NoLinks(path); using var input = NodeWorkerLaunch.OpenPinned(path, sha);
        if (bytes is { } expected && input.Length != expected) throw new IOException("Hello input byte count differs.");
        pins[path] = (input.Length, sha);
    }
    private void CheckRow(string root, JsonElement row)
    {
        var relative = row.GetProperty("path").GetString()!;
        if (relative.Contains('\\') || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new IOException("Invalid Hello admitted relative path.");
        Pin(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), row.GetProperty("sha256").GetString()!, row.GetProperty("bytes").GetInt64());
    }
    private static JsonDocument Read(string path)
    { var bytes = File.ReadAllBytes(path); if (bytes.Length > 8_388_608) throw new IOException("Hello metadata byte limit."); return JsonDocument.Parse(bytes); }
}
