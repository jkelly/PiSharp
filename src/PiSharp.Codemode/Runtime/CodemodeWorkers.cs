// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/runtime/host.ts (Execution.start, worker.terminate) and
// packages/codemode/src/runtime/worker.ts (the worker entry). Pi isolates each script in a Node worker running a separate wasm
// instance; PiSharp runs each script's Jint engine in a child process of its own, so nothing a script does to the engine's
// stack or memory can take the host process down.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace PiSharp.Codemode;

/// <summary>How to start a codemode worker process: an executable (and arguments) whose entry point hands
/// <see cref="CodemodeWorker.Argument"/> to <see cref="CodemodeWorker.RunAsync"/>.</summary>
public sealed record CodemodeWorkerLauncher(string FileName, ImmutableArray<string> Arguments)
{
    /// <summary>Variables added to the worker's inherited environment, for example <see cref="CodemodeWorker.StackVariable"/>.</summary>
    public ImmutableDictionary<string, string>? Environment { get; init; }

    /// <summary>The current process's own executable: the apphost or single-file executable, or <c>dotnet &lt;entry assembly&gt;</c>.
    /// Null when the process has no launchable entry.</summary>
    public static CodemodeWorkerLauncher? ForCurrentProcess()
    {
        var path = System.Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Assembly.GetEntryAssembly()?.Location;
            return string.IsNullOrEmpty(entry) ? null : new(path, [entry, CodemodeWorker.Argument]);
        }
        return new(path, [CodemodeWorker.Argument]);
    }
}

/// <summary>The worker process side and the default launcher.</summary>
public static class CodemodeWorker
{
    /// <summary>The single command-line argument of a worker process.</summary>
    public const string Argument = "--codemode-worker";
    /// <summary>Launcher used by sandboxes that do not name one. Null runs engines in-process on their own threads, which a
    /// script nesting values hundreds of thousands of levels deep can crash; hosts set this at startup.</summary>
    public static CodemodeWorkerLauncher? Default { get; set; }

    /// <summary>Worker entry: warms the engine, reads one start message, runs the script on a large-stack thread and relays
    /// messages until the host closes standard input or kills the process. Writes nothing else to <paramref name="output"/>.</summary>
    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(output);
        Warm();
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        if (line is null) return 0;
        var start = CodemodeWireCodec.DecodeStart(line);
        var gate = new object();
        void Post(CodemodeWorkerMessage message)
        {
            var encoded = CodemodeWireCodec.Encode(message);
            lock (gate) { writer.WriteLine(encoded); writer.Flush(); }
        }
        var runner = new CodemodeEngineRunner(start, Post);
        var thread = new Thread(() =>
        {
            try { runner.Run(CancellationToken.None); }
            catch (Exception error) { try { Post(new CodemodeCrashMessage($"{error.GetType().Name}: {error.Message}")); } catch (Exception) { } }
        }, StackBytes) { IsBackground = true, Name = "codemode" };
        thread.Start();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } next) runner.Settle(CodemodeWireCodec.DecodeResult(next));
        runner.Complete();
        return 0;
    }

    /// <summary>Optional worker setting: the engine thread's stack size in bytes (default 256 MiB; 64 MiB in 32-bit processes).</summary>
    public const string StackVariable = "PISHARP_CODEMODE_WORKER_STACK_BYTES";

    internal static int StackBytes => int.TryParse(Environment.GetEnvironmentVariable(StackVariable), out var bytes) && bytes >= 1024 * 1024
        ? bytes : Environment.Is64BitProcess ? 256 * 1024 * 1024 : 64 * 1024 * 1024;

    /// <summary>Runs a trivial script once, so the spare worker has compiled the engine before the host hands it a script.</summary>
    private static void Warm()
    {
        var thread = new Thread(() =>
        {
            try
            {
                CodemodeEngineRunner? runner = null;
                runner = new(new("text(JSON.stringify([[1]].join()))", "[]", "[]", "{}", CodemodeLimits.DefaultMemoryLimitBytes,
                    CodemodeLimits.DefaultTotalAllocationLimitBytes, CodemodeLimits.DefaultRecursionLimit),
                    message => { if (message is CodemodeDoneMessage or CodemodeCrashMessage) runner!.Complete(); });
                runner.Run(CancellationToken.None);
            }
            catch (Exception) { }
        }, StackBytes) { IsBackground = true };
        thread.Start(); thread.Join();
    }
}

/// <summary>One script's engine, wherever it runs. Messages are delivered in order on one thread at a time.</summary>
internal interface ICodemodeWorker
{
    /// <summary><paramref name="exited"/> reports a worker that ended before the host terminated it, with its exit code.</summary>
    void Start(CodemodeStartMessage start, Action<CodemodeWorkerMessage> received, Action<int?> exited, Action<Exception> broken);
    void Send(CodemodeResultMessage result);
    /// <summary>worker.terminate(): stops the engine and resolves once it is gone.</summary>
    Task TerminateAsync();
}

/// <summary>The engine on a dedicated thread of the host process.</summary>
internal sealed class CodemodeThreadWorker : ICodemodeWorker
{
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CodemodeEngineRunner? runner;

    public void Start(CodemodeStartMessage start, Action<CodemodeWorkerMessage> received, Action<int?> exited, Action<Exception> broken)
    {
        runner = new(start, received);
        var thread = new Thread(() =>
        {
            try { runner.Run(stop.Token); }
            catch (Exception error) { broken(error); }
            finally { ended.TrySetResult(); }
        }, Environment.Is64BitProcess ? 1024 * 1024 * 1024 : 64 * 1024 * 1024) { IsBackground = true, Name = "codemode" };
        thread.Start();
    }

