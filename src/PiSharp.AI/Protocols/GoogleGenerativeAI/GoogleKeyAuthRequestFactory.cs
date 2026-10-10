using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.GoogleGenerativeAI;

/// <summary>Native REST key authentication. Every call creates a fresh owned request.</summary>
public sealed class GoogleKeyAuthRequestFactory(ModelDescriptor model, GoogleGenerativeAIOptions options)
{
    public async ValueTask<HttpRequestMessage> CreateAsync(ChatRequest request, CancellationToken token = default)
    {
        if (request.Model != model) throw GoogleData.Fail(GoogleFailure.Configuration);
        options.Validate(); token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(options.ApiKey)) throw GoogleData.Fail(GoogleFailure.MissingKey);
        var parameters = GoogleRequestProjector.Project(request, options);
        if (options.Hooks.OnPayload is { } payload)
        {
            var replacement = await payload(parameters, new(model, options.ModelMetadata), token).ConfigureAwait(false);
            if (replacement is not null) parameters = GoogleData.Admit(replacement, options);
        }
        token.ThrowIfCancellationRequested();
        var requestModel = GoogleData.String(parameters.Value, "model");
        if (string.IsNullOrEmpty(requestModel)) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
        var baseUrl = GoogleData.String(options.ModelMetadata.Value, "baseUrl");
        if (string.IsNullOrEmpty(baseUrl)) baseUrl = "https://generativelanguage.googleapis.com/v1beta";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) throw GoogleData.Fail(GoogleFailure.Configuration);
        var name = requestModel.StartsWith("models/", StringComparison.Ordinal) ? requestModel[7..] : requestModel;
        var endpoint = new Uri(baseUrl.TrimEnd('/') + "/models/" + Uri.EscapeDataString(name) + ":streamGenerateContent?alt=sse");
        var body = GoogleData.Admit(JsonData.Parse(JsonUtf16.ToJsonString(GoogleRequestProjector.WireBody(parameters))), options);
        // The body (images included) is bounded by the payload budget, not by the projection's 1 MiB string defaults.
        var projected = EcmaScriptJsonProjection.Project(body, new(MaximumInputCharacters: options.MaximumPayloadBytes,
            MaximumInputBytes: options.MaximumPayloadBytes, MaximumOutputCharacters: options.MaximumPayloadBytes,
            MaximumOutputBytes: options.MaximumPayloadBytes, MaximumStringCharacters: options.MaximumPayloadBytes,
            // Node and number counts grow with the message count; the payload budget bounds them.
            MaximumNodes: options.MaximumPayloadBytes, MaximumNumbers: options.MaximumPayloadBytes, MaximumTotalNumberCharacters: options.MaximumPayloadBytes));
        if (Encoding.UTF8.GetByteCount(projected) > options.MaximumPayloadBytes) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-goog-api-key"] = options.ApiKey, ["content-type"] = "application/json", ["accept"] = "text/event-stream",
            ["user-agent"] = "pi/1.1.0"
        };
        void Merge(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            foreach (var field in value.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.Null) headers.Remove(field.Name);
                else if (field.Value.ValueKind == JsonValueKind.String) headers[field.Name] = field.Value.GetString();
                else throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            }
        }
        if (options.ModelMetadata.Value.TryGetProperty("headers", out var modelHeaders)) Merge(modelHeaders);
        // provider-attribution.ts getSessionHeaders (opencode-headers.ts for the bare provider): an OpenCode request carries the
        // agent session id in x-opencode-session, below the caller's own headers.
        foreach (var (header, value) in PiSharp.AI.Providers.ProviderHeaderPolicies.OpenCodeSessionHeaders(model.Provider, endpoint, request.SessionId))
            headers[header] = value;
        if (options.Headers is { } extra) Merge(extra.Value);
        if (headers.Count > options.MaximumHeaders || headers.Any(h => h.Key.Length + (h.Value?.Length ?? 0) > options.MaximumHeaderCharacters))
            throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        var owned = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            owned.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(projected));
            foreach (var header in headers)
            {
                if (header.Key.Contains('\r') || header.Key.Contains('\n') || header.Value?.Contains('\r') == true || header.Value?.Contains('\n') == true)
                    throw GoogleData.Fail(GoogleFailure.Configuration);
                if (!owned.Headers.TryAddWithoutValidation(header.Key, header.Value) &&
                    !owned.Content.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw GoogleData.Fail(GoogleFailure.Configuration);
            }
            return owned;
        }
        catch { owned.Dispose(); throw; }
    }
}
