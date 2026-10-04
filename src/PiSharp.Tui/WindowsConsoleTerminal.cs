using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Tui;

/// <summary>Process-exclusive Windows console state lease over owned duplicates of borrowed handles.</summary>
public sealed class WindowsConsoleTerminal : IConsoleTerminal, ITerminalViewportSource
{
    private static int leased;
    private readonly SafeFileHandle input, output;
    private readonly TerminalLeaseOptions options;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopToken;
    private readonly object gate = new();
    private readonly TerminalConsoleState original, acquired;
    private TerminalConsoleState? restored;
    private Task? read, write, disposal;
    private int activeReads, activeWrites;
    private long readStarted, readSettled, writeStarted, writeSettled;
    private WindowsConsoleTerminal(SafeFileHandle input, SafeFileHandle output, TerminalLeaseOptions options, TerminalConsoleState original, TerminalConsoleState acquired)
    { this.input = input; this.output = output; this.options = options; this.original = original; this.acquired = acquired; stopToken = stop.Token; }
    public TerminalLeaseSnapshot Snapshot
    { get { lock (gate) return new(original, acquired, restored, disposal is not null, restored == original, activeReads, activeWrites, readStarted, readSettled, writeStarted, writeSettled); } }

    public TerminalViewport ReadViewport()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            using var active = options.FollowActiveScreenBuffer ? OpenActiveOutput() : null;
            if (!Native.GetConsoleScreenBufferInfo(active ?? output, out var value)) throw Error(TerminalFailure.NativeIoFailed);
            var columns = value.Window.Right - value.Window.Left + 1;
            var rows = value.Window.Bottom - value.Window.Top + 1;
            if (columns < 1 || rows < 1 || value.Window.Left < 0 || value.Window.Top < 0 ||
                value.Window.Right >= value.Size.X || value.Window.Bottom >= value.Size.Y)
                throw Error(TerminalFailure.NativeIoFailed);
            return new(columns, rows, value.Window.Left, value.Window.Top, value.Size.X, value.Size.Y);
        }
    }

    public static ValueTask<WindowsConsoleTerminal> OpenAsync(TerminalLeaseOptions? options = null, CancellationToken token = default)
    {
        Validate(options ?? new()); token.ThrowIfCancellationRequested(); Windows();
        using var input = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-10)), ownsHandle: false);
        using var output = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-11)), ownsHandle: false);
        return OpenBorrowedHandlesAsync(input, output, options, token);
    }
    public static ValueTask<WindowsConsoleTerminal> OpenBorrowedHandlesAsync(SafeFileHandle borrowedInput, SafeFileHandle borrowedOutput,
        TerminalLeaseOptions? options = null, CancellationToken token = default)
    {
        var configured = options ?? new(); Validate(configured); token.ThrowIfCancellationRequested(); Windows();
        ArgumentNullException.ThrowIfNull(borrowedInput); ArgumentNullException.ThrowIfNull(borrowedOutput);
        if (Interlocked.CompareExchange(ref leased, 1, 0) != 0) throw Error(TerminalFailure.AlreadyOwned);
        SafeFileHandle? input = null, output = null; TerminalConsoleState? original = null; var changed = false;
        try
        {
            input = Duplicate(borrowedInput); output = Duplicate(borrowedOutput); original = Inspect(input, output);
            token.ThrowIfCancellationRequested(); changed = true;
            // No codepage, process environment or system setting changes. Both original modes remain exact snapshots.
            if (!Native.SetConsoleMode(input, (original.InputMode & ~0x0047u) | 0x0280u) ||
                !Native.SetConsoleMode(output, original.OutputMode | 0x0005u)) throw Error(TerminalFailure.NativeIoFailed);
            var cursor = new CursorInfo { Size = original.CursorSize, Visible = false };
            if (!Native.SetConsoleCursorInfo(output, ref cursor)) throw Error(TerminalFailure.NativeIoFailed);
            var acquired = Inspect(input, output); token.ThrowIfCancellationRequested();
            var terminal = new WindowsConsoleTerminal(input, output, configured, original, acquired); input = output = null;
            return ValueTask.FromResult(terminal);
        }
        catch
        {
            if (changed && original is not null && input is not null && output is not null)
            {
                var confirmed = Restore(input, output, original);
                try { confirmed = Inspect(input, output) == original && confirmed; } catch (Exception) { confirmed = false; }
                if (!confirmed) throw Error(TerminalFailure.RestorationFailed);
            }
            throw;
        }
        finally
        {
            if (input is not null || output is not null) { input?.Dispose(); output?.Dispose(); Volatile.Write(ref leased, 0); }
            // Failure before the first duplicate still releases the process reservation.
            else if (original is null) Volatile.Write(ref leased, 0);
        }
    }
    public static TerminalConsoleState InspectStandardConsole()
    {
        Windows(); using var input = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-10)), false);
        using var output = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-11)), false); return Inspect(input, output);
    }

    public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); TaskCompletionSource admission; Task<int> operation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (destination.IsEmpty) return ValueTask.FromResult(0);
            if (activeReads != 0) throw new InvalidOperationException("Terminal permits one actual input read.");
            admission = NewGate(); activeReads = 1; readStarted++; operation = ReadOwned(destination, token, admission.Task); read = operation;
        }
        admission.TrySetResult(); return new(operation);
    }
    private async Task<int> ReadOwned(Memory<char> destination, CancellationToken caller, Task admission)
    {
        await admission.ConfigureAwait(false);
        try
        {
            var value = await Worker(token =>
            {
                token.ThrowIfCancellationRequested(); var data = new char[Math.Min(destination.Length, options.MaximumReadCharacters)];
                var success = Native.ReadConsoleW(input, data, (uint)data.Length, out var count, IntPtr.Zero); var error = success ? 0 : Marshal.GetLastPInvokeError();
                if (count > data.Length) throw Error(TerminalFailure.NativeIoFailed);
                // Preserve characters already consumed even if cancellation arrives at native completion.
                if (count > 0) return (Data: data, Count: checked((int)count));
                // Windows can settle an interrupted ReadConsoleW successfully with zero characters.
                // With no consumed input, caller cancellation or closing must still settle as cancellation.
                if (success) token.ThrowIfCancellationRequested();
                if (!success) { if (error == 995) token.ThrowIfCancellationRequested(); throw Error(TerminalFailure.NativeIoFailed); }
                return (Data: data, Count: 0);
            }, caller).ConfigureAwait(false);
            value.Data.AsMemory(0, value.Count).CopyTo(destination); return value.Count;
        }
        finally { lock (gate) { activeReads = 0; readSettled++; } }
    }
    public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (frame.Length > options.MaximumWriteCharacters) throw Error(TerminalFailure.InvalidOptions);
        ValidateUnicode(frame.Span); TaskCompletionSource admission; Task operation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            if (frame.IsEmpty) return ValueTask.CompletedTask;
            if (activeWrites != 0) throw new InvalidOperationException("Terminal permits one actual output frame.");
            var owned = frame.ToArray(); admission = NewGate(); activeWrites = 1; writeStarted++; operation = WriteOwned(owned, token, admission.Task); write = operation;
        }
        admission.TrySetResult(); return new(operation);
    }
    private async Task WriteOwned(char[] frame, CancellationToken caller, Task admission)
    {
        await admission.ConfigureAwait(false);
        try
        {
            await Worker(token =>
            {
                using var active = options.FollowActiveScreenBuffer ? OpenActiveOutput() : null;
                var position = 0;
                while (position < frame.Length)
                {
                    token.ThrowIfCancellationRequested(); var length = Math.Min(4096, frame.Length - position);
                    if (char.IsHighSurrogate(frame[position + length - 1])) length--;
                    var data = frame.AsSpan(position, length).ToArray();
                    var success = Native.WriteConsoleW(active ?? output, data, (uint)data.Length, out var count, IntPtr.Zero); var error = success ? 0 : Marshal.GetLastPInvokeError();
                    if (count > data.Length) throw Error(TerminalFailure.NativeIoFailed);
                    position += checked((int)count);
                    if (!success) { if (error == 995) token.ThrowIfCancellationRequested(); throw Error(TerminalFailure.NativeIoFailed); }
                    if (count == 0) throw Error(TerminalFailure.NativeIoFailed);
                }
                if (options.FollowActiveScreenBuffer)
                {
                    // Conhost's alternate-buffer API cursor can remain hidden after DECTCEM output.
                    // The explicit full-screen profile also applies this final trusted transport command
                    // with its equivalent native API, before the owned write is physically settled.
                    bool? visible = frame.AsSpan().EndsWith("\u001b[?25h", StringComparison.Ordinal) ? true :
                        frame.AsSpan().EndsWith("\u001b[?25l", StringComparison.Ordinal) ? false : null;
                    if (visible is not null)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Native.GetConsoleCursorInfo(active ?? output, out var cursor)) throw Error(TerminalFailure.NativeIoFailed);
                        cursor.Visible = visible.Value;
                        if (!Native.SetConsoleCursorInfo(active ?? output, ref cursor)) throw Error(TerminalFailure.NativeIoFailed);
                    }
                }
                return 0;
            }, caller).ConfigureAwait(false);
        }
        finally { Array.Clear(frame); lock (gate) { activeWrites = 0; writeSettled++; } }
    }
    private async Task<T> Worker<T>(Func<CancellationToken, T> operation, CancellationToken caller)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, stopToken);
        var ready = NewGate(); var done = NewGate(); var canceled = NewGate(); SafeFileHandle? thread = null;
        var registration = linked.Token.UnsafeRegister(_ => canceled.TrySetResult(), null);
        var worker = Task.Factory.StartNew(() =>
        {
            try
            {
                linked.Token.ThrowIfCancellationRequested(); var process = Native.GetCurrentProcess();
                if (!Native.DuplicateHandle(process, Native.GetCurrentThread(), process, out thread, 0, false, 2) || thread.IsInvalid)
                    throw Error(TerminalFailure.NativeIoFailed);
                ready.TrySetResult(); return operation(linked.Token);
            }
            finally { ready.TrySetResult(); done.TrySetResult(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var canceling = CancelWorker();
        try { return await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { throw new ObjectDisposedException(nameof(WindowsConsoleTerminal)); }
        finally { await canceling.ConfigureAwait(false); await registration.DisposeAsync().ConfigureAwait(false); thread?.Dispose(); }

        async Task CancelWorker()
        {
            await Task.WhenAny(done.Task, canceled.Task).ConfigureAwait(false); if (done.Task.IsCompleted) return;
            await ready.Task.ConfigureAwait(false);
            // ERROR_NOT_FOUND can mean cancellation raced immediately before the synchronous native call.
            // Repeat until the real dedicated worker settles; never abandon it behind WaitAsync.
            while (!done.Task.IsCompleted)
            {
                if (thread is not null && !thread.IsInvalid) _ = Native.CancelSynchronousIo(thread);
                await Task.WhenAny(done.Task, Task.Delay(10)).ConfigureAwait(false);
            }
        }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task[] pending = []; Task settled;
        lock (gate)
        {
            if (disposal is null) { completion = NewGate(); disposal = completion.Task; pending = new[] { read, write }.OfType<Task>().ToArray(); }
            settled = disposal;
        }
        if (completion is not null) _ = Close(pending, completion); return new(settled);
    }
    private async Task Close(Task[] pending, TaskCompletionSource completion)
    {
        var failed = false;
        try
        {
            try { stop.Cancel(); } catch (Exception) { failed = true; }
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception) { /* Operation failures belong to their actual callers. */ }
            if (!Restore(input, output, original)) failed = true;
            try { var actual = Inspect(input, output); lock (gate) restored = actual; if (actual != original) failed = true; } catch (Exception) { failed = true; }
        }
        catch (Exception) { failed = true; }
        finally
        {
            input.Dispose(); output.Dispose(); stop.Dispose(); Volatile.Write(ref leased, 0);
            if (failed) completion.TrySetException(Error(TerminalFailure.RestorationFailed)); else completion.TrySetResult();
        }
    }
    private static bool Restore(SafeFileHandle input, SafeFileHandle output, TerminalConsoleState value)
    {
        var success = Native.SetConsoleMode(input, value.InputMode); success = Native.SetConsoleMode(output, value.OutputMode) && success;
        var cursor = new CursorInfo { Size = value.CursorSize, Visible = value.CursorVisible }; success = Native.SetConsoleCursorInfo(output, ref cursor) && success;
        return Native.SetConsoleCursorPosition(output, new(value.CursorColumn, value.CursorRow)) && success;
    }
    private static TerminalConsoleState Inspect(SafeFileHandle input, SafeFileHandle output)
    {
        if (input.IsInvalid || output.IsInvalid || !Native.GetConsoleMode(input, out var inputMode) || !Native.GetNumberOfConsoleInputEvents(input, out _) || !Native.GetConsoleMode(output, out var outputMode) ||
            !Native.GetConsoleCursorInfo(output, out var cursor) || !Native.GetConsoleScreenBufferInfo(output, out var buffer)) throw Error(TerminalFailure.NotConsole);
        return new(inputMode, outputMode, Native.GetConsoleCP(), Native.GetConsoleOutputCP(), cursor.Size, cursor.Visible, buffer.Cursor.X, buffer.Cursor.Y);
    }
    private static SafeFileHandle Duplicate(SafeFileHandle borrowed)
    {
        var held = false;
        try
        {
            borrowed.DangerousAddRef(ref held); if (borrowed.IsInvalid || borrowed.IsClosed) throw Error(TerminalFailure.NotConsole);
            var process = Native.GetCurrentProcess();
            if (!Native.DuplicateHandle(process, borrowed.DangerousGetHandle(), process, out var copy, 0, false, 2)) throw Error(TerminalFailure.NotConsole);
            if (copy.IsInvalid) { copy.Dispose(); throw Error(TerminalFailure.NotConsole); } return copy;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException) { throw Error(TerminalFailure.NotConsole); }
        finally { if (held) borrowed.DangerousRelease(); }
    }
    private static SafeFileHandle OpenActiveOutput()
    {
        var handle = Native.CreateFileW("CONOUT$", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid && Native.GetConsoleMode(handle, out _)) return handle;
        handle.Dispose(); throw Error(TerminalFailure.NativeIoFailed);
    }
    private static void Windows() { if (!OperatingSystem.IsWindows()) throw Error(TerminalFailure.UnsupportedPlatform); }
    private static void Validate(TerminalLeaseOptions value)
    { if (value.MaximumReadCharacters is < 1 or > 65_536 || value.MaximumWriteCharacters is < 1 or > 1_048_576) throw Error(TerminalFailure.InvalidOptions); }
    private static void ValidateUnicode(ReadOnlySpan<char> value)
    { for (var index = 0; index < value.Length; index++) if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Error(TerminalFailure.NativeIoFailed); } else if (char.IsLowSurrogate(value[index])) throw Error(TerminalFailure.NativeIoFailed); }
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TerminalException Error(TerminalFailure failure) => new(failure);
    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size; [MarshalAs(UnmanagedType.Bool)] public bool Visible; }
    [StructLayout(LayoutKind.Sequential)] private struct SmallRect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public ushort Attributes; public SmallRect Window; public Coord MaximumWindowSize; }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(uint id);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] internal static extern uint GetConsoleCP();
        [DllImport("kernel32.dll")] internal static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out SafeFileHandle copy, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNumberOfConsoleInputEvents(SafeFileHandle handle, out uint count);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleMode(SafeFileHandle handle, uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleCursorInfo(SafeFileHandle handle, out CursorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCursorInfo(SafeFileHandle handle, ref CursorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleScreenBufferInfo(SafeFileHandle handle, out BufferInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCursorPosition(SafeFileHandle handle, Coord position);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadConsoleW(SafeFileHandle handle, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] data, uint count, out uint read, IntPtr control);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WriteConsoleW(SafeFileHandle handle, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] data, uint count, out uint written, IntPtr reserved);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CancelSynchronousIo(SafeFileHandle thread);
    }
}
