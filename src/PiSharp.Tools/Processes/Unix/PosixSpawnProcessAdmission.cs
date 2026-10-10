// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bash-executor.ts and utils/shell.ts (the shell runs
// detached in its own process group; abort and timeout kill the whole tree).
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Tools.Processes.Unix;

/// <summary>
/// The Linux/macOS process admission, dependency-free over libc. An anchor (<c>/bin/sh</c> waiting on <c>sleep</c>, stdio on
/// <c>/dev/null</c>) is spawned with <c>POSIX_SPAWN_SETPGROUP</c> into a fresh private process group whose id is its pid; the command is
/// then spawned with <c>POSIX_SPAWN_SETPGROUP</c> into that same group, so it is a group member before its first instruction runs.
/// The command's stdout and stderr are pipes; its stdin is a pipe carrying <see cref="ProcessRequest.StandardInput"/> then EOF (or
/// <c>/dev/null</c> without it, source commandTransport "stdin"); its environment is exactly the request's, and it
/// starts in the request's working directory (a <c>/bin/sh</c> <c>cd</c> before <c>exec</c>). Both processes are reaped by this class
/// (<c>waitpid</c>). Stopping kills the whole group with SIGKILL, so descendants that stayed in the group die with it, as upstream
/// kills the detached process tree.
/// </summary>
public sealed class PosixSpawnProcessAdmission : IUnixProcessAdmission
{
    private const string Shell = "/bin/sh";
    private const string AnchorScript = "trap '' HUP INT TERM; while :; do sleep 86400; done";
    private const string LaunchScript = "cd -- \"$0\" || exit 127; exec \"$@\"";

