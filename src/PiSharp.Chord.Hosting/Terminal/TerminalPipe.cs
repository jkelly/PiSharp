// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/tui/pipe.ts.
// Native adapters retain explicit trusted-code and terminal ownership boundaries.
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Hosting.Terminal;

/// <summary>The upstream terminal protocol: a big-endian frame length, header length, JSON header and raw body.</summary>
public static class TerminalPipe
{
    public const int MaximumFrameBytes = 17 * 1024 * 1024;
    public const int MaximumHeaderBytes = 1024 * 1024;
    private const long MaximumSafeInteger = 9007199254740991;

    public static byte[] Encode(ClientMessage message) => message switch
    {
        ClientMessage.Write m => Frame(new() { ["kind"] = "write" }, Encoding.UTF8.GetBytes(m.Data)),
        ClientMessage.SocketSend m => Frame(new() { ["kind"] = "socket_send", ["socket"] = m.Socket }, m.Bytes.Span),
        ClientMessage.SocketOpen m => Frame(new() { ["kind"] = "socket_open", ["socket"] = m.Socket }),
        ClientMessage.SocketClose m => Frame(new() { ["kind"] = "socket_close", ["socket"] = m.Socket }),
        ClientMessage.Request m => Frame(new() { ["kind"] = "request", ["id"] = m.Id, ["request"] = RequestHeader(m.Value) }),
        ClientMessage.Outdated => Frame(new() { ["kind"] = "outdated" }),
        ClientMessage.Failed m => Frame(new() { ["kind"] = "failed", ["message"] = m.Message }),
        ClientMessage.Exit m => Frame(new() { ["kind"] = "exit", ["code"] = m.Code }),
        _ => throw Malformed()
    };

    public static byte[] Encode(LauncherMessage message) => message switch
    {
        LauncherMessage.Input m => Frame(new() { ["kind"] = "input" }, Encoding.UTF8.GetBytes(m.Data)),
        LauncherMessage.Resize m => Frame(new() { ["kind"] = "resize", ["columns"] = m.Columns, ["rows"] = m.Rows }),
        LauncherMessage.SocketOpened m => Frame(new() { ["kind"] = "socket_opened", ["socket"] = m.Socket }),
        LauncherMessage.SocketMessage m => Frame(new() { ["kind"] = "socket_message", ["socket"] = m.Socket }, m.Bytes.Span),
        LauncherMessage.SocketClosed m => Frame(new() { ["kind"] = "socket_closed", ["socket"] = m.Socket }),
        LauncherMessage.Reply m => Frame(new() { ["kind"] = "reply", ["id"] = m.Id, ["value"] = m.Value?.DeepClone() }),
        LauncherMessage.Attached m => Frame(new() { ["kind"] = "attached", ["id"] = m.Id, ["name"] = m.Name }, m.Bytes.Span),
        LauncherMessage.Refused m => Frame(new() { ["kind"] = "refused", ["id"] = m.Id, ["message"] = m.Message }),
        _ => throw Malformed()
    };

    public static byte[] EncodeStart(TerminalStart start, string source) => Frame(new()
    {
        ["kind"] = "start", ["start"] = new JsonObject
        {
            ["base"] = start.Base, ["combined"] = start.Combined, ["columns"] = start.Columns, ["rows"] = start.Rows,
            ["colors"] = start.Colors, ["mode"] = start.Mode,
            ["locales"] = new JsonArray((start.Locales ?? []).Select(locale => (JsonNode?)JsonValue.Create(locale)).ToArray())
        }
    }, Encoding.UTF8.GetBytes(source));

    private static JsonObject RequestHeader(TerminalRequest request) => request switch
    {
        TerminalRequest.Base => new() { ["kind"] = "base" },
        TerminalRequest.Fetch m => new() { ["kind"] = "fetch", ["path"] = m.Path },
        TerminalRequest.Copy m => new() { ["kind"] = "copy", ["text"] = m.Text },
        TerminalRequest.Open m => new() { ["kind"] = "open", ["url"] = m.Url },
        TerminalRequest.Attach m => new() { ["kind"] = "attach", ["path"] = m.Path },
        _ => throw Malformed()
    };

