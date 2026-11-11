// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/link/binary.ts, packages/chord/src/link/text.ts, packages/chord/src/link/channel.ts.
// Authored native regression; not an upstream execution capture.
using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using PiSharp.Chord;
using PiSharp.Chord.Link;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Link152Cases()
    {
        yield return Case("link.152.frozen-codec-goldens", () =>
        {
            var root = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(root, "tests/fixtures/chord-link-v152.json")))
                root = Directory.GetParent(root)?.FullName ?? throw new Exception("fixture missing");
            var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests/fixtures/chord-link-v152.json")))!;
            Equal("b4afdddf44e059c67ccf5097e5317d913521b470", fixture["sourceSha"]!.GetValue<string>(), "frozen identity");
            foreach (var row in fixture["rows"]!.AsArray())
            {
                var text = Convert.FromBase64String(row!["text"]!.GetValue<string>());
                var binary = Convert.FromBase64String(row["binary"]!.GetValue<string>());
                var decoded = MessageCheck.Check(TextCodec.Instance.Decode(text));
                Sequence(text, TextCodec.Instance.Encode(decoded), "text " + row["id"]);
                Sequence(binary, BinaryCodec.Instance.Encode(decoded), "binary " + row["id"]);
                var binaryDecoded = MessageCheck.Check(BinaryCodec.Instance.Decode(binary));
                Sequence(binary, BinaryCodec.Instance.Encode(binaryDecoded), "binary roundtrip " + row["id"]);
                Sequence(text, TextCodec.Instance.Encode(binaryDecoded), "cross-codec " + row["id"]);
            }
        });
        yield return Case("link.152.trace-old-positions-and-validation", () =>
        {
            var call = new CallMessage(0, 1, 2);
            var trace = new Dictionary<string, string> { ["a"] = "b" };
            Equal("[\"c\",0,1,2,null,null,null,true]", TextCodec.Describe(call with { Old = true }), "old placeholders");
            Equal("[\"c\",0,1,2,null,null,{\"a\":\"b\"}]", TextCodec.Describe(call with { Trace = trace }), "trace placeholders");
            Equal("[\"c\",0,1,2,null,null,{\"a\":\"b\"},true]", TextCodec.Describe(call with { Trace = trace, Old = true }), "both placeholders");
            Equal((byte)0x23, BinaryCodec.Instance.Encode(call with { Old = true })[0], "old flag");
            Equal((byte)0xe3, BinaryCodec.Instance.Encode(call with { Trace = trace, Old = true, Origin = 2 })[0], "all flags");
            foreach (var malformed in new[]
            {
                "[\"c\",0,1,2,null,null,{},false]", "[\"c\",0,1,2,null,null,{\"a b\":\"v\"}]",
                "[\"c\",0,1,2,null,null,{\"a\":\"\\u001b\"}]", "[\"c\",0,1,2,null,null,{\"a\":1}]",
                "[\"c\",0,1,2,null,null,[]]",
            }) Throws<ArgumentException>(() => MessageCheck.Check(TextCodec.Instance.Decode(Encoding.UTF8.GetBytes(malformed))), malformed);
            foreach (var carrier in new[]
            {
                Enumerable.Range(0, 9).ToDictionary(i => "k" + i, _ => "v"),
                new Dictionary<string, string> { [new string('k', 257)] = "v" },
                new Dictionary<string, string> { ["k"] = new string('v', 257) },
            }) Throws<ArgumentException>(() => MessageCheck.Check(call with { Trace = carrier }), "carrier bound");
            MessageCheck.Check(call with { Trace = new Dictionary<string, string> { ["k"] = "a=1, b=2", ["empty"] = "" } });
            Throws<ArgumentException>(() => MessageCheck.Check(new HoldMessage(0, new string('t', 257), 1, "0123456789abcdef", new Part.Text("{}"), new("server", new JsonObject()))), "type name bound");
            Throws<ArgumentException>(() => BinaryCodec.Instance.Decode(new byte[] { 0x22, 0 }), "old only on call");
            Throws<ArgumentException>(() => BinaryCodec.Instance.Decode(new byte[] { 0x4d, 2, (byte)'{', (byte)'}' }), "trace only on hold/call");
        });
        yield return Case("link.152.scheduler-pause-preserves-response-window", () =>
        {
            var clock = new LinkClock(); var link = new ScriptLink(); var handler = new LinkHandler();
            var channel = new Channel(link, handler, new(BinaryCodec.Instance, LinkIncarnation, 1, false, _ => throw new Exception("protocol"), clock));
            channel.Start(); link.Push(BinaryCodec.Instance.Encode(new HelloMessage(1, LinkIncarnation, 1)));
            channel.Tick(120000, 118500, 15000, 30000);
            Check(!channel.Closed, "pause does not close before queued traffic reads");
            Check(BinaryCodec.Instance.Decode(link.Sent.Last()) is PingMessage, "pause pings immediately");
            channel.Tick(134999, 0, 15000, 30000); Check(!channel.Closed, "response allowance retained");
            clock.Now = 134999; link.Push(BinaryCodec.Instance.Encode(new PongMessage()));
            channel.Tick(164998, 0, 15000, 30000); Check(!channel.Closed, "pong resets allowance");
            channel.Tick(164999, 0, 15000, 30000); Check(channel.Closed, "true silence still closes");
        });
        yield return Case("link.152.nested-byte-payload-with-carrier", () =>
        {
            var payload = Parts.Payload(new Dictionary<string, object?>
            {
                ["nested"] = new object?[] { new Dictionary<string, object?> { ["bytes"] = new byte[] { 0, 10, 255 } }, new ReadOnlyMemory<byte>([1, 2]) },
            }, true);
            foreach (var codec in new ICodec[] { BinaryCodec.Instance, TextCodec.Instance })
            {
                var encoded = codec.Encode(new CallMessage(1, 0, 2, new Part.Text(payload.Json), payload.Segments, new Dictionary<string, string> { ["a"] = "b" }, true));
                var decoded = (CallMessage)MessageCheck.Check(codec.Decode(encoded));
                var tree = (Dictionary<string, object?>)Parts.ParsePayload(decoded.Args!, decoded.Segments)!;
                var nested = (object?[])tree["nested"]!;
                Sequence(new byte[] { 0, 10, 255 }, ((ReadOnlyMemory<byte>)((Dictionary<string, object?>)nested[0]!)["bytes"]!).ToArray(), codec.Name + " nested");
                Sequence(new byte[] { 1, 2 }, ((ReadOnlyMemory<byte>)nested[1]!).ToArray(), codec.Name + " memory");
            }
        });
        yield return Case("link.152.websocket-oversize-completes-1009-handshake", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                using var client = new TcpClient();
                var accept = listener.AcceptTcpClientAsync();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                using var server = await accept;
                using var serverSocket = WebSocket.CreateFromStream(server.GetStream(), true, null, Timeout.InfiniteTimeSpan);
                using var clientSocket = WebSocket.CreateFromStream(client.GetStream(), false, null, Timeout.InfiniteTimeSpan);
                var link = WebSocketLink.FromWebSocket(serverSocket, 4);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await clientSocket.SendAsync(new byte[8].AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
                var received = await clientSocket.ReceiveAsync(new byte[32].AsMemory(), timeout.Token);
                Equal(WebSocketMessageType.Close, received.MessageType, "close frame received");
                Equal(WebSocketCloseStatus.MessageTooBig, clientSocket.CloseStatus!.Value, "1009 code");
                await clientSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", timeout.Token);
                await WaitLink(() => link.Closed && serverSocket.State == WebSocketState.Closed);
                Equal(WebSocketState.Closed, serverSocket.State, "handshake completed without abort");
            }
            finally { listener.Stop(); }
        });
    }
}
