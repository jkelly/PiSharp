// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/google-vertex.ts (vertexAuth) and
// api/google-vertex.ts (streamSimple; an API key or ADC with project and location, resolved for every request).
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Protocols.GoogleVertex;
using PiSharp.Contracts;

namespace PiSharp.AI.Providers;

/// <summary>One request's Vertex endpoint and credential: an API key (express mode) or an ADC access token.</summary>
public sealed record GoogleVertexRequestAuth(Uri Endpoint, string Token, bool ApiKeyMode, string Project, string Location)
{
    public override string ToString() => "GoogleVertexRequestAuth [redacted]";
}

public static partial class NativeProviderFactory
{
    /// <summary>google-vertex through the Simple Vertex transport, with its endpoint and credential resolved before every request.</summary>
    public static NativeHttpModelProvider CreateGoogleVertexRoute(ModelDescriptor model, JsonData modelMetadata,
        Func<CancellationToken, ValueTask<GoogleVertexRequestAuth>> auth, double? maxTokens = null, bool summary = false, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(modelMetadata); ArgumentNullException.ThrowIfNull(auth);
        if (model.Provider != "google-vertex" || model.Api != "google-vertex") throw new ArgumentException("Unsupported native model or endpoint selection.");
        GoogleVertexSimpleTransport Transport(HttpClient client, GoogleVertexRequestAuth resolved, string? level) => new(client, model,
            new(new(resolved.Endpoint, resolved.Token, new GoogleGenerativeAIOptions(modelMetadata) { MaxTokens = maxTokens, MaximumPayloadBytes = MaximumGooglePayloadBytes }) { ApiKeyMode = resolved.ApiKeyMode },
                resolved.Project, resolved.Location, level));
        return Bind(model, handler, client =>
        {
            var levels = summary ? ImmutableArray.Create("off")
                : Transport(client, new(new("https://aiplatform.googleapis.com/"), "prototype", true, "-", "-"), null).GetSupportedThinkingLevels();
            return new VertexRouteTransport(model, levels, async (request, token) =>
                Transport(client, await auth(token).ConfigureAwait(false), summary ? null : request.ThinkingLevel));
        });
    }

    /// <summary>The Google transport's largest request body, its validated maximum (Pi sends ~4.5 MB images; upstream has no cap).</summary>
    private const int MaximumGooglePayloadBytes = 8_388_608;

    private sealed class VertexRouteTransport(ModelDescriptor model, ImmutableArray<string> levels,
        Func<ChatRequest, CancellationToken, ValueTask<IChatTransport>> create) : IChatTransport, IThinkingLevelTransport
    {
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor selected) => selected == model ? levels : throw new ArgumentException("Unknown native route model.");
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Model != model) throw new ArgumentException("Unknown native route model.");
            return Stream(request, cancellationToken);
        }
        private async IAsyncEnumerable<StreamEvent> Stream(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            IChatTransport? transport = null; string? failure = null;
            try { transport = await create(request, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { failure = error.Message; }
            catch (OperationCanceledException) { failure = "Request was aborted"; }
            if (transport is null)
            {
                var reason = token.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
                yield return new StreamError(reason, new AssistantMessage(model.Api, model.Provider, model.Id, request.Timestamp, [], TokenUsage.Zero, reason,
                    JsonFields.Empty.Set("errorMessage", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(failure ?? "Request failed")))));
                yield break;
            }
            await foreach (var frame in transport.StreamAsync(request, token).ConfigureAwait(false)) yield return frame;
        }
    }
}
