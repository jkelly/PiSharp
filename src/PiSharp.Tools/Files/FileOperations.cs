namespace PiSharp.Tools.Files;

/// <summary>
/// Read/write bounds and the source ReadToolOptions. Pi itself has no size limits; these memory bounds default to the
/// largest admitted values, and text output is still truncated to 2000 lines or 50KB as in the source.
/// </summary>
public sealed record ReadWriteToolOptions(int MaximumReadBytes = 64 * 1024 * 1024,
    int MaximumWriteBytes = 64 * 1024 * 1024, int MaximumArgumentCharacters = 8 * 1024 * 1024, int MaximumPathCharacters = 4096)
{
    /// <summary>Source autoResizeImages (settings images.autoResize). Default true.</summary>
    public bool AutoResizeImages { get; init; } = true;
    /// <summary>Source resizeOptions: the model's image resize profile, or the built-in 2000x2000 / 4.5MB defaults.</summary>
    public PiSharp.Tools.Images.ImageResizeOptions? ImageResizeOptions { get; init; }
    /// <summary>The decode/encode backend (Photon upstream). Default: the dependency-free built-in codec.</summary>
    public PiSharp.Tools.Images.IImageCodec? ImageCodec { get; init; }
    /// <summary>Source ctx.model.input: false adds the non-vision note to image reads; null means no current model.</summary>
    public Func<bool?>? CurrentModelSupportsImages { get; init; }
}

public enum FileToolFailure { ResourceLimit, UnsupportedContent }

public sealed class FileToolException : Exception
{
    public FileToolFailure Failure { get; }
    public FileToolException(FileToolFailure failure) : base(failure == FileToolFailure.ResourceLimit
        ? "File content exceeds the supported input limit."
        : "File content is not valid UTF-8 text; editing it would replace its undecodable bytes.") => Failure = failure;
}

/// <summary>Trusted host seam. Async methods settle all their owned I/O and cleanup before returning or throwing.</summary>
public interface IFileOperations
{
    ValueTask<bool> ExistsAsync(string absolutePath, CancellationToken cancellationToken);
    ValueTask<string> CanonicalizeAsync(string absolutePath, CancellationToken cancellationToken);
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(string absolutePath, int maximumBytes, CancellationToken cancellationToken);
    ValueTask CreateDirectoryAsync(string absolutePath, CancellationToken cancellationToken);
    ValueTask WriteAsync(string absolutePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

/// <summary>Framework file I/O with bounded reads and an existing-segment symlink/junction resolution profile.</summary>
public sealed class LocalFileOperations : IDirectoryFileOperations
{
    public ValueTask<bool> IsDirectoryAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(absolutePath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(absolutePath) : new FileInfo(absolutePath);
            attributes = (info.ResolveLinkTarget(true) ?? throw new IOException("Cannot resolve filesystem link.")).Attributes;
        }
        return ValueTask.FromResult((attributes & FileAttributes.Directory) != 0);
    }

    public ValueTask<System.Collections.Immutable.ImmutableArray<string>> ReadDirectoryAsync(string absolutePath,
        int maximumEntries, int maximumNameCharacters, CancellationToken cancellationToken)
    {
        if (maximumEntries < 1 || maximumNameCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        cancellationToken.ThrowIfCancellationRequested();
        var names = System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
        long characters = 0;
        // The enumerator owns the OS directory handle; using joins disposal even on cancellation or a bound failure.
        using var entries = Directory.EnumerateFileSystemEntries(absolutePath).GetEnumerator();
        while (entries.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entries.Current);
            characters += name.Length;
            if (names.Count == maximumEntries || characters > maximumNameCharacters)
                throw new FileToolException(FileToolFailure.ResourceLimit);
            names.Add(name);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(names.ToImmutable());
    }

    public ValueTask<bool> ExistsAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { _ = File.GetAttributes(absolutePath); return ValueTask.FromResult(true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ValueTask.FromResult(false); }
    }

    public ValueTask<string> CanonicalizeAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(absolutePath);
        try { return ValueTask.FromResult(ResolveSegments(full, 32, cancellationToken)); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return ValueTask.FromResult(full); }
    }

    private static string ResolveSegments(string full, int remainingLinks, CancellationToken token)
    {
        var root = Path.GetPathRoot(full) ?? throw new IOException("An absolute path is required.");
        var current = root;
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            current = Path.Combine(current, segments[index]);
            var attributes = File.GetAttributes(current);
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                if (remainingLinks == 0) throw new IOException("Filesystem link resolution exceeds the supported depth.");
                var target = info.ResolveLinkTarget(returnFinalTarget: true) ?? throw new IOException("Cannot resolve filesystem link.");
                current = ResolveSegments(Path.GetFullPath(target.FullName), --remainingLinks, token);
                attributes = File.GetAttributes(current);
            }
            if (index + 1 < segments.Length && (attributes & FileAttributes.Directory) == 0)
                throw new DirectoryNotFoundException("A parent path is not a directory.");
        }
        return Path.GetFullPath(current);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string absolutePath, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
        using var collected = new MemoryStream();
        var buffer = new byte[Math.Min(81920, maximumBytes + 1)];
        while (true)
        {
            var remaining = maximumBytes - (int)collected.Length;
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (count > remaining) throw new FileToolException(FileToolFailure.ResourceLimit);
            collected.Write(buffer, 0, count);
        }
        return collected.ToArray();
    }

    public ValueTask CreateDirectoryAsync(string absolutePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(absolutePath);
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(string absolutePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.Read,
            81920, FileOptions.Asynchronous);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
