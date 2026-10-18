// PiSharp-only (no upstream Pi source): speech, transcription and music over the OpenAI-compatible audio endpoints (OpenAI and
// OpenRouter) and OpenRouter's chat completions with audio output.
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.Media;

[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
internal static class AudioShared
{
    /// <summary>The MIME type of audio bytes by their signature, else <paramref name="fallback"/>.</summary>
    internal static string Sniff(ReadOnlySpan<byte> data, string fallback)
    {
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8)) return "audio/wav";
        if (data.Length >= 3 && data[..3].SequenceEqual("ID3"u8)) return "audio/mpeg";
        if (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x06) != 0) return "audio/mpeg";
        if (data.Length >= 4 && data[..4].SequenceEqual("OggS"u8)) return "audio/ogg";
        if (data.Length >= 4 && data[..4].SequenceEqual("fLaC"u8)) return "audio/flac";
        if (data.Length >= 8 && data[4..8].SequenceEqual("ftyp"u8)) return "audio/mp4";
        return fallback;
    }

    internal static string FormatMime(string? format) => format?.ToLowerInvariant() switch
    {
        "wav" => "audio/wav", "flac" => "audio/flac", "opus" or "ogg" => "audio/ogg", "aac" => "audio/aac", "pcm" => "audio/pcm",
        _ => "audio/mpeg",
    };

    /// <summary>The short format name of an audio MIME type (<c>mp3</c>, <c>wav</c>, …), as OpenRouter's <c>input_audio.format</c> takes it.</summary>
    internal static string FormatOf(string mime) => mime.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => "mp3", "audio/wav" or "audio/x-wav" or "audio/wave" => "wav", "audio/ogg" => "ogg", "audio/flac" => "flac",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "m4a", "audio/webm" => "webm", "audio/aac" => "aac",
        _ => mime.Contains('/') ? mime[(mime.IndexOf('/') + 1)..] : mime,
    };

    /// <summary>A <c>data:</c> URL's MIME type and base64 payload, or null for another URL.</summary>
    internal static (string Mime, string Data)? DataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal)) return null;
        var comma = url.IndexOf(','); var semicolon = url.IndexOf(';');
        return comma < 0 || semicolon < 0 || semicolon > comma ? null : (url[5..semicolon], url[(comma + 1)..]);
    }

    internal static double? Cost(JsonElement response) =>
        MediaHttp.Field(response, "usage") is { ValueKind: JsonValueKind.Object } usage ? MediaHttp.Number(usage, "cost") : null;
}

/// <summary>
/// Speech over the OpenAI-compatible <c>POST {baseUrl}/audio/speech</c> (OpenAI and OpenRouter): <c>model</c>, <c>input</c> (text, or
/// a list of <c>{ text, voice, instructions }</c> turns for multi-speaker models), <c>voice</c>, <c>instructions</c>,
/// <c>response_format</c>, <c>speed</c> and OpenRouter's <c>input_references</c> for voice cloning, with the request's options merged in.
/// The answer is the audio bytes. OpenAI requires a voice, so <c>alloy</c> is sent when none is given.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenAISpeech : ISpeechApi
{
    public const string ApiId = "openai-speech";
    public static OpenAISpeech Instance { get; } = new();
    public string Api => ApiId;

    public async Task<MediaResult> SpeakAsync(MediaModel model, MediaEndpoint endpoint, SpeechRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["model"] = model.Id };
        if (!request.Turns.IsDefaultOrEmpty)
            body["input"] = new JsonArray([.. request.Turns.Select(turn =>
            {
                var item = new JsonObject { ["text"] = turn.Text };
                if (turn.Voice is { } voice) item["voice"] = voice;
                if (turn.Instructions is { } instructions) item["instructions"] = instructions;
                return (JsonNode)item;
            })]);
        else body["input"] = request.Input;
        var voiceName = request.Voice ?? (model.Provider == "openai" && request.Turns.IsDefaultOrEmpty ? "alloy" : null);
        if (voiceName is not null) body["voice"] = voiceName;
        if (request.Instructions is { } guidance) body["instructions"] = guidance;
        // OpenRouter: without a format some models (Kokoro) answer raw PCM, which no player opens.
        var format = request.Format ?? (model.Provider == "openrouter" ? "mp3" : null);
        if (format is not null) body["response_format"] = format;
        if (request.Speed is { } speed) body["speed"] = speed;
        if (!request.References.IsDefaultOrEmpty)
            body["input_references"] = new JsonArray([.. request.References.Select(Reference)]);
        MediaHttp.MergeOptions(body, request.Options);
        var (response, audio) = await MediaHttp.SendAsync(HttpMethod.Post, MediaHttp.Url(MediaHttp.Versioned(endpoint.BaseUrl), "audio/speech"),
            MediaHttp.BearerHeaders(endpoint, "audio/*"), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        if (audio.Length == 0) throw new InvalidDataException($"{model.Provider} returned no audio");
        var type = response.Headers.FirstOrDefault(pair => pair.Key == "content-type").Value?.Split(';')[0].Trim();
        var mime = type is { Length: > 0 } && type.StartsWith("audio/", StringComparison.Ordinal) ? type
            : AudioShared.Sniff(audio, AudioShared.FormatMime(format));
        return new MediaResult(model.Provider, model.Id, ModelOperationStopReason.Stop) { Audio = new GeneratedAudio(audio, mime) };
    }

    private static JsonNode Reference(MediaReference reference)
    {
        if (reference.Kind == MediaReferenceKind.Image)
            return new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = reference.Url } };
        var audio = AudioShared.DataUrl(reference.Url) is { } inline
            ? new JsonObject { ["data"] = inline.Data, ["format"] = AudioShared.FormatOf(inline.Mime) }
            : new JsonObject { ["url"] = reference.Url, ["format"] = Path.GetExtension(new Uri(reference.Url).AbsolutePath).TrimStart('.') };
        return new JsonObject { ["type"] = "input_audio", ["input_audio"] = audio };
    }
}

