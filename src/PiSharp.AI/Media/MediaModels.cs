// PiSharp-only (no upstream Pi source): embeddings, video, speech, transcription, music, rerank and decisions, and the media model
// descriptions they and the PiSharp image APIs use. Upstream Pi has image generation (ModelOperations/OpenRouterImages.cs) and
// classifiers, but none of these.
using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>What a media model produces.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public enum MediaKind { Embedding, Image, Video, Speech, Transcription, Music, Rerank, Decision }

/// <summary>
/// One embedding, image or video model: the provider it is called through, the model id that provider knows, and the
/// <see cref="Api"/> implementation that speaks its wire protocol. Listed models come from <see cref="MediaCatalog"/> or the
/// host; a spec the catalog does not list resolves to an unlisted model with the provider's default API.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MediaModel(MediaKind Kind, string Provider, string Id, string Api)
{
    /// <summary>Display name; empty means <see cref="Id"/>.</summary>
    public string Name { get; init; } = "";
    /// <summary>A base URL that replaces the provider's, e.g. a host catalog entry's.</summary>
    public string? BaseUrl { get; init; }
    /// <summary>Model headers, sent under the resolved auth headers.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Headers { get; init; } = [];
    /// <summary>Embeddings: the vector size the model returns by default.</summary>
    public int? Dimensions { get; init; }
    /// <summary>Images and videos: whether reference images (or a first frame) are accepted.</summary>
    public bool AcceptsImages { get; init; }
    /// <summary>Images: whether the model can also answer in text.</summary>
    public bool OutputsText { get; init; }
    /// <summary>Embeddings: USD per million input tokens, when known.</summary>
    public double? CostPerMillionTokens { get; init; }
    /// <summary>False for a model synthesized from a spec the catalog does not list.</summary>
    public bool Listed { get; init; } = true;

    /// <summary><c>provider/id</c>, the form tools and settings name models by.</summary>
    public string Spec => Provider + "/" + Id;
    public string DisplayName => Name.Length == 0 ? Id : Name;

    /// <summary>This image model as the image API contract's <see cref="ImageModel"/> at <paramref name="baseUrl"/>.</summary>
    public ImageModel ToImageModel(string baseUrl) => new(Id, DisplayName, Api, Provider, baseUrl,
        AcceptsImages ? ["text", "image"] : ["text"], ModelCost.Free, OutputsText ? ["image", "text"] : ["image"]) { Headers = Headers };
}

/// <summary>Where and how one request is sent: the resolved base URL, credential and headers. Secrets never appear in ToString.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MediaEndpoint(string BaseUrl, string? ApiKey)
{
    /// <summary>Auth, model and request headers merged in that order; a null value suppresses a header.</summary>
    public ImmutableArray<KeyValuePair<string, string?>> Headers { get; init; } = [];
    public override string ToString() => $"MediaEndpoint ({BaseUrl}) [redacted]";
}

/// <summary>Request options of the media APIs: the model-operation options plus video polling.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MediaOptions : ModelRequestOptions
{
    /// <summary>Video: the wait between status polls; default 5000.</summary>
    public int? PollIntervalMs { get; init; }
    /// <summary>Video: how long a job may run before the request fails; default 20 minutes.</summary>
    public int? MaxWaitMs { get; init; }
}

// ----- Embeddings -----

