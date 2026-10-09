// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/rpc/rpc-mode.ts.
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Pi;

// rpc-mode.ts handleInputLine JSON.parse's every LF line (decoded by StringDecoder("utf8")) and hands the value to handleCommand.
// The expected frames were captured by feeding the same lines to the installed @earendil-works/pi-coding-agent@1.1.0 `pi --mode rpc`.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RpcInputCases() =>
    [
        ("rpc-input.json-parse-values-duplicates-surrogates-and-invalid-utf8", async () =>
        {
            byte[] Line(string text) => [.. Encoding.UTF8.GetBytes(text), (byte)'\n'];
            var lines = new List<byte[]>
            {
                // Values that are not objects have no id or type: handleCommand's default branch.
                Line("5"), Line("\"x\""), Line("[]"), Line("true"), Line("{}"), Line("[1,{\"type\":\"get_state\"}]"), Line("-0"),
                Line("{\"id\":\"a\"}"),
                // Duplicate names: the last value wins.
                Line("{\"type\":\"get_state\",\"type\":\"bogus\",\"id\":\"d\"}"), Line("{\"type\":\"bogus\",\"id\":\"1\",\"id\":\"2\"}"),
                // An escaped lone surrogate is a valid JSON.parse string; numbers beyond binary64 are Infinity.
                Line("{\"id\":\"q\",\"type\":\"bogus\",\"note\":\"\\ud800\"}"), Line("{\"id\":\"1\",\"type\":\"bogus\",\"x\":1e999}"),
                // An echoed lone surrogate is written back as JSON.stringify escapes it.
                Line("{\"id\":\"s\\udc00\",\"type\":\"bogus\\ud800\"}"),
                // Malformed UTF-8 decodes to U+FFFD.
                Encoding.UTF8.GetBytes("{\"id\":\"u\",\"type\":\"bo").Append((byte)0xFF).Concat(Encoding.UTF8.GetBytes("gus\"}\n")).ToArray(),
                // JSON.parse failures keep V8's SyntaxError text.
                Line("{\"type\":\"get_state\""), Line("  "),
            };
            var frames = await RunRpc(lines);
            string[] expected =
            [
                .. Enumerable.Repeat("""{"type":"response","success":false,"error":"Unknown command: undefined"}""", 7),
                """{"id":"a","type":"response","success":false,"error":"Unknown command: undefined"}""",
                """{"id":"d","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}""",
                """{"id":"2","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}""",
                """{"id":"q","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}""",
                """{"id":"1","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}""",
                """{"id":"s\udc00","type":"response","command":"bogus\ud800","success":false,"error":"Unknown command: bogus\ud800"}""",
                "{\"id\":\"u\",\"type\":\"response\",\"command\":\"bo\uFFFDgus\",\"success\":false,\"error\":\"Unknown command: bo\uFFFDgus\"}",
                """{"type":"response","command":"parse","success":false,"error":"Failed to parse command: Expected ',' or '}' after property value in JSON at position 19 (line 1 column 20)"}""",
                """{"type":"response","command":"parse","success":false,"error":"Failed to parse command: Unexpected end of JSON input"}""",
            ];
            // Byte-identical to JSON.stringify: ' and non-ASCII raw, lone surrogates as lowercase escapes.
            Equal(string.Join("\n", expected.Order(StringComparer.Ordinal)), string.Join("\n", frames.Code.Order(StringComparer.Ordinal)), "rpc responses");
            Equal(0, frames.Exit, "rpc exit; " + frames.Stderr);
        }),
        ("rpc-input.non-string-ids-and-types-echo-and-no-length-or-depth-32-caps", async () =>
        {
            byte[] Line(string text) => [.. Encoding.UTF8.GetBytes(text), (byte)'\n'];
            var longType = new string('t', 300);
            var frames = await RunRpc(
            [
                Line("{\"id\":5,\"type\":\"nope\"}"), Line("{\"id\":\"t\",\"type\":5}"), Line("{\"id\":null,\"type\":true}"),
                Line("{\"type\":[1,null,\"a\",[2,3]]}"), Line("{\"id\":{\"k\":[1]},\"type\":{\"a\":1}}"), Line("{\"id\":\"n\",\"type\":null}"),
                Line("{\"id\":\"L\",\"type\":\"" + longType + "\"}"),
                Line("{\"id\":\"deep\",\"type\":\"bogus\",\"x\":" + new string('[', 60) + new string(']', 60) + "}"),
                Line("{\"id\":[1.50,-0],\"type\":\"get_state\"}"),
            ]);
            string[] expected =
            [
                """{"id":5,"type":"response","command":"nope","success":false,"error":"Unknown command: nope"}""",
                """{"id":"t","type":"response","command":5,"success":false,"error":"Unknown command: 5"}""",
                """{"id":null,"type":"response","command":true,"success":false,"error":"Unknown command: true"}""",
                """{"type":"response","command":[1,null,"a",[2,3]],"success":false,"error":"Unknown command: 1,,a,2,3"}""",
                """{"id":{"k":[1]},"type":"response","command":{"a":1},"success":false,"error":"Unknown command: [object Object]"}""",
                """{"id":"n","type":"response","command":null,"success":false,"error":"Unknown command: null"}""",
                "{\"id\":\"L\",\"type\":\"response\",\"command\":\"" + longType + "\",\"success\":false,\"error\":\"Unknown command: " + longType + "\"}",
                """{"id":"deep","type":"response","command":"bogus","success":false,"error":"Unknown command: bogus"}""",
            ];
            Equal(string.Join("\n", expected.Order(StringComparer.Ordinal)),
                string.Join("\n", frames.Code.Where(frame => !frame.Contains("\"command\":\"get_state\"", StringComparison.Ordinal)).Order(StringComparer.Ordinal)), "rpc responses");
            var state = JsonDocument.Parse(frames.Code.Single(frame => frame.Contains("\"command\":\"get_state\"", StringComparison.Ordinal))).RootElement;
            Equal("[1.5,0]", state.GetProperty("id").GetRawText(), "array id");
            Check(state.GetProperty("success").GetBoolean(), "get_state with an array id failed");
        }),
        ("rpc-input.prompt-text-and-image-count-have-no-pi-limit", async () =>
        {
            // rpc-mode.ts prompt: session.prompt(command.message, { images }) has no length or image count limit of its own.
            const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
            var text = new string('x', 70_000);
            var images = string.Join(",", Enumerable.Repeat("{\"type\":\"image\",\"data\":\"" + png + "\",\"mimeType\":\"image/png\"}", 17));
            var result = await RunRpc([Encoding.UTF8.GetBytes("{\"id\":\"p\",\"type\":\"prompt\",\"message\":\"" + text + "\",\"images\":[" + images + "]}\n")]);
            var prompt = JsonDocument.Parse(result.Code.Single(frame => frame.Contains("\"id\":\"p\"", StringComparison.Ordinal))).RootElement;
            Check(prompt.GetProperty("success").GetBoolean(), "prompt refused: " + prompt.GetRawText());
            // The admitted user message carries the whole text and every image.
            var user = result.All.Select(line => JsonDocument.Parse(line).RootElement).First(frame => frame.GetProperty("type").GetString() == "message_start" &&
                frame.GetProperty("message").GetProperty("role").GetString() == "user");
            var content = user.GetProperty("message").GetProperty("content");
            Equal(17, content.EnumerateArray().Count(block => block.GetProperty("type").GetString() == "image"), "images admitted");
            Check(content.EnumerateArray().Any(block => block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString() == text), "text admitted");
        }),
        ("rpc-input.null-command-is-an-unhandled-type-error", async () =>
        {
            // handleCommand(null) reads null.id, and so does its catch block: Node prints the TypeError and exits 1 with no response.
            var result = await RunRpc([Encoding.UTF8.GetBytes("null\n")]);
            Equal(1, result.Exit, "exit code");
            Equal("", string.Join("\n", result.Code), "responses");
            Check(result.Stderr.Contains("TypeError: Cannot read properties of null (reading 'id')", StringComparison.Ordinal), "stderr: " + result.Stderr);
        }),
    ];

    private static async Task<(int Exit, string[] Code, string Stderr, string[] All)> RunRpc(IEnumerable<byte[]> lines, Action<Sandbox>? setup = null)
    {
        using var sandbox = new Sandbox("rpc-input");
        setup?.Invoke(sandbox);
        var input = new MemoryStream([.. lines.SelectMany(line => line)]);
        using var output = new MemoryStream();
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        var exit = await PiCommand.RunAsync(["--mode", "rpc", "--offline", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None);
        var all = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var responses = all.Where(line => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString() == "response").ToArray();
        return (exit, responses, stderr.ToString(), all);
    }
}
