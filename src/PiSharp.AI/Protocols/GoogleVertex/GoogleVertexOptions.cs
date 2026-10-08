using System.Text.Json.Serialization;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.GoogleVertex;

/// <summary>Caller-admitted exact endpoint and token; never acquires Google credentials.
/// Projection holds the genuine google-vertex metadata and existing bounded Google controls.</summary>
public sealed record GoogleVertexOptions(Uri Endpoint, [property: JsonIgnore] string AccessToken,
    GoogleGenerativeAIOptions Projection)
{
    public override string ToString() => nameof(GoogleVertexOptions);
    /// <summary>Pi abe508e1 google-vertex.ts createClientWithApiKey: the token is a Vertex API key sent as x-goog-api-key
    /// (express mode) instead of an OAuth access token in Authorization.</summary>
    public bool ApiKeyMode { get; init; }
    internal void Validate(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(Endpoint); ArgumentNullException.ThrowIfNull(Projection);
        Projection.Validate();
        // Source hooks are single function properties. Multicast async delegates discard
        // earlier ValueTasks; refuse them before any callback or native acquisition.
        if (Projection.Hooks.OnPayload?.GetInvocationList().Length > 1 ||
            Projection.Hooks.OnNativeResponse?.GetInvocationList().Length > 1 ||
            Projection.Hooks.OnProviderStreamEvent?.GetInvocationList().Length > 1 ||
            Projection.BodyReaderFactory?.GetInvocationList().Length > 1)
            throw GoogleData.Fail(GoogleFailure.Configuration);
        if (model.Api != "google-vertex" || model.Provider != "google-vertex" ||
            string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 1024 || model.Id.Any(char.IsControl) ||
            GoogleData.String(Projection.ModelMetadata.Value, "api") != model.Api ||
            GoogleData.String(Projection.ModelMetadata.Value, "provider") != model.Provider ||
            GoogleData.String(Projection.ModelMetadata.Value, "id") != model.Id ||
            Projection.ApiKey is not null || Projection.MaxRetries != 0 ||
            !Endpoint.IsAbsoluteUri || Endpoint.Scheme != "https" || Endpoint.UserInfo.Length != 0 || Endpoint.Fragment.Length != 0 ||
            AccessToken is null || AccessToken.Length is < 1 or > 4096 || AccessToken.Any(character => character is < '!' or > '~'))
            throw GoogleData.Fail(GoogleFailure.Configuration);
    }
}
