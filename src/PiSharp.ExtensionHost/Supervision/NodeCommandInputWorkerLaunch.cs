using System.Diagnostics;
using System.Text.Json;

namespace PiSharp.ExtensionHost.Supervision;

/// <summary>Closed whole Commands/Input successor; immutable predecessor qualification is composed read-only.</summary>
public sealed class NodeCommandInputWorkerLaunch
{
    public const string AdmissionRevision = "bounded-original-suppliers-1";
    public const string EntryRelativePath = "tools/NodeCommandInputBridge/worker.mjs";
    public const string EntrySha256 = "61ed16f4f4b7001ff3c0e2153e9df9e9e1d46855b4eb0ea9da0f397e085bb03c";
    public const string PlanSha256 = "b2927bd5329bf0ce495e0fd08012a76b5c7f490bf1b65165f20cb5b07e7228f9";
    public const string SourceReferencePlanSha256 = "7eae052f5eb4447ccb2c15c2140684f6bb6260a9e1c28a8437d29dce5de759f4";
    public const string SourceReferenceManifestSha256 = "5619550b6b561596dcdb253106879eb976554d6c9daa8c8a5fb189d95a825900";
    public const string SourceReferenceExpectedSha256 = "d74d53610906cb41ec6c2ad8a10f57d242c2a2846631a0ec4901ef5a3225930d";
    private readonly NodeExtensionWorkerLaunch predecessor;
    private readonly Dictionary<string, (long Bytes, string Sha)> pins = new(StringComparer.OrdinalIgnoreCase);
    public string NodePath => predecessor.NodePath;
    public string RepositoryRoot => predecessor.RepositoryRoot;
    public string EntryPath => Path.Combine(RepositoryRoot, EntryRelativePath.Replace('/', Path.DirectorySeparatorChar));
    public string RunRoot => predecessor.RunRoot;
    public string OracleRoot => predecessor.OracleRoot;
    public string JitiRoot => predecessor.JitiRoot;
    public string ReferenceRoot => predecessor.ReferenceRoot;
    public string CommandInputReferenceRoot { get; }
    public long WorkerGeneration => predecessor.WorkerGeneration;
    public long SessionGeneration => predecessor.SessionGeneration;
    private NodeCommandInputWorkerLaunch(NodeExtensionWorkerLaunch qualifiedInputs, string sourceReference)
    { predecessor = qualifiedInputs; CommandInputReferenceRoot = sourceReference; }
    public static NodeCommandInputWorkerLaunch ForCommandsAndInput(string nodeExecutable, string repositoryRoot, string freshRunRoot,
        string oracleRoot, string jitiRoot, string referenceRoot, string commandInputReference,
        long workerGeneration, long sessionGeneration)
    {
        var inputs = NodeExtensionWorkerLaunch.ForProtectedPaths(nodeExecutable, repositoryRoot, freshRunRoot,
            oracleRoot, jitiRoot, referenceRoot, workerGeneration, sessionGeneration);
        var source = NodeWorkerLaunch.Absolute(commandInputReference); NodeWorkerLaunch.NoLinks(source);
        if (!Directory.Exists(source) || NodeWorkerLaunch.Overlaps(inputs.RunRoot, source))
            throw new IOException("Fresh Commands/Input run root overlaps or lacks the explicit source reference.");
        var launch = new NodeCommandInputWorkerLaunch(inputs, source); launch.VerifyImmutable(); return launch;
    }
    internal NodeWorkerLaunch AsWorkerLaunch() => NodeWorkerLaunch.FromCommandInputExtension(this);
    internal void Configure(ProcessStartInfo info)
    {
        predecessor.Configure(info);
        info.ArgumentList.Add("--command-input-reference"); info.ArgumentList.Add(CommandInputReferenceRoot);
        // Closed profile locale is explicit; no inherited machine timezone is consulted by the corpus clock.
        info.Environment.Add("TZ", "UTC"); info.Environment.Add("LANG", "en_US.UTF-8"); info.Environment.Add("LC_ALL", "en_US.UTF-8");
    }
    public void VerifyImmutable()
    {
        predecessor.VerifyImmutable();
        var planPath = Path.Combine(RepositoryRoot, "compatibility/node/command-input-bridge.plan.json"); Pin(planPath, PlanSha256);
        Pin(Path.Combine(CommandInputReferenceRoot, "compatibility/node/command-input-reference.plan.json"), SourceReferencePlanSha256);
        Pin(Path.Combine(CommandInputReferenceRoot, "fixtures/reference/node-command-input/manifest.json"), SourceReferenceManifestSha256);
        Pin(Path.Combine(CommandInputReferenceRoot, "fixtures/reference/node-command-input/expected.json"), SourceReferenceExpectedSha256);
        using var plan = Read(planPath); var value = plan.RootElement;
        if (value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("profile").GetString() != "real-command-input-0" ||
            value.GetProperty("admissionRevision").GetString() != AdmissionRevision ||
            value.GetProperty("sourceCommit").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" ||
            value.GetProperty("sourceReferenceExpectedSha256").GetString() != SourceReferenceExpectedSha256)
            throw new IOException("Closed Commands/Input profile identity differs.");
        foreach (var row in value.GetProperty("helpers").EnumerateArray()) CheckRow(RepositoryRoot, row);
        var supplier = value.GetProperty("originalNamespaceInjection");
        if (supplier.GetProperty("profile").GetString() != AdmissionRevision ||
            supplier.GetProperty("provider").GetString() != "tools/NodeCompatibility/OriginalPluginMapping/live-original-virtual-modules.mjs" ||
            supplier.GetProperty("boundedSuppliedSpecifiers").GetInt32() != 12 ||
            !supplier.GetProperty("rendererTransportImplemented").GetBoolean() || supplier.GetProperty("wholeNamespaceParity").GetBoolean())
            throw new IOException("Closed original namespace supplier identity differs.");
        foreach (var row in supplier.GetProperty("sourcePins").EnumerateArray()) CheckRow(Path.Combine(OracleRoot, "upstream"), row);
        foreach (var row in value.GetProperty("sourceReference").EnumerateArray()) CheckRow(CommandInputReferenceRoot, row);
        using var manifest = Read(Path.Combine(CommandInputReferenceRoot, "fixtures/reference/node-command-input/manifest.json"));
        foreach (var row in manifest.RootElement.GetProperty("inputs").GetProperty("harness").EnumerateArray()) CheckRow(CommandInputReferenceRoot, row);
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
        if (bytes is { } expected && input.Length != expected) throw new IOException("Commands/Input input byte count differs."); pins[path] = (input.Length, sha);
    }
    private void CheckRow(string root, JsonElement row)
    {
        var relative = row.GetProperty("path").GetString()!;
        if (relative.Contains('\\') || relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':')))
            throw new IOException("Invalid Commands/Input admitted relative path.");
        Pin(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), row.GetProperty("sha256").GetString()!, row.GetProperty("bytes").GetInt64());
    }
    private static JsonDocument Read(string path)
    { var bytes = File.ReadAllBytes(path); if (bytes.Length > 8_388_608) throw new IOException("Commands/Input metadata byte limit."); return JsonDocument.Parse(bytes); }
}
