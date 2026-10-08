// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/simple-options.ts and packages/ai/src/utils/estimate.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.MistralConversations;

public sealed record MistralSimpleResolution(double ContextTokens, double MaxTokens);

/// <summary>Pure Simple resolution followed by the existing direct HTTP/SSE consumer. Borrows the client and immutable complete model row.</summary>
public sealed class MistralSimpleHttpSseTransport : IChatTransport, IThinkingLevelTransport
{
    // Pi 1.1.0 estimate.ts CHARS_PER_TOKEN; session compaction keeps its own four-character estimate.
    internal const double CharsPerToken = 3.5;
    private readonly HttpClient _client;
    private readonly ModelDescriptor _model;
    private readonly MistralTextOptions _options;
    private readonly double _contextWindow, _modelMaxTokens;
    private readonly MistralTextHttpSseTransport _capabilities;
    public JsonData ModelMetadata { get; }

    public MistralSimpleHttpSseTransport(HttpClient client, ModelDescriptor model, JsonData modelMetadata, MistralTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(client); ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(modelMetadata); ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        try
        {
            if (Encoding.UTF8.GetByteCount(modelMetadata.ToString()) > options.MaximumPayloadBytes) throw Limit();
            using var admitted = JsonDocument.Parse(modelMetadata.ToString(), new JsonDocumentOptions { MaxDepth = options.MaximumJsonDepth });
            var row = admitted.RootElement;
            if (row.ValueKind != JsonValueKind.Object || row.GetProperty("id").GetString() != model.Id ||
                row.GetProperty("provider").GetString() != model.Provider || row.GetProperty("api").GetString() != model.Api)
                throw Invalid();
            _contextWindow = Number(row, "contextWindow"); _modelMaxTokens = Number(row, "maxTokens");
            var reasoning = row.GetProperty("reasoning").GetBoolean();
            var images = false;
            if (row.TryGetProperty("input", out var input))
            {
                if (input.ValueKind != JsonValueKind.Array ||
                    input.EnumerateArray().Any(part => part.ValueKind != JsonValueKind.String || part.GetString() is not ("text" or "image")))
                    throw Invalid();
                images = input.EnumerateArray().Any(part => part.GetString() == "image");
            }
            ImmutableDictionary<string, string?>? map = null;
            if (reasoning && row.TryGetProperty("thinkingLevelMap", out var value) && value.ValueKind != JsonValueKind.Null)
                map = value.EnumerateObject().ToImmutableDictionary(pair => pair.Name,
                    pair => pair.Value.ValueKind == JsonValueKind.Null ? null : pair.Value.GetString(), StringComparer.Ordinal);
            ImmutableDictionary<string, string?>? headers = null;
            if (row.TryGetProperty("headers", out var declaredHeaders) && declaredHeaders.ValueKind != JsonValueKind.Null)
                headers = declaredHeaders.EnumerateObject().ToImmutableDictionary(pair => pair.Name,
                    pair => pair.Value.ValueKind == JsonValueKind.Null ? null : pair.Value.GetString(), StringComparer.Ordinal);
            var midSystem = row.TryGetProperty("compat", out var compat) && compat.ValueKind != JsonValueKind.Null &&
                compat.TryGetProperty("supportsMidConvoSystemMessages", out var mid) && mid.ValueKind != JsonValueKind.Null && mid.GetBoolean();
            _options = options with { Reasoning = reasoning, ThinkingLevelMap = map, ModelHeaders = headers,
                SupportsImages = images, SupportsMidConversationSystemMessages = midSystem, PromptMode = null, ReasoningEffort = null };
            _capabilities = new(client, model, _options); _client = client; _model = model; ModelMetadata = modelMetadata;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { throw Invalid(); }
    }

    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) => _capabilities.GetSupportedThinkingLevels(model);
    public MistralSimpleResolution Resolve(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model != _model || request.Messages.IsDefault) throw Invalid();
        var estimate = Estimate(request.Messages);
        var maximum = _options.MaxTokens ?? _modelMaxTokens;
        maximum = _contextWindow <= 0 ? Math.Max(1, maximum) : Math.Min(maximum, Math.Max(1, _contextWindow - estimate - 4096));
        return new(estimate, maximum);
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        // Original Simple refuses the missing key before base options or payload effects.
        if (string.IsNullOrEmpty(_options.ApiKey)) throw Invalid();
        cancellationToken.ThrowIfCancellationRequested(); var resolved = Resolve(request);
        return new MistralTextHttpSseTransport(_client, _model, _options with { MaxTokens = resolved.MaxTokens })
            .StreamAsync(request, cancellationToken);
    }
    private double Estimate(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.Length > 256) throw Limit();
        try
        {
            double latest = double.NegativeInfinity, usage = 0; int? last = null; long characters = 0;
            for (var i = 0; i < messages.Length; i++)
            {
                var entry = messages[i]; var body = entry.WireBody.Value;
                characters += body.GetRawText().Length; if (characters > _options.MaximumContentCharacters) throw Limit();
                var timestamp = body.TryGetProperty("timestamp", out var field) ? Number(field) : double.NaN;
                if (entry.Role == "assistant")
                {
                    var totals = body.GetProperty("usage"); var tokens = Number(totals, "totalTokens");
                    if (tokens == 0) tokens = Number(totals, "input") + Number(totals, "output") + Number(totals, "cacheRead") + Number(totals, "cacheWrite");
                    if (timestamp >= latest && body.GetProperty("stopReason").GetString() is not ("error" or "aborted") && tokens > 0)
                    { last = i; usage = tokens; }
                }
                latest = Math.Max(latest, timestamp);
            }
            for (var i = last is { } index ? index + 1 : 0; i < messages.Length; i++) usage += MessageTokens(messages[i]);
            if (!double.IsFinite(usage)) throw Limit(); return usage;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { throw Invalid(); }
        catch (EcmaScriptJsonProjectionException error)
        { throw error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit ? Limit() : Invalid(); }
    }
    private double MessageTokens(TranscriptEntry entry)
    {
        var body = entry.WireBody.Value; var content = body.GetProperty("content");
        if (entry.Role == "system")
        {
            var parts = new List<string> { content.ValueKind == JsonValueKind.String ? content.GetString()! :
                string.Join("\n", content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text").Select(block => block.GetProperty("text").GetString()!)) };
            if (body.TryGetProperty("sections", out var sections) && sections.ValueKind != JsonValueKind.Null)
                foreach (var section in sections.EnumerateObject()) if (section.Value.ValueKind != JsonValueKind.Null) parts.Add(section.Value.GetString()!);
            var tokens = Math.Ceiling(string.Join("\n\n", parts.Where(part => part.Length > 0)).Length / CharsPerToken);
            foreach (var name in new[] { "toolsAdded", "toolsRemoved" })
                if (body.TryGetProperty(name, out var tools) && tools.ValueKind != JsonValueKind.Null && tools.GetArrayLength() > 0)
                    tokens += Math.Ceiling(JsonText(tools).Length / CharsPerToken);
            return tokens;
        }
        if (content.ValueKind == JsonValueKind.String && entry.Role is "user" or "toolResult") return Math.Ceiling(content.GetString()!.Length / CharsPerToken);
        double characters = 0;
        foreach (var block in content.EnumerateArray()) characters += block.GetProperty("type").GetString() switch
        {
            "text" => block.GetProperty("text").GetString()!.Length,
            "thinking" when entry.Role == "assistant" => block.GetProperty("thinking").GetString()!.Length,
            "toolCall" when entry.Role == "assistant" => block.GetProperty("name").GetString()!.Length + JsonText(block.GetProperty("arguments")).Length,
            "image" when entry.Role is "user" or "toolResult" => 4800,
            _ => throw Invalid()
        };
        return Math.Ceiling(characters / CharsPerToken);
    }
    private string JsonText(JsonElement value) => EcmaScriptJsonProjection.Project(JsonData.FromElement(value), new(
        MaximumInputCharacters: _options.MaximumContentCharacters, MaximumInputBytes: _options.MaximumContentCharacters * 4,
        MaximumOutputCharacters: _options.MaximumContentCharacters, MaximumOutputBytes: _options.MaximumContentCharacters * 4,
        MaximumDepth: _options.MaximumJsonDepth, MaximumStringCharacters: _options.MaximumContentCharacters));
    private static double Number(JsonElement body, string name) => Number(body.GetProperty(name));
    private static double Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Invalid();
    private static MistralTextException Invalid() => MistralTextHttpSseTransport.Fail(NativeChatFailureCode.UnsupportedFeature, "Unsupported Mistral Simple identity, metadata or transcript.");
    private static MistralTextException Limit() => MistralTextHttpSseTransport.Fail(NativeChatFailureCode.ResourceLimit, "Mistral Simple context limit.");
}
