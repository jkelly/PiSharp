// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/env/index.ts (results, file errors and file metadata; the
// FileSystem capability is in FileSystem.cs). The Shell and ExecutionEnv halves, watches, text-line readers, line scans and temporary
// files join with the local execution environment in P9; JSONL storage (P4) needs only the file system.
using System.Diagnostics.CodeAnalysis;

namespace PiSharp.Durable.Env;

/// <summary>The value of an operation that succeeds with nothing to return (upstream <c>void</c>).</summary>
public readonly record struct Unit
{
    /// <summary>The only value.</summary>
    public static Unit Value => default;
}

/// <summary>Result of a fallible operation (upstream <c>Result&lt;TValue, TError&gt;</c>). Expected failures are returned instead of thrown.
/// A value converts implicitly to a success and an error to a failure.</summary>
public readonly struct Result<TValue, TError> where TError : Exception
{
    private readonly TValue? value;
    private readonly TError? error;

    private Result(bool ok, TValue? value, TError? error) { IsOk = ok; this.value = value; this.error = error; }

    /// <summary>A success.</summary>
    public static Result<TValue, TError> Ok(TValue value) => new(true, value, null);

    /// <summary>A failure.</summary>
    public static Result<TValue, TError> Err(TError error) => new(false, default, error ?? throw new ArgumentNullException(nameof(error)));

    /// <summary>Whether the operation succeeded (upstream <c>ok</c>).</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsOk { get; }

    /// <summary>The value of a success.</summary>
    public TValue Value => IsOk ? value! : throw new InvalidOperationException("The result is a failure.", error);

    /// <summary>The error of a failure; null for a success.</summary>
    public TError? Error => error;

    /// <summary>The value, or throws the error (upstream <c>getOrThrow</c>).</summary>
    public TValue GetOrThrow() => IsOk ? value! : throw (Exception?)error ?? new InvalidOperationException("The result is uninitialized.");

    /// <summary>The value, or the default for a failure (upstream <c>getOrUndefined</c>).</summary>
    public TValue? GetOrDefault() => IsOk ? value : default;

    /// <summary>A success.</summary>
    public static implicit operator Result<TValue, TError>(TValue value) => Ok(value);

    /// <summary>A failure.</summary>
    public static implicit operator Result<TValue, TError>(TError error) => Err(error);

    /// <inheritdoc/>
    public override string ToString() => IsOk ? $"ok: {value}" : $"error: {error?.Message}";
}

/// <summary>Constructors for <see cref="Result{TValue, TError}"/> (upstream <c>ok</c>, <c>err</c>, <c>toError</c>).</summary>
public static class Result
{
    /// <summary>A success.</summary>
    public static Result<TValue, TError> Ok<TValue, TError>(TValue value) where TError : Exception => Result<TValue, TError>.Ok(value);

    /// <summary>A failure.</summary>
    public static Result<TValue, TError> Err<TValue, TError>(TError error) where TError : Exception => Result<TValue, TError>.Err(error);

    /// <summary>An exception for any thrown value: an exception itself, or one whose message describes the value.</summary>
    public static Exception ToError(object? error) => error switch
    {
        Exception exception => exception,
        string text => new InvalidOperationException(text),
        _ => new InvalidOperationException(error?.ToString() ?? "undefined"),
    };
}

/// <summary>What a path names (upstream <c>FileKind</c>).</summary>
public enum FileKind
{
    /// <summary>A regular file.</summary>
    File,
    /// <summary>A directory.</summary>
    Directory,
    /// <summary>A symbolic link, not followed.</summary>
    Symlink,
}

/// <summary>Why a file operation failed (upstream <c>FileErrorCode</c>; <c>"not_found"</c> is <see cref="NotFound"/>).</summary>
public enum FileErrorCode
{
    /// <summary><c>aborted</c>: the context was cancelled.</summary>
    Aborted,
    /// <summary><c>not_found</c>.</summary>
    NotFound,
    /// <summary><c>permission_denied</c>.</summary>
    PermissionDenied,
    /// <summary><c>not_directory</c>.</summary>
    NotDirectory,
    /// <summary><c>is_directory</c>.</summary>
    IsDirectory,
    /// <summary><c>invalid</c>.</summary>
    Invalid,
    /// <summary><c>not_supported</c>.</summary>
    NotSupported,
    /// <summary><c>unknown</c>.</summary>
    Unknown,
}

/// <summary>A failed file operation (upstream <c>FileError</c>).</summary>
public sealed class FileError(FileErrorCode code, string message, string? path = null, Exception? cause = null) : Exception(message, cause)
{
    /// <summary>The failure class.</summary>
    public FileErrorCode Code { get; } = code;

    /// <summary>The path the failure concerns, when known.</summary>
    public string? Path { get; } = path;

    /// <summary>Upstream's code string, such as <c>"not_found"</c>.</summary>
    public string CodeName => Code switch
    {
        FileErrorCode.NotFound => "not_found",
        FileErrorCode.PermissionDenied => "permission_denied",
        FileErrorCode.NotDirectory => "not_directory",
        FileErrorCode.IsDirectory => "is_directory",
        FileErrorCode.NotSupported => "not_supported",
        _ => Code.ToString().ToLowerInvariant(),
    };
}

/// <summary>Metadata of one path, not following a final symbolic link (upstream <c>FileInfo</c>; renamed to stay clear of the
/// base library's FileInfo).</summary>
/// <param name="Name">The final path component.</param>
/// <param name="Path">The absolute path.</param>
/// <param name="Kind">What the path names.</param>
/// <param name="Size">Size in bytes of a file.</param>
/// <param name="MtimeMs">Last modification, in milliseconds since the Unix epoch.</param>
public sealed record FileMetadata(string Name, string Path, FileKind Kind, long Size, double MtimeMs);
