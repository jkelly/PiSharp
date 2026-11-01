// PiSharp-only (no upstream Pi source): reranking and Decisions over OpenRouter.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>
/// Rerank over <c>POST {baseUrl}/rerank</c> (OpenRouter; the Cohere-style shape): <c>{ model, query, documents, top_n? }</c> with the
/// request's options merged in. Hits come from <c>results[].index</c> and <c>relevance_score</c>, most relevant first; <c>usage.cost</c>
/// is the cost.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenRouterRerank : IRerankApi
{
    public const string ApiId = "openrouter-rerank";
    public static OpenRouterRerank Instance { get; } = new();
    public string Api => ApiId;

    public async Task<MediaResult> RerankAsync(MediaModel model, MediaEndpoint endpoint, RerankRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id, ["query"] = request.Query,
            ["documents"] = new JsonArray([.. request.Documents.Select(document => (JsonNode)document)]),
        };
        if (request.TopN is { } top) body["top_n"] = top;
        MediaHttp.MergeOptions(body, request.Options);
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, "rerank"),
            MediaHttp.BearerHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 2, cancellationToken).ConfigureAwait(false);
        if (MediaHttp.Field(response, "results") is not { ValueKind: JsonValueKind.Array } results)
            throw new InvalidDataException($"{model.Provider} returned no rerank results");
        var hits = results.EnumerateArray()
            .Select(item => new RerankHit(MediaHttp.Number(item, "index") is { } index ? (int)index : throw new InvalidDataException("A rerank result has no index"),
                MediaHttp.Number(item, "relevance_score") ?? 0))
            .OrderByDescending(hit => hit.Score).ToImmutableArray();
        return new MediaResult(model.Provider, model.Id, ModelOperationStopReason.Stop) { Ranking = hits, Cost = AudioShared.Cost(response) };
    }
}

/// <summary>
/// OpenRouter Decisions (alpha) over <c>POST {origin}/api/alpha/decisions</c>, at the API's origin rather than under <c>/api/v1</c>:
/// <c>{ model, state, questions }</c> with the request's options merged in. The answer's <c>answers</c> object is the result data.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenRouterDecisions : IDecisionsApi
{
    public const string ApiId = "openrouter-decisions";
    public static OpenRouterDecisions Instance { get; } = new();
    public string Api => ApiId;

    public async Task<MediaResult> DecideAsync(MediaModel model, MediaEndpoint endpoint, DecisionRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["state"] = JsonNode.Parse(request.State.ToString()),
            ["questions"] = JsonNode.Parse(request.Questions.ToString()),
        };
        MediaHttp.MergeOptions(body, request.Options);
        var origin = new Uri(endpoint.BaseUrl).GetLeftPart(UriPartial.Authority);
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(origin, "api/alpha/decisions"),
            MediaHttp.BearerHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 2, cancellationToken).ConfigureAwait(false);
        if (MediaHttp.Field(response, "answers") is not { ValueKind: JsonValueKind.Object } answers)
            throw new InvalidDataException($"{model.Provider} returned no answers");
        return new MediaResult(model.Provider, model.Id, ModelOperationStopReason.Stop)
        {
            Data = PiSharp.Contracts.JsonData.Parse(answers.GetRawText()), Cost = AudioShared.Cost(response),
        };
    }
}
