// PiSharp-only (no upstream Pi source): the built-in media models and each provider's default media API.
using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;

namespace PiSharp.AI.Media;

/// <summary>
/// The media models PiSharp lists out of the box, in preference order (the first available model of a kind is the default unless
/// settings name one), and the API a provider's unlisted models use. OpenRouter's image models also come from its released catalog
/// shard (hosts add them with <see cref="FromImageModel"/>); any other OpenRouter model can be named as <c>openrouter/&lt;id&gt;</c>, and
/// <see cref="MediaRegistry.DiscoverAsync"/> lists them all.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public static class MediaCatalog
{
    private static MediaModel Embedding(string provider, string id, string api, int dimensions, double cost, string? name = null) =>
        new(MediaKind.Embedding, provider, id, api) { Dimensions = dimensions, CostPerMillionTokens = cost, Name = name ?? "" };

    private static MediaModel Image(string provider, string id, string api, bool acceptsImages, bool outputsText = false, string? name = null) =>
        new(MediaKind.Image, provider, id, api) { AcceptsImages = acceptsImages, OutputsText = outputsText, Name = name ?? "" };

    private static MediaModel Of(MediaKind kind, string provider, string id, string api, string name, bool acceptsImages = false) =>
        new(kind, provider, id, api) { Name = name, AcceptsImages = acceptsImages };

    /// <summary>The built-in models.</summary>
    public static ImmutableArray<MediaModel> Builtin { get; } =
    [
        Embedding("openai", "text-embedding-3-small", OpenAIEmbeddings.ApiId, 1536, 0.02, "OpenAI Embedding 3 Small"),
        Embedding("openai", "text-embedding-3-large", OpenAIEmbeddings.ApiId, 3072, 0.13, "OpenAI Embedding 3 Large"),
        Embedding("google", "gemini-embedding-001", GoogleEmbeddings.ApiId, 3072, 0.15, "Gemini Embedding"),
        Embedding("openrouter", "openai/text-embedding-3-small", OpenAIEmbeddings.ApiId, 1536, 0.02, "OpenAI Embedding 3 Small (OpenRouter)"),
        Embedding("openrouter", "openai/text-embedding-3-large", OpenAIEmbeddings.ApiId, 3072, 0.13, "OpenAI Embedding 3 Large (OpenRouter)"),
        Embedding("mistral", "mistral-embed", OpenAIEmbeddings.ApiId, 1024, 0.1, "Mistral Embed"),

        Image("openai", "gpt-image-1.5", OpenAIImages.ApiId, acceptsImages: true, name: "GPT Image 1.5"),
        Image("openai", "gpt-image-2", OpenAIImages.ApiId, acceptsImages: true, name: "GPT Image 2"),
        Image("openai", "gpt-image-1", OpenAIImages.ApiId, acceptsImages: true, name: "GPT Image 1"),
        Image("openai", "gpt-image-1-mini", OpenAIImages.ApiId, acceptsImages: true, name: "GPT Image 1 Mini"),
        Image("google", "gemini-2.5-flash-image", GoogleImages.ApiId, acceptsImages: true, outputsText: true, name: "Gemini 2.5 Flash Image"),
        Image("google", "imagen-4.0-generate-001", GoogleImages.ApiId, acceptsImages: false, name: "Imagen 4"),

        Of(MediaKind.Video, "google", "veo-3.0-generate-001", GoogleVideos.ApiId, "Veo 3", acceptsImages: true),
        Of(MediaKind.Video, "google", "veo-3.0-fast-generate-001", GoogleVideos.ApiId, "Veo 3 Fast", acceptsImages: true),
        Of(MediaKind.Video, "google", "veo-3.1-generate-preview", GoogleVideos.ApiId, "Veo 3.1 (preview)", acceptsImages: true),
        Of(MediaKind.Video, "openrouter", "google/veo-3.1-fast", OpenRouterVideos.ApiId, "Veo 3.1 Fast (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Video, "openrouter", "google/veo-3.1-lite", OpenRouterVideos.ApiId, "Veo 3.1 Lite (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Video, "openrouter", "google/veo-3.1", OpenRouterVideos.ApiId, "Veo 3.1 (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Video, "openrouter", "kwaivgi/kling-v3.0-std", OpenRouterVideos.ApiId, "Kling 3.0 Standard (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Video, "openrouter", "bytedance/seedance-2.0-fast", OpenRouterVideos.ApiId, "Seedance 2.0 Fast (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Video, "openrouter", "runway/gen-4.5", OpenRouterVideos.ApiId, "Runway Gen-4.5 (OpenRouter)", acceptsImages: true),

        Of(MediaKind.Speech, "openai", "gpt-4o-mini-tts", OpenAISpeech.ApiId, "GPT-4o mini TTS"),
        Of(MediaKind.Speech, "openai", "tts-1", OpenAISpeech.ApiId, "TTS-1"),
        Of(MediaKind.Speech, "openrouter", "hexgrad/kokoro-82m", OpenAISpeech.ApiId, "Kokoro 82M (OpenRouter)"),
        Of(MediaKind.Speech, "openrouter", "google/gemini-3.1-flash-tts-preview", OpenAISpeech.ApiId, "Gemini 3.1 Flash TTS (OpenRouter)"),
        Of(MediaKind.Speech, "openrouter", "elevenlabs/eleven-flash-v2.5", OpenAISpeech.ApiId, "ElevenLabs Flash v2.5 (OpenRouter)"),
        Of(MediaKind.Speech, "openrouter", "elevenlabs/eleven-v3", OpenAISpeech.ApiId, "ElevenLabs v3 (OpenRouter)"),

        Of(MediaKind.Transcription, "openai", "gpt-4o-mini-transcribe", OpenAITranscription.ApiId, "GPT-4o mini Transcribe"),
        Of(MediaKind.Transcription, "openai", "gpt-4o-transcribe", OpenAITranscription.ApiId, "GPT-4o Transcribe"),
        Of(MediaKind.Transcription, "openai", "whisper-1", OpenAITranscription.ApiId, "Whisper"),
        Of(MediaKind.Transcription, "openrouter", "openai/whisper-large-v3-turbo", OpenAITranscription.ApiId, "Whisper Large v3 Turbo (OpenRouter)"),
        Of(MediaKind.Transcription, "openrouter", "openai/gpt-4o-mini-transcribe", OpenAITranscription.ApiId, "GPT-4o mini Transcribe (OpenRouter)"),
        Of(MediaKind.Transcription, "openrouter", "elevenlabs/scribe-v2", OpenAITranscription.ApiId, "ElevenLabs Scribe v2 (OpenRouter)"),

        Of(MediaKind.Music, "openrouter", "google/lyria-3-clip-preview", OpenRouterChatAudio.ApiId, "Lyria 3 Clip, 30 s (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Music, "openrouter", "google/lyria-3-pro-preview", OpenRouterChatAudio.ApiId, "Lyria 3 Pro, full songs (OpenRouter)", acceptsImages: true),

        Of(MediaKind.Rerank, "openrouter", "cohere/rerank-4-fast", OpenRouterRerank.ApiId, "Cohere Rerank 4 Fast (OpenRouter)"),
        Of(MediaKind.Rerank, "openrouter", "cohere/rerank-4-pro", OpenRouterRerank.ApiId, "Cohere Rerank 4 Pro (OpenRouter)"),
        Of(MediaKind.Rerank, "openrouter", "voyageai/rerank-2.5-lite", OpenRouterRerank.ApiId, "Voyage Rerank 2.5 Lite (OpenRouter)"),

        Of(MediaKind.Decision, "openrouter", "typesafe/jev-1.14", OpenRouterDecisions.ApiId, "TypeSafe JEV 1.14 (OpenRouter)", acceptsImages: true),
        Of(MediaKind.Decision, "openrouter", "perplexity/pplx-decider-flash-v1.2", OpenRouterDecisions.ApiId, "Perplexity Decider Flash (OpenRouter)", acceptsImages: true),
    ];

    /// <summary>The API an unlisted model of <paramref name="provider"/> uses, or null when the provider has none of that kind.
    /// Embeddings default to the OpenAI-compatible API, which most providers serve.</summary>
    public static string? DefaultApi(MediaKind kind, string provider) => (kind, provider) switch
    {
        (MediaKind.Embedding, "google") => GoogleEmbeddings.ApiId,
        (MediaKind.Embedding, "anthropic" or "amazon-bedrock" or "google-vertex" or "openai-codex" or "github-copilot") => null,
        (MediaKind.Embedding, _) => OpenAIEmbeddings.ApiId,
        (MediaKind.Image, "openai") => OpenAIImages.ApiId,
        (MediaKind.Image, "google") => GoogleImages.ApiId,
        (MediaKind.Image, "openrouter") => OpenRouterImageApi.ApiId,
        (MediaKind.Video, "google") => GoogleVideos.ApiId,
        (MediaKind.Video, "openrouter") => OpenRouterVideos.ApiId,
        (MediaKind.Speech, "openai" or "openrouter") => OpenAISpeech.ApiId,
        (MediaKind.Transcription, "openai" or "openrouter") => OpenAITranscription.ApiId,
        (MediaKind.Music, "openrouter") => OpenRouterChatAudio.ApiId,
        (MediaKind.Rerank, "openrouter") => OpenRouterRerank.ApiId,
        (MediaKind.Decision, "openrouter") => OpenRouterDecisions.ApiId,
        _ => null,
    };

    /// <summary>OpenRouter's <c>architecture.output_modalities</c> value for a kind.</summary>
    public static string OpenRouterModality(MediaKind kind) => kind switch
    {
        MediaKind.Embedding => "embeddings",
        MediaKind.Image => "image",
        MediaKind.Video => "video",
        MediaKind.Speech => "speech",
        MediaKind.Transcription => "transcription",
        MediaKind.Music => "audio",
        MediaKind.Rerank => "rerank",
        _ => "decisions",
    };

    /// <summary>The lower-case name of a kind, as settings and tools spell it.</summary>
    public static string Name(MediaKind kind) => kind switch
    {
        MediaKind.Embedding => "embedding", MediaKind.Image => "image", MediaKind.Video => "video", MediaKind.Speech => "speech",
        MediaKind.Transcription => "transcription", MediaKind.Music => "music", MediaKind.Rerank => "rerank", _ => "decision",
    };

    /// <summary>The kind a name spells (singular or plural, a few synonyms), or null.</summary>
    public static MediaKind? Parse(string name) => name.Trim().ToLowerInvariant() switch
    {
        "embedding" or "embeddings" or "embed" => MediaKind.Embedding,
        "image" or "images" => MediaKind.Image,
        "video" or "videos" => MediaKind.Video,
        "speech" or "tts" or "voice" => MediaKind.Speech,
        "transcription" or "transcriptions" or "stt" or "transcribe" => MediaKind.Transcription,
        "music" or "audio" or "song" => MediaKind.Music,
        "rerank" or "reranking" => MediaKind.Rerank,
        "decision" or "decisions" or "decide" or "classify" => MediaKind.Decision,
        _ => null,
    };

    /// <summary>A catalog image model (an OpenRouter shard entry) as a media model. OpenRouter's entries name the chat-completions
    /// route (<c>openrouter-images</c>), which takes no image options; media requests use its Image API instead.</summary>
    public static MediaModel FromImageModel(ImageModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var api = model.Api == OpenRouterImages.ApiId ? OpenRouterImageApi.ApiId : model.Api;
        return new MediaModel(MediaKind.Image, model.Provider, model.Id, api)
        {
            Name = model.Name, BaseUrl = model.BaseUrl.Length == 0 ? null : model.BaseUrl, Headers = model.Headers, AcceptsImages = model.AcceptsImages,
            OutputsText = !model.Output.IsDefault && model.Output.Contains("text"),
        };
    }
}
