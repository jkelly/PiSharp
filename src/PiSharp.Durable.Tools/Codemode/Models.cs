// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/codemode/models.ts.
// - pi-ai's Models (api.models) is IDurableModels' catalog, classifier and image members (GetModelsOfType, GetAvailableOfTypeAsync,
//   GetModelOfType, ClassifyAsync, GenerateImagesAsync); models, contexts and results are pi-ai wire JSON. A host whose IDurableModels
//   keeps the members' defaults has no classifier or image models: the catalog lists none, so scripts get upstream's "Unknown ... model"
//   errors.
// - A script's argument list is JSON: an `undefined` the script passed is absent (a C# null) at the end of the list and JSON null inside it,
//   as the sandbox's JSON round trip leaves it. `typeof`, `Array.isArray` and the record check read the JSON kind.
// - `Type.Object({ model: Type.Unknown(), context: Type.Unknown() })` and `Type.Unknown()` are the JSON TypeBox prints for them.
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Tools;

/// <summary>
/// <c>models.*</c> for code mode scripts, copied from the coding agent's code mode (upstream <c>models.ts</c>): the model catalog, and
/// classifier and image model calls.
/// <para>The catalog functions are host globals, journaled like any global. <c>models.classify()</c> and <c>models.generateImages()</c> are
/// paid effects, so each runs as an internal tool (callers tools, a reserved name, hidden from scripts' tool listings): a nested durable call
/// with its own task, live row, abort, usage accounting, and replay policy. The code mode tool turns a script's call of the global into a call
/// of that tool.</para>
/// </summary>
internal static partial class CodemodeModels
{
    /// <summary>Reserved names of code mode's internal tools; they are never listed to scripts or the model.</summary>
    public const string InternalPrefix = "pi.codemode.";
    public const string ClassifyTool = InternalPrefix + "models.classify";
    public const string GenerateImagesTool = InternalPrefix + "models.generateImages";

