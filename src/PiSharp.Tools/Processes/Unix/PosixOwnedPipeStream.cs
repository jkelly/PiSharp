using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Tools.Processes.Unix;

// OwnedChildProcess must be able to retire a pipe even when an escaped process keeps its other end open. A blocking
// FileStream read/write cannot provide that contract: closing the descriptor does not interrupt an in-flight Unix syscall.
internal sealed class PosixOwnedPipeStream : Stream
{
    private readonly object gate = new();
    private readonly SafeFileHandle handle;
    private readonly bool writer;
    private readonly CancellationTokenSource closed = new();
    private bool disposed;

    internal PosixOwnedPipeStream(int descriptor, bool writer)
    {
        var flags = Native.GetFlags(descriptor, 3); // F_GETFL
        if (flags < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var nonblock = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
        // Darwin arm64 puts *variadic* arguments on the stack. Fill x2-x7 so the third C argument is the first stack slot.
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? Native.SetFlagsAppleArm64(descriptor, 4, 0, 0, 0, 0, 0, 0, flags | nonblock)
            : Native.SetFlags(descriptor, 4, flags | nonblock); // F_SETFL
        if (result != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nonblocking pipe configuration failed.");
        var configured = Native.GetFlags(descriptor, 3);
        if (configured < 0 || (configured & nonblock) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Nonblocking pipe configuration could not be verified.");
        handle = new((nint)descriptor, ownsHandle: true); this.writer = writer;
    }

    public override bool CanRead => !disposed && !writer;
    public override bool CanWrite => !disposed && writer;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { lock (gate) ObjectDisposedException.ThrowIf(disposed, this); }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (writer) throw new NotSupportedException();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
        var bytes = new byte[Math.Min(buffer.Length, 64 * 1024)];
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            int error;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var count = Native.Read(handle, bytes, (nuint)bytes.Length);
                if (count >= 0) { bytes.AsSpan(0, (int)count).CopyTo(buffer.Span); return (int)count; }
                error = Marshal.GetLastPInvokeError();
            }
            if (error == 4) continue; // EINTR
            if (!WouldBlock(error)) throw new IOException("Owned pipe read failed.", new Win32Exception(error));
            await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!writer) throw new NotSupportedException();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
        var offset = 0;
        do
        {
            var bytes = buffer.Slice(offset, Math.Min(buffer.Length - offset, 64 * 1024)).ToArray();
            var remaining = bytes.Length;
            do
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int error;
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    var count = Native.Write(handle, bytes, (nuint)remaining);
                    if (count >= 0)
                    {
                        offset += (int)count; remaining -= (int)count;
                        if (remaining == 0) break;
                        Array.Copy(bytes, (int)count, bytes, 0, remaining);
                        error = OperatingSystem.IsMacOS() ? 35 : 11;
                    }
                    else error = Marshal.GetLastPInvokeError();
                }
                if (error == 4) continue;
                if (!WouldBlock(error)) throw new IOException("Owned pipe write failed.", new Win32Exception(error));
                await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
            } while (remaining > 0);
        } while (offset < buffer.Length);
    }

    private static bool WouldBlock(int error) => error == (OperatingSystem.IsMacOS() ? 35 : 11);
    protected override void Dispose(bool disposing)
    {
        lock (gate) { if (disposed) return; disposed = true; handle.Dispose(); }
        closed.Cancel();
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private static class Native
    {
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int GetFlags(int descriptor, int command);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int SetFlags(int descriptor, int command, int flags);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] internal static extern int SetFlagsAppleArm64(int descriptor, int command,
            nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7, nint flags);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)] internal static extern nint Read(SafeFileHandle descriptor, [Out] byte[] buffer, nuint length);
        [DllImport("libc", EntryPoint = "write", SetLastError = true)] internal static extern nint Write(SafeFileHandle descriptor, byte[] buffer, nuint length);
    }
}
