// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts (ClassifierContext, ClassifierResult,
// ImagesContext and AssistantImages JSON shapes).
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PiSharp.Contracts.ModelOperations;

/// <summary>The upstream JSON shapes of classifier and image requests and results, for bridges (codemode scripts, Node
/// extensions, RPC) that exchange them as JSON. Object keys follow JavaScript property order: array-index keys first in
/// ascending order, then the rest in source order; a repeated key keeps its first position and its last value.</summary>
public static class ModelOperationJson
{
    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Parses <c>{ state, images?, questions }</c>. Throws <see cref="FormatException"/> for another shape.</summary>
    public static ClassifierContext ParseClassifierContext(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("A classifier context must be an object.");
        if (!value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            throw new FormatException("context.state must be an object.");
        var images = ImmutableArray<ImageContent>.Empty;
        if (value.TryGetProperty("images", out var imageList) && imageList.ValueKind != JsonValueKind.Undefined)
        {
            if (imageList.ValueKind != JsonValueKind.Array) throw new FormatException("context.images must be an array.");
            images = [.. imageList.EnumerateArray().Select((image, index) => ParseImage(image) ??
                throw new FormatException($"context.images[{index}] must be an image block."))];
        }
        if (!value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Object)
            throw new FormatException("context.questions must map question IDs to questions.");
        var parsed = ImmutableArray.CreateBuilder<KeyValuePair<string, ClassifierQuestion>>();
        foreach (var (id, question) in ObjectEntries(questions)) parsed.Add(new(id, ParseQuestion(id, question)));
        return new(JsonData.FromElement(state), parsed.ToImmutable(), images);
    }

    /// <summary>Parses <c>{ input: [text | image blocks] }</c>. Throws <see cref="FormatException"/> for another shape.</summary>
    public static ImagesContext ParseImagesContext(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array)
            throw new FormatException("An images context must be { input: [...] }.");
        return new([.. input.EnumerateArray().Select((block, index) => ParseBlock(block) ??
            throw new FormatException($"context.input[{index}] must be a text or image block."))]);
    }

