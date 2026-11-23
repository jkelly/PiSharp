using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

internal static partial class Program
{
    private static readonly (string Id, Func<Task> Run)[] TransportRetryClassificationCases =
    [
        ("wave4.transport-retry-signal-all-four-native-transports", Wave4TransportRetrySignals),
        ("wave4.transport-retry-session-recovery-and-exhaustion", Wave4TransportRetrySessionRecoveryAndExhaustion),
    ];

    private static async Task Wave4TransportRetrySignals()
    {
        var scenarios = CreateFourTransportScenarios();
        foreach (var scenario in scenarios)
        {
            // 1. Pre-header socket drop -> retryable, sanitized message unchanged, no wire leakage.
            using (var scope = scenario.Build(_ => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("simulated socket drop", new SocketException((int)SocketError.ConnectionReset)))))
            {
                var result = await new ChatClient(scope.Transport).CompleteAsync(new(scenario.Model, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(result.IsRetryableTransportFailure, $"{scenario.Name}: expected pre-header SocketException to set IsRetryableTransportFailure.");
                Equal(scenario.SanitizedError, ReadErrorMessage(result.Message));
                var wire = PiWireJson.WriteMessage(result.Message).ToString();
                Check(!wire.Contains("IsRetryableTransportFailure", StringComparison.OrdinalIgnoreCase), $"{scenario.Name}: internal retry flag leaked into wire JSON.");
                Check(!wire.Contains("simulated socket drop", StringComparison.Ordinal), $"{scenario.Name}: raw exception text leaked into wire JSON.");
            }

            // 2. Mid-stream network IOException wrapping SocketException -> retryable, sanitized message unchanged.
            using (var scope = scenario.Build(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream(
                    new IOException("Unable to read data from the transport connection: reset.", new SocketException((int)SocketError.ConnectionReset))))
            })))
            {
                var result = await new ChatClient(scope.Transport).CompleteAsync(new(scenario.Model, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(result.IsRetryableTransportFailure, $"{scenario.Name}: expected mid-stream SocketException IOException to set IsRetryableTransportFailure.");
                Equal(scenario.SanitizedError, ReadErrorMessage(result.Message));
            }

            // 3. Non-transient TLS / SecureConnectionError -> NOT retryable.
            using (var scope = scenario.Build(_ => Task.FromException<HttpResponseMessage>(
                new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.", new AuthenticationException("cert")))))
            {
                var result = await new ChatClient(scope.Transport).CompleteAsync(new(scenario.Model, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"{scenario.Name}: TLS SecureConnectionError must NOT be marked retryable.");
            }

            // 4. Non-transient InvalidOperationException -> NOT retryable.
            using (var scope = scenario.Build(_ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("internal bug"))))
            {
                var result = await new ChatClient(scope.Transport).CompleteAsync(new(scenario.Model, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"{scenario.Name}: InvalidOperationException must NOT be marked retryable.");
            }

            // 4b. Non-transient HttpIOException protocol/resource-limit errors -> NOT retryable.
            foreach (var nonTransientKind in new[] { HttpRequestError.InvalidResponse, HttpRequestError.ConfigurationLimitExceeded })
            {
                using var scope = scenario.Build(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ThrowingReadStream(
                        new HttpIOException(nonTransientKind, "protocol or configuration limit")))
                }));
                var result = await new ChatClient(scope.Transport).CompleteAsync(new(scenario.Model, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"{scenario.Name}: HttpIOException({nonTransientKind}) must NOT be marked retryable.");
            }

            // 5. Caller cancellation -> Aborted, NOT retryable.
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                using var scope = scenario.Build(_ => Task.FromException<HttpResponseMessage>(
                    new SocketException((int)SocketError.ConnectionReset)));
                var result = await new ChatClient(scope.Transport).CompleteAsync(new(scenario.Model, [Ask], 1), cts.Token).WaitAsync(Deadline);
                Equal(StopReason.Aborted, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"{scenario.Name}: caller cancellation must NOT be marked retryable.");
            }
        }

        // 6. Mistral permanent 401 whose error-body read drops -> NOT retryable (status stays authoritative).
        {
            var mistralModel = new ModelDescriptor("mistral-large-latest", "mistral-conversations", "mistral");
            using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StreamContent(new ThrowingReadStream(
                    new IOException("Connection reset while reading 401 body", new SocketException((int)SocketError.ConnectionReset))))
            })));
            var transport = new MistralTextHttpSseTransport(client, mistralModel, new MistralTextOptions(new("https://api.mistral.ai/"), true, new(0, 0, 0, 0), "offline-fixture") { ApiKey = Key });
            var result = await new ChatClient(transport).CompleteAsync(new(mistralModel, [Ask], 1)).WaitAsync(Deadline);
            Equal(StopReason.Error, result.Message.StopReason);
            Check(!result.IsRetryableTransportFailure, "Mistral 401 with mid-error-body IOException must NOT be marked retryable.");
        }

        // 7. Completions observer callbacks (OnSourcePublished, OnResponse, OnProviderStreamEvent) throwing TimeoutException -> NOT retryable.
        {
            var completionsModel = new ModelDescriptor("gpt-4o", "openai-completions", "openai");
            static HttpResponseMessage CompletionsOk() => Sse(null,
                """data: {"id":"cmpl-1","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""" + "\n\n" +
                "data: [DONE]\n\n");
            foreach (var (label, hooks) in new (string, CompletionsLifecycleHooks)[]
            {
                ("OnSourcePublished", new() { OnSourcePublished = _ => throw new TimeoutException("observer timeout") }),
                ("OnResponse", new() { OnResponse = (_, _, _) => ValueTask.FromException(new TimeoutException("response hook timeout")) }),
                ("OnResponse-OCE", new() { OnResponse = (_, _, _) => ValueTask.FromException(new OperationCanceledException("hook timeout")) }),
                ("OnProviderStreamEvent", new() { OnProviderStreamEvent = (_, _, _) => ValueTask.FromException(new TimeoutException("stream hook timeout")) }),
            })
            {
                using var client = new HttpClient(new Handler(_ => Task.FromResult(CompletionsOk())));
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions"),
                    options: new CompletionsHttpSseOptions { Hooks = hooks });
                var result = await new ChatClient(transport).CompleteAsync(new(completionsModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"Completions {label} must NOT be marked retryable.");
            }
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))))
            {
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions") { Content = new StringContent("{}") },
                    options: new CompletionsHttpSseOptions
                    {
                        Retry = new CompletionsRetryOptions(MaxRetries: 1, MaxRetryDelayMilliseconds: 100)
                        {
                            OnRetry = _ => throw new TimeoutException("retry observer timeout")
                        }
                    });
                var result = await new ChatClient(transport).CompleteAsync(new(completionsModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, "Completions Retry.OnRetry TimeoutException must NOT be marked retryable.");
            }
            // Completions OnBodyRead and OnInputPublished observer failures -> NOT retryable.
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(CompletionsOk()))))
            {
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions"),
                    options: new CompletionsHttpSseOptions { OnBodyRead = _ => throw new TimeoutException("body observer timeout") });
                var result = await new ChatClient(transport).CompleteAsync(new(completionsModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, "Completions OnBodyRead TimeoutException must NOT be marked retryable.");
            }
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(CompletionsOk()))))
            {
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions"),
                    options: new CompletionsHttpSseOptions { OnInputPublished = _ => throw new TimeoutException("input observer timeout") });
                await using var run = await transport.StartAsync(new(completionsModel, [Ask], 1)).AsTask().WaitAsync(Deadline);
                StreamTerminalEvent? terminal = null;
                await foreach (var frame in run.ReadCanonicalEventsAsync(CancellationToken.None))
                    if (frame is StreamTerminalEvent done) terminal = done;
                Check(terminal is StreamError { IsRetryableTransportFailure: false },
                    "Completions OnInputPublished TimeoutException must NOT be marked retryable.");
            }
            // Completions BodyReaderFactory / BodyResultReaderFactory failures -> NOT retryable.
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(CompletionsOk()))))
            {
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions"),
                    options: new CompletionsHttpSseOptions { BodyReaderFactory = (_, _) => ValueTask.FromException<ICompletionsResponseBodyReader>(new TimeoutException("factory timeout")) });
                var result = await new ChatClient(transport).CompleteAsync(new(completionsModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, "Completions BodyReaderFactory TimeoutException must NOT be marked retryable.");
            }
            // Completions premature EOF without [DONE] or finish_reason -> retryable UnexpectedEof; explicit [DONE] without finish_reason -> NOT retryable.
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(Sse(null,
                """data: {"id":"cmpl-1","choices":[{"index":0,"delta":{"role":"assistant","content":"partial"},"finish_reason":null}]}""" + "\n\n")))))
            {
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions"));
                var result = await new ChatClient(transport).CompleteAsync(new(completionsModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(result.IsRetryableTransportFailure, "Completions premature EOF without [DONE] or finish_reason must be marked retryable.");
            }
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(Sse(null,
                """data: {"id":"cmpl-1","choices":[{"index":0,"delta":{"role":"assistant","content":"partial"},"finish_reason":null}]}""" + "\n\n" +
                "data: [DONE]\n\n")))))
            {
                var transport = new CompletionsHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions"));
                var result = await new ChatClient(transport).CompleteAsync(new(completionsModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, "Completions clean [DONE] without finish_reason must NOT be marked retryable.");
            }
        }

        // 8. Anthropic hooks (OnPayload, OnResponse, OnProviderStreamEvent) and permanent 401 error-body IO failures -> NOT retryable.
        {
            var anthropicModel = new ModelDescriptor("claude-3-5-haiku-latest", "anthropic-messages", "anthropic");
            foreach (var (withHooks, label) in new[] { (false, "plain"), (true, "hooks") })
            {
                using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StreamContent(new ThrowingReadStream(
                        new IOException("Connection reset while reading 401 body", new SocketException((int)SocketError.ConnectionReset))))
                })));
                var transport = new AnthropicMessagesHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://anthropic.invalid/v1/messages"),
                    hooks: withHooks ? new AnthropicMessagesHooks { OnResponse = (_, _, _) => Task.CompletedTask } : null);
                var result = await new ChatClient(transport).CompleteAsync(new(anthropicModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"Anthropic ({label}) 401 with mid-error-body IOException must NOT be marked retryable.");
            }
            foreach (var (label, hooks) in new (string, AnthropicMessagesHooks)[]
            {
                ("OnResponse-Timeout", new() { OnResponse = (_, _, _) => Task.FromException(new TimeoutException("response hook timeout")) }),
                ("OnResponse-OCE", new() { OnResponse = (_, _, _) => Task.FromException(new OperationCanceledException("hook canceled")) }),
                ("OnProviderStreamEvent-Timeout", new() { OnProviderStreamEvent = (_, _, _) => Task.FromException(new TimeoutException("event hook timeout")) }),
                ("OnProviderStreamEvent-OCE", new() { OnProviderStreamEvent = (_, _, _) => Task.FromException(new OperationCanceledException("event hook canceled")) }),
            })
            {
                using var client = new HttpClient(new Handler(_ => Task.FromResult(AnthropicSuccessSse())));
                var transport = new AnthropicMessagesHttpSseTransport(client,
                    (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://anthropic.invalid/v1/messages"),
                    hooks: hooks);
                var result = await new ChatClient(transport).CompleteAsync(new(anthropicModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"Anthropic {label} must NOT be marked retryable.");
            }
            using (var client = new HttpClient(new Handler(_ => Task.FromResult(AnthropicSuccessSse()))))
            {
                var factory = new AnthropicMessagesKeyAuthRequestFactory(new("https://anthropic.invalid"), anthropicModel, new(100));
                var transport = AnthropicMessagesHttpSseTransport.FromPrepared(client,
                    (req, tok) => factory.Prepare(req, Key, tok),
                    hooks: new AnthropicMessagesHooks { OnPayload = (_, _, _) => Task.FromException<JsonData?>(new TimeoutException("payload hook timeout")) });
                var result = await new ChatClient(transport).CompleteAsync(new(anthropicModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, "Anthropic OnPayload TimeoutException must NOT be marked retryable.");
            }
        }

        // 9. Mistral hooks (OnPayload, OnResponse, OnProviderStreamEvent) throwing unrequested OCE -> NOT retryable.
        {
            var mistralModel = new ModelDescriptor("mistral-large-latest", "mistral-conversations", "mistral");
            static HttpResponseMessage MistralOk() => Sse(null,
                """data: {"id":"m1","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""" + "\n\n" +
                "data: [DONE]\n\n");
            var baseOpts = new MistralTextOptions(new("https://api.mistral.ai/"), true, new(0, 0, 0, 0), "offline-fixture") { ApiKey = Key };
            foreach (var (label, opts) in new (string, MistralTextOptions)[]
            {
                ("OnPayload-OCE", baseOpts with { OnPayload = (_, _, _) => ValueTask.FromException<JsonData?>(new OperationCanceledException("hook timeout")) }),
                ("OnResponse-OCE", baseOpts with { OnResponse = (_, _, _) => ValueTask.FromException(new OperationCanceledException("hook timeout")) }),
                ("OnProviderStreamEvent-OCE", baseOpts with { OnProviderStreamEvent = (_, _, _) => ValueTask.FromException(new OperationCanceledException("hook timeout")) }),
            })
            {
                using var client = new HttpClient(new Handler(_ => Task.FromResult(MistralOk())));
                var transport = new MistralTextHttpSseTransport(client, mistralModel, opts);
                var result = await new ChatClient(transport).CompleteAsync(new(mistralModel, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"Mistral {label} must NOT be marked retryable.");
            }
        }

        // 10. Google hooks (OnPayload, OnProviderStreamEvent) throwing TimeoutException or unrequested OCE -> NOT retryable.
        {
            static HttpResponseMessage GoogleOk() => Sse(null,
                """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]},"finishReason":"STOP"}]}""" + "\n\n");
            foreach (var (label, hooks) in new (string, GoogleGenerativeAIHooks)[]
            {
                ("OnPayload-Timeout", new() { OnPayload = (_, _, _) => ValueTask.FromException<JsonData?>(new TimeoutException("payload timeout")) }),
                ("OnProviderStreamEvent-OCE", new() { OnProviderStreamEvent = (_, _, _) => ValueTask.FromException(new OperationCanceledException("event timeout")) }),
            })
            {
                using var client = new HttpClient(new Handler(_ => Task.FromResult(GoogleOk())));
                var transport = new GoogleGenerativeAIHttpTransport(client, Sync160Google,
                    new GoogleGenerativeAIOptions(Sync160GoogleRow("google-generative-ai"), Key) { Hooks = hooks });
                var result = await new ChatClient(transport).CompleteAsync(new(Sync160Google, [Ask], 1)).WaitAsync(Deadline);
                Equal(StopReason.Error, result.Message.StopReason);
                Check(!result.IsRetryableTransportFailure, $"Google {label} must NOT be marked retryable.");
            }
        }
    }

    private static async Task Wave4TransportRetrySessionRecoveryAndExhaustion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pisharp-wave4-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var scenario in CreateFourTransportScenarios())
            {
                // Recovery: first attempt drops with ConnectionReset, second attempt succeeds.
                {
                    var attempts = 0;
                    using var scope = scenario.Build(_ =>
                    {
                        var call = Interlocked.Increment(ref attempts);
                        if (call == 1)
                            return Task.FromException<HttpResponseMessage>(new SocketException((int)SocketError.ConnectionReset));
                        return Task.FromResult(scenario.SuccessResponse());
                    });

                    var sessionPath = Path.Combine(tempDir, $"{scenario.Name}-recover.jsonl");
                    var header = new SessionEntryCodec().Parse(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "session", version = 3, id = "sess-recover", timestamp = "2026-11-23T00:00:00.000Z", cwd = tempDir
                    }));
                    var entrySeq = 0;
                    await using var session = await PersistentAgentSession.CreateAsync(
                        sessionPath, header, new AgentConfiguration(scenario.Model, scope.Transport, []),
                        () => 1000L, () => $"e{Interlocked.Increment(ref entrySeq)}").WaitAsync(Deadline);
                    var retryEvents = new List<SessionOperationEvent>();
                    using var _ = session.SubscribeOperationEvents(new OperationEventCollector(retryEvents));
                    session.ConfigureAutomaticRetry(new AgentRetryPolicy(true, 2, 1, 10),
                        originalDelay: (_, _) => Task.CompletedTask);

                    await session.PromptAsync([Ask]).WaitAsync(Deadline);
                    Equal(2, attempts);
                    Check(retryEvents.OfType<SessionAutoRetryStarted>().Count() == 1, $"{scenario.Name}: expected 1 retry start.");
                    Check(retryEvents.OfType<SessionAutoRetryEnded>().Single() is { Success: true }, $"{scenario.Name}: expected retry to succeed.");
                    var lastAssistant = session.Snapshot.Agent.Messages.Last(m => m.Role == "assistant");
                    var text = lastAssistant.WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString();
                    Equal("recovered!", text);
                }

                // Exhaustion: every attempt drops with ConnectionReset -> emits exact sanitized error text.
                {
                    var attempts = 0;
                    using var scope = scenario.Build(_ =>
                    {
                        Interlocked.Increment(ref attempts);
                        return Task.FromException<HttpResponseMessage>(
                            new HttpRequestException("secret-upstream-host:443 reset", new SocketException((int)SocketError.ConnectionReset)));
                    });

                    var sessionPath = Path.Combine(tempDir, $"{scenario.Name}-exhaust.jsonl");
                    var header = new SessionEntryCodec().Parse(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "session", version = 3, id = "sess-exhaust", timestamp = "2026-11-23T00:00:00.000Z", cwd = tempDir
                    }));
                    var entrySeq = 0;
                    await using var session = await PersistentAgentSession.CreateAsync(
                        sessionPath, header, new AgentConfiguration(scenario.Model, scope.Transport, []),
                        () => 1000L, () => $"e{Interlocked.Increment(ref entrySeq)}").WaitAsync(Deadline);
                    var retryEvents = new List<SessionOperationEvent>();
                    using var _ = session.SubscribeOperationEvents(new OperationEventCollector(retryEvents));
                    session.ConfigureAutomaticRetry(new AgentRetryPolicy(true, 1, 1, 10),
                        originalDelay: (_, _) => Task.CompletedTask);

                    await session.PromptAsync([Ask]).WaitAsync(Deadline);
                    Equal(2, attempts);
                    Check(retryEvents.OfType<SessionAutoRetryStarted>().Count() == 1, $"{scenario.Name}: expected 1 retry start before exhaustion.");
                    var ended = retryEvents.OfType<SessionAutoRetryEnded>().Single();
                    Check(!ended.Success, $"{scenario.Name}: expected retry exhaustion.");
                    Equal(scenario.SanitizedError, ended.FinalError);
                    var lastAssistant = session.Snapshot.Agent.Messages.Last(m => m.Role == "assistant");
                    Equal(scenario.SanitizedError, lastAssistant.WireBody.Value.GetProperty("errorMessage").GetString());
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class TransportScope(HttpClient client, IChatTransport transport) : IDisposable
    {
        public IChatTransport Transport => transport;
        public void Dispose() => client.Dispose();
    }

    private sealed record TransportRetryScenario(
        string Name,
        ModelDescriptor Model,
        string SanitizedError,
        Func<Func<HttpRequestMessage, Task<HttpResponseMessage>>, TransportScope> Build,
        Func<HttpResponseMessage> SuccessResponse);

    private static TransportRetryScenario[] CreateFourTransportScenarios()
    {
        var completionsModel = new ModelDescriptor("gpt-4o", "openai-completions", "openai");
        var anthropicModel = new ModelDescriptor("claude-3-5-haiku-latest", "anthropic-messages", "anthropic");
        var googleModel = Sync160Google;
        var vertexModel = Sync160Vertex;
        var mistralModel = new ModelDescriptor("mistral-large-latest", "mistral-conversations", "mistral");

        return
        [
            new(
                "openai-completions",
                completionsModel,
                "Completions stream did not complete.",
                fn =>
                {
                    var client = new HttpClient(new Handler(fn));
                    return new(client, new CompletionsHttpSseTransport(client, (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://openai.invalid/v1/chat/completions")));
                },
                () => Sse(null,
                    """data: {"id":"cmpl-1","choices":[{"index":0,"delta":{"role":"assistant","content":"recovered!"},"finish_reason":"stop"}]}""" + "\n\n" +
                    "data: [DONE]\n\n")),

            new(
                "anthropic-messages-plain",
                anthropicModel,
                "Anthropic stream did not complete.",
                fn =>
                {
                    var client = new HttpClient(new Handler(fn));
                    return new(client, new AnthropicMessagesHttpSseTransport(client, (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://anthropic.invalid/v1/messages")));
                },
                AnthropicSuccessSse),

            new(
                "anthropic-messages-hooks",
                anthropicModel,
                "Anthropic stream did not complete.",
                fn =>
                {
                    var client = new HttpClient(new Handler(fn));
                    return new(client, new AnthropicMessagesHttpSseTransport(client, (_, _) => new HttpRequestMessage(HttpMethod.Post, "https://anthropic.invalid/v1/messages"),
                        hooks: new AnthropicMessagesHooks { OnResponse = (_, _, _) => Task.CompletedTask }));
                },
                AnthropicSuccessSse),

            new(
                "google-generative-ai",
                googleModel,
                "Google provider operation failed.",
                fn =>
                {
                    var client = new HttpClient(new Handler(fn));
                    return new(client, new GoogleGenerativeAIHttpTransport(client, googleModel, new GoogleGenerativeAIOptions(Sync160GoogleRow("google-generative-ai"), Key)));
                },
                () => Sse(null, """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"recovered!"}]},"finishReason":"STOP"}]}""" + "\n\n")),

            new(
                "google-vertex",
                vertexModel,
                "Google provider operation failed.",
                fn =>
                {
                    var client = new HttpClient(new Handler(fn));
                    return new(client, new GoogleVertexHttpTransport(client, vertexModel, new GoogleVertexOptions(Sync160VertexEndpoint, Key,
                        new GoogleGenerativeAIOptions(Sync160GoogleRow("google-vertex"))) { ApiKeyMode = true }));
                },
                () => Sse(null, """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"recovered!"}]},"finishReason":"STOP"}]}""" + "\n\n")),

            new(
                "mistral-conversations",
                mistralModel,
                "Mistral source operation failed.",
                fn =>
                {
                    var client = new HttpClient(new Handler(fn));
                    return new(client, new MistralTextHttpSseTransport(client, mistralModel, new MistralTextOptions(new("https://api.mistral.ai/"), true, new(0, 0, 0, 0), "offline-fixture") { ApiKey = Key }));
                },
                () => Sse(null,
                    """data: {"id":"m1","choices":[{"index":0,"delta":{"role":"assistant","content":"recovered!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""" + "\n\n" +
                    "data: [DONE]\n\n")),
        ];
    }

    private static HttpResponseMessage AnthropicSuccessSse() => Sse(null,
        "event: message_start\n" +
        """data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-3-5-haiku-latest","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}""" + "\n\n" +
        "event: content_block_start\n" +
        """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""" + "\n\n" +
        "event: content_block_delta\n" +
        """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"recovered!"}}""" + "\n\n" +
        "event: content_block_stop\n" +
        """data: {"type":"content_block_stop","index":0}""" + "\n\n" +
        "event: message_delta\n" +
        """data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}""" + "\n\n" +
        "event: message_stop\n" +
        """data: {"type":"message_stop"}""" + "\n\n");

    private static string? ReadErrorMessage(AssistantMessage message) =>
        message.ExtraProperties is not null && message.ExtraProperties.TryGet("errorMessage", out var value)
            ? value!.Value.GetString() : null;

    private sealed class OperationEventCollector(List<SessionOperationEvent> items) : ISessionOperationEventSink
    {
        public ValueTask EmitAsync(SessionOperationEvent value, CancellationToken cancellationToken)
        {
            items.Add(value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(exception);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(exception);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
