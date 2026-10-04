using System.Text;

namespace PiSharp.Cli.Output;

/// <summary>Unbuffered, strict UTF-8 output. The underlying writable stream is borrowed.</summary>
public sealed class Utf8StreamTextWriter : TextWriter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Stream stream;
    private readonly int maximumWriteBytes;
    private int active, disposed, failed;

    public Utf8StreamTextWriter(Stream stream, int maximumWriteBytes = 8_388_608)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("A writable stream is required.", nameof(stream));
        if (maximumWriteBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWriteBytes));
        this.stream = stream;
        this.maximumWriteBytes = maximumWriteBytes;
    }

    public override Encoding Encoding => Utf8;

    public override void Write(char value) => Write(value.ToString());
    public override void Write(string? value) => Write(value.AsSpan());
    public override void Write(ReadOnlySpan<char> value)
    {
        Enter();
        try
        {
            var bytes = Encode(value);
            try { stream.Write(bytes); }
            catch { Volatile.Write(ref failed, 1); throw; }
        }
        finally { Volatile.Write(ref active, 0); }
    }
    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));
    public override Task WriteAsync(char value) => WriteAsync(value.ToString());
    public override Task WriteAsync(string? value) => WriteAsync(value.AsMemory(), CancellationToken.None);
    public override Task WriteAsync(char[] buffer, int index, int count) => WriteAsync(buffer.AsMemory(index, count));

    public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Encode(buffer.Span);
            try { await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
            catch { Volatile.Write(ref failed, 1); throw; }
        }
        finally { Volatile.Write(ref active, 0); }
    }

    public override Task WriteLineAsync(string? value) => WriteLineAsync(value.AsMemory(), CancellationToken.None);
    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        if ((long)buffer.Length + NewLine.Length > maximumWriteBytes) throw Limit();
        return WriteAsync((buffer.ToString() + NewLine).AsMemory(), cancellationToken);
    }

    public override void Flush()
    {
        Enter();
        try
        {
            try { stream.Flush(); }
            catch { Volatile.Write(ref failed, 1); throw; }
        }
        finally { Volatile.Write(ref active, 0); }
    }
    public override Task FlushAsync() => FlushAsync(CancellationToken.None);
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await stream.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch { Volatile.Write(ref failed, 1); throw; }
        }
        finally { Volatile.Write(ref active, 0); }
    }

    private byte[] Encode(ReadOnlySpan<char> value)
    {
        if (value.Length > maximumWriteBytes) throw Limit();
        var count = Utf8.GetByteCount(value);
        if (count > maximumWriteBytes) throw Limit();
        var bytes = new byte[count];
        Utf8.GetBytes(value, bytes);
        return bytes;
    }
    private void Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref failed) != 0) throw new IOException("Output stream delivery has failed.");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent output operations are unsupported.");
        if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref failed) == 0) return;
        Volatile.Write(ref active, 0);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        throw new IOException("Output stream delivery has failed.");
    }
    private static IOException Limit() => new("Output write exceeds configured bounds.");

    // There is no buffer to flush. In particular, disposal must not retry delivery after a failure.
    protected override void Dispose(bool disposing) => Volatile.Write(ref disposed, 1);
    public override ValueTask DisposeAsync()
    {
        Volatile.Write(ref disposed, 1);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
