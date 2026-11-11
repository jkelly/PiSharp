// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/sync/files.ts.
using System.Text;

namespace PiSharp.Chord.Sync;

/// <summary>An operation's value or failure. Missing paths are classified independently of the filesystem implementation.</summary>
public sealed class FileResult<T>
{
    private FileResult(T? value, Exception? exception) { Value = value; Exception = exception; }
    public bool Ok => Exception is null;
    public T? Value { get; }
    public Exception? Exception { get; }
    public bool IsNotFound => Exception is FileNotFoundException or DirectoryNotFoundException
        || Exception is SyncFileException { Code: "not_found" };
    public static FileResult<T> Success(T value) => new(value, null);
    public static FileResult<T> Failure(Exception exception) => new(default, exception ?? throw new ArgumentNullException(nameof(exception)));
    public T Unwrap()
    {
        if (Exception is { } error) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return Value!;
    }
}

public sealed class SyncFileException(string code, string message, Exception? innerException = null) : IOException(message, innerException)
{
    public string Code { get; } = code;
}
public sealed record SyncFileInfo(string Name, string Kind);
public sealed record SyncFileReaderOptions(bool NoFollow = false);
public sealed record SyncCreateDirectoryOptions(bool Recursive = false);
public sealed record SyncRemoveOptions(bool Recursive = false, bool Force = false);

public interface ISyncFileReader
{
    /// <summary>Reads up to length bytes at offset, returning fewer only at EOF.</summary>
    Task<FileResult<ReadOnlyMemory<byte>>> ReadAsync(long offset, int length, ChordContext context);
    Task CloseAsync(ChordContext context);
}

/// <summary>Portable directory operations. Successful write-like operations return true; missing paths fail with not_found.</summary>
public interface ISyncFileSystem
{
    Task<FileResult<string>> JoinPathAsync(IReadOnlyList<string> parts, ChordContext context);
    Task<FileResult<string>> ReadTextFileAsync(string path, ChordContext context);
    Task<FileResult<ReadOnlyMemory<byte>>> ReadBinaryFileAsync(string path, ChordContext context);
    Task<FileResult<ISyncFileReader>> OpenBinaryReaderAsync(string path, SyncFileReaderOptions? options, ChordContext context);
    Task<FileResult<bool>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, ChordContext context);
    Task<FileResult<bool>> WriteFileAsync(string path, string content, ChordContext context)
        => WriteFileAsync(path, Encoding.UTF8.GetBytes(content), context);
    Task<FileResult<bool>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, ChordContext context);
    Task<FileResult<bool>> AppendFileAsync(string path, string content, ChordContext context)
        => AppendFileAsync(path, Encoding.UTF8.GetBytes(content), context);
    Task<FileResult<bool>> RenameFileAsync(string sourcePath, string destinationPath, ChordContext context);
    Task<FileResult<IReadOnlyList<SyncFileInfo>>> ListDirAsync(string path, ChordContext context);
    Task<FileResult<bool>> CreateDirAsync(string path, SyncCreateDirectoryOptions? options, ChordContext context);
    Task<FileResult<bool>> RemoveAsync(string path, SyncRemoveOptions? options, ChordContext context);
}

