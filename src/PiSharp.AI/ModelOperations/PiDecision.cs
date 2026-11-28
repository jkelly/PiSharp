// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/ai/src/api/pi-decision.ts.
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

public sealed class PiDecision : IClassifierApi
{
    public const string ApiId = "pi-decision";
    public static PiDecision Instance { get; } = new();
    public string Api => ApiId;

    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = ClassifierShared.Start(model, options);
        try
        {
            if (model.Api != ApiId) throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            if (!model.AcceptsImages && !context.ImageList.IsEmpty)
                throw new InvalidOperationException($"Pi Decision model {model.Id} does not support image input");
            var payload = new JsonObject { ["model"] = model.Id, ["context"] = JsonNode.Parse(ModelOperationJson.WriteClassifierContext(context.ImageList.IsEmpty ? context with { Images = default } : context).ToString()) };
            if (options?.SessionId is { } session) payload["options"] = new JsonObject { ["sessionId"] = session };
            var body = await ClassifierShared.PostAsync("Pi Decision", new Uri(model.BaseUrl.TrimEnd('/') + "/decision"), model, payload, options, cancellationToken).ConfigureAwait(false);
            if (ProviderRequest.IsString(body, "responseId", out var id) && id.Length > 0) result = result with { ResponseId = id };
            result = result with { Usage = PiUsage.Parse(ClassifierShared.Field(body, "usage"), model) };
            if (ClassifierShared.Field(body, "answers") is not { ValueKind: JsonValueKind.Object } answers)
                throw new InvalidDataException("Pi Decision returned an unexpected response");
            return result with { Answers = [.. ClassifierShared.Questions(context).Select(q =>
                KeyValuePair.Create(q.Key, ParseAnswer(q.Key, q.Value, answers)))] };
        }
        catch (Exception error) { return ClassifierShared.Fail(result, ProviderRequest.FormatError(error, "Pi Decision error"), cancellationToken); }
    }

    private static ClassifierAnswer ParseAnswer(string id, ClassifierQuestion question, JsonElement answers)
    {
        if (!answers.TryGetProperty(id, out var answer)) throw new InvalidDataException($"Pi Decision did not return an answer for {id}");
        if (answer.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Pi Decision returned an invalid answer for {id}");
        ProviderRequest.IsString(answer, "type", out var type);
        static double Bounded(JsonElement? value, string field, double max = 1)
        {
            var n = ProviderRequest.RequiredNumber("Pi Decision", value, field);
            return n >= 0 && n <= max ? n : throw new InvalidDataException($"Pi Decision returned out-of-range {field}: {n}");
        }
        if (question is ClassifierChoiceQuestion choice)
        {
            if (type != "choice" || !ProviderRequest.IsString(answer, "choice", out var selected)) throw Wrong("choice");
            var labels = choice.Criteria.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            if (!labels.Contains(selected)) throw new InvalidDataException($"Pi Decision returned unknown choice \"{selected}\" for {id}");
            if (ClassifierShared.Field(answer, "probabilities") is not { ValueKind: JsonValueKind.Object } probabilities)
                throw new InvalidDataException($"Pi Decision returned invalid probabilities for {id}");
            return new ClassifierChoiceAnswer(selected, ClassifierShared.Entries(probabilities.EnumerateObject().Select(p =>
                labels.Contains(p.Name) ? KeyValuePair.Create(p.Name, Bounded(p.Value, $"probability for {id}.{p.Name}"))
                    : throw new InvalidDataException($"Pi Decision returned unknown choice \"{p.Name}\" in probabilities for {id}"))),
                Bounded(ClassifierShared.Field(answer, "confidence"), $"confidence for {id}"));
        }
        if (question is ClassifierScoreQuestion score)
        {
            if (type != "score") throw Wrong("score");
            return new ClassifierScoreAnswer(Bounded(ClassifierShared.Field(answer, "score"), $"score for {id}", Math.Max(0, score.Criteria.Length - 1)),
                Bounded(ClassifierShared.Field(answer, "confidence"), $"confidence for {id}"));
        }
        if (type != "bool") throw Wrong("bool");
        return new ClassifierBoolAnswer(Bounded(ClassifierShared.Field(answer, "probability"), $"probability for {id}"));
        InvalidDataException Wrong(string expect) => new($"Pi Decision did not return a {expect} answer for {id}");
    }
}
