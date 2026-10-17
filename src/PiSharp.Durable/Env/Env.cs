// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/index.ts (results, file and execution errors, file
// metadata). The FileSystem capability, its readers, line scans and watches are in FileSystem.cs; the Shell and ExecutionEnv halves in
// Shell.cs; decode.ts and line-scan.ts, which index.ts re-exports, in Decode.cs and LineScan.cs.
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

/// <summary>Why a command could not run to completion (upstream <c>ExecutionErrorCode</c>; <c>"shell_unavailable"</c> is
/// <see cref="ShellUnavailable"/>). A command that ran and exited non-zero is not an error.</summary>
public enum ExecutionErrorCode
{
    /// <summary><c>aborted</c>: the context was cancelled.</summary>
    Aborted,
    /// <summary><c>timeout</c>: the command outlived its timeout, or the timeout was invalid.</summary>
    Timeout,
    /// <summary><c>shell_unavailable</c>: no shell to run a string command.</summary>
    ShellUnavailable,
    /// <summary><c>spawn_error</c>: the program could not be started.</summary>
    SpawnError,
    /// <summary><c>callback_error</c>: the output callback threw.</summary>
    CallbackError,
    /// <summary><c>unknown</c>.</summary>
    Unknown,
}

/// <summary>A command that could not run to completion (upstream <c>ExecutionError</c>).</summary>
public sealed class ExecutionError(ExecutionErrorCode code, string message, Exception? cause = null) : Exception(message, cause)
{
    /// <summary>The failure class.</summary>
    public ExecutionErrorCode Code { get; } = code;

    /// <summary>Spill file of a command that timed out or was aborted after its output crossed the spill thresholds.</summary>
    public string? SpillPath { get; set; }

    /// <summary>Upstream's code string, such as <c>"spawn_error"</c>.</summary>
    public string CodeName => Code switch
    {
        ExecutionErrorCode.ShellUnavailable => "shell_unavailable",
        ExecutionErrorCode.SpawnError => "spawn_error",
        ExecutionErrorCode.CallbackError => "callback_error",
        _ => Code.ToString().ToLowerInvariant(),
    };
}