/// <summary>The native filesystem adapter. Rename replaces an existing file within its directory.</summary>
public sealed class LocalSyncFileSystem : ISyncFileSystem
{
    private static CancellationToken Token(ChordContext context) => context.AbortSignal ?? CancellationToken.None;
    private static async Task<FileResult<T>> Run<T>(ChordContext context, Func<CancellationToken, Task<T>> action)
    {
        try { var token = Token(context); token.ThrowIfCancellationRequested(); return FileResult<T>.Success(await action(token).ConfigureAwait(false)); }
        catch (Exception error) { return FileResult<T>.Failure(error); }
    }
    public Task<FileResult<string>> JoinPathAsync(IReadOnlyList<string> parts, ChordContext context)
        => Run(context, _ => Task.FromResult(Path.Combine(parts.ToArray())));
    public Task<FileResult<string>> ReadTextFileAsync(string path, ChordContext context)
        => Run(context, token => File.ReadAllTextAsync(path, token));
    public Task<FileResult<ReadOnlyMemory<byte>>> ReadBinaryFileAsync(string path, ChordContext context)
        => Run<ReadOnlyMemory<byte>>(context, async token => await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
    public Task<FileResult<ISyncFileReader>> OpenBinaryReaderAsync(string path, SyncFileReaderOptions? options, ChordContext context)
        => Run<ISyncFileReader>(context, _ =>
        {
            if (options?.NoFollow == true && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to follow a symbolic link: " + path);
            return Task.FromResult<ISyncFileReader>(new Reader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess)));
        });
    public Task<FileResult<bool>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, ChordContext context)
        => Run(context, async token =>
        {
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(content, token).ConfigureAwait(false);
            return true;
        });
    public Task<FileResult<bool>> WriteFileAsync(string path, string content, ChordContext context)
        => WriteFileAsync(path, Encoding.UTF8.GetBytes(content), context);
    public Task<FileResult<bool>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, ChordContext context)
        => Run(context, async token =>
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(content, token).ConfigureAwait(false);
            return true;
        });
    public Task<FileResult<bool>> AppendFileAsync(string path, string content, ChordContext context)
        => AppendFileAsync(path, Encoding.UTF8.GetBytes(content), context);
    public Task<FileResult<bool>> RenameFileAsync(string sourcePath, string destinationPath, ChordContext context)
        => Run(context, _ => { File.Move(sourcePath, destinationPath, overwrite: true); return Task.FromResult(true); });
    public Task<FileResult<IReadOnlyList<SyncFileInfo>>> ListDirAsync(string path, ChordContext context)
        => Run<IReadOnlyList<SyncFileInfo>>(context, token =>
        {
            var entries = new List<SyncFileInfo>();
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                entries.Add(new(entry.Name, (entry.Attributes & FileAttributes.ReparsePoint) != 0 ? "symlink"
                    : (entry.Attributes & FileAttributes.Directory) != 0 ? "directory" : "file"));
            }
            return Task.FromResult<IReadOnlyList<SyncFileInfo>>(entries);
        });
    public Task<FileResult<bool>> CreateDirAsync(string path, SyncCreateDirectoryOptions? options, ChordContext context)
        => Run(context, _ =>
        {
            if (options?.Recursive != true && Path.GetDirectoryName(Path.GetFullPath(path)) is { } parent && !Directory.Exists(parent))
                throw new DirectoryNotFoundException(parent);
            Directory.CreateDirectory(path);
            return Task.FromResult(true);
        });
    public Task<FileResult<bool>> RemoveAsync(string path, SyncRemoveOptions? options, ChordContext context)
        => Run(context, _ =>
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception error) when (options?.Force == true && (error is FileNotFoundException or DirectoryNotFoundException))
            { return Task.FromResult(true); }
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, options?.Recursive == true);
            else File.Delete(path);
            return Task.FromResult(true);
        });

    private sealed class Reader(FileStream stream) : ISyncFileReader
    {
        public Task<FileResult<ReadOnlyMemory<byte>>> ReadAsync(long offset, int length, ChordContext context)
            => Run<ReadOnlyMemory<byte>>(context, async token =>
            {
                ArgumentOutOfRangeException.ThrowIfNegative(offset);
                ArgumentOutOfRangeException.ThrowIfNegative(length);
                var bytes = new byte[length];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await RandomAccess.ReadAsync(stream.SafeFileHandle, bytes.AsMemory(count), offset + count, token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                return bytes.AsMemory(0, count);
            });
        // Closing is mandatory cleanup and must succeed even with a cancelled context.
        public Task CloseAsync(ChordContext context) => stream.DisposeAsync().AsTask();
    }
}

internal static class SyncFiles
{
    internal const int ReadChunk = 256 * 1024;
    internal static async Task WriteAtomicAsync(ISyncFileSystem fs, string directory, string name,
        IAsyncEnumerable<ReadOnlyMemory<byte>> body, ChordContext context)
    {
        (await fs.CreateDirAsync(directory, new(Recursive: true), context).ConfigureAwait(false)).Unwrap();
        var path = (await fs.JoinPathAsync([directory, name], context).ConfigureAwait(false)).Unwrap();
        var temporary = (await fs.JoinPathAsync([directory, name + "." + Guid.NewGuid().ToString("N") + ".tmp"], context).ConfigureAwait(false)).Unwrap();
        try
        {
            (await fs.WriteFileAsync(temporary, ReadOnlyMemory<byte>.Empty, context).ConfigureAwait(false)).Unwrap();
            await foreach (var chunk in body.WithCancellation(context.AbortSignal ?? CancellationToken.None).ConfigureAwait(false))
            {
                context.AbortSignal?.ThrowIfCancellationRequested();
                (await fs.AppendFileAsync(temporary, chunk, context).ConfigureAwait(false)).Unwrap();
            }
            context.AbortSignal?.ThrowIfCancellationRequested();
            (await fs.RenameFileAsync(temporary, path, context).ConfigureAwait(false)).Unwrap();
        }
        catch
        {
            // Never allow cancelled cleanup to leave a partially written temporary file behind or mask the original error.
            try { await fs.RemoveAsync(temporary, new(Force: true), ChordContext.WithoutAbortSignal(context)).ConfigureAwait(false); }
            catch { }
            throw;
        }
    }
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> Once(ReadOnlyMemory<byte> bytes)
    { yield return bytes; await Task.CompletedTask; }
}
