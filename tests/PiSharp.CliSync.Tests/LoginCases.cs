using System.Text;
using System.Text.Json;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

// auth.anthropic /login surface (interactive-mode.ts /login, login-dialog.ts, auth-storage.ts, resolve.ts): the interactive login
// drives AnthropicOAuth against a fake token endpoint, stores auth.json and a later resolution refreshes it. Authored expectations;
// no network: the token endpoint is an in-process handler and no browser is opened.
internal static partial class Program
{
    private sealed class FixedTime(long unixMilliseconds) : TimeProvider
    {
        public long Now = unixMilliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    /// <summary>Fake platform.claude.com token endpoint: records each JSON body and answers with the next scripted token set.</summary>
    private sealed class TokenEndpoint(params string[] responses) : HttpMessageHandler
    {
        public readonly List<JsonElement> Bodies = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri == new Uri(AnthropicOAuth.TokenUrl), "unexpected OAuth request " + request.RequestUri);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            lock (Bodies) Bodies.Add(body);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>The login renders from its own task; reads and writes of the view text are serialized.</summary>
    private sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();
        public override void Write(char value) { lock (_gate) base.Write(value); }
        public override void Write(string? value) { lock (_gate) base.Write(value); }
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        { lock (_gate) base.Write(buffer.Span); return Task.CompletedTask; }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override string ToString() { lock (_gate) return base.ToString(); }
    }

    private static async Task WaitFor(TextWriter view, string text, int count = 1)
    {
        int Count()
        {
            var all = view.ToString()!; var found = 0;
            for (var at = all.IndexOf(text, StringComparison.Ordinal); at >= 0; at = all.IndexOf(text, at + 1, StringComparison.Ordinal)) found++;
            return found;
        }
        for (var attempt = 0; attempt < 1000 && Count() < count; attempt++) await Task.Delay(10);
        Check(Count() >= count, "view never showed: " + text + "\n" + view);
    }

