using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace PiSharp.Chord.Legacy;

/// <summary>
/// Native .NET pipe transport: u32 big-endian byte length, one byte kind (0=text, 1=binary), then WebSocket encoding.
/// This explicit framing is for .NET peers; Node IPC uses <see cref="LegacyIpcLink"/> and its Node serialization adapter.
/// </summary>
public sealed class LegacyPipeLink : LegacyLink
{
    private readonly Stream stream;
    private readonly object writeGate = new();
    private Task writes = Task.CompletedTask;
    private readonly CancellationTokenSource stopped = new();
    private readonly bool compress, acceptPacked;
    private readonly int maximumMessageBytes;
    public LegacyPipeLink(Stream stream, bool compress = true, bool acceptPacked = true, int maximumMessageBytes = int.MaxValue)
    {
        if (!stream.CanRead || !stream.CanWrite) throw new ArgumentException("A duplex stream is required", nameof(stream));
        if (maximumMessageBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        this.stream = stream; this.compress = compress; this.acceptPacked = acceptPacked; this.maximumMessageBytes = maximumMessageBytes;
        _ = ReadAsync();
    }
    protected override long? Transmit(LegacyFrame frame)
    {
        var message = LegacyCodec.Encode(frame, compress);
        var bytes = message.Binary ?? Encoding.UTF8.GetBytes(message.Text!);
        if (bytes.Length > maximumMessageBytes) throw new InvalidDataException("pipe frame exceeds limit");
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)bytes.Length)); header[4] = message.IsBinary ? (byte)1 : (byte)0;
        lock (writeGate) writes = writes.ContinueWith(async _ =>
        {
            try
            {
                await stream.WriteAsync(header, stopped.Token).ConfigureAwait(false);
                await stream.WriteAsync(bytes, stopped.Token).ConfigureAwait(false);
                await stream.FlushAsync(stopped.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            { End("pipe send failed: " + error.Message); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return bytes.Length + 5;
    }
    private async Task ReadAsync()
    {
        try
        {
            var header = new byte[5];
            while (!stopped.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, stopped.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length > maximumMessageBytes || header[4] > 1) throw new FormatException("invalid pipe frame");
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, stopped.Token).ConfigureAwait(false);
                Deliver(LegacyCodec.Decode(header[4] == 1 ? new(null, bytes) : new(Encoding.UTF8.GetString(bytes), null), acceptPacked));
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (EndOfStreamException) { End("pipe closed"); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { End("pipe closed"); }
        catch (Exception) { Close("protocol error"); }
    }
    protected override void Shutdown() => _ = DrainAndCloseAsync();
    private async Task DrainAndCloseAsync()
    {
        Task pending; lock (writeGate) pending = writes;
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        stopped.Cancel(); stream.Dispose();
    }
}

/// <summary>Constructs protocol4 links around actual System.Net.WebSockets connections.</summary>
public static class LegacyNativeWebSocket
{
    public static LegacyWebSocketLink Create(WebSocket socket, bool compress = false, bool acceptPacked = true, int maximumMessageBytes = int.MaxValue)
    {
        if (maximumMessageBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        var adapter = new Adapter(socket, maximumMessageBytes);
        var link = new LegacyWebSocketLink(adapter, compress, acceptPacked);
        adapter.Start(); return link;
    }
    private sealed class Adapter(WebSocket socket, int maximumMessageBytes) : ILegacyWebSocket
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource stop = new();
        private Task sending = Task.CompletedTask;
        private int ended;
        public int ReadyState => Volatile.Read(ref ended) != 0 ? 3 : socket.State switch
        { WebSocketState.Open => 1, WebSocketState.Connecting or WebSocketState.None => 0, _ => 2 };
        public event Action? Opened;
        public event Action<LegacyMessage>? Message;
        public event Action? Closed;
        internal void Start() { if (ReadyState == 1) Opened?.Invoke(); _ = ReadAsync(); }
        public void Send(LegacyMessage message)
        {
            var bytes = message.Binary ?? Encoding.UTF8.GetBytes(message.Text!);
            lock (gate) sending = sending.ContinueWith(async _ =>
            {
                try { await socket.SendAsync(bytes.AsMemory(), message.IsBinary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, stop.Token).ConfigureAwait(false); }
                catch (Exception) { End(); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
        private async Task ReadAsync()
        {
            try
            {
                var buffer = new byte[8192];
                while (!stop.IsCancellationRequested)
                {
                    using var bytes = new MemoryStream(); ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) { End(); return; }
                        if (bytes.Length + result.Count > maximumMessageBytes) throw new FormatException("socket frame exceeds limit");
                        bytes.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    var data = bytes.ToArray();
                    Message?.Invoke(result.MessageType == WebSocketMessageType.Binary ? new(null, data) : new(Encoding.UTF8.GetString(data), null));
                }
            }
            catch (Exception) { End(); }
        }
        public void Close(int code) => _ = CloseAsync(code);
        private async Task CloseAsync(int code)
        {
            Task pending; lock (gate) pending = sending;
            try
            {
                await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                if (pending.IsCompleted && socket.State == WebSocketState.Open)
                    await socket.CloseOutputAsync((WebSocketCloseStatus)code, null, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception) { }
            finally { End(); }
        }
        private void End()
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            stop.Cancel(); socket.Dispose(); Closed?.Invoke();
        }
    }
}
