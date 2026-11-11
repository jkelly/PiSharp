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
    { var result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false); await CheckAsync(result.Count, result.EndOfMessage, cancellationToken).ConfigureAwait(false); return result; }
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false); await CheckAsync(result.Count, result.EndOfMessage, cancellationToken).ConfigureAwait(false); return result; }
    private async Task CheckAsync(int count, bool end, CancellationToken cancellationToken)
    {
        received += count;
        if (received > limit)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            try { await inner.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message exceeds admitted payload limit", deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { inner.Abort(); }
            throw new WebSocketException("WebSocket message exceeds admitted payload limit");
        }
        if (end) received = 0;
    }
}
