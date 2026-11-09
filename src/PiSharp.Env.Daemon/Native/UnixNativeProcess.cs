using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PiSharp.Env.Daemon.Native;

/// <summary>The native shim owns fork through exec; the CLR runs only in the parent.</summary>
internal sealed class UnixNativeProcess : IAsyncDisposable
{
    private readonly object gate = new();
    private Task? disposal;
    private bool reaped;
    private readonly NativeFdStream input, output;
    private readonly NativeFdStream? error;
    public int ProcessId { get; }
    public Stream StandardInput => input;
    public Stream StandardOutput => output;
    public Stream StandardError => error ?? Stream.Null;
    public Task<int> Exit { get; }

    private UnixNativeProcess(int pid, int inputFd, int outputFd, int errorFd, bool terminal)
    {
        ProcessId = pid;
        input = new(inputFd, readable: false, writable: true, terminal);
        output = new(outputFd, readable: true, writable: false, terminal);
        if (errorFd >= 0) error = new(errorFd, readable: true, writable: false, terminal: false);
        Exit = Task.Run(() => { Native.Check(Native.WaitExit(pid, out var code)); return code; });
    }

    public static Task<UnixNativeProcess> SpawnPipedAsync(IReadOnlyList<string> argv, string cwd,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        Task.FromResult(Spawn(argv, cwd, environment, terminal: false, 0, 0, cancellationToken));

    public static UnixNativeProcess SpawnTerminal(IReadOnlyList<string> argv, string cwd,
        IReadOnlyDictionary<string, string> environment, int cols, int rows, CancellationToken cancellationToken,
        string? executable = null) => Spawn(argv, cwd, environment, terminal: true, cols, rows, cancellationToken, executable);

    private static UnixNativeProcess Spawn(IReadOnlyList<string> argv, string cwd,
        IReadOnlyDictionary<string, string> environment, bool terminal, int cols, int rows,
        CancellationToken cancellationToken, string? executable = null)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Unix child processes require Linux or macOS.");
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0]) || argv.Any(value => value is null || value.Contains('\0')))
            throw new ArgumentException("argv must contain a program and valid arguments.", nameof(argv));
        if (cwd.Contains('\0') || environment.Any(pair => pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0')))
            throw new ArgumentException("Invalid working directory or environment.");
        executable = ResolveExecutable(executable ?? argv[0], cwd, environment);
        using var arguments = new NativeStringArray(argv);
        using var variables = new NativeStringArray(environment.Select(pair => pair.Key + "=" + pair.Value));
        Native.Check(Native.Spawn(executable, arguments.Pointer, variables.Pointer, cwd, terminal ? 1 : 0,
            checked((ushort)cols), checked((ushort)rows), out var pid, out var stdin, out var stdout, out var stderr));
        try { return new(pid, stdin, stdout, stderr, terminal); }
        catch
        {
            Native.KillGroup(pid);
            Native.Close(stdin); Native.Close(stdout); if (stderr >= 0) Native.Close(stderr);
            Native.Reap(pid);
            throw;
        }
    }

    private static string ResolveExecutable(string executable, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (executable.Contains('/')) return Path.GetFullPath(executable, cwd);
        var search = environment.TryGetValue("PATH", out var path) ? path : "/usr/local/bin:/usr/bin:/bin";
        string? inaccessible = null;
        foreach (var directory in search.Split(':'))
        {
            var candidate = Path.Combine(Path.GetFullPath(directory.Length == 0 ? "." : directory, cwd), executable);
            if (!File.Exists(candidate)) continue;
            // Match execvp's search through non-executable candidates; execve still validates permissions.
            if ((File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return candidate;
            inaccessible ??= candidate;
        }
        return inaccessible ?? Path.Combine(cwd, executable);
    }

    public void Resize(int cols, int rows) => output.WithDescriptor(fd => Native.Check(Native.Resize(fd, checked((ushort)cols), checked((ushort)rows))));
    public int Foreground() { var group = 0; output.WithDescriptor(fd => Native.Check(Native.Foreground(fd, out group))); return group; }
    public static string? ProcessName(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return File.ReadAllText($"/proc/{pid}/comm").TrimEnd();
            var bytes = new byte[1024]; var length = Native.ProcessName(pid, bytes, bytes.Length);
            return length > 0 ? Encoding.UTF8.GetString(bytes, 0, length).TrimEnd('\0') : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The leader remains waitable until DisposeAsync; its group id cannot be reused meanwhile.</summary>
    public ValueTask KillAsync()
    {
        lock (gate) { if (!reaped) Native.Check(Native.KillGroup(ProcessId)); }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try { Native.Check(Native.KillGroup(ProcessId)); }
        finally { input.Dispose(); output.Dispose(); error?.Dispose(); }
        try { await Exit.ConfigureAwait(false); }
        finally { lock (gate) { Native.Check(Native.Reap(ProcessId)); reaped = true; } }
    }

    private sealed class NativeStringArray : IDisposable
    {
        private readonly IntPtr[] strings;
        public IntPtr Pointer { get; }
        public NativeStringArray(IEnumerable<string> values)
        {
            strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocCoTaskMem(checked((strings.Length + 1) * IntPtr.Size));
            Marshal.Copy(strings, 0, Pointer, strings.Length);
            Marshal.WriteIntPtr(Pointer, strings.Length * IntPtr.Size, IntPtr.Zero);
        }
        public void Dispose() { foreach (var item in strings) Marshal.FreeCoTaskMem(item); Marshal.FreeCoTaskMem(Pointer); }
    }

    private sealed class NativeFdStream(int descriptor, bool readable, bool writable, bool terminal) : Stream
    {
        private readonly object fdGate = new();
        private int fd = descriptor;
        public override bool CanRead => readable;
        public override bool CanWrite => writable;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void WithDescriptor(Action<int> action) { lock (fdGate) { ObjectDisposedException.ThrowIf(fd < 0, this); action(fd); } }
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsMemory(offset, count), CancellationToken.None);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => ReadCore(buffer, cancellationToken), cancellationToken));
        private int ReadCore(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (!readable) throw new NotSupportedException();
            if (destination.Length == 0) return 0;
            var buffer = new byte[Math.Min(destination.Length, 64 * 1024)];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int received;
                lock (fdGate)
                {
                    ObjectDisposedException.ThrowIf(fd < 0, this);
                    var ready = Native.Poll(fd, 0);
                    if (ready == -4 || ready == 0) continue; // EINTR or polling interval elapsed.
                    Native.CheckNegative(ready);
                    received = Native.Read(fd, buffer, buffer.Length);
                }
                if (received is -4 or -11 or -35) continue; // EINTR, Linux/macOS EAGAIN.
                if (terminal && received == -5) return 0; // Linux PTY EOF is EIO.
                Native.CheckNegative(received);
                buffer.AsMemory(0, received).CopyTo(destination);
                return received;
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteCore(buffer.AsMemory(offset, count), CancellationToken.None);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => WriteCore(buffer, cancellationToken), cancellationToken));
        private void WriteCore(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            if (!writable) throw new NotSupportedException();
            while (source.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = source[..Math.Min(source.Length, 64 * 1024)].ToArray();
                int written;
                lock (fdGate)
                {
                    ObjectDisposedException.ThrowIf(fd < 0, this);
                    var ready = Native.Poll(fd, 1);
                    if (ready == -4 || ready == 0) continue;
                    Native.CheckNegative(ready);
                    written = Native.Write(fd, buffer, buffer.Length);
                }
                if (written is -4 or -11 or -35) continue;
                Native.CheckNegative(written);
                if (written == 0) throw new IOException("Child input closed.");
                source = source[written..];
            }
        }
        protected override void Dispose(bool disposing) { lock (fdGate) { if (fd >= 0) { Native.Close(fd); fd = -1; } } base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static class Native
    {
        private const string Library = "pisharp_env_native";
        public static void Check(int error) { if (error != 0) throw new Win32Exception(error); }
        public static void CheckNegative(int result) { if (result < 0) throw new Win32Exception(-result); }
        [DllImport(Library, EntryPoint = "pisharp_spawn", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Spawn([MarshalAs(UnmanagedType.LPUTF8Str)] string executable, IntPtr argv, IntPtr env,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string cwd, int terminal, ushort cols, ushort rows, out int pid, out int input, out int output, out int error);
        [DllImport(Library, EntryPoint = "pisharp_wait_exit", CallingConvention = CallingConvention.Cdecl)] public static extern int WaitExit(int pid, out int exitCode);
        [DllImport(Library, EntryPoint = "pisharp_reap", CallingConvention = CallingConvention.Cdecl)] public static extern int Reap(int pid);
        [DllImport(Library, EntryPoint = "pisharp_kill_group", CallingConvention = CallingConvention.Cdecl)] public static extern int KillGroup(int pid);
        [DllImport(Library, EntryPoint = "pisharp_poll", CallingConvention = CallingConvention.Cdecl)] public static extern int Poll(int fd, int writing);
        [DllImport(Library, EntryPoint = "pisharp_read", CallingConvention = CallingConvention.Cdecl)] public static extern int Read(int fd, [Out] byte[] buffer, int length);
        [DllImport(Library, EntryPoint = "pisharp_write", CallingConvention = CallingConvention.Cdecl)] public static extern int Write(int fd, byte[] buffer, int length);
        [DllImport(Library, EntryPoint = "pisharp_close", CallingConvention = CallingConvention.Cdecl)] public static extern void Close(int fd);
        [DllImport(Library, EntryPoint = "pisharp_resize", CallingConvention = CallingConvention.Cdecl)] public static extern int Resize(int fd, ushort cols, ushort rows);
        [DllImport(Library, EntryPoint = "pisharp_foreground", CallingConvention = CallingConvention.Cdecl)] public static extern int Foreground(int fd, out int pid);
        [DllImport(Library, EntryPoint = "pisharp_process_name", CallingConvention = CallingConvention.Cdecl)] public static extern int ProcessName(int pid, [Out] byte[] buffer, int length);
    }
}
