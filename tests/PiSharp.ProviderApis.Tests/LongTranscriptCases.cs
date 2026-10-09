using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;

// Long sessions: Pi v1.1.0 caps no transcript message count (packages/ai/src/api/*.ts convert every message; the request is
// bounded by the model's context, which compaction maintains). A transcript of about 2,000 messages, including 260 loadout
// system records from 130 MCP enable/disable cycles, goes through each provider family's production live route
// (LiveSessionSelection) whole. Authored expectations; every HTTP peer is an in-process fake.
internal static partial class Program
{
    private const int LongTurns = 435, LoadoutCycles = 130;
    private static readonly string[] McpTools = ["mcp_docs_search", "mcp_docs_fetch", "mcp_docs_list", "mcp_docs_index", "mcp_docs_open", "mcp_docs_close"];

    private static string Declaration(string name) =>
        JsonSerializer.Serialize(new { name, description = "Tool " + name, parameters = new { type = "object", properties = new { path = new { type = "string" } }, required = new[] { "path" } } });

    /// <summary>
    /// An initial system message, then per turn: a loadout record for the first 260 turns (MCP tools enabled, then disabled,
    /// 130 times), a user message, an assistant tool call, its result and an assistant answer. Markers name every message.
    /// </summary>
    private static (ImmutableArray<TranscriptEntry> Messages, string[] Markers) LongTranscript()
    {
        var messages = ImmutableArray.CreateBuilder<TranscriptEntry>(); var markers = new List<string>(); long time = 1;
        string[] active = ["read"];
        messages.Add(Entry("""{"role":"system","content":"Long-session base prompt","toolsAdded":[""" + Declaration("read") + "],\"timestamp\":0}"));
        const string Usage = "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}";
        const string Identity = "\"api\":\"fixture-api\",\"provider\":\"fixture\",\"model\":\"fixture-model\"";
        for (var turn = 0; turn < LongTurns; turn++)
        {
            if (turn < 2 * LoadoutCycles)
            {
                string[] next = turn % 2 == 0 ? ["read", .. McpTools] : ["read"];
                messages.Add(Entry("{\"role\":\"system\",\"content\":\"\",\"toolsRemoved\":[" + string.Join(",", active.Select(name => JsonSerializer.Serialize(new { name }))) +
                    "],\"toolsAdded\":[" + string.Join(",", next.Select(Declaration)) + "],\"timestamp\":" + time++ + "}"));
                active = next;
            }
            var id = turn.ToString("D4");
            messages.Add(Entry("{\"role\":\"user\",\"content\":\"u" + id + "x question\",\"timestamp\":" + time++ + "}"));
            messages.Add(Entry("{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call_" + id + "\",\"name\":\"read\",\"arguments\":{\"path\":\"p" + id + "x\"}}]," +
                Identity + "," + Usage + ",\"stopReason\":\"toolUse\",\"timestamp\":" + time++ + "}"));
            messages.Add(Entry("{\"role\":\"toolResult\",\"toolCallId\":\"call_" + id + "\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"r" + id +
                "x contents\"}],\"isError\":false,\"timestamp\":" + time++ + "}"));
            messages.Add(Entry("{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"a" + id + "x answer\"}]," + Identity + "," + Usage +
                ",\"stopReason\":\"stop\",\"timestamp\":" + time++ + "}"));
            markers.AddRange(["u" + id + "x", "p" + id + "x", "r" + id + "x", "a" + id + "x"]);
        }
        messages.Add(Entry("{\"role\":\"user\",\"content\":\"final question\",\"timestamp\":" + time + "}"));
        return (messages.ToImmutable(), [.. markers]);
    }