    public void Send(CodemodeResultMessage result) => runner?.Settle(result);

    public Task TerminateAsync()
    {
        try { stop.Cancel(); } catch (AggregateException) { }
        runner?.Complete();
        return runner is null ? Task.CompletedTask : ended.Task;
    }
}

/// <summary>The engine in a child process. One process runs one script; the next one is started ahead so it is ready.
/// Every worker this process starts is tracked until its exit has been observed: a retired worker is killed (SIGKILL on Unix,
/// TerminateProcess on Windows), its standard input is closed and its exit is waited for, so it is reaped before the
/// <see cref="Process"/> is disposed and never lingers as a zombie or an orphan. A worker whose host dies gets EOF and exits.</summary>
internal sealed class CodemodeProcessWorker(CodemodeWorkerLauncher launcher) : ICodemodeWorker
{
    private static readonly ConcurrentDictionary<CodemodeWorkerLauncher, Process> Spares = new();
    private static readonly ConcurrentDictionary<Process, byte> Live = new(ReferenceEqualityComparer.Instance);
    private static int exitHooked;

    /// <summary>Worker processes started and not yet retired and reaped, spares included (tests).</summary>
    internal static int Running => Live.Count;

    private readonly object gate = new();
    private Process? process;
    private Task reading = Task.CompletedTask;
    private bool terminating;

    /// <summary>Stops the spare workers kept ready for the next script and waits until they are gone.</summary>
    internal static Task DiscardSparesAsync() =>
        Task.WhenAll(Spares.Keys.Select(key => Spares.TryRemove(key, out var spare) ? RetireAsync(spare) : Task.CompletedTask));

    /// <summary>Kill, close its input, wait for the exit (which reaps it), then forget and dispose. Killing first means closing the
    /// pipe can never wait on a worker that stopped reading.</summary>
    private static async Task RetireAsync(Process worker)
    {
        try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); } catch (Exception) { }
        try { worker.StandardInput.Close(); } catch (Exception) { }
        try { await worker.WaitForExitAsync().ConfigureAwait(false); } catch (Exception) { }
        Live.TryRemove(worker, out _);
        worker.Dispose();
    }

    private static Process Launch(CodemodeWorkerLauncher launcher)
    {
        if (Interlocked.Exchange(ref exitHooked, 1) == 0)
            // A normal host exit takes its spares down at once instead of leaving them to notice EOF.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                foreach (var key in Spares.Keys)
                    if (Spares.TryRemove(key, out var spare)) { try { spare.Kill(entireProcessTree: true); } catch (Exception) { } }
            };
        var info = new ProcessStartInfo(launcher.FileName)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in launcher.Arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in launcher.Environment ?? ImmutableDictionary<string, string>.Empty) info.Environment[name] = value;
        var started = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!started.Start()) { started.Dispose(); throw new InvalidOperationException("The codemode worker process did not start."); }
        Live[started] = 0;
        // Engine diagnostics belong to no one: drain and discard standard error, as the original discards QuickJS's.
        _ = started.StandardError.BaseStream.CopyToAsync(Stream.Null);
        started.StandardInput.NewLine = "\n";
        return started;
    }

    private static Process Take(CodemodeWorkerLauncher launcher)
    {
        Process? spare = null;
        if (Spares.TryRemove(launcher, out var candidate))
        {
            if (!candidate.HasExited) spare = candidate;
            else _ = RetireAsync(candidate);
        }
        _ = Task.Run(() =>
        {
            try
            {
                var next = Launch(launcher);
                if (!Spares.TryAdd(launcher, next)) _ = RetireAsync(next);
            }
            catch (Exception) { }
        });
        return spare ?? Launch(launcher);
    }

    public void Start(CodemodeStartMessage start, Action<CodemodeWorkerMessage> received, Action<int?> exited, Action<Exception> broken)
    {
        var started = Take(launcher);
        lock (gate) process = started;
        started.StandardInput.WriteLine(CodemodeWireCodec.Encode(start));
        started.StandardInput.Flush();
        reading = Task.Run(async () =>
        {
            try
            {
                while (await started.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    CodemodeWorkerMessage message;
                    try { message = CodemodeWireCodec.DecodeWorker(line); }
                    catch (Exception) { broken(new FormatException("unknown message from the worker")); return; }
                    received(message);
                }
                await started.WaitForExitAsync().ConfigureAwait(false);
                bool expected; lock (gate) expected = terminating;
                if (!expected) exited(started.ExitCode);
            }
            catch (Exception error) { bool expected; lock (gate) expected = terminating; if (!expected) broken(error); }
        });
    }

    public void Send(CodemodeResultMessage result)
    {
        Process? target; lock (gate) target = terminating ? null : process;
        if (target is null) return;
        try { lock (target) { target.StandardInput.WriteLine(CodemodeWireCodec.Encode(result)); target.StandardInput.Flush(); } }
        catch (Exception) { /* The worker is gone; its exit is reported by the reader. */ }
    }

    public async Task TerminateAsync()
    {
        Process? target;
        lock (gate) { terminating = true; target = process; }
        if (target is null) return;
        await RetireAsync(target).ConfigureAwait(false);
        try { await reading.ConfigureAwait(false); } catch (Exception) { }
    }
}
