using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;

// Managed per-turn effort on the production live route (RpcSessionCommand → LiveSessionSelection → NativeProviderFactory) against a
// fake endpoint. Upstream (read, not captured): packages/ai/src/api/anthropic-messages.ts (buildParams, getBetaFeatures,
// insertThinkingLevelMessages, providerThinkingLevel) and packages/ai/src/models.ts (getSupportedThinkingLevels, clampThinkingLevel).
internal static partial class Program
{
    private static HttpResponseMessage ManagedStream(string model)
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        var stream = Frame("message_start", new { type = "message_start", message = new { id = "authored", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } })
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "", signature = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "plan" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "sig" } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "text", text = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = "ok" } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 1 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 1 } })
            + Frame("message_stop", new { type = "message_stop" });
        return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
    }

    // claude-sonnet-5-5 (thinkingLevelMap off/minimal null, compat.supportsMidConvoEffort): the live session admits the model with
    // upstream's levels, clamps "off" up to "low", and every request carries managed adaptive thinking, both betas and one effort
    // marker per recorded assistant turn plus the active one; each response records its providerThinkingLevel.
    private static Task LiveManagedEffortLevels() => WithLiveRoot("live-managed-effort", async root =>
    {
        const string id = "claude-sonnet-5-5";
        var provider = new LiveEndpoint(seen => seen.Url == MessagesUrl ? ManagedStream(id) : throw new InvalidOperationException("Unexpected URL " + seen.Url));
        await using var rpc = new LiveRpc(LiveArgs(root, "anthropic", id), new(Env(("ANTHROPIC_API_KEY", "env-key")), () => provider));
        Equal("""["low","medium","high","xhigh","max"]""", (await rpc.Command("levels", new { type = "get_available_thinking_levels" })).GetProperty("levels").GetRawText(), "levels");
        await rpc.Command("off", new { type = "set_thinking_level", level = "off" });
        Equal("low", (await rpc.Command("state-off", new { type = "get_state" })).GetProperty("thinkingLevel").GetString(), "off clamps to low");
        await rpc.Prompt("p-one", "one");
        await rpc.Command("max", new { type = "set_thinking_level", level = "max" });
        await rpc.Prompt("p-two", "two");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);

        var requests = provider.Snapshot();
        Equal(2, requests.Length, "two requests");
        static string Marker(string effort) => "{\"role\":\"system\",\"content\":[],\"output_config\":{\"effort\":\"" + effort + "\"}}";
        const string cached = ",\"cache_control\":{\"type\":\"ephemeral\"}";
        var expected = new[]
        {
            "[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"" + cached + "}]}," + Marker("low") + "]",
            "[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"}]}," + Marker("low") +
                ",{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"plan\",\"signature\":\"sig\"},{\"type\":\"text\",\"text\":\"ok\"}]}," +
                "{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"two\"" + cached + "}]}," + Marker("max") + "]"
        };
        for (var index = 0; index < 2; index++)
        {
            var body = JsonDocument.Parse(requests[index].Body!).RootElement;
            Equal(expected[index], body.GetProperty("messages").GetRawText(), "messages " + index);
            Equal("""{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}}""", body.GetProperty("thinking").GetRawText(), "thinking " + index);
            Equal("""{"effort":"high"}""", body.GetProperty("output_config").GetRawText(), "output_config " + index);
            Check(!body.TryGetProperty("temperature", out _), "temperature sent");
            Equal("mid-conversation-output-config-2026-07-01,thinking-binding-controls-2026-08-01", requests[index].Headers["anthropic-beta"], "betas " + index);
        }
        var levels = rpc.Events.Where(record => record.Value.GetProperty("type").GetString() == "message_end" &&
                record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant")
            .Select(record => record.Value.GetProperty("message").GetProperty("providerThinkingLevel").GetString()).ToArray();
        Names(["low", "max"], levels!, "providerThinkingLevel");

        // The summary route carries no level metadata and runs thinking off: a managed model still sends managed "high" effort
        // (buildParams ignores thinkingEnabled for it), an unmanaged model keeps its disabled thinking, and an unmanaged model whose
        // thinkingLevelMap.off is null (claude-fable-5) sends no thinking field at all.
        const string managed = """{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}}""";
        foreach (var (model, thinking, effort) in new[] { (id, managed, "high"), ("claude-sonnet-4-5", """{"type":"disabled"}""", (string?)null),
            ("claude-fable-5", (string?)null, (string?)null) })
        {
            var endpoint = new LiveEndpoint(_ => ManagedStream(model));
            var selection = LiveSessionSelection.Parse("anthropic", model, null);
            using var connection = selection.Connect(new(Env(("ANTHROPIC_API_KEY", "env-key")), () => endpoint));
            var summary = connection.CreateTransport(summary: true);
            Names(["off"], ThinkingLevels.GetSupported(summary, selection.Model), model + " summary levels");
            StreamEvent? last = null;
            await foreach (var frame in summary.StreamAsync(new(selection.Model, [new("user", JsonData.Parse("""{"role":"user","content":"summarize","timestamp":1}"""))], 1) { ThinkingLevel = "off" }))
                last = frame;
            var recorded = last is StreamDone done && done.Message.ExtraProperties?.TryGet("providerThinkingLevel", out var value) == true ? value!.Value.GetString() : null;
            Check(last is StreamDone, model + " summary terminal");
            Equal(effort, recorded, model + " summary providerThinkingLevel");
            var body = JsonDocument.Parse(endpoint.Snapshot().Single().Body!).RootElement;
            Equal(thinking, body.TryGetProperty("thinking", out var sent) ? sent.GetRawText() : null, model + " summary thinking");
            Equal(effort is null ? null : "[{\"role\":\"user\",\"content\":\"summarize\"}," + Marker(effort) + "]",
                effort is null ? null : body.GetProperty("messages").GetRawText(), model + " summary messages");
            // buildParams: compat.allowedFallbackModels (claude-fable-5) become fallbacks on every request, summaries included.
            if (effort is null)
                Equal("{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"summarize\"}],\"max_tokens\":" + connection.MaximumOutputTokens + ",\"stream\":true" +
                    (thinking is null ? "" : ",\"thinking\":" + thinking) +
                    (model == "claude-fable-5" ? ",\"fallbacks\":[{\"model\":\"claude-opus-4-8\"},{\"model\":\"claude-opus-5\"}]" : "") + "}",
                    endpoint.Snapshot().Single().Body, model + " summary body");
        }
    });
}
