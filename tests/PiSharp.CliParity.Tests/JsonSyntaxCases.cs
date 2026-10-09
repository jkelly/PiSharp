using System.Text;
using System.Text.Json;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Pi;
using PiSharp.Contracts.Compatibility;

// Pi interpolates JSON.parse's SyntaxError into its errors (model-config.ts, settings-manager.ts, auth-storage.ts, trust-manager.ts,
// rpc-mode.ts, mcp/config.ts, theme.ts). Pi 1.1.0 requires Node >= 22.19, so the texts are V8 12.4's; the goldens were produced by
// tools/V8JsonSyntax/gen-goldens.mjs under Node 22.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> JsonSyntaxCases() =>
    [
        ("json-syntax.v8-messages-match-the-node-22-goldens", Sync(() =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "JsonSyntax", "v8-json-parse-goldens.json")));
            var checkedCases = 0; var invalid = 0;
            foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                var text = item.GetProperty("text").GetString()!;
                var expected = item.GetProperty("message").ValueKind == JsonValueKind.Null ? null : item.GetProperty("message").GetString();
                Equal(expected, JsJsonSyntax.Error(text), "JSON.parse(" + JsonSerializer.Serialize(text) + ")");
                checkedCases++; if (expected is not null) invalid++;
            }
            Check(checkedCases > 3000 && invalid > 2000, $"corpus size {checkedCases}/{invalid}");
        })),
        ("json-syntax.parse-failures-carry-the-v8-message", async () =>
        {
            using var sandbox = new Sandbox("json-syntax");
            Equal("Unexpected token '<', \"<html><bod\"... is not valid JSON",
                Throws<JsonException>(() => PiJson.Parse("<html><body>502 Bad Gateway</body></html>"), "html").Message, "PiJson.Parse");
            // auth-storage.ts load: an empty auth.json is JSON.parse("") and fails.
            var authPath = sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"), "");
            var store = new AuthJsonCredentialStore(authPath);
            var empty = await ThrowsAsync<InvalidDataException>(() => store.ReadAsync("anthropic", CancellationToken.None), "empty auth.json");
            Equal("Failed to read auth.json: Unexpected end of JSON input", empty.Message, "empty auth.json");
            File.WriteAllText(authPath, "{\"anthropic\":");
            Equal("Failed to read auth.json: Unexpected end of JSON input",
                (await ThrowsAsync<InvalidDataException>(() => store.ReadAsync("anthropic", CancellationToken.None), "truncated auth.json")).Message, "truncated");
            File.Delete(authPath);
            // rpc-mode.ts handleInputLine: every line is JSON.parse'd, an empty line included.
            var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\n\n{\"id\":\"1\",\"type\":\"get_state\"}\n"));
            using var output = new MemoryStream();
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
            Equal(0, await PiCommand.RunAsync(["--mode", "rpc", "--offline", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None),
                "rpc exit; " + stderr);
            var errors = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement).Where(frame => frame.TryGetProperty("command", out var command) && command.GetString() == "parse")
                .Select(frame => frame.GetProperty("error").GetString()).ToArray();
            Equal("Failed to parse command: Unexpected end of JSON input|Failed to parse command: Unexpected end of JSON input", string.Join("|", errors), "rpc parse errors");
        }),
    ];

    private static async Task<T> ThrowsAsync<T>(Func<Task> run, string what) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
}
