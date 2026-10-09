using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Authored expectations derived by reading Pi v1.1.0 (abe508e1b89912adde45528136c3221eb69acdd7) source text, not captured:
// packages/ai/src/api/anthropic-messages.ts (stream, streamSimple, mapThinkingLevelToEffort, getBetaFeatures, buildParams,
// convertMessages, insertThinkingLevelMessages, the input_transformations diagnostic), packages/ai/src/models.ts
// (getSupportedThinkingLevels) and the pinned anthropic.json catalog rows. Fake HTTP only.
internal static partial class Program
{
    private static readonly Uri AnthropicEndpoint = new("https://api.anthropic.com/");
    private const string MidBetas = "mid-conversation-output-config-2026-07-01,thinking-binding-controls-2026-08-01";
    private const string Managed = "\"thinking\":{\"type\":\"adaptive\",\"display\":\"summarized\",\"block_binding\":{\"prefix_mismatch_behavior\":\"drop_block\"}},\"output_config\":{\"effort\":\"high\"}";
    private const string Ephemeral = ",\"cache_control\":{\"type\":\"ephemeral\"}";
    // models.ts getSupportedThinkingLevels over each row's thinkingLevelMap: off is null for all five.
    private static readonly (string Id, string[] Levels)[] MidEffortModels =
    [
        ("claude-opus-5", ["minimal", "low", "medium", "high", "xhigh", "max"]),
        ("claude-opus-5-5", ["low", "medium", "high", "xhigh", "max"]),
        ("claude-sonnet-5-5", ["low", "medium", "high", "xhigh", "max"]),
        ("claude-haiku-5-5", ["low", "medium", "high", "xhigh", "max"]),
        ("claude-fable-5-1", ["minimal", "low", "medium", "high", "xhigh", "max"])
    ];