/// <summary>
/// Transcription over the OpenAI-compatible multipart <c>POST {baseUrl}/audio/transcriptions</c> (OpenAI and OpenRouter):
/// <c>file</c>, <c>model</c>, <c>language</c>, <c>prompt</c>, <c>temperature</c>, <c>response_format</c> (<c>verbose_json</c> with
/// <c>timestamp_granularities[]</c> when timestamps or speaker labels are asked for, else <c>json</c>), OpenRouter's <c>diarize</c> and
/// <c>keyterms[]</c>, and the request's options as further parts (objects JSON-encoded). OpenRouter gets the same fields as its documented
/// JSON body instead, with the audio as base64 <c>input_audio</c> (its multipart parser rejects .NET's form encoding). The answer's
/// <c>text</c> is the transcript and the rest of it (language, duration, segments, words) the result data.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenAITranscription : ITranscriptionApi
{
    public const string ApiId = "openai-transcription";
    public static OpenAITranscription Instance { get; } = new();
    public string Api => ApiId;

    public async Task<MediaResult> TranscribeAsync(MediaModel model, MediaEndpoint endpoint, TranscriptionRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var verbose = request.Timestamps || request.Diarize;
        Func<HttpContent> content = model.Provider == "openrouter" ? MediaHttp.Json(JsonBody(model, request, verbose)) : Content;
        HttpContent Content()
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(request.Audio);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(MimeOf(request.FileName));
            form.Add(file, "file", Path.GetFileName(request.FileName));
            void Add(string name, string value) => form.Add(new StringContent(value), name);
            Add("model", model.Id);
            if (request.Language is { } language) Add("language", language);
            if (request.Prompt is { } prompt) Add("prompt", prompt);
            if (request.Temperature is { } temperature) Add("temperature", temperature.ToString(CultureInfo.InvariantCulture));
            Add("response_format", verbose ? "verbose_json" : "json");
            if (verbose) { Add("timestamp_granularities[]", "segment"); Add("timestamp_granularities[]", "word"); }
            if (request.Diarize) Add("diarize", "true");
            foreach (var term in request.KeyTerms.IsDefault ? [] : request.KeyTerms) Add("keyterms[]", term);
            if (request.Options is { Value.ValueKind: JsonValueKind.Object } extra)
                foreach (var field in extra.Value.EnumerateObject())
                    foreach (var value in field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray() : new[] { field.Value }.AsEnumerable())
                        Add(field.Name, value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
            return form;
        }
        var response = await MediaHttp.SendJsonAsync(model.Provider, HttpMethod.Post, MediaHttp.Url(MediaHttp.Versioned(endpoint.BaseUrl), "audio/transcriptions"),
            MediaHttp.BearerHeaders(endpoint), content, options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        var text = MediaHttp.String(response, "text") ?? throw new InvalidDataException($"{model.Provider} returned no transcript");
        var data = JsonNode.Parse(response.GetRawText())!.AsObject();
        data.Remove("text");
        return new MediaResult(model.Provider, model.Id, ModelOperationStopReason.Stop)
        {
            Text = text, Data = PiSharp.Contracts.JsonData.Parse(data.ToJsonString()), Cost = AudioShared.Cost(response),
        };
    }

    private static JsonObject JsonBody(MediaModel model, TranscriptionRequest request, bool verbose)
    {
        var format = Path.GetExtension(request.FileName).TrimStart('.').ToLowerInvariant();
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(request.Audio), ["format"] = format.Length == 0 ? "mp3" : format },
            ["response_format"] = verbose ? "verbose_json" : "json",
        };
        if (request.Language is { } language) body["language"] = language;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (verbose) body["timestamp_granularities"] = new JsonArray("segment", "word");
        if (request.Diarize) body["diarize"] = true;
        if (!request.KeyTerms.IsDefaultOrEmpty) body["keyterms"] = new JsonArray([.. request.KeyTerms.Select(term => (JsonNode)term)]);
        MediaHttp.MergeOptions(body, request.Options);
        return body;
    }

    internal static string MimeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp3" or ".mpga" or ".mpeg" => "audio/mpeg", ".wav" => "audio/wav", ".m4a" => "audio/mp4", ".mp4" => "audio/mp4", ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/ogg", ".flac" => "audio/flac", ".webm" => "audio/webm", ".aac" => "audio/aac",
        _ => "application/octet-stream",
    };
}

