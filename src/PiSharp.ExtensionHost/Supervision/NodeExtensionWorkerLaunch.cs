using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using PiSharp.ExtensionHost.Protocol;

namespace PiSharp.ExtensionHost.Supervision;

/// <summary>Closed trusted prototype: one pinned public hook, an exact official loader and explicit qualified roots.</summary>
public sealed class NodeExtensionWorkerLaunch
{
    public const string EntryRelativePath = "tools/NodeBridge/worker.mjs";
    // LF byte identities; the held plan binds all three closed-profile helper files.
    public const string EntrySha256 = "ef20cf354f3729da4488d3dd6fe860d8320233d12e62e7099f9ce8eb541708eb";
    public const string PlanSha256 = "056df456e031bbca4e627df63a8a7632c4b9359cfb09f736e5909219f3fade12";
    public const string ReferencePlanSha256 = "e55b7af94907598157809fc6da08ad1d60899c23b849d3623c28174d5d7fc622";
    public const string ReferenceManifestSha256 = "b74a7ece33b0596850266a1e1686b3c9cd1d5c978e3dcae1f07174adc3757ff9";
    public string NodePath { get; }
    public string RepositoryRoot { get; }
    public string EntryPath => Path.Combine(RepositoryRoot, EntryRelativePath.Replace('/', Path.DirectorySeparatorChar));
    public string RunRoot { get; }
    public string OracleRoot { get; }
    public string JitiRoot { get; }
    public string ReferenceRoot { get; }
    public long WorkerGeneration { get; }
    public long SessionGeneration { get; }
    private readonly Dictionary<string, (long Bytes, string Sha)> pins = new(StringComparer.OrdinalIgnoreCase);
    private NodeExtensionWorkerLaunch(string node, string repo, string run, string oracle, string jiti,
        string reference, long worker, long session)
    { NodePath = node; RepositoryRoot = repo; RunRoot = run; OracleRoot = oracle; JitiRoot = jiti;
      ReferenceRoot = reference; WorkerGeneration = worker; SessionGeneration = session; }

