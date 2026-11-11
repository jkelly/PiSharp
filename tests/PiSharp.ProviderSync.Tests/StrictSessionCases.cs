// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/api/strict-tool-limits.ts, packages/ai/src/api/anthropic-messages.ts, packages/ai/src/api/bedrock-converse-stream.ts.
// Authored native regression; not an upstream execution capture.
// Authored from Pi v1.5.2 strict-tool-limits.ts and the Anthropic/Bedrock callers. No live provider requests.
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.Bedrock;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

internal static partial class Program
{
    private static Task StrictSessionCachePolicy()
    {
        SessionResources.Cleanup();
        try
        {
            StrictToolLimits.MarkExceeded(null);
            Check(!StrictToolLimits.HasExceeded(null), "An absent session must never be marked.");
            // Re-marking does not move the insertion; a full cache evicts the oldest inserted session.
            for (var index = 0; index < StrictToolLimits.MaximumSessions; index++) StrictToolLimits.MarkExceeded("bounded-" + index);
            StrictToolLimits.MarkExceeded("bounded-0");
            StrictToolLimits.MarkExceeded("bounded-new");
            Check(!StrictToolLimits.HasExceeded("bounded-0"), "Oldest session was not evicted.");
            Check(StrictToolLimits.HasExceeded("bounded-1") && StrictToolLimits.HasExceeded("bounded-new"), "Capacity evicted the wrong session.");
            SessionResources.Cleanup("bounded-1");
            Check(!StrictToolLimits.HasExceeded("bounded-1") && StrictToolLimits.HasExceeded("bounded-new"), "Per-session cleanup crossed session boundaries.");
            // Empty is still a defined session ID; cleanup of it must not clear another session.
            StrictToolLimits.MarkExceeded(""); SessionResources.Cleanup("");
            Check(!StrictToolLimits.HasExceeded("") && StrictToolLimits.HasExceeded("bounded-new"), "Empty-session cleanup cleared all sessions.");
            SessionResources.Cleanup();
            Check(!StrictToolLimits.HasExceeded("bounded-new"), "Global cleanup did not clear strict fallback state.");
            Parallel.For(0, 20_000, index => StrictToolLimits.MarkExceeded("concurrent-" + index));
            var retained = Enumerable.Range(0, 20_000).Count(index => StrictToolLimits.HasExceeded("concurrent-" + index));
            Equal(StrictToolLimits.MaximumSessions, retained);
        }
        finally { SessionResources.Cleanup(); }
        return Task.CompletedTask;
    }

