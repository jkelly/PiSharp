// Platform raw terminal I/O for ProcessTerminal (Node's process.stdin.setRawMode and process.stdout).
using System.Runtime.InteropServices;
using System.Text;

namespace PiSharp.Tui.Pi;

/// <summary>Windows console: VT input and output processing on the process's console handles (libuv raw mode).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class WindowsRawTerminalIo : IRawTerminalIo
{
    private const int StdInput = -10, StdOutput = -11;
    private const uint EnableProcessedInput = 0x1, EnableLineInput = 0x2, EnableEchoInput = 0x4, EnableWindowInput = 0x8,
        EnableQuickEditMode = 0x40, EnableExtendedFlags = 0x80, EnableVirtualTerminalInput = 0x200;
    private const uint EnableProcessedOutput = 0x1, EnableWrapAtEolOutput = 0x2, EnableVirtualTerminalProcessing = 0x4;
    private nint input, output;
    private uint originalInputMode, originalOutputMode;
    private bool raw;
    private Thread? reader;
    private volatile bool reading;
    private volatile Action? activity;
    private readonly object writeGate = new();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleMode(nint handle, uint mode);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadConsole(nint handle, [Out] char[] buffer, uint count, out uint read, nint control);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsole(nint handle, string buffer, uint count, out uint written, nint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNumberOfConsoleInputEvents(nint handle, out uint events);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetConsoleScreenBufferInfo(nint handle, out ScreenBufferInfo info);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SmallRect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenBufferInfo { public Coord Size, Cursor; public ushort Attributes; public SmallRect Window; public Coord MaximumWindowSize; }

    public void EnterRawMode()
    {
        input = GetStdHandle(StdInput); output = GetStdHandle(StdOutput);
        if (!GetConsoleMode(input, out originalInputMode) || !GetConsoleMode(output, out originalOutputMode))
            throw new IOException("Interactive mode requires a console for standard input and output.");
        // libuv raw mode: no line input, echo or processed Ctrl+C; VT input so modified keys arrive as escape sequences.
        SetConsoleMode(input, (originalInputMode & ~(EnableLineInput | EnableEchoInput | EnableProcessedInput | EnableQuickEditMode)) |
            EnableExtendedFlags | EnableWindowInput | EnableVirtualTerminalInput);
        SetConsoleMode(output, originalOutputMode | EnableProcessedOutput | EnableWrapAtEolOutput | EnableVirtualTerminalProcessing);
        raw = true;
    }

    public void ExitRawMode()
    {
        if (!raw) return;
        raw = false;
        SetConsoleMode(input, originalInputMode);
        SetConsoleMode(output, originalOutputMode);
    }

    private void ReadLoop(Func<bool> keepGoing, uint wait, Action<string> onText)
    {
        var buffer = new char[4096]; char? pendingHigh = null;
        while (keepGoing())
        {
            if (WaitForSingleObject(input, wait) != 0) continue;
            if (!keepGoing()) break;
            if (!GetNumberOfConsoleInputEvents(input, out var events) || events == 0) continue;
            if (!ReadConsole(input, buffer, (uint)buffer.Length, out var read, 0)) break;
            if (read == 0) continue;
            activity?.Invoke();
            var text = new string(buffer, 0, (int)read);
            if (pendingHigh is { } high) { text = high + text; pendingHigh = null; }
            if (text.Length > 0 && char.IsHighSurrogate(text[^1])) { pendingHigh = text[^1]; text = text[..^1]; }
            if (text.Length > 0) onText(text);
        }
    }

    public void StartReading(Action<string> onText)
    {
        reading = true;
        reader = new Thread(() => ReadLoop(() => reading, 100, onText)) { IsBackground = true, Name = "pi-tui-stdin" };
        reader.Start();
    }

    public void StopReading()
    {
        reading = false;
        if (reader is not null && reader != Thread.CurrentThread) reader.Join(TimeSpan.FromSeconds(1));
        reader = null;
    }

    public IDisposable ObserveActivity(Action onActivity)
    {
        activity = onActivity;
        Thread? drain = null; var stop = 0;
        if (!reading)
        {
            // Nothing reads while draining: consume input here so key releases never reach the shell.
            drain = new Thread(() => ReadLoop(() => Volatile.Read(ref stop) == 0, 20, _ => { })) { IsBackground = true };
            drain.Start();
        }
        return new Callback(() => { activity = null; Volatile.Write(ref stop, 1); drain?.Join(TimeSpan.FromMilliseconds(200)); });
    }

    public void Write(string data)
    {
        if (data.Length == 0) return;
        lock (writeGate)
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var chunk = data.Length - offset <= 16384 ? (offset == 0 ? data : data[offset..]) : data.Substring(offset, 16384);
                if (!WriteConsole(output, chunk, (uint)chunk.Length, out var written, 0) || written == 0) return;
                offset += (int)written;
            }
        }
    }

    public (int Columns, int Rows) Size()
    {
        if (output == 0) output = GetStdHandle(StdOutput);
        if (!GetConsoleScreenBufferInfo(output, out var info)) return (0, 0);
        return (info.Window.Right - info.Window.Left + 1, info.Window.Bottom - info.Window.Top + 1);
    }
    public void WatchResize(Action onResize) { }
    public void StopWatchingResize() { }
    public bool IsShiftPressed() => (GetAsyncKeyState(0x10) & 0x8000) != 0;
    private sealed class Callback(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

/// <summary>POSIX tty: raw mode as libuv's UV_TTY_MODE_RAW, reads through poll/read on fd 0, writes to fd 1.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class PosixRawTerminalIo : IRawTerminalIo
{
    private readonly byte[] original = new byte[256];
    private bool raw;
    private Thread? reader;
    private volatile bool reading;
    private volatile Action? activity;
    private PosixSignalRegistration? winch, cont;
    private readonly object writeGate = new();

    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, byte[] termios);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int actions, byte[] termios);
    [DllImport("libc")] private static extern void cfmakeraw(byte[] termios);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint count, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, out WinSize size);
    [DllImport("libc", SetLastError = true)] private static extern int isatty(int fd);
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short Revents; }
    [StructLayout(LayoutKind.Sequential)] private struct WinSize { public ushort Rows, Columns, XPixel, YPixel; }
    private const int EIntr = 4;
    private static bool Again(int error) => error is EIntr or 11 or 35;

    public void EnterRawMode()
    {
        if (isatty(0) != 1 || tcgetattr(0, original) != 0) throw new IOException("Interactive mode requires a terminal for standard input.");
        var attributes = (byte[])original.Clone();
        cfmakeraw(attributes);
        // libuv keeps output post-processing (OPOST|ONLCR) in raw mode, unlike cfmakeraw.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            BitConverter.TryWriteBytes(attributes.AsSpan(8), BitConverter.ToUInt64(attributes, 8) | 0x1 | 0x2);
        else
            BitConverter.TryWriteBytes(attributes.AsSpan(4), BitConverter.ToUInt32(attributes, 4) | 0x1 | 0x4);
        if (tcsetattr(0, 0, attributes) != 0) throw new IOException("The terminal could not enter raw mode.");
        raw = true;
    }

    public void ExitRawMode()
    {
        if (!raw) return;
        raw = false;
        tcsetattr(0, 0, original);
    }

    private static int Poll(int timeout)
    {
        var fds = new[] { new PollFd { Fd = 0, Events = 1 } };
        var ready = poll(fds, 1, timeout);
        if (ready > 0 && (fds[0].Revents & 1) == 0 && (fds[0].Revents & (0x8 | 0x10 | 0x20)) != 0) return -2;
        return ready;
    }

    public void StartReading(Action<string> onText)
    {
        reading = true;
        reader = new Thread(() =>
        {
            var decoder = new UTF8Encoding(false, false).GetDecoder();
            var bytes = new byte[4096]; var chars = new char[4097];
            while (reading)
            {
                var ready = Poll(100);
                if (ready == -2) break;
                if (ready <= 0) { if (ready < 0 && !Again(Marshal.GetLastPInvokeError())) break; continue; }
                if (!reading) break;
                var count = read(0, bytes, bytes.Length);
                if (count <= 0) { if (count < 0 && Again(Marshal.GetLastPInvokeError())) continue; break; }
                activity?.Invoke();
                var charCount = decoder.GetChars(bytes, 0, (int)count, chars, 0, false);
                if (charCount > 0) onText(new string(chars, 0, charCount));
            }
        }) { IsBackground = true, Name = "pi-tui-stdin" };
        reader.Start();
    }

    public void StopReading()
    {
        reading = false;
        if (reader is not null && reader != Thread.CurrentThread) reader.Join(TimeSpan.FromSeconds(1));
        reader = null;
    }

    public IDisposable ObserveActivity(Action onActivity)
    {
        activity = onActivity;
        Thread? drain = null; var stop = 0;
        if (!reading)
        {
            drain = new Thread(() =>
            {
                var bytes = new byte[1024];
                while (Volatile.Read(ref stop) == 0)
                {
                    if (Poll(20) <= 0) continue;
                    if (read(0, bytes, bytes.Length) <= 0) break;
                    onActivity();
                }
            }) { IsBackground = true };
            drain.Start();
        }
        return new Callback(() => { activity = null; Volatile.Write(ref stop, 1); drain?.Join(TimeSpan.FromMilliseconds(200)); });
    }

    public void Write(string data)
    {
        if (data.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(data);
        lock (writeGate)
        {
            while (bytes.Length > 0)
            {
                var written = write(1, bytes, bytes.Length);
                if (written < 0) { if (Again(Marshal.GetLastPInvokeError())) continue; return; }
                if (written >= bytes.Length) return;
                bytes = bytes[(int)written..];
            }
        }
    }

    public (int Columns, int Rows) Size()
    {
        nuint request = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 0x40087468u : 0x5413u;
        if (ioctl(1, request, out var size) == 0 && size.Columns > 0) return (size.Columns, size.Rows);
        return ioctl(0, request, out size) == 0 ? (size.Columns, size.Rows) : (0, 0);
    }

    public void WatchResize(Action onResize)
    {
        winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => onResize());
        // SIGWINCH is lost while the process is stopped; SIGCONT re-checks the size after a resume.
        cont = PosixSignalRegistration.Create(PosixSignal.SIGCONT, _ => onResize());
    }
    public void StopWatchingResize() { winch?.Dispose(); winch = null; cont?.Dispose(); cont = null; }
    public bool IsShiftPressed() => false;
    private sealed class Callback(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