    public ValueTask<IUnixProcessLease> LaunchAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        cancellationToken.ThrowIfCancellationRequested();
        // posix_spawn takes C strings: a NUL byte would silently cut an argument, the working directory or a variable and run something
        // else. Node's spawn refuses such strings (ERR_INVALID_ARG_VALUE), and so does every launch here.
        if (NodeArgumentErrors.SpawnNullBytes(request.Executable, request.Arguments, request.WorkingDirectory, request.Environment) is { } nulError)
            throw new ArgumentException(nulError, nameof(request));
        var environment = request.Environment.Select(pair => pair.Key + "=" + pair.Value).ToArray();
        var anchor = Spawn(Shell, [Shell, "-c", AnchorScript], environment, processGroup: 0, stdout: -1, stderr: -1);
        int[] stdout = [-1, -1], stderr = [-1, -1], stdin = [-1, -1];
        try
        {
            Pipe(stdout); Pipe(stderr);
            if (request.StandardInput is not null) Pipe(stdin);
            int command;
            try
            {
                command = Spawn(Shell, [Shell, "-c", LaunchScript, request.WorkingDirectory, request.Executable, .. request.Arguments], environment,
                    processGroup: anchor, stdout: stdout[1], stderr: stderr[1], stdin: stdin[0]);
            }
            finally { Close(ref stdout[1]); Close(ref stderr[1]); Close(ref stdin[0]); } // The child holds its copies; EOF follows its exit.
            // The input is written off the caller's thread and the pipe closed (EOF); a child that exits early ends the write (EPIPE).
            var input = request.StandardInput is { } bytes ? WriteInputAsync(Writer(ref stdin[1]), bytes) : Task.CompletedTask;
            var lease = new Lease(anchor, command, Stream(ref stdout[0]), Stream(ref stderr[0]), input);
            return ValueTask.FromResult<IUnixProcessLease>(lease);
        }
        catch
        {
            Close(ref stdout[0]); Close(ref stdout[1]); Close(ref stderr[0]); Close(ref stderr[1]); Close(ref stdin[0]); Close(ref stdin[1]);
            _ = Native.kill(-anchor, Native.SIGKILL);
            Reap(anchor);
            throw;
        }
    }

    private static FileStream Stream(ref int fd)
    {
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true); fd = -1;
        return new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: false);
    }

    private static FileStream Writer(ref int fd)
    {
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true); fd = -1;
        return new FileStream(handle, FileAccess.Write, bufferSize: 0, isAsync: false);
    }

    private static Task WriteInputAsync(FileStream pipe, byte[] bytes) => Task.Factory.StartNew(() =>
    {
        try { pipe.Write(bytes); } catch (IOException) { } // The child closed its end (it no longer reads input).
        finally { try { pipe.Dispose(); } catch (IOException) { } }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Pipe(int[] fds)
    {
        if (Native.pipe(fds) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "pipe failed");
        // The parent's ends must not leak into later children.
        _ = Native.fcntl(fds[0], Native.F_SETFD, Native.FD_CLOEXEC);
        _ = Native.fcntl(fds[1], Native.F_SETFD, Native.FD_CLOEXEC);
    }

    private static void Close(ref int fd) { if (fd >= 0) { _ = Native.close(fd); fd = -1; } }

    /// <summary>posix_spawn with a process group (0: a new group led by the child) and stdio: the given pipe ends, or /dev/null for
    /// each that is negative.</summary>
    private static int Spawn(string path, string[] argv, string[] envp, int processGroup, int stdout, int stderr, int stdin = -1)
    {
        var actions = Marshal.AllocHGlobal(1024); var attributes = Marshal.AllocHGlobal(1024);
        var strings = new List<IntPtr>();
        try
        {
            Check(Native.posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            Check(Native.posix_spawnattr_init(attributes), "posix_spawnattr_init");
            try
            {
                if (stdin >= 0) Check(Native.posix_spawn_file_actions_adddup2(actions, stdin, 0), "adddup2 stdin");
                else Check(Native.posix_spawn_file_actions_addopen(actions, 0, "/dev/null", Native.O_RDONLY, 0), "addopen stdin");
                if (stdout >= 0) Check(Native.posix_spawn_file_actions_adddup2(actions, stdout, 1), "adddup2 stdout");
                else Check(Native.posix_spawn_file_actions_addopen(actions, 1, "/dev/null", Native.O_WRONLY, 0), "addopen stdout");
                if (stderr >= 0) Check(Native.posix_spawn_file_actions_adddup2(actions, stderr, 2), "adddup2 stderr");
                else Check(Native.posix_spawn_file_actions_addopen(actions, 2, "/dev/null", Native.O_WRONLY, 0), "addopen stderr");
                Check(Native.posix_spawnattr_setpgroup(attributes, processGroup), "posix_spawnattr_setpgroup");
                Check(Native.posix_spawnattr_setflags(attributes, Native.POSIX_SPAWN_SETPGROUP), "posix_spawnattr_setflags");
                var argvBlock = Block(argv, strings); var envBlock = Block(envp, strings);
                try { Check(Native.posix_spawn(out var pid, path, actions, attributes, argvBlock, envBlock), "posix_spawn"); return pid; }
                finally { Marshal.FreeHGlobal(argvBlock); Marshal.FreeHGlobal(envBlock); }
            }
            finally { _ = Native.posix_spawn_file_actions_destroy(actions); _ = Native.posix_spawnattr_destroy(attributes); }
        }
        finally
        {
            foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(actions); Marshal.FreeHGlobal(attributes);
        }
    }

    private static IntPtr Block(string[] values, List<IntPtr> strings)
    {
        var block = Marshal.AllocHGlobal(IntPtr.Size * (values.Length + 1));
        for (var index = 0; index < values.Length; index++)
        {
            var text = Marshal.StringToCoTaskMemUTF8(values[index]); strings.Add(text);
            Marshal.WriteIntPtr(block, index * IntPtr.Size, text);
        }
        Marshal.WriteIntPtr(block, values.Length * IntPtr.Size, IntPtr.Zero);
        return block;
    }

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new Win32Exception(result, operation + " failed");
    }

    /// <summary>waitpid until the process is reaped: its exit status, or 128 + the terminating signal.</summary>
    internal static int Reap(int pid)
    {
        while (true)
        {
            var reaped = Native.waitpid(pid, out var status, 0);
            if (reaped == pid) return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            var error = Marshal.GetLastPInvokeError();
            if (reaped == -1 && error == Native.EINTR) continue;
            return -1;
        }
    }

    private sealed class Lease : IUnixProcessLease
    {
        private readonly int anchor;
        private readonly Task<int> anchorExit;
        private readonly Task input;
        private readonly object gate = new();
        private Task<bool>? stop;
        private Task? disposal;
        public int ProcessId { get; }
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }
        public Task<int> Exit { get; }

        internal Lease(int anchor, int command, Stream stdout, Stream stderr, Task input)
        {
            this.anchor = anchor; this.input = input; ProcessId = command; StandardOutput = stdout; StandardError = stderr;
            Exit = Task.Factory.StartNew(() => Reap(command), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            anchorExit = Task.Factory.StartNew(() => Reap(anchor), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public ValueTask<bool> StopGroupAsync() { lock (gate) return new(stop ??= StopCoreAsync()); }

        /// <summary>SIGKILL to the whole group (the anchor keeps the group id reserved until now), then both reaps are joined and the
        /// group must be gone.</summary>
        private async Task<bool> StopCoreAsync()
        {
            if (anchorExit.IsCompleted) return false; // The identity anchor is gone: never signal a group id that may be reused.
            if (Native.kill(-anchor, Native.SIGKILL) != 0 && Marshal.GetLastPInvokeError() != Native.ESRCH) return false;
            await Task.WhenAll(Exit, anchorExit).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (Native.kill(-anchor, 0) != 0) return Marshal.GetLastPInvokeError() == Native.ESRCH;
                await Task.Delay(10).ConfigureAwait(false);
            }
            return false;
        }

        public ValueTask DisposeAsync() { lock (gate) return new(disposal ??= DisposeCoreAsync()); }
        private async Task DisposeCoreAsync()
        {
            var failures = new List<Exception>();
            try { await StopGroupAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await input.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await StandardOutput.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            try { await StandardError.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
    }

    private static class Native
    {
        internal const int SIGKILL = 9, ESRCH = 3, EINTR = 4, O_RDONLY = 0, O_WRONLY = 1, F_SETFD = 2, FD_CLOEXEC = 1;
        internal const short POSIX_SPAWN_SETPGROUP = 0x02;
        [DllImport("libc", SetLastError = true)] internal static extern int pipe([Out] int[] fds);
        [DllImport("libc", SetLastError = true)] internal static extern int fcntl(int fd, int command, int argument);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int kill(int pid, int signal);
        [DllImport("libc", SetLastError = true)] internal static extern int waitpid(int pid, out int status, int options);
        [DllImport("libc")] internal static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr fileActions, IntPtr attributes, IntPtr argv, IntPtr envp);
        [DllImport("libc")] internal static extern int posix_spawn_file_actions_init(IntPtr actions);
        [DllImport("libc")] internal static extern int posix_spawn_file_actions_destroy(IntPtr actions);
        [DllImport("libc")] internal static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
        [DllImport("libc")] internal static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
        [DllImport("libc")] internal static extern int posix_spawnattr_init(IntPtr attributes);
        [DllImport("libc")] internal static extern int posix_spawnattr_destroy(IntPtr attributes);
        [DllImport("libc")] internal static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
        [DllImport("libc")] internal static extern int posix_spawnattr_setpgroup(IntPtr attributes, int processGroup);
    }
}

/// <summary>Source createLocalBashOperations on Linux and macOS: <c>shell -c command</c> in its own process group (the
/// <see cref="PosixSpawnProcessAdmission"/>), stdout and stderr forwarded as raw chunks in arrival order, the group killed on
/// cancellation. Returns the exit code.</summary>
public sealed class PosixShellOperations(ShellConfiguration shell, ImmutableDictionary<string, string> environment, string scratchDirectory) : IShellOperations
{
    private readonly IUnixProcessAdmission admission = new PosixSpawnProcessAdmission();

    public async ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onData);
        // As on Windows: the missing working directory, Node's spawn validation (a NUL byte is ERR_INVALID_ARG_VALUE, never a command
        // cut short by posix_spawn's C strings), E2BIG and ENOENT, before anything is spawned.
        ShellSpawnPreflight.Check(shell, command, workingDirectory, environment);
        var request = new ProcessRequest(shell.Shell, shell.CommandArguments(command), workingDirectory, environment,
            Path.Combine(scratchDirectory, "pi-bash-discarded-" + Guid.NewGuid().ToString("N") + ".log"))
        { StandardInput = shell.CommandTransport == ShellCommandTransport.Stdin ? Encoding.UTF8.GetBytes(command) : null };
        await using var lease = await admission.LaunchAsync(request, cancellationToken).ConfigureAwait(false);
        var callbacks = new SemaphoreSlim(1, 1);
        async Task PumpAsync(Stream stream)
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) return;
                await callbacks.WaitAsync().ConfigureAwait(false);
                try { await onData(buffer.AsMemory(0, count).ToArray()).ConfigureAwait(false); } finally { callbacks.Release(); }
            }
        }
        var pumps = Task.WhenAll(Task.Run(() => PumpAsync(lease.StandardOutput)), Task.Run(() => PumpAsync(lease.StandardError)));
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => canceled.TrySetResult()))
        {
            var finished = Task.WhenAll(lease.Exit, pumps);
            if (await Task.WhenAny(finished, canceled.Task).ConfigureAwait(false) != finished)
            {
                await lease.StopGroupAsync().ConfigureAwait(false);
                try { await pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
                throw new OperationCanceledException(cancellationToken);
            }
        }
        var exit = await lease.Exit.ConfigureAwait(false);
        return exit < 0 ? null : exit;
    }
}
