using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

// Authored expectations derived by reading Pi v1.5.0 (f9a26365568127461c47a07a37dcb42c140a3ac5) dc939913f: packages/ai/src/api/
// anthropic-messages.ts (buildParams thinkingDisabled and effortMessages, providerThinkingLevel), test/anthropic-mid-conversation-effort.test.ts
// and the @earendil-works/pi-ai@1.5.0 anthropic.json rows. Not captured; fake HTTP only.
internal static partial class Program
{
    private static (string Id, Func<Task> Run)[] Sync150Cases() =>
    [
        ("sync150.claude-opus-5-and-haiku-5-5-off-sends-disabled-thinking", Sync150DisabledThinking),
        ("sync150.claude-opus-5-5-off-unsupported-keeps-managed-effort", Sync150OffUnsupported),
    ];

    private static TranscriptEntry Sync150Replayed(string id) => new("assistant", PiWireJson.WriteMessage(new("anthropic-messages", "anthropic", id, 2,
        [new TextContent("before")], TokenUsage.Zero, StopReason.Stop, JsonFields.Empty.Set("providerThinkingLevel", JsonData.Parse("\"low\"")))));

    // DISABLED_THINKING_MODELS: "off" sends thinking {type: "disabled"} with no output_config, no effort markers (not even for a replayed
    // turn that recorded a level) and no block_binding, and records no providerThinkingLevel. The betas still follow the compat flag.
    private static async Task Sync150DisabledThinking()
    {
        foreach (var (id, levels) in new[] { ("claude-opus-5", new[] { "off", "minimal", "low", "medium", "high", "xhigh", "max" }),
            ("claude-haiku-5-5", new[] { "off", "low", "medium", "high", "xhigh", "max" }) })
        {
            var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
            ImmutableArray<TranscriptEntry> context = [UserText("one", 1), Sync150Replayed(id), Ask];
            var disabled = "{\"model\":\"" + id + "\",\"messages\":[{\"role\":\"user\",\"content\":\"one\"},{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"before\"}]}," +
                CachedUser("ask") + "],\"max_tokens\":4096,\"stream\":true,\"thinking\":{\"type\":\"disabled\"}}";
            var endpoint = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
            using (var provider = NativeAnthropic(id, 4096, endpoint))
            {
                Check(ThinkingLevels.GetSupported(provider.Transport, model).SequenceEqual(levels), id + " key-route levels.");
                Equal(null, Level((await new ChatClient(provider.Transport).CompleteAsync(new(model, context, 3) { ThinkingLevel = "off" }).WaitAsync(Deadline)).Message));
                Equal("high", Level((await new ChatClient(provider.Transport).CompleteAsync(new(model, context, 3) { ThinkingLevel = "high" }).WaitAsync(Deadline)).Message));
            }
            BodyEqual(disabled, endpoint.Requests[0].Body);
            Equal(KeyHeaders(MidBetas), endpoint.Requests[0].Headers);
            // Any other level is managed effort as before: the replayed turn's marker, then the active one.
            BodyEqual(MidBody(id, 4096, null, "{\"role\":\"user\",\"content\":\"one\"}", Marker("low"), "{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"before\"}]}",
                CachedUser("ask"), Marker("high")), endpoint.Requests[1].Body);

            var resolvedEndpoint = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
            var (lease, resolvedLevels) = await ResolvedAnthropic(id, 4096, resolvedEndpoint);
            await using (lease)
            {
                Check(resolvedLevels.SequenceEqual(levels), id + " resolved-route levels.");
                Equal(null, Level((await new ChatClient(lease.Transport).CompleteAsync(new(model, context, 3) { ThinkingLevel = "off" }).WaitAsync(Deadline)).Message));
            }
            BodyEqual(disabled, resolvedEndpoint.Requests.Single().Body);

            // Simple: no reasoning level is thinking off.
            var simple = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
            using var client = new HttpClient(simple);
            var factory = new AnthropicMessagesSimpleRequestFactory(client, AnthropicEndpoint, model, new(CatalogRow("anthropic", id), new(4096), null) { ApiKey = Key });
            Equal(null, Level((await new ChatClient(factory).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline)).Message));
            var body = JsonDocument.Parse(simple.Requests.Single().Body).RootElement;
            Equal("""{"type":"disabled"}""", body.GetProperty("thinking").GetRawText());
            Check(!body.TryGetProperty("output_config", out _), id + " Simple sent output_config.");
            Equal("[" + CachedUser("ask") + "]", body.GetProperty("messages").GetRawText());
        }
    }

    // claude-opus-5-5 rejects disabled thinking (thinkingLevelMap.off is null): "off" is not offered, and a request with thinking off still
    // sends managed adaptive thinking with the "high" marker and records it.
    private static async Task Sync150OffUnsupported()
    {
        const string id = "claude-opus-5-5"; var model = new ModelDescriptor(id, "anthropic-messages", "anthropic");
        var endpoint = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
        using (var provider = NativeAnthropic(id, 4096, endpoint))
        {
            Check(ThinkingLevels.GetSupported(provider.Transport, model).SequenceEqual(["low", "medium", "high", "xhigh", "max"]), "opus-5-5 levels.");
            var refused = false;
            try { await foreach (var _ in provider.Transport.StreamAsync(new(model, [Ask], 1) { ThinkingLevel = "off" })) { } }
            catch (ArgumentException) { refused = true; }
            Check(refused, "off was admitted for claude-opus-5-5.");
        }
        Equal(0, endpoint.Requests.Count);
        var simple = new MessagesEndpoint(_ => MessagesStream(id, "ok"));
        using var client = new HttpClient(simple);
        var factory = new AnthropicMessagesSimpleRequestFactory(client, AnthropicEndpoint, model, new(CatalogRow("anthropic", id), new(4096), null) { ApiKey = Key });
        Equal("high", Level((await new ChatClient(factory).CompleteAsync(new(model, [Ask], 1)).WaitAsync(Deadline)).Message));
        BodyEqual(MidBody(id, 128000, null, CachedUser("ask"), Marker("high")), simple.Requests.Single().Body);
    }
}
