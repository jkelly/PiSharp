// PiSharp-only (decision 0010): the device's content-addressed blob store for bodies too large for a session document (fetched pages,
// feeds, exported evidence). Files are immutable; the catalog holds their metadata and the references that keep them alive.
using System.Globalization;

namespace PiSharp.Durable.Hosting;

/// <summary>One stored blob.</summary>
/// <param name="Hash"><c>sha256:</c> and the lowercase hex SHA-256 of the bytes.</param>
public sealed record BlobInfo(string Hash, long Size, string ContentType, long CreatedAt);

/// <summary>What a blob collection removed, or would remove in a dry run.</summary>
public sealed record BlobCollection(int Blobs, long Bytes, int OrphanFiles, int TemporaryFiles, bool DryRun);

/// <summary>
/// Content-addressed blobs under <c>&lt;data&gt;/blobs/sha256/xx/&lt;hex&gt;</c>. A write goes to a temporary file that is renamed into
/// place, then the catalog row and its owner's reference are added in one transaction; a crash between them leaves an orphan file that
/// collection removes after its grace period. A referenced blob is never collected.
/// </summary>
public sealed class BlobStore
{
    /// <summary>The largest blob, 16 MiB.</summary>
    public const long MaxBlobBytes = 16L * 1024 * 1024;

    private readonly SessionCatalog catalog;
    private readonly TimeProvider time;

    internal BlobStore(string root, SessionCatalog catalog, TimeProvider time)
    {
        Root = root;
        this.catalog = catalog;
        this.time = time;
    }

    /// <summary>The store's directory.</summary>
    public string Root { get; }

