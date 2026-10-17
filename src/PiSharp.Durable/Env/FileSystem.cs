// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/index.ts (the portable FileSystem capability: its text-line,
// binary and directory readers, line scans, watches, temporary files and the optional listFiles). Upstream's `string | Uint8Array` contents
// are bytes here, with UTF-8 string overloads in FileSystemExtensions; upstream's `{ paths } | { overflow } | { error }` WatchChange union is
// the WatchChange record hierarchy. The optional `listFiles?` member is a default interface method that fails with not_supported, which
// callers already treat as a missing method; ListFilesOptions keeps upstream's `number` bounds as doubles so a fractional or negative bound
// is refused with invalid, as upstream refuses it.
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

    /// <summary>
    /// One pass over the file that locates lines <c>[startLine, endLine)</c> (<paramref name="endLine"/> null: to the end), 0-based, where
    /// line <c>k</c> starts after the <c>k</c>-th newline byte. Decoded sizes are those of the text decoding the whole file as UTF-8 (WHATWG,
    /// as <see cref="StreamDecoder"/>) would produce for that range, so a byte-order mark at the start of the file is not counted. An empty
    /// or negative range fails with <see cref="FileErrorCode.Invalid"/>.
    /// </summary>
    Task<Result<LineScan, FileError>> ScanLinesAsync(long startLine, long? endLine, DurableContext context);

    /// <summary>Releases the file; idempotent. Later reads fail with <see cref="FileErrorCode.Invalid"/>.</summary>
    Task CloseAsync(DurableContext context);
}

/// <summary>Where lines of a file are, as <see cref="IBinaryReader.ScanLinesAsync"/> found them (upstream <c>LineScan</c>).</summary>
/// <param name="Newlines">Newline bytes in the whole file; it has <c>Newlines + 1</c> lines.</param>
/// <param name="Start">Start of the byte range of the selected lines: from the start of the first to the end of the last, without the newline
/// that ends it. A selection past the last line is empty at the end of the file.</param>
/// <param name="End">End of that byte range.</param>
/// <param name="FirstLineEnd">Where the first selected line ends: its newline, or the end of the file.</param>
/// <param name="LastLineStart">Where the last selected line starts.</param>
/// <param name="SelectedBytes">UTF-8 byte length of the decoded selection.</param>
/// <param name="FirstLineBytes">UTF-8 byte length of the decoded first selected line.</param>
public sealed record LineScan(long Newlines, long Start, long End, long FirstLineEnd, long LastLineStart, long SelectedBytes, long FirstLineBytes);

/// <summary>One line of a text file (upstream <c>TextLine</c>).</summary>
/// <param name="Text">The line without its newline; a carriage return before the newline is kept.</param>
/// <param name="Terminated">Whether a newline ended the line (false only for a last line without one).</param>
public sealed record TextLine(string Text, bool Terminated);

/// <summary>Strict LF line reads of one opened text file, decoded as UTF-8 with a leading byte-order mark dropped (upstream
/// <c>TextLineReader</c>).</summary>
public interface ITextLineReader
{
    /// <summary>The next line, or null at the end of the file. A cancelled read consumes nothing, so it can be retried.</summary>
    Task<Result<TextLine?, FileError>> ReadLineAsync(DurableContext context);

    /// <summary>Releases the file; idempotent. Later reads fail with <see cref="FileErrorCode.Invalid"/>.</summary>
    Task CloseAsync(DurableContext context);
}

/// <summary>Options of <see cref="IFileSystem.ReadTextLinesAsync"/>.</summary>
public sealed record ReadTextLinesOptions
{
    /// <summary>Stop after this many lines; zero or less reads none. Null: every line.</summary>
    public int? MaxLines { get; init; }
}

/// <summary>Options of <see cref="IFileSystem.CreateTempFileAsync"/>.</summary>
public sealed record TempFileOptions
{
    /// <summary>Text before the file's unique name.</summary>
    public string? Prefix { get; init; }
    /// <summary>Text after the file's unique name, such as an extension.</summary>
    public string? Suffix { get; init; }
}

/// <summary>Entries below a watched directory that are neither watched nor reported (upstream <c>WatchTarget.exclude</c>).</summary>
public sealed record WatchExclusions
{
    /// <summary>Names starting with <c>.</c>.</summary>
    public bool Hidden { get; init; }
    /// <summary>These names.</summary>
    public ImmutableArray<string> Names { get; init; } = [];
}

/// <summary>A file or directory to watch (upstream <c>WatchTarget</c>). It may be missing; creating it is a change.</summary>
/// <param name="Path">The path, relative paths resolving against the file system's working directory.</param>
public sealed record WatchTarget(string Path)
{
    /// <summary>Watch everything below a directory, not only its entries. Symbolic links below it are not followed.</summary>
    public bool Recursive { get; init; }
    /// <summary>Entries below <see cref="Path"/> that are neither watched nor reported.</summary>
    public WatchExclusions? Exclude { get; init; }
}

/// <summary>
/// What changed (upstream <c>WatchChange</c>): <see cref="WatchPathsChange"/>, something at or below each path may have changed (calls may be
/// spurious; a change is never missed while the watcher is healthy); <see cref="WatchOverflowChange"/>, coverage was uncertain for a while,
/// so rescan everything that is watched; <see cref="WatchErrorChange"/>, the watcher stopped and no calls follow.
/// </summary>
public abstract record WatchChange
{
    private protected WatchChange() { }
}

/// <summary>Something at or below each path may have changed; a directory path covers its whole subtree (upstream <c>{ paths }</c>).</summary>
/// <param name="Paths">Absolute paths, sorted ordinally.</param>
public sealed record WatchPathsChange(ImmutableArray<string> Paths) : WatchChange;