    /// <summary>The global a script calls, by internal tool.</summary>
    public static readonly ImmutableDictionary<string, string> ModelCalls = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
    [
        KeyValuePair.Create("models.classify", ClassifyTool),
        KeyValuePair.Create("models.generateImages", GenerateImagesTool),
    ]);

    /// <summary>The <see cref="ModelCalls"/> globals in upstream's key order.</summary>
    public static readonly ImmutableArray<string> ModelCallGlobals = ["models.classify", "models.generateImages"];

    /// <summary>The catalog functions, host globals of the <c>models</c> namespace.</summary>
    public static readonly ImmutableArray<string> CatalogGlobals = ["models.getModelsOfType", "models.getAvailableOfType", "models.getModelOfType"];

    /// <summary><c>models.classify()</c> and <c>models.generateImages()</c> calls one script may have in flight; the rest wait.</summary>
    public const int MaxConcurrentModelCalls = 4;

    private static readonly ImmutableArray<string> ModelTypes = ["chat", "image", "classifier"];

    public static readonly string Description = string.Join("\n",
        "`models` (all functions return promises):",
        "- `models.getModelsOfType(type, provider?)`, `models.getAvailableOfType(type, provider?)` (configured auth only), `models.getModelOfType(type, provider, id)`: the catalog; `type` is \"chat\", \"image\", or \"classifier\".",
        "- `models.classify(model, { state: { ... }, images?: [{ type: \"image\", data, mimeType }], questions: { <id>: { type: \"choice\", instructions, criteria: { <label>: <meaning> } } | { type: \"score\", instructions, criteria: [<lowest level>, ..., <highest level>] } | { type: \"bool\", instructions, criteria: { true: <meaning>, false: <meaning> } } } })` resolves to `{ answers: { <id>: ... }, stopReason, errorMessage?, usage? }`.",
        "- `models.generateImages(model, { input: [{ type: \"text\", text: <prompt> }, ...optional { type: \"image\", data, mimeType }] })` resolves to `{ output: [image blocks], stopReason, errorMessage?, usage? }`; show each image with `image(block)`.",
        $"At most {MaxConcurrentModelCalls} classify/generateImages calls run at once; more wait.");

    private static bool IsRecord(JsonValue? value) => value is JsonObjectValue;

    private static JsonValue? Get(JsonObjectValue value, string key) => value.TryGetValue(key, out var found) ? found : null;

    /// <summary>A script value in an error message: <c>undefined</c>, <c>a string</c>, <c>an array</c>, or its keys (<c>{ prompt }</c>).</summary>
    private static string DescribeValue(JsonValue? value) => value switch
    {
        null => "undefined",
        JsonNullValue => "null",
        JsonArrayValue array => array.Count == 0 ? "an empty array" : "an array",
        JsonObjectValue properties => properties.Count == 0 ? "{}"
            : $"{{ {string.Join(", ", properties.Keys.Take(6))}{(properties.Count > 6 ? ", ..." : "")} }}",
        JsonStringValue => "a string",
        JsonNumberValue => "a number",
        _ => "a boolean",
    };

    /// <summary><c>an image</c>, <c>a classifier</c>.</summary>
    private static string WithArticle(string word) => $"{(Vowel().IsMatch(word) ? "an" : "a")} {word}";

    [GeneratedRegex("^[aeiou]")] private static partial Regex Vowel();

    private static string ToModelType(JsonValue? value)
    {
        if (value is JsonStringValue text && ModelTypes.Contains(text.Value)) return text.Value;
        throw new InvalidOperationException($"Unknown model type {(value is null ? "undefined" : value.ToJson())}. Use \"chat\", \"image\", or \"classifier\".");
    }

    private static string? ToProvider(JsonValue? value) => value switch
    {
        null or JsonNullValue => null,
        JsonStringValue text => text.Value,
        _ => throw new InvalidOperationException("provider must be a string"),
    };

    /// <summary>Catalog entry for scripts. <c>headers</c> is dropped because configured headers can carry credentials.</summary>
    private static JsonValue ToModelInfo(JsonObjectValue model) => JsonValue.Object(model.Where(property => property.Key != "headers"));

    /// <summary>The catalog functions: what a script's call of <paramref name="name"/> with <paramref name="args"/> (the argument list)
    /// resolves to; null for undefined.</summary>
    public static async Task<JsonValue?> CatalogCall(string name, JsonValue? args, IDurableModels models, DurableContext context)
    {
        var list = args as JsonArrayValue ?? JsonArrayValue.Empty;
        JsonValue? At(int index) => index < list.Count ? list[index] : null;
        var (type, provider, id) = (At(0), At(1), At(2));
        if (name == "models.getModelsOfType")
        {
            var modelType = ToModelType(type);
            return JsonValue.Array(models.GetModelsOfType(modelType, ToProvider(provider)).Select(ToModelInfo));
        }
        if (name == "models.getAvailableOfType")
        {
            var modelType = ToModelType(type);
            var available = await models.GetAvailableOfTypeAsync(modelType, ToProvider(provider), context.CancellationToken).ConfigureAwait(false);
            return JsonValue.Array(available.Select(ToModelInfo));
        }
        if (provider is not JsonStringValue providerText || id is not JsonStringValue idText)
        {
            throw new InvalidOperationException(
                $"models.getModelOfType(type, provider, id) expects three strings, got ({string.Join(", ", list.Select(item => DescribeValue(item)))}). The provider and the id are separate arguments, for example models.getModelOfType(\"classifier\", \"typesafe\", \"jev-latest\").");
        }
        var model = models.GetModelOfType(ToModelType(type), providerText.Value, idText.Value);
        return model is null ? null : ToModelInfo(model);
    }

    private const string ClassifierContextShape =
        "{ state: { ... }, images?: [{ type: \"image\", data: <base64>, mimeType }], questions: { <id>: { type: \"choice\", instructions, criteria: { <label>: <meaning> } } | { type: \"score\", instructions, criteria: [<lowest level>, ..., <highest level>] } | { type: \"bool\", instructions, criteria: { true: <meaning>, false: <meaning> } } } }";

    private static bool IsImageBlock(JsonValue? value) => value is JsonObjectValue image && Get(image, "type") is JsonStringValue { Value: "image" } &&
        Get(image, "data") is JsonStringValue && Get(image, "mimeType") is JsonStringValue;

    /// <summary>Check a script's classifier context, so mistakes fail with the expected shape instead of a provider error.</summary>
    private static JsonObjectValue CheckClassifierContext(JsonValue? context)
    {
        InvalidOperationException Fail(string problem) => new($"models.classify() {problem}. Expected context: {ClassifierContextShape}.");
        if (context is not JsonObjectValue value) throw Fail($"expects a context object as its second argument, got {DescribeValue(context)}");
        if (!IsRecord(Get(value, "state"))) throw Fail($"context.state must be an object, got {DescribeValue(Get(value, "state"))}");
        if (Get(value, "images") is { } images)
        {
            if (images is not JsonArrayValue list) throw Fail($"context.images must be an array, got {DescribeValue(images)}");
            for (var index = 0; index < list.Count; index++)
                if (!IsImageBlock(list[index])) throw Fail($"context.images[{index}] must be an image block, got {DescribeValue(list[index])}");
        }
        var questions = Get(value, "questions");
        if (questions is not JsonObjectValue byId || byId.Count == 0)
            throw Fail($"context.questions must map question IDs to questions, got {DescribeValue(questions)}");
        static bool IsStrings(IReadOnlyCollection<JsonValue> values) => values.Count > 0 && values.All(item => item is JsonStringValue);
        foreach (var (id, question) in byId)
        {
            var at = $"context.questions.{id}";
            if (question is not JsonObjectValue entry) throw Fail($"{at} must be a question object, got {DescribeValue(question)}");
            if (Get(entry, "instructions") is not JsonStringValue) throw Fail($"{at}.instructions must be a string");
            var criteria = Get(entry, "criteria");
            var type = Get(entry, "type");
            if (type is JsonStringValue { Value: "choice" })
            {
                if (criteria is not JsonObjectValue labels || !IsStrings([.. labels.Select(label => label.Value)]))
                    throw Fail($"{at} is a \"choice\" question, so criteria must map each label to its meaning");
            }
            else if (type is JsonStringValue { Value: "score" })
            {
                if (criteria is not JsonArrayValue levels || !IsStrings([.. levels]))
                    throw Fail($"{at} is a \"score\" question, so criteria must list the levels as strings, lowest first");
            }
            else if (type is JsonStringValue { Value: "bool" })
            {
                if (criteria is not JsonObjectValue meanings || Get(meanings, "true") is not JsonStringValue || Get(meanings, "false") is not JsonStringValue)
                    throw Fail($"{at} is a \"bool\" question, so criteria must be {{ true: string, false: string }}");
            }
            else
            {
                throw Fail($"{at}.type must be \"choice\", \"score\", or \"bool\", got {(type is null ? "undefined" : type.ToJson())}");
            }
        }
        return value;
    }

    /// <summary>Check a script's image context, so mistakes such as <c>{ prompt }</c> fail with the expected shape.</summary>
    private static JsonObjectValue CheckImagesContext(JsonValue? context)
    {
        InvalidOperationException Fail(string problem) => new(
            $"models.generateImages() {problem}. Expected context: {{ input: [{{ type: \"text\", text: <prompt> }}, ...optional {{ type: \"image\", data: <base64>, mimeType }} references] }}.");
        if (context is not JsonObjectValue value) throw Fail($"expects a context object as its second argument, got {DescribeValue(context)}");
        var input = Get(value, "input");
        if (input is not JsonArrayValue blocks || blocks.Count == 0) throw Fail($"context.input must be a non-empty array of blocks, got {DescribeValue(input)}");
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block is JsonObjectValue text && Get(text, "type") is JsonStringValue { Value: "text" } && Get(text, "text") is JsonStringValue) continue;
            if (IsImageBlock(block)) continue;
            throw Fail($"context.input[{index}] must be a text or image block, got {DescribeValue(block)}");
        }
        return value;
    }

    /// <summary>
    /// Resolve the script's model by provider and id only, so a script-supplied baseUrl or headers never receive the credentials, with errors
    /// that say what to do.
    /// </summary>
    private static JsonObjectValue ResolveModel(string name, string type, JsonValue? model, IDurableModels models)
    {
        var listHint = $"List the {type} models you can use with models.getAvailableOfType(\"{type}\").";
        if (model is not JsonObjectValue value || Get(value, "provider") is not JsonStringValue provider || Get(value, "id") is not JsonStringValue id)
        {
            var undefinedHint = model is null or JsonNullValue ? " models.getModelOfType() returns undefined for an unknown provider or id." : "";
            throw new InvalidOperationException(
                $"{name}() expects {WithArticle(type)} model as its first argument, got {DescribeValue(model)}.{undefinedHint} {listHint}");
        }
        if (models.GetModelOfType(type, provider.Value, id.Value) is { } resolved) return resolved;
        var actualType = ModelTypes.FirstOrDefault(other => other != type && models.GetModelOfType(other, provider.Value, id.Value) is not null);
        throw new InvalidOperationException(actualType is not null
            ? $"\"{provider.Value}/{id.Value}\" is {WithArticle(actualType)} model, not {WithArticle(type)} model. {listHint}"
            : $"Unknown {type} model \"{provider.Value}/{id.Value}\". {listHint}");
    }

    /// <summary><c>Type.Object({ model: Type.Unknown(), context: Type.Unknown() })</c>, as TypeBox prints it.</summary>
    private static readonly JsonObjectValue ModelCallParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["model","context"],"properties":{"model":{},"context":{}}}""");

    /// <summary>The result of a model call as the tool returns it: the whole result for the script, usage counted by the Harness.</summary>
    private static ToolExecutionResult ModelResult(string label, JsonObjectValue result)
    {
        var stopReason = Get(result, "stopReason") is JsonStringValue reason ? reason.Value : "undefined";
        var ok = stopReason == "stop";
        var errorMessage = Get(result, "errorMessage") is JsonStringValue message ? message.Value : "";
        return new ToolExecutionResult
        {
            Output = JsonValue.Array(JsonValue.Object([KeyValuePair.Create("type", (JsonValue)"text"),
                KeyValuePair.Create("text", (JsonValue)(ok ? $"{label} done" : $"{label} {stopReason}: {errorMessage}"))])),
            StructuredOutput = result,
            IsError = ok ? null : true,
            Usage = Get(result, "usage") as JsonObjectValue,
        };
    }

    private static string Label(string operation, JsonObjectValue model) =>
        $"{operation} {DurableModelOperations.Field(model, "provider")}/{DurableModelOperations.Field(model, "id")}";

    /// <summary>The internal tools behind <c>models.classify()</c> and <c>models.generateImages()</c>.</summary>
    public static ImmutableArray<ToolRegistration> CreateModelTools() =>
    [
        Define.Tool(new ToolRegistration
        {
            Name = ClassifyTool,
            Description = "models.classify() of code mode scripts.",
            Parameters = ModelCallParameters,
            StructuredOutputSchema = JsonObjectValue.Empty,
            Callers = [ToolCaller.Tools],
            Execute = async (arguments, api, context) =>
            {
                var args = (JsonObjectValue)arguments;
                var model = ResolveModel("models.classify", "classifier", Get(args, "model"), api.Models);
                var checkedContext = CheckClassifierContext(Get(args, "context"));
                var result = await api.Models.ClassifyAsync(model, checkedContext, context.CancellationToken).ConfigureAwait(false);
                return ModelResult(Label("classify", model), result);
            },
        }),
        Define.Tool(new ToolRegistration
        {
            Name = GenerateImagesTool,
            Description = "models.generateImages() of code mode scripts.",
            Parameters = ModelCallParameters,
            StructuredOutputSchema = JsonObjectValue.Empty,
            Callers = [ToolCaller.Tools],
            Execute = async (arguments, api, context) =>
            {
                var args = (JsonObjectValue)arguments;
                var model = ResolveModel("models.generateImages", "image", Get(args, "model"), api.Models);
                var checkedContext = CheckImagesContext(Get(args, "context"));
                var result = await api.Models.GenerateImagesAsync(model, checkedContext, context.CancellationToken).ConfigureAwait(false);
                return ModelResult(Label("generateImages", model), result);
            },
        }),
    ];
}