    public static JsonData WriteClassifierContext(ClassifierContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("state"); context.State.Value.WriteTo(writer);
            if (!context.Images.IsDefault)
            {
                writer.WriteStartArray("images");
                foreach (var image in context.Images) WriteBlock(writer, image);
                writer.WriteEndArray();
            }
            writer.WriteStartObject("questions");
            foreach (var (id, question) in Ordered(context.Questions))
            {
                writer.WriteStartObject(id);
                switch (question)
                {
                    case ClassifierChoiceQuestion choice:
                        writer.WriteString("type", "choice"); writer.WriteString("instructions", choice.Instructions);
                        writer.WriteStartObject("criteria");
                        foreach (var (label, meaning) in Ordered(choice.Criteria)) writer.WriteString(label, meaning);
                        writer.WriteEndObject();
                        break;
                    case ClassifierScoreQuestion score:
                        writer.WriteString("type", "score"); writer.WriteString("instructions", score.Instructions);
                        writer.WriteStartArray("criteria");
                        foreach (var level in score.Criteria) writer.WriteStringValue(level);
                        writer.WriteEndArray();
                        break;
                    case ClassifierBoolQuestion boolean:
                        writer.WriteString("type", "bool"); writer.WriteString("instructions", boolean.Instructions);
                        writer.WriteStartObject("criteria");
                        writer.WriteString("true", boolean.True); writer.WriteString("false", boolean.False);
                        writer.WriteEndObject();
                        break;
                    default: throw new ArgumentException("Unsupported classifier question.");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <summary><c>{ api, provider, model, answers, stopReason, timestamp, usage?, errorMessage? }</c>.</summary>
    /// <summary>Parses a ClassifierResult object (an extension's classifier provider returns it). Throws <see cref="FormatException"/>.</summary>
    public static ClassifierResult ParseClassifierResult(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("A classifier result must be an object.");
        var answers = ImmutableArray.CreateBuilder<KeyValuePair<string, ClassifierAnswer>>();
        if (value.TryGetProperty("answers", out var list) && list.ValueKind == JsonValueKind.Object)
            foreach (var (id, answer) in ObjectEntries(list))
            {
                double Num(string name) => answer.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number ? number.GetDouble() : 0;
                answers.Add(new(id, (answer.TryGetProperty("type", out var type) ? type.GetString() : null) switch
                {
                    "choice" => new ClassifierChoiceAnswer(answer.GetProperty("choice").GetString() ?? "",
                        answer.TryGetProperty("probabilities", out var probabilities) && probabilities.ValueKind == JsonValueKind.Object
                            ? [.. ObjectEntries(probabilities).Select(entry => KeyValuePair.Create(entry.Key, entry.Value.GetDouble()))] : [], Num("confidence")),
                    "score" => new ClassifierScoreAnswer(Num("score"), Num("confidence")),
                    "bool" => new ClassifierBoolAnswer(Num("probability")),
                    _ => throw new FormatException($"Unsupported classifier answer type for {id}.")
                }));
            }
        return new(Str(value, "api"), Str(value, "provider"), Str(value, "model"), answers.ToImmutable(), StopReason(value),
            value.TryGetProperty("timestamp", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            Usage = value.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? PiWireJson.ReadUsageObject(usage) : null,
            ErrorMessage = value.TryGetProperty("errorMessage", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null
        };
    }

    /// <summary>Parses an AssistantImages object (an extension's image provider returns it). Throws <see cref="FormatException"/>.</summary>
    public static AssistantImages ParseAssistantImages(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("An images result must be an object.");
        ImmutableArray<ImagesContentBlock> output = value.TryGetProperty("output", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select((block, index) => ParseBlock(block) ?? throw new FormatException($"output[{index}] must be a text or image block."))] : [];
        return new(Str(value, "api"), Str(value, "provider"), Str(value, "model"), output, StopReason(value),
            value.TryGetProperty("timestamp", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            ResponseId = value.TryGetProperty("responseId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Usage = value.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? PiWireJson.ReadUsageObject(usage) : null,
            ErrorMessage = value.TryGetProperty("errorMessage", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null
        };
    }

    private static string Str(JsonElement value, string name) => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "";
    private static ModelOperationStopReason StopReason(JsonElement value) =>
        (value.TryGetProperty("stopReason", out var reason) ? reason.GetString() : null) switch
        { "error" => ModelOperationStopReason.Error, "aborted" => ModelOperationStopReason.Aborted, _ => ModelOperationStopReason.Stop };

    public static JsonData WriteClassifierResult(ClassifierResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("api", result.Api); writer.WriteString("provider", result.Provider); writer.WriteString("model", result.Model);
            writer.WriteStartObject("answers");
            foreach (var (id, answer) in Ordered(result.Answers.IsDefault ? [] : result.Answers))
            {
                writer.WriteStartObject(id);
                switch (answer)
                {
                    case ClassifierChoiceAnswer choice:
                        writer.WriteString("type", "choice"); writer.WriteString("choice", choice.Choice);
                        writer.WriteStartObject("probabilities");
                        foreach (var (label, probability) in Ordered(choice.Probabilities)) { writer.WritePropertyName(label); Number(writer, probability); }
                        writer.WriteEndObject();
                        writer.WritePropertyName("confidence"); Number(writer, choice.Confidence);
                        break;
                    case ClassifierScoreAnswer score:
                        writer.WriteString("type", "score");
                        writer.WritePropertyName("score"); Number(writer, score.Score);
                        writer.WritePropertyName("confidence"); Number(writer, score.Confidence);
                        break;
                    case ClassifierBoolAnswer boolean:
                        writer.WriteString("type", "bool");
                        writer.WritePropertyName("probability"); Number(writer, boolean.Probability);
                        break;
                    default: throw new ArgumentException("Unsupported classifier answer.");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteString("stopReason", StopReasonName(result.StopReason));
            writer.WriteNumber("timestamp", result.Timestamp);
            if (result.Usage is { } usage) { writer.WritePropertyName("usage"); WriteUsage(writer, usage); }
            if (result.ErrorMessage is { } error) writer.WriteString("errorMessage", error);
            writer.WriteEndObject();
        });
    }

    public static JsonData WriteImagesContext(ImagesContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("input");
            foreach (var block in context.Input.IsDefault ? [] : context.Input) WriteBlock(writer, block);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary><c>{ api, provider, model, output, stopReason, timestamp, responseId?, usage?, errorMessage? }</c>.</summary>
    public static JsonData WriteAssistantImages(AssistantImages result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("api", result.Api); writer.WriteString("provider", result.Provider); writer.WriteString("model", result.Model);
            writer.WriteStartArray("output");
            foreach (var block in result.Output.IsDefault ? [] : result.Output) WriteBlock(writer, block);
            writer.WriteEndArray();
            writer.WriteString("stopReason", StopReasonName(result.StopReason));
            writer.WriteNumber("timestamp", result.Timestamp);
            if (result.ResponseId is { } id) writer.WriteString("responseId", id);
            if (result.Usage is { } usage) { writer.WritePropertyName("usage"); WriteUsage(writer, usage); }
            if (result.ErrorMessage is { } error) writer.WriteString("errorMessage", error);
            writer.WriteEndObject();
        });
    }

    /// <summary><c>{ input, output, cacheRead, cacheWrite, totalTokens, cost }</c>; the cost is the exact binary64
    /// snapshot when the producer recorded one.</summary>
    public static JsonData WriteUsage(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return Write(writer => WriteUsage(writer, usage));
    }

    public static string StopReasonName(ModelOperationStopReason reason) => reason switch
    {
        ModelOperationStopReason.Stop => "stop", ModelOperationStopReason.Aborted => "aborted", _ => "error"
    };

    /// <summary>JavaScript own-property order of a JSON object: duplicates keep their first position and last value,
    /// then array-index keys move first in ascending numeric order.</summary>
    public static ImmutableArray<KeyValuePair<string, JsonElement>> ObjectEntries(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a JSON object.");
        return Ordered(value.EnumerateObject().Select(property => KeyValuePair.Create(property.Name, property.Value)));
    }

    /// <summary>The same JavaScript own-property order for already materialized entries.</summary>
    public static ImmutableArray<KeyValuePair<string, T>> Ordered<T>(IEnumerable<KeyValuePair<string, T>> entries)
    {
        var keys = new List<string>(); var values = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var (key, value) in entries) { if (!values.ContainsKey(key)) keys.Add(key); values[key] = value; }
        return [.. keys.Select((key, index) => (key, index, arrayIndex: ArrayIndex(key)))
            .OrderBy(item => item.arrayIndex is null ? 1 : 0).ThenBy(item => item.arrayIndex ?? 0).ThenBy(item => item.index)
            .Select(item => KeyValuePair.Create(item.key, values[item.key]))];
    }

    private static uint? ArrayIndex(string name)
    {
        // ECMAScript array index: canonical decimal of an integer in [0, 2^32 - 2].
        if (name.Length is 0 or > 10 || name.Length > 1 && name[0] == '0' || !name.All(char.IsAsciiDigit)) return null;
        return ulong.TryParse(name, out var value) && value < uint.MaxValue ? (uint)value : null;
    }

    private static ClassifierQuestion ParseQuestion(string id, JsonElement question)
    {
        var at = $"context.questions.{id}";
        if (question.ValueKind != JsonValueKind.Object) throw new FormatException($"{at} must be a question object.");
        if (!question.TryGetProperty("instructions", out var instructions) || instructions.ValueKind != JsonValueKind.String)
            throw new FormatException($"{at}.instructions must be a string.");
        question.TryGetProperty("criteria", out var criteria);
        var type = question.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString() : null;
        switch (type)
        {
            case "choice":
                if (criteria.ValueKind != JsonValueKind.Object || ObjectEntries(criteria).Any(entry => entry.Value.ValueKind != JsonValueKind.String))
                    throw new FormatException($"{at} is a \"choice\" question, so criteria must map each label to its meaning.");
                return new ClassifierChoiceQuestion(instructions.GetString()!,
                    [.. ObjectEntries(criteria).Select(entry => KeyValuePair.Create(entry.Key, entry.Value.GetString()!))]);
            case "score":
                if (criteria.ValueKind != JsonValueKind.Array || criteria.EnumerateArray().Any(level => level.ValueKind != JsonValueKind.String))
                    throw new FormatException($"{at} is a \"score\" question, so criteria must list the levels as strings, lowest first.");
                return new ClassifierScoreQuestion(instructions.GetString()!, [.. criteria.EnumerateArray().Select(level => level.GetString()!)]);
            case "bool":
                if (criteria.ValueKind != JsonValueKind.Object || !criteria.TryGetProperty("true", out var yes) || yes.ValueKind != JsonValueKind.String ||
                    !criteria.TryGetProperty("false", out var no) || no.ValueKind != JsonValueKind.String)
                    throw new FormatException($"{at} is a \"bool\" question, so criteria must be {{ true: string, false: string }}.");
                return new ClassifierBoolQuestion(instructions.GetString()!, yes.GetString()!, no.GetString()!);
            default: throw new FormatException($"{at}.type must be \"choice\", \"score\", or \"bool\".");
        }
    }

    private static ImageContent? ParseImage(JsonElement value) => ParseBlock(value) as ImageContent;

    private static ImagesContentBlock? ParseBlock(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type)) return null;
        if (type.ValueEquals("text") && value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return new ImagesTextBlock(text.GetString()!);
        if (type.ValueEquals("image") && value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String &&
            value.TryGetProperty("mimeType", out var mime) && mime.ValueKind == JsonValueKind.String)
            return new ImageContent(data.GetString()!, mime.GetString()!);
        return null;
    }

    private static void WriteBlock(Utf8JsonWriter writer, ImagesContentBlock block)
    {
        writer.WriteStartObject();
        switch (block)
        {
            case ImagesTextBlock text: writer.WriteString("type", "text"); writer.WriteString("text", text.Text); break;
            case ImageContent image:
                writer.WriteString("type", "image"); writer.WriteString("data", image.Data); writer.WriteString("mimeType", image.MimeType); break;
            default: throw new ArgumentException("Unsupported content block.");
        }
        writer.WriteEndObject();
    }

    private static void WriteUsage(Utf8JsonWriter writer, TokenUsage usage)
    {
        writer.WriteStartObject();
        writer.WriteNumber("input", usage.Input); writer.WriteNumber("output", usage.Output);
        writer.WriteNumber("cacheRead", usage.CacheRead); writer.WriteNumber("cacheWrite", usage.CacheWrite);
        writer.WriteNumber("totalTokens", usage.TotalTokens);
        writer.WritePropertyName("cost");
        if (usage.Cost.SourceBinary64Cost is { } exact) exact.Value.WriteTo(writer);
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("input", usage.Cost.Input); writer.WriteNumber("output", usage.Cost.Output);
            writer.WriteNumber("cacheRead", usage.Cost.CacheRead); writer.WriteNumber("cacheWrite", usage.Cost.CacheWrite);
            writer.WriteNumber("total", usage.Cost.Total);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // JSON.stringify writes null for NaN and the infinities.
    private static void Number(Utf8JsonWriter writer, double value)
    {
        if (double.IsFinite(value)) writer.WriteNumberValue(value); else writer.WriteNullValue();
    }

    // JSON.stringify output: JavaScript number text and property order.
    private static readonly Compatibility.EcmaScriptJsonProjectionOptions Projection = new(MaximumInputCharacters: int.MaxValue,
        MaximumInputBytes: int.MaxValue, MaximumOutputCharacters: int.MaxValue, MaximumOutputBytes: int.MaxValue, MaximumNodes: int.MaxValue,
        MaximumPropertiesPerObject: int.MaxValue, MaximumNumbers: int.MaxValue, MaximumTotalNumberCharacters: int.MaxValue,
        MaximumStringCharacters: int.MaxValue, MaximumDepth: 64);

    private static JsonData Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Writer)) write(writer);
        return JsonData.Parse(Compatibility.EcmaScriptJsonProjection.Project(Encoding.UTF8.GetString(buffer.ToArray()), Projection));
    }
}
