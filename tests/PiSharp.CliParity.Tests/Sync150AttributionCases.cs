using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Pi;
using PiSharp.CodingAgent.Diagnostics;
using PiSharp.Cli.Models;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/client-identity.ts (getClientIdentity),
// core/sdk.ts (identity on every request; transformHeaders before the identity headers), packages/ai/src/utils/identity.ts
// (getAttributionHeaders, getIdentityHeaders) and the removed core/provider-attribution.ts. Pi 1.5.0 attributes requests to pi
// (PI_ATTRIBUTION: pi, https://pi.dev) on OpenRouter, NVIDIA NIM and OpenCode while install telemetry is on. PiSharp sends no
// attribution whatever enableInstallTelemetry says (owner decision 2026-11-05, "Use safe defaults", core.client-identity-attribution)
// and keeps x-opencode-client: pisharp as an intentional difference. Authored from the source, never captured from an upstream run;
// every provider is a fake endpoint in process.
#pragma warning disable PISHARP_IDENTITY001
internal static partial class Program
{
    private static readonly string[] AttributionHeaders = ["HTTP-Referer", "X-OpenRouter-Title", "X-OpenRouter-Categories", "X-BILLING-INVOKE-ORIGIN"];

    private static IEnumerable<(string, Func<Task>)> Sync150AttributionCases() =>
    [
        ("sync150.no-attribution-on-any-provider-route-with-install-telemetry-on", () => NoAttributionOnEveryRoute(true)),
        ("sync150.no-attribution-on-any-provider-route-with-install-telemetry-off", () => NoAttributionOnEveryRoute(false)),
        ("sync150.user-agent-comes-from-the-shared-formatter-at-every-call-site", UserAgentCallSites),
    ];

