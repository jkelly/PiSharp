// PiSharp-only (no upstream Pi source): video generation as asynchronous jobs over Google's long-running predict operations (Veo).
// OpenAI's Videos API (Sora 2) was shut down on 2026-09-24, so it has no implementation.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>
/// Google Veo over the Gemini API: <c>POST {baseUrl}/models/{id}:predictLongRunning</c> with
/// <c>{ instances: [{ prompt, image? }], parameters: { aspectRatio?, durationSeconds?, resolution?, negativePrompt? } }</c> returns an
/// operation <c>{ name }</c>; <c>GET {baseUrl}/{name}</c> reports <c>done</c>, <c>error</c> and, when done,
/// <c>response.generateVideoResponse.generatedSamples[0].video.uri</c>, which is downloaded with the API key (following redirects).
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class GoogleVideos : IVideosApi
{
    public const string ApiId = "google-videos";
    public static GoogleVideos Instance { get; } = new();
    public string Api => ApiId;

    public async Task<string> SubmitAsync(MediaModel model, MediaEndpoint endpoint, VideoRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var name = model.Id.StartsWith("models/", StringComparison.Ordinal) ? model.Id : "models/" + model.Id;
        var instance = new JsonObject { ["prompt"] = request.Prompt };
        if (request.Image is { } image)
            instance["image"] = new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = image.Data } };
        var parameters = new JsonObject();
        if (request.AspectRatio is { Length: > 0 } ratio) parameters["aspectRatio"] = ratio;
        if (request.DurationSeconds is { } seconds) parameters["durationSeconds"] = seconds;
        if (request.Resolution is { Length: > 0 } resolution) parameters["resolution"] = resolution;
        if (request.NegativePrompt is { Length: > 0 } negative) parameters["negativePrompt"] = negative;
        var body = new JsonObject { ["instances"] = new JsonArray(instance) };
        if (parameters.Count != 0) body["parameters"] = parameters;
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, name + ":predictLongRunning"),
            MediaHttp.GoogleHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        return MediaHttp.String(response, "name") is { Length: > 0 } operation ? operation : throw new InvalidDataException($"{model.Provider} returned no operation name");
    }

    public async Task<VideoJobStatus> GetStatusAsync(MediaModel model, MediaEndpoint endpoint, string jobId, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Get, MediaHttp.Url(endpoint.BaseUrl, jobId),
            MediaHttp.GoogleHeaders(endpoint), null, options, options.MaxRetries ?? 3, cancellationToken).ConfigureAwait(false);
        if (MediaHttp.Field(response, "error") is { ValueKind: JsonValueKind.Object } error)
            return new VideoJobStatus(VideoJobState.Failed) { ErrorMessage = MediaHttp.String(error, "message") ?? "Video operation failed" };
        if (MediaHttp.Field(response, "done") is not { ValueKind: JsonValueKind.True }) return new VideoJobStatus(VideoJobState.Running);
        var generated = MediaHttp.Field(response, "response") is { } result ? MediaHttp.Field(result, "generateVideoResponse") : null;
        var samples = generated is { } video ? MediaHttp.Field(video, "generatedSamples") : null;
        if (samples is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0 &&
            MediaHttp.Field(list[0], "video") is { } file && MediaHttp.String(file, "uri") is { Length: > 0 } uri)
            return new VideoJobStatus(VideoJobState.Completed) { Progress = 100, Result = uri };
        var filtered = generated is { } filteredResponse && MediaHttp.Field(filteredResponse, "raiMediaFilteredReasons") is { ValueKind: JsonValueKind.Array } reasons
            ? string.Join("; ", reasons.EnumerateArray().Where(reason => reason.ValueKind == JsonValueKind.String).Select(reason => reason.GetString()))
            : "";
        return new VideoJobStatus(VideoJobState.Failed)
        {
            ErrorMessage = filtered.Length > 0 ? "The video was filtered: " + filtered : "The operation finished without a video",
        };
    }

    public async Task<GeneratedVideo> DownloadAsync(MediaModel model, MediaEndpoint endpoint, string jobId, VideoJobStatus completed,
        MediaOptions options, CancellationToken cancellationToken)
    {
        var uri = new Uri(completed.Result ?? throw new InvalidOperationException("The completed operation has no video URI"));
        // The API key goes only to the API's own host; a URI elsewhere is expected to be signed.
        var sameHost = string.Equals(uri.Host, new Uri(endpoint.BaseUrl).Host, StringComparison.OrdinalIgnoreCase);
        var headers = MediaHttp.GoogleHeaders(sameHost ? endpoint : endpoint with { ApiKey = null }, "video/mp4, */*");
        var (response, body) = await MediaHttp.SendAsync(HttpMethod.Get, uri, headers, null, options, options.MaxRetries ?? 3, cancellationToken)
            .ConfigureAwait(false);
        return new GeneratedVideo(body, MediaHttp.VideoMime(response.Headers));
    }

}

