using System.Net.WebSockets;

namespace PiSharp.Chord.Hosting.Http;

internal sealed class LimitedWebSocket(WebSocket inner, int limit) : WebSocket
{
    private long received;
    public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
    public override string? CloseStatusDescription => inner.CloseStatusDescription;
    public override WebSocketState State => inner.State;
    public override string? SubProtocol => inner.SubProtocol;
    public override void Abort() => inner.Abort();
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
    public override void Dispose() { }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    { var result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false); Check(result.Count, result.EndOfMessage); return result; }
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false); Check(result.Count, result.EndOfMessage); return result; }
    private void Check(int count, bool end)
    {
        received += count;
        if (received > limit) { inner.Abort(); throw new WebSocketException("WebSocket message exceeds admitted payload limit"); }
        if (end) received = 0;
    }
}
