using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>Simple provider admission and projection over the accepted direct factory and actual HTTP/SSE owners.</summary>
public sealed class CompletionsSimpleRequestFactory : IChatTransport
{
    private static readonly ImmutableArray<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly ModelDescriptor _model;
    private readonly CompletionsSimpleOptions _options;
    private readonly bool _reasoning;
    private readonly JsonElement _levelMap;
    private readonly double _contextWindow, _modelMaxTokens;

    public CompletionsSimpleRequestFactory(HttpClient client, Uri endpoint, ModelDescriptor model, CompletionsSimpleOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (endpoint is null || model is null || options is null || options.ModelMetadata is null ||
            options.DirectOptions is null || options.HttpOptions is null || !endpoint.IsAbsoluteUri ||
            endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 ||
            model.Api != "openai-completions" || options.MaximumContextMessages is < 1 or > 65_536 ||
            options.MaximumContextCharacters is < 2 or > PiRequestBudget.MaximumBound ||
            !double.IsFinite(options.DirectOptions.MaximumTokenMagnitude) || options.DirectOptions.MaximumTokenMagnitude <= 0)
            throw Fail(CompletionsRequestFailure.InvalidConfiguration);
        _client = client; _endpoint = endpoint; _model = model; _options = options;
        if (options.TimeoutMilliseconds is < 0 || options.WebsocketConnectTimeoutMilliseconds is < 0 ||
            options.TransportPreference is not (null or "sse" or "auto" or "websocket") ||
            options.Reasoning is { } requested && !Levels.Contains(requested, StringComparer.Ordinal))
            throw Fail(CompletionsRequestFailure.UnsupportedContent);
        // Pi forwards these values through buildBaseOptions. Completions does not
        // serialize telemetry/metadata or use the WebSocket preference/deadline.
        // The configured direct cache retention is explicit and wins over env;
        // scoped env therefore supplies no implicit credentials, proxy or client mutation.
        AdmitOpaqueOption(options.TelemetryContext);
        AdmitOpaqueOption(options.Metadata, requireObject: true);
        AdmitOpaqueOption(options.Environment, requireObject: true, stringValues: true);
        try
        {
            var raw = options.ModelMetadata.Value;
            if (raw.ValueKind != JsonValueKind.Object || String(raw, "id") != model.Id ||
                String(raw, "api") != model.Api || String(raw, "provider") != model.Provider)
                throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            _contextWindow = Number(raw, "contextWindow", CompletionsRequestFailure.InvalidConfiguration);
            _modelMaxTokens = Number(raw, "maxTokens", CompletionsRequestFailure.InvalidConfiguration);
            if (raw.TryGetProperty("reasoning", out var reasoning))
            {
                if (reasoning.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                _reasoning = reasoning.GetBoolean();
            }
            if (raw.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind != JsonValueKind.Null)
            {
                if (map.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                foreach (var entry in map.EnumerateObject())
                    if (!Levels.Contains(entry.Name, StringComparer.Ordinal) || entry.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw Fail(CompletionsRequestFailure.InvalidConfiguration);
                _levelMap = map;
            }
            CheckMagnitude(_modelMaxTokens);
            if (options.DirectOptions.MaxTokens is { } maximum) CheckMagnitude(maximum);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }

    public ImmutableArray<string> GetSupportedThinkingLevels()
    {
        if (!_reasoning) return ["off"];
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var level in Levels)
        {
            var defined = _levelMap.ValueKind == JsonValueKind.Object && _levelMap.TryGetProperty(level, out _);
            if (defined && _levelMap.GetProperty(level).ValueKind == JsonValueKind.Null) continue;
            if (level is "xhigh" or "max" && !defined) continue;
            result.Add(level);
        }
        return result.ToImmutable();
    }

    public string ClampThinkingLevel(string level)
    {
        var index = Levels.IndexOf(level);
        if (index < 0) throw Fail(CompletionsRequestFailure.UnsupportedContent);
        var available = GetSupportedThinkingLevels();
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        for (var i = index; i < Levels.Length; i++) if (available.Contains(Levels[i], StringComparer.Ordinal)) return Levels[i];
        for (var i = index - 1; i >= 0; i--) if (available.Contains(Levels[i], StringComparer.Ordinal)) return Levels[i];
        return available.Length == 0 ? "off" : available[0];
    }

    public CompletionsSimpleResolution Resolve(ChatRequest request)
    {
        if (request is null || request.Model != _model || request.Messages.IsDefault) throw Fail(CompletionsRequestFailure.InvalidRequest);
        var estimate = EstimateContext(request.Messages);
        var maximum = _options.DirectOptions.MaxTokens ?? _modelMaxTokens;
        var resolved = _options.Reasoning is { } requested ? ClampThinkingLevel(requested) : null;
        var capped = _contextWindow <= 0 ? Math.Max(1, maximum) :
            Math.Min(maximum, Math.Max(1, _contextWindow - estimate.Tokens - 4096));
        CheckMagnitude(capped);
        return new(estimate, capped, GetSupportedThinkingLevels(), resolved, resolved is null or "off" ? null : resolved);
    }

    // These ordinary methods admit auth synchronously before the actual owner is created.
    public ValueTask<CompletionsRun> StartAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        Bind(request).StartAsync(request, cancellationToken);

    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
        Bind(request).StreamAsync(request, cancellationToken);

    private CompletionsHttpSseTransport Bind(ChatRequest request)
    {
        var key = AdmitKey();
        var resolved = Resolve(request);
        var direct = new CompletionsKeyAuthRequestFactory(_endpoint, _model, options: _options.DirectOptions with
        { ModelMetadata = _options.ModelMetadata, MaxTokens = resolved.MaxTokens, ReasoningEffort = resolved.ReasoningEffort });
        var hooks = _options.HttpOptions.Hooks ?? new CompletionsLifecycleHooks();
        var http = _options.HttpOptions with
        { RequestTimeoutMilliseconds = _options.TimeoutMilliseconds ?? _options.HttpOptions.RequestTimeoutMilliseconds };
        return CompletionsHttpSseTransport.FromAsyncRequestFactory(_client,
            (current, token) => direct.CreateAsync(current, key, hooks, token), http, direct.ResolvedWireOptions);
    }

    private string AdmitKey()
    {
        if (!string.IsNullOrEmpty(_options.ApiKey)) return _options.ApiKey;
        if (_options.DirectOptions.Headers is { } headers && headers.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in headers.Value.EnumerateObject())
                if ((entry.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                     entry.Name.Equals("cf-aig-authorization", StringComparison.OrdinalIgnoreCase)) &&
                    entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString()!.Trim().Length != 0)
                    return "unused";
        }
        // Raw model headers and DirectOptions.ModelHeaders deliberately do not satisfy simple admission.
        throw Fail(CompletionsRequestFailure.InvalidKey);
    }

    private void AdmitOpaqueOption(JsonData? source, bool requireObject = false, bool stringValues = false)
    {
        if (source is null) return;
        var raw = source.ToString();
        if (raw.Length > _options.DirectOptions.MaximumPayloadBytes ||
            Encoding.UTF8.GetByteCount(raw) > _options.DirectOptions.MaximumPayloadBytes)
            throw Fail(CompletionsRequestFailure.ResourceLimit);
        try
        {
            var owned = CompletionsJson.Strict(source, _options.DirectOptions.MaximumPayloadDepth, CancellationToken.None);
            if (requireObject && owned.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                throw Fail(CompletionsRequestFailure.InvalidConfiguration);
            if (stringValues && owned.Value.ValueKind == JsonValueKind.Object)
                foreach (var entry in owned.Value.EnumerateObject())
                    if (entry.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw Fail(CompletionsRequestFailure.InvalidConfiguration);
        }
        catch (CompletionsRequestException error) when (error.Failure is CompletionsRequestFailure.InvalidTranscript or CompletionsRequestFailure.InvalidRequest)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }

    private CompletionsContextUsageEstimate EstimateContext(ImmutableArray<TranscriptEntry> messages)
    {
        if (messages.Length > _options.MaximumContextMessages) throw Fail(CompletionsRequestFailure.ResourceLimit);
        try
        {
            var latestPrefix = double.NegativeInfinity; int? last = null; var usageTokens = 0d; long inputCharacters = 0;
            for (var i = 0; i < messages.Length; i++)
            {
                var entry = messages[i];
                if (entry is null || entry.WireBody is null) throw Fail(CompletionsRequestFailure.InvalidTranscript);
                var message = entry.WireBody.Value;
                if (message.ValueKind != JsonValueKind.Object || String(message, "role") != entry.Role)
                    throw Fail(CompletionsRequestFailure.InvalidTranscript);
                inputCharacters += entry.WireBody.ToString().Length;
                if (inputCharacters > _options.MaximumContextCharacters) throw Fail(CompletionsRequestFailure.ResourceLimit);
                var timestamp = Number(message, "timestamp", CompletionsRequestFailure.InvalidTranscript);
                if (entry.Role == "assistant")
                {
                    var usage = message.GetProperty("usage");
                    var total = Number(usage, "totalTokens", CompletionsRequestFailure.InvalidTranscript);
                    if (total == 0) total = Number(usage, "input", CompletionsRequestFailure.InvalidTranscript) +
                        Number(usage, "output", CompletionsRequestFailure.InvalidTranscript) +
                        Number(usage, "cacheRead", CompletionsRequestFailure.InvalidTranscript) +
                        Number(usage, "cacheWrite", CompletionsRequestFailure.InvalidTranscript);
                    if (!double.IsFinite(total)) throw Fail(CompletionsRequestFailure.InvalidTranscript);
                    if (timestamp >= latestPrefix && String(message, "stopReason") is not ("aborted" or "error") && total > 0)
                    { last = i; usageTokens = total; }
                }
                latestPrefix = Math.Max(latestPrefix, timestamp);
            }
            var trailing = 0d;
            for (var i = last is { } index ? index + 1 : 0; i < messages.Length; i++) trailing += EstimateMessage(messages[i].WireBody.Value);
            var totalTokens = usageTokens + trailing;
            if (!double.IsFinite(totalTokens)) throw Fail(CompletionsRequestFailure.ResourceLimit);
            return new(totalTokens, usageTokens, trailing, last);
        }
        catch (EcmaScriptJsonProjectionException error)
        { throw Fail(error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit ? CompletionsRequestFailure.ResourceLimit : CompletionsRequestFailure.InvalidTranscript); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Fail(CompletionsRequestFailure.InvalidTranscript); }
    }

    private double EstimateMessage(JsonElement message)
    {
        var role = String(message, "role"); var content = message.GetProperty("content");
        if (role == "system")
        {
            var parts = new List<string> { SystemContent(content) };
            if (message.TryGetProperty("sections", out var sections))
                foreach (var section in sections.EnumerateObject())
                    if (section.Value.ValueKind != JsonValueKind.Null) parts.Add(section.Value.GetString()!);
            var tokens = TextTokens(string.Join("\n\n", parts.Where(p => p.Length != 0)));
            foreach (var name in new[] { "toolsAdded", "toolsRemoved" })
                if (message.TryGetProperty(name, out var tools) && tools.GetArrayLength() > 0) tokens += TextTokens(JsonText(tools));
            return tokens;
        }
        if (role is "user" or "toolResult")
        {
            if (content.ValueKind == JsonValueKind.String) return TextTokens(content.GetString()!);
            var chars = 0d;
            foreach (var block in content.EnumerateArray()) chars += String(block, "type") switch
            { "text" => block.GetProperty("text").GetString()!.Length, "image" => 4800, _ => throw Fail(CompletionsRequestFailure.UnsupportedContent) };
            return Math.Ceiling(chars / 4);
        }
        if (role != "assistant") throw Fail(CompletionsRequestFailure.UnsupportedContent);
        var assistantChars = 0d;
        foreach (var block in content.EnumerateArray()) assistantChars += String(block, "type") switch
        {
            "text" => block.GetProperty("text").GetString()!.Length,
            "thinking" => block.GetProperty("thinking").GetString()!.Length,
            "toolCall" => block.GetProperty("name").GetString()!.Length + JsonText(block.GetProperty("arguments")).Length,
            _ => throw Fail(CompletionsRequestFailure.UnsupportedContent)
        };
        return Math.Ceiling(assistantChars / 4);
    }

    private string JsonText(JsonElement value) => EcmaScriptJsonProjection.Project(JsonData.FromElement(value), new(
        MaximumInputCharacters: _options.MaximumContextCharacters, MaximumInputBytes: _options.MaximumContextCharacters,
        MaximumOutputCharacters: _options.MaximumContextCharacters, MaximumOutputBytes: _options.MaximumContextCharacters,
        MaximumDepth: 64, MaximumStringCharacters: _options.MaximumContextCharacters));

    private static string SystemContent(JsonElement content) => content.ValueKind == JsonValueKind.String ? content.GetString()! :
        string.Join("\n", content.EnumerateArray().Where(b => String(b, "type") == "text").Select(b => b.GetProperty("text").GetString()!));
    private static double TextTokens(string text) => Math.Ceiling(text.Length / 4d);
    private void CheckMagnitude(double value)
    {
        if (!double.IsFinite(value)) throw Fail(CompletionsRequestFailure.InvalidConfiguration);
        if (Math.Abs(value) > _options.DirectOptions.MaximumTokenMagnitude) throw Fail(CompletionsRequestFailure.ResourceLimit);
    }
    private static double Number(JsonElement value, string name, CompletionsRequestFailure failure)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out var number) || !double.IsFinite(number)) throw Fail(failure);
        return number;
    }
    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field.GetString() : null;
    private static CompletionsRequestException Fail(CompletionsRequestFailure failure) => new(failure);
}
