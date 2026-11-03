using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;

// The production live route over the @earendil-works/pi-ai@1.5.0 catalog shards, authored from Pi v1.5.0
// (f9a26365568127461c47a07a37dcb42c140a3ac5) by reading: packages/ai/src/providers/kimi-coding.ts (openai-completions at /coding/v1),
// providers/openrouter.ts (anthropic-messages for the openrouter.json Anthropic rows),
// providers/opencode-go.ts (google-generative-ai, wrapped by withOpenCodeSessionHeader), api/anthropic-messages.ts (buildParams
// thinkingDisabled/effortMessages, dc939913f) and the minimax.json effort map. Fake endpoints only; nothing is an upstream capture.
internal static partial class Program
{
    private static (string Id, Func<Task> Run)[] Sync150CatalogCases() =>
    [
        ("sync150.kimi-coding-live-route-speaks-chat-completions", Sync150KimiCodingLive),
        ("sync150.opencode-go-google-route-carries-x-opencode-session", Sync150OpenCodeGoGoogleLive),
        ("sync150.minimax-m3.1-flash-sends-output-config-effort", Sync150MiniMaxFlashLive),
        ("sync150.claude-opus-5-off-sends-disabled-thinking-live", Sync150OpusOffLive),
        ("sync150.openrouter-anthropic-latest-alias-speaks-messages", Sync150OpenRouterLatestLive),
    ];

    private static HttpResponseMessage Sync150Capture() => new(HttpStatusCode.BadRequest)
    { Content = new StringContent("""{"error":{"message":"capture","type":"invalid_request_error"}}""", Encoding.UTF8, "application/json") };

    private static async Task<Seen[]> Sync150Prompt(string root, string name, string provider, string model, LiveSessionRuntime runtime,
        Func<LiveRpc, Task>? before = null)
    {
        var workspace = Path.Combine(root, name); Directory.CreateDirectory(workspace);
        var endpoint = (LiveEndpoint)runtime.CreateHttpHandler()!;
        await using var rpc = new LiveRpc(LiveArgs(workspace, provider, model), runtime);
        if (before is not null) await before(rpc);
        await rpc.Prompt("p", "hello");
        await rpc.Finish();
        var requests = endpoint.Snapshot();
        Check(requests.Length > 0, provider + "/" + model + " sent no request; " + rpc.Error);
        return requests;
    }

    // kimi-coding is an openai-completions provider at https://api.kimi.com/coding/v1, with the API key or the Kimi Code OAuth token as a
    // Bearer header; the K3 compat sends max_tokens and no developer role.
    private static Task Sync150KimiCodingLive() => WithLiveRoot("live-kimi-coding", async root =>
    {
        var keyEndpoint = new LiveEndpoint(_ => Sync150Capture());
        var key = await Sync150Prompt(root, "key", "kimi-coding", "k3.1-pro", new(Env(("KIMI_API_KEY", "kimi-key")), () => keyEndpoint));
        Equal("https://api.kimi.com/coding/v1/chat/completions", key[0].Url, "kimi-coding URL");
        Equal("Bearer kimi-key", key[0].Headers["Authorization"], "API key");
        Check(!key[0].Headers.ContainsKey("x-api-key"), "no Anthropic key header");
        var body = JsonDocument.Parse(key[0].Body!).RootElement;
        Equal("k3.1-pro", body.GetProperty("model").GetString(), "model");
        Check(body.TryGetProperty("max_tokens", out _) && !body.TryGetProperty("max_completion_tokens", out _), "maxTokensField max_tokens");
        Check(body.GetProperty("messages").EnumerateArray().All(message => message.GetProperty("role").GetString() != "developer"), "no developer role");

        var authPath = Path.Combine(root, "agent", "auth.json"); Directory.CreateDirectory(Path.GetDirectoryName(authPath)!);
        await File.WriteAllTextAsync(authPath, """{"kimi-coding":{"type":"oauth","access":"kimi-access","refresh":"kimi-refresh","expires":9999999999999}}""");
        var oauthEndpoint = new LiveEndpoint(_ => Sync150Capture());
        var oauth = await Sync150Prompt(root, "oauth", "kimi-coding", "kimi-for-coding", new(Env(), () => oauthEndpoint, authPath));
        Equal("https://api.kimi.com/coding/v1/chat/completions", oauth[0].Url, "OAuth URL");
        Equal("Bearer kimi-access", oauth[0].Headers["Authorization"], "Kimi Code OAuth owns the Authorization header");
        Check(!oauth[0].Headers.ContainsKey("x-api-key"), "no Anthropic key header with OAuth");
    });

