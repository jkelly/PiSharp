using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.GoogleVertex;

/// <summary>Actual native REST request consumer. The endpoint/token are already admitted by the caller.</summary>
public sealed class GoogleVertexRequestFactory
{
    private readonly ModelDescriptor model;
    private readonly GoogleVertexOptions options;
    public GoogleVertexRequestFactory(ModelDescriptor model, GoogleVertexOptions options)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(options);
        options.Validate(model); this.model = model; this.options = options;
    }
    public async ValueTask<HttpRequestMessage> CreateAsync(ChatRequest request, CancellationToken token = default)
    {
        if (request.Model != model) throw GoogleData.Fail(GoogleFailure.Configuration);
        token.ThrowIfCancellationRequested();
        var projection = options.Projection;
        var parameters = GoogleRequestProjector.ProjectVertex(request, projection);
        if (projection.Hooks.OnPayload is { } payload)
        {
            var replacement = await payload(parameters, new(model, projection.ModelMetadata), token).ConfigureAwait(false);
            if (replacement is not null) parameters = GoogleData.Admit(replacement, projection);
        }
        token.ThrowIfCancellationRequested();
        // An exact endpoint is bound to this selected model; no SDK endpoint builder is invented.
        if (GoogleData.String(parameters.Value, "model") != model.Id) throw GoogleData.Fail(GoogleFailure.Configuration);
        var body = GoogleData.Admit(JsonData.Parse(GoogleRequestProjector.WireBody(parameters).ToJsonString()), projection);
        var bytes = Encoding.UTF8.GetBytes(EcmaScriptJsonProjection.Project(body));
        if (bytes.Length > projection.MaximumPayloadBytes) throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["User-Agent"] = "pi/1.1.0", ["Accept"] = "text/event-stream" };
        void Merge(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            foreach (var property in value.EnumerateObject())
            {
                // Generated auth is this explicit token's authority. SDK header override equivalence remains open.
                if (property.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)) throw GoogleData.Fail(GoogleFailure.Configuration);
                if (property.Value.ValueKind == JsonValueKind.Null) headers.Remove(property.Name);
                else if (property.Value.ValueKind == JsonValueKind.String) headers[property.Name] = property.Value.GetString()!;
                else throw GoogleData.Fail(GoogleFailure.UnsupportedValue);
            }
        }
        if (projection.ModelMetadata.Value.TryGetProperty("headers", out var modelHeaders)) Merge(modelHeaders);
        if (projection.Headers is { } extra) Merge(extra.Value);
        headers["Authorization"] = "Bearer " + options.AccessToken;
        if (headers.Count + 1 > projection.MaximumHeaders || headers.Any(header => header.Key.Length + header.Value.Length > projection.MaximumHeaderCharacters))
            throw GoogleData.Fail(GoogleFailure.ResourceLimit);
        var owned = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        try
        {
            owned.Content = new ByteArrayContent(bytes); owned.Content.Headers.ContentType = new("application/json");
            foreach (var header in headers)
            {
                if (header.Key.Any(char.IsControl) || header.Value.Any(character => character is '\r' or '\n') ||
                    !owned.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw GoogleData.Fail(GoogleFailure.Configuration);
            }
            return owned;
        }
        catch { owned.Dispose(); throw; }
    }
}
