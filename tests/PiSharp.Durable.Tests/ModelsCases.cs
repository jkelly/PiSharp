using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

// NativeDurableModels (decision 0005, P8): the production IDurableModels over PiSharp.AI. Authored native cases, fully offline: every
// request goes to a recording HttpMessageHandler that serves recorded SSE, every environment is an injected dictionary (never the process
// environment) and every key is a fake test value.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ModelsCases() =>
    [
        Case("models.catalog.looks-up-built-in-chat-models-by-provider-and-id", () =>
        {
            using var models = Models(new Dictionary<string, string>());
            var gpt = models.GetModel("openai", "gpt-4.1") ?? throw new InvalidOperationException("gpt-4.1 missing");
            Equal("GPT-4.1", gpt.Name, "name");
            Equal("openai-responses", gpt.Api, "api");
            Equal("https://api.openai.com/v1", gpt.BaseUrl, "base URL");
            Check(!gpt.Reasoning, "not reasoning");
            Equal(32768d, gpt.MaxTokens, "max tokens");
            Equal(1047576d, gpt.ContextWindow, "context window");
            Equal("text,image", string.Join(",", gpt.Input), "input");
            Equal("{\"supportsStrictMode\":true}", gpt.Wire!["compat"].ToJson(), "wire compat");
            Equal(new ModelDescriptor("gpt-4.1", "openai-responses", "openai"), gpt.Descriptor, "descriptor");
            // gpt-6-luna is both a classifier (openai-decisions) and a chat model; lookups see only the chat model.
            Equal("openai-responses", models.GetModel("openai", "gpt-6-luna")!.Api, "the chat model, not the classifier");
            Check(models.GetModels().All(model => ((JsonStringValue)model.Wire!["type"]).Value == "chat"), "chat models only");
            Check(models.GetModel("openai", "no-such-model") is null, "unknown model");
            Check(models.GetModel("no-such-provider", "gpt-4.1") is null, "unknown provider");
            Check(models.GetModels("openai").All(model => model.Provider == "openai" && model.Api == "openai-responses"), "openai chat models");
            Equal(42, models.Providers.Count, "every built-in provider registers");
        }),
        // pi-ai 1.2.0 Model.inputLimits (work list area e): the read tool's image limits come from images.resize.
        Case("models.catalog.input-limits-come-from-the-catalog-entry", () =>
        {
            using var models = Models(new Dictionary<string, string>());
            var fable = models.GetModel("anthropic", "claude-fable-5") ?? throw new InvalidOperationException("claude-fable-5 missing");
            Equal(new DurableModelInputLimits
            {
                MaxRequestBytes = 33554432,
                Images = new DurableModelImageInputLimits
                {
                    MaxPerRequest = 600,
                    Resize = new DurableModelImageResizeOptions { MaxWidth = 2000, MaxHeight = 2000, MaxBytes = 4718592, JpegQuality = 80 },
                },
            }, fable.InputLimits, "claude-fable-5");
            var withLimits = 0;
            foreach (var model in models.GetModels())
            {
                var wire = model.Wire!.TryGetValue("inputLimits", out var limits) ? limits : null;
                Equal(wire is null, model.InputLimits is null, model.Id + " has limits");
                if (wire is not JsonObjectValue json) continue;
                withLimits++;
                var resize = json.TryGetValue("images", out var images) && ((JsonObjectValue)images).TryGetValue("resize", out var options) ? (JsonObjectValue)options : null;
                double? Field(string name) => resize is not null && resize.TryGetValue(name, out var value) ? ((JsonNumberValue)value).Value : null;
                Equal(Field("maxWidth"), (double?)model.InputLimits!.Images?.Resize?.MaxWidth, model.Id + " maxWidth");
                Equal(Field("maxHeight"), (double?)model.InputLimits.Images?.Resize?.MaxHeight, model.Id + " maxHeight");
                Equal(Field("maxBytes"), (double?)model.InputLimits.Images?.Resize?.MaxBytes, model.Id + " maxBytes");
            }
            Check(withLimits > 0, "catalog models declare input limits");
            Equal(null, DurableModelInputLimits.FromJson(JsonValue.Parse("[]")), "not an object");
            Equal(new DurableModelInputLimits { Images = new DurableModelImageInputLimits { Resize = new DurableModelImageResizeOptions { MaxHeight = 7 } } },
                DurableModelInputLimits.FromJson(JsonValue.Parse("""{"images":{"resize":{"maxWidth":0,"maxHeight":7,"maxBytes":1.5}}}""")), "invalid fields unset");
        }),
        Case("models.catalog.every-embedded-shard-matches-its-pinned-hash", () =>
        {
            Equal(42, DurableBuiltinCatalog.Providers.Length, "shards");
            foreach (var provider in DurableBuiltinCatalog.Providers)
                Equal(provider, DurableBuiltinCatalog.Get(provider)!.Provider, "shard " + provider);
            // The shards are PiSharp.Cli's pinned copies, linked rather than copied.
            var cli = Path.Combine(RepositoryRoot(), "src", "PiSharp.Cli", "Models");
            Equal(42, Directory.GetFiles(cli, "*.json").Length, "Cli shards");
        }),
        Case("models.catalog.registers-only-the-selected-and-host-providers", () =>
        {
            var host = PiSharp.AI.Catalogs.FrozenModelCatalog.ReadProviderJson("local", Encoding.UTF8.GetBytes(
                """{"openai-completions":{"chat:tiny":{"type":"chat","id":"tiny","name":"Tiny","api":"openai-completions","provider":"local","baseUrl":"http://127.0.0.1:9/v1","reasoning":false,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":4096,"maxTokens":512}}}"""));
            using var models = new NativeDurableModels(new() { Providers = ["openai"], Catalogs = [host], Environment = _ => null });
            Equal("local,openai", string.Join(",", models.Providers), "providers");
            Check(models.GetModel("anthropic", "claude-sonnet-4-5") is null, "anthropic is not registered");
            Equal(512d, models.GetModel("local", "tiny")!.MaxTokens, "host model");
            Throws<ArgumentException>(() => new NativeDurableModels(new() { Providers = ["nope"] }), "unknown built-in provider");
        }),
        Case("models.env.resolves-the-provider-key-from-the-injected-environment", async () =>
        {
            Equal("OPENAI_API_KEY", string.Join(",", NativeDurableModels.ApiKeyVariables("openai")), "openai variables");
            Equal("ANTHROPIC_API_KEY", string.Join(",", NativeDurableModels.ApiKeyVariables("anthropic")), "anthropic variables");
            Equal("", string.Join(",", NativeDurableModels.ApiKeyVariables("local")), "no variables");
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-openai-key" }, handler);
            var message = await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None);
            Equal(StopReason.Stop, message.StopReason, "stop");
            Equal("Bearer test-openai-key", handler.Requests.Single().Header("Authorization"), "bearer key");
        }),
        Case("models.env.an-explicit-key-resolver-wins-over-the-environment", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = new NativeDurableModels(new()
            {
                Environment = Env(new() { ["OPENAI_API_KEY"] = "test-env-key" }), HttpHandler = handler,
                ResolveApiKey = (model, _) => ValueTask.FromResult<string?>(model.Provider == "openai" ? "test-stored-key" : null),
            });
            await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None);
            Equal("Bearer test-stored-key", handler.Requests.Single().Header("Authorization"), "stored key wins");
        }),
        Case("models.env.a-missing-or-empty-key-ends-the-stream-with-provider-is-not-configured", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = Models(new() { ["OPENAI_API_KEY"] = "" }, handler);
            var events = await Collect(models.StreamSimple(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None));
            var error = (StreamError)events.Single();
            Equal(StopReason.Error, error.Reason, "error");
            Equal("Provider is not configured: openai", ErrorText(error.Message), "message");
            Equal(0, handler.Requests.Count, "no request");
        }),
        Case("models.env.without-an-environment-reader-no-variable-is-read", async () =>
        {
            // The portable core never reads the process environment: without a reader only ResolveApiKey supplies keys.
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = new NativeDurableModels(new() { HttpHandler = handler });
            var message = await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None);
            Equal("Provider is not configured: openai", ErrorText(message), "no key");
            Equal(0, handler.Requests.Count, "no request");
        }),
        Case("models.env.anthropic-uses-anthropic-api-key-and-refuses-oauth-tokens", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(AnthropicText));
            using (var oauth = Models(new() { ["ANTHROPIC_OAUTH_TOKEN"] = "test-oauth-token" }, handler))
            {
                var message = await oauth.CompleteSimpleAsync(oauth.GetModel("anthropic", "claude-sonnet-4-5")!, UserTurn("hi"), null, CancellationToken.None);
                Equal(StopReason.Error, message.StopReason, "refused");
                Check(ErrorText(message).Contains("ANTHROPIC_API_KEY", StringComparison.Ordinal), "names the variable: " + ErrorText(message));
            }
            using var models = Models(new() { ["ANTHROPIC_API_KEY"] = "test-anthropic-key" }, handler);
            var answer = await models.CompleteSimpleAsync(models.GetModel("anthropic", "claude-sonnet-4-5")!, UserTurn("hi"), null, CancellationToken.None);
            Equal(StopReason.Stop, answer.StopReason, "answered");
            Equal("test-anthropic-key", handler.Requests.Single().Header("x-api-key"), "x-api-key");
        }),
        Case("models.mapping.responses-carries-max-tokens-reasoning-session-cache-and-headers", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-openai-key" }, handler);
            await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-5")!, UserTurn("hi"), new DurableStreamOptions
            {
                MaxTokens = 1000, Reasoning = "high", SessionId = "session-1", CacheRetention = "long",
                Headers = new Dictionary<string, string> { ["X-Durable-Test"] = "yes" },
            }, CancellationToken.None);
            var request = handler.Requests.Single();
            Equal("https://api.openai.com/v1/responses", request.Uri, "endpoint");
            var body = request.Json;
            Equal("gpt-5", body.GetProperty("model").GetString(), "model");
            Equal(1000, body.GetProperty("max_output_tokens").GetInt32(), "max_output_tokens");
            Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString(), "effort");
            Equal("session-1", body.GetProperty("prompt_cache_key").GetString(), "cache key");
            Equal("24h", body.GetProperty("prompt_cache_retention").GetString(), "long retention");
            Check(body.GetProperty("stream").GetBoolean(), "streams");
            Equal("yes", request.Header("X-Durable-Test"), "caller header");
        }),
        Case("models.mapping.defaults-use-the-model-cap-and-clamp-the-level", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-openai-key" }, handler);
            // gpt-5 maps xhigh to null: the level clamps down to high. No session and cache retention none: no cache key.
            await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-5")!, UserTurn("hi"), new DurableStreamOptions { Reasoning = "xhigh" },
                CancellationToken.None);
            await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-5")!, UserTurn("hi"),
                new DurableStreamOptions { SessionId = "session-2", CacheRetention = "none" }, CancellationToken.None);
            var first = handler.Requests[0].Json;
            Equal(128000, first.GetProperty("max_output_tokens").GetInt32(), "model cap");
            Equal("high", first.GetProperty("reasoning").GetProperty("effort").GetString(), "clamped effort");
            var second = handler.Requests[1].Json;
            Check(!second.TryGetProperty("prompt_cache_key", out _), "no cache key without retention");
        }),
        Case("models.mapping.anthropic-carries-user-id-cache-ttl-and-the-thinking-budget", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(AnthropicText));
            using var models = Models(new() { ["ANTHROPIC_API_KEY"] = "test-anthropic-key" }, handler);
            await models.CompleteSimpleAsync(models.GetModel("anthropic", "claude-sonnet-4-5")!, UserTurn("hi"), new DurableStreamOptions
            {
                MaxTokens = 4000, Reasoning = "medium", CacheRetention = "long",
                Metadata = JsonValue.Object([new("user_id", "user-7"), new("ignored", 1)]),
            }, CancellationToken.None);
            var request = handler.Requests.Single();
            Equal("https://api.anthropic.com/v1/messages?beta=true", request.Uri, "endpoint");
            var body = request.Json;
            // adjustMaxTokensForThinking: 4000 + the medium budget 8192.
            Equal(12192, body.GetProperty("max_tokens").GetInt32(), "max_tokens");
            Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString(), "thinking");
            Equal(8192, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32(), "budget");
            Equal("user-7", body.GetProperty("metadata").GetProperty("user_id").GetString(), "user id");
            Check(request.Body.Contains("\"ttl\":\"1h\"", StringComparison.Ordinal), "long cache ttl: " + request.Body);
        }),
        Case("models.mapping.completions-binds-the-catalog-endpoint", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(CompletionsText));
            using var models = Models(new() { ["GROQ_API_KEY"] = "test-groq-key" }, handler);
            var message = await models.CompleteSimpleAsync(models.GetModel("groq", "llama-3.3-70b-versatile")!, UserTurn("hi"),
                new DurableStreamOptions { MaxTokens = 77 }, CancellationToken.None);
            var request = handler.Requests.Single();
            Equal("https://api.groq.com/openai/v1/chat/completions", request.Uri, "endpoint");
            Equal("Bearer test-groq-key", request.Header("Authorization"), "key");
            var body = request.Json;
            Check(body.TryGetProperty("max_completion_tokens", out var cap) && cap.GetInt32() == 77 ||
                body.TryGetProperty("max_tokens", out cap) && cap.GetInt32() == 77, "cap: " + request.Body);
            Equal("Hi", ((TextContent)message.Content.Single()).Text, "answer");
        }),
        Case("models.mapping.system-and-tool-messages-reach-the-request", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-openai-key" }, handler);
            ImmutableArray<JsonValue> transcript =
            [
                JsonValue.Parse("""{"role":"system","content":"You are a concise assistant.","timestamp":0}"""),
                .. UserTurn("hi"),
            ];
            await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-4.1")!, transcript, null, CancellationToken.None);
            Check(handler.Requests.Single().Body.Contains("You are a concise assistant.", StringComparison.Ordinal), "system prompt sent");
            var invalid = await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-4.1")!, [JsonValue.Parse("""{"content":"x"}""")], null,
                CancellationToken.None);
            Equal("Transcript message 0 has no role.", ErrorText(invalid), "role required");
        }),
        Case("models.stream.responses-sse-becomes-the-durable-assistant-message", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(ResponsesText));
            using var models = new NativeDurableModels(new()
            {
                Environment = Env(new() { ["OPENAI_API_KEY"] = "test-openai-key" }), HttpHandler = handler, TimeProvider = new FixedTime(1_800_000_000_000),
            });
            var events = await Collect(models.StreamSimple(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None));
            Equal("Hello world", string.Concat(events.OfType<TextDelta>().Select(delta => delta.Delta)), "deltas");
            var done = (StreamDone)events[^1];
            Check(events.Take(events.Count - 1).All(value => value is not StreamTerminalEvent), "one terminal, last");
            Equal(StopReason.Stop, done.Reason, "stop");
            // The exact pi-ai wire message durable stores (generation appends it as the pi.assistant entry), and its round trip.
            var wire = PiWireJson.WriteMessage(done.Message);
            var stored = (JsonObjectValue)JsonValue.FromData(wire);
            Equal("""{"role":"assistant","content":[{"type":"text","text":"Hello world","textSignature":"{\"v\":1,\"id\":\"msg_1\"}"}],"api":"openai-responses","provider":"openai","model":"gpt-4.1","usage":{"input":12,"output":3,"cacheRead":0,"cacheWrite":0,"reasoning":0,"totalTokens":15,"cost":{"input":0.000024,"output":0.000024,"cacheRead":0,"cacheWrite":0,"total":0.000048}},"stopReason":"stop","timestamp":1800000000000,"responseId":"resp_1","rawStopReason":"completed","durationMs":0}""",
                stored.ToJson(), "stored message");
            var reread = PiWireJson.ReadMessage(wire.Value);
            Equal(wire.ToString(), PiWireJson.WriteMessage(reread).ToString(), "round trip");
            var completed = await models.CompleteSimpleAsync(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None);
            Equal(stored.ToJson(), JsonValue.FromData(PiWireJson.WriteMessage(completed)).ToJson(), "complete simple returns the same message");
        }),
        Case("models.stream.anthropic-sse-becomes-the-durable-assistant-message", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(AnthropicText));
            using var models = new NativeDurableModels(new()
            {
                Environment = Env(new() { ["ANTHROPIC_API_KEY"] = "test-anthropic-key" }), HttpHandler = handler, TimeProvider = new FixedTime(1_800_000_000_000),
            });
            var message = await models.CompleteSimpleAsync(models.GetModel("anthropic", "claude-haiku-4-5")!, UserTurn("hi"), null, CancellationToken.None);
            var stored = (JsonObjectValue)JsonValue.FromData(PiWireJson.WriteMessage(message));
            Equal("assistant", ((JsonStringValue)stored["role"]).Value, "role");
            Equal("stop", ((JsonStringValue)stored["stopReason"]).Value, "stop reason");
            Equal("""[{"type":"text","text":"Hello there"}]""", stored["content"].ToJson(), "content");
            Equal("anthropic/claude-haiku-4-5", ((JsonStringValue)stored["provider"]).Value + "/" + ((JsonStringValue)stored["model"]).Value, "usage key");
            Equal(9d, ((JsonNumberValue)((JsonObjectValue)stored["usage"])["input"]).Value, "input tokens");
        }),
        Case("models.stream.http-errors-and-setup-failures-end-with-an-error-message", async () =>
        {
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("""{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}""", Encoding.UTF8, "application/json") });
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-bad-key", ["AWS_PROFILE"] = "test" }, handler);
            var events = await Collect(models.StreamSimple(models.GetModel("openai", "gpt-4.1")!, UserTurn("hi"), null, CancellationToken.None));
            var error = (StreamError)events[^1];
            Equal(StopReason.Error, error.Reason, "error");
            Check(ErrorText(error.Message).Length > 0, "error message");
            var unknown = await models.CompleteSimpleAsync(new DurableModel { Id = "m", Name = "m", Api = "openai-responses", Provider = "nobody" },
                UserTurn("hi"), null, CancellationToken.None);
            Equal("Unknown provider: nobody", ErrorText(unknown), "unknown provider");
            var bedrock = models.GetModels("amazon-bedrock")[0];
            Check(!NativeDurableModels.Supports(bedrock), "bedrock is unsupported");
            var refused = await models.CompleteSimpleAsync(bedrock, UserTurn("hi"), null, CancellationToken.None);
            Equal(StopReason.Error, refused.StopReason, "refused");
            Check(ErrorText(refused).Contains("amazon-bedrock", StringComparison.Ordinal), "names the provider: " + ErrorText(refused));
        }),
        Case("models.stream.timeout-is-a-retryable-error-and-caller-abort-is-aborted", async () =>
        {
            var handler = new RecordingHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Sse(ResponsesText); });
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-openai-key" }, handler);
            var model = models.GetModel("openai", "gpt-4.1")!;
            var timedOut = await models.CompleteSimpleAsync(model, UserTurn("hi"), new DurableStreamOptions { TimeoutMs = 200 }, CancellationToken.None);
            Equal(StopReason.Error, timedOut.StopReason, "timeout is an error");
            Equal("Request timed out.", ErrorText(timedOut), "timeout message");
            using var abort = new CancellationTokenSource(200);
            var aborted = await models.CompleteSimpleAsync(model, UserTurn("hi"), new DurableStreamOptions { TimeoutMs = 60_000 }, abort.Token);
            Equal(StopReason.Aborted, aborted.StopReason, "caller abort");
            using var already = new CancellationTokenSource();
            await already.CancelAsync();
            var early = await models.CompleteSimpleAsync(model, UserTurn("hi"), null, already.Token);
            Equal(StopReason.Aborted, early.StopReason, "cancelled before the request");
        }),
        Case("models.deferred.reports-the-gap", async () =>
        {
            using var models = Models(new() { ["OPENAI_API_KEY"] = "test-openai-key" }, new RecordingHandler(_ => Sse(ResponsesText)));
            var model = models.GetModel("openai", "gpt-4.1")!;
            var handle = new DurableDeferredHandle(JsonValue.Object([new("provider", "openai"), new("modelId", "gpt-4.1"), new("api", "openai-responses"), new("id", "d1")]));
            var events = await Collect(models.StreamDeferred(model, handle, CancellationToken.None));
            var error = (StreamError)events.Single();
            Check(ErrorText(error.Message).StartsWith("Provider openai does not support deferred responses", StringComparison.Ordinal), "stream gap");
            Equal(StopReason.Error, (await models.FetchDeferredAsync(model, handle, CancellationToken.None)).StopReason, "fetch gap");
            var thrown = await ThrowsAsync<NotSupportedException>(() => models.CancelDeferredAsync(model, handle, CancellationToken.None), "cancel gap");
            Check(thrown.Message.Contains("deferred", StringComparison.Ordinal), "names deferred");
        }),
    ];

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Func<string, string?> Env(Dictionary<string, string> values) => name => values.GetValueOrDefault(name);

    private static NativeDurableModels Models(Dictionary<string, string> environment, HttpMessageHandler? handler = null) =>
        new(new() { Environment = Env(environment), HttpHandler = handler ?? new RecordingHandler(_ => Sse(ResponsesText)) });

    private static ImmutableArray<JsonValue> UserTurn(string text) =>
        [JsonValue.Object([new("role", "user"), new("content", text), new("timestamp", 1)])];

    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> events)
    {
        var list = new List<StreamEvent>();
        await foreach (var value in events) list.Add(value);
        return list;
    }

    private static string ErrorText(AssistantMessage message) =>
        message.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var text) && text is not null ? text.Value.GetString() ?? "" : "";

    private static HttpResponseMessage Sse(string frames) => new(HttpStatusCode.OK) { Content = new StringContent(frames, Encoding.UTF8, "text/event-stream") };

    /// <summary>Recorded OpenAI Responses stream: one message with "Hello world".</summary>
    private const string ResponsesText =
        "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"in_progress\",\"output\":[]}}\n\n" +
        "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
        "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\",\"sequence_number\":2,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\",\"annotations\":[]}}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":3,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello\"}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":4,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\" world\"}\n\n" +
        "event: response.output_text.done\ndata: {\"type\":\"response.output_text.done\",\"sequence_number\":5,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"text\":\"Hello world\"}\n\n" +
        "event: response.content_part.done\ndata: {\"type\":\"response.content_part.done\",\"sequence_number\":6,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"Hello world\",\"annotations\":[]}}\n\n" +
        "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":7,\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello world\",\"annotations\":[]}]}}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":8,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":3,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":15}}}\n\n";

    /// <summary>Recorded Anthropic Messages stream: one text block "Hello there".</summary>
    private const string AnthropicText =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":9,\"output_tokens\":1}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" there\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    /// <summary>Recorded OpenAI Chat Completions stream: "Hi".</summary>
    private const string CompletionsText =
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"llama-3.3-70b-versatile\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"llama-3.3-70b-versatile\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}\n\n" +
        "data: [DONE]\n\n";

    /// <summary>A clock frozen at one instant (request timestamps and durationMs are deterministic).</summary>
    private sealed class FixedTime(long unixMilliseconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        public override long GetTimestamp() => 0;
    }

    /// <summary>One request the fake provider saw.</summary>
    private sealed record RecordedRequest(string Uri, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string? Header(string name) => Headers.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Records every request and answers it offline.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond;
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => this.respond = (request, _) => Task.FromResult(respond(request));
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => this.respond = respond;
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            lock (Requests) Requests.Add(new(request.RequestUri!.ToString(), headers, body));
            return await respond(request, cancellationToken);
        }
    }
}
