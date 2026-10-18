// PiSharp-only (decision 0008). Server-Sent Events as the durable network sends them (durable-pi apps/dpi-backend/src/streams.ts, MIT): each
// frame is `id: <seq>`, `event: <type>`, `data: <json>` and a blank line; `: ping` comments keep the connection alive every 15 s. A hub's
// browser stream sends `data:` lines only.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Durable.Client;

/// <summary>One frame: its sequence number, its event name, and its JSON body (which carries <c>type</c>).</summary>
public sealed record SseFrame(long? Id, string? Event, JsonObject Data)
{
    /// <summary>The body's <c>type</c>, or the event name.</summary>
    public string Type => Data["type"]?.GetValue<string>() ?? Event ?? "";
}

/// <summary>Reads frames from an event stream.</summary>
public static class SseReader
{
    /// <summary>Frames until the stream ends. <paramref name="onActivity"/> runs for every line, comments included, so a caller can tell a
    /// live connection from a dead one.</summary>
    public static async IAsyncEnumerable<SseFrame> ReadAsync(Stream stream, Action? onActivity = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16384, leaveOpen: true);
        long? id = null;
        string? name = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) yield break;
            onActivity?.Invoke();
            if (line.Length == 0)
            {
                if (data.Length > 0 && JsonNode.Parse(data.ToString()) is JsonObject body) yield return new SseFrame(id, name, body);
                id = null;
                name = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
                case "event":
                    name = value;
                    break;
                case "id":
                    id = long.TryParse(value, out var number) ? number : null;
                    break;
            }
        }
    }

    /// <summary>A frame as the device writes it.</summary>
    public static string Format(long id, string type, string json) => $"id: {id}\nevent: {type}\ndata: {json}\n\n";
}
