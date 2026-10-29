// PiSharp-only (no upstream Pi source): the speech, transcription, music, rerank and decision tools of the media extension.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.AI.Media;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

public static partial class MediaTools
{
    // ----- generate_speech -----

    private static readonly JsonObjectValue SpeechArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","properties":{
        "text":{"type":"string","description":"Text to speak."},
        "turns":{"type":"array","items":{"type":"object","required":["text"],"properties":{"text":{"type":"string"},"voice":{"type":"string"},"instructions":{"type":"string"}}},"description":"Multi-speaker models: turns, each with its own text, voice and instructions, instead of text."},
        "model":{"type":"string","description":"provider/model, e.g. openai/gpt-4o-mini-tts, openrouter/elevenlabs/eleven-v3 or openrouter/hexgrad/kokoro-82m. Omit for the default."},
        "voice":{"type":"string","description":"Provider voice id, e.g. OpenAI alloy, ash, coral, nova, sage, shimmer; ElevenLabs and others have their own."},
        "instructions":{"type":"string","description":"Tone, pacing or emotion (OpenAI gpt-4o-mini-tts, Gemini TTS, Fish Audio)."},
        "format":{"type":"string","description":"mp3 (default) or pcm on OpenRouter; OpenAI also opus, aac, flac, wav."},
        "speed":{"type":"number","description":"Playback speed multiplier, where supported (e.g. 0.25-4 on OpenAI)."},
        "voice_samples":{"type":"array","items":{"type":"string"},"description":"OpenRouter voice cloning: paths of 1-3 audio clips of the voice, or an image for voice design."},
        "options":{"type":"object","description":"Extra provider fields sent as they are."},
        "output_path":{"type":"string","description":"File or directory to save to. Default: a new file under the media output directory."}}}
        """);

    /// <summary><c>generate_speech</c>: text to speech, saved to an audio file.</summary>
    public static ToolRegistration CreateSpeechTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = SpeechToolName,
        Description = "Turn text into spoken audio (text-to-speech) and save it to a file: narration, voice-overs, multi-speaker dialogue, " +
            "or a cloned voice from samples. Returns the saved path.",
        Parameters = SpeechArguments,
        StructuredOutputSchema = FilesSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "voice_samples"),
        Execute = async (arguments, api, context) =>
        {
            if (await SavedAsync(api, "speech", context).ConfigureAwait(false) is { } saved) return Replayed(saved, "audio file");
            var (args, model) = await PrepareAsync(options, MediaKind.Speech, arguments, context).ConfigureAwait(false);
            var env = ToolEnv.Require(api);
            var turns = args.TryGetValue("turns", out var turnValue) && turnValue is JsonArrayValue list
                ? list.OfType<JsonObjectValue>().Select(turn => new SpeechTurn(RequiredText(turn, "text"), Text(turn, "voice"), Text(turn, "instructions"))).ToImmutableArray()
                : [];
            var text = Text(args, "text") ?? "";
            if (text.Length == 0 && turns.IsEmpty) throw new InvalidOperationException("text or turns is required");
            var samples = ImmutableArray.CreateBuilder<MediaReference>();
            foreach (var sample in Strings(args, "voice_samples")) samples.Add(await ReadReferenceAsync(env, sample, context).ConfigureAwait(false));
            var request = new SpeechRequest(text)
            {
                Turns = turns, Voice = Text(args, "voice"), Instructions = Text(args, "instructions"), Format = Text(args, "format"),
                Speed = Number(args, "speed"), References = samples.ToImmutable(), Options = Options(args),
            };
            await MarkSentAsync(api, context).ConfigureAwait(false);
            var result = await options.Media.SpeakAsync(model, request, null, context.CancellationToken).ConfigureAwait(false);
            if (result.Audio is not { } audio) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "speech failed"}");
            var stem = Stem("speech", text.Length > 0 ? text : turns[0].Text);
            var path = OutputPaths(Text(args, "output_path"), options.OutputDirectory, stem, [Extension(audio.MimeType)])[0];
            await SaveAsync(env, path, audio.Data, context).ConfigureAwait(false);
            var summary = (JsonObjectValue)await api.MemoAsync(ResultMemo,
                Summary(model, [FileEntry(path, audio.MimeType, audio.Data.Length)], null, null, result.Cost), context).ConfigureAwait(false);
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(Describe("audio file", summary))), StructuredOutput = summary, Details = summary };
        },
    });

    // ----- transcribe_audio -----

    private static readonly JsonObjectValue TranscribeArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["path"],"properties":{
        "path":{"type":"string","description":"Audio file to transcribe (mp3, wav, m4a, ogg, flac, webm; 25 MB at most)."},
        "model":{"type":"string","description":"provider/model, e.g. openai/gpt-4o-mini-transcribe, openai/whisper-1 or openrouter/elevenlabs/scribe-v2. Omit for the default."},
        "language":{"type":"string","description":"ISO-639-1 code such as en or ja; auto-detected when omitted."},
        "prompt":{"type":"string","description":"OpenAI: text that guides spelling or style (names, jargon)."},
        "temperature":{"type":"number","description":"Sampling temperature."},
        "timestamps":{"type":"boolean","description":"Include segment and word timestamps (verbose_json; OpenAI only on whisper-1)."},
        "diarize":{"type":"boolean","description":"OpenRouter: label each word with its speaker (implies timestamps)."},
        "keyterms":{"type":"array","items":{"type":"string"},"description":"OpenRouter: names or terms to bias recognition toward (some models)."},
        "options":{"type":"object","description":"Extra provider fields sent as form parts."},
        "output_path":{"type":"string","description":"Also write the transcript here: .json gets the full result with timestamps, anything else the text."}}}
        """);

    private static readonly JsonObjectValue TranscriptSchema = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["model","text"],"properties":{"model":{"type":"string"},"text":{"type":"string"},"details":{"type":"object"},"path":{"type":"string"},"cost":{"type":"number"}}}
        """);

    /// <summary><c>transcribe_audio</c>: speech to text.</summary>
    public static ToolRegistration CreateTranscribeTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = TranscribeToolName,
        Description = "Transcribe an audio file to text (speech-to-text), optionally with timestamps and speaker labels.",
        Parameters = TranscribeArguments,
        StructuredOutputSchema = TranscriptSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "keyterms"),
        Execute = async (arguments, api, context) =>
        {
            var (args, model) = await PrepareAsync(options, MediaKind.Transcription, arguments, context).ConfigureAwait(false);
            var env = ToolEnv.Require(api);
            var path = RequiredText(args, "path");
            var diarize = Flag(args, "diarize") == true;
            var request = new TranscriptionRequest(await ReadFileAsync(env, path, context).ConfigureAwait(false), Path.GetFileName(path))
            {
                Language = Text(args, "language"), Prompt = Text(args, "prompt"), Temperature = Number(args, "temperature"),
                Timestamps = diarize || Flag(args, "timestamps") == true, Diarize = diarize, KeyTerms = [.. Strings(args, "keyterms")], Options = Options(args),
            };
            var result = await options.Media.TranscribeAsync(model, request, null, context.CancellationToken).ConfigureAwait(false);
            if (result.Text is not { } transcript) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "transcription failed"}");
            var structured = new List<KeyValuePair<string, JsonValue>> { new("model", model.Spec), new("text", transcript) };
            if (result.Data is { } data) structured.Add(new("details", JsonValue.FromData(data)));
            if (result.Cost is { } cost) structured.Add(new("cost", cost));
            var text = new StringBuilder($"Transcript of {path} ({model.Spec}):\n{transcript}");
            if (Text(args, "output_path") is { } outputPath)
            {
                var full = JsonValue.Object(structured);
                var content = outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? full.ToString() : transcript;
                await SaveAsync(env, outputPath, Encoding.UTF8.GetBytes(content), context).ConfigureAwait(false);
                structured.Add(new("path", outputPath));
                text.Append($"\n\nWrote the transcript to {outputPath}.");
            }
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(text.ToString())), StructuredOutput = JsonValue.Object(structured) };
        },
    });

    // ----- generate_music -----

    private static readonly JsonObjectValue MusicArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["prompt"],"properties":{
        "prompt":{"type":"string","description":"The music: genre, mood, instruments, tempo, structure, and lyrics if wanted."},
        "model":{"type":"string","description":"provider/model, e.g. openrouter/google/lyria-3-clip-preview (30 s clip) or openrouter/google/lyria-3-pro-preview (full song). Omit for the default."},
        "images":{"type":"array","items":{"type":"string"},"description":"Paths of images to set the mood (models that accept images)."},
        "seed":{"type":"number","description":"Seed for repeatable output, where supported."},
        "options":{"type":"object","description":"Extra request fields sent as they are, e.g. {\"audio\":{\"voice\":\"alloy\",\"format\":\"wav\"}} for gpt-audio."},
        "output_path":{"type":"string","description":"File or directory to save to. Default: a new file under the media output directory."}}}
        """);

    /// <summary><c>generate_music</c>: music or other audio from a prompt, saved to a file.</summary>
    public static ToolRegistration CreateMusicTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = MusicToolName,
        Description = "Generate music (a clip or a full song) or other audio from a prompt and save it to a file. Returns the saved path.",
        Parameters = MusicArguments,
        StructuredOutputSchema = FilesSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "images"),
        Execute = async (arguments, api, context) =>
        {
            if (await SavedAsync(api, "music", context).ConfigureAwait(false) is { } saved) return Replayed(saved, "audio file");
            var (args, model) = await PrepareAsync(options, MediaKind.Music, arguments, context).ConfigureAwait(false);
            var env = ToolEnv.Require(api);
            var prompt = RequiredText(args, "prompt");
            var images = ImmutableArray.CreateBuilder<ImageContent>();
            foreach (var imagePath in Strings(args, "images")) images.Add(await ReadImageAsync(env, imagePath, context).ConfigureAwait(false));
            var request = new MusicRequest(prompt) { Images = images.ToImmutable(), Seed = Seed(args), Options = Options(args) };
            await MarkSentAsync(api, context).ConfigureAwait(false);
            var result = await options.Media.ComposeAsync(model, request, null, context.CancellationToken).ConfigureAwait(false);
            if (result.Audio is not { } audio) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "music generation failed"}");
            var path = OutputPaths(Text(args, "output_path"), options.OutputDirectory, Stem("music", prompt), [Extension(audio.MimeType)])[0];
            await SaveAsync(env, path, audio.Data, context).ConfigureAwait(false);
            var summary = (JsonObjectValue)await api.MemoAsync(ResultMemo,
                Summary(model, [FileEntry(path, audio.MimeType, audio.Data.Length)], result.Text, null, result.Cost), context).ConfigureAwait(false);
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(Describe("audio file", summary))), StructuredOutput = summary, Details = summary };
        },
    });

    // ----- rerank -----

    private static readonly JsonObjectValue RerankArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["query","documents"],"properties":{
        "query":{"type":"string","description":"What to rank the documents against."},
        "documents":{"type":"array","items":{"type":"string"},"description":"Texts to rank."},
        "top_n":{"type":"number","description":"Return only the best N."},
        "model":{"type":"string","description":"provider/model, e.g. openrouter/cohere/rerank-4-fast or openrouter/voyageai/rerank-2.5-lite. Omit for the default."},
        "options":{"type":"object","description":"Extra provider fields sent as they are, e.g. OpenRouter {\"provider\":{\"order\":[\"cohere\"]}}."}}}
        """);

    private static readonly JsonObjectValue RerankSchema = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["model","ranking"],"properties":{"model":{"type":"string"},"ranking":{"type":"array","items":{"type":"object","required":["index","score"],"properties":{"index":{"type":"number"},"score":{"type":"number"}}}},"cost":{"type":"number"}}}
        """);

    /// <summary><c>rerank</c>: orders documents by relevance to a query with a rerank model.</summary>
    public static ToolRegistration CreateRerankTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = RerankToolName,
        Description = "Rank documents by relevance to a query with a rerank model (more precise than embeddings for search results).",
        Parameters = RerankArguments,
        StructuredOutputSchema = RerankSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "documents"),
        Execute = async (arguments, api, context) =>
        {
            var (args, model) = await PrepareAsync(options, MediaKind.Rerank, arguments, context).ConfigureAwait(false);
            var documents = Texts(args, "documents");
            var request = new RerankRequest(RequiredText(args, "query"), [.. documents]) { TopN = Integer(args, "top_n"), Options = Options(args) };
            var result = await options.Media.RerankAsync(model, request, null, context.CancellationToken).ConfigureAwait(false);
            if (result.StopReason != ModelOperationStopReason.Stop) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "rerank failed"}");
            var text = new StringBuilder($"Ranked {documents.Count} document(s) with {model.Spec}:");
            foreach (var hit in result.Ranking)
                text.Append(CultureInfo.InvariantCulture, $"\n{hit.Score:0.0000}  [{hit.Index}] {(hit.Index < documents.Count ? Preview(documents[hit.Index]) : "")}");
            var structured = new List<KeyValuePair<string, JsonValue>>
            {
                new("model", model.Spec),
                new("ranking", JsonValue.Array(result.Ranking.Select(hit => (JsonValue)JsonValue.Object([new("index", hit.Index), new("score", Round(hit.Score))])))),
            };
            if (result.Cost is { } cost) structured.Add(new("cost", cost));
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(text.ToString())), StructuredOutput = JsonValue.Object(structured) };
        },
    });

    // ----- decide -----

    private static readonly JsonObjectValue DecideArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["state","questions"],"properties":{
        "state":{"description":"The content to judge: a string, or a JSON object or array of related context."},
        "questions":{"type":"object","description":"Named questions. Each is {\"type\":\"noul\",\"instructions\":...} (yes/no probability), {\"type\":\"choice\",\"instructions\":...,\"criteria\":{\"label\":\"meaning\",...}}, {\"type\":\"score\",\"instructions\":...,\"criteria\":[\"lowest\",...,\"highest\"]} or {\"type\":\"box\",\"instructions\":...} (image bounding box)."},
        "model":{"type":"string","description":"provider/model, e.g. openrouter/typesafe/jev-1.14 or openrouter/perplexity/pplx-decider-flash-v1.2. Omit for the default."},
        "options":{"type":"object","description":"Extra provider fields sent as they are."}}}
        """);

    private static readonly JsonObjectValue DecideSchema = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["model","answers"],"properties":{"model":{"type":"string"},"answers":{"type":"object"},"cost":{"type":"number"}}}
        """);

    /// <summary><c>decide</c>: fast, calibrated judgments (yes/no, choice, score, bounding box) from a decision model.</summary>
    public static ToolRegistration CreateDecideTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = DecideToolName,
        Description = "Ask a decision (classifier) model calibrated questions about some content: yes/no probabilities, a choice between " +
            "labels, a score on a scale, or an image bounding box. Cheap and fast; good for routing, moderation and grading.",
        Parameters = DecideArguments,
        StructuredOutputSchema = DecideSchema,
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var (args, model) = await PrepareAsync(options, MediaKind.Decision, arguments, context).ConfigureAwait(false);
            if (!args.TryGetValue("state", out var state)) throw new InvalidOperationException("state is required");
            if (!args.TryGetValue("questions", out var questions) || questions is not JsonObjectValue) throw new InvalidOperationException("questions must be an object");
            var request = new DecisionRequest(state.ToData(), questions.ToData()) { Options = Options(args) };
            var result = await options.Media.DecideAsync(model, request, null, context.CancellationToken).ConfigureAwait(false);
            if (result.Data is not { } answers) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "decision failed"}");
            var structured = new List<KeyValuePair<string, JsonValue>> { new("model", model.Spec), new("answers", JsonValue.FromData(answers)) };
            if (result.Cost is { } cost) structured.Add(new("cost", cost));
            return new ToolExecutionResult
            {
                Output = JsonValue.Array(TextBlock($"Answers from {model.Spec}:\n{answers}")), StructuredOutput = JsonValue.Object(structured),
            };
        },
    });
}
