// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/env/node.ts (the FileSystem half of NodeExecutionEnv: path
// resolution, error mapping, the text-line, binary and directory readers, line scans, watches, temporary files and every file operation;
// the watcher itself is LocalFileWatcher, node-watch.ts). The Shell half is LocalExecutionEnv in the durable tools project, which builds on
// this class.
//
// This folder is the local runtime side of the storage boundary (upstream env/node.ts): it alone in PiSharp.Durable/Env uses System.IO.
// Every handle is opened with FileShare.ReadWrite | FileShare.Delete and closed before the operation returns, so on Windows a file can be
// renamed over, truncated or removed while another operation (or an open binary reader) has it open, as on POSIX. Flushes call
// FileStream.Flush(true): FlushFileBuffers on Windows, fsync on Unix. Renames replace the destination (MoveFileEx with
// MOVEFILE_REPLACE_EXISTING on Windows, rename(2) on Unix); transient sharing violations on Windows are retried briefly. Upstream opens a
// binary reader with O_NONBLOCK (and O_NOFOLLOW) so a FIFO is refused without waiting for a writer; .NET has no non-blocking open, so the
// file's type (NativeFiles) is checked before opening, which is not race-free, as upstream's own Windows check is not.
using System.Collections.Immutable;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Env.Local;

/// <summary>The local file system through System.IO (the file-system half of upstream <c>NodeExecutionEnv</c>).</summary>
public class LocalFileSystem : IFileSystem
{
    private const int BinaryReadChunk = 1024 * 1024;
    /// <summary>Chunk size of text-line reads and line scans.</summary>
    private const int TextReadChunk = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private readonly LocalWatchOptions watchOptions;
    private string cwd;

    /// <summary>A file system whose relative paths resolve against <paramref name="cwd"/>.</summary>
    public LocalFileSystem(string cwd) : this(cwd, null) { }

    /// <summary>A file system whose relative paths resolve against <paramref name="cwd"/>, watching as <paramref name="watch"/> says.</summary>
    public LocalFileSystem(string cwd, LocalWatchOptions? watch)
    {
        this.cwd = Path.GetFullPath(cwd ?? throw new ArgumentNullException(nameof(cwd)));
        watchOptions = watch ?? new LocalWatchOptions();
    }

    /// <inheritdoc/>
    /// <remarks>Every local file system sees the same files.</remarks>
    public string Id => "local";

    /// <inheritdoc/>
    public string Cwd { get => cwd; set => cwd = Path.GetFullPath(value ?? throw new ArgumentNullException(nameof(value))); }

