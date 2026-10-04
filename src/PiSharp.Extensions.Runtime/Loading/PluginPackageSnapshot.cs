using System.Security.Cryptography;
using PiSharp.Extensions.Runtime.Discovery;

namespace PiSharp.Extensions.Runtime.Loading;

internal sealed record SnapshotArtifact(string RelativePath, string FullPath, byte[] Bytes);

/// <summary>Owns exact verified bytes and a private resolver directory. It never exposes mutable images.</summary>
internal sealed class PluginPackageSnapshot : IAsyncDisposable
{
    internal string DirectoryPath { get; }
    internal Dictionary<string, SnapshotArtifact> Artifacts { get; } = new(StringComparer.Ordinal);
    private readonly List<FileStream> locks = [];
    private readonly HashSet<string> directories = new(StringComparer.Ordinal);
    private readonly string parent;

    private PluginPackageSnapshot(string parent)
    {
        this.parent = parent;
        DirectoryPath = Path.Combine(parent, "pisharp-package-" + Guid.NewGuid().ToString("N"));
    }

    internal static async Task<PluginPackageSnapshot> CreateAsync(ExtensionManifestAdmission admission,
        string parent, ExtensionManifestReaderOptions limits, CancellationToken cancellationToken)
    {
        var snapshot = new PluginPackageSnapshot(parent);
        var manifest = admission.Manifest!;
        try
        {
            ManifestPathPolicy.Inspect(parent, requireDirectory: true);
            Directory.CreateDirectory(snapshot.DirectoryPath);
            ManifestPathPolicy.Inspect(snapshot.DirectoryPath, requireDirectory: true);
            long total = 0;
            foreach (var artifact in manifest.ArtifactHashes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ManifestPathPolicy.Resolve(admission.CanonicalPackageRoot!, artifact.RelativePath);
                ManifestPathPolicy.Inspect(source, requireDirectory: false);
                byte[] bytes;
                await using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8_192, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var length = stream.Length;
                    if (length > limits.MaximumArtifactBytes || length > limits.MaximumTotalArtifactBytes - total || length > int.MaxValue)
                        throw Failure(PluginLoadFailure.LimitExceeded, manifest.Id, "snapshot-read");
                    bytes = new byte[(int)length];
                    await stream.ReadExactlyAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 || stream.Length != length ||
                        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(artifact.Sha256)))
                        throw Failure(PluginLoadFailure.ArtifactChanged, manifest.Id, "snapshot-read");
                }
                ManifestPathPolicy.Inspect(source, requireDirectory: false);
                total += bytes.LongLength;
                var target = ManifestPathPolicy.Resolve(snapshot.DirectoryPath, artifact.RelativePath);
                var targetDirectory = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(targetDirectory);
                for (var path = targetDirectory; path != snapshot.DirectoryPath; path = Path.GetDirectoryName(path)!)
                    snapshot.directories.Add(path);
                ManifestPathPolicy.Inspect(targetDirectory, requireDirectory: true);
                // CreateNew prevents replacing an unexpected existing target. Locks retain resolver bytes on Windows.
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    8_192, FileOptions.Asynchronous))
                {
                    snapshot.Artifacts.Add(artifact.RelativePath, new(artifact.RelativePath, target, bytes));
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                ManifestPathPolicy.Inspect(target, requireDirectory: false);
                snapshot.locks.Add(new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }
        catch (Exception original)
        {
            try { await snapshot.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new SnapshotCreationFailure(snapshot, original, cleanup); }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var stream in locks)
            try { await stream.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        locks.Clear();
        // Never recursively delete. Unexpected extra children keep the owned directory and produce a truthful failure.
        if (Path.GetDirectoryName(DirectoryPath) != parent || !Path.GetFileName(DirectoryPath).StartsWith("pisharp-package-", StringComparison.Ordinal))
            throw new IOException("Snapshot cleanup target does not match its ownership.");
        if (Directory.Exists(DirectoryPath))
        {
            try { ManifestPathPolicy.Inspect(DirectoryPath, requireDirectory: true); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count == 0)
            {
                foreach (var artifact in Artifacts.Values)
                {
                    try { ManifestPathPolicy.Inspect(artifact.FullPath, requireDirectory: false); File.Delete(artifact.FullPath); }
                    catch (Exception error) { failures.Add(error); }
                    Array.Clear(artifact.Bytes);
                }
                foreach (var directory in directories.OrderByDescending(path => path.Length))
                    try { ManifestPathPolicy.Inspect(directory, requireDirectory: true); Directory.Delete(directory); }
                    catch (Exception error) { failures.Add(error); }
                try { Directory.Delete(DirectoryPath); } catch (Exception error) { failures.Add(error); }
            }
        }
        Artifacts.Clear();
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    internal static PluginLoadException Failure(PluginLoadFailure failure, string owner, string operation, Exception? inner = null) =>
        new(failure, owner, operation, inner);
}

// Carries ownership to the loader so failed rollback retains its owner/budget rather than leaking uncharged directories.
internal sealed class SnapshotCreationFailure(PluginPackageSnapshot snapshot, Exception original, Exception cleanup)
    : Exception("Owned snapshot rollback failed.", new AggregateException(original, cleanup))
{
    internal PluginPackageSnapshot Snapshot { get; } = snapshot;
}
