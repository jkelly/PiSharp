using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

// The production live route (RpcSessionCommand → OfflineSessionProfile → LiveSessionSelection) against fake endpoints.
// Upstream: packages/ai/src/auth/resolve.ts (resolveProviderAuth), providers/anthropic.ts (anthropicApiKeyAuth.resolve),
// coding-agent core/model-runtime.ts (prepareRequest resolves auth per request), core/auth-storage.ts, providers/azure.ts and
// api/azure-openai-config.ts. Authored expectations; no network, no real environment, no home directory.
internal static partial class Program
{
    private sealed record Seen(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body);

    /// <summary>Fake provider endpoints: records every request and answers through <c>respond</c>.</summary>
    private sealed class LiveEndpoint(Func<Seen, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<Seen> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? []))
                headers[header.Key] = string.Join(", ", header.Value);
            var seen = new Seen(request.Method.Method, request.RequestUri!.AbsoluteUri, headers,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            lock (Requests) Requests.Add(seen);
            return respond(seen);
        }
        public Seen[] Snapshot() { lock (Requests) return [.. Requests]; }
    }

    private static HttpResponseMessage AnthropicStream()
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        var stream = Frame("message_start", new { type = "message_start", message = new { id = "authored", role = "assistant", model = "claude-sonnet-4-5", content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } })
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "ok" } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 1 } })
            + Frame("message_stop", new { type = "message_stop" });
        return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages?beta=true";

    /// <summary>One production RPC host over a bounded in-memory connection.</summary>
    private sealed class LiveRpc : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = System.Threading.Channels.Channel.CreateUnbounded<JsonData>();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        private readonly BoundedRpcConnection connection;
        private bool finished;
        public StringWriter Error { get; } = new();
        public Task<int> Completion { get; }
        public List<JsonData> Events { get; } = [];
        public LiveRpc(string[] args, LiveSessionRuntime runtime, PiSharp.Cli.Mcp.McpSessionHost? mcpHost = null)
        {
            connection = new((record, _) => { records.Writer.TryWrite(record); return ValueTask.CompletedTask; });
            Completion = Run();
            async Task<int> Run()
            {
                try { return await RpcSessionCommand.RunWithPresentationAsync(args, connection.Input, connection.Output, Error, null!, deadline.Token, liveRuntime: runtime, mcpHost: mcpHost); }
                finally { records.Writer.TryComplete(); }
            }
        }
        private async Task<JsonData> Next(string what)
        {
            try { return await records.Reader.ReadAsync(deadline.Token); }
            catch (ChannelClosedException) { throw new InvalidOperationException($"RPC host ended before {what}; exit {await Completion}; {Error}"); }
        }
        public async Task Prompt(string id, string message)
        {
            await connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(new { id, type = "prompt", message })), deadline.Token);
            var responded = false;
            while (true)
            {
                var record = await Next(id); Events.Add(record);
                var type = record.Value.GetProperty("type").GetString();
                if (type == "response" && record.Value.GetProperty("id").GetString() == id)
                { Check(record.Value.GetProperty("success").GetBoolean(), "prompt refused: " + record); responded = true; }
                else if (type == "agent_settled" && responded) return;
            }
        }
        public async Task<int> Finish()
        {
            finished = true; connection.CompleteInput();
            while (await records.Reader.WaitToReadAsync()) while (records.Reader.TryRead(out var record)) Events.Add(record);
            return await Completion;
        }
        /// <summary>The assistant messages' stop reasons, so a failed request is reported with its message.</summary>
        public string[] StopReasons() => [.. Events.Where(record => record.Value.GetProperty("type").GetString() == "message_end" &&
            record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant")
            .Select(record => record.Value.GetProperty("message").GetProperty("stopReason").GetString() + ":" +
                (record.Value.GetProperty("message").TryGetProperty("errorMessage", out var error) ? error.GetString() : ""))];
        public async ValueTask DisposeAsync()
        {
            if (!finished) { connection.CompleteInput(); try { await Completion; } catch (Exception) { } }
            await connection.DisposeAsync(); deadline.Dispose();
        }
    }

    private static string[] LiveArgs(string root, string provider, string model) =>
        ["session", "rpc", "--session", Path.Combine(root, "session.jsonl"), "--workspace", root, "--live", "--provider", provider, "--model", model,
            "--session-mode", "new-memory"];

    private static Func<string, string?> Env(params (string Name, string Value)[] values) => name =>
    {
        foreach (var (key, value) in values) if (key == name) return value;
        return null;
    };

    private static async Task WithLiveRoot(string name, Func<string, Task> run)
    {
        var root = Temp(name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { await run(Path.GetFullPath(root)); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static async Task RunPrompts(LiveRpc rpc, params string[] prompts)
    {
        foreach (var prompt in prompts) await rpc.Prompt("p-" + prompt, prompt);
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
    }

    // auth.anthropic + refresh during a running session: the stored auth.json OAuth credential owns the provider over the environment,
    // is refreshed before the request that finds it within five minutes of expiry, the rotation is persisted, and a logout mid-session
    // falls back to the environment at the next request (resolveProviderAuth runs per request).
    private static Task LiveStoredOAuthRefreshesMidSession() => WithLiveRoot("live-oauth", async root =>
    {
        var authPath = Path.Combine(root, "agent", "auth.json"); Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
        var time = new FixedTime(1_800_000_000_000); var expires = time.Now + 3_600_000;
        await File.WriteAllTextAsync(authPath, "{\"anthropic\":{\"type\":\"oauth\",\"refresh\":\"refresh-1\",\"access\":\"sk-ant-oat01-first\",\"expires\":" + expires + "}}");
        var tokens = new TokenEndpoint("""{"access_token":"sk-ant-oat01-second","refresh_token":"refresh-2","expires_in":7200}""");
        var provider = new LiveEndpoint(seen => seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url));
        var runtime = new LiveSessionRuntime(Env(("ANTHROPIC_API_KEY", "env-key")), () => provider, authPath,
            () => new HttpMessageInvoker(tokens, disposeHandler: false), time);
        await using var rpc = new LiveRpc(LiveArgs(root, "anthropic", "claude-sonnet-4-5"), runtime);
        await rpc.Prompt("first", "first");
        time.Now = expires - 300_000; // Within the five-minute window: the next request refreshes first.
        await rpc.Prompt("second", "second");
        var stored = await new PiSharp.Cli.Authentication.AuthJsonCredentialStore(authPath).ReadAsync("anthropic", CancellationToken.None);
        Check(stored is { Access: "sk-ant-oat01-second", Refresh: "refresh-2" } && stored.ExpiresUnixMilliseconds == time.Now + 7_200_000 - 300_000,
            "rotation persisted to auth.json");
        await new PiSharp.Cli.Authentication.AuthJsonCredentialStore(authPath).DeleteAsync("anthropic", CancellationToken.None);
        await rpc.Prompt("third", "third");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        Names(["stop:", "stop:", "stop:"], rpc.StopReasons(), "assistant stops");
        var requests = provider.Snapshot();
        Equal(3, requests.Length, "provider requests");
        Equal("Bearer sk-ant-oat01-first", requests[0].Headers["Authorization"], "stored OAuth token");
        Equal("Bearer sk-ant-oat01-second", requests[1].Headers["Authorization"], "refreshed token on the next request");
        foreach (var oauth in requests[..2])
        {
            Check(!oauth.Headers.ContainsKey("x-api-key") && oauth.Headers["anthropic-beta"].Contains("oauth-2025-04-20", StringComparison.Ordinal) &&
                oauth.Headers["User-Agent"].StartsWith("claude-cli/", StringComparison.Ordinal), "OAuth projection headers");
        }
        Equal("env-key", requests[2].Headers["x-api-key"], "after logout the environment key");
        Check(!requests[2].Headers.ContainsKey("Authorization"), "no stale OAuth token after logout");
        Equal(1, tokens.Bodies.Count, "one refresh");
        Equal("refresh_token", tokens.Bodies[0].GetProperty("grant_type").GetString(), "refresh grant");
        Equal("refresh-1", tokens.Bodies[0].GetProperty("refresh_token").GetString(), "refresh token sent");
    });

    // auth.anthropic precedence on the live route (anthropicApiKeyAuth.resolve): a stored api_key (with its $VAR template) wins over the
    // environment; then ANTHROPIC_AUTH_TOKEN (Bearer) over OAUTH_TOKEN over API_KEY; workload identity federation last, exchanging once
    // for both requests of the session; nothing configured refuses the session.
    private static Task LiveAnthropicPrecedenceAndFederation() => WithLiveRoot("live-precedence", async root =>
    {
        async Task<Seen[]> Session(Func<string, string?> environment, string? authPath = null, int prompts = 1, Func<Seen, HttpResponseMessage>? respond = null)
        {
            var provider = new LiveEndpoint(respond ?? (seen => seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url)));
            var tokens = new TokenEndpoint();
            await using var rpc = new LiveRpc(LiveArgs(root, "anthropic", "claude-sonnet-4-5"), new(environment, () => provider, authPath,
                () => new HttpMessageInvoker(tokens, disposeHandler: false)));
            await RunPrompts(rpc, [.. Enumerable.Range(1, prompts).Select(index => "prompt" + index)]);
            Check(tokens.Bodies.Count == 0, "no OAuth refresh");
            return provider.Snapshot();
        }
        var authPath = Path.Combine(root, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"anthropic\":{\"type\":\"api_key\",\"key\":\"pre-$STORED_PART\",\"env\":{\"STORED_PART\":\"stored\"}}}");
        var stored = (await Session(Env(("ANTHROPIC_API_KEY", "env-key"), ("ANTHROPIC_AUTH_TOKEN", "env-token")), authPath)).Single();
        Equal("pre-stored", stored.Headers["x-api-key"], "stored api_key owns the provider");
        Check(!stored.Headers.ContainsKey("Authorization"), "stored key without bearer");

        var bearer = (await Session(Env(("ANTHROPIC_AUTH_TOKEN", "env-token"), ("ANTHROPIC_OAUTH_TOKEN", "sk-ant-oat01-env"), ("ANTHROPIC_API_KEY", "env-key")))).Single();
        Equal("Bearer env-token", bearer.Headers["Authorization"], "AUTH_TOKEN first");
        Check(!bearer.Headers.ContainsKey("x-api-key"), "AUTH_TOKEN carries no key");
        var oauth = (await Session(Env(("ANTHROPIC_OAUTH_TOKEN", "sk-ant-oat01-env"), ("ANTHROPIC_API_KEY", "env-key")))).Single();
        Equal("Bearer sk-ant-oat01-env", oauth.Headers["Authorization"], "OAUTH_TOKEN before API_KEY, with the OAuth projection");
        var key = (await Session(Env(("ANTHROPIC_API_KEY", "env-key")))).Single();
        Equal("env-key", key.Headers["x-api-key"], "API_KEY");

        var identity = Path.Combine(root, "identity.jwt"); await File.WriteAllTextAsync(identity, "header.payload.signature\n");
        var exchanges = 0;
        var federated = await Session(Env(("ANTHROPIC_FEDERATION_RULE_ID", "fdrl_test"), ("ANTHROPIC_ORGANIZATION_ID", "org-test"),
            ("ANTHROPIC_IDENTITY_TOKEN_FILE", identity), ("ANTHROPIC_WORKSPACE_ID", "wrkspc_test")), prompts: 2,
            respond: seen => seen.Url == "https://api.anthropic.com/v1/oauth/token" ? JsonResponse($$"""{"access_token":"federated-{{++exchanges}}","expires_in":3600,"token_type":"Bearer"}""")
                : seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url));
        Equal(3, federated.Length, "one exchange, two messages");
        Equal("https://api.anthropic.com/v1/oauth/token", federated[0].Url, "exchange first");
        Equal("""{"grant_type":"urn:ietf:params:oauth:grant-type:jwt-bearer","assertion":"header.payload.signature","federation_rule_id":"fdrl_test","organization_id":"org-test","workspace_id":"wrkspc_test"}""",
            federated[0].Body, "exchange body");
        foreach (var message in federated[1..])
        {
            Equal(MessagesUrl, message.Url, "messages");
            Equal("Bearer federated-1", message.Headers["Authorization"], "federated access token");
            Check(message.Headers["anthropic-beta"].Contains("oauth-2025-04-20", StringComparison.Ordinal) && !message.Headers.ContainsKey("x-api-key"), "federation headers");
        }

        // Nothing stored or configured: the session is refused before any request.
        var none = new LiveEndpoint(_ => throw new InvalidOperationException("No request expected."));
        await using var refused = new LiveRpc(LiveArgs(root, "anthropic", "claude-sonnet-4-5"), new(Env(), () => none, Path.Combine(root, "absent", "auth.json")));
        Equal(2, await refused.Finish(), "missing credential exit");
        Check(refused.Error.ToString().Contains("\"MissingLiveApiKey\"", StringComparison.Ordinal), "missing credential code: " + refused.Error);
        Check(none.Snapshot().Length == 0 && !Directory.Exists(Path.Combine(root, "absent")), "no request and no auth.json directory");
        // A stored key command is not run (deviation): the session is refused with the reason.
        await File.WriteAllTextAsync(authPath, "{\"anthropic\":{\"type\":\"api_key\",\"key\":\"!pass show anthropic\"}}");
        await using var command = new LiveRpc(LiveArgs(root, "anthropic", "claude-sonnet-4-5"), new(Env(("ANTHROPIC_API_KEY", "env-key")), () => none, authPath));
        Equal(2, await command.Finish(), "stored command exit");
        Check(command.Error.ToString().Contains("\"LiveAuthenticationFailed\"", StringComparison.Ordinal) &&
            command.Error.ToString().Contains("are not run by PiSharp", StringComparison.Ordinal), "stored command refused: " + command.Error);
    });

    // provider.azure-rename: provider azure on the live route. The pinned azure.json shard routes azure-openai-responses to the Azure
    // Responses transport and openai-completions to Foundry Chat Completions; AZURE_OPENAI_RESOURCE_NAME and
    // AZURE_OPENAI_DEPLOYMENT_NAME_MAP select the endpoint and the body's model; AZURE_OPENAI_API_KEY authenticates.
    private static Task LiveAzureRoutes() => WithLiveRoot("live-azure", async root =>
    {
        const string ResponsesSse = "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[]}}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"item_id\":\"m\",\"delta\":\"ok\"}\n\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}}}\n\n";
        const string CompletionsSse = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var environment = Env(("AZURE_OPENAI_API_KEY", "azure-key"), ("AZURE_OPENAI_RESOURCE_NAME", "pisharp-res"),
            ("AZURE_OPENAI_DEPLOYMENT_NAME_MAP", "gpt-5.4=prod-gpt54, deepseek-v4-pro=ds-prod"), ("OPENAI_API_KEY", "openai-key"));
        async Task<Seen> Session(string model)
        {
            var provider = new LiveEndpoint(seen => new(HttpStatusCode.OK)
            { Content = new StringContent(seen.Url.Contains("/responses", StringComparison.Ordinal) ? ResponsesSse : CompletionsSse, Encoding.UTF8, "text/event-stream") });
            await using var rpc = new LiveRpc(LiveArgs(root, "azure", model), new(environment, () => provider));
            await RunPrompts(rpc, "hello");
            Names(["stop:"], rpc.StopReasons(), model + " assistant stop");
            return provider.Snapshot().Single();
        }
        var responses = await Session("gpt-5.4");
        Equal("https://pisharp-res.openai.azure.com/openai/v1/responses?api-version=v1", responses.Url, "Azure Responses endpoint");
        Equal("azure-key", responses.Headers["api-key"], "Azure api-key header");
        Check(!responses.Headers.ContainsKey("Authorization"), "no OpenAI bearer on Azure Responses");
        using (var body = JsonDocument.Parse(responses.Body!))
        {
            Equal("prod-gpt54", body.RootElement.GetProperty("model").GetString(), "deployment as the body model");
            Equal(false, body.RootElement.GetProperty("store").GetBoolean(), "store false");
        }
        var completions = await Session("deepseek-v4-pro");
        Equal("https://pisharp-res.openai.azure.com/openai/v1/chat/completions", completions.Url, "Foundry Chat Completions endpoint");
        Equal("Bearer azure-key", completions.Headers["Authorization"], "OpenAI client bearer key");
        using (var body = JsonDocument.Parse(completions.Body!))
            Equal("ds-prod", body.RootElement.GetProperty("model").GetString(), "deployment replaces the body model");

        // No base URL or resource name: the upstream configuration error, before any request.
        var none = new LiveEndpoint(_ => throw new InvalidOperationException("No request expected."));
        await using var refused = new LiveRpc(LiveArgs(root, "azure", "gpt-5.4"), new(Env(("AZURE_OPENAI_API_KEY", "azure-key")), () => none));
        Equal(2, await refused.Finish(), "missing endpoint exit");
        Check(refused.Error.ToString().Contains("Azure OpenAI base URL is required. Set AZURE_OPENAI_BASE_URL or AZURE_OPENAI_RESOURCE_NAME", StringComparison.Ordinal),
            "upstream endpoint message: " + refused.Error);
        // The OpenAI key does not authenticate azure.
        await using var keyless = new LiveRpc(LiveArgs(root, "azure", "gpt-5.4"), new(Env(("OPENAI_API_KEY", "k"), ("AZURE_OPENAI_RESOURCE_NAME", "r")), () => none));
        Equal(2, await keyless.Finish(), "missing key exit");
        Check(keyless.Error.ToString().Contains("AZURE_OPENAI_API_KEY", StringComparison.Ordinal), "missing key message: " + keyless.Error);
        Check(none.Snapshot().Length == 0, "refused sessions send nothing");
    });
}
