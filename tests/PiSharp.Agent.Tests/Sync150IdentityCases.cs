// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/agent/src/agent-loop.ts (DEFAULT_AGENT_IDENTITY, streamAssistantResponse)
// and packages/agent/src/agent.ts (AgentOptions.identity, agent.identity).
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

// Authored expectations derived by reading the cited Pi 1.5.0 source; nothing here runs upstream code or is an upstream capture.
// agent-loop.ts passes mergeClientIdentity(DEFAULT_AGENT_IDENTITY, config.identity) to the stream function on every request, and
// agent.identity changes it for later requests. Upstream's DEFAULT_AGENT_IDENTITY attributes requests to Pi; PiSharp's is
// { attribution: null } (owner decision 2026-11-05, "Use safe defaults", core.agent-identity), so these cases also pin that the agent
// adds no attribution of its own.
#pragma warning disable PISHARP_IDENTITY001
internal static class Sync150IdentityCases
{
    private static readonly ModelDescriptor Model = new("identity-model", "openai-responses", "fixture-provider");

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync150.agent-identity-reaches-every-request-without-attribution", IdentityReachesEveryRequest),
        ("sync150.agent-identity-merges-under-the-request-identity", RequestIdentityWinsPerField),
        ("sync150.agent-default-identity-carries-no-attribution", DefaultCarriesNoAttribution),
    ];

    private static async Task IdentityReachesEveryRequest()
    {
        Equal(new ClientIdentity { AttributionOff = true }, AgentConfiguration.DefaultIdentity);
        Check(AgentConfiguration.DefaultIdentity.Attribution is null && AgentConfiguration.DefaultIdentity.AttributionOff, "The default identity must turn attribution off.");

        // Without an identity the agent leaves requests as they are: the detailed User-Agent of the tracked Pi version and no attribution.
        var transport = new Transport();
        await using (var plain = new NativeAgent(new(Model, transport, []), () => 123, new Sink()))
            await plain.PromptAsync(Input("hi"));
        Equal(null, transport.Requests.Single().Identity);
        Check(ClientIdentities.FormatUserAgent(transport.Requests.Single().Identity).StartsWith("pi/" + ClientIdentities.PiVersion + " (", StringComparison.Ordinal),
            "default user agent");

        // An agent identity reaches every request; agent.identity (Configure) changes it for the next one.
        transport = new Transport();
        var configuration = new AgentConfiguration(Model, transport, []) { Identity = new() { UserAgent = ClientUserAgent.Custom("ext/1.0") } };
        await using var agent = new NativeAgent(configuration, () => 123, new Sink());
        await agent.PromptAsync(Input("hi"));
        await agent.PromptAsync(Input("again"));
        agent.Configure(configuration with { Identity = new() { Attribution = new("ext", "https://ext.test"), Version = "2" } });
        await agent.PromptAsync(Input("later"));
        Equal(3, transport.Requests.Count);
        Equal(new ClientIdentity { UserAgent = ClientUserAgent.Custom("ext/1.0"), AttributionOff = true }, transport.Requests[0].Identity);
        Equal(transport.Requests[0].Identity, transport.Requests[1].Identity);
        Equal(new ClientIdentity { Attribution = new("ext", "https://ext.test"), Version = "2" }, transport.Requests[2].Identity);
        Equal("ext/1.0", ClientIdentities.FormatUserAgent(transport.Requests[0].Identity));
        Check(ClientIdentities.FormatUserAgent(transport.Requests[2].Identity).StartsWith("pi/2 (", StringComparison.Ordinal), "versioned user agent");
        Equal(0, ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", transport.Requests[0].Identity).Count);
        Equal("ext", ClientIdentities.GetAttributionHeaders("openrouter", "https://openrouter.ai/api/v1", transport.Requests[2].Identity)
            .Single(header => header.Key == "X-OpenRouter-Title").Value);
    }

    private static async Task RequestIdentityWinsPerField()
    {
        // A request that already carries an identity keeps its own fields; the agent identity fills the ones it leaves unset.
        var transport = new Transport();
        var runner = new TurnRunner(new ChatClient(transport, capacity: 1), new ToolBatchScheduler([]))
        { Identity = new() { UserAgent = ClientUserAgent.Short, Attribution = new("agent", "https://agent.test"), Version = "3" } };
        var request = new ChatRequest(Model, [Input("hi")], 123) { Identity = new() { AttributionOff = true, Version = "4" } };
        await runner.RunAsync(request, new Sink());
        Equal(new ClientIdentity { UserAgent = ClientUserAgent.Short, AttributionOff = true, Version = "4" }, transport.Requests.Single().Identity);
        Equal("pi", ClientIdentities.FormatUserAgent(transport.Requests.Single().Identity));
        Equal(0, ClientIdentities.GetAttributionHeaders("openrouter", "", transport.Requests.Single().Identity).Count);
    }

    // The default is { attribution: null }: whatever identity sits below an agent request (a transport's configured identity naming pi,
    // as upstream's withClientIdentity sets one under every request), a request whose agent identity sets no attribution carries none,
    // on every attribution target (OpenRouter, NVIDIA NIM, OpenCode, by provider or by base URL).
    private static async Task DefaultCarriesNoAttribution()
    {
        var pi = new ClientIdentity { Attribution = new("pi", "https://pi.dev", ["cli-agent"]) };
        var transport = new Transport();
        var configuration = new AgentConfiguration(Model, transport, []) { Identity = new() { UserAgent = ClientUserAgent.Detailed, Version = ClientIdentities.PiVersion } };
        await using (var agent = new NativeAgent(configuration, () => 123, new Sink())) await agent.PromptAsync(Input("hi"));
        var sent = transport.Requests.Single().Identity;
        Check(sent is { Attribution: null, AttributionOff: true }, "request identity: " + sent);
        foreach (var (provider, baseUrl) in new[] { ("openrouter", "https://openrouter.ai/api/v1"), ("nvidia", "https://integrate.api.nvidia.com/v1"),
            ("opencode", "https://opencode.ai/zen/v1"), ("opencode-go", "https://opencode.ai/zen/go/v1"), ("custom", "https://openrouter.ai/api/v1"),
            ("custom", "https://integrate.api.nvidia.com/v1"), ("custom", "https://opencode.ai/zen/v1") })
        {
            Equal(0, ClientIdentities.GetAttributionHeaders(provider, baseUrl, AgentConfiguration.DefaultIdentity).Count);
            var headers = ClientIdentities.GetIdentityHeaders(provider, baseUrl, ClientIdentities.Merge(pi, sent));
            Check(headers.Count == 1 && headers[0].Key == "User-Agent" && headers[0].Value == ClientIdentities.FormatUserAgent(null),
                provider + " " + baseUrl + ": " + string.Join(", ", headers));
        }
    }

    private sealed class Transport : IChatTransport
    {
        public readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 0, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new(""));
            yield return new TextEnded(0, "done");
            yield return new StreamDone(StopReason.Stop, final);
            await Task.CompletedTask;
        }
    }

    private sealed class Sink : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken) => ValueTask.CompletedTask; }

    private static TranscriptEntry Input(string text) =>
        new("user", JsonData.FromElement(JsonSerializer.SerializeToElement(new { role = "user", content = text, timestamp = 123 })));
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#pragma warning restore PISHARP_IDENTITY001
