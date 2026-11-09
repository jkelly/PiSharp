// PiSharp-only: manifest-verified native daemon artifacts and atomic local installation.
using System.Security.Cryptography;
using System.Text.Json;

namespace PiSharp.Env;

public sealed record DaemonArtifact(string Platform, string Arch, string Version, string Path, string Sha256)
{
    /// <summary>Android requires the explicitly packaged upstream Rust backend and provenance fields.</summary>
    public string Backend { get; init; } = "native-dotnet";
    public string? SourceCommit { get; init; }
    public string? License { get; init; }
    public string? TestStatus { get; init; }
}

/// <summary>A caller-trusted release manifest. Hashes must come from the release, never from an untrusted download.</summary>
public sealed class DaemonCatalog
{
    private readonly string root;
    private readonly IReadOnlyList<DaemonArtifact> artifacts;
    public DaemonCatalog(string root, IEnumerable<DaemonArtifact> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(artifacts);
        this.root = System.IO.Path.GetFullPath(root);
        this.artifacts = artifacts.ToArray();
        foreach (var artifact in this.artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ValidatePlatform(artifact.Platform, artifact.Arch, artifact.Backend == "upstream-rust");
            if (artifact.Platform == "android" && (artifact.SourceCommit is not { Length: 40 } || !artifact.SourceCommit.All(Uri.IsHexDigit) || artifact.License != "MIT" || string.IsNullOrWhiteSpace(artifact.TestStatus)))
                throw new ArgumentException("Android upstream artifacts require source commit, MIT license and explicit test status provenance.");
            if (string.IsNullOrWhiteSpace(artifact.Version) || artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Invalid daemon version or SHA-256.");
            ResolvePath(artifact);
        }
        if (this.artifacts.GroupBy(a => (a.Platform, a.Arch)).Any(g => g.Count() > 1))
            throw new ArgumentException("Duplicate daemon platform in catalog.");
    }

    public static async Task<DaemonCatalog> LoadAsync(string manifest, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(manifest);
        var entries = await JsonSerializer.DeserializeAsync<DaemonArtifact[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
            ?? throw new InvalidDataException("Empty daemon catalog.");
        return new(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifest))!, entries);
    }

    public DaemonArtifact Select(string platform, string arch)
    {
        ValidatePlatform(platform, arch, artifacts.Any(a => a.Platform == platform && a.Arch == arch && a.Backend == "upstream-rust"));
        return artifacts.SingleOrDefault(a => a.Platform == platform && a.Arch == arch)
            ?? throw new FileNotFoundException($"No verified native daemon artifact for {platform}-{arch}; publish it and supply a trusted release catalog.");
    }

    public async Task<byte[]> ReadVerifiedAsync(DaemonArtifact artifact, CancellationToken cancellationToken = default)
    {
        if (!artifacts.Contains(artifact)) throw new ArgumentException("Artifact is not in this catalog.", nameof(artifact));
        var bytes = await File.ReadAllBytesAsync(ResolvePath(artifact), cancellationToken);
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Daemon artifact SHA-256 does not match the trusted release catalog.");
        return bytes;
    }

    private string ResolvePath(DaemonArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Path);
        if (System.IO.Path.IsPathRooted(artifact.Path)) throw new ArgumentException("Catalog artifact paths must be relative.");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, artifact.Path));
        var relative = System.IO.Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Catalog artifact path escapes its root.");
        return path;
    }

    internal static void ValidatePlatform(string platform, string arch, bool upstreamAndroid = false)
    {
        if (platform == "android" && !upstreamAndroid) throw new PlatformNotSupportedException("Android requires a separately verified upstream daemon backend; this native .NET catalog does not ship one.");
        if (platform is not ("windows" or "linux" or "darwin") && !(platform == "android" && upstreamAndroid) || arch is not ("x64" or "arm64"))
            throw new PlatformNotSupportedException($"Unsupported daemon platform {platform}-{arch}.");
    }
}

public static class Bootstrap
{
    /// <summary>Installs verified bytes in a content-addressed cache. Concurrent installs never expose partial bytes.</summary>
    public static async Task<string> InstallAsync(DaemonCatalog catalog, DaemonArtifact artifact, string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        var bytes = await catalog.ReadVerifiedAsync(artifact, cancellationToken);
        var directory = System.IO.Path.GetFullPath(cacheDirectory);
        Directory.CreateDirectory(directory);
        var destination = System.IO.Path.Combine(directory, "pi-env-" + artifact.Sha256.ToLowerInvariant() + (artifact.Platform == "windows" ? ".exe" : ""));
        // Serialize across processes as well as async callers. Windows cannot rename over an open hash reader.
        await using var cacheLock = await LockAsync(destination + ".lock", cancellationToken);
        if (File.Exists(destination) && Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(destination, cancellationToken))).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            return destination;
        var temporary = System.IO.Path.Combine(directory, ".pi-env-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(25, cancellationToken); }
        }
    }
}