    private static string Marker(string effort) => "{\"role\":\"system\",\"content\":[],\"output_config\":{\"effort\":\"" + effort + "\"}}";
    private static string CachedUser(string text) => "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"" + Ephemeral + "}]}";
    private static string MidBody(string id, int maximum, string? system, params string[] messages) => "{\"model\":\"" + id + "\",\"messages\":[" +
        string.Join(",", messages) + "],\"max_tokens\":" + maximum + ",\"stream\":true," + (system is null ? "" : "\"system\":[" + system + "],") + Managed + "}";
    private static string KeyHeaders(string? beta) => string.Join("\n", new[] { "accept: application/json", beta is null ? null : "anthropic-beta: " + beta,
        "anthropic-dangerous-direct-browser-access: true", "anthropic-version: 2023-06-01", "content-type: application/json", "user-agent: PiSharp",
        "x-api-key: " + Key }.Where(line => line is not null));

    private sealed record Sent(string Headers, string Body);
    /// <summary>Records each request (headers sorted, lower-cased) and answers with one Messages stream.</summary>
    private sealed class MessagesEndpoint(Func<int, string> stream) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var headers = request.Headers.Concat(request.Content!.Headers).Select(pair => pair.Key.ToLowerInvariant() + ": " + string.Join(", ", pair.Value))
                .Order(StringComparer.Ordinal);
            Requests.Add(new(string.Join("\n", headers), await request.Content.ReadAsStringAsync(token)));
            return new(HttpStatusCode.OK) { Content = new StringContent(stream(Requests.Count - 1), Encoding.UTF8, "text/event-stream") };
        }
    }
    private static string MessagesStream(string model, string text, string? thinking = null, string? signature = null,
        object? startTransformations = null, object? deltaTransformations = null)
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        var message = new Dictionary<string, object> { ["id"] = "authored", ["role"] = "assistant", ["model"] = model, ["content"] = Array.Empty<object>(),
            ["usage"] = new { input_tokens = 3, output_tokens = 0 } };
        if (startTransformations is not null) message["input_transformations"] = startTransformations;
        var stream = new StringBuilder(Frame("message_start", new { type = "message_start", message }));
        var index = 0;
        if (thinking is not null)
            stream.Append(Frame("content_block_start", new { type = "content_block_start", index, content_block = new { type = "thinking", thinking = "", signature = "" } }))
                .Append(Frame("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "thinking_delta", thinking } }))
                .Append(Frame("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "signature_delta", signature } }))
                .Append(Frame("content_block_stop", new { type = "content_block_stop", index = index++ }));
        stream.Append(Frame("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = "" } }))
            .Append(Frame("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text } }))
            .Append(Frame("content_block_stop", new { type = "content_block_stop", index }));
        var delta = new Dictionary<string, object> { ["type"] = "message_delta", ["delta"] = new { stop_reason = "end_turn" }, ["usage"] = new { output_tokens = 2 } };
        if (deltaTransformations is not null) delta["input_transformations"] = deltaTransformations;
        return stream.Append(Frame("message_delta", delta)).Append(Frame("message_stop", new { type = "message_stop" })).ToString();
    }
    private static string? Level(AssistantMessage message) =>
        message.ExtraProperties?.TryGet("providerThinkingLevel", out var level) == true ? level!.Value.GetString() : null;
    private static NativeHttpModelProvider NativeAnthropic(string id, int maximum, HttpMessageHandler handler, AnthropicMessagesRequestOptions? projection = null) =>
        NativeProviderFactory.CreateAnthropic(new(id, "anthropic-messages", "anthropic"), AnthropicEndpoint, Key,
            projection ?? new(MaximumTokens: maximum, ModelReasoning: true, ThinkingEnabled: false), new(MaxTokens: maximum), handler, CatalogRow("anthropic", id));
    private sealed class ProviderLease(NativeHttpModelProvider provider) : IAsyncDisposable
    { public ValueTask DisposeAsync() { provider.Dispose(); return ValueTask.CompletedTask; } }
    private static async Task<(AnthropicInjectedTransportLease Lease, ImmutableArray<string> Levels)> ResolvedAnthropic(string id, int maximum,
        HttpMessageHandler handler, string environmentName = "ANTHROPIC_API_KEY", string secret = Key)
    {
        var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
        var resolution = await InjectedAuthenticationResolver.ResolveAnthropicApiKeyAsync(
            new ProviderEnvironmentSnapshot([KeyValuePair.Create<string, string?>(environmentName, secret)]), (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(null));
        ImmutableArray<string> levels = default;
        var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(model, resolution, (selected, binding, _) =>
        {
            var provider = AnthropicResolvedProviderFactory.Create(selected, AnthropicEndpoint, binding,
                new(MaximumTokens: maximum, ModelReasoning: true, ThinkingEnabled: false), new(MaxTokens: maximum), handler, CatalogRow("anthropic", id));
            levels = ThinkingLevels.GetSupported(provider.Transport, selected);
            return ValueTask.FromResult(new AnthropicTransportAdmission(selected, provider.Transport, new ProviderLease(provider)));
        });
        return (lease, levels);
    }
    private static TranscriptEntry UserText(string text, long timestamp) => Entry("{\"role\":\"user\",\"content\":\"" + text + "\",\"timestamp\":" + timestamp + "}");

    // Every managed row constructs on the key route and the resolved route, at a normal and a sub-2048 cap, with
    // upstream's level list; the first allowed level (the session default when off is unavailable) sends managed effort.
    private static async Task MidEffortModelsConstruct()
    {
        foreach (var (id, levels) in MidEffortModels)
        {
            var row = CatalogRow("anthropic", id).Value;
            Check(row.GetProperty("compat").GetProperty("supportsMidConvoEffort").GetBoolean() && row.GetProperty("compat").GetProperty("forceAdaptiveThinking").GetBoolean() &&
                row.GetProperty("thinkingLevelMap").GetProperty("off").ValueKind == JsonValueKind.Null, id + " catalog row changed.");
            var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
            foreach (var maximum in new[] { 4096, 1024 })
            {
                var endpoint = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
                using (var provider = NativeAnthropic(id, maximum, endpoint))
                {
                    Check(ThinkingLevels.GetSupported(provider.Transport, model).SequenceEqual(levels), id + " key-route levels.");
                    var result = await new ChatClient(provider.Transport).CompleteAsync(new(model, [Ask], 1) { ThinkingLevel = levels[0] }).WaitAsync(Deadline);
                    Equal(StopReason.Stop, result.Message.StopReason); Equal("low", Level(result.Message));
                }
                var (lease, resolvedLevels) = await ResolvedAnthropic(id, maximum, endpoint);
                await using (lease)
                {
                    Check(resolvedLevels.SequenceEqual(levels), id + " resolved-route levels.");
                    var result = await new ChatClient(lease.Transport).CompleteAsync(new(model, [Ask], 1) { ThinkingLevel = levels[0] }).WaitAsync(Deadline);
                    Equal(StopReason.Stop, result.Message.StopReason); Equal("low", Level(result.Message));
                }
                foreach (var sent in endpoint.Requests)
                {
                    BodyEqual(MidBody(id, maximum, null, CachedUser("ask"), Marker("low")), sent.Body);
                    Equal(KeyHeaders(MidBetas), sent.Headers);
                }
                Equal(2, endpoint.Requests.Count);
            }
        }
    }

    // claude-sonnet-5-5 on the key route: every allowed level, the unselected default (effort omitted means "high"), the
    // rejected levels, the resolved OAuth projection and the Simple factory.
    private static async Task MidEffortSonnetEveryLevel()
    {
        const string id = "claude-sonnet-5-5"; var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
        var endpoint = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
        using (var provider = NativeAnthropic(id, 4096, endpoint))
        {
            foreach (var level in new[] { null, "low", "medium", "high", "xhigh", "max" })
            {
                var result = await new ChatClient(provider.Transport).CompleteAsync(new(model, [Ask], 1) { ThinkingLevel = level }).WaitAsync(Deadline);
                Equal(level ?? "high", Level(result.Message));
                Check(PiWireJson.WriteMessage(result.Message).ToString().Contains("\"providerThinkingLevel\":\"" + (level ?? "high") + "\"", StringComparison.Ordinal),
                    "providerThinkingLevel is not persisted.");
                var sent = endpoint.Requests[^1];
                BodyEqual(MidBody(id, 4096, null, CachedUser("ask"), Marker(level ?? "high")), sent.Body);
                Equal(KeyHeaders(MidBetas), sent.Headers);
            }
            foreach (var rejected in new[] { "off", "minimal" })
            {
                var refused = false;
                try { await foreach (var _ in provider.Transport.StreamAsync(new(model, [Ask], 1) { ThinkingLevel = rejected })) { } }
                catch (ArgumentException) { refused = true; }
                Check(refused, rejected + " was admitted for an off:null/minimal:null model.");
            }
        }
        Equal(6, endpoint.Requests.Count);

        // Resolved OAuth projection: the Claude Code betas come first, then the managed-effort pair.
        var oauth = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
        var (lease, _) = await ResolvedAnthropic(id, 4096, oauth, "ANTHROPIC_OAUTH_TOKEN", "sk-ant-oat01-inert");
        await using (lease)
            Equal("xhigh", Level((await new ChatClient(lease.Transport).CompleteAsync(new(model, [Ask], 1) { ThinkingLevel = "xhigh" }).WaitAsync(Deadline)).Message));
        var authored = oauth.Requests.Single();
        BodyEqual(MidBody(id, 4096, "{\"type\":\"text\",\"text\":\"You are Claude Code, Anthropic's official CLI for Claude.\"" + Ephemeral + "}", CachedUser("ask"), Marker("xhigh")), authored.Body);
        Check(authored.Headers.Split('\n').Contains("anthropic-beta: claude-code-20250219,oauth-2025-04-20," + MidBetas), "OAuth betas: " + authored.Headers);

        // Simple: streamSimple maps the reasoning level (minimal falls back to low), and an omitted level still sends managed "high".
        var simple = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
        using var client = new HttpClient(simple);
        foreach (var (reasoning, effort) in new[] { ("minimal", "low"), ("xhigh", "xhigh"), ((string?)null, "high") })
        {
            var factory = new AnthropicMessagesSimpleRequestFactory(client, AnthropicEndpoint, model,
                new(CatalogRow("anthropic", id), new(4096), reasoning) { ApiKey = Key });
            var result = await new ChatClient(factory).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline);
            Equal(effort, Level(result.Message));
            BodyEqual(MidBody(id, 128000, null, CachedUser("ask"), Marker(effort)), simple.Requests[^1].Body);
            Equal(KeyHeaders(MidBetas), simple.Requests[^1].Headers);
        }
    }

    // Three turns on claude-sonnet-5-5: each response records its effort, and later requests replay it as a marker before that
    // assistant turn. Legacy (no level), other-provider and failed assistants get no marker; the active marker is last.
    private static async Task MidEffortReplay()
    {
        const string id = "claude-sonnet-5-5"; var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
        var endpoint = new MessagesEndpoint(turn => turn switch
        {
            0 => MessagesStream(id, "first", "plan", "sig-1"),
            1 => MessagesStream(id, "second", "deeper", "sig-2"),
            _ => MessagesStream(id, "third")
        });
        using var provider = NativeAnthropic(id, 4096, endpoint,
            new(MaximumTokens: 4096, ModelReasoning: true, ThinkingEnabled: false, SupportsMidConversationSystemMessages: true));
        var client = new ChatClient(provider.Transport);
        TranscriptEntry Replay(AssistantMessage message) => new("assistant", PiWireJson.WriteMessage(message));
        var system = Entry("""{"role":"system","content":"Base","timestamp":0}""");
        var first = (await client.CompleteAsync(new(model, [system, UserText("one", 1)], 1) { ThinkingLevel = "low" }).WaitAsync(Deadline)).Message;
        Equal("low", Level(first));
        var second = (await client.CompleteAsync(new(model, [system, UserText("one", 1), Replay(first), UserText("two", 3)], 3) { ThinkingLevel = "max" }).WaitAsync(Deadline)).Message;
        Equal("max", Level(second));
        AssistantMessage Authored(string provider, string text, StopReason reason, string? level) => new("anthropic-messages", provider, id, 5,
            [new TextContent(text)], TokenUsage.Zero, reason, level is null ? null : JsonFields.Empty.Set("providerThinkingLevel", JsonData.Parse("\"" + level + "\"")));
        var third = (await client.CompleteAsync(new(model, [system, UserText("one", 1), Replay(first), UserText("two", 3), Replay(second), UserText("three", 5),
            Replay(Authored("anthropic", "failed", StopReason.Error, "xhigh")), Replay(Authored("anthropic", "legacy", StopReason.Stop, null)), UserText("four", 7),
            Replay(Authored("other-provider", "other", StopReason.Stop, "low")), Entry("""{"role":"system","content":"Be brief","timestamp":8}"""),
            UserText("five", 9)], 9) { ThinkingLevel = "medium" }).WaitAsync(Deadline)).Message;
        Equal("medium", Level(third));

        const string baseSystem = "{\"type\":\"text\",\"text\":\"Base\"" + Ephemeral + "}";
        const string a1 = """{"role":"assistant","content":[{"type":"thinking","thinking":"plan","signature":"sig-1"},{"type":"text","text":"first"}]}""";
        const string a2 = """{"role":"assistant","content":[{"type":"thinking","thinking":"deeper","signature":"sig-2"},{"type":"text","text":"second"}]}""";
        static string Plain(string role, string text) => "{\"role\":\"" + role + "\",\"content\":" + (role == "user" ? "\"" + text + "\"" : "[{\"type\":\"text\",\"text\":\"" + text + "\"}]") + "}";
        var expected = new[]
        {
            MidBody(id, 4096, baseSystem, CachedUser("one"), Marker("low")),
            MidBody(id, 4096, baseSystem, Plain("user", "one"), Marker("low"), a1, CachedUser("two"), Marker("max")),
            // The held system update is flushed after the last user turn, takes the cache breakpoint, and precedes the active marker.
            MidBody(id, 4096, baseSystem, Plain("user", "one"), Marker("low"), a1, Plain("user", "two"), Marker("max"), a2, Plain("user", "three"),
                Plain("assistant", "legacy"), Plain("user", "four"), Plain("assistant", "other"), Plain("user", "five"),
                "{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"Be brief\"" + Ephemeral + "}]}", Marker("medium"))
        };
        Equal(3, endpoint.Requests.Count);
        for (var turn = 0; turn < 3; turn++) { BodyEqual(expected[turn], endpoint.Requests[turn].Body); Equal(KeyHeaders(MidBetas), endpoint.Requests[turn].Headers); }
    }

    // anthropic_input_transformations: the last reported array is a diagnostic on a completed response, stamped Date.now() at completion
    // (anthropic-messages.ts ~871-882), not the request timestamp.
    private static async Task MidEffortInputTransformations()
    {
        const string id = "claude-fable-5-1"; var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
        var endpoint = new MessagesEndpoint(_ => MessagesStream(id, "ok",
            startTransformations: new[] { new { type = "thinking_dropped", path = "messages.1.content.0", reason = "prefix_binding_mismatch" } },
            deltaTransformations: new object[] { new { type = "thinking_dropped", path = "messages.3.content.0", reason = "model_binding_mismatch" }, new { type = "thinking_dropped" } }));
        using var provider = NativeAnthropic(id, 4096, endpoint);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var message = (await new ChatClient(provider.Transport).CompleteAsync(new(model, [Ask], 42) { ThinkingLevel = "high" }).WaitAsync(Deadline)).Message;
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Equal(StopReason.Stop, message.StopReason); Equal("high", Level(message));
        var diagnostics = message.ExtraProperties!.TryGet("diagnostics", out var value) ? value!.Value : throw new InvalidOperationException("No diagnostics.");
        var stamp = diagnostics[0].GetProperty("timestamp").GetInt64();
        Check(stamp >= before && stamp <= after, "The diagnostic was not stamped at completion.");
        Equal("""[{"type":"anthropic_input_transformations","timestamp":0,"details":{"transformations":[{"type":"thinking_dropped","path":"messages.3.content.0","reason":"model_binding_mismatch"},{"type":"thinking_dropped"}]}}]""",
            diagnostics.ToString().Replace("\"timestamp\":" + stamp, "\"timestamp\":0", StringComparison.Ordinal));
    }

    private sealed class FixedClock(long milliseconds) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }

    // Captured from the installed pi-ai 1.1.0 stream() against a local SSE server: a non-array input_transformations is ignored
    // (Array.isArray), entries map `type/path/reason ?? undefined` unchecked (values copied, null omitted, non-objects -> {}),
    // an empty last array reports nothing, and a null entry fails with the TypeError text.
    private static async Task AnthropicInputTransformationsUnchecked()
    {
        const string id = "claude-sonnet-4-5"; var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
        async Task<AssistantMessage> Run(object? start, object? delta)
        {
            var events = MessagesStream(id, "ok", startTransformations: start, deltaTransformations: delta)
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(frame => JsonData.Parse(frame.Split("\ndata: ")[1])).ToArray();
            async IAsyncEnumerable<JsonData> Source() { foreach (var item in events) { await Task.Yield(); yield return item; } }
            var transport = new AnthropicMessagesTransport((_, _) => Source(), new AnthropicMessagesOptions { Clock = new FixedClock(1791560882073) });
            return (await new ChatClient(transport).CompleteAsync(new(model, [Ask], 42)).WaitAsync(Deadline)).Message;
        }
        static string? Text(AssistantMessage message, string name) => message.ExtraProperties?.TryGet(name, out var value) == true ? value!.ToString() : null;
        var mixed = await Run(new[] { new { type = "a" } }, new object?[] { new Dictionary<string, object?> { ["type"] = 1, ["path"] = null, ["reason"] = new { x = true } },
            "str", 5, new[] { 1 }, new { path = "p", extra = 9 }, new { } });
        Equal(StopReason.Stop, mixed.StopReason);
        Equal("""[{"type":"anthropic_input_transformations","timestamp":1791560882073,"details":{"transformations":[{"type":1,"reason":{"x":true}},{},{},{},{"path":"p"},{}]}}]""",
            Text(mixed, "diagnostics"));
        var kept = await Run(new[] { new { type = "kept" } }, new { type = "unsupported" });
        Equal(StopReason.Stop, kept.StopReason);
        Equal("""[{"type":"anthropic_input_transformations","timestamp":1791560882073,"details":{"transformations":[{"type":"kept"}]}}]""", Text(kept, "diagnostics"));
        var ignored = await Run("nope", null); Equal(StopReason.Stop, ignored.StopReason); Equal(null, Text(ignored, "diagnostics"));
        var empty = await Run(new[] { new { type = "x" } }, Array.Empty<object>()); Equal(StopReason.Stop, empty.StopReason); Equal(null, Text(empty, "diagnostics"));
        var nullEntry = await Run(null, new object?[] { new { type = "t" }, null });
        Equal(StopReason.Error, nullEntry.StopReason); Equal(null, Text(nullEntry, "diagnostics"));
        Equal("Cannot read properties of null (reading 'type')", nullEntry.ExtraProperties!.TryGet("errorMessage", out var error) ? error!.Value.GetString() : null);
    }

    // Unmanaged models are unchanged: claude-fable-5 (adaptive, off:null, fallbacks) keeps top-level effort without markers even when a
    // replayed turn carries a level; claude-sonnet-4-5 keeps budget thinking, and a sub-2048 cap still leaves it only off.
    private static async Task UnmanagedAnthropicModelsUnchanged()
    {
        var fable = new ModelDescriptor("claude-fable-5", "anthropic-messages", "anthropic");
        var endpoint = new MessagesEndpoint(_ => MessagesStream("claude-fable-5", "ok"));
        var replayed = new TranscriptEntry("assistant", PiWireJson.WriteMessage(new("anthropic-messages", "anthropic", "claude-fable-5", 2,
            [new TextContent("before")], TokenUsage.Zero, StopReason.Stop, JsonFields.Empty.Set("providerThinkingLevel", JsonData.Parse("\"low\"")))));
        using (var provider = NativeAnthropic("claude-fable-5", 4096, endpoint, new(MaximumTokens: 4096, ModelReasoning: true, ThinkingEnabled: false,
            AllowedFallbackModels: ["claude-opus-4-8", "claude-opus-5"])))
        {
            Check(ThinkingLevels.GetSupported(provider.Transport, fable).SequenceEqual(["minimal", "low", "medium", "high", "xhigh", "max"]), "Fable 5 levels.");
            var message = (await new ChatClient(provider.Transport).CompleteAsync(new(fable, [UserText("one", 1), replayed, Ask], 3) { ThinkingLevel = "max" }).WaitAsync(Deadline)).Message;
            Equal(null, Level(message));
        }
        BodyEqual("""{"model":"claude-fable-5","messages":[{"role":"user","content":"one"},{"role":"assistant","content":[{"type":"text","text":"before"}]},""" + CachedUser("ask") +
            """],"max_tokens":4096,"stream":true,"thinking":{"type":"adaptive","display":"summarized"},"output_config":{"effort":"max"},"fallbacks":[{"model":"claude-opus-4-8"},{"model":"claude-opus-5"}]}""",
            endpoint.Requests.Single().Body);
        Equal(KeyHeaders("server-side-fallback-2026-07-01"), endpoint.Requests.Single().Headers);

        var sonnet = new ModelDescriptor("claude-sonnet-4-5", "anthropic-messages", "anthropic");
        var budget = new MessagesEndpoint(_ => MessagesStream("claude-sonnet-4-5", "ok"));
        using (var provider = NativeAnthropic("claude-sonnet-4-5", 4096, budget))
        {
            Check(ThinkingLevels.GetSupported(provider.Transport, sonnet).SequenceEqual(["off", "minimal", "low", "medium", "high"]), "Sonnet 4.5 levels.");
            foreach (var level in new[] { "medium", "off" })
                Equal(null, Level((await new ChatClient(provider.Transport).CompleteAsync(new(sonnet, [Ask], 1) { ThinkingLevel = level }).WaitAsync(Deadline)).Message));
        }
        BodyEqual("""{"model":"claude-sonnet-4-5","messages":[""" + CachedUser("ask") + """],"max_tokens":4096,"stream":true,"thinking":{"type":"enabled","budget_tokens":3072,"display":"summarized"}}""",
            budget.Requests[0].Body);
        Equal(KeyHeaders("interleaved-thinking-2025-05-14"), budget.Requests[0].Headers);
        BodyEqual("""{"model":"claude-sonnet-4-5","messages":[""" + CachedUser("ask") + """],"max_tokens":4096,"stream":true,"thinking":{"type":"disabled"}}""", budget.Requests[1].Body);
        Equal(KeyHeaders(null), budget.Requests[1].Headers);
        using (var capped = NativeAnthropic("claude-sonnet-4-5", 2047, budget))
            Check(ThinkingLevels.GetSupported(capped.Transport, sonnet).SequenceEqual(["off"]), "A nonadaptive sub-2048 cap admitted thinking.");
    }
}
