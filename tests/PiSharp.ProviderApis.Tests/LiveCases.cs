using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.AI;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

internal static partial class Program
{
    private static async Task LiveRoutes()
    {
        var directory = Temp("live"); var authPath = Path.Combine(directory, "auth.json");
        var far = 4_000_000_000_000L;
        await File.WriteAllTextAsync(authPath, $$$"""
            {"openai-codex":{"type":"oauth","refresh":"cr","access":"{{{CodexToken}}}","expires":{{{far}}},"accountId":"acct-123"},
             "github-copilot":{"type":"oauth","refresh":"gh","access":"{{{CopilotToken}}}","expires":{{{far}}},"availableModelIds":["kimi-k3"]},
             "openai":{"type":"oauth","refresh":"or","access":"chatgpt-access","expires":{{{far}}},"clientId":"c"},
             "cloudflare-ai-gateway":{"type":"api_key","key":"cf-key","env":{"CLOUDFLARE_ACCOUNT_ID":"acct","CLOUDFLARE_GATEWAY_ID":"gw"}} }
            """);
        var http = new FakeHttp();
        http.OnUrl("https://", _ => Json("""{"error":{"message":"fake peer refuses"}}""", HttpStatusCode.BadRequest));
        LiveSessionRuntime Runtime(Dictionary<string, string?> env, string? path = null) =>
            new(name => env.GetValueOrDefault(name), () => http, path ?? authPath, () => new HttpMessageInvoker(http, disposeHandler: false), new FixedTime(1_800_000_000_000))
            { HomeDirectory = directory, Transport = () => "sse" }; // One SSE request per provider; the Codex WebSocket has its own cases.
        async Task<Recorded> Send(string provider, string id, Dictionary<string, string?>? env = null)
        {
            var selection = LiveSessionSelection.Parse(provider, id, "512");
            var before = http.All.Count;
            await using var connection = selection.Connect(Runtime(env ?? new()));
            var events = await Collect(connection.CreateTransport(), new(selection.Model, [Entry("""{"role":"user","content":"Hi","timestamp":1}""")], 1));
            Check(events[^1] is StreamError && http.All.Count == before + 1, provider + " reached the fake peer: " + ErrorMessage(events[^1]));
            return http.All.Skip(before).Single();
        }
        var bedrock = await Send("amazon-bedrock", "amazon.nova-lite-v1:0", new() { ["AWS_ACCESS_KEY_ID"] = "AKIDLIVE", ["AWS_SECRET_ACCESS_KEY"] = "s" });
        Check(bedrock.Url == "https://bedrock-runtime.us-east-1.amazonaws.com/model/amazon.nova-lite-v1%3A0/converse-stream" &&
            bedrock.Header("authorization")!.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDLIVE/", StringComparison.Ordinal), "bedrock live route");
        Check(bedrock.Body.Contains("\"maxTokens\":512", StringComparison.Ordinal), "bedrock output cap");
        var codex = await Send("openai-codex", "gpt-5.5");
        Check(codex.Url == "https://chatgpt.com/backend-api/codex/responses" && codex.Header("chatgpt-account-id") == "acct-123" &&
            codex.Header("authorization") == "Bearer " + CodexToken, "codex live route");
        var copilot = await Send("github-copilot", "kimi-k3");
        Check(copilot.Url == "https://api.individual.githubcopilot.com/chat/completions" && copilot.Header("authorization") == "Bearer " + CopilotToken &&
            copilot.Header("x-initiator") == "user", "copilot live route");
        var hidden = await Throws<LiveSessionException>(() => Task.FromResult(LiveSessionSelection.Parse("github-copilot", "claude-sonnet-4.6", "512").Connect(Runtime(new()))));
        Equal("UnknownLiveModel", hidden.Code, "availableModelIds filter");
        var gateway = await Send("cloudflare-ai-gateway", "claude-fable-5");
        Check(gateway.Url == "https://gateway.ai.cloudflare.com/v1/acct/gw/anthropic/v1/messages?beta=true" && gateway.Header("cf-aig-authorization") == "Bearer cf-key" &&
            gateway.Header("x-api-key") is null, "gateway live route (stored key and env)");
        var workers = await Send("cloudflare-workers-ai", "@cf/deepseek-ai/deepseek-v4-flash-0731", new() { ["CLOUDFLARE_API_KEY"] = "env-key", ["CLOUDFLARE_ACCOUNT_ID"] = "env-acct" });
        Check(workers.Url == "https://api.cloudflare.com/client/v4/accounts/env-acct/ai/v1/chat/completions" && workers.Header("authorization") == "Bearer env-key", "workers live route");
        var vertex = await Send("google-vertex", "gemini-2.5-flash", new() { ["GOOGLE_CLOUD_API_KEY"] = "vertex-key" });
        Check(vertex.Url == "https://aiplatform.googleapis.com/v1beta1/publishers/google/models/gemini-2.5-flash:streamGenerateContent?alt=sse" &&
            vertex.Header("x-goog-api-key") == "vertex-key" && vertex.Header("authorization") is null, "vertex api key live route: " + vertex.Url);
        var openai = await Send("openai", "gpt-4.1");
        Check(openai.Url == "https://api.openai.com/v1/responses" && openai.Header("authorization") == "Bearer chatgpt-access", "stored ChatGPT OAuth on the openai route");
        // Unconfigured routed providers refuse the session with the source guidance.
        var missing = await Throws<LiveSessionException>(() => Task.FromResult(LiveSessionSelection.Parse("openai-codex", "gpt-5.5", "512").Connect(Runtime(new(), Path.Combine(directory, "none.json")))));
        Check(missing.Code == "MissingLiveApiKey" && missing.Message == "Run /login to sign in to OpenAI Codex before launching the live session.", "codex without login");
        var noVertex = await Throws<LiveSessionException>(() => Task.FromResult(LiveSessionSelection.Parse("google-vertex", "gemini-2.5-flash", "512").Connect(Runtime(new(), Path.Combine(directory, "none.json")))));
        Equal("MissingLiveApiKey", noVertex.Code, "vertex without credentials");
        // The A1 APIs pass live-route selection.
        foreach (var (provider, api) in new[] { ("amazon-bedrock", "bedrock-converse-stream"), ("openai-codex", "openai-codex-responses"), ("github-copilot", "anthropic-messages"),
            ("cloudflare-ai-gateway", "openai-responses"), ("cloudflare-workers-ai", "openai-completions"), ("google-vertex", "google-vertex") })
            Check(LiveSessionSelection.SupportedApi(provider, api), provider + " " + api);
        Check(!LiveSessionSelection.SupportedApi("cloudflare-workers-ai", "cloudflare-workers-ai-system-one"), "classifier api has no chat route");
    }

    private sealed class LockedWriter : StringWriter
    {
        private readonly object gate = new();
        public override void Write(char value) { lock (gate) base.Write(value); }
        public override void Write(string? value) { lock (gate) base.Write(value); }
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) { lock (gate) base.Write(buffer.Span); return Task.CompletedTask; }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override string ToString() { lock (gate) return base.ToString(); }
    }

