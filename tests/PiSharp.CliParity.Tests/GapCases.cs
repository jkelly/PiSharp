using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// main.ts (RPC background catalog refresh, configureHttpDispatcher), core/http-dispatcher.ts and settings-manager.ts
// (httpIdleTimeoutMs), cli/credential-print.ts (OAuth refresh for print-bearer-token), migrations.ts (keybindings write-back) and the
// unported install telemetry (PI_TELEMETRY, enableInstallTelemetry accepted and ignored).
internal static partial class Program
{
    /// <summary>A response body that sends its first bytes, then stalls until canceled.</summary>
    private sealed class StallingStream(byte[] first) : Stream
    {
        private bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sent) { sent = true; first.CopyTo(buffer); return first.Length; }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Delayed(TimeSpan delay, Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(delay, cancellationToken); return respond(); }
    }

    private sealed class TokenEndpoint : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.RequestUri!.AbsoluteUri + " " + (request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK)
            { Content = new StringContent("""{"access_token":"new-access","refresh_token":"r2","expires_in":3600,"token_type":"Bearer"}""", Encoding.UTF8, "application/json") };
        }
    }

    private static IEnumerable<(string, Func<Task>)> GapCases() =>
    [
        ("rpc.background-catalog-refresh-unless-offline", async () =>
        {
            foreach (var offline in new[] { false, true })
            {
                using var sandbox = new Sandbox("rpc-refresh");
                var input = new MemoryStream("{\"id\":\"1\",\"type\":\"get_state\"}\n"u8.ToArray());
                var gate = new GatedInput(input);
                using var output = new SignalingStream("\"command\":\"get_state\"", gate.Release);
                using var stdout = new StringWriter(); using var stderr = new StringWriter();
                Task? refresh = null;
                var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false, CatalogRefreshStarted = task => refresh = task };
                string[] args = ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5", .. offline ? ["--offline"] : Array.Empty<string>()];
                Equal(0, await PiCommand.RunAsync(args, host, CancellationToken.None), "exit; " + stderr);
                if (offline) { Equal(null, refresh, "no refresh offline"); Equal(0, sandbox.CatalogRequests.Count, "no catalog request offline"); continue; }
                Check(refresh is not null, "the refresh starts with RPC mode");
                await refresh!.WaitAsync(TimeSpan.FromSeconds(30));
                Check(sandbox.CatalogRequests.Any(url => url.StartsWith("https://catalog.test/api/models/providers/anthropic?types=", StringComparison.Ordinal)),
                    "catalog revalidation: " + string.Join(", ", sandbox.CatalogRequests.Take(3)));
                Check(File.Exists(Path.Combine(sandbox.AgentDir, "models-store.json")), "the refreshed catalog state is persisted");
            }
        }),
        ("settings.http-idle-timeout-ms-parse-default-errors-and-timeouts", async () =>
        {
            long? Parse(string json) => PiHttpIdleTimeout.Parse(JsonNode.Parse("{\"v\":" + json + "}")!["v"]);
            Equal<long?>(1500, Parse("1500.7"), "floored"); Equal<long?>(0, Parse("0"), "zero"); Equal<long?>(0, Parse("\" Disabled \""), "disabled");
            Equal<long?>(2000, Parse("\" 2000 \""), "numeric string"); Equal<long?>(16, Parse("\"0x10\""), "hex string");
            Equal<long?>(null, Parse("-1"), "negative"); Equal<long?>(null, Parse("\"abc\""), "text"); Equal<long?>(null, Parse("\"\""), "empty");
            Equal<long?>(null, Parse("true"), "boolean"); Equal<long?>(null, Parse("\"Infinity\""), "infinite");
            Equal(300_000L, PiHttpIdleTimeout.FromSettings(new JsonObject()), "default 5 minutes");
            Equal("Invalid httpIdleTimeoutMs setting: abc", Throws<InvalidDataException>(() => PiHttpIdleTimeout.FromSettings(new JsonObject { ["httpIdleTimeoutMs"] = "abc" }), "invalid").Message, "invalid text");
            Equal("Invalid httpIdleTimeoutMs setting: 1,2", Throws<InvalidDataException>(() => PiHttpIdleTimeout.FromSettings(JsonNode.Parse("{\"httpIdleTimeoutMs\":[1,2]}")!.AsObject()), "array").Message, "String(array)");
            Equal("Invalid httpIdleTimeoutMs setting: null", Throws<InvalidDataException>(() => PiHttpIdleTimeout.FromSettings(JsonNode.Parse("{\"httpIdleTimeoutMs\":null}")!.AsObject()), "null").Message, "null");
            // Headers that do not arrive, and a body that stalls after its first chunk, fail after the idle timeout.
            using (var client = new HttpClient(PiHttpIdleTimeout.Wrap(() => new Delayed(TimeSpan.FromSeconds(30), () => new(HttpStatusCode.OK)), 150)()!))
            {
                var error = await ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://idle.test/"));
                Equal("Headers Timeout Error", error.Message, "headers timeout");
            }
            using (var client = new HttpClient(PiHttpIdleTimeout.Wrap(() => new Delayed(TimeSpan.Zero, () => new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream("partial"u8.ToArray())) }), 150)()!))
            {
                using var response = await client.GetAsync("https://idle.test/", HttpCompletionOption.ResponseHeadersRead);
                await using var body = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[64];
                Equal(7, await body.ReadAsync(buffer), "first chunk");
                var error = await ThrowsAsync<HttpRequestException>(async () => _ = await body.ReadAsync(buffer.AsMemory(), CancellationToken.None));
                Equal("Body Timeout Error", error.Message, "body timeout");
            }
            Check(PiHttpIdleTimeout.Wrap(() => null, 0)() is null, "0 disables the timeout (the provider default handler stays)");
            // Through the entry: an invalid setting stops startup; a stalled provider stream fails after the configured timeout.
            using var sandbox = new Sandbox("idle-timeout");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"httpIdleTimeoutMs":"soon"}""");
            var (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            Check(code == 1 && stderr.Contains("Error: Invalid httpIdleTimeoutMs setting: soon", StringComparison.Ordinal), "invalid setting: " + code + " " + stderr);
            Equal(0, sandbox.Requests.Count, "nothing sent");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"httpIdleTimeoutMs":300,"retry":{"enabled":false}}""");
            sandbox.Respond = (_, _) => new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes(
                Frame("message_start", new { type = "message_start", message = new { id = "msg_1", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 3, output_tokens = 0 } } })))) };
            var started = DateTime.UtcNow;
            (code, var stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            // The provider stream reports the interrupted body in its own words; without the idle timeout this request would hang.
            Check(code == 1 && stderr.Length > 0, $"stalled stream fails: {code} {stdout} {stderr}");
            Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "fails after the idle timeout");
        }),
        ("auth.print-bearer-token-refreshes-an-expiring-oauth-token", async () =>
        {
            using var sandbox = new Sandbox("bearer-refresh");
            sandbox.Vars.Remove("ANTHROPIC_API_KEY");
            var authPath = sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"),
                """{"anthropic":{"type":"oauth","access":"old-access","refresh":"r1","expires":1}}""");
            var tokens = new TokenEndpoint();
            async Task<(int, string, string)> Auth(params string[] args)
            {
                using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
                var host = sandbox.Host(stdout, stderr, null) with { LiveRuntime = sandbox.Runtime() with { CreateAuthHttp = () => new HttpMessageInvoker(tokens) } };
                var code = await PiCommand.RunAsync(args, host, CancellationToken.None);
                return (code, stdout.ToString(), stderr.ToString());
            }
            var (code, stdout, stderr) = await Auth("auth", "check", "--provider", "anthropic", "--credentials", "--no-refresh");
            Equal(0, tokens.Bodies.Count, "--no-refresh does not refresh; " + code + " " + stdout + stderr);
            (code, stdout, stderr) = await Auth("auth", "print-bearer-token", "--provider", "anthropic");
            Check(code == 0 && stdout == "new-access\n", $"refreshed token: {code} {stdout} {stderr}");
            Check(tokens.Bodies.Single().StartsWith("https://platform.claude.com/v1/oauth/token ", StringComparison.Ordinal) &&
                tokens.Bodies.Single().Contains("r1", StringComparison.Ordinal), "refresh request: " + tokens.Bodies.Single());
            var stored = JsonNode.Parse(File.ReadAllText(authPath))!["anthropic"]!;
            Check(stored["access"]!.GetValue<string>() == "new-access" && stored["refresh"]!.GetValue<string>() == "r2", "persisted: " + stored.ToJsonString());
            (code, stdout, _) = await Auth("auth", "print-bearer-token", "--provider", "anthropic");
            Check(code == 0 && stdout == "new-access\n" && tokens.Bodies.Count == 1, "a valid token is not refreshed again");
            // A requested minimum beyond what the refreshed token offers refreshes, then fails as upstream does.
            (code, stdout, stderr) = await Auth("auth", "print-bearer-token", "--provider", "anthropic", "--min-expiry", "2h");
            Check(code == 1 && tokens.Bodies.Count == 2 && stderr == "Error: OAuth refresh returned a token that expires too soon for anthropic\n",
                $"--min-expiry beyond the remaining validity: {code} {stdout} {stderr}");
        }),
        ("migrations.keybindings-legacy-names-written-back", async () =>
        {
            using var sandbox = new Sandbox("keybindings");
            var path = sandbox.Write(Path.Combine(sandbox.AgentDir, "keybindings.json"), """{"zzCustom":"ctrl+z","cursorDown":"down","cursorUp":["up","ctrl+p"]}""");
            var (code, _, stderr) = await sandbox.Run("--help");
            Equal(0, code, "exit; " + stderr);
            Equal("{\n  \"tui.editor.cursorUp\": [\n    \"up\",\n    \"ctrl+p\"\n  ],\n  \"tui.editor.cursorDown\": \"down\",\n  \"zzCustom\": \"ctrl+z\"\n}\n",
                File.ReadAllText(path), "migrated file");
            const string current = """{"tui.editor.cursorUp":"up"}""";
            File.WriteAllText(path, current);
            await sandbox.Run("--help");
            Equal(current, File.ReadAllText(path), "an already-current file is left as it is");
            File.WriteAllText(path, "{not json");
            Equal(0, (await sandbox.Run("--help")).Code, "a malformed file does not stop startup");
            Equal("{not json", File.ReadAllText(path), "a malformed file is left alone");
        }),
        ("telemetry.pi-telemetry-and-enable-install-telemetry-are-accepted-and-ignored", async () =>
        {
            foreach (var value in new[] { "1", "0", "yes", "garbage" })
            {
                using var sandbox = new Sandbox("telemetry");
                sandbox.Vars["PI_TELEMETRY"] = value;
                sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"enableInstallTelemetry":true}""");
                sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), """{"enableInstallTelemetry":false}""");
                var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
                Check(code == 0 && stdout == "ok\n" && stderr == "", $"PI_TELEMETRY={value}: {code} {stdout} {stderr}");
                Check(sandbox.Requests.All(request => request.Url.StartsWith("https://api.anthropic.com/", StringComparison.Ordinal)), "only the provider is contacted");
            }
        }),
    ];

    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name + ".");
    }
}
