// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bash-executor.ts.
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Agent.Tools;

namespace PiSharp.Tools.Processes;

/// <summary>Source BashOperations.exec: runs one command, delivering raw output in arrival order. A null exit code
/// means the process was killed. Throwing after cancellation is allowed; the executor keeps the output it received.</summary>
public interface IShellOperations
{
    ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData,
        CancellationToken cancellationToken);
}

/// <summary>Source BashResult. <see cref="ExitCode"/> is null when the command was cancelled or killed.</summary>
public sealed record ShellCommandResult(string Output, int? ExitCode, bool Cancelled, bool Truncated, string? FullOutputPath);

/// <summary>Sanitized streamed text (source onChunk), awaited before the next raw chunk is processed.</summary>
public delegate ValueTask ShellTextCallback(string chunk);

/// <summary>
/// Source executeBashWithOperations: ANSI-stripped, binary-sanitized, CR-free streaming with split escape sequences held
/// back (<see cref="ShellTextStream"/>), a rolling buffer of twice the 50KB limit, a spill file once more than 50KB of raw
/// output arrived or the final tail is truncated, and the final tail truncated to 2000 lines or 50KB.
/// </summary>
public sealed class ShellCommandExecutor
{
    private const int MaximumRetainedCharacters = ToolOutputTruncator.DefaultMaxBytes * 2;
    private readonly IShellOperations _operations;
    private readonly string _spillDirectory;
    private readonly IProcessOutputStorage _storage;
    private readonly Func<string> _nextSpillFileName;

    public ShellCommandExecutor(IShellOperations operations, string spillDirectory, IProcessOutputStorage? storage = null,
        Func<string>? nextSpillFileName = null)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (string.IsNullOrEmpty(spillDirectory) || !Path.IsPathFullyQualified(spillDirectory) || Path.GetFullPath(spillDirectory) != spillDirectory)
            throw new ArgumentException("The spill directory must be a normalized absolute path.", nameof(spillDirectory));
        _operations = operations; _spillDirectory = spillDirectory; _storage = storage ?? new LocalProcessOutputStorage();
        // Source output-files.ts: <tmp>/pi-bash-<16 random hex>.log.
        _nextSpillFileName = nextSpillFileName ?? (() => "pi-bash-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".log");
    }

    /// <summary>The same spill storage and file naming over other operations (a user_bash handler's custom operations).</summary>
    public ShellCommandExecutor WithOperations(IShellOperations operations) => new(operations, _spillDirectory, _storage, _nextSpillFileName);

    public async Task<ShellCommandResult> ExecuteAsync(string command, string workingDirectory, ShellTextCallback? onChunk,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command); ArgumentNullException.ThrowIfNull(workingDirectory);
        var chunks = new LinkedList<string>(); var retained = 0; long totalBytes = 0;
        var stream = new ShellTextStream(); Stream? spill = null; string? spillPath = null;
        async ValueTask EnsureSpillAsync()
        {
            if (spillPath is not null) return;
            var name = _nextSpillFileName();
            if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name) throw new InvalidOperationException("Invalid spill file name.");
            var path = Path.Combine(_spillDirectory, name);
            spill = await _storage.CreateNewAsync(path).ConfigureAwait(false) ?? throw new IOException("Output storage returned no stream.");
            spillPath = path;
            foreach (var chunk in chunks) await WriteSpillAsync(chunk).ConfigureAwait(false);
        }
        ValueTask WriteSpillAsync(string text) => spill!.WriteAsync(Encoding.UTF8.GetBytes(text), CancellationToken.None);
        async ValueTask AppendAsync(string text)
        {
            if (text.Length == 0) return;
            if (totalBytes > ToolOutputTruncator.DefaultMaxBytes) await EnsureSpillAsync().ConfigureAwait(false);
            if (spill is not null) await WriteSpillAsync(text).ConfigureAwait(false);
            chunks.AddLast(text); retained += text.Length;
            while (retained > MaximumRetainedCharacters && chunks.Count > 1) { retained -= chunks.First!.Value.Length; chunks.RemoveFirst(); }
            if (onChunk is not null) await onChunk(text).ConfigureAwait(false);
        }
        try
        {
            int? exitCode = null;
            try
            {
                exitCode = await _operations.ExecuteAsync(command, workingDirectory, async data =>
                {
                    totalBytes += data.Length;
                    await AppendAsync(Clean(stream.Append(data.Span))).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            // An aborted command still returns the output it produced so far.
            catch (Exception) when (cancellationToken.IsCancellationRequested) { }
            await AppendAsync(Clean(stream.Flush())).ConfigureAwait(false);
            var full = string.Concat(chunks);
            var tail = ToolOutputTruncator.Tail(full);
            if (tail.Truncated) await EnsureSpillAsync().ConfigureAwait(false);
            if (spill is not null) await spill.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            var cancelled = cancellationToken.IsCancellationRequested;
            return new(tail.Truncated ? tail.Content : full, cancelled ? null : exitCode, cancelled, tail.Truncated, spillPath);
        }
        finally { if (spill is not null) await spill.DisposeAsync().ConfigureAwait(false); }
    }

    // A lone C1 CSI introducer that never formed a sequence is not admitted display text in the native session profile.
    private static string Clean(string text) => text.Contains('\u009B') ? text.Replace("\u009B", "", StringComparison.Ordinal) : text;
}

/// <summary>Local source createLocalBashOperations over the bounded Windows process primitive: <c>shell -c command</c>
/// (or the command over standard input for legacy WSL bash) with the host's complete environment. The runner's own
/// spill copy is discarded; the executor owns user-bash spill.</summary>
public sealed class NativeShellOperations : IShellOperations
{
    private readonly NativeProcessRunner _runner;
    private readonly ShellConfiguration _shell;
    private readonly string _scratchDirectory;
    private readonly ImmutableDictionary<string, string> _environment;

    public NativeShellOperations(string shell, ImmutableDictionary<string, string> environment, string scratchDirectory,
        ProcessRunnerOptions? options = null) : this(new ShellConfiguration(shell ?? throw new ArgumentNullException(nameof(shell)), ["-c"]),
            environment, scratchDirectory, options) { }

    public NativeShellOperations(ShellConfiguration shell, ImmutableDictionary<string, string> environment, string scratchDirectory,
        ProcessRunnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(shell); ArgumentNullException.ThrowIfNull(environment); ArgumentNullException.ThrowIfNull(scratchDirectory);
        _shell = shell; _environment = environment; _scratchDirectory = scratchDirectory;
        _runner = new NativeProcessRunner(options, outputStorage: new DiscardedOutputStorage());
    }

    public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData,
        CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(_shell.Shell, _shell.CommandArguments(command), workingDirectory, _environment,
            Path.Combine(_scratchDirectory, "pi-bash-discarded-" + Guid.NewGuid().ToString("N") + ".log"))
        { StandardInput = _shell.CommandTransport == ShellCommandTransport.Stdin ? Encoding.UTF8.GetBytes(command) : null };
        var result = await _runner.RunStreamingAsync(request, onData, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ProcessRunStatus.Exited or ProcessRunStatus.NonZeroExit => result.ExitCode,
            ProcessRunStatus.Canceled => throw new OperationCanceledException(cancellationToken),
            _ => throw new IOException("Shell command failed: " + string.Join(", ", result.Diagnostics))
        };
    }

    private sealed class DiscardedOutputStorage : IProcessOutputStorage
    {
        public ValueTask<Stream> CreateNewAsync(string absolutePath) => ValueTask.FromResult(Stream.Null);
    }
}
