using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

internal static partial class McpTransportTests
{
    // Authored against 7d073328, packages/mcp/src/transports/streamable-http.ts.
    private static async Task HttpSseLineEndings()
    {
        foreach (var ending in new[] { "\r", "\n", "\r\n", "mixed" })
        {
            var endings = ending == "mixed" ? new[] { "\r", "\n", "\r\n", "\n", "\r" } : new[] { ending };
            var lines = new[] { "\uFEFF: comment", "event: message", "data: {\"jsonrpc\":\"2.0\",",
                "data: \"id\":1,\"result\":\"雪\"}", "" };
            var payload = Encoding.UTF8.GetBytes(string.Concat(lines.Select((line, index) => line + endings[index % endings.Length])));
            // Every possible split includes splits inside CRLF and the UTF-8 scalar. Also
            // exercise one-byte reads. The response stays open: dispatch cannot depend on EOF.
            foreach (var split in Enumerable.Range(1, payload.Length - 1).Prepend(0))
            {
                var body = new Output();
                if (split == 0) foreach (var item in payload) body.Push([item]);
                else { body.Push(payload[..split]); body.Push(payload[split..]); }
                var wire = Http(Binding(), (_, _) => ValueTask.FromResult(SseBody(body)));
                JsonData? received = null;
                try
                {
                    await wire.StartAsync(new(message => { received = message; return ValueTask.CompletedTask; }, _ => { }), default);
                    await wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test\"}"), default).WaitAsync(TimeSpan.FromSeconds(3));
                    Equal("雪", received!.Value.GetProperty("result").GetString());
                }
                finally { body.Finish(); await wire.CloseAsync(); }
            }
        }
    }

    private static async Task HttpSseCrBounds()
    {
        foreach (var ending in new[] { "\r", "\n", "\r\n" })
        foreach (var kind in new[] { "line", "event", "exact" })
        {
            const int limit = 128;
            // Multibyte values ensure the bound measures bytes rather than characters.
            var payload = kind switch
            {
                "line" => ":" + new string('雪', 43) + ending,
                "event" => string.Concat(Enumerable.Repeat("data: " + new string('雪', 12) + ending, 4)),
                _ => ":" + new string('x', limit - 1) + ending +
                    "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}" + ending + ending
            };
            var body = new Output(); foreach (var item in Encoding.UTF8.GetBytes(payload)) body.Push([item]);
            var wire = Http(Binding(), (_, _) => ValueTask.FromResult(SseBody(body)), new(MaximumFrameBytes: limit));
            try
            {
                await wire.StartAsync(new(_ => ValueTask.CompletedTask, _ => { }), default);
                var send = wire.SendAsync(Json("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"test\"}"), default);
                if (kind == "exact") await send.WaitAsync(TimeSpan.FromSeconds(3));
                else Check((await Throws<McpRuntimeProtocolException>(send)).Message.Contains(kind == "line" ? "line byte limit" : "event byte limit"));
            }
            finally { body.Finish(); await wire.CloseAsync(); }
        }
    }
}
