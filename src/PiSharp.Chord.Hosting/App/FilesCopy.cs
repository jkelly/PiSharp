// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/files-copy.ts.
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Hosting.App;

/// <summary>Copies only the files adapter's metadata and referenced immutable blobs into an absent target.</summary>
public static class CopyFiles
{
    public static async Task CopyAsync(ChordStore source, string sourceDirectory, ChordStore target, string targetDirectory,
        ChordContext context, string? sourceHostKey = null, string? targetHostKey = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(target);
        var destination = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(destination) || File.Exists(destination)) throw FilesValues.Invalid("Target files directory already exists");
        var parent = Path.GetDirectoryName(destination) ?? throw FilesValues.Invalid("Target files directory has no parent");
        Directory.CreateDirectory(parent); Blobs.RejectLink(parent);
        var staging = Path.Combine(parent, ".files-copy-" + FilesValues.Token());
        var moved = false;
        var admitted = false;
        var targetIndex = new FilesIndex(target, targetHostKey);
        try
        {
            FilesSnapshot snapshot;
            Directory.CreateDirectory(staging);
            var staged = new Blobs(Path.Combine(staging, "blobs")); staged.Prepare();
            var original = new Blobs(Path.Combine(Path.GetFullPath(sourceDirectory), "blobs"));
            // Reading the first export part owns the commit queue. Keep that ownership until every referenced
            // blob was copied; a remove cannot retire the snapshot's references before their files are opened.
            await using (var held = source.Export(context))
            {
                if (!await held.MoveNextAsync().ConfigureAwait(false)) throw new IOException("Source export has no manifest");
                (_, snapshot) = await FilesIndex.ReadAsync(source, FilesIndex.Address(sourceHostKey), context).ConfigureAwait(false);
                foreach (var (id, blob) in snapshot.Blobs)
                {
                    context.AbortSignal?.ThrowIfCancellationRequested();
                    var sourcePath = original.PathOf(id); Blobs.RejectLink(sourcePath);
                    // A retained descriptor also covers cancellation releasing the export's queue ownership.
                    await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != blob.Size) throw new IOException("Source blob length does not match its index");
                    await using var output = new FileStream(staged.PathOf(id), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await input.CopyToAsync(output, context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false);
                    await output.FlushAsync(CancellationToken.None).ConfigureAwait(false); output.Flush(true);
                }
            }
            context.AbortSignal?.ThrowIfCancellationRequested();
            Directory.Move(staging, destination); moved = true;
            // The target was acquired by this operation and stays so through the atomic metadata admission.
            await targetIndex.ChangeAsync(s =>
            {
                s.Spaces = snapshot.Spaces; s.Blobs = snapshot.Blobs; s.Bytes = snapshot.Bytes; return true;
            }, context, requireAbsent: true, beforeWrites: () => admitted = true).ConfigureAwait(false);
        }
        catch
        {
            if (!admitted)
            {
                if (moved) Blobs.RemoveOwnedDirectory(destination, parent);
                else Blobs.RemoveOwnedDirectory(staging, parent);
            }
            throw;
        }
        finally { targetIndex.Close(); }
    }
}