    // opencode-go registers google-generative-ai: Gemini models reach /inference/go/google/v1beta with x-opencode-session.
    private static Task Sync150OpenCodeGoGoogleLive() => WithLiveRoot("live-opencode-go-google", async root =>
    {
        var endpoint = new LiveEndpoint(_ => Sync150Capture());
        string? sessionId = null;
        var requests = await Sync150Prompt(root, "go", "opencode-go", "gemini-4-argon", new(Env(("OPENCODE_API_KEY", "opencode-key")), () => endpoint),
            async rpc => sessionId = (await rpc.Command("state", new { type = "get_state" })).GetProperty("sessionId").GetString());
        Check(requests[0].Url.StartsWith("https://opencode.ai/inference/go/google/v1beta/models/gemini-4-argon:", StringComparison.Ordinal), "Google route URL " + requests[0].Url);
        foreach (var request in requests)
            Equal(sessionId, request.Headers.TryGetValue("x-opencode-session", out var value) ? value : null, "x-opencode-session");
    });

    // MiniMax-M3.1-Flash: forceAdaptiveThinking with the effort map low..max (off null), so the level goes out as output_config.effort.
    private static Task Sync150MiniMaxFlashLive() => WithLiveRoot("live-minimax-flash", async root =>
    {
        const string id = "MiniMax-M3.1-Flash";
        var endpoint = new LiveEndpoint(_ => ManagedStream(id));
        var workspace = Path.Combine(root, "minimax"); Directory.CreateDirectory(workspace);
        await using var rpc = new LiveRpc(LiveArgs(workspace, "minimax", id), new(Env(("MINIMAX_API_KEY", "minimax-key")), () => endpoint));
        Equal("""["low","medium","high","xhigh","max"]""", (await rpc.Command("levels", new { type = "get_available_thinking_levels" })).GetProperty("levels").GetRawText(), "levels");
        await rpc.Command("off", new { type = "set_thinking_level", level = "off" });
        Equal("low", (await rpc.Command("state", new { type = "get_state" })).GetProperty("thinkingLevel").GetString(), "off clamps to low");
        await rpc.Command("xhigh", new { type = "set_thinking_level", level = "xhigh" });
        await rpc.Prompt("p", "hello");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        var sent = endpoint.Snapshot().Single();
        Check(sent.Url.StartsWith("https://api.minimax.io/anthropic/v1/messages", StringComparison.Ordinal), "MiniMax URL " + sent.Url);
        var body = JsonDocument.Parse(sent.Body!).RootElement;
        Equal("""{"type":"adaptive","display":"summarized"}""", body.GetProperty("thinking").GetRawText(), "adaptive thinking");
        Equal("""{"effort":"xhigh"}""", body.GetProperty("output_config").GetRawText(), "effort");
    });