    /// <summary>
    /// <c>pisharp -p</c> against every built-in provider that authenticates with an environment key (one chat model per API the
    /// provider declares, so every protocol route runs), Cloudflare Workers AI and AI Gateway, and custom providers whose base URL is
    /// OpenRouter's, NVIDIA NIM's or OpenCode's (attribution also applies by base URL). No request carries an attribution header or
    /// names pi.dev; x-opencode-client, where sent, is pisharp; and the User-Agent is the shared detailed formatter's.
    /// </summary>
    private static async Task NoAttributionOnEveryRoute(bool telemetry)
    {
        using var sandbox = new Sandbox("attribution-" + (telemetry ? "on" : "off"));
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{\"enableInstallTelemetry\":" + (telemetry ? "true" : "false") + "}");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), """
            {"providers":{
            "custom-openrouter":{"baseUrl":"https://openrouter.ai/api/v1","api":"openai-completions","apiKey":"custom-key","models":[{"id":"m"}]},
            "custom-nvidia":{"baseUrl":"https://integrate.api.nvidia.com/v1","api":"openai-completions","apiKey":"custom-key","models":[{"id":"m"}]},
            "custom-opencode":{"baseUrl":"https://opencode.ai/zen/v1","api":"openai-completions","apiKey":"custom-key","models":[{"id":"m"}]}}}
            """);
        var targets = new List<(string Provider, string Model)> { ("custom-openrouter", "m"), ("custom-nvidia", "m"), ("custom-opencode", "m") };
        foreach (var provider in BuiltinProviders.All)
        {
            if (provider.Auth is not (ProviderAuthKind.EnvironmentKey or ProviderAuthKind.Anthropic or ProviderAuthKind.CloudflareWorkersAI or ProviderAuthKind.CloudflareAIGateway) ||
                provider.ApiKeyVariables.IsDefaultOrEmpty || !BuiltinModelCatalog.Has(provider.Id)) continue;
            // Anthropic keeps the sandbox's API key only: no subscription or OAuth token.
            if (provider.Id != "anthropic") foreach (var variable in provider.ApiKeyVariables) sandbox.Vars[variable] = "test-key";
            // The Completions route refuses a maxTokens above 1,000,000 (MaximumTokenMagnitude) before sending, so those models are passed over.
            foreach (var model in BuiltinModelCatalog.Get(provider.Id).Models.Where(model => model.Type == CatalogModelType.Chat &&
                    !(model.Raw.Value.TryGetProperty("maxTokens", out var max) && max.GetDouble() > 1_000_000))
                .GroupBy(model => model.DeclaredApi, StringComparer.Ordinal).Select(group => group.First()))
                targets.Add((provider.Id, model.Id));
        }
        sandbox.Vars["CLOUDFLARE_ACCOUNT_ID"] = "account"; sandbox.Vars["CLOUDFLARE_GATEWAY_ID"] = "gateway";
        sandbox.Vars["AZURE_OPENAI_BASE_URL"] = "https://example.openai.azure.com/openai/v1";
        // A non-retryable refusal: each run sends one request and stops.
        sandbox.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        { Content = new StringContent("{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"refused\"}}", Encoding.UTF8, "application/json") };
        // The fake endpoint records a header as .NET enumerates it (product tokens joined with ", ").
        var userAgent = Recorded(ClientIdentities.FormatUserAgent(null));
        var problems = new List<string>(); var reached = new HashSet<string>(StringComparer.Ordinal); var refused = new List<string>();
        foreach (var (provider, model) in targets)
        {
            var before = sandbox.Requests.Count;
            var (_, _, stderr) = await sandbox.Run("-p", "--no-session", "--provider", provider, "--model", model, "hi");
            Seen[] sent; lock (sandbox.Requests) sent = [.. sandbox.Requests.Skip(before)];
            if (sent.Length > 0) reached.Add(provider); else refused.Add($"{provider}/{model}: {stderr.Trim()}");
            foreach (var request in sent)
            {
                var where = $"{provider}/{model} {request.Url}";
                foreach (var name in AttributionHeaders)
                    if (request.Headers.ContainsKey(name)) problems.Add($"{where}: {name}: {request.Headers[name]}");
                if (request.Headers.TryGetValue("x-opencode-client", out var client) && client != "pisharp") problems.Add($"{where}: x-opencode-client: {client}");
                foreach (var (name, value) in request.Headers)
                    if (value.Contains("pi.dev", StringComparison.OrdinalIgnoreCase)) problems.Add($"{where}: {name} names pi.dev: {value}");
                // GitHub Copilot's requests identify as its editor integration (Copilot's dynamic headers), as in Pi.
                var expected = provider == "github-copilot" ? null : userAgent;
                if (expected is not null && request.Headers.GetValueOrDefault("User-Agent") != expected)
                    problems.Add($"{where}: User-Agent {request.Headers.GetValueOrDefault("User-Agent") ?? "(none)"}");
            }
        }
        Check(problems.Count == 0, string.Join("\n", problems));
        // The attribution targets, by provider id and by base URL, and both Cloudflare routes were reached.
        foreach (var provider in new[] { "openrouter", "nvidia", "opencode", "opencode-go", "cloudflare-workers-ai", "cloudflare-ai-gateway",
            "custom-openrouter", "custom-nvidia", "custom-opencode", "anthropic", "openai", "google", "mistral" })
            Check(reached.Contains(provider), $"no request reached {provider}; not sent: {string.Join("; ", refused)}");
        Equal(targets.Select(target => target.Provider).Distinct().Count(), reached.Count, "providers that sent a request; not sent: " + string.Join("; ", refused));
    }

    private static string Recorded(string userAgent)
    {
        using var probe = new HttpRequestMessage();
        probe.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return string.Join(", ", probe.Headers.GetValues("User-Agent"));
    }

    /// <summary>Pi 1.5.0 pi-user-agent.ts getPiUserAgent is pi-ai's detailed formatUserAgent: the pi.dev catalog, the GitHub release
    /// checks of the tools manager and the bug report all send it, with the Pi version PiSharp tracks.</summary>
    private static async Task UserAgentCallSites()
    {
        var userAgent = ClientIdentities.FormatUserAgent(null);
        Check(userAgent.StartsWith("pi/" + ClientIdentities.PiVersion + " (", StringComparison.Ordinal), userAgent);
        var catalog = new UserAgentRecorder();
        using (await RemoteCatalogOverlay.RequestCatalogAsync("openrouter", "https://catalog.test", () => new HttpClient(catalog), null, CancellationToken.None)) { }
        Check(catalog.UserAgents.Count > 0 && catalog.UserAgents.All(agent => agent == userAgent), "catalog: " + string.Join(", ", catalog.UserAgents));
        Equal(userAgent, PiToolsManager.ToolsUserAgent, "tools manager");
        Equal(userAgent, BugReport.PiUserAgent(ClientIdentities.PiVersion), "bug report");
    }
}
#pragma warning restore PISHARP_IDENTITY001
