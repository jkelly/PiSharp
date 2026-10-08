// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/system-one-shared.ts,
// api/typesafe-system-one.ts, api/cloudflare-workers-ai-system-one.ts and providers/cloudflare-stream.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>The TypeSafe System One protocol over one service: the request URL, the request envelope and the response
/// envelope differ; questions, answers and usage are shared. Public <c>bool</c> questions travel as wire-level
/// <c>noul</c>. System One has no temperature and takes no images.</summary>
public abstract class SystemOneClassifier : IClassifierApi
{
    public abstract string Api { get; }
    /// <summary>Service name used in error messages.</summary>
    protected abstract string Label { get; }
    protected abstract Uri Url(ClassifierModel model);
    /// <summary>Wraps <c>{ state, questions }</c> in the service's request envelope.</summary>
    protected abstract JsonNode Payload(ClassifierModel model, JsonObject request);
    /// <summary>Extracts the System One output (<c>{ answers, usage }</c>) from the service's response envelope.</summary>
    protected abstract JsonElement Output(JsonElement body);

    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(context);
        var output = ClassifierShared.Start(model, options);
        try
        {
            if (model.Api != Api) throw new InvalidOperationException($"Unsupported classifier API: {model.Api}");
            if (context.ImageList.Length != 0) throw new InvalidOperationException($"{Label} does not support image input");
            var body = await ClassifierShared.PostAsync(Label, Url(model), model, Payload(model, WireRequest(context)), options,
                cancellationToken).ConfigureAwait(false);
            var result = Output(body);
            // Set before parsing answers: a request with malformed answers was still billed.
            if (ClassifierShared.ParseUsage(ClassifierShared.Field(result, "usage"), model) is { } usage) output = output with { Usage = usage };
            return output with { Answers = ParseAnswers(ClassifierShared.Field(result, "answers"), context) };
        }
        catch (Exception error)
        {
            return ClassifierShared.Fail(output, ProviderRequest.FormatError(error, $"{Label} error"), cancellationToken);
        }
    }

    protected static string TrimmedBase(ClassifierModel model) => model.BaseUrl.TrimEnd('/') + "/";

    /// <summary>Maps public <c>bool</c> questions to TypeSafe's wire-level <c>noul</c> type.</summary>
    private static JsonObject WireRequest(ClassifierContext context)
    {
        var questions = new JsonObject();
        foreach (var (id, question) in ClassifierShared.Questions(context))
            questions[id] = ClassifierShared.QuestionJson(question, question is ClassifierBoolQuestion ? "noul" : null);
        return new JsonObject { ["state"] = ProviderRequest.Node(context.State), ["questions"] = questions };
    }

    private ImmutableArray<KeyValuePair<string, double>> Probabilities(JsonElement? value, string id)
    {
        if (value is not { ValueKind: JsonValueKind.Object } table) throw new InvalidDataException($"{Label} returned invalid probabilities for {id}");
        return ClassifierShared.Entries(ModelOperationJson.ObjectEntries(table).Select(entry =>
            KeyValuePair.Create(entry.Key, ProviderRequest.RequiredNumber(Label, entry.Value, $"probability for {id}.{entry.Key}"))));
    }

    private ImmutableArray<KeyValuePair<string, ClassifierAnswer>> ParseAnswers(JsonElement? value, ClassifierContext context)
    {
        if (value is not { ValueKind: JsonValueKind.Object } answers) throw new InvalidDataException($"{Label} returned an unexpected response");
        var parsed = new List<KeyValuePair<string, ClassifierAnswer>>();
        var byId = ModelOperationJson.ObjectEntries(answers).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var (id, question) in ClassifierShared.Questions(context))
        {
            if (!byId.TryGetValue(id, out var answer) || answer.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{Label} did not return an answer for {id}");
            ProviderRequest.IsString(answer, "type", out var type);
            switch (question)
            {
                case ClassifierChoiceQuestion:
                    if (type != "choice" || !ProviderRequest.IsString(answer, "choice", out var choice))
                        throw new InvalidDataException($"{Label} did not return a choice answer for {id}");
                    parsed.Add(new(id, new ClassifierChoiceAnswer(choice, Probabilities(ClassifierShared.Field(answer, "probabilities"), id),
                        ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "confidence"), $"confidence for {id}"))));
                    break;
                case ClassifierScoreQuestion:
                    if (type != "score") throw new InvalidDataException($"{Label} did not return a score answer for {id}");
                    parsed.Add(new(id, new ClassifierScoreAnswer(
                        ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "score"), $"score for {id}"),
                        ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "confidence"), $"confidence for {id}"))));
                    break;
                default:
                    if (type != "noul") throw new InvalidDataException($"{Label} did not return a bool answer for {id}");
                    parsed.Add(new(id, new ClassifierBoolAnswer(
                        ProviderRequest.RequiredNumber(Label, ClassifierShared.Field(answer, "noul"), $"probability for {id}"))));
                    break;
            }
        }
        return ClassifierShared.Entries(parsed);
    }
}

