// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-decisions.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>
/// OpenAI's Decisions API: <c>POST {baseUrl}/decisions</c> with <c>{ model, input, questions }</c>.
/// The state is sent as JSON text. With images, the input becomes one user message with the state as <c>input_text</c>
/// followed by <c>input_image</c> data URLs (at most 128). Questions map to Decisions types: <c>choice</c> to
/// <c>choice</c>, <c>score</c> to <c>score</c>, and <c>bool</c> to <c>predicate</c>; predicates have no criteria field, so
/// the meanings of true and false are appended to the instructions. Only OpenAI API keys work: Sign in with ChatGPT
/// tokens are rejected on this route. A 504 gateway timeout is not retried.
/// </summary>
public sealed class OpenAIDecisionsClassifier : IClassifierApi
{
    public const string ApiId = "openai-decisions";
    private const string Label = "OpenAI Decisions";
    /// <summary>The endpoint accepts at most this many image parts per request.</summary>
    public const int MaxImages = 128;
    // Cloudflare in front of api.openai.com answers 504 with an HTML page when a request runs longer than about five
    // seconds. Large inputs hit this limit and a retry of the same input runs into it again.
    private static readonly int[] NoRetryStatuses = [504];

    public static OpenAIDecisionsClassifier Instance { get; } = new();
    public string Api => ApiId;

    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        var output = ClassifierShared.Start(model, options);
        try
        {
            if (model.Api != ApiId) throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            var questions = ClassifierShared.Questions(context);
            var body = await ClassifierShared.PostAsync(Label, new Uri(new Uri(model.BaseUrl.TrimEnd('/') + "/"), "decisions"), model,
                new JsonObject
                {
                    ["model"] = model.Id, ["input"] = WireInput(context),
                    ["questions"] = new JsonArray([.. questions.Select(question => (JsonNode)WireQuestion(question.Key, question.Value))])
                }, options, cancellationToken, NoRetryStatuses).ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{Label} returned an unexpected response");
            // Set before parsing answers: a request with malformed or refused answers was still billed.
            if (ClassifierShared.ParseUsage(ClassifierShared.Field(body, "usage"), model) is { } usage) output = output with { Usage = usage };
            return output with { Answers = ParseAnswers(ClassifierShared.Field(body, "answers"), questions) };
        }
        catch (Exception error)
        {
            return ClassifierShared.Fail(output, ErrorMessage(error), cancellationToken);
        }
    }

    private static string PredicateInstructions(ClassifierBoolQuestion question)
    {
        var meanings = new[]
        {
            question.True.Length != 0 ? $"True means: {question.True}" : "", question.False.Length != 0 ? $"False means: {question.False}" : ""
        }.Where(meaning => meaning.Length != 0).ToArray();
        return meanings.Length > 0 ? $"{question.Instructions}\n\n{string.Join("\n", meanings)}" : question.Instructions;
    }

    private static JsonObject WireQuestion(string name, ClassifierQuestion question) => question switch
    {
        ClassifierChoiceQuestion choice => new JsonObject
        {
            ["type"] = "choice", ["name"] = name, ["instructions"] = choice.Instructions,
            ["choices"] = new JsonArray([.. ClassifierShared.Entries(choice.Criteria).Select(entry => (JsonNode)(entry.Value.Length != 0
                ? new JsonObject { ["value"] = entry.Key, ["description"] = entry.Value } : new JsonObject { ["value"] = entry.Key }))])
        },
        ClassifierScoreQuestion score => new JsonObject
        {
            ["type"] = "score", ["name"] = name, ["instructions"] = score.Instructions,
            ["levels"] = new JsonArray([.. score.Criteria.Select(label => (JsonNode)new JsonObject { ["label"] = label })])
        },
        ClassifierBoolQuestion boolean => new JsonObject { ["type"] = "predicate", ["name"] = name, ["instructions"] = PredicateInstructions(boolean) },
        _ => throw new ArgumentException("Unsupported classifier question.")
    };

    private static JsonNode WireInput(ClassifierContext context)
    {
        var state = ProviderRequest.Stringify(context.State);
        var images = context.ImageList;
        if (images.Length == 0) return JsonValue.Create(state);
        if (images.Length > MaxImages) throw new InvalidOperationException($"{Label} accepts at most {MaxImages} images, got {images.Length}");
        var content = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = state } };
        foreach (var image in images)
            content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.MimeType};base64,{image.Data}" });
        return new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } };
    }

    private static ImmutableArray<KeyValuePair<string, double>> ChoiceProbabilities(JsonElement? value, string id)
    {
        if (value is not { ValueKind: JsonValueKind.Array } list) throw new InvalidDataException($"{Label} returned invalid probabilities for {id}");
        return ClassifierShared.Entries(list.EnumerateArray().Select(entry =>
        {
            if (!ProviderRequest.IsString(entry, "value", out var label)) throw new InvalidDataException($"{Label} returned invalid probabilities for {id}");
            return KeyValuePair.Create(label, ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(entry, "probability"), $"probability for {id}.{label}"));
        }).ToList());
    }

    private static ClassifierAnswer ParseAnswer(string id, ClassifierQuestion question, JsonElement answer)
    {
        ProviderRequest.IsString(answer, "type", out var type);
        if (type == "refusal") throw new InvalidDataException($"{Label} refused to answer {id}");
        switch (question)
        {
            case ClassifierChoiceQuestion:
                if (type != "choice" || !ProviderRequest.IsString(answer, "choice", out var choice))
                    throw new InvalidDataException($"{Label} did not return a choice answer for {id}");
                return new ClassifierChoiceAnswer(choice, ChoiceProbabilities(ClassifierShared.Field(answer, "probabilities"), id),
                    ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "confidence"), $"confidence for {id}"));
            case ClassifierScoreQuestion:
                if (type != "score") throw new InvalidDataException($"{Label} did not return a score answer for {id}");
                return new ClassifierScoreAnswer(ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "score"), $"score for {id}"),
                    ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "confidence"), $"confidence for {id}"));
            default:
                if (type != "predicate") throw new InvalidDataException($"{Label} did not return a predicate answer for {id}");
                return new ClassifierBoolAnswer(ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "probability"), $"probability for {id}"));
        }
    }

    private static ImmutableArray<KeyValuePair<string, ClassifierAnswer>> ParseAnswers(JsonElement? value,
        ImmutableArray<KeyValuePair<string, ClassifierQuestion>> questions)
    {
        if (value is not { ValueKind: JsonValueKind.Array } list) throw new InvalidDataException($"{Label} returned an unexpected response");
        var byName = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var answer in list.EnumerateArray())
            if (ProviderRequest.IsString(answer, "name", out var name)) byName[name] = answer;
        return ClassifierShared.Entries(questions.Select(question => byName.TryGetValue(question.Key, out var answer)
            ? KeyValuePair.Create(question.Key, ParseAnswer(question.Key, question.Value, answer))
            : throw new InvalidDataException($"{Label} did not return an answer for {question.Key}")).ToList());
    }

    private static string ErrorMessage(Exception error) => error is ProviderRequestException { Status: 504 }
        ? $"{Label} error (504): the request timed out at the gateway. Very large inputs (above roughly 600K tokens) currently exceed its time limit."
        : ProviderRequest.FormatError(error, $"{Label} error");
}
