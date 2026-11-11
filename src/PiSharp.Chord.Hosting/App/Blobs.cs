// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/blobs.ts.
namespace PiSharp.Chord.Hosting.App;

internal sealed class Blobs
{
    internal string DirectoryPath { get; }
    internal Blobs(string directory) => DirectoryPath = Path.GetFullPath(directory);
    internal string PathOf(string id)
    {
        if (id.Length != 36 || id.Any(c => !char.IsAsciiHexDigitLower(c))) throw FilesValues.Invalid("Invalid blob id");
        return Path.Combine(DirectoryPath, id);
    }
    internal void Prepare()
    {
        Directory.CreateDirectory(DirectoryPath);
        RejectLink(DirectoryPath);
    }
    internal async Task WriteAsync(string id, byte[] bytes, string staging)
    {
        Prepare();
        await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(true);
        }
        File.Move(staging, PathOf(id));
    }
    internal async Task<byte[]> ReadAsync(string id, long size, long offset, int length)
    {
        if (offset < 0 || length < 0) throw FilesValues.Invalid("Invalid read range");
        if (length > FilesValues.MaxPiece) throw FilesValues.TooLarge("A read takes at most 4 MiB");
        if (offset >= size || length == 0) return [];
        var path = PathOf(id);
        RejectLink(path);
        // Delete sharing retains an opened version even if its last metadata reference is concurrently removed.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var bytes = new byte[(int)Math.Min(length, size - offset)];
        stream.Position = offset;
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        return bytes;
    }
    internal void Remove(IEnumerable<string> ids)
    {
        foreach (var id in ids) File.Delete(PathOf(id));
    }
    internal void Sweep(IReadOnlyDictionary<string, BlobUse> retained)
    {
        Prepare();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath))
        {
            var id = Path.GetFileName(path);
            if (!retained.ContainsKey(id)) File.Delete(path);
        }
    }
    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Files directory and contents may not be symbolic links: {path}");
    }
    internal static void RemoveOwnedDirectory(string path, string parent)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Refusing to remove an unowned files directory");
        if (!Directory.Exists(full)) return;
        RejectLink(full);
        foreach (var entry in Directory.EnumerateFileSystemEntries(full)) RejectLink(entry);
        Directory.Delete(full, recursive: true);
    }
}