    /// <summary>Stores <paramref name="bytes"/> (once per content) and references it for <paramref name="owner"/>.</summary>
    /// <exception cref="HostTooLargeException">The bytes exceed <see cref="MaxBlobBytes"/>.</exception>
    public BlobInfo Put(ReadOnlySpan<byte> bytes, string contentType, string owner)
    {
        if (bytes.Length > MaxBlobBytes) throw new HostTooLargeException($"A blob may have at most {MaxBlobBytes} bytes.");
        CheckOwner(owner);
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 200) throw new HostInvalidException("A blob needs a content type.");
        var hash = CanonicalJson.HashBytes(bytes);
        var path = PathOf(hash);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = Path.Join(Root, "tmp", Guid.NewGuid().ToString("N") + ".tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
        }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var size = (long)bytes.Length;
        return catalog.Transact(transaction =>
        {
            catalog.Run(transaction, "INSERT OR IGNORE INTO blobs (hash, size, content_type, created_at) VALUES ($hash, $size, $type, $at);",
                ("$hash", hash), ("$size", size), ("$type", contentType), ("$at", now));
            catalog.Run(transaction, "INSERT OR IGNORE INTO blob_refs (hash, owner, created_at) VALUES ($hash, $owner, $at);",
                ("$hash", hash), ("$owner", owner), ("$at", now));
            return Info(transaction, hash)!;
        });
    }

    /// <summary>The blob's metadata; null when the catalog does not know it.</summary>
    public BlobInfo? Get(string hash) => catalog.Read(_ => Info(null, hash));

    /// <summary>The blob's bytes, checked against its hash; null when it is missing or damaged.</summary>
    public byte[]? Read(string hash)
    {
        if (!IsHash(hash) || Get(hash) is null) return null;
        var path = PathOf(hash);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        return CanonicalJson.HashBytes(bytes) == hash ? bytes : null;
    }

    /// <summary>References a stored blob for another owner.</summary>
    public void AddReference(string hash, string owner)
    {
        CheckOwner(owner);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        catalog.Transact(transaction =>
        {
            if (Info(transaction, hash) is null) throw new HostNotFoundException($"No blob {hash}.");
            catalog.Run(transaction, "INSERT OR IGNORE INTO blob_refs (hash, owner, created_at) VALUES ($hash, $owner, $at);",
                ("$hash", hash), ("$owner", owner), ("$at", now));
            return 0;
        });
    }

    /// <summary>Drops every reference <paramref name="owner"/> holds; the blobs stay until collected.</summary>
    public int RemoveReferences(string owner) => catalog.Transact(transaction =>
    {
        var count = Convert.ToInt32(catalog.Value(transaction, "SELECT COUNT(*) FROM blob_refs WHERE owner = $owner;", ("$owner", owner)), CultureInfo.InvariantCulture);
        catalog.Run(transaction, "DELETE FROM blob_refs WHERE owner = $owner;", ("$owner", owner));
        return count;
    });

    /// <summary>How many references the blob has.</summary>
    public int References(string hash) => catalog.Read(_ =>
        Convert.ToInt32(catalog.Value(null, "SELECT COUNT(*) FROM blob_refs WHERE hash = $hash;", ("$hash", hash)), CultureInfo.InvariantCulture));

    /// <summary>
    /// Removes unreferenced blobs older than <paramref name="grace"/>, files the catalog does not know that are older than it, and stale
    /// temporary files. A dry run only counts them.
    /// </summary>
    public BlobCollection Collect(TimeSpan grace, bool dryRun = false)
    {
        var cutoff = time.GetUtcNow() - grace;
        var cutoffMs = cutoff.ToUnixTimeMilliseconds();
        var candidates = catalog.Read(_ => catalog.Rows(null,
            "SELECT b.hash, b.size FROM blobs b WHERE b.created_at < $cutoff AND NOT EXISTS (SELECT 1 FROM blob_refs r WHERE r.hash = b.hash);",
            reader => (Hash: reader.GetString(0), Size: reader.GetInt64(1)), ("$cutoff", cutoffMs)));
        var blobs = 0;
        long bytes = 0;
        foreach (var (hash, size) in candidates)
        {
            if (!dryRun)
            {
                // Recheck in the deleting transaction: a reference added since the scan keeps the blob.
                var removed = catalog.Transact(transaction =>
                {
                    if (Convert.ToInt64(catalog.Value(transaction, "SELECT COUNT(*) FROM blob_refs WHERE hash = $hash;", ("$hash", hash)), CultureInfo.InvariantCulture) > 0)
                        return false;
                    catalog.Run(transaction, "DELETE FROM blobs WHERE hash = $hash;", ("$hash", hash));
                    return true;
                });
                if (!removed) continue;
                var path = PathOf(hash);
                if (File.Exists(path)) File.Delete(path);
            }
            blobs++;
            bytes += size;
        }
        var orphans = 0;
        var directory = Path.Join(Root, "sha256");
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var hash = "sha256:" + Path.GetFileName(file);
                if (!IsHash(hash) || File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime || Get(hash) is not null) continue;
                if (!dryRun) File.Delete(file);
                orphans++;
            }
        }
        var temporary = 0;
        var tmp = Path.Join(Root, "tmp");
        if (Directory.Exists(tmp))
        {
            foreach (var file in Directory.EnumerateFiles(tmp))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff.UtcDateTime) continue;
                if (!dryRun) File.Delete(file);
                temporary++;
            }
        }
        return new BlobCollection(blobs, bytes, orphans, temporary, dryRun);
    }

    /// <summary>Whether <paramref name="hash"/> is a <c>sha256:</c> hash.</summary>
    public static bool IsHash(string? hash) => hash is { Length: 71 } && hash.StartsWith("sha256:", StringComparison.Ordinal) &&
        hash.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal string PathOf(string hash)
    {
        if (!IsHash(hash)) throw new HostInvalidException($"Not a blob hash: {hash}");
        var hex = hash[7..];
        return Path.Join(Root, "sha256", hex[..2], hex);
    }

    private BlobInfo? Info(Microsoft.Data.Sqlite.SqliteTransaction? transaction, string hash) =>
        catalog.Rows(transaction, "SELECT hash, size, content_type, created_at FROM blobs WHERE hash = $hash;",
            reader => new BlobInfo(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3)), ("$hash", hash)).FirstOrDefault();

    private static void CheckOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 300) throw new HostInvalidException("A blob reference needs an owner.");
    }
}
