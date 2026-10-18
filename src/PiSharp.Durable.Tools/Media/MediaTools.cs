// PiSharp-only (no upstream Pi source): durable tools over PiSharp.AI's MediaRegistry. Generated files (images, videos, speech, music)
// are written in the execution environment and their paths returned. The paid calls are memoized in the tool task, so a rerun after a
// crash returns the saved files or keeps polling the submitted job instead of paying again. Each kind has defaults (a model and default
// arguments) that a call's own arguments override; set_media_defaults changes and saves them.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PiSharp.AI.Media;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Tools;

/// <summary>Options of <see cref="MediaTools"/>.</summary>
public sealed record MediaToolsOptions
{
    /// <summary>The models, credentials and APIs the tools call.</summary>
    public required MediaRegistry Media { get; init; }
    /// <summary>Per-kind defaults (model and arguments); empty by default.</summary>
    public IMediaDefaults Defaults { get; init; } = new MediaDefaults();
    /// <summary>Where files go when a call names no <c>output_path</c>; relative paths resolve against the call's working directory.</summary>
    public string OutputDirectory { get; init; } = ".pi/media";
    /// <summary>Whether <c>generate_image</c> also shows the images to a model that accepts image input (within the read tool's size
    /// limit); default true.</summary>
    public bool ShowImages { get; init; } = true;
    /// <summary>Whether the less common tools (speech, transcription, music, rerank, decide) are deferred, so the model finds them with
    /// <c>tool_search</c> instead of seeing them in every request; default true.</summary>
    public bool DeferSpecializedTools { get; init; } = true;
    /// <summary>Video polling interval; null: <see cref="MediaRegistry.DefaultPollInterval"/>.</summary>
    public int? VideoPollIntervalMs { get; init; }
    /// <summary>Longest video job wait; null: <see cref="MediaRegistry.DefaultMaxWait"/>.</summary>
    public int? VideoMaxWaitMs { get; init; }
}

/// <summary>
/// The <c>media</c> extension: <c>generate_image</c>, <c>generate_video</c>, <c>generate_speech</c>, <c>transcribe_audio</c>,
/// <c>generate_music</c>, <c>embed</c>, <c>rerank</c>, <c>decide</c>, <c>list_media_models</c> and <c>set_media_defaults</c>. Subagents
/// start as copies of their parent's agent, so they get these tools too, and programs reach them through nested tool calls with
/// structured output.
/// </summary>
public static partial class MediaTools
{
    public const string ExtensionName = "media";
    public const string ImageToolName = "generate_image";
    public const string VideoToolName = "generate_video";
    public const string SpeechToolName = "generate_speech";
    public const string TranscribeToolName = "transcribe_audio";
    public const string MusicToolName = "generate_music";
    public const string EmbedToolName = "embed";
    public const string RerankToolName = "rerank";
    public const string DecideToolName = "decide";
    public const string ModelsToolName = "list_media_models";
    public const string DefaultsToolName = "set_media_defaults";

    private const string ResultMemo = "result";
    private const string JobMemo = "video-job";

    /// <summary>The extension with every media tool.</summary>
    public static Extension CreateExtension(MediaToolsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ToolRegistration Specialized(ToolRegistration tool) => options.DeferSpecializedTools ? tool with { Deferred = true } : tool;
        return Define.Extension(new Extension
        {
            Name = ExtensionName,
            Tools =
            [
                CreateImageTool(options), CreateVideoTool(options), CreateEmbedTool(options),
                Specialized(CreateSpeechTool(options)), Specialized(CreateTranscribeTool(options)), Specialized(CreateMusicTool(options)),
                Specialized(CreateRerankTool(options)), Specialized(CreateDecideTool(options)),
                CreateModelsTool(options), CreateDefaultsTool(options),
            ],
        });
    }

    // ----- generate_image -----

