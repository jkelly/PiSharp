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
        var file = ShellSpawnPreflight.Check(_shell, command, workingDirectory, _environment);
        var request = new ProcessRequest(file, _shell.CommandArguments(command), workingDirectory, _environment,
            Path.Combine(_scratchDirectory, "pi-bash-discarded-" + Guid.NewGuid().ToString("N") + ".log"))
        { StandardInput = _shell.CommandTransport == ShellCommandTransport.Stdin ? Encoding.UTF8.GetBytes(command) : null };
        var result = await _runner.RunStreamingAsync(request, onData, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ProcessRunStatus.Exited or ProcessRunStatus.NonZeroExit => result.ExitCode,
            ProcessRunStatus.Canceled => throw new OperationCanceledException(cancellationToken),
            // CreateProcess refused the shell: Node's spawn error for libuv's code (an access-denied shell is "spawn EPERM").
            _ when result.LaunchError is { } error => throw ShellSpawnPreflight.WindowsLaunchFailure(_shell.Shell, error),
            _ => throw new IOException("Shell command failed: " + string.Join(", ", result.Diagnostics))
        };
    }

    private sealed class DiscardedOutputStorage : IProcessOutputStorage
    {
        public ValueTask<Stream> CreateNewAsync(string absolutePath) => ValueTask.FromResult(Stream.Null);
    }
}

/// <summary>Source createLocalShellOperations exec, before anything is spawned, on every platform: the working-directory check
/// (<c>fs.access(cwd, F_OK)</c>, which a file passes), then child_process.spawn's argument validation (a NUL byte in the file, args, cwd
/// or env is ERR_INVALID_ARG_VALUE) and the operating system's command-line limit, then what libuv's uv_spawn refuses, with Node's spawn
/// error text (<see cref="NodeArgumentErrors.SpawnFailure"/>): on Windows libuv's executable search (a directory or a missing file is
/// <c>spawn &lt;shell&gt; ENOENT</c>, and so is a working directory that is a file); on Linux and macOS the child's <c>chdir</c> (a file is
/// <c>spawn ENOTDIR</c>) and then <c>execve</c> (missing ENOENT, not executable or a directory EACCES, neither a binary nor a script
/// ENOEXEC). Executors report these messages as the command's failure. A NUL never reaches posix_spawn or CreateProcess, which would cut
/// the string there and run the truncated command. Returns the file to launch (libuv's search result on Windows).</summary>
internal static class ShellSpawnPreflight
{
    internal static string Check(ShellConfiguration shell, string command, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        if (!PathExists(workingDirectory))
            throw new PiSharp.Agent.ToolSourceErrorException($"Working directory does not exist: {workingDirectory}\nCannot execute bash commands.");
        var arguments = shell.CommandArguments(command);
        // With commandTransport "stdin" the command goes to standard input, which Node does not check; only the spawn strings are.
        if ((NodeArgumentErrors.SpawnNullBytes(shell.Shell, arguments, workingDirectory, environment) ??
            NodeArgumentErrors.SpawnLimit(shell.Shell, arguments)) is { } spawnError)
            throw new PiSharp.Agent.ToolSourceErrorException(spawnError);
        if (OperatingSystem.IsWindows())
        {
            // CreateProcess refuses a working directory that is a file with ERROR_DIRECTORY, which libuv reads as ENOENT.
            var file = WindowsSearch(shell.Shell);
            return file is not null && Directory.Exists(workingDirectory) ? file : throw Failure(shell.Shell, "ENOENT");
        }
        if (UnixSpawnError(shell.Shell, workingDirectory) is { } code) throw Failure(shell.Shell, code);
        return shell.Shell;
    }

    /// <summary>Node's spawn failure for a CreateProcess error (libuv's translation of the Win32 code).</summary>
    internal static PiSharp.Agent.ToolSourceErrorException WindowsLaunchFailure(string shell, int win32Error) =>
        Failure(shell, NodeArgumentErrors.WindowsErrorCode(win32Error));

    private static PiSharp.Agent.ToolSourceErrorException Failure(string shell, string code) => new(NodeArgumentErrors.SpawnFailure(shell, code));

    private static bool PathExists(string path) => OperatingSystem.IsWindows() ? Path.Exists(path) : UnixNative.access(path, UnixNative.F_OK) == 0;

    /// <summary>libuv's search_path for a path with a directory (src/win/process.c): a name with an extension is tried as is, then with
    /// <c>.com</c> and <c>.exe</c> appended; a name without one only with the appended extensions. Directories never match.</summary>
    internal static string? WindowsSearch(string file)
    {
        string[] candidates = Path.GetExtension(file).Length > 1 ? [file, file + ".com", file + ".exe"] : [file + ".com", file + ".exe"];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The errno libuv's child reports for <c>chdir(cwd)</c> and then <c>execve(shell)</c>, as a libuv code, or null when both
    /// succeed. A directory is EACCES (execve); a readable regular file is ENOEXEC unless it starts like an ELF, Mach-O or PE image or a
    /// <c>#!</c> script.</summary>
    private static string? UnixSpawnError(string shell, string workingDirectory)
    {
        var macOS = OperatingSystem.IsMacOS();
        if (!Directory.Exists(workingDirectory)) return "ENOTDIR";
        if (UnixNative.access(workingDirectory, UnixNative.X_OK) != 0) return Errno();
        if (UnixNative.access(shell, UnixNative.X_OK) != 0) return Errno();
        if (Directory.Exists(shell)) return "EACCES";
        // Linux libuv forks and calls glibc's execvp, which runs a file the kernel refuses (ENOEXEC) as a /bin/sh script, as the
        // admission's exec does; macOS libuv uses posix_spawn, which reports ENOEXEC.
        if (!macOS) return null;
        try
        {
            using var stream = new FileStream(shell, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Span<byte> header = stackalloc byte[4];
            var read = stream.ReadAtLeast(header, 4, throwOnEndOfStream: false);
            header = header[..read];
            if (header.StartsWith("#!"u8) || header.StartsWith("MZ"u8) || header.SequenceEqual("\u007fELF"u8)) return null;
            if (read == 4 && BitConverter.ToUInt32(header) is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xBEBAFECA or 0xCAFEBABE) return null;
            return "ENOEXEC";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return null; } // Execute-only files still run.
        string Errno() => NodeArgumentErrors.UnixErrorCode(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), macOS);
    }

    private static class UnixNative
    {
        internal const int F_OK = 0, X_OK = 1;
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern int access([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, int mode);
    }
}
