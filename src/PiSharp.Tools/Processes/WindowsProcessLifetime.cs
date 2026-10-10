using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Tools.Processes;

internal sealed class ProcessLaunchException(ProcessDiagnostic diagnostic, bool cleanupConfirmed) : Exception("Process launch failed.")
{
    public ProcessDiagnostic Diagnostic { get; } = diagnostic;
    public bool CleanupConfirmed { get; } = cleanupConfirmed;
    /// <summary>The Win32 error of a refused CreateProcess.</summary>
    public int? NativeError { get; init; }
}

/// <summary>Owns one suspended launch, its private non-breakaway job, restricted inherited handles and async pipes.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessLifetime : IAsyncDisposable
{
    private SafeKernelHandle? _job, _process, _thread, _stdoutClient, _stderrClient, _stdinClient;
    private NamedPipeServerStream? _stdout, _stderr, _stdin;
    private Task? _input;
    private WaitHandle? _processWait;
    private RegisteredWaitHandle? _exitRegistration;
    private readonly TaskCompletionSource<int?> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _exitCallbackSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task<bool>? _termination;
    private Task? _disposal;
    private bool _assigned;
    public bool Started { get; private set; }
    public bool CleanupConfirmed { get; private set; } = true;
    public int ProcessId { get; private set; }
    public Task<int?> Exit => _exit.Task;
    public Stream StandardOutput => _stdout ?? throw new InvalidOperationException("Output is closed.");
    public Stream StandardError => _stderr ?? throw new InvalidOperationException("Output is closed.");

    public static async ValueTask<WindowsProcessLifetime> StartAsync(ProcessRequest request,
        ProcessLifecycleCallback? observer, CancellationToken token)
    {
        var owned = new WindowsProcessLifetime();
        var diagnostic = ProcessDiagnostic.SpawnFailed;
        try
        {
            token.ThrowIfCancellationRequested();
            (owned._stdout, owned._stdoutClient) = await PipeAsync(PipeDirection.In, token).ConfigureAwait(false);
            (owned._stderr, owned._stderrClient) = await PipeAsync(PipeDirection.In, token).ConfigureAwait(false);
            // A read-only null-device handle has immediate EOF. A connected named pipe whose
            // server is closed before resume does not establish this contract reliably.
            if (request.StandardInput is not null)
                (owned._stdin, owned._stdinClient) = await PipeAsync(PipeDirection.Out, token).ConfigureAwait(false);
            else
            {
                var inputSecurity = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
                owned._stdinClient = Native.CreateFileW("NUL", 0x80000000, 3, ref inputSecurity, 3, 0, IntPtr.Zero);
                if (owned._stdinClient.IsInvalid) throw new IOException("Null input creation failed.");
            }
            owned._job = Native.CreateJobObjectW(IntPtr.Zero, null);
            if (owned._job.IsInvalid) throw new IOException("Job creation failed.");
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway flags.
            if (!Native.SetInformationJobObject(owned._job, 9, ref limits,
                    (uint)Marshal.SizeOf<ExtendedLimitInformation>())) throw new IOException("Job configuration failed.");

            token.ThrowIfCancellationRequested();
            // CreateProcessW is synchronous. Keep its ownership joined, while allowing the
            // caller to obtain the asynchronous run and request cancellation during launch.
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                owned.CreateSuspended(request);
            }).ConfigureAwait(false);
            owned.RegisterExitWait();
            if (!Native.AssignProcessToJobObject(owned._job, owned._process!)) throw new IOException("Job assignment failed.");
            owned._assigned = true;
            owned.CloseClientCopies();
            if (observer is not null)
            {
                diagnostic = ProcessDiagnostic.LifecycleCallbackFailed;
                await observer(new(ProcessLifecycleStage.BeforeResume, owned.ProcessId, false, false)).ConfigureAwait(false);
                diagnostic = ProcessDiagnostic.SpawnFailed;
            }
            token.ThrowIfCancellationRequested();
            if (Native.ResumeThread(owned._thread!) == uint.MaxValue) throw new IOException("Process resume failed.");
            owned.Started = true;
            owned._thread!.Dispose(); owned._thread = null;
            // Source commandTransport "stdin": write the command after resume, then close for EOF. Write errors are ignored
            // (source child.stdin.on("error", () => {})); a process that exits without reading breaks the pipe.
            if (owned._stdin is { } input) owned._input = WriteInputAsync(input, request.StandardInput!);
            return owned;
        }
        catch (Exception error)
        {
            await owned.DisposeAsync().ConfigureAwait(false);
            if (error is OperationCanceledException && token.IsCancellationRequested && owned.CleanupConfirmed) throw;
            throw new ProcessLaunchException(owned.CleanupConfirmed ? diagnostic : ProcessDiagnostic.CleanupFailed,
                owned.CleanupConfirmed) { NativeError = (error as System.ComponentModel.Win32Exception)?.NativeErrorCode };
        }
    }

    private static async Task WriteInputAsync(NamedPipeServerStream input, byte[] bytes)
    {
        try { await input.WriteAsync(bytes).ConfigureAwait(false); await input.FlushAsync().ConfigureAwait(false); }
        catch (Exception) { }
        finally { try { await input.DisposeAsync().ConfigureAwait(false); } catch (Exception) { } }
    }

    private static async ValueTask<(NamedPipeServerStream Server, SafeKernelHandle Client)> PipeAsync(
        PipeDirection direction, CancellationToken token)
    {
        var name = "pisharp-process-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, direction, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
        SafeKernelHandle? client = null;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            client = Native.CreateFileW("\\\\.\\pipe\\" + name, direction == PipeDirection.In ? 0x40000000u : 0x80000000u,
                0, ref security, 3, 0, IntPtr.Zero);
            if (client.IsInvalid) throw new IOException("Pipe connection failed.");
            await server.WaitForConnectionAsync(token).ConfigureAwait(false);
            return (server, client);
        }
        catch { client?.Dispose(); await server.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void CreateSuspended(ProcessRequest request)
    {
        var bytes = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        var inherited = IntPtr.Zero;
        var environment = IntPtr.Zero;
        var initialized = false;
        try
        {
            _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (bytes == IntPtr.Zero || bytes.ToInt64() > 65536) throw new IOException("Invalid launch attributes.");
            attributes = Marshal.AllocHGlobal(bytes);
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new IOException("Launch attributes failed.");
            initialized = true;
            inherited = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(inherited, 0, _stdinClient!.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size, _stdoutClient!.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 2 * IntPtr.Size, _stderrClient!.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, inherited,
                    (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new IOException("Inherited handle list failed.");
            var start = new StartupInformationEx
            {
                StartupInformation = new StartupInformation
                {
                    Size = (uint)Marshal.SizeOf<StartupInformationEx>(), Flags = 0x100,
                    StandardInput = _stdinClient.DangerousGetHandle(), StandardOutput = _stdoutClient.DangerousGetHandle(),
                    StandardError = _stderrClient.DangerousGetHandle()
                },
                AttributeList = attributes
            };
            var env = new StringBuilder();
            foreach (var (key, value) in request.Environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                env.Append(key).Append('=').Append(value).Append('\0');
            env.Append('\0'); if (request.Environment.Count == 0) env.Append('\0');
            environment = Marshal.StringToHGlobalUni(env.ToString());
            var command = new StringBuilder(Quote(request.Executable));
            foreach (var argument in request.Arguments) command.Append(' ').Append(Quote(argument));
            if (command.Length >= 32767) throw new IOException("Command exceeds launch limit.");
            // A bad executable must fail without a loader/error dialog holding the launch.
            // Change only this worker thread, preserving its existing flags and restoring
            // them before returning it to the pool. No await occurs within this scope.
            var errorMode = Native.GetThreadErrorMode();
            if (!Native.SetThreadErrorMode(errorMode | 0x8003, out var previousErrorMode))
                throw new IOException("Headless launch configuration failed.");
            try
            {
                if (!Native.CreateProcessW(request.Executable, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x4 | 0x400 | 0x80000 | 0x08000000, environment, request.WorkingDirectory, ref start, out var process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Process creation failed.");
                _process = new(process.Process, true); _thread = new(process.Thread, true);
                ProcessId = checked((int)process.ProcessId);
            }
            finally
            {
                if (!Native.SetThreadErrorMode(previousErrorMode, out _))
                    throw new IOException("Headless launch restoration failed.");
            }
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (inherited != IntPtr.Zero) Marshal.FreeHGlobal(inherited);
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }

    // Windows CRT transport quoting. The explicit application name prevents executable token ambiguity.
    /// <summary>Characters of the CreateProcess command line built for these arguments (at most 32,766 are accepted).</summary>
    internal static long CommandLineLength(string executable, IEnumerable<string> arguments) =>
        Quote(executable).Length + arguments.Sum(argument => 1L + Quote(argument).Length);

    internal static string Quote(string argument)
    {
        var quoted = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character); slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    private void RegisterExitWait()
    {
        var process = _process!;
        _processWait = new KernelWait(process.DangerousGetHandle());
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(_processWait, (_, _) =>
        {
            try
            {
                if (Native.GetExitCodeProcess(process, out var code)) _exit.TrySetResult(unchecked((int)code));
                else _exit.TrySetResult(null);
            }
            catch (Exception) { _exit.TrySetResult(null); }
            finally { _exitCallbackSettled.TrySetResult(true); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public Task<bool> TerminateAndConfirmAsync()
    {
        lock (_gate) return _termination ??= Task.Run(TerminateCoreAsync);
    }

    private async Task<bool> TerminateCoreAsync()
    {
        var confirmed = true;
        try
        {
            if (_process is null) return true;
            if (_assigned && _job is not null)
            {
                if (!Native.TerminateJobObject(_job, 1)) confirmed = false;
            }
            else if (!Exit.IsCompleted && !Native.TerminateProcess(_process, 1)) confirmed = false;
            if (_exitRegistration is null) RegisterExitWait();
            await Exit.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (_assigned && _job is not null)
            {
                var deadline = Environment.TickCount64 + 5000;
                while (true)
                {
                    var accounting = new BasicAccountingInformation();
                    if (!Native.QueryInformationJobObject(_job, 1, ref accounting,
                            (uint)Marshal.SizeOf<BasicAccountingInformation>(), out _)) { confirmed = false; break; }
                    if (accounting.ActiveProcesses == 0) break;
                    if (Environment.TickCount64 >= deadline) { confirmed = false; break; }
                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) { confirmed = false; _exit.TrySetResult(null); }
        CleanupConfirmed = confirmed;
        return confirmed;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposal ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        CleanupConfirmed &= await TerminateAndConfirmAsync().ConfigureAwait(false);
        CloseClientCopies();
        try { if (_stdout is not null) await _stdout.DisposeAsync().ConfigureAwait(false); } catch (Exception) { CleanupConfirmed = false; }
        try { if (_stderr is not null) await _stderr.DisposeAsync().ConfigureAwait(false); } catch (Exception) { CleanupConfirmed = false; }
        if (_input is not null)
        {
            try { await _input.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { CleanupConfirmed = false; }
        }
        else if (_stdin is not null) try { await _stdin.DisposeAsync().ConfigureAwait(false); } catch (Exception) { CleanupConfirmed = false; }
        _stdout = null; _stderr = null; _stdin = null; _input = null;
        if (_exitRegistration is not null)
        {
            try { await _exitCallbackSettled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { CleanupConfirmed = false; }
        }
        _exitRegistration?.Unregister(null); _exitRegistration = null;
        _processWait?.Dispose(); _processWait = null;
        _thread?.Dispose(); _process?.Dispose(); _job?.Dispose();
        _thread = null; _process = null; _job = null;
    }

    private void CloseClientCopies()
    {
        _stdinClient?.Dispose(); _stdoutClient?.Dispose(); _stderrClient?.Dispose();
        _stdinClient = null; _stdoutClient = null; _stderrClient = null;
    }

    private sealed class KernelWait : WaitHandle
    { public KernelWait(IntPtr process) => SafeWaitHandle = new(process, ownsHandle: false); }
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle() : base(true) { }
        public SafeKernelHandle(IntPtr handle, bool owns) : base(owns) => SetHandle(handle);
        protected override bool ReleaseHandle() => Native.CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        public uint Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes; public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx { public StartupInformation StartupInformation; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation; public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    private static class Native
    {
        [DllImport("kernel32.dll")] public static extern uint GetThreadErrorMode();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetThreadErrorMode(uint mode, out uint previousMode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeKernelHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(SafeKernelHandle job, int kind, ref ExtendedLimitInformation value, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AssignProcessToJobObject(SafeKernelHandle job, SafeKernelHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateJobObject(SafeKernelHandle job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryInformationJobObject(SafeKernelHandle job, int kind, ref BasicAccountingInformation value, uint length, out uint returned);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeKernelHandle CreateFileW(string path, uint access, uint sharing, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
            string directory, ref StartupInformationEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(SafeKernelHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateProcess(SafeKernelHandle process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    }
}
