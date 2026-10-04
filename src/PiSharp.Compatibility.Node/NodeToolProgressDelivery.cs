using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Compatibility.Node;

/// <summary>One invocation-bound update sink. The worker dispatcher additionally owns its callback handle and lease.</summary>
internal sealed class NodeToolProgressDelivery(string operationId, string callbackId, IExtensionToolInvocationContext context)
{
    private readonly object gate = new();
    private int count, bytes;
    private bool pending, failed, closed;
    internal void Close()
    {
        lock (gate) { if (pending) throw new InvalidOperationException("Node progress delivery has not joined."); closed = true; }
    }
    internal async ValueTask<int> DeliverAsync(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fields = value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        if (!fields.SequenceEqual(new[] { "callbackId", "operationId", "partialResultJson", "sequence", "toolCallId" }.Order(StringComparer.Ordinal)) ||
            value.GetProperty("operationId").GetString() != operationId || value.GetProperty("callbackId").GetString() != callbackId ||
            value.GetProperty("toolCallId").GetString() != context.ToolCallId)
            throw new InvalidOperationException("Foreign or malformed Node progress identity.");
        var sequence = value.GetProperty("sequence").GetInt32();
        var raw = value.GetProperty("partialResultJson").GetString() ?? throw new InvalidOperationException("Node progress JSON required.");
        var length = Encoding.UTF8.GetByteCount(raw);
        if (length > 65_536) throw new IOException("Node progress frame budget.");
        var update = JsonData.Parse(raw);
        if (update.Value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Node progress result object required.");
        lock (gate)
        {
            if (closed || failed || pending || sequence != count + 1 || count >= 16 || length > 262_144 - bytes)
                throw new InvalidOperationException("Node progress sequence/backpressure/budget.");
            pending = true; count++; bytes += length;
        }
        try
        {
            await context.ReportUpdateAsync(update, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return sequence;
        }
        catch { lock (gate) failed = true; throw; }
        finally { lock (gate) pending = false; }
    }
}
