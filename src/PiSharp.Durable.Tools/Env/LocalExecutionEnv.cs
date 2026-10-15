// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/node.ts (NodeExecutionEnv: the Shell half, exec with
// streamed output, spill, cancellation, timeout and cleanup; the FileSystem half is LocalFileSystem, which this class extends).
// Intentional differences, each following PiSharp's coding-agent bash tool:
// - Shell discovery is PiSharp.Tools' ShellDiscovery (upstream getShellConfig): the configured shell path, then on Windows Git Bash under
//   ProgramFiles and ProgramFiles(x86) and bash.exe on PATH, on Unix /bin/bash, bash on PATH, then sh. Unlike upstream's `where bash.exe`,
//   the working directory is not searched, so a workspace file cannot shadow the shell; there is no PowerShell or cmd fallback, as upstream
//   has none ("No bash shell found" is shell_unavailable, with upstream's text).
// - A command's processes live in a private job (Windows) or process group (Linux, macOS), see LocalProcess. Abort, timeout and cleanup
//   kill the whole job or group; so does settling, which kills descendants the command left running in the background (upstream leaves
//   them; PiSharp's bash tool kills them too). Descendants that hold stdout or stderr open past the command's exit are read until the
//   streams have been idle for 100 ms, as upstream does, before they are killed.
// - A process killed through its job on Windows exits with code 1 (taskkill's code upstream); exit codes are 32-bit integers.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Tools.Processes;

namespace PiSharp.Durable.Tools.Env;

/// <summary>Options of <see cref="LocalExecutionEnv"/> (upstream <c>NodeExecutionEnv</c>'s constructor options).</summary>
public sealed record LocalExecutionEnvOptions
{
    /// <summary>The working directory relative paths and commands start in.</summary>
    public required string Cwd { get; init; }

    /// <summary>A bash to run string commands with, instead of discovering one.</summary>
    public string? ShellPath { get; init; }

    /// <summary>Variables every command sees on top of this process's environment (unless <see cref="ShellExecOptions.InheritEnv"/> is
    /// false), below the command's own <see cref="ShellExecOptions.Env"/>.</summary>
    public IReadOnlyDictionary<string, string>? ShellEnv { get; init; }

    /// <summary>How <see cref="IFileSystem.WatchAsync"/> watches.</summary>
    public LocalWatchOptions? Watch { get; init; }
}

/// <summary>The local execution environment: the local file system plus commands run through a local shell (upstream
/// <c>NodeExecutionEnv</c>).</summary>
public class LocalExecutionEnv : LocalFileSystem, IExecutionEnv
{
    private const double MaxTimeoutMs = 2_147_483_647;
    private const double MaxTimeoutSeconds = MaxTimeoutMs / 1000;
    private const int ExitStdioGraceMs = 100;
    private const int ReadChunk = 64 * 1024;
    private const int SpillBufferBytes = 1024 * 1024;
    private readonly string? shellPath;
    private readonly IReadOnlyDictionary<string, string>? shellEnv;
    private readonly ConcurrentDictionary<LocalProcess, byte> active = new();

    /// <summary>An environment in <paramref name="cwd"/> with a discovered shell.</summary>
    public LocalExecutionEnv(string cwd) : this(new LocalExecutionEnvOptions { Cwd = cwd }) { }

    /// <summary>An environment with the given options.</summary>
    public LocalExecutionEnv(LocalExecutionEnvOptions options) : base((options ?? throw new ArgumentNullException(nameof(options))).Cwd, options.Watch)
    {
        shellPath = options.ShellPath;
        shellEnv = options.ShellEnv;
    }