/// <summary>Texts to embed, in order. <see cref="Dimensions"/> asks models that support it for shorter vectors;
/// <see cref="TaskType"/> is a Gemini task type such as <c>RETRIEVAL_QUERY</c> or <c>SEMANTIC_SIMILARITY</c>.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record EmbeddingRequest(ImmutableArray<string> Input)
{
    public int? Dimensions { get; init; }
    public string? TaskType { get; init; }
    /// <summary>A JSON object merged into each request as it is (for example OpenRouter's <c>provider</c> routing or OpenAI's
    /// <c>user</c>).</summary>
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>One vector per input text, in input order. Failures arrive as an error or aborted result with an
/// <see cref="ErrorMessage"/> and no vectors.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record EmbeddingResult(string Provider, string Model, ImmutableArray<float[]> Vectors, ModelOperationStopReason StopReason)
{
    public int? InputTokens { get; init; }
    public double? Cost { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>One embedding API implementation. It may throw; <see cref="MediaRegistry"/> turns failures into error results.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface IEmbeddingsApi
{
    string Api { get; }
    Task<EmbeddingResult> EmbedAsync(MediaModel model, MediaEndpoint endpoint, EmbeddingRequest request, MediaOptions options,
        CancellationToken cancellationToken);
}

// ----- Video -----

/// <summary>A video prompt. <see cref="Image"/> is a first frame for models that accept one; the other fields are passed to
/// providers that understand them. The prompt may be empty for models that animate an image alone.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record VideoRequest(string Prompt)
{
    public ImageContent? Image { get; init; }
    /// <summary>OpenRouter: an image for the last frame.</summary>
    public ImageContent? LastFrame { get; init; }
    /// <summary>OpenRouter: reference images, audio or video that guide the generation.</summary>
    public ImmutableArray<MediaReference> References { get; init; } = [];
    public int? DurationSeconds { get; init; }
    /// <summary>For example <c>16:9</c> or <c>9:16</c>.</summary>
    public string? AspectRatio { get; init; }
    /// <summary>For example <c>720p</c>, <c>1080p</c> or <c>4K</c>.</summary>
    public string? Resolution { get; init; }
    /// <summary>OpenRouter: exact pixels, <c>WIDTHxHEIGHT</c>.</summary>
    public string? Size { get; init; }
    /// <summary>Google: what to avoid.</summary>
    public string? NegativePrompt { get; init; }
    /// <summary>OpenRouter: whether to generate a soundtrack, where the model can.</summary>
    public bool? GenerateAudio { get; init; }
    public long? Seed { get; init; }
    /// <summary>OpenRouter: a completed job to edit or extend.</summary>
    public string? PreviousJobId { get; init; }
    /// <summary>A JSON object merged into the submission as it is (for example OpenRouter's <c>provider</c> options).</summary>
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>A reference asset: an image, audio or video, as a URL (<c>https:</c> or a <c>data:</c> URL).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MediaReference(MediaReferenceKind Kind, string Url)
{
    /// <summary>A <c>data:</c> URL of base64 bytes.</summary>
    public static MediaReference FromBytes(MediaReferenceKind kind, ReadOnlySpan<byte> data, string mimeType) =>
        new(kind, $"data:{mimeType};base64,{Convert.ToBase64String(data)}");
}

[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public enum MediaReferenceKind { Image, Audio, Video }

[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public enum VideoJobState { Queued, Running, Completed, Failed }

/// <summary>A video job's state. <see cref="Result"/> is the provider's handle of the finished video (a download URI).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record VideoJobStatus(VideoJobState State)
{
    /// <summary>Percent complete, when the provider reports it.</summary>
    public double? Progress { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Result { get; init; }
    /// <summary>USD, when the provider reports it.</summary>
    public double? Cost { get; init; }
}

/// <summary>A downloaded video.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record GeneratedVideo(byte[] Data, string MimeType);

/// <summary>One video API implementation: an asynchronous job that is submitted, polled and downloaded. It may throw;
/// <see cref="MediaRegistry"/> turns failures into error results.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface IVideosApi
{
    string Api { get; }
    /// <summary>Starts a job and returns its id.</summary>
    Task<string> SubmitAsync(MediaModel model, MediaEndpoint endpoint, VideoRequest request, MediaOptions options, CancellationToken cancellationToken);
    Task<VideoJobStatus> GetStatusAsync(MediaModel model, MediaEndpoint endpoint, string jobId, MediaOptions options, CancellationToken cancellationToken);
    /// <summary>Downloads a completed job's video; <paramref name="completed"/> is its last status.</summary>
    Task<GeneratedVideo> DownloadAsync(MediaModel model, MediaEndpoint endpoint, string jobId, VideoJobStatus completed, MediaOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Hooks that make a video job resumable: a caller that records the job id when it is submitted can pass it back as
/// <see cref="ResumeJobId"/> after a restart, and polling continues without a second submission.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record VideoJobHooks
{
    /// <summary>A job submitted earlier: polled instead of submitting a new one.</summary>
    public string? ResumeJobId { get; init; }
    /// <summary>Called once with a new job's id; returns the id to poll (a durable caller returns the id it recorded first).</summary>
    public Func<string, CancellationToken, ValueTask<string>>? OnSubmittedAsync { get; init; }
    /// <summary>Called with every polled status.</summary>
    public Func<VideoJobStatus, CancellationToken, ValueTask>? OnStatusAsync { get; init; }
}

/// <summary>A video generation outcome. Failures arrive as an error or aborted result with an <see cref="ErrorMessage"/>; a job
/// that was submitted keeps its <see cref="JobId"/>.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record VideoResult(string Provider, string Model, ModelOperationStopReason StopReason)
{
    public string? JobId { get; init; }
    public GeneratedVideo? Video { get; init; }
    public double? Cost { get; init; }
    public string? ErrorMessage { get; init; }
}

// ----- Speech, transcription, music, rerank and decisions -----

/// <summary>Generated audio bytes and their MIME type.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record GeneratedAudio(byte[] Data, string MimeType);

/// <summary>One turn of multi-speaker speech.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record SpeechTurn(string Text, string? Voice = null, string? Instructions = null);

/// <summary>Text to speak. <see cref="Turns"/> replaces <see cref="Input"/> for multi-speaker models; <see cref="References"/> are audio
/// (voice cloning) or an image (voice design).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record SpeechRequest(string Input)
{
    public ImmutableArray<SpeechTurn> Turns { get; init; } = [];
    public string? Voice { get; init; }
    /// <summary>Tone, pacing or emotion (OpenAI gpt-4o-mini-tts, Gemini TTS, Fish Audio).</summary>
    public string? Instructions { get; init; }
    /// <summary>OpenRouter: <c>mp3</c> or <c>pcm</c>; OpenAI also <c>opus</c>, <c>aac</c>, <c>flac</c>, <c>wav</c>.</summary>
    public string? Format { get; init; }
    public double? Speed { get; init; }
    public ImmutableArray<MediaReference> References { get; init; } = [];
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>Audio to transcribe.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record TranscriptionRequest(byte[] Audio, string FileName)
{
    /// <summary>ISO-639-1, e.g. <c>en</c>; auto-detected when null.</summary>
    public string? Language { get; init; }
    /// <summary>OpenAI: text that guides style or spelling.</summary>
    public string? Prompt { get; init; }
    public double? Temperature { get; init; }
    /// <summary>Segment and word timestamps (<c>verbose_json</c>).</summary>
    public bool Timestamps { get; init; }
    /// <summary>OpenRouter: label each word with its speaker (needs <see cref="Timestamps"/>).</summary>
    public bool Diarize { get; init; }
    /// <summary>OpenRouter: terms to bias recognition toward.</summary>
    public ImmutableArray<string> KeyTerms { get; init; } = [];
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>Music or other audio from a prompt (OpenRouter chat models with audio output, such as Lyria).</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MusicRequest(string Prompt)
{
    public ImmutableArray<ImageContent> Images { get; init; } = [];
    public long? Seed { get; init; }
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>Documents to order by relevance to a query.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record RerankRequest(string Query, ImmutableArray<string> Documents)
{
    public int? TopN { get; init; }
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>One reranked document: its input index and relevance score.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record RerankHit(int Index, double Score);

/// <summary>A Decisions request: <see cref="State"/> is the content to judge (a string, object or array) and <see cref="Questions"/> an
/// object of named questions (<c>{ type: "noul" | "choice" | "score" | "box", instructions, criteria? }</c>), sent as they are.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record DecisionRequest(PiSharp.Contracts.JsonData State, PiSharp.Contracts.JsonData Questions)
{
    public PiSharp.Contracts.JsonData? Options { get; init; }
}

/// <summary>The outcome of a speech, transcription, music, rerank or decision request. Failures arrive as an error or aborted result
/// with an <see cref="ErrorMessage"/>.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed record MediaResult(string Provider, string Model, ModelOperationStopReason StopReason)
{
    /// <summary>Speech and music.</summary>
    public GeneratedAudio? Audio { get; init; }
    /// <summary>A transcript, or the text a music model returned with its audio.</summary>
    public string? Text { get; init; }
    /// <summary>Rerank: hits, most relevant first.</summary>
    public ImmutableArray<RerankHit> Ranking { get; init; } = [];
    /// <summary>The structured part of the response: a transcription's language, duration, segments and words, or decision answers.</summary>
    public PiSharp.Contracts.JsonData? Data { get; init; }
    /// <summary>USD, when the provider reports it.</summary>
    public double? Cost { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>One speech API.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface ISpeechApi
{
    string Api { get; }
    Task<MediaResult> SpeakAsync(MediaModel model, MediaEndpoint endpoint, SpeechRequest request, MediaOptions options, CancellationToken cancellationToken);
}

/// <summary>One transcription API.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface ITranscriptionApi
{
    string Api { get; }
    Task<MediaResult> TranscribeAsync(MediaModel model, MediaEndpoint endpoint, TranscriptionRequest request, MediaOptions options,
        CancellationToken cancellationToken);
}

/// <summary>One music API.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface IMusicApi
{
    string Api { get; }
    Task<MediaResult> ComposeAsync(MediaModel model, MediaEndpoint endpoint, MusicRequest request, MediaOptions options, CancellationToken cancellationToken);
}

/// <summary>One rerank API.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface IRerankApi
{
    string Api { get; }
    Task<MediaResult> RerankAsync(MediaModel model, MediaEndpoint endpoint, RerankRequest request, MediaOptions options, CancellationToken cancellationToken);
}

/// <summary>One decisions API.</summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public interface IDecisionsApi
{
    string Api { get; }
    Task<MediaResult> DecideAsync(MediaModel model, MediaEndpoint endpoint, DecisionRequest request, MediaOptions options, CancellationToken cancellationToken);
}