    private static async Task WaitForView(TextWriter view, string text, int count = 1)
    {
        int Count() { var all = view.ToString()!.ReplaceLineEndings("\n"); var found = 0; for (var at = all.IndexOf(text, StringComparison.Ordinal); at >= 0; at = all.IndexOf(text, at + 1, StringComparison.Ordinal)) found++; return found; }
        for (var attempt = 0; attempt < 1000 && Count() < count; attempt++) await Task.Delay(10);
        Check(Count() >= count, "view never showed: " + text + "\n" + view);
    }

    /// <summary>A device-code OAuth fake for the CLI flow.</summary>
    private sealed class FakeDeviceOAuth : IProviderOAuth
    {
        public string Name => "GitHub Copilot"; public bool IsSubscription => true; public string? LoginLabel => null;
        public Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken)
        {
            interaction.Notify(new(AuthEventKind.DeviceCode) { UserCode = "ABCD-1234", VerificationUri = "https://github.com/login/device" });
            return Task.FromResult(new OAuthCredentialSnapshot("copilot", "gh", 4_000_000_000_000, new Dictionary<string, string>(),
                new Dictionary<string, JsonData> { ["availableModelIds"] = JsonData.Parse("""["gpt-5.5"]""") }));
        }
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken) => Task.FromResult(current);
        public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
    }

    private static async Task CliLogin()
    {
        var directory = Temp("cli-login"); var authPath = Path.Combine(directory, "auth.json");
        var store = new AuthJsonCredentialStore(authPath);
        var host = new ProviderLoginHost(store, () => new HttpMessageInvoker(new FakeHttp(), disposeHandler: true), _ => { }, null, "127.0.0.1", 0)
        { AllProviders = true, ReadEnvironment = name => name == "MISTRAL_API_KEY" ? "set" : null, OAuthOverride = (id, _) => id == "github-copilot" ? new FakeDeviceOAuth() : null };
        var view = new LockedWriter();
        using var frontend = new InteractiveSessionFrontend(view);
        frontend.Bind((_, _) => Task.CompletedTask); frontend.BindLogin(host);
        // /login: the auth-type selector, then the API-key provider selector (sorted by name, with the configured status).
        await frontend.LineAsync("/login", CancellationToken.None);
        await WaitForView(view, "3: Sign in with Radius • not configured");
        await frontend.LineAsync("2", CancellationToken.None);
        await WaitForView(view, "Select provider to configure:");
        await WaitForView(view, "(Enter a number to select, /cancel to cancel)", 2);
        var text = view.ToString()!;
        Check(Regex.IsMatch(text, @"\d+: Mistral ✓ env: MISTRAL_API_KEY"), "env status\n" + text);
        var number = Regex.Match(text, @"(\d+): OpenRouter • not configured").Groups[1].Value;
        Check(number.Length != 0 && Regex.IsMatch(text, @"1: Amazon Bedrock • not configured"), "sorted provider list\n" + text);
        await frontend.LineAsync(number, CancellationToken.None);
        await WaitForView(view, "Enter OpenRouter API key\n(/cancel to cancel)");
        await frontend.LineAsync("sk-or-test", CancellationToken.None);
        await frontend.LoginCompletion;
        await WaitForView(view, "Saved API key for OpenRouter. Credentials saved to " + store.AuthPath);
        // An exact provider with one method starts directly: the Cloudflare prompts.
        await frontend.LineAsync("/login cloudflare-ai-gateway", CancellationToken.None);
        await WaitForView(view, "Enter Cloudflare API key");
        await frontend.LineAsync("cf-key", CancellationToken.None);
        await WaitForView(view, "Enter Cloudflare account ID");
        await frontend.LineAsync("acct", CancellationToken.None);
        await WaitForView(view, "Enter Cloudflare AI Gateway ID");
        await frontend.LineAsync("gw", CancellationToken.None);
        await frontend.LoginCompletion;
        await WaitForView(view, "Saved API key for Cloudflare AI Gateway.");
        // A provider with both methods asks which; the OAuth device flow shows its code.
        await frontend.LineAsync("/login GitHub Copilot", CancellationToken.None);
        await WaitForView(view, "Select authentication method for GitHub Copilot:\n1: Sign in with an account\n2: Sign in with an API key");
        await frontend.LineAsync("1", CancellationToken.None);
        await frontend.LoginCompletion;
        await WaitForView(view, "https://github.com/login/device\nEnter code: ABCD-1234\nWaiting for authentication...");
        await WaitForView(view, "Logged in to GitHub Copilot. Credentials saved to " + store.AuthPath);
        var auth = (await File.ReadAllTextAsync(authPath)).ReplaceLineEndings("\n");
        Check(auth.Contains("\"openrouter\": {\n    \"type\": \"api_key\",\n    \"key\": \"sk-or-test\"\n  }", StringComparison.Ordinal) &&
            auth.Contains("\"CLOUDFLARE_GATEWAY_ID\": \"gw\"", StringComparison.Ordinal) && auth.Contains("\"availableModelIds\": [\n      \"gpt-5.5\"\n    ]", StringComparison.Ordinal), "auth.json\n" + auth);
        // Unknown references filter the provider selector; /cancel ends the login.
        await frontend.LineAsync("/login nothing-matches", CancellationToken.None);
        await frontend.LoginCompletion;
        await WaitForView(view, "[error] No login provider matches \"nothing-matches\".");
        // /logout lists the stored providers by name.
        await frontend.LineAsync("/logout", CancellationToken.None);
        await WaitForView(view, "Select provider to logout:\n1: Cloudflare AI Gateway ✓ configured\n2: GitHub Copilot ✓ configured\n3: OpenRouter ✓ configured");
        await frontend.LineAsync("2", CancellationToken.None);
        await frontend.LoginCompletion;
        await WaitForView(view, "Logged out of GitHub Copilot");
        Check(!(await File.ReadAllTextAsync(authPath)).Contains("github-copilot", StringComparison.Ordinal), "credential removed");
    }
}
