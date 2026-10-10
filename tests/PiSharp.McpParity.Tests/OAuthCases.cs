using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// Upstream: extensions/mcp/oauth.ts (withRefreshLock, createMcpAuthProvider: REFRESH_SKEW_MS, a shared refresh, tokens changed
// meanwhile are used without refreshing), extensions/mcp/runtime.ts (auth.provider token read on every request), index.ts (turn_start
// reconnectSignedIn), cli.ts (login), core/resolve-config-value.ts (`!command` values) and test/mcp-oauth-refresh.test.ts
// ("refreshes once when several processes find the same token rejected", "waits for a running refresh to save the new tokens").
internal static partial class Program
{
    private static readonly Uri McpServerUrl = new("https://mcp.example.test/mcp");
    private const string OAuthIssuer = "https://issuer.example.test/";
    private static readonly string OAuthMetadata = "{\"issuer\":\"" + OAuthIssuer + "\",\"authorization_endpoint\":\"" + OAuthIssuer + "authorize\",\"token_endpoint\":\"" +
        OAuthIssuer + "token\",\"registration_endpoint\":\"" + OAuthIssuer + "register\",\"response_types_supported\":[\"code\"],\"code_challenge_methods_supported\":[\"S256\"]," +
        "\"token_endpoint_auth_methods_supported\":[\"none\",\"client_secret_post\"]}";

