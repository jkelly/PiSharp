using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.ExtensionHost.Supervision;

namespace PiSharp.Compatibility.Node;

/// <summary>Explicit private qualification-source authority. This cannot select canonical sources
/// or arbitrary plugins. The caller keeps this held lease alive until its Node owner is closed.</summary>
public sealed class OriginalUiQualificationSourceLease : IDisposable
{
    public const int ExpectedBytes = 2960;
    public const string ExpectedSha256 = "8e27790e1b5aa480c0e44595e98026b3d130fcb8a789b3b3aed2b5d335d336c8";
    private static readonly string[] Commands = ["qualification-input", "qualification-editor", "qualification-timeout",
        "qualification-preabort", "qualification-child", "qualification-abort", "qualification-parent", "qualification-session"];
    private readonly object gate = new();
    private readonly FileStream held;
    private readonly string root, path, id = "ui-qualification-" + Guid.NewGuid().ToString("N");
    private bool bound, disposed;
    private string? owner; private long generation;
    private OriginalUiQualificationSourceLease(string root, string path, FileStream held)
    { this.root = root; this.path = path; this.held = held; }
    public static OriginalUiQualificationSourceLease AcquireHeld(string sourceRoot, string sourcePath)
    {
        if (!Path.IsPathFullyQualified(sourceRoot) || !Path.IsPathFullyQualified(sourcePath)) throw new IOException("Explicit qualification paths required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)); var path = Path.GetFullPath(sourcePath);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path) != "private-original-ui-qualification-plugin.mjs") throw new IOException("Qualification source containment differs.");
        CheckPath(root, path);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try { var lease = new OriginalUiQualificationSourceLease(root, path, held); lease.VerifyImmutable(); return lease; }
        catch { held.Dispose(); throw; }
    }
    private static void CheckPath(string root, string path)
    {
        var current = path;
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Qualification source links refused.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new IOException("Qualification source parent missing.");
        }
    }
    internal Dictionary<string, object?> Bind(string ownerId, long ownerGeneration)
    {
        lock (gate)
        {
            VerifyImmutable();
            if (bound || string.IsNullOrEmpty(ownerId) || ownerGeneration <= 0) throw new InvalidOperationException("Qualification source lease is affine.");
            bound = true; owner = ownerId; generation = ownerGeneration;
            return new() { ["leaseId"] = id, ["ownerId"] = owner, ["ownerGeneration"] = generation,
                ["sourceRoot"] = root, ["sourcePath"] = path, ["bytes"] = ExpectedBytes,
                ["sha256"] = ExpectedSha256, ["sourceCommit"] = NodeTierAAdmission.SourceCommit };
        }
    }
    public void VerifyImmutable()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this); CheckPath(root, path);
            held.Position = 0;
            if (held.Length != ExpectedBytes || Convert.ToHexString(SHA256.HashData(held)).ToLowerInvariant() != ExpectedSha256)
                throw new IOException("Exact private qualification source pin differs.");
        }
    }
    internal void ValidateReceipt(JsonElement source)
    {
        lock (gate)
        {
            VerifyImmutable();
            if (!bound || source.GetProperty("admissionProfile").GetString() != "private-original-ui-qualification" ||
                source.GetProperty("sourceCommit").GetString() != NodeTierAAdmission.SourceCommit ||
                !source.GetProperty("factoryAwaited").GetBoolean() || !source.GetProperty("sourceFunctionsRemainInNode").GetBoolean() ||
                source.GetProperty("sourceFactoryCount").GetInt32() != 1 || source.GetProperty("successfulSourceFactoryInvocations").GetInt32() != 1 ||
                source.GetProperty("sourceReference").GetProperty("expectedSha256").GetString() != NodeCommandInputWorkerLaunch.SourceReferenceExpectedSha256)
                throw new IOException("Private qualification provenance differs.");
            var receipt = source.GetProperty("qualificationLease");
            if (receipt.GetProperty("leaseId").GetString() != id || receipt.GetProperty("ownerId").GetString() != owner ||
                receipt.GetProperty("ownerGeneration").GetInt64() != generation || receipt.GetProperty("sourceRoot").GetString() != root ||
                receipt.GetProperty("sourcePath").GetString() != path || receipt.GetProperty("bytes").GetInt32() != ExpectedBytes ||
                receipt.GetProperty("sha256").GetString() != ExpectedSha256 || receipt.GetProperty("sourceCommit").GetString() != NodeTierAAdmission.SourceCommit)
                throw new IOException("Private qualification lease receipt differs.");
            var pins = source.GetProperty("sourcePins");
            if (pins.GetArrayLength() != 1 || pins[0].GetProperty("path").GetString() != path ||
                pins[0].GetProperty("bytes").GetInt32() != ExpectedBytes || pins[0].GetProperty("sha256").GetString() != ExpectedSha256)
                throw new IOException("Private qualification source receipt differs.");
            foreach (var name in new[] { "inputHandlers", "beforeAgentStartHandlers", "sessionHandlers", "tools" })
                if (source.GetProperty(name).GetArrayLength() != 0) throw new IOException("Unexpected private qualification registration.");
            var commands = source.GetProperty("commands"); var names = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<string>(StringComparer.Ordinal);
            if (commands.GetArrayLength() != Commands.Length) throw new IOException("Private qualification command count differs.");
            foreach (var row in commands.EnumerateArray())
            {
                var name = row.GetProperty("name").GetString(); var callback = row.GetProperty("callbackId").GetString();
                if (name is null || !Commands.Contains(name, StringComparer.Ordinal) || !names.Add(name) || string.IsNullOrEmpty(callback) ||
                    callback.Length > 128 || !ids.Add(callback) || row.GetProperty("sourcePath").GetString() != path || row.GetProperty("hasCompletion").GetBoolean())
                    throw new IOException("Private qualification command identity differs.");
            }
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; held.Dispose(); } }
}