/// <summary>Coverage was uncertain for a while (lost events, reconnect): rescan everything that is watched (upstream <c>{ overflow: true }</c>).</summary>
public sealed record WatchOverflowChange : WatchChange;

/// <summary>The watcher stopped, for example because the watched tree grew past the environment's limit; no calls follow (upstream
/// <c>{ error }</c>).</summary>
/// <param name="Error">Why it stopped.</param>
public sealed record WatchErrorChange(FileError Error) : WatchChange;

/// <summary>How reliably a watcher reports changes (upstream <c>FileWatcher.mode</c>).</summary>
public enum WatchMode
{
    /// <summary><c>native</c>: changes are reported within about two seconds.</summary>
    Native,
    /// <summary><c>polling</c>: the environment compares snapshots, because the file system does not report changes reliably (network and
    /// FUSE file systems); a change undone between two snapshots can be missed.</summary>
    Polling,
}

/// <summary>A running watch (upstream <c>FileWatcher</c>).</summary>
public interface IFileWatcher
{
    /// <summary>How changes are found.</summary>
    WatchMode Mode { get; }

    /// <summary>Stops watching; no change callback starts after this completes. Idempotent.</summary>
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

/// <summary>What <see cref="IFileSystem.ListFilesAsync"/> lists (upstream <c>ListFilesOptions</c>).</summary>
public sealed record ListFilesOptions
{
    /// <summary>Include names starting with <c>.</c> (on Windows also files with the hidden attribute); default true. Entries named
    /// <c>.git</c> are never listed.</summary>
    public bool Hidden { get; init; } = true;

    /// <summary>Inside a git work tree, skip what git ignores: the <c>.gitignore</c> files of the directory, its subdirectories and its
    /// parents, <c>.git/info/exclude</c>, and the global excludes file; default true. Unlike <c>git ls-files</c>, a tracked file that matches
    /// an ignore rule is skipped too. Outside work trees, <c>.gitignore</c> files have no effect.</summary>
    public bool Gitignore { get; init; } = true;

    /// <summary>Names of entries to skip with everything below them, such as <c>node_modules</c>.</summary>
    public ImmutableArray<string> Exclude { get; init; } = [];

    /// <summary>Most entries to list; default 50 000. Upstream's <c>number</c>: anything but a non-negative safe integer fails with
    /// <see cref="FileErrorCode.Invalid"/>.</summary>
    public double? MaxEntries { get; init; }

    /// <summary>Most bytes of the listing: each entry's UTF-8 path plus two; default 4 MiB, at most 15.9 MiB. A non-negative safe
    /// integer.</summary>
    public double? MaxBytes { get; init; }

    /// <summary>Time after which the listing stops; default 10 000 ms. A non-negative safe integer.</summary>
    public double? TimeoutMs { get; init; }
}

/// <summary>One entry of a <see cref="FileListing"/> (upstream <c>FileListEntry</c>).</summary>
/// <param name="Path">Relative to the listed directory, with <c>/</c> separators on every system.</param>
/// <param name="Kind">What the entry is; a symbolic link is listed as such and not followed.</param>
public sealed record FileListEntry(string Path, FileKind Kind);

/// <summary>The entries below a directory (upstream <c>FileListing</c>).</summary>
/// <param name="Entries">Depth first, parents before their entries, siblings in byte order of their names.</param>
/// <param name="Truncated">A bound in <see cref="ListFilesOptions"/> ended the listing early.</param>
public sealed record FileListing(ImmutableArray<FileListEntry> Entries, bool Truncated);

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

    /// <summary>Opens a text file for strict LF line reads.</summary>
    Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, DurableContext context);

    /// <summary>The lines of a text file, as <see cref="OpenTextLineReaderAsync"/> reads them, up to <see cref="ReadTextLinesOptions.MaxLines"/>.</summary>
    Task<Result<ImmutableArray<string>, FileError>> ReadTextLinesAsync(string path, ReadTextLinesOptions? options, DurableContext context);

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

    /// <summary>
    /// Optional (upstream's optional <c>listFiles?</c>): the files, directories and symbolic links below a directory, at any depth, for
    /// indexes such as file completion. Symbolic links are not followed; other kinds of files are not listed; entries that cannot be read are
    /// skipped. See <see cref="ListFilesOptions"/> for hidden and ignored files and for the bounds. Fails with
    /// <see cref="FileErrorCode.NotSupported"/> when the environment cannot list (one that does not implement it, which this default reports,
    /// or an older remote daemon), so callers fall back to their own walk.
    /// </summary>
    Task<Result<FileListing, FileError>> ListFilesAsync(string path, ListFilesOptions? options, DurableContext context) =>
        Task.FromResult<Result<FileListing, FileError>>(new FileError(FileErrorCode.NotSupported, "The environment does not list files", path));

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

    /// <summary>
    /// Reports changes to files and directories, for hosts that load resources from the environment. When the returned watcher exists,
    /// coverage is established: a host that watches before it loads cannot miss a change made during the load. See <see cref="WatchChange"/>
    /// for what is reported and <see cref="IFileWatcher.Mode"/> for how reliably. <paramref name="onChange"/> runs on a background thread, one
    /// call at a time; an exception it throws is ignored.
    /// </summary>
    Task<Result<IFileWatcher, FileError>> WatchAsync(IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange, DurableContext context);

    /// <summary>Creates a new, empty directory under the environment's temporary directory; its name starts with
    /// <paramref name="prefix"/> (default <c>tmp-</c>).</summary>
    Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context);

    /// <summary>Creates a new, empty file in a new temporary directory and returns its path.</summary>
    Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context);

    /// <summary>Releases what the environment still holds, for its owner's shutdown; best effort, never fails.</summary>
    Task CleanupAsync(DurableContext context);
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