/// <summary>typesafe-system-one.ts: TypeSafe's native System One protocol, <c>POST {baseUrl}/systemone</c> with
/// <c>{ model, state, questions }</c>. OpenRouter, OpenCode Zen, Vercel AI Gateway and llama.cpp decision models serve
/// the same protocol at their own base URLs.</summary>
public sealed class TypeSafeSystemOneClassifier : SystemOneClassifier
{
    public const string ApiId = "typesafe-system-one";
    public static TypeSafeSystemOneClassifier Instance { get; } = new();
    public override string Api => ApiId;
    protected override string Label => "System One API";
    protected override Uri Url(ClassifierModel model) => new(new Uri(TrimmedBase(model)), "systemone");
    protected override JsonNode Payload(ClassifierModel model, JsonObject request)
    {
        var payload = new JsonObject { ["model"] = model.Id };
        foreach (var (name, value) in request.ToArray()) { request.Remove(name); payload[name] = value; }
        return payload;
    }
    protected override JsonElement Output(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object ? body : throw new InvalidDataException("System One API returned an unexpected response");
}

/// <summary>cloudflare-workers-ai-system-one.ts: System One models on the Workers AI REST endpoint,
/// <c>POST {baseUrl}/run</c> with <c>{ model, input }</c>. Third-party models such as <c>typesafe/jev</c> answer with a
/// run record (<c>{ success, result: { state: "Completed", result: { answers, usage } } }</c>); Cloudflare-hosted models
/// such as <c>@cf/cloudflare/clef</c> answer with the output directly (<c>{ success, result: { answers, usage } }</c>).</summary>
public sealed class CloudflareWorkersAISystemOneClassifier : SystemOneClassifier
{
    public const string ApiId = "cloudflare-workers-ai-system-one";
    private const string Name = "Cloudflare Workers AI";
    public static CloudflareWorkersAISystemOneClassifier Instance { get; } = new();
    public override string Api => ApiId;
    protected override string Label => Name;
    protected override Uri Url(ClassifierModel model) => new(new Uri(TrimmedBase(model)), "run");
    protected override JsonNode Payload(ClassifierModel model, JsonObject request) => new JsonObject { ["model"] = model.Id, ["input"] = request };
    protected override JsonElement Output(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{Name} returned an unexpected response");
        if (body.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            throw new InvalidDataException(ErrorMessage(ClassifierShared.Field(body, "errors")));
        if (ClassifierShared.Field(body, "result") is not { ValueKind: JsonValueKind.Object } result)
            throw new InvalidDataException($"{Name} returned an unexpected response");
        if (result.TryGetProperty("answers", out _)) return result;
        var state = ClassifierShared.Field(result, "state");
        if (state is not { ValueKind: JsonValueKind.String } completed || completed.GetString() != "Completed")
            throw new InvalidDataException($"{Name} run did not complete (state: {JsString(state)})");
        return ClassifierShared.Field(result, "result") is { ValueKind: JsonValueKind.Object } output ? output
            : throw new InvalidDataException($"{Name} returned an unexpected response");
    }

    private static string ErrorMessage(JsonElement? errors)
    {
        if (errors is { ValueKind: JsonValueKind.Array } list)
        {
            var messages = list.EnumerateArray().Where(error => ProviderRequest.IsString(error, "message", out _))
                .Select(error => error.GetProperty("message").GetString()!).ToArray();
            if (messages.Length > 0) return $"{Name} error: {string.Join("; ", messages)}";
        }
        return $"{Name} request failed";
    }

    /// <summary><c>String(value)</c> of a JSON value.</summary>
    private static string JsString(JsonElement? value) => value switch
    {
        null => "undefined",
        { ValueKind: JsonValueKind.String } text => text.GetString()!,
        { ValueKind: JsonValueKind.Null } => "null",
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        { ValueKind: JsonValueKind.Object } => "[object Object]",
        { ValueKind: JsonValueKind.Array } array => string.Join(",", array.EnumerateArray().Select(item =>
            item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : JsString(item))),
        { } number => ProviderRequest.Stringify(JsonNode.Parse(number.GetRawText()))
    };

    /// <summary>cloudflare-stream.ts <c>resolveCloudflareModel</c>: fills the <c>{CLOUDFLARE_ACCOUNT_ID}</c> and
    /// <c>{CLOUDFLARE_GATEWAY_ID}</c> endpoint placeholders from the resolved provider env.</summary>
    public static T ResolveModel<T>(T model, IReadOnlyDictionary<string, string>? env) where T : OperationModel
    {
        if (env is null || env.Count == 0) return model;
        var baseUrl = model.BaseUrl;
        foreach (var name in new[] { "CLOUDFLARE_ACCOUNT_ID", "CLOUDFLARE_GATEWAY_ID" })
            if (env.TryGetValue(name, out var value)) baseUrl = baseUrl.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return baseUrl == model.BaseUrl ? model : model with { BaseUrl = baseUrl };
    }
}