/// <summary>
/// Music and other generated audio over OpenRouter's <c>POST {baseUrl}/chat/completions</c> with <c>modalities: ["text", "audio"]</c>
/// (Lyria, gpt-audio): one user message with the prompt and any images, <c>seed</c>, and the request's options merged in (for example
/// <c>audio: { voice, format }</c>). OpenRouter requires <c>stream: true</c> for audio output: the server-sent chunks'
/// <c>choices[0].delta.audio.data</c> pieces (base64) make up the audio, and their transcript or the text deltas the result text.
/// </summary>
[System.Diagnostics.CodeAnalysis.Experimental("PISHARP_MEDIA001")]
public sealed class OpenRouterChatAudio : IMusicApi
{
    public const string ApiId = "openrouter-chat-audio";
    public static OpenRouterChatAudio Instance { get; } = new();
    public string Api => ApiId;

    public async Task<MediaResult> ComposeAsync(MediaModel model, MediaEndpoint endpoint, MusicRequest request, MediaOptions options,
        CancellationToken cancellationToken)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
        foreach (var image in request.Images.IsDefault ? [] : request.Images)
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{image.MimeType};base64,{image.Data}" } });
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
            ["modalities"] = new JsonArray("text", "audio"),
            ["stream"] = true,
        };
        if (request.Seed is { } seed) body["seed"] = seed;
        MediaHttp.MergeOptions(body, request.Options);
        var (_, raw) = await MediaHttp.SendAsync(HttpMethod.Post, MediaHttp.Url(endpoint.BaseUrl, "chat/completions"),
            MediaHttp.BearerHeaders(endpoint, "text/event-stream"), MediaHttp.Json(body), options, options.MaxRetries ?? 0, cancellationToken).ConfigureAwait(false);
        var audio = new MemoryStream();
        var transcript = new System.Text.StringBuilder();
        var answer = new System.Text.StringBuilder();
        double? cost = null;
        foreach (var line in System.Text.Encoding.UTF8.GetString(raw).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = trimmed[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            JsonElement chunk;
            try { using var document = JsonDocument.Parse(payload); chunk = document.RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (MediaHttp.Field(chunk, "error") is { ValueKind: JsonValueKind.Object } error)
                throw new InvalidDataException($"{model.Provider}: {MediaHttp.String(error, "message") ?? error.GetRawText()}");
            cost = AudioShared.Cost(chunk) ?? cost;
            if (MediaHttp.Field(chunk, "choices") is not { ValueKind: JsonValueKind.Array } choices || choices.GetArrayLength() == 0) continue;
            var delta = MediaHttp.Field(choices[0], "delta") ?? MediaHttp.Field(choices[0], "message");
            if (delta is not { } part) continue;
            if (MediaHttp.String(part, "content") is { Length: > 0 } words) answer.Append(words);
            if (MediaHttp.Field(part, "audio") is not { ValueKind: JsonValueKind.Object } piece) continue;
            if (MediaHttp.String(piece, "data") is { Length: > 0 } data) { var bytes = Convert.FromBase64String(data); audio.Write(bytes); }
            if (MediaHttp.String(piece, "transcript") is { Length: > 0 } said) transcript.Append(said);
        }
        var text = transcript.Length > 0 ? transcript.ToString() : answer.ToString();
        if (audio.Length == 0)
            throw new InvalidDataException($"{model.Provider} returned no audio" + (text.Length > 0 ? ": " + text[..Math.Min(text.Length, 300)] : ""));
        var result = audio.ToArray();
        return new MediaResult(model.Provider, model.Id, ModelOperationStopReason.Stop)
        {
            Audio = new GeneratedAudio(result, AudioShared.Sniff(result, "audio/pcm")), Text = text.Length > 0 ? text : null, Cost = cost,
        };
    }
}