/// <summary>
/// OpenRouter video: <c>POST {baseUrl}/videos</c> with <c>model</c>, <c>prompt</c>, <c>duration</c>, <c>aspect_ratio</c>, <c>resolution</c>,
/// <c>size</c>, <c>seed</c>, <c>generate_audio</c>, <c>frame_images</c> (first and last frame), <c>input_references</c> (image, audio or
/// video) and <c>previous_job_id</c>, with the request's options merged in, returns a job <c>{ id, status }</c>;
/// <c>GET {baseUrl}/videos/{id}</c> reports <c>status</c> (<c>pending</c>, <c>in_progress</c>, <c>completed</c>, <c>failed</c>,
/// <c>cancelled</c>, <c>expired</c>), <c>error</c> and <c>usage.cost</c>; <c>GET {baseUrl}/videos/{id}/content?index=0</c> downloads it.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenRouterVideos : IVideosApi
{
    public const string ApiId = "openrouter-videos";
    public static OpenRouterVideos Instance { get; } = new();
    public string Api => ApiId;

    public async Task<string> SubmitAsync(MediaModel model, MediaEndpoint endpoint, VideoRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["model"] = model.Id };
        if (request.Prompt.Length > 0) body["prompt"] = request.Prompt;
        if (request.DurationSeconds is { } seconds) body["duration"] = seconds;
        if (request.AspectRatio is { Length: > 0 } ratio) body["aspect_ratio"] = ratio;
        if (request.Resolution is { Length: > 0 } resolution) body["resolution"] = resolution;
        if (request.Size is { Length: > 0 } size) body["size"] = size;
        if (request.Seed is { } seed) body["seed"] = seed;
        if (request.GenerateAudio is { } audio) body["generate_audio"] = audio;
        if (request.PreviousJobId is { Length: > 0 } previous) body["previous_job_id"] = previous;
        var frames = new JsonArray();
        if (request.Image is { } first) frames.Add(Frame(first, "first_frame"));
        if (request.LastFrame is { } last) frames.Add(Frame(last, "last_frame"));
        if (frames.Count > 0) body["frame_images"] = frames;
        if (!request.References.IsDefaultOrEmpty)
            body["input_references"] = new JsonArray([.. request.References.Select(reference =>
            {
                var type = reference.Kind switch { MediaReferenceKind.Audio => "audio_url", MediaReferenceKind.Video => "video_url", _ => "image_url" };
                return (JsonNode)new JsonObject { ["type"] = type, [type] = new JsonObject { ["url"] = reference.Url } };
            })]);
        MediaHttp.MergeOptions(body, request.Options);
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, "videos"),
            MediaHttp.BearerHeaders(endpoint), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        return MediaHttp.String(response, "id") is { Length: > 0 } id ? id : throw new InvalidDataException($"{model.Provider} returned no video job id");
    }

    private static JsonObject Frame(ImageContent image, string type) => new()
    {
        ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{image.MimeType};base64,{image.Data}" }, ["frame_type"] = type,
    };

    public async Task<VideoJobStatus> GetStatusAsync(MediaModel model, MediaEndpoint endpoint, string jobId, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Get, MediaHttp.Url(endpoint.BaseUrl, "videos/" + Uri.EscapeDataString(jobId)),
            MediaHttp.BearerHeaders(endpoint), null, options, options.MaxRetries ?? 3, cancellationToken).ConfigureAwait(false);
        var status = MediaHttp.String(response, "status");
        var state = status switch
        {
            "completed" => VideoJobState.Completed,
            "failed" or "cancelled" or "expired" => VideoJobState.Failed,
            "in_progress" => VideoJobState.Running,
            _ => VideoJobState.Queued,
        };
        var cost = MediaHttp.Field(response, "usage") is { ValueKind: JsonValueKind.Object } usage ? MediaHttp.Number(usage, "cost") : null;
        return new VideoJobStatus(state)
        {
            Cost = cost,
            ErrorMessage = state == VideoJobState.Failed ? MediaHttp.String(response, "error") ?? $"Video job {status}" : null,
        };
    }

    public async Task<GeneratedVideo> DownloadAsync(MediaModel model, MediaEndpoint endpoint, string jobId, VideoJobStatus completed,
        MediaOptions options, CancellationToken cancellationToken)
    {
        var (response, body) = await MediaHttp.SendAsync(HttpMethod.Get, MediaHttp.Url(endpoint.BaseUrl, $"videos/{Uri.EscapeDataString(jobId)}/content?index=0"),
            MediaHttp.BearerHeaders(endpoint, "video/*, */*"), null, options, options.MaxRetries ?? 3, cancellationToken).ConfigureAwait(false);
        return new GeneratedVideo(body, MediaHttp.VideoMime(response.Headers));
    }

}