    /// <summary>An MCP server at mcp.example.test that accepts only issued (or the configured) bearer tokens, and its authorization
    /// server at issuer.example.test.</summary>
    internal sealed class FakeOAuthServer : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Method, Uri Uri, string Body, string? Authorization)> Log = new();
        public readonly ConcurrentDictionary<string, bool> Accepted = new(StringComparer.Ordinal);
        public TimeSpan TokenDelay = TimeSpan.Zero;
        /// <summary>The 401 challenge; and tokens whose calls are refused with <see cref="StepUpChallenge"/> (403 insufficient_scope).</summary>
        public string Challenge = "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"";
        public readonly ConcurrentDictionary<string, bool> Limited = new(StringComparer.Ordinal);
        public string StepUpChallenge = "Bearer error=\"insufficient_scope\", scope=\"docs.write\", resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource-alt/mcp\"";
        private int issued;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            var authorization = request.Headers.Authorization?.ToString();
            var uri = request.RequestUri!;
            Log.Enqueue((request.Method.Method, uri, body, authorization));
            if (uri.Host is "mcp.example.test" or "remote.example.test")
            {
                if (uri.AbsolutePath == "/mcp") return Mcp(request, body, authorization);
                if (uri.AbsolutePath is "/.well-known/oauth-protected-resource/mcp" or "/.well-known/oauth-protected-resource-alt/mcp")
                    return Json(200, "{\"resource\":\"" + McpServerUrl.AbsoluteUri + "\",\"authorization_servers\":[\"" + OAuthIssuer + "\"]}");
                return Json(404, "{}");
            }
            switch (uri.AbsolutePath)
            {
                case "/.well-known/oauth-authorization-server": return Json(200, OAuthMetadata);
                case "/register":
                    return Json(201, "{\"client_id\":\"registered\",\"redirect_uris\":" + JsonDocument.Parse(body).RootElement.GetProperty("redirect_uris").GetRawText() + "}");
                case "/token":
                    if (TokenDelay > TimeSpan.Zero) await Task.Delay(TokenDelay, token);
                    var access = (body.Contains("grant_type=refresh_token", StringComparison.Ordinal) ? "issued-refresh-" : "issued-code-") + Interlocked.Increment(ref issued);
                    Accepted[access] = true;
                    return Json(200, "{\"access_token\":\"" + access + "\",\"token_type\":\"Bearer\",\"refresh_token\":\"refresh-next\",\"expires_in\":3600}");
                default: return Json(404, "{}");
            }
        }
        private HttpResponseMessage Mcp(HttpRequestMessage request, string body, string? authorization)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.MethodNotAllowed);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.OK);
            if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || !Accepted.ContainsKey(authorization["Bearer ".Length..]))
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", Challenge);
                return denied;
            }
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = json.RootElement.GetProperty("method").GetString();
            if (method == "tools/call" && Limited.ContainsKey(authorization["Bearer ".Length..]))
            {
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
                forbidden.Headers.TryAddWithoutValidation("WWW-Authenticate", StepUpChallenge);
                return forbidden;
            }
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"remote","version":"1"},"capabilities":{"tools":{}}}""",
                "tools/list" => """{"tools":[{"name":"lookup","description":"Looks up.","inputSchema":{"type":"object"}}]}""",
                "tools/call" => """{"content":[{"type":"text","text":"remote answered."}]}""",
                _ => "{}"
            };
            var response = Json(200, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}");
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "session-1");
            return response;
        }
        internal int TokenRequests => Log.Count(row => row.Uri.AbsolutePath == "/token");
    }

    private static HttpResponseMessage Json(int status, string text) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    /// <summary>The user approves in a browser, which follows the redirect to the loopback callback.</summary>
    private static Action<string> Browser(ConcurrentBag<Task> pages) => url => pages.Add(Task.Run(async () =>
    {
        var authorize = new Uri(url);
        var query = System.Web.HttpUtility.ParseQueryString(authorize.Query);
        using var client = new HttpClient();
        using var page = await client.GetAsync(query["redirect_uri"] + "?code=granted&state=" + Uri.EscapeDataString(query["state"]!));
    }));

    // withRefreshLock: one lock file per server (by its credential key) in the agent directory; a lock its holder stopped renewing
    // (older than 20 s) is taken over, also the original's lock directory; a held lock is waited for.
    private static async Task RefreshLockFile()
    {
        var root = Path.GetFullPath(Temp("lock-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
        try
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("mcp__docs_v2|https://mcp.example.test/mcp")))[..16];
            Equal(Path.Combine(root, $"mcp-auth-refresh-{hash}.lock"), McpOAuthRefreshLock.PathFor(root, "docs-v2", McpServerUrl), "lock path");
            var acquire = McpOAuthRefreshLock.For(root, "docs-v2", McpServerUrl);
            var path = McpOAuthRefreshLock.PathFor(root, "docs-v2", McpServerUrl);
            var first = await acquire(CancellationToken.None);
            Check(File.Exists(path), "lock file while held");
            var second = acquire(CancellationToken.None);
            await Task.Delay(400);
            Check(!second.IsCompleted, "a held lock is waited for");
            await first.DisposeAsync();
            await (await second.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
            Check(!File.Exists(path), "released");
            // A stale lock file (its holder was killed) and the original's stale lock directory are taken over.
            File.WriteAllText(path, ""); File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromSeconds(30));
            await (await acquire(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
            Directory.CreateDirectory(path);
            var waiting = acquire(CancellationToken.None);
            await Task.Delay(300);
            Check(!waiting.IsCompleted, "the original's fresh lock directory is waited for");
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromSeconds(30));
            await (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
            Check(!Directory.Exists(path) && !File.Exists(path), "stale directory taken over and released");
        }
        finally { Directory.Delete(root, true); }
    }

    private static (McpAdmittedOAuthRefreshAdapter Adapter, McpAdmittedHttpAuthentication Authentication) Adapter(string agent, FakeOAuthServer server,
        bool locked, Func<double>? clock = null)
    {
        var store = new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(agent)).ForServer("docs", McpServerUrl);
        var adapter = new McpAdmittedOAuthRefreshAdapter(McpServerUrl, store, async (request, token) =>
        {
            using var message = new HttpRequestMessage(request.Method, request.Endpoint);
            if (request.Body is not null) message.Content = new StringContent(request.Body, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var client = new HttpClient(server, disposeHandler: false);
            using var response = await client.SendAsync(message, token);
            return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(token));
        }, clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), new McpOAuthCancellationAdmission())
        { RefreshLock = locked ? McpOAuthRefreshLock.For(agent, "docs", McpServerUrl) : null };
        return (adapter, adapter.CreateAuthentication());
    }

    private static async Task SaveState(string agent, string access, double? expireAt = null) =>
        await new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(agent)).ForServer("docs", McpServerUrl).SaveAsync(new(McpServerUrl.AbsoluteUri,
            JsonData.Parse("{\"client_id\":\"registered\"}"), new(access, "Bearer", RefreshToken: "refresh-0"), expireAt,
            Discovery: JsonData.Parse("{\"authorizationServerUrl\":\"" + OAuthIssuer + "\",\"authorizationServerMetadata\":" + OAuthMetadata + "}")));

    // Two processes (here: two adapters over the same agent directory) find the same token rejected: with the lock, one refreshes and
    // the other uses the saved tokens; without it, both would refresh and the second would spend the rotated refresh token.
    private static async Task RefreshLockSerializes()
    {
        foreach (var locked in new[] { true, false })
        {
            var agent = Path.GetFullPath(Temp("refresh-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(agent);
            try
            {
                await SaveState(agent, "old");
                var server = new FakeOAuthServer { TokenDelay = TimeSpan.FromMilliseconds(400) };
                var (_, first) = Adapter(agent, server, locked); var (_, second) = Adapter(agent, server, locked);
                using var rejected1 = new HttpResponseMessage(HttpStatusCode.Unauthorized); using var rejected2 = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                await Task.WhenAll(first.OnUnauthorized!(new(rejected1, McpServerUrl, "old"), CancellationToken.None).AsTask(),
                    second.OnUnauthorized!(new(rejected2, McpServerUrl, "old"), CancellationToken.None).AsTask());
                Equal(locked ? 1 : 2, server.TokenRequests, locked ? "one refresh with the lock" : "two refreshes without it");
                var token = await first.Token(CancellationToken.None);
                Equal(token, await second.Token(CancellationToken.None), "both use the saved tokens");
                Check(token!.StartsWith("issued-refresh-", StringComparison.Ordinal), token);
                Check(!Directory.EnumerateFileSystemEntries(agent, "mcp-auth-refresh-*").Any(), "lock released");
            }
            finally { Directory.Delete(agent, true); }
        }
    }

    // createMcpAuthProvider token(): an access token within 30 s of expiry is refreshed before it is sent; a fresh one is not.
    private static async Task RefreshBeforeExpiry()
    {
        var agent = Path.GetFullPath(Temp("expiry-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(agent);
        try
        {
            const double Now = 1_000_000;
            var server = new FakeOAuthServer();
            await SaveState(agent, "fresh", Now + 60_000);
            Equal("fresh", await Adapter(agent, server, true, () => Now).Authentication.Token(CancellationToken.None), "not near expiry");
            Equal(0, server.TokenRequests, "no refresh");
            await SaveState(agent, "expiring", Now + 20_000);
            var token = await Adapter(agent, server, true, () => Now).Authentication.Token(CancellationToken.None);
            Check(token!.StartsWith("issued-refresh-", StringComparison.Ordinal) && server.TokenRequests == 1, "refreshed before sending: " + token);
        }
        finally { Directory.Delete(agent, true); }
    }

    // runtime.ts auth.provider: the server receives the provider's current login token on every request (here from the host's provider
    // token seam); without the seam the CLI's credentials resolve it (the environment key here).
    private static Task AuthProviderServer() => WithRoot("auth-provider",
        """{"mcpServers":{"remote":{"url":"https://mcp.example.test/mcp","exposure":"direct","auth":{"provider":"anthropic"}}}}""", async fixture =>
    {
        var server = new FakeOAuthServer(); server.Accepted["provider-token"] = true;
        var provider = new Endpoint();
        var asked = new ConcurrentQueue<string>();
        var host = fixture.Host() with
        {
            CreateHttpHandler = () => server,
            ProviderToken = (name, _) => { asked.Enqueue(name); return ValueTask.FromResult<string?>("provider-token"); }
        };
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, host))
        {
            await rpc.Prompt("p1", "hello");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names([], McpDiagnostics(rpc.Error.ToString()), "nothing reported");
        }
        Check(ToolNames(provider.Snapshot().Single()).Contains("mcp__remote__lookup"), "the provider-authenticated server's tool is declared");
        Check(server.Log.Where(row => row.Uri.AbsolutePath == "/mcp" && row.Method == "POST").All(row => row.Authorization == "Bearer provider-token"), "the token on every request");
        Check(asked.All(name => name == "anthropic") && !asked.IsEmpty, "the configured provider is asked");
        Check(!server.Log.Any(row => row.Uri.Host == "issuer.example.test"), "no OAuth");
        var runtime = new LiveSessionRuntime(name => name switch { "ANTHROPIC_API_KEY" => "env-anthropic", "OPENAI_API_KEY" => "env-openai", _ => null }, () => new Endpoint());
        Equal("env-anthropic", await McpProviderTokens.ResolveAsync(runtime, "anthropic", CancellationToken.None), "anthropic from the CLI credentials");
        Equal("env-openai", await McpProviderTokens.ResolveAsync(runtime, "openai", CancellationToken.None), "openai from the CLI credentials");
        Equal(null, await McpProviderTokens.ResolveAsync(runtime, "no-such-provider", CancellationToken.None), "unknown provider");
    });

    // index.ts turn_start reconnectSignedIn: a server that needed a sign-in reconnects once `mcp login` stored credentials in another
    // process, before the next prompt.
    private static Task ExternalSignIn() => WithRoot("external-sign-in", """{"mcpServers":{"remote":{"url":"https://mcp.example.test/mcp","exposure":"direct"}}}""", async fixture =>
    {
        var server = new FakeOAuthServer();
        var provider = new Endpoint();
        await using (var rpc = new Rpc(Args(fixture.Root, "new-memory"), provider, fixture.Host() with { CreateHttpHandler = () => server }))
        {
            await rpc.Prompt("p1", "hello");
            await Settled(fixture, rpc, 1);
            var manager = await fixture.Manager.Task;
            Equal("needs-auth", manager.Servers.Single().State, "needs a sign-in");
            // `mcp login remote` elsewhere stores tokens the server accepts.
            server.Accepted["issued-external"] = true;
            await new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(fixture.Agent)).ForServer("remote", McpServerUrl)
                .SaveAsync(new(McpServerUrl.AbsoluteUri, Tokens: new("issued-external", "Bearer")));
            await rpc.Prompt("p2", "again");
            Equal("connected", manager.Servers.Single().State, "reconnected");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            Names(["MCP servers need attention:\n  remote: needs sign-in (run \"PiSharp.Cli mcp login remote\")"], McpDiagnostics(rpc.Error.ToString()), "reported once");
        }
        var requests = provider.Snapshot();
        Check(!ToolNames(requests[0]).Contains("mcp__remote__lookup") && ToolNames(requests[1]).Contains("mcp__remote__lookup"), "declared after the sign-in elsewhere");
    });

    // oauth.clientSecret may be a `!command` (owner decision 0004): `mcp login` connects first, then signs in with the command's output
    // as the client secret, and reports the tools.
    private static Task ClientSecretCommand() => WithRoot("client-secret",
        """{"mcpServers":{"remote":{"url":"https://mcp.example.test/mcp","oauth":{"clientId":"confidential","clientSecret":"!echo s3cret"}}}}""", async fixture =>
    {
        var server = new FakeOAuthServer(); var pages = new ConcurrentBag<Task>();
        using var output = new StringWriter { NewLine = "\n" }; using var error = new StringWriter { NewLine = "\n" };
        var code = await McpCommand.RunAsync(["login", "remote", "--timeout", "30"], output, error, new McpCommandOptions(fixture.Project, fixture.Agent)
        { HttpHandler = server, OpenUrl = Browser(pages), ReadRedirectUrl = WaitForCancellation });
        await Task.WhenAll(pages);
        Equal((0, ""), (code, error.ToString()), "login");
        Check(output.ToString().EndsWith("Signed in to MCP server \"remote\" (1 tools).\n", StringComparison.Ordinal), output.ToString());
        var exchange = server.Log.Single(row => row.Uri.AbsolutePath == "/token").Body;
        Check(exchange.Contains("client_id=confidential", StringComparison.Ordinal) && exchange.Contains("client_secret=s3cret", StringComparison.Ordinal), "secret from the command: " + exchange);
        Check(!server.Log.Any(row => row.Uri.AbsolutePath == "/register"), "a configured client does not register");
    });

    private static async Task<string?> WaitForCancellation(CancellationToken token)
    {
        try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        return null;
    }
}