    private static async Task LoginStoresAndRefreshes()
    {
        var directory = Temp("login-" + Guid.NewGuid().ToString("N")); var authPath = Path.Combine(directory, "agent", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
        // Another provider's credential is preserved byte for byte as JSON.
        await File.WriteAllTextAsync(authPath, "{\"openai\":{\"type\":\"api_key\",\"key\":\"kept\"}}");
        try
        {
            var time = new FixedTime(1_800_000_000_000);
            var endpoint = new TokenEndpoint(
                """{"access_token":"sk-ant-oat01-first","refresh_token":"refresh-1","expires_in":3600}""",
                """{"access_token":"sk-ant-oat01-second","refresh_token":"refresh-2","expires_in":7200}""");
            var opened = new List<string>();
            var store = new AuthJsonCredentialStore(authPath, time);
            var host = new ProviderLoginHost(store, () => new HttpMessageInvoker(endpoint, disposeHandler: false), opened.Add, time, "127.0.0.1", 0);
            var sent = new List<string>(); var view = new LockedWriter();
            using (var frontend = new InteractiveSessionFrontend(view))
            {
                frontend.Bind((record, _) => { sent.Add(record.ToString()); return Task.CompletedTask; });
                frontend.BindLogin(host);
                await frontend.LineAsync("/login openai", CancellationToken.None);
                // Copy-code login: choose the method, then paste the code Anthropic shows.
                await frontend.LineAsync("/login", CancellationToken.None);
                await WaitFor(view, "(Enter a number to select, /cancel to cancel)");
                await frontend.LineAsync("3", CancellationToken.None);
                await frontend.LineAsync("2", CancellationToken.None);
                await WaitFor(view, "Paste the code Anthropic shows after you sign in:");
                await frontend.LineAsync("code-123", CancellationToken.None);
                await frontend.LoginCompletion;
                // A cancelled login stores nothing.
                await frontend.LineAsync("/login anthropic", CancellationToken.None);
                await WaitFor(view, "(Enter a number to select, /cancel to cancel)", 2);
                await frontend.LineAsync("/cancel", CancellationToken.None);
                await frontend.LoginCompletion;
            }
            Check(sent.Count == 0, "login lines reached RPC: " + string.Join(",", sent));
            var text = view.ToString()!.ReplaceLineEndings("\n");
            var url = opened.Single();
            Check(url.StartsWith(AnthropicOAuth.AuthorizeUrl + "?code=true&client_id=", StringComparison.Ordinal) &&
                url.Contains("&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&", StringComparison.Ordinal), "copy-code authorize URL");
            var state = url[(url.LastIndexOf("&state=", StringComparison.Ordinal) + 7)..];
            Equal(string.Join("\n",
                "[error] No login provider matches \"openai\". Available: anthropic.",
                "Login to Anthropic",
                "Select Anthropic login method:", "1: Browser login (default)", "2: Copy code login (headless)", "(Enter a number to select, /cancel to cancel)",
                "[login] Enter a number from 1 to 2, or /cancel.",
                url, "Complete login in your browser, then copy the code Anthropic shows and paste it here.",
                "Paste the code Anthropic shows after you sign in:", "(/cancel to cancel)",
                "Exchanging authorization code for tokens...",
                "Logged in to Anthropic",
                "Login to Anthropic",
                "Select Anthropic login method:", "1: Browser login (default)", "2: Copy code login (headless)", "(Enter a number to select, /cancel to cancel)",
                "Login cancelled", ""), text, "login view");
            var exchange = endpoint.Bodies[0];
            Equal("authorization_code", exchange.GetProperty("grant_type").GetString(), "grant");
            Equal("code-123", exchange.GetProperty("code").GetString(), "code");
            Equal(state, exchange.GetProperty("state").GetString(), "state is the verifier");
            Equal(AnthropicOAuth.CopyCodeRedirectUri, exchange.GetProperty("redirect_uri").GetString(), "redirect");
            var expires = 1_800_000_000_000 + 3_600_000 - 300_000;
            Equal("{\n  \"openai\": {\n    \"type\": \"api_key\",\n    \"key\": \"kept\"\n  },\n  \"anthropic\": {\n    \"type\": \"oauth\",\n    \"refresh\": \"refresh-1\",\n" +
                "    \"access\": \"sk-ant-oat01-first\",\n    \"expires\": " + expires + "\n  }\n}", await File.ReadAllTextAsync(authPath), "auth.json");
            Check(!File.Exists(authPath + ".lock") && !Directory.Exists(authPath + ".lock"), "auth.json lock left behind");

            // Later requests: a valid stored credential is used as is; one expiring within five minutes is refreshed and persisted.
            using var http = new HttpMessageInvoker(endpoint, disposeHandler: false);
            var resolved = await StoredAnthropicAuthentication.ResolveAsync(store, http, time, CancellationToken.None);
            Check(resolved is { Diagnostic: AuthenticationDiagnostic.Resolved, Authentication: { Kind: AuthenticationKind.ApiKey, Secret: "sk-ant-oat01-first" } },
                "stored access token in the apiKey channel");
            Equal(1, endpoint.Bodies.Count, "no refresh while valid");
            time.Now = expires - 300_000;
            resolved = await StoredAnthropicAuthentication.ResolveAsync(store, http, time, CancellationToken.None);
            Equal("sk-ant-oat01-second", resolved!.Authentication!.Secret, "refreshed access token");
            Equal("refresh_token", endpoint.Bodies[1].GetProperty("grant_type").GetString(), "refresh grant");
            Equal("refresh-1", endpoint.Bodies[1].GetProperty("refresh_token").GetString(), "refresh token sent");
            var stored = await store.ReadAsync("anthropic", CancellationToken.None);
            Check(stored is { Access: "sk-ant-oat01-second", Refresh: "refresh-2" } && stored.ExpiresUnixMilliseconds == time.Now + 7_200_000 - 300_000,
                "rotated credential persisted");
            await store.DeleteAsync("anthropic", CancellationToken.None);
            Equal("{\n  \"openai\": {\n    \"type\": \"api_key\",\n    \"key\": \"kept\"\n  }\n}", await File.ReadAllTextAsync(authPath), "delete keeps others");
            Check(await StoredAnthropicAuthentication.ResolveAsync(store, http, time, CancellationToken.None) is null, "no stored credential falls back");
            // Source getAuthPath: PI_CODING_AGENT_DIR, else ~/.pi/agent.
            Equal(Path.Combine(directory, "custom", "auth.json"), AuthJsonCredentialStore.DefaultPath(
                new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = Path.Combine(directory, "custom") }, directory), "agent dir override");
            Equal(Path.Combine(directory, ".pi", "agent", "auth.json"), AuthJsonCredentialStore.DefaultPath(new Dictionary<string, string?>(), directory), "default agent dir");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task BrowserLoginPastedRedirect()
    {
        var directory = Temp("login-browser-" + Guid.NewGuid().ToString("N")); var authPath = Path.Combine(directory, "auth.json");
        Directory.CreateDirectory(directory);
        try
        {
            var endpoint = new TokenEndpoint("""{"access_token":"sk-ant-oat01-browser","refresh_token":"refresh-b","expires_in":3600}""");
            var opened = new List<string>(); var view = new LockedWriter();
            var host = new ProviderLoginHost(new AuthJsonCredentialStore(authPath), () => new HttpMessageInvoker(endpoint, disposeHandler: false),
                opened.Add, new FixedTime(1_800_000_000_000), "127.0.0.1", 0);
            using var frontend = new InteractiveSessionFrontend(view);
            frontend.Bind((_, _) => Task.CompletedTask); frontend.BindLogin(host);
            await frontend.LineAsync("/login", CancellationToken.None);
            await WaitFor(view, "(Enter a number to select, /cancel to cancel)");
            await frontend.LineAsync("1", CancellationToken.None);
            await WaitFor(view, "Complete login in your browser, or paste the authorization code / redirect URL here:");
            var url = opened.Single();
            var redirect = Uri.UnescapeDataString(url.Split("&redirect_uri=")[1].Split('&')[0]);
            Check(redirect.StartsWith("http://localhost:", StringComparison.Ordinal) && redirect.EndsWith("/callback", StringComparison.Ordinal), "loopback redirect " + redirect);
            var state = url[(url.LastIndexOf("&state=", StringComparison.Ordinal) + 7)..];
            // The final redirect URL pasted from another machine carries the code and the state.
            await frontend.LineAsync(redirect + "?code=pasted-code&state=" + state, CancellationToken.None);
            await frontend.LoginCompletion;
            Check(view.ToString()!.ReplaceLineEndings("\n").EndsWith("Exchanging authorization code for tokens...\nLogged in to Anthropic\n", StringComparison.Ordinal), "browser login view:\n" + view);
            Equal(redirect, endpoint.Bodies.Single().GetProperty("redirect_uri").GetString(), "bound redirect used for the exchange");
            Equal("pasted-code", endpoint.Bodies.Single().GetProperty("code").GetString(), "pasted code");
            var stored = await new AuthJsonCredentialStore(authPath).ReadAsync("anthropic", CancellationToken.None);
            Check(stored is { Access: "sk-ant-oat01-browser", Refresh: "refresh-b" }, "browser credential stored");
            // A wrong pasted state fails the login with the source message.
            await frontend.LineAsync("/login", CancellationToken.None);
            await WaitFor(view, "(Enter a number to select, /cancel to cancel)", 2);
            await frontend.LineAsync("1", CancellationToken.None);
            for (var attempt = 0; attempt < 1000 && opened.Count < 2; attempt++) await Task.Delay(10);
            await WaitFor(view, "(/cancel to cancel)", 2);
            await frontend.LineAsync("other-code#not-the-state", CancellationToken.None);
            await frontend.LoginCompletion;
            Check(view.ToString()!.ReplaceLineEndings("\n").EndsWith("[error] Failed to login to Anthropic: OAuth state mismatch\n", StringComparison.Ordinal), "state mismatch view");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