    /// <inheritdoc/>
    public virtual async Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsCancellationRequested) return new ExecutionError(ExecutionErrorCode.Aborted, "aborted");
        if (options?.Timeout is { } timeout)
        {
            if (!double.IsFinite(timeout) || timeout <= 0)
                return new ExecutionError(ExecutionErrorCode.Timeout, "Invalid timeout: must be a finite number of seconds");
            if (timeout * 1000 > MaxTimeoutMs)
                return new ExecutionError(ExecutionErrorCode.Timeout, $"Invalid timeout: maximum is {JsNumber(MaxTimeoutSeconds)} seconds");
        }
        string cwd;
        try { cwd = string.IsNullOrEmpty(options?.Cwd) ? Cwd : ResolvePath(options.Cwd); }
        catch (Exception error) { return new ExecutionError(ExecutionErrorCode.SpawnError, error.Message, error); }

        // A string runs through the shell; an argv runs its program directly, without shell parsing.
        string program;
        ImmutableArray<string> arguments;
        byte[]? standardInput = null;
        if (command.IsScript)
        {
            ShellConfiguration shell;
            try { shell = ShellDiscovery.Resolve(shellPath); }
            catch (ShellDiscoveryException error)
            {
                return new ExecutionError(ExecutionErrorCode.ShellUnavailable,
                    error.Message.Replace("  3. Set shellPath in settings.json", "  3. Configure an explicit shellPath", StringComparison.Ordinal), error);
            }
            program = shell.Shell;
            arguments = shell.CommandArguments(command.Script!);
            if (shell.CommandTransport == ShellCommandTransport.Stdin) standardInput = Encoding.UTF8.GetBytes(command.Script!);
        }
        else
        {
            if (command.Argv.IsDefaultOrEmpty) return new ExecutionError(ExecutionErrorCode.SpawnError, "Empty argv: no program to run");
            program = command.Argv[0];
            arguments = command.Argv.RemoveAt(0);
        }
        if (!Path.Exists(cwd))
            return new ExecutionError(ExecutionErrorCode.SpawnError, $"Working directory does not exist: {cwd}\nCannot execute bash commands.");

        var environment = Environment(options);
        LocalProcess process;
        try
        {
            var file = LocalProcess.Resolve(program, arguments, cwd, environment);
            process = await LocalProcess.StartAsync(file, arguments, cwd, environment, standardInput).ConfigureAwait(false);
        }
        catch (LocalSpawnException error) { return new ExecutionError(ExecutionErrorCode.SpawnError, error.Message, error); }
        active.TryAdd(process, 0);
        try { return await new Execution(this, process, options, context).RunAsync().ConfigureAwait(false); }
        finally
        {
            active.TryRemove(process, out _);
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>Kills the process tree of every command still running; their <see cref="ExecAsync"/> calls then settle.</remarks>
    public override async Task CleanupAsync(DurableContext context)
    {
        var running = active.Keys.ToList();
        active.Clear();
        foreach (var process in running)
        {
            try { await process.KillAsync().ConfigureAwait(false); }
            catch (Exception) { } // Best effort.
        }
        await base.CleanupAsync(context).ConfigureAwait(false);
    }

    /// <summary>Opens the spill file a command's complete output is appended to (upstream <c>createWriteStream(spillPath, { flags: "a" })</c>);
    /// a seam for tests that slow it down.</summary>
    protected virtual Stream OpenSpillStream(string path) =>
        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, SpillBufferBytes, FileOptions.Asynchronous);

    /// <summary>Upstream <c>getShellEnv</c>: this process's variables, then the environment's, then the command's; or the command's alone.</summary>
    private ImmutableDictionary<string, string> Environment(ShellExecOptions? options)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);
        if (options?.InheritEnv != false)
        {
            foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) environment[key] = value;
            if (shellEnv is not null) foreach (var (key, value) in shellEnv) environment[key] = value;
        }
        if (options?.Env is { } overrides) foreach (var (key, value) in overrides) environment[key] = value;
        return environment.ToImmutableDictionary(comparer);
    }

    /// <summary>A JavaScript number's text for the values these messages use.</summary>
    private static string JsNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>One running command: its output pumps, spill and settlement (upstream exec's promise body and waitForChildProcess).</summary>
    private sealed class Execution(LocalExecutionEnv env, LocalProcess process, ShellExecOptions? options, DurableContext context)
    {
        private readonly SemaphoreSlim turn = new(1, 1);
        // One decoder per stream, so a character split across chunks of one stream survives interleaving.
        private readonly StreamDecoder stdoutDecoder = new();
        private readonly StreamDecoder stderrDecoder = new();
        private readonly List<byte[]> spillPrefix = [];
        private volatile bool settled, finalized, timedOut;
        private ExecutionError? callbackError, spillError;
        private bool spillStarted;
        private volatile bool spillBusy;
        private string? spillPath;
        private Stream? spillStream;
        // Output seen before the spill starts: counted against the thresholds and kept for the spill's prefix.
        private long seenBytes, seenNewlines;
        private long lastActivity = System.Environment.TickCount64;
        /// <summary>Pumps still reading, and how many of them are parked in a read: output is idle only while every open pump waits for
        /// more, so output already in a pipe is never abandoned to a pump that has not been scheduled yet.</summary>
        private int pumpsOpen = 2, pumpsInRead;

        public async Task<Result<ShellExecResult, ExecutionError>> RunAsync()
        {
            using var timer = options?.Timeout is { } seconds
                ? new Timer(_ => { timedOut = true; Kill(); }, null, TimeSpan.FromMilliseconds(seconds * 1000), Timeout.InfiniteTimeSpan)
                : null;
            using var registration = context.CancellationToken.Register(Kill);
            using var reads = new CancellationTokenSource();
            var stdout = Pump(process.StandardOutput, ShellStream.Stdout, reads.Token);
            var stderr = Pump(process.StandardError, ShellStream.Stderr, reads.Token);
            var pumps = Task.WhenAll(stdout, stderr);
            var exit = await process.Exit.ConfigureAwait(false);
            await WaitForStdioAsync(pumps).ConfigureAwait(false);

            // No output reaches the caller after exec settled, for example from a descendant holding stdio open.
            await turn.WaitAsync().ConfigureAwait(false);
            finalized = true;
            turn.Release();
            await FinishSpillAsync().ConfigureAwait(false);
            Emit(stdoutDecoder.Finish(), ShellStream.Stdout);
            Emit(stderrDecoder.Finish(), ShellStream.Stderr);
            var result = Result();
            settled = true;
            // The streams end once the job or group is gone; a descendant that escaped it can hold them, so stop waiting after a while.
            await process.KillAsync().ConfigureAwait(false);
            await reads.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(pumps, Task.Delay(1000)).ConfigureAwait(false);
            return result;

            Result<ShellExecResult, ExecutionError> Result()
            {
                if (callbackError is not null) return callbackError;
                var interrupted = timedOut ? new ExecutionError(ExecutionErrorCode.Timeout, $"timeout:{JsNumber(options!.Timeout!.Value)}")
                    : context.IsCancellationRequested ? new ExecutionError(ExecutionErrorCode.Aborted, "aborted")
                    : null;
                if (interrupted is not null)
                {
                    if (spillPath is not null) interrupted.SpillPath = spillPath;
                    return interrupted;
                }
                if (spillError is not null) return spillError;
                return new ShellExecResult(exit ?? 1, spillPath);
            }
        }

        /// <summary>After the exit: until both streams end, or until they have been idle for the grace period while no spill write is
        /// pending (a descendant may hold them open indefinitely).</summary>
        private async Task WaitForStdioAsync(Task pumps)
        {
            Interlocked.Exchange(ref lastActivity, System.Environment.TickCount64);
            while (!pumps.IsCompleted)
            {
                var draining = spillError is null && spillStarted && (spillStream is null || spillBusy);
                var waiting = Volatile.Read(ref pumpsInRead) >= Volatile.Read(ref pumpsOpen);
                if (draining || turn.CurrentCount == 0 || !waiting) Interlocked.Exchange(ref lastActivity, System.Environment.TickCount64);
                var remaining = ExitStdioGraceMs - (System.Environment.TickCount64 - Interlocked.Read(ref lastActivity));
                if (remaining <= 0) return;
                await Task.WhenAny(pumps, Task.Delay((int)Math.Min(remaining, ExitStdioGraceMs))).ConfigureAwait(false);
            }
        }

        /// <summary>Reads one output stream until it ends. Windows pipes are overlapped and read asynchronously. POSIX pipes are blocking
        /// descriptors whose asynchronous reads are blocking reads queued to the thread pool, which can start long after the process exited
        /// when the pool is busy, so each gets a thread of its own; a descendant that escaped the process group can hold such a read until it
        /// exits, and only that thread waits for it.</summary>
        private Task Pump(Stream stream, ShellStream source, CancellationToken cancellation)
        {
            if (OperatingSystem.IsWindows()) return Task.Run(() => PumpAsync(stream, source, cancellation));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { PumpBlocking(stream, source); }
                finally { done.TrySetResult(); }
            }) { IsBackground = true, Name = "durable exec " + source.ToString().ToLowerInvariant() };
            thread.Start();
            return done.Task;
        }

        private async Task PumpAsync(Stream stream, ShellStream source, CancellationToken cancellation)
        {
            var buffer = new byte[ReadChunk];
            try
            {
                while (!finalized)
                {
                    int read;
                    Interlocked.Increment(ref pumpsInRead);
                    try { read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref pumpsInRead); }
                    if (read == 0) return;
                    await HandleAsync(buffer.AsSpan(0, read).ToArray(), source).ConfigureAwait(false);
                }
            }
            catch (Exception) { } // A stream closed or cancelled after settlement, or a broken pipe: the stream ended.
            finally { Interlocked.Decrement(ref pumpsOpen); }
        }

        private void PumpBlocking(Stream stream, ShellStream source)
        {
            var buffer = new byte[ReadChunk];
            try
            {
                while (!finalized)
                {
                    int read;
                    Interlocked.Increment(ref pumpsInRead);
                    try { read = stream.Read(buffer); }
                    finally { Interlocked.Decrement(ref pumpsInRead); }
                    if (read == 0) return;
                    HandleAsync(buffer.AsSpan(0, read).ToArray(), source).GetAwaiter().GetResult();
                }
            }
            catch (Exception) { } // A stream closed after settlement, or a broken pipe: the stream ended.
            finally { Interlocked.Decrement(ref pumpsOpen); }
        }

        private void Kill()
        {
            _ = process.KillAsync().ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
        }

        private void Emit(string text, ShellStream source)
        {
            if (settled || text.Length == 0 || options?.OnOutput is not { } onOutput || callbackError is not null) return;
            try { onOutput(text, context, new ShellOutputInfo(source)); }
            catch (Exception error)
            {
                callbackError ??= new ExecutionError(ExecutionErrorCode.CallbackError, error.Message, error);
                Kill();
            }
        }

        private async Task HandleAsync(byte[] chunk, ShellStream source)
        {
            await turn.WaitAsync().ConfigureAwait(false);
            try
            {
                if (finalized) return;
                Emit((source == ShellStream.Stdout ? stdoutDecoder : stderrDecoder).Decode(chunk), source);
                if (options?.Spill is not { } spill || chunk.Length == 0) return;
                if (spillStarted)
                {
                    await WriteSpillAsync(chunk).ConfigureAwait(false);
                    return;
                }
                seenBytes += chunk.Length;
                seenNewlines += chunk.AsSpan().Count((byte)0x0a);
                var lines = seenNewlines + (chunk[^1] == 0x0a ? 0 : 1);
                if (seenBytes <= spill.AfterBytes && lines <= spill.AfterLines)
                {
                    spillPrefix.Add(chunk);
                    return;
                }
                spillPrefix.Add(chunk);
                await StartSpillAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref lastActivity, System.Environment.TickCount64);
                turn.Release();
            }
        }

        private async Task StartSpillAsync()
        {
            spillStarted = true;
            spillBusy = true;
            try
            {
                var created = await env.CreateTempFileAsync(new TempFileOptions { Prefix = "pi-output-", Suffix = ".log" }, context).ConfigureAwait(false);
                if (!created.IsOk) throw created.Error;
                spillPath = created.Value;
                spillStream = env.OpenSpillStream(spillPath);
                foreach (var queued in spillPrefix) await spillStream.WriteAsync(queued).ConfigureAwait(false);
                spillPrefix.Clear();
            }
            catch (Exception error) { FailSpill(error); }
            finally { spillBusy = false; }
        }

        private async Task WriteSpillAsync(byte[] chunk)
        {
            if (spillStream is null || spillError is not null) return;
            spillBusy = true;
            try { await spillStream.WriteAsync(chunk).ConfigureAwait(false); }
            catch (Exception error) { FailSpill(error); }
            finally { spillBusy = false; }
        }

        private void FailSpill(Exception error)
        {
            if (spillError is not null) return;
            spillError = new ExecutionError(ExecutionErrorCode.Unknown, $"Failed to preserve complete shell output: {error.Message}", error);
            Kill();
        }

        private async Task FinishSpillAsync()
        {
            if (spillStream is not { } stream) return;
            try
            {
                if (spillError is null) await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception) { } // Upstream resolves its finish on a stream error too.
            finally
            {
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
    }
}