    // ~anthropic/claude-haiku-latest is an anthropic-messages row at https://openrouter.ai/api in the 1.5.0 openrouter.json; its map's
    // "off": "none" is never sent as an effort, so the route opens and a level goes out as adaptive thinking with output_config.effort.
    private static Task Sync150OpenRouterLatestLive() => WithLiveRoot("live-openrouter-latest", async root =>
    {
        var endpoint = new LiveEndpoint(_ => Sync150Capture());
        var requests = await Sync150Prompt(root, "router", "openrouter", "~anthropic/claude-haiku-latest", new(Env(("OPENROUTER_API_KEY", "router-key")), () => endpoint),
            async rpc => await rpc.Command("high", new { type = "set_thinking_level", level = "high" }));
        Equal("https://openrouter.ai/api/v1/messages?beta=true", requests[0].Url, "Messages URL");
        var body = JsonDocument.Parse(requests[0].Body!).RootElement;
        Equal("~anthropic/claude-haiku-latest", body.GetProperty("model").GetString(), "model");
        Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString(), "adaptive thinking");
        Equal("""{"effort":"high"}""", body.GetProperty("output_config").GetRawText(), "effort");
    });

    // claude-opus-5 offers off at pi-ai 1.5.0: "off" sends disabled thinking without effort or markers and records no level, the summary
    // route (thinking off) does the same, and a later level is managed effort again.
    private static Task Sync150OpusOffLive() => WithLiveRoot("live-opus-5-off", async root =>
    {
        const string id = "claude-opus-5";
        var provider = new LiveEndpoint(seen => seen.Url == MessagesUrl ? ManagedStream(id) : throw new InvalidOperationException("Unexpected URL " + seen.Url));
        var workspace = Path.Combine(root, "opus"); Directory.CreateDirectory(workspace);
        await using var rpc = new LiveRpc(LiveArgs(workspace, "anthropic", id), new(Env(("ANTHROPIC_API_KEY", "env-key")), () => provider));
        Equal("""["off","minimal","low","medium","high","xhigh","max"]""", (await rpc.Command("levels", new { type = "get_available_thinking_levels" })).GetProperty("levels").GetRawText(), "levels");
        await rpc.Command("off", new { type = "set_thinking_level", level = "off" });
        Equal("off", (await rpc.Command("state", new { type = "get_state" })).GetProperty("thinkingLevel").GetString(), "off kept");
        await rpc.Prompt("p-one", "one");
        await rpc.Command("medium", new { type = "set_thinking_level", level = "medium" });
        await rpc.Prompt("p-two", "two");
        Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
        var requests = provider.Snapshot();
        Equal(2, requests.Length, "two requests");
        var off = JsonDocument.Parse(requests[0].Body!).RootElement;
        Equal("""{"type":"disabled"}""", off.GetProperty("thinking").GetRawText(), "disabled thinking");
        Check(!off.TryGetProperty("output_config", out _), "no output_config");
        Check(off.GetProperty("messages").EnumerateArray().All(message => message.GetProperty("role").GetString() != "system"), "no effort markers");
        var managed = JsonDocument.Parse(requests[1].Body!).RootElement;
        Equal("""{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}}""", managed.GetProperty("thinking").GetRawText(), "managed again");
        // The off turn recorded no level, so only the active marker is sent.
        Equal(1, managed.GetProperty("messages").EnumerateArray().Count(message => message.GetProperty("role").GetString() == "system"), "one marker");
        var levels = rpc.Events.Where(record => record.Value.GetProperty("type").GetString() == "message_end" &&
                record.Value.GetProperty("message").GetProperty("role").GetString() == "assistant")
            .Select(record => record.Value.GetProperty("message").TryGetProperty("providerThinkingLevel", out var level) ? level.GetString() : null).ToArray();
        Check(levels.SequenceEqual(new string?[] { null, "medium" }), "providerThinkingLevel: " + string.Join(",", levels));

        var endpoint = new LiveEndpoint(_ => ManagedStream(id));
        var selection = LiveSessionSelection.Parse("anthropic", id, null);
        using var connection = selection.Connect(new(Env(("ANTHROPIC_API_KEY", "env-key")), () => endpoint));
        StreamEvent? last = null;
        await foreach (var frame in connection.CreateTransport(summary: true).StreamAsync(new(selection.Model,
            [new("user", JsonData.Parse("""{"role":"user","content":"summarize","timestamp":1}"""))], 1) { ThinkingLevel = "off" }))
            last = frame;
        Check(last is StreamDone { Message.ExtraProperties: var extra } && extra?.TryGet("providerThinkingLevel", out _) != true, "summary records no level");
        Equal("{\"model\":\"" + id + "\",\"messages\":[{\"role\":\"user\",\"content\":\"summarize\"}],\"max_tokens\":" + connection.MaximumOutputTokens +
            ",\"stream\":true,\"thinking\":{\"type\":\"disabled\"}}", endpoint.Snapshot().Single().Body, "summary body");
    });
}
