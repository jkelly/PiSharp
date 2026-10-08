using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;

// Owner decision 0004 (budgets follow Pi): a prompt image at Pi's 4.5MB of base64 (image-resize-core.ts DEFAULT_MAX_BYTES) flows
// through the production RPC host (RpcSessionCommand → OfflineSessionProfile → LiveSessionSelection) into each live API family's
// request body and the stream completes. Fake endpoints; authored streams; no network.
internal static partial class Program
{
    /// <summary>Each live API family with the provider preferred for it (null: the first built-in provider that has an image model).</summary>
    private static readonly (string Api, string? Provider)[] ImageFamilies =
    [
        ("openai-completions", null), ("openai-responses", "openai"), ("anthropic-messages", "anthropic"),
        ("google-generative-ai", "google"), ("mistral-conversations", "mistral"), ("pi-messages", null),
    ];

    private static readonly string[] ImageRouteExclusions =
        ["azure", "amazon-bedrock", "openai-codex", "github-copilot", "cloudflare-workers-ai", "cloudflare-ai-gateway", "google-vertex"];

    private static (string Provider, string Model)? ImageModel(string api, string? preferred)
    {
        foreach (var provider in BuiltinProviders.All.Select(item => item.Id).OrderBy(id => id == preferred ? 0 : 1))
        {
            if (ImageRouteExclusions.Contains(provider) || preferred is not null && provider != preferred) continue;
            var model = BuiltinModelCatalog.Get(provider).Models.FirstOrDefault(item => item.Type == CatalogModelType.Chat &&
                item.DeclaredApi == api && item.DeclaresImageInput && LiveSessionSelection.SupportedApi(provider, api));
            if (model is not null) return (provider, model.Id);
        }
        return null;
    }

    private static string FamilyStream(string api, string model)
    {
        static string Data(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        static string Frame(string type, object value) => "event: " + type + "\n" + Data(value);
        return api switch
        {
            "openai-completions" or "mistral-conversations" => Data(new
            {
                id = "c", @object = "chat.completion.chunk", created = 0, model,
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "ok" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
            }) + "data: [DONE]\n\n",
            "openai-responses" =>
                Data(new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "m", content = Array.Empty<object>() } }) +
                Data(new { type = "response.output_text.delta", output_index = 0, item_id = "m", delta = "ok" }) +
                Data(new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "m", content = new[] { new { type = "output_text", text = "ok" } } } }) +
                Data(new { type = "response.completed", response = new { id = "r", status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 5, output_tokens = 3, total_tokens = 8 } } }),
            "anthropic-messages" =>
                Frame("message_start", new { type = "message_start", message = new { id = "m", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } }) +
                Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }) +
                Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "ok" } }) +
                Frame("content_block_stop", new { type = "content_block_stop", index = 0 }) +
                Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 1 } }) +
                Frame("message_stop", new { type = "message_stop" }),
            "google-generative-ai" => Data(new
            {
                candidates = new[] { new { content = new { role = "model", parts = new[] { new { text = "ok" } } }, finishReason = "STOP", index = 0 } },
                usageMetadata = new { promptTokenCount = 1, candidatesTokenCount = 1, totalTokenCount = 2 }
            }),
            "pi-messages" =>
                Data(new { type = "start" }) + Data(new { type = "text_start", contentIndex = 0 }) +
                Data(new { type = "text_delta", contentIndex = 0, delta = "ok" }) + Data(new { type = "text_end", contentIndex = 0, content = "ok" }) +
                Data(new { type = "done", reason = "stop", usage = new { input = 1, output = 1, cacheRead = 0, cacheWrite = 0, totalTokens = 2,
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } } }),
            _ => throw new InvalidOperationException("No authored stream for " + api)
        };
    }

    private static IEnumerable<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Strings),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(property => Strings(property.Value)),
        _ => []
    };

    /// <summary>An RPC prompt carrying a JPEG just under Pi's 4.5MB of base64 reaches every live family's request whole.</summary>
    private static Task LiveImageEveryFamily() => WithLiveRoot("live-image", async root =>
    {
        var jpeg = NoiseJpeg(1900, 1900, 4_400_000, PiPayloadBudget.ImageBase64Characters - 1);
        var data = Convert.ToBase64String(jpeg);
        Check(data.Length is > 4_400_000 and < PiPayloadBudget.ImageBase64Characters, "image base64 size " + data.Length);
        var routed = new List<string>();
        foreach (var (api, preferred) in ImageFamilies)
        {
            var (provider, model) = ImageModel(api, preferred) ?? throw new InvalidOperationException("No built-in image model for " + api);
            var variable = ProviderEnvironmentKeys.GetApiKeyVariables(provider)![^1];
            var endpoint = new LiveEndpoint(seen => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(FamilyStream(api, model), Encoding.UTF8, "text/event-stream") });
            var sessionRoot = Path.Combine(root, provider); Directory.CreateDirectory(sessionRoot);
            await using var rpc = new LiveRpc(LiveArgs(sessionRoot, provider, model), new LiveSessionRuntime(Env((variable, "test-key-" + provider)), () => endpoint));
            try { await rpc.Prompt("image", "describe", [new { type = "image", data, mimeType = "image/jpeg" }]); }
            catch (InvalidOperationException error) { throw new InvalidOperationException($"{api} ({provider}/{model}): {error.Message}", error); }
            Equal(0, await rpc.Finish(), $"{api} exit code; {rpc.Error}");
            var requests = endpoint.Snapshot();
            Check(requests.Length == 1, $"{api} ({provider}/{model}) sent {requests.Length} requests; stops {string.Join(",", rpc.StopReasons())}");
            var body = requests[0].Body!;
            // The whole image as one decoded JSON string (raw base64 or a data URL), whatever the body's escaping.
            using (var parsed = JsonDocument.Parse(body))
                Check(Strings(parsed.RootElement).Any(text => text.EndsWith(data, StringComparison.Ordinal)), $"{api} request lacks the whole image");
            var stops = rpc.StopReasons();
            Check(stops.Length == 1 && stops[0] == "stop:", $"{api} ({provider}/{model}) stream did not complete: {string.Join(",", stops)}");
            routed.Add($"{api}: {provider}/{model} {requests[0].Url} body {Encoding.UTF8.GetByteCount(body)} bytes");
        }
        foreach (var route in routed) Console.WriteLine("  " + route);
    });
}
