// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/env/index.ts (the portable FileSystem capability, its binary and
// directory readers). Upstream's `string | Uint8Array` contents are bytes here, with UTF-8 string overloads in FileSystemExtensions.
// Members still to come with P9: openTextLineReader/readTextLines, BinaryReader.scanLines, watch, createTempDir/createTempFile, cleanup.
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Env;

/// <summary>Options of <see cref="IFileSystem.OpenBinaryReaderAsync"/>.</summary>
public sealed record BinaryReaderOptions
{
    /// <summary>A symbolic link as the final path component fails with <see cref="FileErrorCode.Invalid"/> instead of being followed;
    /// earlier components are still resolved.</summary>
    public bool NoFollow { get; init; }
}

/// <summary>Options of <see cref="IFileSystem.CreateDirAsync"/>.</summary>
public sealed record CreateDirOptions
{
    /// <summary>Create missing parents, and succeed when the directory exists. Defaults to true.</summary>
    public bool Recursive { get; init; } = true;
}

/// <summary>Options of <see cref="IFileSystem.RemoveAsync"/>.</summary>
public sealed record RemoveOptions
{
    /// <summary>Remove a directory and everything below it.</summary>
    public bool Recursive { get; init; }
    /// <summary>Succeed when the path does not exist.</summary>
    public bool Force { get; init; }
}

/// <summary>Positional reads from one opened regular file; all calls see the same file even if its path is renamed (upstream
/// <c>BinaryReader</c>).</summary>
public interface IBinaryReader
{
    /// <summary>Metadata of the opened file, not of whatever its path names now.</summary>
    Task<Result<FileMetadata, FileError>> InfoAsync(DurableContext context);

    /// <summary>Up to <paramref name="length"/> bytes at <paramref name="offset"/>; fewer only at end of file.</summary>
    Task<Result<byte[], FileError>> ReadAsync(long offset, long length, DurableContext context);

    /// <summary>Releases the file; idempotent. Later reads fail with <see cref="FileErrorCode.Invalid"/>.</summary>
    Task CloseAsync(DurableContext context);
}

/// <summary>Pages of one directory's entries (upstream <c>DirReader</c>).</summary>
/// <param name="Entries">Entries of this page.</param>
/// <param name="Done">Whether the end was reached.</param>
public sealed record DirPage(ImmutableArray<FileMetadata> Entries, bool Done);

/// <summary>Pages of one directory's entries (upstream <c>DirReader</c>).</summary>
public interface IDirReader
{
    /// <summary>Up to <paramref name="maxEntries"/> entries in the order the file system returns them, continuing where the previous call
    /// stopped. <see cref="DirPage.Done"/> marks the end; it may come with the last entries or with an empty page. An entry that disappears
    /// before its metadata is read is skipped, as are entries of unsupported kinds. After a failed or aborted call, close the reader.</summary>
    Task<Result<DirPage, FileError>> NextAsync(int maxEntries, DurableContext context);

    /// <summary>Releases the directory; idempotent.</summary>
    Task CloseAsync(DurableContext context);
}

/// <summary>Portable file system capability (upstream <c>FileSystem</c>). Operations return failures rather than throwing; a cancelled
/// context fails them with <see cref="FileErrorCode.Aborted"/>. Relative paths resolve against <see cref="Cwd"/>.</summary>
public interface IFileSystem
{
    /// <summary>The file namespace: equal IDs see the same files at the same paths, whatever their <see cref="Cwd"/>.</summary>
    string Id { get; }

    /// <summary>The directory relative paths resolve against.</summary>
    string Cwd { get; set; }

    /// <summary>The absolute, normalized form of <paramref name="path"/>.</summary>
    Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context);

    /// <summary>The parts joined with the platform separator and normalized.</summary>
    Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context);

    /// <summary>The whole file decoded as UTF-8.</summary>
    Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context);

    /// <summary>The whole file.</summary>
    Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context);

    /// <summary>Opens a regular file for bounded positional reads. A directory fails with <see cref="FileErrorCode.IsDirectory"/>, other
    /// non-regular files with <see cref="FileErrorCode.Invalid"/>.</summary>
    Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context);

    /// <summary>Creates or replaces the file, creating missing parent directories.</summary>
    Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context);

    /// <summary>Appends to the file, creating it and missing parent directories.</summary>
    Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context);

    /// <summary>Truncates or extends a file to exactly <paramref name="size"/> bytes; never creates it.</summary>
    Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context);

    /// <summary>Flushes file contents and the metadata needed to retrieve them to the storage device.</summary>
    Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context);

    /// <summary>Renames a file, atomically replacing an existing destination.</summary>
    Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context);

    /// <summary>Metadata of a path, not following a final symbolic link.</summary>
    Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context);

    /// <summary>Metadata of every entry of a directory.</summary>
    Task<Result<ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context);

    /// <summary>Opens a directory for paged listing.</summary>
    Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context);

    /// <summary>The absolute path with every symbolic link resolved.</summary>
    Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context);

    /// <summary>Whether the path exists (a dangling symbolic link exists).</summary>
    Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context);

    /// <summary>Creates a directory.</summary>
    Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context);

    /// <summary>Removes a file, link or directory.</summary>
    Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context);
}

/// <summary>Upstream's string contents: text is written as UTF-8.</summary>
public static class FileSystemExtensions
{
    /// <summary>Creates or replaces the file with UTF-8 text.</summary>
    public static Task<Result<Unit, FileError>> WriteFileAsync(this IFileSystem fileSystem, string path, string content, DurableContext context) =>
        fileSystem.WriteFileAsync(path, Encoding.UTF8.GetBytes(content), context);

    /// <summary>Appends UTF-8 text.</summary>
    public static Task<Result<Unit, FileError>> AppendFileAsync(this IFileSystem fileSystem, string path, string content, DurableContext context) =>
        fileSystem.AppendFileAsync(path, Encoding.UTF8.GetBytes(content), context);
}

/// <summary>
/// The environment tools and prompt sections run in (upstream <c>ExecutionEnv</c>, <c>FileSystem &amp; Shell</c>). The Harness builds one per
/// use through <c>HarnessOptions.Env</c> and hands it to task runtimes. TODO(P9): the Shell half joins with the local execution environment;
/// until then an environment is its file system.
/// </summary>
public interface IExecutionEnv : IFileSystem
{
}