    private static byte[] Frame(JsonObject header, ReadOnlySpan<byte> body = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        if (json.Length > MaximumHeaderBytes || body.Length > MaximumFrameBytes - 4 - json.Length) throw Malformed();
        var bytes = new byte[8 + json.Length + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, bytes.Length - 4);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), json.Length);
        json.CopyTo(bytes.AsSpan(8)); body.CopyTo(bytes.AsSpan(8 + json.Length)); return bytes;
    }

    internal static (JsonObject Header, ReadOnlyMemory<byte> Body) Parse(byte[] payload)
    {
        if (payload.Length < 4) throw Malformed();
        var length = BinaryPrimitives.ReadInt32BigEndian(payload);
        if (length < 0 || length > MaximumHeaderBytes || length > payload.Length - 4) throw Malformed();
        try
        {
            var header = JsonNode.Parse(payload.AsSpan(4, length)) as JsonObject ?? throw Malformed();
            return (header, payload.AsMemory(4 + length));
        }
        catch (JsonException error) { throw new IOException("terminal: malformed frame header", error); }
    }

    internal static ClientMessage Client(byte[] payload)
    {
        var (header, body) = Parse(payload);
        return String(header, "kind") switch
        {
            "write" => new ClientMessage.Write(Encoding.UTF8.GetString(body.Span)),
            "socket_send" => new ClientMessage.SocketSend(Integer(header, "socket"), body),
            "socket_open" => new ClientMessage.SocketOpen(Integer(header, "socket")),
            "socket_close" => new ClientMessage.SocketClose(Integer(header, "socket")),
            "request" => new ClientMessage.Request(Integer(header, "id"), Request(header["request"] as JsonObject ?? throw Malformed())),
            "outdated" => new ClientMessage.Outdated(),
            "failed" => new ClientMessage.Failed(header["message"]?.ToString() ?? ""),
            "exit" => new ClientMessage.Exit(header["code"] is JsonValue v && v.TryGetValue<int>(out var code) ? code : 1),
            _ => throw Malformed()
        };
    }

    internal static LauncherMessage Launcher(byte[] payload)
    {
        var (header, body) = Parse(payload);
        return String(header, "kind") switch
        {
            "input" => new LauncherMessage.Input(Encoding.UTF8.GetString(body.Span)),
            "resize" => new LauncherMessage.Resize(checked((int)Integer(header, "columns")), checked((int)Integer(header, "rows"))),
            "socket_opened" => new LauncherMessage.SocketOpened(Integer(header, "socket")),
            "socket_message" => new LauncherMessage.SocketMessage(Integer(header, "socket"), body),
            "socket_closed" => new LauncherMessage.SocketClosed(Integer(header, "socket")),
            "reply" => new LauncherMessage.Reply(Integer(header, "id"), header["value"]?.DeepClone()),
            "attached" => new LauncherMessage.Attached(Integer(header, "id"), String(header, "name"), body),
            "refused" => new LauncherMessage.Refused(Integer(header, "id"), String(header, "message")),
            _ => throw Malformed()
        };
    }

    private static TerminalRequest Request(JsonObject request) => String(request, "kind") switch
    {
        "base" => new TerminalRequest.Base(), "fetch" => new TerminalRequest.Fetch(String(request, "path")),
        "copy" => new TerminalRequest.Copy(String(request, "text")), "open" => new TerminalRequest.Open(String(request, "url")),
        "attach" => new TerminalRequest.Attach(String(request, "path")), _ => throw Malformed()
    };
    private static string String(JsonObject header, string key) => header[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw Malformed();
    private static long Integer(JsonObject header, string key)
    {
        if (header[key] is not JsonValue value) throw Malformed();
        if (value.TryGetValue<long>(out var integer) && integer is >= -MaximumSafeInteger and <= MaximumSafeInteger) return integer;
        // JSON's 1.0 and 1e0 are the same Number as 1 in the upstream bridge.
        if (value.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Truncate(number) == number &&
            number is >= -MaximumSafeInteger and <= MaximumSafeInteger) return (long)number;
        throw Malformed();
    }
    private static IOException Malformed() => new("terminal: malformed bridge message");
}

/// <summary>Incremental bounded reader. Copies a fragmented frame once, never retaining memory owned by the caller.</summary>
public sealed class TerminalFrameReader<T>(Func<byte[], T> decode)
{
    private readonly byte[] _prefix = new byte[4];
    private int _prefixCount, _used;
    private byte[]? _payload;
    private bool _failed;

    public IReadOnlyList<T> Push(ReadOnlySpan<byte> chunk)
    {
        if (_failed) throw new IOException("terminal: frame reader has failed");
        var messages = new List<T>();
        try
        {
            while (!chunk.IsEmpty)
            {
                if (_payload is null)
                {
                    var take = Math.Min(4 - _prefixCount, chunk.Length);
                    chunk[..take].CopyTo(_prefix.AsSpan(_prefixCount)); _prefixCount += take; chunk = chunk[take..];
                    if (_prefixCount < 4) break;
                    var length = BinaryPrimitives.ReadInt32BigEndian(_prefix);
                    if (length is < 4 or > TerminalPipe.MaximumFrameBytes) throw new IOException("terminal: malformed frame length");
                    _payload = new byte[length]; _used = 0;
                }
                var copied = Math.Min(_payload.Length - _used, chunk.Length);
                chunk[..copied].CopyTo(_payload.AsSpan(_used)); _used += copied; chunk = chunk[copied..];
                if (_used != _payload.Length) continue;
                messages.Add(decode(_payload)); _payload = null; _prefixCount = 0;
            }
            return messages;
        }
        catch { _failed = true; _payload = null; throw; }
    }

    public void Complete()
    {
        if (_failed || _payload is not null || _prefixCount != 0) throw new IOException("terminal: truncated bridge frame");
    }
}

public static class TerminalFrameReaders
{
    public static TerminalFrameReader<ClientMessage> Client() => new(TerminalPipe.Client);
    public static TerminalFrameReader<LauncherMessage> Launcher() => new(TerminalPipe.Launcher);
}
