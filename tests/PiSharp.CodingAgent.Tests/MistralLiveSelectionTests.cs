using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;

internal static class MistralLiveSelectionTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mistral CLI selects every pinned chat row without credential effects", Catalog),
        ("mistral CLI normal Simple and summary direct routes use actual factory HTTP", Routes),
        ("mistral CLI model limits and missing credentials retain zero-effect refusal", Refusals)
    ];
    private static Task Catalog()
    {
        using var resource = typeof(LiveSessionSelection).Assembly.GetManifestResourceStream("PiSharp.Cli.Models.mistral.json")!;
        using var document = JsonDocument.Parse(resource); var count = 0;
        foreach (var row in document.RootElement.GetProperty("mistral-conversations").EnumerateObject())
        {
            var value = row.Value; var selection = LiveSessionSelection.Parse("mistral", value.GetProperty("id").GetString(), null);
            Check(selection.Model.Api == "mistral-conversations" && selection.Model.Provider == "mistral");
            Check(selection.Definition.Raw.Value.GetProperty("contextWindow").GetDouble() == value.GetProperty("contextWindow").GetDouble());
            foreach (var field in new[] { "input", "output", "cacheRead", "cacheWrite" })
                Check(selection.Definition.Raw.Value.GetProperty("cost").GetProperty(field).GetDouble() == value.GetProperty("cost").GetProperty(field).GetDouble());
            Check(selection.Definition.DeclaresImageInput == value.GetProperty("input").EnumerateArray().Any(item => item.GetString() == "image")); count++;
        }
        Check(count == 32); return Task.CompletedTask;
    }
    private static async Task Routes()
    {
        var selected = LiveSessionSelection.Parse("mistral", "open-mistral-7b", "8000");
        using var handler = new Handler(); var reads = new List<string>();
        using var connection = selected.Connect(new(name => { reads.Add(name); return "fixture-only"; }, () => handler));
        var normal = connection.CreateTransport(); var summary = connection.CreateTransport(summary: true);
        var request = new ChatRequest(selected.Model, [new("user", JsonData.Parse("{\"content\":\"hello\"}"))], 1);
        var normalEvents = await Collect(normal.StreamAsync(request)); var summaryEvents = await Collect(summary.StreamAsync(request));
        Check(normalEvents[^1] is StreamDone && summaryEvents[^1] is StreamDone);
        Check(reads.SequenceEqual(new[] { "MISTRAL_API_KEY" }) && handler.Payloads.Count == 2);
        var first = handler.Payloads[0].Value; var second = handler.Payloads[1].Value;
        Check(first.GetProperty("max_tokens").GetDouble() > 0 && first.GetProperty("max_tokens").GetDouble() <= 3904);
        Check(second.GetProperty("max_tokens").GetDouble() == 8000);
        var costs = selected.Definition.Raw.Value.GetProperty("cost"); var usage = ((StreamDone)normalEvents[^1]).Message.Usage;
        Check(Math.Abs((double)usage.Cost.Input - 10 * costs.GetProperty("input").GetDouble() / 1_000_000) < 1e-12);
        connection.Dispose(); Check(!handler.Disposed);
        var refused = false;
        try { normal.StreamAsync(request); } catch (ObjectDisposedException) { refused = true; }
        Check(refused);
        var reasoning = LiveSessionSelection.Parse("mistral", "magistral-medium-latest", "100");
        using var reasoningConnection = reasoning.Connect(new(_ => "fixture-only", () => handler));
        var reasoningTransport = reasoningConnection.CreateTransport();
        Check(((IThinkingLevelTransport)reasoningTransport).GetSupportedThinkingLevels(reasoning.Model).Contains("high"));
        var reasoningRequest = request with { Model = reasoning.Model, ThinkingLevel = "high" };
        Check((await Collect(reasoningTransport.StreamAsync(reasoningRequest)))[^1] is StreamDone);
        Check(handler.Payloads[^1].Value.GetProperty("prompt_mode").GetString() == "reasoning");
        await ImageRoutes();
    }
    private static async Task ImageRoutes()
    {
        foreach (var (id, supportsImages) in new[] { ("mistral-large-latest", true), ("open-mistral-7b", false) })
        {
            var selected = LiveSessionSelection.Parse("mistral", id, "100");
            Check(selected.Definition.DeclaresImageInput == supportsImages);
            using var handler = new Handler();
            using var connection = selected.Connect(new(_ => "fixture-only", () => handler));
            var messages = new TranscriptEntry[]
            {
                new("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"look\"},{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"AA==\"}],\"timestamp\":1}")),
                new("assistant", JsonData.Parse(JsonSerializer.Serialize(new
                {
                    role = "assistant", provider = selected.Model.Provider, api = selected.Model.Api, model = selected.Model.Id,
                    stopReason = "toolUse", timestamp = 2,
                    usage = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = 0,
                        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 } },
                    content = new[] { new { type = "toolCall", id = "same|raw-ID", name = "lookup", arguments = new Dictionary<string, object>() } }
                }))),
                new("toolResult", JsonData.Parse("{\"role\":\"toolResult\",\"toolCallId\":\"same|raw-ID\",\"toolName\":\"lookup\",\"content\":[{\"type\":\"image\",\"mimeType\":\"image/png\",\"data\":\"AA==\"}],\"isError\":false,\"timestamp\":3}"))
            };
            var before = messages.Select(message => message.WireBody.ToString()).ToArray();
            foreach (var summary in new[] { false, true })
            {
                var events = await Collect(connection.CreateTransport(summary: summary).StreamAsync(new(selected.Model, [.. messages], 4)));
                Check(events[^1] is StreamDone && handler.Payloads.Count == (summary ? 2 : 1));
                var wire = handler.Payloads[^1].Value;
                var blocks = wire.GetProperty("messages").EnumerateArray()
                    .Where(message => message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    .SelectMany(message => message.GetProperty("content").EnumerateArray()).ToArray();
                var images = blocks.Where(block => block.GetProperty("type").GetString() == "image_url").ToArray();
                Check(images.Length == (supportsImages ? 2 : 0));
                foreach (var image in images) Check(image.GetProperty("image_url").GetString() == "data:image/png;base64,AA==");
                if (!supportsImages)
                {
                    var texts = blocks.Where(block => block.GetProperty("type").GetString() == "text")
                        .Select(block => block.GetProperty("text").GetString()).ToArray();
                    Check(texts.Contains("(image omitted: model does not support images)"));
                    Check(texts.Contains("(tool image omitted: model does not support images)"));
                }
                Check(wire.GetProperty("max_tokens").GetDouble() == 100);
                Check(!wire.TryGetProperty("prompt_mode", out _));
                Check(before.SequenceEqual(messages.Select(message => message.WireBody.ToString())));
            }
        }
    }
    private static Task Refusals()
    {
        foreach (var (provider, model, max) in new[] { ("mistral", "missing", "100"), ("mistral", "codestral-latest", "8192"), ("mistral", "open-mistral-7b", "0") })
        {
            var refused = false;
            try { LiveSessionSelection.Parse(provider, model, max); } catch (Exception error) when (error is LiveSessionException or SessionCommandException) { refused = true; }
            Check(refused);
        }
        var handlerCalls = 0; var selection = LiveSessionSelection.Parse("mistral", "codestral-latest", null); var rejected = false;
        try { selection.Connect(new(_ => null, () => { handlerCalls++; return null; })); }
        catch (LiveSessionException error) { rejected = error.Code == "MissingLiveApiKey"; }
        Check(rejected && handlerCalls == 0); return Task.CompletedTask;
    }
    private static async Task<List<StreamEvent>> Collect(IAsyncEnumerable<StreamEvent> stream)
    { var result = new List<StreamEvent>(); await foreach (var item in stream) result.Add(item); return result; }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Mistral live selection control failed."); }
    private sealed class Handler : HttpMessageHandler
    {
        internal List<JsonData> Payloads { get; } = [];
        internal bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.AbsoluteUri == "https://api.mistral.ai/v1/chat/completions");
            Check(request.Headers.Authorization?.Parameter == "fixture-only");
            Payloads.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