    private static readonly JsonObjectValue ImageArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["prompt"],"properties":{
        "prompt":{"type":"string","description":"What to draw: subject, style, composition, text to render."},
        "model":{"type":"string","description":"provider/model, e.g. openai/gpt-image-1.5, google/gemini-2.5-flash-image or openrouter/<id>. Omit for the default; list_media_models shows the choices."},
        "reference_images":{"type":"array","items":{"type":"string"},"description":"Paths of images to edit or use as references (models that accept images)."},
        "mask":{"type":"string","description":"OpenAI edits: path of a PNG, the size of the first reference image, whose fully transparent pixels mark the area to change."},
        "count":{"type":"number","description":"Number of images (default 1)."},
        "size":{"type":"string","description":"auto, WIDTHxHEIGHT (OpenAI: 1024x1024, 1536x1024, 1024x1536; newer models any multiple of 16 up to 3840x2160), or a tier such as 2K (OpenRouter)."},
        "aspect_ratio":{"type":"string","description":"Google and OpenRouter: e.g. 1:1, 16:9, 9:16, 4:3, 21:9 or auto."},
        "resolution":{"type":"string","description":"Google and OpenRouter tier: 512, 1K, 2K or 4K."},
        "quality":{"type":"string","description":"low, medium, high or auto (OpenAI and OpenRouter)."},
        "background":{"type":"string","description":"transparent, opaque or auto (OpenAI and OpenRouter). transparent needs png or webp output."},
        "output_format":{"type":"string","description":"png, jpeg or webp (OpenRouter vectorization models also svg)."},
        "output_compression":{"type":"number","description":"0-100 compression for jpeg or webp output."},
        "moderation":{"type":"string","description":"OpenAI moderation: auto or low."},
        "input_fidelity":{"type":"string","description":"OpenAI edits: low or high; high keeps faces and details of the reference images."},
        "seed":{"type":"number","description":"Seed for repeatable output, where the model supports it (OpenRouter)."},
        "options":{"type":"object","description":"Extra provider fields sent as they are, e.g. OpenRouter {\"provider\":{\"order\":[\"openai\"]}} or FLUX {\"steps\":30}."},
        "output_path":{"type":"string","description":"File or directory to save to. Default: a new file under the media output directory."}}}
        """);

    private static readonly JsonObjectValue FilesSchema = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["model","files"],"properties":{
        "model":{"type":"string"},
        "files":{"type":"array","items":{"type":"object","required":["path","mimeType","bytes"],"properties":{"path":{"type":"string"},"mimeType":{"type":"string"},"bytes":{"type":"number"}}}},
        "text":{"type":"string"},
        "jobId":{"type":"string"},
        "cost":{"type":"number"}}}
        """);

    /// <summary><c>generate_image</c>: generates images, saves them and returns their paths (and the images, for models that see images).</summary>
    public static ToolRegistration CreateImageTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = ImageToolName,
        Description = "Generate or edit images with an image model and save them to files. Returns the saved paths. Pass reference_images to " +
            "edit an image or keep a style or subject consistent. Defaults set with set_media_defaults apply unless overridden.",
        Parameters = ImageArguments,
        StructuredOutputSchema = FilesSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "reference_images"),
        Execute = async (arguments, api, context) =>
        {
            if (await api.MemoAsync(ResultMemo, context).ConfigureAwait(false) is JsonObjectValue saved) return Replayed(saved, "image");
            var (args, model) = await PrepareAsync(options, MediaKind.Image, arguments, context).ConfigureAwait(false);
            var env = ToolEnv.Require(api);
            var prompt = RequiredText(args, "prompt");
            var input = ImmutableArray.CreateBuilder<ImagesContentBlock>();
            input.Add(new ImagesTextBlock(prompt));
            foreach (var path in Strings(args, "reference_images"))
                input.Add(await ReadImageAsync(env, path, context).ConfigureAwait(false));
            var parameters = new ImageParameters
            {
                Count = Integer(args, "count"), Size = Text(args, "size"), AspectRatio = Text(args, "aspect_ratio"), Resolution = Text(args, "resolution"),
                Quality = Text(args, "quality"), Background = Text(args, "background"), OutputFormat = Text(args, "output_format"),
                OutputCompression = Integer(args, "output_compression", allowZero: true), Moderation = Text(args, "moderation"),
                InputFidelity = Text(args, "input_fidelity"), Seed = Integer(args, "seed", allowZero: true),
                Mask = Text(args, "mask") is { } maskPath ? await ReadImageAsync(env, maskPath, context).ConfigureAwait(false) : null,
                Options = await TierRoutingAsync(api, model, Options(args), context).ConfigureAwait(false),
            };
            var result = await options.Media.GenerateImagesAsync(model, new ImagesContext(input.ToImmutable()), parameters, null, context.CancellationToken)
                .ConfigureAwait(false);
            if (result.StopReason != ModelOperationStopReason.Stop) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "image generation failed"}");
            var images = result.Output.OfType<ImageContent>().ToList();
            var text = string.Join("\n", result.Output.OfType<ImagesTextBlock>().Select(block => block.Text));
            if (images.Count == 0) throw new InvalidOperationException($"{model.Spec} returned no images" + (text.Length > 0 ? ": " + text : ""));
            var paths = OutputPaths(Text(args, "output_path"), options.OutputDirectory, Stem("image", prompt), images.Select(image => Extension(image.MimeType)).ToList());
            var files = new List<JsonValue>();
            for (var i = 0; i < images.Count; i++)
            {
                var bytes = Convert.FromBase64String(images[i].Data);
                await SaveAsync(env, paths[i], bytes, context).ConfigureAwait(false);
                files.Add(FileEntry(paths[i], images[i].MimeType, bytes.Length));
            }
            var cost = result.Usage is { } usage && usage.Cost.Total > 0 ? (double)usage.Cost.Total : (double?)null;
            var summary = (JsonObjectValue)await api.MemoAsync(ResultMemo, Summary(model, files, text, null, cost), context).ConfigureAwait(false);
            var output = new List<JsonValue> { TextBlock(Describe("image", summary)) };
            if (options.ShowImages && await AcceptsImagesAsync(api, context).ConfigureAwait(false))
                output.AddRange(images.Take(4).Where(image => image.Data.Length <= ImageLimits.Default.MaxBytes)
                    .Select(image => JsonValue.Object([new("type", "image"), new("data", image.Data), new("mimeType", image.MimeType)])));
            return new ToolExecutionResult
            {
                Output = JsonValue.Array(output), StructuredOutput = summary, Details = summary,
                Usage = result.Usage is { } tokens ? (JsonObjectValue)JsonValue.FromData(ModelOperationJson.WriteUsage(tokens)) : null,
            };
        },
    });

    // ----- generate_video -----

    private static readonly JsonObjectValue VideoArguments = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","properties":{
        "prompt":{"type":"string","description":"The shot: subject, action, camera, style, and any dialogue or sound. Optional for models that animate an image alone."},
        "model":{"type":"string","description":"provider/model, e.g. openrouter/google/veo-3.1-fast, openrouter/kwaivgi/kling-v3.0-std or google/veo-3.0-generate-001. Omit for the default; list_media_models shows the choices."},
        "image":{"type":"string","description":"Path of an image to use as the first frame."},
        "last_frame":{"type":"string","description":"OpenRouter: path of an image to use as the last frame."},
        "references":{"type":"array","items":{"type":"string"},"description":"OpenRouter: paths or https URLs of reference images, audio or video (audio and video only for models that support them, such as Seedance 2)."},
        "duration_seconds":{"type":"number","description":"Clip length in seconds (model-specific, e.g. Veo 4, 6 or 8)."},
        "aspect_ratio":{"type":"string","description":"16:9, 9:16, 1:1, 4:3, 3:4, 3:2, 2:3, 21:9 or 9:21."},
        "resolution":{"type":"string","description":"360p, 480p, 720p, 768p, 1080p, 1K, 2K or 4K."},
        "size":{"type":"string","description":"OpenRouter: exact pixels WIDTHxHEIGHT, instead of resolution and aspect_ratio."},
        "generate_audio":{"type":"boolean","description":"OpenRouter: generate a soundtrack, where the model can."},
        "seed":{"type":"number","description":"Seed for repeatable output, where supported."},
        "negative_prompt":{"type":"string","description":"Google: what to avoid."},
        "previous_job_id":{"type":"string","description":"OpenRouter: the jobId of a completed video to edit or extend (models that support continuation)."},
        "options":{"type":"object","description":"Extra provider fields sent as they are, e.g. OpenRouter {\"provider\":{\"options\":{\"<slug>\":{...}}}}."},
        "output_path":{"type":"string","description":"File or directory to save to. Default: a new file under the media output directory."}}}
        """);

    /// <summary><c>generate_video</c>: submits a video job, waits for it, saves the video and returns its path. The job id is memoized, so
    /// a rerun after a restart resumes the same job.</summary>
    public static ToolRegistration CreateVideoTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = VideoToolName,
        Description = "Generate a video clip with a video model (this takes minutes) and save it to a file. Returns the saved path and a jobId " +
            "that previous_job_id can extend. Defaults set with set_media_defaults apply unless overridden.",
        Parameters = VideoArguments,
        StructuredOutputSchema = FilesSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "references"),
        Execute = async (arguments, api, context) =>
        {
            if (await api.MemoAsync(ResultMemo, context).ConfigureAwait(false) is JsonObjectValue saved) return Replayed(saved, "video");
            var (args, model) = await PrepareAsync(options, MediaKind.Video, arguments, context).ConfigureAwait(false);
            var env = ToolEnv.Require(api);
            var prompt = Text(args, "prompt") ?? "";
            var references = ImmutableArray.CreateBuilder<MediaReference>();
            foreach (var reference in Strings(args, "references"))
                references.Add(await ReadReferenceAsync(env, reference, context).ConfigureAwait(false));
            var request = new VideoRequest(prompt)
            {
                Image = Text(args, "image") is { } imagePath ? await ReadImageAsync(env, imagePath, context).ConfigureAwait(false) : null,
                LastFrame = Text(args, "last_frame") is { } lastPath ? await ReadImageAsync(env, lastPath, context).ConfigureAwait(false) : null,
                References = references.ToImmutable(),
                DurationSeconds = Integer(args, "duration_seconds"), AspectRatio = Text(args, "aspect_ratio"), Resolution = Text(args, "resolution"),
                Size = Text(args, "size"), NegativePrompt = Text(args, "negative_prompt"), Seed = Integer(args, "seed", allowZero: true),
                GenerateAudio = Flag(args, "generate_audio"), PreviousJobId = Text(args, "previous_job_id"), Options = Options(args),
            };
            var resume = await api.MemoAsync(JobMemo, context).ConfigureAwait(false) is JsonStringValue job ? job.Value : null;
            string? lastReport = null;
            var hooks = new VideoJobHooks
            {
                ResumeJobId = resume,
                OnSubmittedAsync = async (id, _) => ((JsonStringValue)await api.MemoAsync(JobMemo, id, context).ConfigureAwait(false)).Value,
                OnStatusAsync = async (status, _) =>
                {
                    var report = status.State + (status.Progress is { } progress ? $" {progress:0}%" : "");
                    if (report == lastReport) return;
                    lastReport = report;
                    var details = new List<KeyValuePair<string, JsonValue>> { new("model", model.Spec), new("state", Lower(status.State.ToString())) };
                    if (status.Progress is { } percent) details.Add(new("progress", percent));
                    await api.DetailsAsync(JsonValue.Object(details), context).ConfigureAwait(false);
                },
            };
            var media = new MediaOptions { PollIntervalMs = options.VideoPollIntervalMs, MaxWaitMs = options.VideoMaxWaitMs };
            var result = await options.Media.GenerateVideoAsync(model, request, media, hooks, context.CancellationToken).ConfigureAwait(false);
            if (result.Video is not { } video)
                throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "video generation failed"}" + (result.JobId is { } id ? $" (job {id})" : ""));
            var path = OutputPaths(Text(args, "output_path"), options.OutputDirectory, Stem("video", prompt.Length > 0 ? prompt : "clip"), [Extension(video.MimeType)])[0];
            await SaveAsync(env, path, video.Data, context).ConfigureAwait(false);
            var summary = Summary(model, [FileEntry(path, video.MimeType, video.Data.Length)], null, result.JobId, result.Cost);
            summary = (JsonObjectValue)await api.MemoAsync(ResultMemo, summary, context).ConfigureAwait(false);
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(Describe("video", summary))), StructuredOutput = summary, Details = summary };
        },
    });

    // ----- embed -----

    private static readonly JsonObjectValue EmbedParameters = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["input"],"properties":{
        "input":{"type":"array","items":{"type":"string"},"description":"Texts to embed."},
        "query":{"type":"string","description":"Rank the input texts by cosine similarity to this text instead of returning vectors."},
        "model":{"type":"string","description":"provider/model, e.g. openai/text-embedding-3-small, google/gemini-embedding-001 or openrouter/<id>. Omit for the default."},
        "dimensions":{"type":"number","description":"Shorter vectors, for models that support it."},
        "task_type":{"type":"string","description":"Gemini task type, e.g. RETRIEVAL_DOCUMENT, RETRIEVAL_QUERY or SEMANTIC_SIMILARITY."},
        "options":{"type":"object","description":"Extra provider fields sent as they are, e.g. OpenRouter {\"provider\":{\"order\":[\"openai\"]}} or {\"input_type\":\"search_query\"}."},
        "output_path":{"type":"string","description":"Write the vectors as JSON to this file."},
        "include_vectors":{"type":"boolean","description":"Also print the vectors in the result (large)."}}}
        """);

    private static readonly JsonObjectValue EmbedSchema = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["model","dimensions","vectors"],"properties":{
        "model":{"type":"string"},
        "dimensions":{"type":"number"},
        "vectors":{"type":"array","items":{"type":"array","items":{"type":"number"}}},
        "ranking":{"type":"array","items":{"type":"object","required":["index","score"],"properties":{"index":{"type":"number"},"score":{"type":"number"}}}},
        "path":{"type":"string"},
        "inputTokens":{"type":"number"}}}
        """);

    /// <summary><c>embed</c>: embeds texts; with <c>query</c>, ranks them by similarity to it. Programs get the vectors as structured
    /// output; <c>output_path</c> writes them to a JSON file.</summary>
    public static ToolRegistration CreateEmbedTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = EmbedToolName,
        Description = "Create text embeddings. With query, ranks the input texts by semantic similarity to it. With output_path, writes the " +
            "vectors to a JSON file for later search.",
        Parameters = EmbedParameters,
        StructuredOutputSchema = EmbedSchema,
        Replay = ToolReplay.Safe,
        PrepareArguments = arguments => WrapString(arguments, "input"),
        Execute = async (arguments, api, context) =>
        {
            var (args, model) = await PrepareAsync(options, MediaKind.Embedding, arguments, context).ConfigureAwait(false);
            var texts = Strings(args, "input");
            if (texts.Count == 0) throw new InvalidOperationException("input must list at least one text");
            var query = Text(args, "query");
            var request = new EmbeddingRequest(query is null ? [.. texts] : [query, .. texts])
            {
                Dimensions = Integer(args, "dimensions"), TaskType = Text(args, "task_type"), Options = Options(args),
            };
            var result = await options.Media.EmbedAsync(model, request, null, context.CancellationToken).ConfigureAwait(false);
            if (result.StopReason != ModelOperationStopReason.Stop) throw new InvalidOperationException($"{model.Spec}: {result.ErrorMessage ?? "embedding failed"}");
            var vectors = query is null ? result.Vectors : result.Vectors.RemoveAt(0);
            var dimensions = vectors.Length == 0 ? 0 : vectors[0].Length;
            var structured = new List<KeyValuePair<string, JsonValue>>
            {
                new("model", model.Spec), new("dimensions", dimensions), new("vectors", JsonValue.Array(vectors.Select(Vector))),
            };
            var text = new StringBuilder($"Embedded {texts.Count} text(s) with {model.Spec} ({dimensions} dimensions");
            if (result.InputTokens is { } tokens) { text.Append($", {tokens} tokens"); structured.Add(new("inputTokens", tokens)); }
            text.Append(").");
            if (query is not null)
            {
                var ranking = vectors.Select((vector, index) => (Index: index, Score: Cosine(result.Vectors[0], vector)))
                    .OrderByDescending(item => item.Score).ToList();
                structured.Add(new("ranking", JsonValue.Array(ranking.Select(item => (JsonValue)JsonValue.Object([new("index", item.Index), new("score", Round(item.Score))])))));
                text.Append("\nRanked by similarity to the query:");
                foreach (var (index, score) in ranking)
                    text.Append(CultureInfo.InvariantCulture, $"\n{score:0.0000}  [{index}] {Preview(texts[index])}");
            }
            if (Text(args, "output_path") is { } outputPath)
            {
                var document = JsonValue.Object([new("model", model.Spec), new("dimensions", dimensions),
                    new("items", JsonValue.Array(texts.Select((input, index) => (JsonValue)JsonValue.Object([new("text", input), new("embedding", Vector(vectors[index]))]))))]);
                await SaveAsync(ToolEnv.Require(api), outputPath, Encoding.UTF8.GetBytes(document.ToString()), context).ConfigureAwait(false);
                structured.Add(new("path", outputPath));
                text.Append($"\nWrote the vectors to {outputPath}.");
            }
            if (Flag(args, "include_vectors") == true)
                text.Append('\n').Append(JsonValue.Array(vectors.Select(Vector)).ToString());
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(text.ToString())), StructuredOutput = JsonValue.Object(structured) };
        },
    });

    // ----- list_media_models -----

    private static readonly JsonObjectValue ModelsParameters = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","properties":{
        "kind":{"type":"string","description":"image, video, speech, transcription, music, embedding, rerank or decision; omit for all."},
        "all":{"type":"boolean","description":"Also list every model OpenRouter offers for the kind, not only the built-in choices."}}}
        """);

    /// <summary><c>list_media_models</c>: the media models whose providers are configured, with the defaults marked; with <c>all</c>, every
    /// OpenRouter model of the kind.</summary>
    public static ToolRegistration CreateModelsTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = ModelsToolName,
        Description = "List the media models that are configured (their provider has credentials) for each kind: image, video, speech, " +
            "transcription, music, embedding, rerank and decision, with the defaults and default options marked. all lists every OpenRouter model.",
        Parameters = ModelsParameters,
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var args = (JsonObjectValue)arguments;
            var kinds = Text(args, "kind") is { } named ? [ParseKind(named)] : Enum.GetValues<MediaKind>();
            var all = Flag(args, "all") == true;
            var text = new StringBuilder();
            foreach (var kind in kinds)
            {
                var available = await options.Media.GetAvailableAsync(kind, context.CancellationToken).ConfigureAwait(false);
                var defaults = options.Defaults.Get(kind);
                var fallback = defaults.Model ?? available.FirstOrDefault()?.Spec;
                text.Append(text.Length == 0 ? "" : "\n").Append(MediaCatalog.Name(kind)).Append(" models:");
                if (available.Length == 0) text.Append("\n  (none configured: set OPENAI_API_KEY, GEMINI_API_KEY or OPENROUTER_API_KEY, or log in)");
                foreach (var model in available)
                {
                    text.Append($"\n  {model.Spec}");
                    if (model.Name.Length != 0) text.Append($" ({model.Name})");
                    if (model.Spec == fallback) text.Append(" [default]");
                    if (kind is MediaKind.Image or MediaKind.Video && model.AcceptsImages) text.Append(kind == MediaKind.Image ? " [reference images]" : " [first frame]");
                    if (model.Dimensions is { } dimensions) text.Append($" [{dimensions} dims]");
                }
                if (defaults.Model is { } chosen && available.All(model => model.Spec != chosen)) text.Append($"\n  {chosen} [default]");
                if (defaults.Options.Count > 0) text.Append("\n  default options: ").Append(defaults.Options.ToString());
                if (all)
                {
                    var discovered = await options.Media.DiscoverAsync(kind, "openrouter", context.CancellationToken).ConfigureAwait(false);
                    if (discovered.Length > 0)
                        text.Append($"\n  all OpenRouter {MediaCatalog.Name(kind)} models ({discovered.Length}): ")
                            .Append(string.Join(", ", discovered.Select(model => model.Spec)));
                }
            }
            text.Append("\nAny model of a configured provider can be named as provider/model.");
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(text.ToString())) };
        },
    });

    // ----- set_media_defaults -----

    private static readonly JsonObjectValue DefaultsParameters = (JsonObjectValue)JsonValue.Parse("""
        {"type":"object","required":["kind"],"properties":{
        "kind":{"type":"string","description":"image, video, speech, transcription, music, embedding, rerank or decision."},
        "model":{"type":"string","description":"provider/model to use when a call names none; an empty string removes the default model."},
        "options":{"type":"object","description":"Default arguments of that kind's tool, by its argument names, merged into the current defaults; a null value removes one. E.g. image {\"background\":\"transparent\",\"output_format\":\"png\"}, video {\"resolution\":\"720p\",\"generate_audio\":true}, speech {\"voice\":\"alloy\"}."},
        "replace":{"type":"boolean","description":"Replace the default options instead of merging into them."},
        "clear":{"type":"boolean","description":"Remove every default of the kind."},
        "scope":{"type":"string","description":"global (default; every project) or project (this project's .pi/settings.json)."}}}
        """);

    /// <summary><c>set_media_defaults</c>: changes a kind's default model and arguments and saves them in settings.</summary>
    public static ToolRegistration CreateDefaultsTool(MediaToolsOptions options) => Define.Tool(new ToolRegistration
    {
        Name = DefaultsToolName,
        Description = "Set the default model and default options for a media kind (image, video, speech, transcription, music, embedding, " +
            "rerank, decision), saved in settings. Calls still override any default. Use when the user asks to make something the default.",
        Parameters = DefaultsParameters,
        Replay = ToolReplay.Safe,
        Execute = async (arguments, api, context) =>
        {
            var args = (JsonObjectValue)arguments;
            var kind = ParseKind(RequiredText(args, "kind"));
            var scope = Text(args, "scope")?.ToLowerInvariant() ?? "global";
            if (scope is not ("global" or "project")) throw new InvalidOperationException("scope must be global or project");
            var current = options.Defaults.Get(kind);
            MediaDefault? next;
            if (Flag(args, "clear") == true) next = null;
            else
            {
                var model = current.Model;
                if (args.TryGetValue("model", out var modelValue) && modelValue is JsonStringValue modelText)
                {
                    model = modelText.Value.Trim().Length == 0 ? null : modelText.Value.Trim();
                    if (model is not null && !options.Media.TryResolve(kind, model, out _, out var error)) throw new InvalidOperationException(error);
                }
                var merged = Flag(args, "replace") == true ? JsonObjectValue.Empty : current.Options;
                if (args.TryGetValue("options", out var optionsValue) && optionsValue is JsonObjectValue changes)
                    foreach (var (name, value) in changes)
                        merged = value is JsonNullValue ? merged.Remove(name) : merged.SetItem(name, value);
                next = new MediaDefault(model, merged);
            }
            await options.Defaults.SetAsync(kind, next, scope, context.CancellationToken).ConfigureAwait(false);
            var now = options.Defaults.Get(kind);
            var text = now.IsEmpty
                ? $"Cleared the {MediaCatalog.Name(kind)} defaults ({scope})."
                : $"Saved the {MediaCatalog.Name(kind)} defaults ({scope}): model {now.Model ?? "(first available)"}, options {now.Options}.";
            return new ToolExecutionResult { Output = JsonValue.Array(TextBlock(text)) };
        },
    });

    // ----- shared -----

    /// <summary>The call's arguments over the kind's default arguments, and the model: the call's <c>model</c>, else the default model,
    /// else the first available model of the kind.</summary>
    private static async Task<(JsonObjectValue Args, MediaModel Model)> PrepareAsync(MediaToolsOptions options, MediaKind kind, JsonValue arguments,
        DurableContext context)
    {
        var defaults = options.Defaults.Get(kind);
        var args = defaults.Options;
        foreach (var (name, value) in (JsonObjectValue)arguments) args = args.SetItem(name, value);
        var spec = Text(args, "model") ?? defaults.Model;
        if (spec is null)
        {
            var available = await options.Media.GetAvailableAsync(kind, context.CancellationToken).ConfigureAwait(false);
            return (args, available.FirstOrDefault() ?? throw new InvalidOperationException(
                $"No {MediaCatalog.Name(kind)} model is configured: set an API key for a provider that has one (OpenRouter has every kind), or name a model as provider/model."));
        }
        return options.Media.TryResolve(kind, spec, out var model, out var error) ? (args, model) : throw new InvalidOperationException(error);
    }

    /// <summary>
    /// PiSharp-only (decision 0007): OpenRouter's image endpoints have no service tiers, so the conversation's tier picks their provider
    /// routing instead: economy sorts by price, priority by throughput, and fast and ultrafast by latency. A call that sets
    /// <c>provider</c> itself keeps it.
    /// </summary>
    private static async Task<PiSharp.Contracts.JsonData?> TierRoutingAsync(IToolExecutionApi api, MediaModel model, PiSharp.Contracts.JsonData? options,
        DurableContext context)
    {
        if (model.Provider != "openrouter") return options;
        if (options is { Value: var value } && value.TryGetProperty("provider", out _)) return options;
        var sort = PiSharp.AI.ServiceTiers.Normalize((await api.AgentAsync(context).ConfigureAwait(false)).ServiceTier) switch
        {
            PiSharp.AI.ServiceTiers.Economy => "price",
            PiSharp.AI.ServiceTiers.Priority => "throughput",
            PiSharp.AI.ServiceTiers.Fast or PiSharp.AI.ServiceTiers.Ultrafast => "latency",
            _ => null,
        };
        if (sort is null) return options;
        var node = options is null ? [] : System.Text.Json.Nodes.JsonNode.Parse(options.ToString())!.AsObject();
        node["provider"] = new System.Text.Json.Nodes.JsonObject { ["sort"] = sort };
        return PiSharp.Contracts.JsonData.Parse(node.ToJsonString());
    }

    private static MediaKind ParseKind(string text) => MediaCatalog.Parse(text)
        ?? throw new InvalidOperationException($"Unknown kind \"{text}\": use image, video, speech, transcription, music, embedding, rerank or decision");

    /// <summary>Whether the conversation's model takes image input; an unknown model counts as one that does, as the read tool assumes.</summary>
    private static async Task<bool> AcceptsImagesAsync(IToolExecutionApi api, DurableContext context)
    {
        var reference = (await api.AgentAsync(context).ConfigureAwait(false)).Model;
        var model = reference is null ? null : api.Models.GetModel(reference.Provider, reference.ModelId);
        return model is null || model.Input.Contains("image");
    }

    private static async Task<ImageContent> ReadImageAsync(IExecutionEnv env, string path, DurableContext context)
    {
        var bytes = await ReadFileAsync(env, path, context).ConfigureAwait(false);
        var mime = ImageDetection.DetectSupportedImageMimeType(bytes) ?? throw new InvalidOperationException($"{path} is not a PNG, JPEG, GIF or WebP image");
        return new ImageContent(Convert.ToBase64String(bytes), mime);
    }

    private static async Task<byte[]> ReadFileAsync(IExecutionEnv env, string path, DurableContext context)
    {
        var absolute = await PathUtils.ResolveReadToolPathAsync(env, path, context).ConfigureAwait(false);
        return (await env.ReadBinaryFileAsync(absolute, context).ConfigureAwait(false)).GetOrThrow();
    }

    /// <summary>A reference to send: an <c>http(s)</c> URL as it is, else a file read into a <c>data:</c> URL, its kind from its type.</summary>
    private static async Task<MediaReference> ReadReferenceAsync(IExecutionEnv env, string reference, DurableContext context)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            return new MediaReference(KindOfMime(MimeOfPath(url.AbsolutePath)), reference);
        var bytes = await ReadFileAsync(env, reference, context).ConfigureAwait(false);
        var mime = ImageDetection.DetectSupportedImageMimeType(bytes) ?? MimeOfPath(reference);
        return MediaReference.FromBytes(KindOfMime(mime), bytes, mime);
    }

    private static MediaReferenceKind KindOfMime(string mime) =>
        mime.StartsWith("audio/", StringComparison.Ordinal) ? MediaReferenceKind.Audio
        : mime.StartsWith("video/", StringComparison.Ordinal) ? MediaReferenceKind.Video : MediaReferenceKind.Image;

    internal static string MimeOfPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".m4a" => "audio/mp4", ".ogg" or ".oga" or ".opus" => "audio/ogg", ".flac" => "audio/flac",
        ".aac" => "audio/aac", ".mp4" or ".m4v" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    private static async Task SaveAsync(IExecutionEnv env, string path, ReadOnlyMemory<byte> data, DurableContext context)
    {
        var absolute = await PathUtils.ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
        if (Path.GetDirectoryName(absolute) is { Length: > 0 } directory)
            (await env.CreateDirAsync(directory, new CreateDirOptions(), context).ConfigureAwait(false)).GetOrThrow();
        (await env.WriteFileAsync(absolute, data, context).ConfigureAwait(false)).GetOrThrow();
    }

    /// <summary>
    /// One path per output. A requested path that ends in a separator or has no extension is a directory; a file path gets <c>-2</c>,
    /// <c>-3</c>, … for the outputs after the first. Without a request, files go to <paramref name="directory"/> named by
    /// <paramref name="stem"/>.
    /// </summary>
    internal static IReadOnlyList<string> OutputPaths(string? requested, string directory, string stem, IReadOnlyList<string> extensions)
    {
        var paths = new List<string>(extensions.Count);
        var isDirectory = requested is null || requested.EndsWith('/') || requested.EndsWith('\\') || Path.GetExtension(requested).Length == 0;
        for (var i = 0; i < extensions.Count; i++)
        {
            var suffix = extensions.Count > 1 ? $"-{i + 1}" : "";
            if (isDirectory)
                paths.Add(Path.Join(requested ?? directory, stem + suffix + extensions[i]).Replace('\\', '/'));
            else
            {
                var extension = Path.GetExtension(requested!);
                paths.Add(i == 0 ? requested! : requested![..^extension.Length] + $"-{i + 1}" + extension);
            }
        }
        return paths;
    }

    /// <summary><c>image-20261017-142530-a-red-fox</c>: the kind, the UTC time and the prompt's first words.</summary>
    internal static string Stem(string kind, string prompt)
    {
        var words = new string(prompt.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5);
        var slug = string.Join('-', words);
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return $"{kind}-{DateTime.UtcNow:yyyyMMdd-HHmmss}" + (slug.Length > 0 ? "-" + slug : "");
    }

    private static string Extension(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/png" => ".png",
        "image/svg+xml" => ".svg",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "audio/aac" => ".aac",
        "audio/mp4" => ".m4a",
        "audio/pcm" => ".pcm",
        _ when mime.StartsWith("video/", StringComparison.Ordinal) => ".mp4",
        _ when mime.StartsWith("audio/", StringComparison.Ordinal) => ".audio",
        _ => ".bin",
    };

    private static JsonObjectValue FileEntry(string path, string mime, long bytes) =>
        JsonValue.Object([new("path", path), new("mimeType", mime), new("bytes", bytes)]);

    private static JsonObjectValue Summary(MediaModel model, IEnumerable<JsonValue> files, string? text, string? jobId, double? cost)
    {
        var fields = new List<KeyValuePair<string, JsonValue>> { new("model", model.Spec), new("files", JsonValue.Array(files)) };
        if (text is { Length: > 0 }) fields.Add(new("text", text));
        if (jobId is not null) fields.Add(new("jobId", jobId));
        if (cost is { } usd) fields.Add(new("cost", usd));
        return JsonValue.Object(fields);
    }

    private static string Describe(string kind, JsonObjectValue summary)
    {
        var files = ((JsonArrayValue)summary["files"]).OfType<JsonObjectValue>().ToList();
        var text = new StringBuilder($"Saved {files.Count} {kind}{(files.Count == 1 ? "" : "s")} from {((JsonStringValue)summary["model"]).Value}:");
        foreach (var file in files)
            text.Append(CultureInfo.InvariantCulture,
                $"\n- {((JsonStringValue)file["path"]).Value} ({((JsonStringValue)file["mimeType"]).Value}, {Size(((JsonNumberValue)file["bytes"]).Value)})");
        if (summary.TryGetValue("jobId", out var job) && job is JsonStringValue jobId) text.Append("\njobId: ").Append(jobId.Value);
        if (summary.TryGetValue("cost", out var cost) && cost is JsonNumberValue usd) text.Append(CultureInfo.InvariantCulture, $"\ncost: ${usd.Value:0.####}");
        if (summary.TryGetValue("text", out var note) && note is JsonStringValue words) text.Append("\nModel note: ").Append(words.Value);
        return text.ToString();
    }

    private static ToolExecutionResult Replayed(JsonObjectValue summary, string kind) => new()
    {
        Output = JsonValue.Array(TextBlock(Describe(kind, summary))), StructuredOutput = summary, Details = summary,
    };

    private static string Size(double bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024):0.0} MB" : $"{bytes / 1024:0} KB";

    private static JsonObjectValue TextBlock(string text) => JsonValue.Object([new("type", "text"), new("text", text)]);

    private static JsonArrayValue Vector(float[] vector) => JsonValue.Array(vector.Select(value => (JsonValue)Round(value)));

    /// <summary>A float as its shortest round-trip decimal, so JSON does not carry binary64 noise.</summary>
    private static double Round(float value) => double.Parse(value.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static double Round(double value) => Math.Round(value, 6);

    internal static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length) throw new InvalidOperationException("Vectors of different sizes cannot be compared");
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++) { dot += left[i] * (double)right[i]; a += left[i] * (double)left[i]; b += right[i] * (double)right[i]; }
        return a == 0 || b == 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private static string Preview(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 100 ? line : line[..100] + "…";
    }

    private static string Lower(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// <summary>Repairs a lone string where an array of strings belongs.</summary>
    private static JsonValue WrapString(JsonValue arguments, string name) =>
        arguments is JsonObjectValue args && args.TryGetValue(name, out var value) && value is JsonStringValue single
            ? args.SetItem(name, JsonValue.Array(single)) : arguments;

    private static string? Text(JsonObjectValue args, string name) =>
        args.TryGetValue(name, out var value) && value is JsonStringValue text && text.Value.Trim().Length > 0 ? text.Value.Trim() : null;

    private static string RequiredText(JsonObjectValue args, string name) =>
        Text(args, name) ?? throw new InvalidOperationException($"{name} must not be empty");

    private static bool? Flag(JsonObjectValue args, string name) =>
        args.TryGetValue(name, out var value) && value is JsonBooleanValue flag ? flag.Value : null;

    private static double? Number(JsonObjectValue args, string name) =>
        args.TryGetValue(name, out var value) && value is JsonNumberValue number && double.IsFinite(number.Value) ? number.Value : null;

    private static int? Integer(JsonObjectValue args, string name, bool allowZero = false)
    {
        if (!args.TryGetValue(name, out var value) || value is not JsonNumberValue number) return null;
        if (!double.IsFinite(number.Value) || Math.Floor(number.Value) != number.Value || number.Value < (allowZero ? 0 : 1) || number.Value > int.MaxValue)
            throw new InvalidOperationException($"{name} must be a {(allowZero ? "non-negative" : "positive")} integer");
        return (int)number.Value;
    }

    private static PiSharp.Contracts.JsonData? Options(JsonObjectValue args) =>
        args.TryGetValue("options", out var value) && value is JsonObjectValue options ? options.ToData() : null;

    private static IReadOnlyList<string> Strings(JsonObjectValue args, string name) =>
        args.TryGetValue(name, out var value) && value is JsonArrayValue items
            ? [.. items.OfType<JsonStringValue>().Select(item => item.Value).Where(item => item.Trim().Length > 0)]
            : [];
}