    private static IEnumerable<string> JsonStrings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(JsonStrings),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => JsonStrings(property.Value)),
        _ => []
    };

    /// <summary>The first chat model of a provider that declares the API and has a live route.</summary>
    private static string LongModel(string provider, string api) =>
        BuiltinModelCatalog.Get(provider).Models.First(model => model.Type == CatalogModelType.Chat && model.DeclaredApi == api &&
            LiveSessionSelection.SupportedApi(provider, api)).Id;

    private static string CompletionsProvider() => BuiltinProviders.All.Select(item => item.Id)
        .Where(id => !LiveProviderRoute.Handles(id) && id is not ("azure" or "mistral" or "anthropic") && ProviderEnvironmentKeys.GetApiKeyVariables(id) is { Count: > 0 })
        .First(id => BuiltinModelCatalog.Get(id).Models.Any(model => model.Type == CatalogModelType.Chat && model.DeclaredApi == "openai-completions"));

    private static async Task LongTranscriptEveryFamily()
    {
        var (messages, markers) = LongTranscript();
        Check(messages.Length is > 1_950 and < 2_100 && messages.Count(message => message.Role == "system") == 2 * LoadoutCycles + 1,
            "long transcript shape " + messages.Length);
        var directory = Temp("long-transcript"); var authPath = Path.Combine(directory, "auth.json"); var far = 4_000_000_000_000L;
        await File.WriteAllTextAsync(authPath, $$$"""
            {"openai-codex":{"type":"oauth","refresh":"cr","access":"{{{CodexToken}}}","expires":{{{far}}},"accountId":"acct-123"},
             "github-copilot":{"type":"oauth","refresh":"gh","access":"{{{CopilotToken}}}","expires":{{{far}}},"availableModelIds":["kimi-k3","claude-sonnet-4.6"]},
             "cloudflare-ai-gateway":{"type":"api_key","key":"cf-key","env":{"CLOUDFLARE_ACCOUNT_ID":"acct","CLOUDFLARE_GATEWAY_ID":"gw"}} }
            """);
        var http = new FakeHttp();
        http.OnUrl("https://", _ => Json("""{"error":{"message":"fake peer refuses"}}""", HttpStatusCode.BadRequest));
        var completions = CompletionsProvider();
        var env = new Dictionary<string, string?>
        {
            ["ANTHROPIC_API_KEY"] = "anthropic-key", ["OPENAI_API_KEY"] = "openai-key", ["MISTRAL_API_KEY"] = "mistral-key", ["RADIUS_API_KEY"] = "radius-key",
            [ProviderEnvironmentKeys.GetApiKeyVariables("google")![^1]] = "google-key", [ProviderEnvironmentKeys.GetApiKeyVariables(completions)![^1]] = "completions-key",
            ["AZURE_OPENAI_API_KEY"] = "azure-key", ["AZURE_OPENAI_RESOURCE_NAME"] = "pisharp-res", ["GOOGLE_CLOUD_API_KEY"] = "vertex-key",
            ["AWS_ACCESS_KEY_ID"] = "AKIDLONG", ["AWS_SECRET_ACCESS_KEY"] = "s", ["CLOUDFLARE_API_KEY"] = "cf-env-key", ["CLOUDFLARE_ACCOUNT_ID"] = "cf-acct",
        };
        var runtime = new LiveSessionRuntime(name => env.GetValueOrDefault(name), () => http, authPath, () => new HttpMessageInvoker(http, disposeHandler: false),
            new FixedTime(1_800_000_000_000)) { HomeDirectory = directory };
        var routes = new (string Family, string Provider, string Model)[]
        {
            ("anthropic-messages", "anthropic", LongModel("anthropic", "anthropic-messages")),
            ("openai-responses", "openai", LongModel("openai", "openai-responses")),
            ("openai-completions", completions, LongModel(completions, "openai-completions")),
            ("azure-openai-responses", "azure", "gpt-5.4"), ("azure-openai-completions", "azure", "deepseek-v4-pro"),
            ("google-generative-ai", "google", LongModel("google", "google-generative-ai")),
            ("google-vertex", "google-vertex", "gemini-2.5-flash"),
            ("mistral-conversations", "mistral", LongModel("mistral", "mistral-conversations")),
            ("pi-messages", "radius", LongModel("radius", "pi-messages")),
            ("bedrock-converse-stream", "amazon-bedrock", "amazon.nova-lite-v1:0"),
            ("openai-codex-responses", "openai-codex", "gpt-5.5"),
            ("github-copilot-completions", "github-copilot", "kimi-k3"), ("github-copilot-anthropic", "github-copilot", "claude-sonnet-4.6"),
            ("cloudflare-workers-ai", "cloudflare-workers-ai", "@cf/deepseek-ai/deepseek-v4-flash-0731"),
            ("cloudflare-ai-gateway", "cloudflare-ai-gateway", "claude-fable-5"),
        };
        var report = new List<string>();
        foreach (var (family, provider, model) in routes)
        {
            var selection = LiveSessionSelection.Parse(provider, model, "512");
            var before = http.All.Count;
            await using var connection = selection.Connect(runtime);
            var events = await Collect(connection.CreateTransport(), new(selection.Model, messages, 1));
            var sent = http.All.Skip(before).ToArray();
            Check(sent.Length == 1, $"{family} ({provider}/{model}) sent {sent.Length} requests: {ErrorMessage(events[^1])}");
            // The fake peer refuses every request; reaching it means the projection admitted the whole transcript.
            Check(events[^1] is StreamError, $"{family} completed against a refusing peer");
            string text;
            using (var body = JsonDocument.Parse(sent[0].Body)) text = string.Join("\n", JsonStrings(body.RootElement));
            var missing = markers.Where(marker => !text.Contains(marker, StringComparison.Ordinal)).ToArray();
            Check(missing.Length == 0, $"{family} request lacks {missing.Length} of {markers.Length} message markers, first {missing.FirstOrDefault()}");
            Check(text.Contains("final question", StringComparison.Ordinal) && text.Contains("read", StringComparison.Ordinal), family + " request lacks the last prompt or tool");
            report.Add($"{family}: {provider}/{model} {messages.Length} messages, body {sent[0].BodyBytes.Length} bytes");
        }
        foreach (var line in report) Console.Error.WriteLine("  " + line);
    }
}
