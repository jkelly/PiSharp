// PiSharp-only (no upstream Pi source): embeddings over the OpenAI-compatible /embeddings endpoint (OpenAI, OpenRouter, Mistral,
// Together, llama.cpp and other compatible servers) and over Gemini's batchEmbedContents.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>
/// <c>POST {baseUrl}/embeddings</c> with <c>{ model, input: [...], encoding_format: "float", dimensions? }</c>, the request's
/// <see cref="EmbeddingRequest.Options"/> merged in, and bearer auth. A base URL without a version segment gets <c>/v1</c>. More than
/// <see cref="MaxInputsPerRequest"/> texts are sent in several requests. Vectors are read from <c>data[].embedding</c> and ordered by
/// <c>data[].index</c>; <c>usage.prompt_tokens</c> is the input token count.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenAIEmbeddings : IEmbeddingsApi
{
    public const string ApiId = "openai-embeddings";
    /// <summary>OpenAI's limit on inputs in one request.</summary>
    public const int MaxInputsPerRequest = 2048;
    public static OpenAIEmbeddings Instance { get; } = new();
    public string Api => ApiId;

    public async Task<EmbeddingResult> EmbedAsync(MediaModel model, MediaEndpoint endpoint, EmbeddingRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var headers = MediaHttp.BearerHeaders(endpoint);
        var url = MediaHttp.Url(MediaHttp.Versioned(endpoint.BaseUrl), "embeddings");
        var vectors = new List<float[]>(request.Input.Length);
        int? tokens = null;
        foreach (var batch in request.Input.Chunk(MaxInputsPerRequest))
        {
            var body = new JsonObject
            {
                ["model"] = model.Id,
                ["input"] = new JsonArray([.. batch.Select(text => (JsonNode)text)]),
                ["encoding_format"] = "float",
            };
            if (request.Dimensions is { } dimensions) body["dimensions"] = dimensions;
            MediaHttp.MergeOptions(body, request.Options);
            var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, url, headers, MediaHttp.Json(body), options,
                options.MaxRetries ?? 2, cancellationToken).ConfigureAwait(false);
            if (MediaHttp.Field(response, "data") is not { ValueKind: JsonValueKind.Array } data)
                throw new InvalidDataException($"{model.Provider} returned no embedding data");
            var ordered = new float[]?[batch.Length];
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = MediaHttp.Number(item, "index") is { } declared ? (int)declared : position;
                position++;
                if (index < 0 || index >= ordered.Length) throw new InvalidDataException($"{model.Provider} returned an embedding for input {index}");
                ordered[index] = Vector(MediaHttp.Field(item, "embedding"), model.Provider);
            }
            if (ordered.Any(vector => vector is null))
                throw new InvalidDataException($"{model.Provider} returned {position} embeddings for {batch.Length} inputs");
            vectors.AddRange(ordered.Select(vector => vector!));
            if (MediaHttp.Field(response, "usage") is { ValueKind: JsonValueKind.Object } usage && MediaHttp.Number(usage, "prompt_tokens") is { } count)
                tokens = (tokens ?? 0) + (int)count;
        }
        return new EmbeddingResult(model.Provider, model.Id, [.. vectors], ModelOperationStopReason.Stop) { InputTokens = tokens };
    }

    internal static float[] Vector(JsonElement? value, string provider)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array)
            throw new InvalidDataException($"{provider} returned an embedding that is not a float array (base64 encoding is not supported)");
        var vector = new float[array.GetArrayLength()];
        var i = 0;
        foreach (var number in array.EnumerateArray())
            vector[i++] = number.ValueKind == JsonValueKind.Number ? number.GetSingle() : throw new InvalidDataException($"{provider} returned a non-numeric embedding value");
        return vector;
    }
}

/// <summary>
/// Gemini <c>POST {baseUrl}/models/{id}:batchEmbedContents</c> with one request per input
/// (<c>{ model: "models/{id}", content: { parts: [{ text }] }, taskType?, outputDimensionality? }</c>) and <c>x-goog-api-key</c>
/// auth, in batches of <see cref="MaxInputsPerRequest"/>; <see cref="EmbeddingRequest.Options"/> are merged into each request. Vectors
/// are read from <c>embeddings[].values</c>. Gemini reports no token usage for embeddings.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class GoogleEmbeddings : IEmbeddingsApi
{
    public const string ApiId = "google-embeddings";
    /// <summary>Gemini's limit on requests in one batch.</summary>
    public const int MaxInputsPerRequest = 100;
    public static GoogleEmbeddings Instance { get; } = new();
    public string Api => ApiId;

    public async Task<EmbeddingResult> EmbedAsync(MediaModel model, MediaEndpoint endpoint, EmbeddingRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var name = model.Id.StartsWith("models/", StringComparison.Ordinal) ? model.Id : "models/" + model.Id;
        var url = MediaHttp.Url(endpoint.BaseUrl, name + ":batchEmbedContents");
        var vectors = ImmutableArray.CreateBuilder<float[]>(request.Input.Length);
        foreach (var batch in request.Input.Chunk(MaxInputsPerRequest))
            vectors.AddRange(await BatchAsync(model, name, url, endpoint, batch, request, options, cancellationToken).ConfigureAwait(false));
        return new EmbeddingResult(model.Provider, model.Id, vectors.MoveToImmutable(), ModelOperationStopReason.Stop);
    }

    private static async Task<List<float[]>> BatchAsync(MediaModel model, string name, Uri url, MediaEndpoint endpoint, string[] batch,
        EmbeddingRequest request, MediaOptions options, CancellationToken cancellationToken)
    {
        var requests = new JsonArray();
        foreach (var text in batch)
        {
            var item = new JsonObject
            {
                ["model"] = name,
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            };
            if (request.TaskType is { Length: > 0 } taskType) item["taskType"] = taskType;
            if (request.Dimensions is { } dimensions) item["outputDimensionality"] = dimensions;
            MediaHttp.MergeOptions(item, request.Options);
            requests.Add(item);
        }
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, url, MediaHttp.GoogleHeaders(endpoint),
            MediaHttp.Json(new JsonObject { ["requests"] = requests }), options, options.MaxRetries ?? 2, cancellationToken).ConfigureAwait(false);
        if (MediaHttp.Field(response, "embeddings") is not { ValueKind: JsonValueKind.Array } embeddings || embeddings.GetArrayLength() != batch.Length)
            throw new InvalidDataException($"{model.Provider} returned a different number of embeddings than inputs");
        return [.. embeddings.EnumerateArray().Select(embedding => OpenAIEmbeddings.Vector(MediaHttp.Field(embedding, "values"), model.Provider))];
    }
}