    /// <inheritdoc/>
    public virtual Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context)
    {
        try { return Task.FromResult((Result<string, FileError>)ResolvePath(path)); }
        catch (Exception error) { return Task.FromResult((Result<string, FileError>)ToFileError(error, path)); }
    }

    /// <inheritdoc/>
    public virtual Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context) =>
        Task.FromResult((Result<string, FileError>)JoinPath(parts));

    /// <inheritdoc/>
    public virtual Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context) => Run<string>(path, context, resolved =>
    {
        // Node's utf8 decoding keeps a byte-order mark and replaces invalid sequences.
        return After(context, resolved, Utf8.GetString(ReadAll(resolved)));
    });

    /// <inheritdoc/>
    public virtual Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, DurableContext context) =>
        Run<ITextLineReader>(path, context, resolved =>
        {
            if (Directory.Exists(resolved)) return new FileError(FileErrorCode.IsDirectory, "EISDIR: illegal operation on a directory, read", resolved);
            var handle = File.OpenHandle(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (context.IsCancellationRequested)
            {
                handle.Dispose();
                return Aborted(resolved);
            }
            return Result.Ok<ITextLineReader, FileError>(new LocalTextLineReader(this, handle, resolved));
        });

    /// <inheritdoc/>
    public virtual async Task<Result<ImmutableArray<string>, FileError>> ReadTextLinesAsync(string path, ReadTextLinesOptions? options, DurableContext context)
    {
        if (options?.MaxLines is <= 0) return ImmutableArray<string>.Empty;
        var opened = await OpenTextLineReaderAsync(path, context).ConfigureAwait(false);
        if (!opened.IsOk) return opened.Error;
        var lines = ImmutableArray.CreateBuilder<string>();
        try
        {
            while (options?.MaxLines is not { } max || lines.Count < max)
            {
                var line = await opened.Value.ReadLineAsync(context).ConfigureAwait(false);
                if (!line.IsOk) return line.Error;
                if (line.Value is null) break;
                lines.Add(line.Value.Text);
            }
            return lines.ToImmutable();
        }
        finally { await opened.Value.CloseAsync(context).ConfigureAwait(false); }
    }

    /// <summary>Reads up to <paramref name="buffer"/>'s length at <paramref name="offset"/> for a text-line reader (a seam for tests that
    /// hold a read while it is pending, as upstream's spy on FileHandle.read does).</summary>
    internal virtual ValueTask<int> ReadTextChunkAsync(SafeFileHandle handle, Memory<byte> buffer, long offset) =>
        RandomAccess.ReadAsync(handle, buffer, offset);

    /// <inheritdoc/>
    public virtual Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context) =>
        Run<byte[]>(path, context, resolved => After(context, resolved, ReadAll(resolved)));

    /// <inheritdoc/>
    public virtual Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context) =>
        Run<IBinaryReader>(path, context, resolved =>
        {
            // There is no portable O_NOFOLLOW; check the final component before opening (not race-free, as upstream on Windows).
            if (options?.NoFollow == true && IsLink(resolved)) return SymlinkRefused(resolved);
            if (Directory.Exists(resolved)) return new FileError(FileErrorCode.IsDirectory, "EISDIR: illegal operation on a directory, read", resolved);
            // Opening a FIFO would wait for a writer; refuse what is not a regular file before opening it (Windows has no FIFOs).
            if (!OperatingSystem.IsWindows() && NativeFiles.TryGet(resolved, follow: true) is { Type: NativeFileType.Other })
                return new FileError(FileErrorCode.Invalid, "Not a regular file", resolved);
            var handle = File.OpenHandle(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (context.IsCancellationRequested)
            {
                handle.Dispose();
                return Aborted(resolved);
            }
            return Result.Ok<IBinaryReader, FileError>(new LocalBinaryReader(handle, resolved));
        });

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) =>
        Run<Unit>(path, context, resolved =>
        {
            CreateParent(resolved);
            if (context.IsCancellationRequested) return Aborted(resolved);
            Retry(() =>
            {
                using var stream = new FileStream(resolved, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                stream.Write(content.Span);
            });
            return Unit.Value;
        });

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) =>
        Run<Unit>(path, context, resolved =>
        {
            CreateParent(resolved);
            if (context.IsCancellationRequested) return Aborted(resolved);
            Retry(() =>
            {
                using var stream = new FileStream(resolved, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                stream.Write(content.Span);
            });
            return After(context, resolved, Unit.Value);
        });

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) => Run<Unit>(path, context, resolved =>
    {
        if (size < 0 || size > Ids.MaxSafeInteger)
            return new FileError(FileErrorCode.Invalid, "File size must be a non-negative safe integer", resolved);
        if (Directory.Exists(resolved)) return new FileError(FileErrorCode.IsDirectory, "EISDIR: illegal operation on a directory, open", resolved);
        Retry(() =>
        {
            using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
            stream.SetLength(size);
        });
        return After(context, resolved, Unit.Value);
    });

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context) => Run<Unit>(path, context, resolved =>
    {
        // POSIX refuses to open a directory for writing and Windows refuses to open it as a file; report both as upstream does.
        if (Directory.Exists(resolved)) return new FileError(FileErrorCode.IsDirectory, "Is a directory", resolved);
        Retry(() =>
        {
            using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
            SyncFile(stream);
        });
        return After(context, resolved, Unit.Value);
    });

    /// <summary>Flushes an opened file's contents and metadata to the storage device (upstream <c>FileHandle.sync</c>; a seam for tests that
    /// observe or fail the sync, as upstream's spy does).</summary>
    internal virtual void SyncFile(FileStream stream) => stream.Flush(flushToDisk: true);

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context)
    {
        string source, destination;
        try { (source, destination) = (ResolvePath(sourcePath), ResolvePath(destinationPath)); }
        catch (Exception error) { return Task.FromResult((Result<Unit, FileError>)ToFileError(error, sourcePath)); }
        if (context.IsCancellationRequested) return Task.FromResult((Result<Unit, FileError>)Aborted(destination));
        try
        {
            if (Directory.Exists(source) && !IsLink(source)) Retry(() => Directory.Move(source, destination), retryDenied: true);
            else Retry(() => File.Move(source, destination, overwrite: true), retryDenied: true);
            return Task.FromResult((Result<Unit, FileError>)Unit.Value);
        }
        // Upstream reports the source path, which is what rename(2) failures name.
        catch (Exception error) { return Task.FromResult((Result<Unit, FileError>)ToFileError(error, source)); }
    }

    /// <inheritdoc/>
    public virtual Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context) =>
        Run<FileMetadata>(path, context, resolved => Stat(resolved));

    /// <inheritdoc/>
    public virtual Task<Result<ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context) =>
        Run<ImmutableArray<FileMetadata>>(path, context, resolved =>
        {
            if (DirectoryProblem(resolved) is { } problem) return problem;
            var infos = ImmutableArray.CreateBuilder<FileMetadata>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(resolved))
            {
                if (context.IsCancellationRequested) return Aborted(resolved);
                var info = Stat(entry);
                if (info.IsOk) infos.Add(info.Value);
                else if (info.Error.Code != FileErrorCode.Invalid) return info.Error;
            }
            return infos.ToImmutable();
        });

    /// <inheritdoc/>
    public virtual Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context) => Run<IDirReader>(path, context, resolved =>
    {
        if (DirectoryProblem(resolved) is { } problem) return problem;
        var entries = Directory.EnumerateFileSystemEntries(resolved).GetEnumerator();
        if (context.IsCancellationRequested)
        {
            entries.Dispose();
            return Aborted(resolved);
        }
        return Result.Ok<IDirReader, FileError>(new LocalDirReader(entries, resolved));
    });

    /// <inheritdoc/>
    public virtual Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context) =>
        Run<string>(path, context, resolved => (Result<string, FileError>)RealPath(resolved));

    /// <inheritdoc/>
    public virtual async Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context)
    {
        var result = await FileInfoAsync(path, context);
        if (result.IsOk) return true;
        if (result.Error.Code == FileErrorCode.NotFound) return false;
        return result.Error;
    }

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) =>
        Run<Unit>(path, context, resolved =>
        {
            if (options?.Recursive ?? true)
            {
                if (File.Exists(resolved)) return new FileError(FileErrorCode.Unknown, "EEXIST: file already exists, mkdir", resolved);
                Directory.CreateDirectory(resolved);
                return Unit.Value;
            }
            var parent = Path.GetDirectoryName(resolved);
            if (parent is not null && !Directory.Exists(parent))
                return new FileError(FileErrorCode.NotFound, "ENOENT: no such file or directory, mkdir", resolved);
            if (File.Exists(resolved) || Directory.Exists(resolved) || IsLink(resolved))
                return new FileError(FileErrorCode.Unknown, "EEXIST: file already exists, mkdir", resolved);
            Directory.CreateDirectory(resolved);
            return Unit.Value;
        });

    /// <inheritdoc/>
    public virtual Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context) =>
        Run<Unit>(path, context, resolved =>
        {
            var info = Stat(resolved);
            if (!info.IsOk)
            {
                if (info.Error.Code == FileErrorCode.NotFound && options?.Force == true) return Unit.Value;
                return info.Error;
            }
            switch (info.Value.Kind)
            {
                case FileKind.Directory:
                    if (options?.Recursive != true)
                        return new FileError(FileErrorCode.IsDirectory, "Path is a directory: rm returned EISDIR (is a directory)", resolved);
                    Retry(() => Directory.Delete(resolved, recursive: true), retryDenied: true);
                    break;
                case FileKind.Symlink when (File.GetAttributes(resolved) & FileAttributes.Directory) != 0:
                    // A directory link (or junction) is removed itself, never its target's contents.
                    Retry(() => Directory.Delete(resolved, recursive: false), retryDenied: true);
                    break;
                default:
                    Retry(() => File.Delete(resolved), retryDenied: true);
                    break;
            }
            return Unit.Value;
        });

    /// <inheritdoc/>
    public virtual async Task<Result<IFileWatcher, FileError>> WatchAsync(IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange,
        DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(onChange);
        if (context.IsCancellationRequested) return Aborted(null);
        try
        {
            var watcher = await LocalFileWatcher.OpenAsync(targets, ResolvePath, onChange, watchOptions).ConfigureAwait(false);
            if (context.IsCancellationRequested)
            {
                await watcher.CloseAsync(context).ConfigureAwait(false);
                return Aborted(null);
            }
            return watcher;
        }
        catch (Exception error) { return ToFileError(error, null); }
    }

    /// <inheritdoc/>
    public virtual Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context)
    {
        if (context.IsCancellationRequested) return Task.FromResult<Result<string, FileError>>(Aborted(null));
        // mkdtemp: the prefix followed by random characters, in the operating system's temporary directory.
        try { return Task.FromResult<Result<string, FileError>>(Directory.CreateTempSubdirectory(prefix ?? "tmp-").FullName); }
        catch (Exception error) { return Task.FromResult<Result<string, FileError>>(ToFileError(error, null)); }
    }

    /// <inheritdoc/>
    public virtual async Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context)
    {
        var directory = await CreateTempDirAsync("tmp-", context).ConfigureAwait(false);
        if (!directory.IsOk) return directory.Error;
        var file = Path.Join(directory.Value, $"{options?.Prefix}{Guid.NewGuid():D}{options?.Suffix}");
        try
        {
            using (new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1)) { }
            return file;
        }
        catch (Exception error) { return ToFileError(error, file); }
    }

    /// <inheritdoc/>
    /// <remarks>The file system holds nothing between calls; <see cref="IFileWatcher"/>s are closed by their owners.</remarks>
    public virtual Task CleanupAsync(DurableContext context) => Task.CompletedTask;

    /// <summary>Resolves <paramref name="path"/> like upstream <c>resolvePath</c>: <c>~</c> and <c>~/</c> name the home directory, a
    /// <c>file://</c> URL its local path, and relative paths resolve against <see cref="Cwd"/>.</summary>
    protected string ResolvePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path;
        if (normalized == "~") normalized = Home();
        else if (normalized.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && normalized.StartsWith("~\\", StringComparison.Ordinal)))
            normalized = Path.Join(Home(), normalized[2..]);
        else if (normalized.StartsWith("file://", StringComparison.Ordinal))
        {
            // Malformed URLs stay ordinary paths so file operations keep their non-throwing contract.
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.IsFile) normalized = uri.LocalPath;
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized, cwd));
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Node's <c>path.join</c>: the non-empty parts joined with the separator, then <c>.</c> and <c>..</c> segments normalized.</summary>
    private static string JoinPath(IReadOnlyList<string> parts)
    {
        var joined = string.Join(Path.DirectorySeparatorChar, parts.Where(part => part.Length > 0));
        if (joined.Length == 0) return ".";
        if (OperatingSystem.IsWindows()) joined = joined.Replace('/', '\\');
        var separator = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(joined) ?? "";
        var segments = new List<string>();
        foreach (var segment in joined[root.Length..].Split(separator))
        {
            if (segment is "" or ".") continue;
            if (segment == ".." && segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
            else if (segment != ".." || root.Length == 0) segments.Add(segment);
        }
        var result = root + string.Join(separator, segments);
        if (result.Length == 0) return ".";
        return segments.Count > 0 && joined[^1] == separator ? result + separator : result;
    }

    private static void CreateParent(string resolved)
    {
        if (Path.GetDirectoryName(resolved) is { Length: > 0 } parent) Directory.CreateDirectory(parent);
    }

    private static byte[] ReadAll(string resolved)
    {
        if (Directory.Exists(resolved)) throw new FileError(FileErrorCode.IsDirectory, "EISDIR: illegal operation on a directory, read", resolved);
        using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static FileError? DirectoryProblem(string resolved)
    {
        if (Directory.Exists(resolved)) return null;
        if (File.Exists(resolved)) return new FileError(FileErrorCode.NotDirectory, "ENOTDIR: not a directory, scandir", resolved);
        return new FileError(FileErrorCode.NotFound, "ENOENT: no such file or directory, scandir", resolved);
    }

    private static bool IsLink(string resolved)
    {
        try { return new FileInfo(resolved).LinkTarget is not null; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Upstream <c>lstat</c> plus <c>fileInfoFromStats</c>: metadata of the path itself, a final link not followed.</summary>
    internal static Result<FileMetadata, FileError> Stat(string resolved)
    {
        try
        {
            var attributes = File.GetAttributes(resolved);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = isDirectory ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            var kind = info.LinkTarget is not null ? FileKind.Symlink : isDirectory ? FileKind.Directory : FileKind.File;
            if (kind == FileKind.File && (attributes & FileAttributes.Device) != 0)
                return new FileError(FileErrorCode.Invalid, "Unsupported file type", resolved);
            var size = kind == FileKind.File ? ((FileInfo)info).Length : 0;
            return new FileMetadata(Path.GetFileName(resolved), resolved, kind, size, (info.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds);
        }
        catch (Exception error) { return ToFileError(error, resolved); }
    }

    /// <summary>Upstream <c>realpath</c>: every link on the path resolved, component by component.</summary>
    private static string RealPath(string resolved)
    {
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            throw new FileNotFoundException("ENOENT: no such file or directory, realpath", resolved);
        var root = Path.GetPathRoot(resolved)!;
        var current = root;
        var pending = new Queue<string>(resolved[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        for (var hops = 0; pending.TryDequeue(out var segment);)
        {
            var next = Path.Join(current, segment);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is { } target)
            {
                if (++hops > 40) throw new IOException("ELOOP: too many symbolic links encountered, realpath");
                var absolute = Path.GetFullPath(target, current);
                var rest = pending.ToArray();
                root = Path.GetPathRoot(absolute)!;
                current = root;
                pending = new Queue<string>(absolute[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest));
                continue;
            }
            // The on-disk spelling of the name, as realpath reports it on case-insensitive file systems.
            var parent = new DirectoryInfo(current);
            var actual = parent.Exists ? parent.EnumerateFileSystemInfos(segment).FirstOrDefault()?.Name : null;
            current = Path.Join(current, actual ?? segment);
        }
        return current;
    }

    private static FileError SymlinkRefused(string resolved, Exception? cause = null) =>
        new(FileErrorCode.Invalid, "Refusing to follow a symbolic link", resolved, cause);

    internal static FileError Aborted(string? path) => new(FileErrorCode.Aborted, "aborted", path);

    private static Result<T, FileError> After<T>(DurableContext context, string resolved, T value) =>
        context.IsCancellationRequested ? Aborted(resolved) : value;

    /// <summary>Upstream <c>toFileError</c>: .NET exceptions mapped to file error codes.</summary>
    internal static FileError ToFileError(Exception error, string? path)
    {
        switch (error)
        {
            case FileError fileError: return fileError;
            case OperationCanceledException: return new FileError(FileErrorCode.Aborted, error.Message, path, error);
            case FileNotFoundException or DirectoryNotFoundException: return new FileError(FileErrorCode.NotFound, error.Message, path, error);
            case UnauthorizedAccessException:
                return path is not null && Directory.Exists(path)
                    ? new FileError(FileErrorCode.IsDirectory, error.Message, path, error)
                    : new FileError(FileErrorCode.PermissionDenied, error.Message, path, error);
            case PathTooLongException: return new FileError(FileErrorCode.Unknown, error.Message, path, error);
            case ArgumentException: return new FileError(FileErrorCode.Invalid, error.Message, path, error);
            case IOException when OperatingSystem.IsWindows():
                return (error.HResult & 0xFFFF) switch
                {
                    2 or 3 or 53 or 161 => new FileError(FileErrorCode.NotFound, error.Message, path, error),
                    5 => new FileError(FileErrorCode.PermissionDenied, error.Message, path, error),
                    267 => new FileError(FileErrorCode.NotDirectory, error.Message, path, error),
                    _ => new FileError(FileErrorCode.Unknown, error.Message, path, error),
                };
            default: return new FileError(FileErrorCode.Unknown, error.Message, path, error);
        }
    }

    /// <summary>Retries an operation that a transient Windows sharing violation (an indexer or scanner holding the file) can fail; a
    /// rename or removal also retries access denied, which Windows reports while a replaced or removed file is still delete-pending.</summary>
    private static void Retry(Action operation, bool retryDenied = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (Exception error) when (attempt < 5 && OperatingSystem.IsWindows() && IsTransient(error, retryDenied)) { Thread.Sleep(10 * (attempt + 1)); }
        }
    }

    private static bool IsTransient(Exception error, bool retryDenied) =>
        error is IOException { HResult: var result } && (result & 0xFFFF) is 32 or 33 || retryDenied && error is UnauthorizedAccessException;

    private Task<Result<T, FileError>> Run<T>(string path, DurableContext context, Func<string, Result<T, FileError>> body)
    {
        string? resolved = null;
        try
        {
            resolved = ResolvePath(path);
            if (context.IsCancellationRequested) return Task.FromResult<Result<T, FileError>>(Aborted(resolved));
            return Task.FromResult(body(resolved));
        }
        catch (Exception error) { return Task.FromResult<Result<T, FileError>>(ToFileError(error, resolved ?? path)); }
    }

    private sealed class LocalBinaryReader(SafeFileHandle handle, string path) : IBinaryReader
    {
        private bool closed;

        public Task<Result<FileMetadata, FileError>> InfoAsync(DurableContext context)
        {
            if (context.IsCancellationRequested) return Task.FromResult<Result<FileMetadata, FileError>>(Aborted(path));
            if (closed) return Task.FromResult<Result<FileMetadata, FileError>>(Closed());
            try
            {
                var modified = File.GetLastWriteTimeUtc(handle);
                return Task.FromResult<Result<FileMetadata, FileError>>(new FileMetadata(Path.GetFileName(path), path, FileKind.File,
                    RandomAccess.GetLength(handle), (modified - DateTime.UnixEpoch).TotalMilliseconds));
            }
            catch (Exception error) { return Task.FromResult<Result<FileMetadata, FileError>>(ToFileError(error, path)); }
        }

        public Task<Result<byte[], FileError>> ReadAsync(long offset, long length, DurableContext context)
        {
            if (context.IsCancellationRequested) return Task.FromResult<Result<byte[], FileError>>(Aborted(path));
            if (closed) return Task.FromResult<Result<byte[], FileError>>(Closed());
            if (offset < 0 || offset > Ids.MaxSafeInteger || length < 0 || length > Ids.MaxSafeInteger)
                return Task.FromResult<Result<byte[], FileError>>(
                    new FileError(FileErrorCode.Invalid, "Offset and length must be non-negative safe integers", path));
            try
            {
                var bytes = new MemoryStream();
                var total = 0L;
                while (total < length)
                {
                    var chunk = new byte[(int)Math.Min(length - total, BinaryReadChunk)];
                    var read = RandomAccess.Read(handle, chunk, offset + total);
                    if (context.IsCancellationRequested) return Task.FromResult<Result<byte[], FileError>>(Aborted(path));
                    if (read == 0) break;
                    bytes.Write(chunk, 0, read);
                    total += read;
                }
                return Task.FromResult<Result<byte[], FileError>>(bytes.ToArray());
            }
            catch (Exception error) { return Task.FromResult<Result<byte[], FileError>>(ToFileError(error, path)); }
        }

        public async Task<Result<LineScan, FileError>> ScanLinesAsync(long startLine, long? endLine, DurableContext context)
        {
            if (context.IsCancellationRequested) return Aborted(path);
            if (closed) return Closed();
            LineScanner scanner;
            try { scanner = new LineScanner(startLine, endLine); }
            catch (ArgumentOutOfRangeException) { return new FileError(FileErrorCode.Invalid, "Invalid line range", path); }
            var chunk = new byte[TextReadChunk];
            try
            {
                for (var position = 0L; ;)
                {
                    var read = await RandomAccess.ReadAsync(handle, chunk, position).ConfigureAwait(false);
                    if (context.IsCancellationRequested) return Aborted(path);
                    if (read == 0) return scanner.Finish();
                    scanner.Push(chunk.AsSpan(0, read));
                    position += read;
                }
            }
            catch (Exception error) { return ToFileError(error, path); }
        }

        public Task CloseAsync(DurableContext context)
        {
            if (!closed)
            {
                closed = true;
                handle.Dispose();
            }
            return Task.CompletedTask;
        }

        private FileError Closed() => new(FileErrorCode.Invalid, "Binary reader is closed", path);
    }

    /// <summary>Strict LF reader (upstream <c>NodeTextLineReader</c>): it reports whether its final line was newline-terminated.</summary>
    private sealed class LocalTextLineReader(LocalFileSystem owner, SafeFileHandle handle, string path) : ITextLineReader
    {
        private readonly StreamDecoder decoder = new();
        private readonly byte[] chunk = new byte[TextReadChunk];
        /// <summary>Decoded text not yet returned starts at <see cref="start"/>; pieces of a long line wait in <see cref="pending"/>.</summary>
        private string buffered = "";
        private int start;
        private readonly StringBuilder pending = new();
        private long byteOffset;
        private bool ended, closed;

        public async Task<Result<TextLine?, FileError>> ReadLineAsync(DurableContext context)
        {
            if (context.IsCancellationRequested) return Aborted(path);
            if (closed) return new FileError(FileErrorCode.Invalid, "Text line reader is closed", path);
            try
            {
                while (true)
                {
                    var newline = buffered.IndexOf('\n', start);
                    if (newline != -1)
                    {
                        var text = Take(newline);
                        start = newline + 1;
                        return new TextLine(text, Terminated: true);
                    }
                    if (ended)
                    {
                        var text = Take(buffered.Length);
                        buffered = "";
                        start = 0;
                        if (text.Length == 0) return Result.Ok<TextLine?, FileError>(null);
                        return new TextLine(text, Terminated: false);
                    }
                    // Explicit positions allow an aborted read to be retried without skipping bytes.
                    var read = await owner.ReadTextChunkAsync(handle, chunk, byteOffset).ConfigureAwait(false);
                    if (context.IsCancellationRequested) return Aborted(path);
                    byteOffset += read;
                    // The rest of the buffer belongs to a line that continues in the new text.
                    pending.Append(buffered, start, buffered.Length - start);
                    start = 0;
                    if (read == 0)
                    {
                        buffered = decoder.Finish();
                        ended = true;
                    }
                    else buffered = decoder.Decode(chunk.AsSpan(0, read));
                }
            }
            catch (Exception error) { return ToFileError(error, path); }
        }

        /// <summary>The pending start of the line plus the buffer from <see cref="start"/> to <paramref name="end"/>.</summary>
        private string Take(int end)
        {
            if (pending.Length == 0) return buffered[start..end];
            pending.Append(buffered, start, end - start);
            var text = pending.ToString();
            pending.Clear();
            return text;
        }

        public Task CloseAsync(DurableContext context)
        {
            if (!closed)
            {
                closed = true;
                buffered = "";
                pending.Clear();
                // Closing is best-effort, including after cancellation or an earlier I/O failure.
                handle.Dispose();
            }
            return Task.CompletedTask;
        }
    }

    private sealed class LocalDirReader(IEnumerator<string> entries, string path) : IDirReader
    {
        private bool done, closed;

        public Task<Result<DirPage, FileError>> NextAsync(int maxEntries, DurableContext context)
        {
            if (context.IsCancellationRequested) return Task.FromResult<Result<DirPage, FileError>>(Aborted(path));
            if (closed) return Task.FromResult<Result<DirPage, FileError>>(new FileError(FileErrorCode.Invalid, "Directory reader is closed", path));
            if (maxEntries <= 0)
                return Task.FromResult<Result<DirPage, FileError>>(new FileError(FileErrorCode.Invalid, "maxEntries must be a positive safe integer", path));
            var page = ImmutableArray.CreateBuilder<FileMetadata>();
            try
            {
                while (!done && page.Count < maxEntries)
                {
                    if (!entries.MoveNext())
                    {
                        done = true;
                        break;
                    }
                    if (context.IsCancellationRequested) return Task.FromResult<Result<DirPage, FileError>>(Aborted(path));
                    var info = Stat(entries.Current);
                    // Removed between enumeration and stat: not part of the listing any more.
                    if (info.IsOk) page.Add(info.Value);
                    else if (info.Error.Code is not (FileErrorCode.NotFound or FileErrorCode.Invalid))
                        return Task.FromResult<Result<DirPage, FileError>>(info.Error);
                }
                return Task.FromResult<Result<DirPage, FileError>>(new DirPage(page.ToImmutable(), done));
            }
            catch (Exception error) { return Task.FromResult<Result<DirPage, FileError>>(ToFileError(error, path)); }
        }

        public Task CloseAsync(DurableContext context)
        {
            if (!closed)
            {
                closed = true;
                entries.Dispose();
            }
            return Task.CompletedTask;
        }
    }
}
