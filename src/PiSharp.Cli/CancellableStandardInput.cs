using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Cli;

/// <summary>
/// CLI-owned, single-consumer synchronous Windows pipe input. Only a duplicate is owned;
/// callers must not concurrently read the underlying borrowed pipe through another handle.
/// </summary>
public sealed class CancellableStandardInput : Stream
{
    private readonly SafeFileHandle _handle;
    private readonly CancellationTokenSource _closing = new();
    private readonly object _gate = new();
    private Task? _activeRead;
    private Task? _disposal;

    private CancellableStandardInput(SafeFileHandle handle) => _handle = handle;

    /// <summary>Uses owned pipe cancellation on Windows; other input profiles retain framework behavior.</summary>
    public static Stream Open()
    {
        if (!OperatingSystem.IsWindows()) return Console.OpenStandardInput();
        var handle = Native.GetStdHandle(unchecked((uint)-10));
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw Unavailable();
        if (Native.GetFileType(handle) != 3) return Console.OpenStandardInput();
        using var borrowed = new SafeFileHandle(handle, ownsHandle: false);
        return DuplicateSynchronousPipe(borrowed);
    }

    /// <summary>
    /// Duplicates an existing readable synchronous pipe without taking its ownership or changing its mode.
    /// The borrowed handle must remain the sole underlying read consumer while this stream is in use.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static CancellableStandardInput DuplicateSynchronousPipe(SafeHandle borrowedHandle)
    {
        ArgumentNullException.ThrowIfNull(borrowedHandle);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows pipe input is required.");
        var held = false;
        SafeFileHandle? duplicate = null;
        try
        {
            borrowedHandle.DangerousAddRef(ref held);
            if (borrowedHandle.IsInvalid || Native.GetFileType(borrowedHandle.DangerousGetHandle()) != 3)
                throw Unavailable();
            var process = Native.GetCurrentProcess();
            if (!Native.DuplicateHandle(process, borrowedHandle.DangerousGetHandle(), process, out duplicate,
                    0, false, 2) || duplicate.IsInvalid) throw Unavailable();
            var input = new CancellableStandardInput(duplicate);
            duplicate = null;
            return input;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException)
        { throw Unavailable(); }
        finally
        {
            duplicate?.Dispose();
            if (held) borrowedHandle.DangerousRelease();
        }
    }

    public override bool CanRead { get { lock (_gate) return _disposal is null; } }
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource admitted;
        Task<int> read;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_activeRead is { IsCompleted: false }) throw new InvalidOperationException("Standard input already has an active read.");
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return ValueTask.FromResult(0);
            admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // The admission gate prevents native calls while this lock is held. Retain the
            // actual read Task so disposal joins its terminal result, including its finally.
            read = ReadOwnedAsync(buffer, cancellationToken, admitted.Task).AsTask();
            _activeRead = read;
        }
        admitted.TrySetResult();
        return new(read);
    }

    private async ValueTask<int> ReadOwnedAsync(Memory<byte> destination, CancellationToken caller,
        Task admitted)
    {
        await admitted.ConfigureAwait(false);
        CancellationTokenSource? stop = null;
        try
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(caller, _closing.Token);
            var bytes = new byte[Math.Min(destination.Length, 8192)];
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                if (!Native.PeekNamedPipe(_handle, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero))
                {
                    if (Eof(Marshal.GetLastPInvokeError())) return 0;
                    throw Unavailable();
                }
                if (available > 0)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var requested = Math.Min(available, (uint)bytes.Length);
                    var read = Native.ReadFile(_handle, bytes, requested, out var count, IntPtr.Zero);
                    var error = read ? 0 : Marshal.GetLastPInvokeError();
                    if (count > requested) throw Unavailable();
                    if (!read && error != 234)
                    {
                        if (Eof(error)) return 0;
                        throw Unavailable();
                    }
                    if (count > 0)
                    {
                        // These bytes were consumed. A late cancellation cannot turn them into a lost read.
                        bytes.AsMemory(0, checked((int)count)).CopyTo(destination);
                        return checked((int)count);
                    }
                }
                await Task.Delay(10, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        { throw new OperationCanceledException(caller); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        { throw new ObjectDisposedException(nameof(CancellableStandardInput)); }
        finally
        {
            stop?.Dispose();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask DisposeAsync() => new(BeginDisposal());
    protected override void Dispose(bool disposing)
    {
        if (disposing) BeginDisposal().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    private Task BeginDisposal()
    {
        TaskCompletionSource settled;
        Task? read;
        lock (_gate)
        {
            if (_disposal is not null) return _disposal;
            settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = settled.Task;
            read = _activeRead;
        }
        // Neither cancellation callbacks nor I/O cleanup run while the coordination lock is held.
        _ = DisposeOwnedAsync(read, settled);
        return settled.Task;
    }

    private async Task DisposeOwnedAsync(Task? read, TaskCompletionSource settled)
    {
        try
        {
            _closing.Cancel();
            if (read is not null)
            {
                // The read's failure belongs to its caller. Disposal still joins its actual completion.
                try { await read.ConfigureAwait(false); } catch (Exception) { }
            }
            _handle.Dispose();
            _closing.Dispose();
            base.Dispose(true);
            GC.SuppressFinalize(this);
            settled.TrySetResult();
        }
        catch (Exception) { settled.TrySetException(Unavailable()); }
    }

    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private static bool Eof(int error) => error is 109 or 232 or 233;
    private static IOException Unavailable() => new("Standard input pipe is unavailable.");
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetStdHandle(uint identifier);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out SafeFileHandle duplicate, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekNamedPipe(SafeFileHandle handle, IntPtr buffer, uint length,
            IntPtr copied, out uint available, IntPtr left);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeFileHandle handle, [Out] byte[] buffer, uint length,
            out uint read, IntPtr overlapped);
    }
}