    public static NodeExtensionWorkerLaunch ForProtectedPaths(string nodeExecutable, string repositoryRoot,
        string freshRunRoot, string oracleRoot, string jitiRoot, string referenceRoot,
        long workerGeneration, long sessionGeneration)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Approved bridge runtime is Windows x64.");
        WorkerFrameCodec.CheckIdentity(workerGeneration); WorkerFrameCodec.CheckIdentity(sessionGeneration);
        var node = NodeWorkerLaunch.Absolute(nodeExecutable); var repo = NodeWorkerLaunch.Absolute(repositoryRoot);
        var run = NodeWorkerLaunch.Absolute(freshRunRoot); var oracle = NodeWorkerLaunch.Absolute(oracleRoot);
        var jiti = NodeWorkerLaunch.Absolute(jitiRoot); var reference = NodeWorkerLaunch.Absolute(referenceRoot);
        foreach (var path in new[] { node, repo, run, oracle, jiti, reference }) NodeWorkerLaunch.NoLinks(path);
        foreach (var path in new[] { repo, oracle, jiti, reference, Path.GetDirectoryName(node)! })
            if (NodeWorkerLaunch.Overlaps(run, path)) throw new IOException("Writable run root overlaps an immutable input.");
        if (Path.Exists(run) || !Directory.Exists(Path.GetDirectoryName(run))) throw new IOException("Fresh caller-owned run root required.");
        var result = new NodeExtensionWorkerLaunch(node, repo, run, oracle, jiti, reference, workerGeneration, sessionGeneration);
        using (NodeWorkerLaunch.OpenPinned(node, NodeWorkerLaunch.RuntimeSha256)) { }
        result.VerifyImmutable(); return result;
    }
    internal NodeWorkerLaunch AsWorkerLaunch() => NodeWorkerLaunch.FromExtension(this);
    internal void Configure(ProcessStartInfo info)
    {
        foreach (var (key, value) in new[] { ("--repo", RepositoryRoot), ("--oracle", OracleRoot),
            ("--jiti", JitiRoot), ("--reference", ReferenceRoot) }) { info.ArgumentList.Add(key); info.ArgumentList.Add(value); }
        info.Environment.Add("JITI_FS_CACHE", "false");
        info.Environment.Add("JITI_CACHE_DIR", Path.Combine(RunRoot, "temp", "jiti-cache"));
        info.Environment.Add("PI_CODING_AGENT_DIR", Path.Combine(RunRoot, "home", "agent"));
        info.Environment.Add("PISHARP_REAL_EXTENSION_ORACLE", OracleRoot);
    }
    internal HeldPins AcquireHeldPins()
    {
        VerifyImmutable(); var files = new List<(FileStream File, string Sha)>();
        try { foreach (var pair in pins) files.Add((NodeWorkerLaunch.OpenPinned(pair.Key, pair.Value.Sha), pair.Value.Sha)); return new(files); }
        catch { foreach (var pair in files) pair.File.Dispose(); throw; }
    }
    internal sealed class HeldPins(List<(FileStream File, string Sha)> files, HeldPins? predecessor = null) : IDisposable
    {
        internal bool Verify() { foreach (var pair in files) { pair.File.Position = 0;
            if (Convert.ToHexStringLower(SHA256.HashData(pair.File)) != pair.Sha) return false; } return predecessor?.Verify() ?? true; }
        public void Dispose() { try { foreach (var pair in files) pair.File.Dispose(); } finally { predecessor?.Dispose(); } }
    }
    public void VerifyImmutable()
    {
        var planPath = Path.Combine(RepositoryRoot, "compatibility/node/protected-paths-bridge.plan.json");
        Pin(planPath, PlanSha256); Pin(Path.Combine(ReferenceRoot, "compatibility/node/real-extension-reference.plan.json"), ReferencePlanSha256);
        var manifestPath = Path.Combine(ReferenceRoot, "fixtures/reference/node-real-extensions/manifest.json");
        Pin(manifestPath, ReferenceManifestSha256);
        using var own = Read(planPath); using var original = Read(Path.Combine(ReferenceRoot, "compatibility/node/real-extension-reference.plan.json"));
        using var manifest = Read(manifestPath);
        var root = own.RootElement; var old = original.RootElement; var inputs = manifest.RootElement.GetProperty("inputs");
        if (root.GetProperty("profile").GetString() != "real-protected-paths-hook-0" || root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("sourceCommit").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f") throw new IOException("Closed bridge profile differs.");
        if (!Same(old.GetProperty("oracle").GetString()!, OracleRoot)) throw new IOException("Unqualified oracle root.");
        foreach (var row in root.GetProperty("helpers").EnumerateArray()) CheckRow(RepositoryRoot, row);
        foreach (var row in old.GetProperty("controlPins").EnumerateArray()) CheckRow(ReferenceRoot, row);
        CheckRow(ReferenceRoot, old.GetProperty("archiveInspector"));
        foreach (var row in old.GetProperty("referencePins").EnumerateArray())
            CheckAbsoluteRow(row);
        foreach (var row in inputs.GetProperty("harness").EnumerateArray()) CheckRow(ReferenceRoot, row);
        CheckAbsoluteRow(inputs.GetProperty("fixture")); CheckRow(ReferenceRoot, manifest.RootElement.GetProperty("expected"), "fixtures/reference/node-real-extensions/");
        var baseline = inputs.GetProperty("base"); CheckAbsoluteRow(baseline.GetProperty("restoredReceipt"));
        using var receipt = Read(baseline.GetProperty("restoredReceipt").GetProperty("path").GetString()!);
        var fingerprint = receipt.RootElement.GetProperty("sourceFingerprint");
        if (fingerprint.GetProperty("canonicalGit").GetProperty("sha256").GetString() != old.GetProperty("source").GetProperty("canonicalFingerprint").GetString())
            throw new IOException("Canonical source receipt differs.");
        var sourceRoot = Path.Combine(OracleRoot, "upstream");
        Inventory(sourceRoot, baseline.GetProperty("sourceFiles").GetInt32(), baseline.GetProperty("sourceFingerprint").GetString()!, skipGit: true);
        var actualGit = Files(Path.Combine(sourceRoot, ".git")).Select(p => ".git/" + Path.GetRelativePath(Path.Combine(sourceRoot, ".git"), p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        var git = baseline.GetProperty("gitMetadata").EnumerateArray().ToArray();
        if (!actualGit.SequenceEqual(git.Select(r => r.GetProperty("path").GetString()!).Order(StringComparer.Ordinal))) throw new IOException("Repository metadata inventory differs.");
        foreach (var row in git) CheckRow(sourceRoot, row);
        var packages = baseline.GetProperty("packages").EnumerateArray().ToArray();
        if (packages.Length != 15) throw new IOException("Qualified dependency count differs.");
        foreach (var row in packages) Inventory(Path.Combine(OracleRoot, "node_modules", row.GetProperty("name").GetString()!),
            row.GetProperty("files").GetInt32(), row.GetProperty("sha256").GetString()!);
        var jiti = inputs.GetProperty("jiti"); var jitiReceipt = jiti.GetProperty("receipt");
        if (!Same(Path.GetDirectoryName(jitiReceipt.GetProperty("path").GetString()!)!, JitiRoot)) throw new IOException("Unqualified Jiti root.");
        CheckAbsoluteRow(jitiReceipt); CheckAbsoluteRow(jiti.GetProperty("package").GetProperty("archive"));
        var jitiFiles = jiti.GetProperty("files").EnumerateArray().ToArray();
        var jitiPackage = Path.Combine(JitiRoot, "node_modules/jiti");
        if (!Files(jitiPackage).Select(p => Path.GetRelativePath(jitiPackage, p).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .SequenceEqual(jitiFiles.Select(r => r.GetProperty("path").GetString()!).Order(StringComparer.Ordinal))) throw new IOException("Jiti publisher inventory differs.");
        foreach (var row in jitiFiles) CheckRow(jitiPackage, row);
    }
    private static bool Same(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(a), Path.GetFullPath(b));
    private static JsonDocument Read(string path) { var bytes = File.ReadAllBytes(path); if (bytes.Length > 8_388_608) throw new IOException("Metadata size limit."); return JsonDocument.Parse(bytes); }
    private void Pin(string path, string sha, long? bytes = null)
    {
        NodeWorkerLaunch.NoLinks(path); using var file = NodeWorkerLaunch.OpenPinned(path, sha);
        if (bytes is { } expected && file.Length != expected) throw new IOException("Pinned byte count differs.");
        pins[path] = (file.Length, sha);
    }
    private void CheckAbsoluteRow(JsonElement row) => Pin(NodeWorkerLaunch.Absolute(row.GetProperty("path").GetString()!),
        row.GetProperty("sha256").GetString()!, row.GetProperty("bytes").GetInt64());
    private void CheckRow(string root, JsonElement row, string prefix = "")
    {
        var relative = prefix + row.GetProperty("path").GetString();
        if (relative.Contains('\\') || relative.Split('/').Any(s => s.Length == 0 || s is "." or ".." || s.Contains(':'))) throw new IOException("Invalid admitted relative path.");
        Pin(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), row.GetProperty("sha256").GetString()!, row.GetProperty("bytes").GetInt64());
    }
    private static List<string> Files(string root)
    {
        NodeWorkerLaunch.NoLinks(root); var result = new List<string>(); long bytes = 0;
        void Visit(string directory) { foreach (var path in Directory.EnumerateFileSystemEntries(directory)) {
            var attributes = File.GetAttributes(path); if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked dependency rejected.");
            if ((attributes & FileAttributes.Directory) != 0) Visit(path); else { var length = new FileInfo(path).Length;
                bytes += length; if (length > 33_554_432 || bytes > 134_217_728 || result.Count >= 10_000) throw new IOException("Inventory limits."); result.Add(path); } } }
        Visit(root); return result;
    }
    private static void Inventory(string root, int count, string sha, bool skipGit = false)
    {
        var rows = Files(root).Select(p => (Path: Path.GetRelativePath(root, p).Replace('\\', '/'), Full: p))
            .Where(p => !skipGit || !p.Path.StartsWith(".git/", StringComparison.Ordinal)).OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
        if (rows.Length != count) throw new IOException("Immutable inventory count differs.");
        using var output = new MemoryStream(); using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) { writer.WriteStartArray();
            foreach (var row in rows) { using var file = new FileStream(row.Full, FileMode.Open, FileAccess.Read, FileShare.Read);
                writer.WriteStartObject(); writer.WriteNumber("bytes", file.Length); writer.WriteString("path", row.Path);
                writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(file))); writer.WriteEndObject(); } writer.WriteEndArray(); }
        if (Convert.ToHexStringLower(SHA256.HashData(output.ToArray())) != sha) throw new IOException("Immutable inventory digest differs.");
    }
}