    private static async Task StrictSessionPersistence()
    {
        foreach (var authenticated in new[] { false, true })
        foreach (var mode in new[] { "direct", "prepared", "payload", "response" })
        {
            var session = "strict-session-" + authenticated + "-" + mode;
            SessionResources.Cleanup(session);
            try
            {
                using var endpoint = new StrictEndpoint(attempt => attempt == 1 ? StrictRejection() : StrictSuccess());
                using var client = new HttpClient(endpoint, disposeHandler: false);
                var options = StrictOptions();
                var key = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), Haiku, options);
                var binding = new AnthropicInjectedAuthenticationBinding(new ResolvedAuthentication(AuthenticationKind.ApiKey,
                    AuthenticationOrigin.EnvironmentApiKey, Key));
                var auth = new AnthropicMessagesAuthenticatedRequestFactory(new("https://anthropic.invalid"), Haiku, options, binding);
                AnthropicMessagesHooks? hooks = mode switch
                {
                    "payload" => new() { OnPayload = (_, _, _) => Task.FromResult<JsonData?>(null) },
                    "response" => new(OnResponse: (_, _, _) => Task.CompletedTask),
                    _ => null
                };
                IChatTransport transport = mode == "direct"
                    ? new AnthropicMessagesHttpSseTransport(client, (request, token) => authenticated ? auth.Create(request, token) : key.Create(request, Key, token))
                    : AnthropicMessagesHttpSseTransport.FromPrepared(client,
                        (request, token) => authenticated ? auth.Prepare(request, token) : key.Prepare(request, Key, token), hooks: hooks);
                var transcript = StrictTranscript(StrictTool("preferred", "prefer"), StrictTool("required", "require"));
                async Task<AssistantMessage> Run(string selected)
                {
                    StreamTerminalEvent? result = null;
                    await foreach (var item in transport.StreamAsync(new(Haiku, transcript, 1) { SessionId = selected }))
                        if (item is StreamTerminalEvent terminal) result = terminal;
                    return result!.Message;
                }
                Equal(StopReason.Stop, (await Run(session)).StopReason);
                Equal(2, endpoint.Payloads.Count);
                var next = await Run(session);
                Equal(StopReason.Stop, next.StopReason);
                Equal(3, endpoint.Payloads.Count);
                Equal(null, Strictness(StrictTools(endpoint.Payloads[2])[0]));
                Equal("true", Strictness(StrictTools(endpoint.Payloads[2])[1]));
                Equal(null, Diagnostics(next));
                // A separate session is unaffected. Clearing the original restores the strict projection even on cached factories.
                await Run(session + "-other");
                Equal("true", Strictness(StrictTools(endpoint.Payloads[3])[0]));
                SessionResources.Cleanup(session);
                await Run(session);
                Equal("true", Strictness(StrictTools(endpoint.Payloads[4])[0]));
            }
            finally { SessionResources.Cleanup(session); SessionResources.Cleanup(session + "-other"); }
        }
    }

    private static async Task StrictSessionFailedRetryPersists()
    {
        const string session = "strict-failed-retry";
        SessionResources.Cleanup(session);
        try
        {
            using var endpoint = new StrictEndpoint(attempt => attempt <= 2 ? StrictRejection() : StrictSuccess());
            var transport = StrictKeyRoute(endpoint, StrictOptions());
            var request = new ChatRequest(Haiku, StrictTranscript(StrictTool("preferred", "prefer"), StrictTool("required", "require")), 1)
                { SessionId = session, DisableContinuation = true };
            async Task<StreamTerminalEvent> Run()
            {
                StreamTerminalEvent? result = null;
                await foreach (var item in transport.StreamAsync(request)) if (item is StreamTerminalEvent terminal) result = terminal;
                return result!;
            }
            Check(await Run() is StreamError, "Second strict-limit rejection should fail the first request.");
            Equal(2, endpoint.Payloads.Count);
            Check(await Run() is StreamDone, "Later request should retain the downgrade even after a failed fallback.");
            Equal(3, endpoint.Payloads.Count);
            Equal(null, Strictness(StrictTools(endpoint.Payloads[2])[0]));
            Equal("true", Strictness(StrictTools(endpoint.Payloads[2])[1]));
        }
        finally { SessionResources.Cleanup(session); }
    }

    private static async Task StrictSessionSharedWithBedrock()
    {
        const string session = "strict-shared-bedrock";
        SessionResources.Cleanup(session);
        try
        {
            var transcript = StrictTranscript(StrictTool("preferred", "prefer"), StrictTool("required", "require"));
            using var anthropicEndpoint = new StrictEndpoint(attempt => attempt == 1 ? StrictRejection() : StrictSuccess());
            var anthropic = StrictKeyRoute(anthropicEndpoint, StrictOptions());
            await foreach (var _ in anthropic.StreamAsync(new(Haiku, transcript, 1) { SessionId = session })) { }

            using var endpoint = new StrictEndpoint(_ => new(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { message = GrammarError })),
                Headers = { { "x-amzn-ErrorType", "ValidationException" } }
            });
            using var client = new HttpClient(endpoint, disposeHandler: false);
            using var invoker = new HttpMessageInvoker(endpoint, disposeHandler: false);
            var model = new ModelDescriptor("us.anthropic.claude-opus-5-5", "bedrock-converse-stream", "amazon-bedrock");
            var metadata = JsonData.Parse("""{"id":"us.anthropic.claude-opus-5-5","api":"bedrock-converse-stream","provider":"amazon-bedrock","baseUrl":"https://bedrock-runtime.us-east-1.amazonaws.com","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":200000,"maxTokens":100,"compat":{"supportsStrictMode":true}}""");
            var environment = new AwsEnvironment(_ => null, ".", invoker);
            var bedrock = new BedrockConverseStreamTransport(client, model, metadata,
                new() { BearerToken = "inert-token", CacheRetention = "none", MaxAttempts = 1 }, environment);
            var request = new ChatRequest(model, transcript, 1) { SessionId = session };
            using (var projected = await bedrock.CreateRequestAsync(request))
            {
                var tools = JsonNode.Parse(await projected.Content!.ReadAsStringAsync())!["toolConfig"]!["tools"]!.AsArray();
                Equal(null, tools[0]!["toolSpec"]!["strict"]);
                Equal("true", tools[1]!["toolSpec"]!["strict"]!.ToJsonString());
            }
            SessionResources.Cleanup(session);
            // Bedrock's rejected first request marks the shared cache even if its fallback is also rejected.
            await foreach (var _ in bedrock.StreamAsync(request)) { }
            Equal(2, endpoint.Payloads.Count);
            using (var projected = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), Haiku,
                StrictOptions()).Create(new(Haiku, transcript, 1) { SessionId = session }, Key))
            {
                var tools = StrictTools(JsonNode.Parse(await projected.Content!.ReadAsStringAsync())!.AsObject());
                Equal(null, Strictness(tools[0])); Equal("true", Strictness(tools[1]));
            }
            await foreach (var _ in bedrock.StreamAsync(request)) { }
            Equal(3, endpoint.Payloads.Count);
            Equal(null, endpoint.Payloads[2]["toolConfig"]!["tools"]![0]!["toolSpec"]!["strict"]);
            Equal("true", endpoint.Payloads[2]["toolConfig"]!["tools"]![1]!["toolSpec"]!["strict"]!.ToJsonString());
        }
        finally { SessionResources.Cleanup(session); }
    }

    private static async Task StrictSessionRefusalsDoNotPersist()
    {
        const string session = "strict-session-refusals";
        SessionResources.Cleanup(session);
        try
        {
            foreach (var mode in new[] { "required", "wrong-status", "wrong-type", "unrelated", "hook", "cancel" })
            {
                using var cancellation = new CancellationTokenSource();
                using var endpoint = new StrictEndpoint(_ =>
                {
                    if (mode == "cancel") cancellation.Cancel();
                    return StrictRejection(mode == "unrelated" ? "Invalid unrelated schema" : GrammarError,
                        status: mode == "wrong-status" ? 500 : 400, type: mode == "wrong-type" ? "authentication_error" : "invalid_request_error");
                });
                var hooks = mode == "hook" ? new AnthropicMessagesHooks
                    { OnPayload = (_, _, _) => throw new InvalidOperationException("400 " + GrammarError) } : null;
                var transport = StrictKeyRoute(endpoint, StrictOptions(), hooks);
                try
                {
                    await foreach (var _ in transport.StreamAsync(new(Haiku, StrictTranscript(StrictTool("test", mode == "required" ? "require" : "prefer")), 1)
                        { SessionId = session }, cancellation.Token)) { }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                Check(!StrictToolLimits.HasExceeded(session), mode + " should not disable preferred strict tools.");
            }
        }
        finally { SessionResources.Cleanup(session); }
    }
}
