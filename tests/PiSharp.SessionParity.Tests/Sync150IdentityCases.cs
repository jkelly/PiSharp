// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/client-identity.ts (getClientIdentity),
// core/sdk.ts (identity on every request; transformHeaders -> before_provider_headers runs before pi-ai adds the identity headers)
// and packages/ai/src/utils/identity.ts (getIdentityHeaders: the User-Agent and attribution headers sit below the request headers,
// and a null request header removes one). Expectations are authored, never captured from an upstream run. PiSharp's identity has
// no attribution, whatever enableInstallTelemetry says (owner decision 2026-11-05, core.client-identity-attribution).
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Cli.Extensions;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using static EventFixture;

#pragma warning disable PISHARP_IDENTITY001
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150IdentityCases() =>
    [
        Case("sync150.session-requests-carry-the-coding-agent-identity-without-attribution", SessionRequestsCarryIdentity),
        Case("sync150.provider-headers-handlers-do-not-see-identity-headers", HeadersHandlersSkipIdentityHeaders),
    ];

    private static async Task SessionRequestsCarryIdentity()
    {
        Equal(new ClientIdentity { UserAgent = ClientUserAgent.Detailed, Version = ClientIdentities.PiVersion, AttributionOff = true },
            CodingAgentClientIdentity.Get(), "getClientIdentity");
        await using var f = await CreateAsync();
        f.StartRpc();
        await f.PromptAsync();
        var identity = f.Transport.Requests.Single().Identity;
        Equal(new ClientIdentity { UserAgent = ClientUserAgent.Detailed, Version = ClientIdentities.PiVersion, AttributionOff = true }, identity, "request identity");
        // Even over an identity naming pi below it (a transport's configured identity), the request sends no attribution.
        var below = new ClientIdentity { Attribution = new("pi", "https://pi.dev", ["cli-agent"]) };
        foreach (var (provider, baseUrl) in new[] { ("openrouter", "https://openrouter.ai/api/v1"), ("nvidia", "https://integrate.api.nvidia.com/v1"), ("opencode", "https://opencode.ai/zen/v1") })
            Equal("User-Agent=" + ClientIdentities.FormatUserAgent(null),
                string.Join("|", ClientIdentities.GetIdentityHeaders(provider, baseUrl, ClientIdentities.Merge(below, identity)).Select(pair => pair.Key + "=" + pair.Value)), provider);
    }

    private static async Task HeadersHandlersSkipIdentityHeaders()
    {
        await using var f = await CreateAsync(); var seen = new List<string>();
        await f.Activate(api =>
        {
            var handlers = (IExtensionEventHandlerRegistry)api;
            handlers.RegisterEventHandler(new("headers", "before_provider_headers", (value, _, _) =>
            {
                var headers = JsonNode.Parse(value.Value.GetProperty("headers").GetRawText())!.AsObject();
                lock (seen) seen.Add(string.Join(",", headers.Select(pair => pair.Key.ToLowerInvariant()).Order(StringComparer.Ordinal)));
                // Setting an identity header overrides it, null removes it; leaving one out keeps it, since handlers never saw it.
                headers["X-OpenRouter-Title"] = "ext"; headers["User-Agent"] = null; headers["x-trace"] = "t-1";
                return ValueTask.FromResult<JsonData?>(JsonData.Parse(headers.ToJsonString()));
            }));
        });
        var provider = new FakeProvider();
        using var client = new HttpClient(new ExtensionProviderHttpHandler(provider, f.Registry, f.Registry.CaptureSnapshot(), Model, f.Report));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        { Content = new StringContent("""{"model":"m"}""", Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("authorization", "Bearer k");
        request.Headers.TryAddWithoutValidation("User-Agent", ClientIdentities.FormatUserAgent(null));
        request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://ext.test");
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", "embedder");
        request.Headers.TryAddWithoutValidation("X-BILLING-INVOKE-ORIGIN", "Embedder");
        request.Headers.TryAddWithoutValidation("x-opencode-client", "pisharp");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        _ = await response.Content.ReadAsStringAsync();
        Equal("authorization", string.Join("|", seen), "headers the handler saw");
        var sent = provider.Request!.Headers;
        Check(!sent.Contains("User-Agent"), "a null User-Agent removes it");
        Equal("ext", string.Join(",", sent.GetValues("X-OpenRouter-Title")), "a returned identity header is set");
        Equal("https://ext.test", string.Join(",", sent.GetValues("HTTP-Referer")), "an identity header the handler left out stays");
        Equal("Embedder", string.Join(",", sent.GetValues("X-BILLING-INVOKE-ORIGIN")), "NVIDIA origin stays");
        Equal("pisharp", string.Join(",", sent.GetValues("x-opencode-client")), "x-opencode-client stays");
        Equal("t-1", string.Join(",", sent.GetValues("x-trace")), "other header edits apply");
        Equal("Bearer k", sent.Authorization?.ToString(), "authorization");
    }
}
#pragma warning restore PISHARP_IDENTITY001
