// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/env/index.ts (the portable Shell capability and ExecutionEnv,
// FileSystem & Shell). Upstream's `string | readonly string[]` command is ShellCommand, which converts implicitly from either; its
// `onOutput(text, context, info)` callback is ShellOutputHandler; timeouts stay in seconds.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Env;

/// <summary>A command to run (upstream <c>string | readonly string[]</c>): a script for the environment's shell, or an argv whose first
/// element is run directly with the rest as its arguments, without a shell, so they reach the program unparsed.</summary>
public readonly struct ShellCommand : IEquatable<ShellCommand>
{
    private ShellCommand(string? script, ImmutableArray<string> argv) { Script = script; Argv = argv; }

    /// <summary>A script for the environment's shell.</summary>
    public static ShellCommand FromScript(string script) => new(script ?? throw new ArgumentNullException(nameof(script)), default);

    /// <summary>A program and its arguments, run without a shell. An empty argv fails to spawn.</summary>
    public static ShellCommand FromArgv(IEnumerable<string> argv) =>
        new(null, (argv ?? throw new ArgumentNullException(nameof(argv))).Select(argument => argument ?? throw new ArgumentException("An argument is null.", nameof(argv))).ToImmutableArray());

    /// <summary>The script, or null for an argv command.</summary>
    public string? Script { get; }

    /// <summary>The argv, or default for a script.</summary>
    public ImmutableArray<string> Argv { get; }

    /// <summary>Whether this runs through the shell.</summary>
    public bool IsScript => Script is not null;

    /// <summary>A script.</summary>
    public static implicit operator ShellCommand(string script) => FromScript(script);

    /// <summary>An argv.</summary>
    public static implicit operator ShellCommand(string[] argv) => FromArgv(argv);

    /// <summary>An argv.</summary>
    public static implicit operator ShellCommand(ImmutableArray<string> argv) => FromArgv(argv);

    /// <inheritdoc/>
    public bool Equals(ShellCommand other) => Script == other.Script &&
        (Argv.IsDefault ? other.Argv.IsDefault : !other.Argv.IsDefault && Argv.SequenceEqual(other.Argv));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ShellCommand other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Script?.GetHashCode(StringComparison.Ordinal) ?? (Argv.IsDefault ? 0 : Argv.Length);

    /// <summary>Equality.</summary>
    public static bool operator ==(ShellCommand left, ShellCommand right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(ShellCommand left, ShellCommand right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Script ?? (Argv.IsDefault ? "" : string.Join(' ', Argv));
}

/// <summary>Spill the complete output to a temporary file once it exceeds either threshold (upstream <c>ShellSpillOptions</c>).</summary>
/// <param name="AfterBytes">Raw bytes of stdout and stderr together.</param>
/// <param name="AfterLines">Complete or partial lines.</param>
public sealed record ShellSpillOptions(long AfterBytes, long AfterLines);

/// <summary>A command that ran (upstream <c>ShellExecResult</c>); a non-zero exit is a result, not an error.</summary>
/// <param name="ExitCode">The exit code; a process killed by a signal reports 128 plus the signal number.</param>
/// <param name="SpillPath">Temporary file holding the complete raw output, when the spill thresholds were exceeded.</param>
public sealed record ShellExecResult(int ExitCode, string? SpillPath = null);

/// <summary>
/// The tail of the combined output a caller keeps, and how often it samples it (upstream <c>ShellOutputWindow</c>). An environment that
/// transfers output over a slow link uses it to omit what the caller would drop anyway and to send no faster than the caller commits.
/// </summary>
/// <param name="MaxBytes">UTF-8 bytes of decoded text kept at the end of the output.</param>
/// <param name="MaxLines">Lines kept at the end of the output.</param>
/// <param name="MinIntervalMs">Minimum pause between the caller's samples of the output.</param>
/// <param name="BytesPerSecond">Each sample also pauses the caller in proportion to its size at this rate.</param>
public sealed record ShellOutputWindow(long MaxBytes, long MaxLines, double MinIntervalMs, double BytesPerSecond);

/// <summary>Output an environment omitted, measured on the decoded text the callback would have received: every U+FFFD counts as three bytes,
/// and no sanitizing is applied (upstream <c>ShellOutputSkip</c>).</summary>
/// <param name="Bytes">UTF-8 byte length of the omitted text.</param>
/// <param name="Newlines">Newlines (U+000A) in the omitted text.</param>
/// <param name="EndsWithNewline">Whether the omitted text ends with a newline.</param>
public sealed record ShellOutputSkip(long Bytes, long Newlines, bool EndsWithNewline);

/// <summary>Which output stream a chunk came from.</summary>
public enum ShellStream
{
    /// <summary><c>stdout</c>.</summary>
    Stdout,
    /// <summary><c>stderr</c>.</summary>
    Stderr,
}

/// <summary>About one chunk of output (upstream <c>ShellOutputInfo</c>).</summary>
/// <param name="Stream">The stream the chunk came from.</param>
/// <param name="Skipped">Output omitted immediately before this chunk, only with <see cref="ShellExecOptions.Window"/>. The chunk then holds all
/// output after the omission up to its end, and that is more than the window by at least one byte or one line, so the omitted text can never
/// be in the kept tail. The omission and such a chunk may span both streams in arrival order; <paramref name="Stream"/> then names the
/// chunk's last stream. Callers that need the streams apart do not pass a window.</param>
public sealed record ShellOutputInfo(ShellStream Stream, ShellOutputSkip? Skipped = null);

/// <summary>Receives every decoded chunk of stdout and stderr as it arrives (upstream <c>ShellExecOptions.onOutput</c>). An exception it
/// throws stops the command with <see cref="ExecutionErrorCode.CallbackError"/>.</summary>
public delegate void ShellOutputHandler(string text, DurableContext context, ShellOutputInfo info);

/// <summary>Options of <see cref="IShell.ExecAsync"/> (upstream <c>ShellExecOptions</c>).</summary>
public sealed record ShellExecOptions
{
    /// <summary>The working directory; relative paths resolve against the environment's working directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>Variables set for the command; an empty value sets the variable to the empty string.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>Start from the environment's own variables (default). False: the command sees only <see cref="Env"/>.</summary>
    public bool InheritEnv { get; init; } = true;

    /// <summary>Seconds after which the command is killed and fails with <see cref="ExecutionErrorCode.Timeout"/>.</summary>
    public double? Timeout { get; init; }

    /// <summary>Every decoded chunk of stdout and stderr as it arrives, in arrival order, with the stream it came from: raw, unbounded and
    /// unthrottled. Each stream is decoded separately, so a character split across chunks survives.</summary>
    public ShellOutputHandler? OnOutput { get; init; }

    /// <summary>Spill the complete raw output to a temporary file once it crosses these thresholds.</summary>
    public ShellSpillOptions? Spill { get; init; }

    /// <summary>The caller keeps only this tail of the output, so the environment may omit output outside it and report the omission as
    /// <see cref="ShellOutputInfo.Skipped"/>. Without it, every chunk is delivered.</summary>
    public ShellOutputWindow? Window { get; init; }
}

/// <summary>Runs commands (upstream <c>Shell</c>).</summary>
public interface IShell
{
    /// <summary>Runs a command. A script runs through the environment's shell; an argv runs its program directly. Cancelling the context
    /// or a timeout kills only this command's processes.</summary>
    Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context);

    /// <summary>Kills every command this environment still runs; for its owner's shutdown, never for one request.</summary>
    Task CleanupAsync(DurableContext context);
}

/// <summary>
/// The environment tools and prompt sections run in (upstream <c>ExecutionEnv</c>, <c>FileSystem &amp; Shell</c>). The Harness builds one per
/// use through <c>HarnessOptions.Env</c> and hands it to task runtimes.
/// </summary>
public interface IExecutionEnv : IFileSystem, IShell
{
    /// <summary>Kills every command the environment still runs and releases what its file system holds (upstream <c>cleanup</c>, which both
    /// halves declare); for its owner's shutdown.</summary>
    new Task CleanupAsync(DurableContext context);
}
