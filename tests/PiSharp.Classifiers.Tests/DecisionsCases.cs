using System.Net;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// test/openai-decisions.test.ts, with full request bodies pinned.
internal static partial class Program
{
    private const string LunaJson = """
        {"type":"classifier","id":"gpt-6-luna","name":"GPT-6 Luna","api":"openai-decisions","provider":"openai","baseUrl":"https://api.openai.com/v1","input":["text","image"],"cost":{"input":0.1,"output":0,"cacheRead":0,"cacheWrite":0,"tiers":[{"inputTokensAbove":272000,"input":0.2,"output":0,"cacheRead":0,"cacheWrite":0}]},"contextWindow":922000}
        """;
    private static readonly ClassifierModel Luna = Classifier(LunaJson);

    private static ClassifierContext DecisionsContext => new(Json("""{"text":"The deployment succeeded, thank you."}"""), Questions(
        ("category", new ClassifierChoiceQuestion("Classify the message", Criteria(("success", "Successful"), ("failure", "")))),
        ("satisfaction", new ClassifierScoreQuestion("Score satisfaction", ["low", "neutral", "high"])),
        ("approved", new ClassifierBoolQuestion("Does the user approve?", "Approval", "No approval"))));

    private const string WireCategory = """{"type":"choice","name":"category","choice":"success","probabilities":[{"value":"success","probability":0.9},{"value":"failure","probability":0.1}],"confidence":0.8}""";
    private const string WireSatisfaction = """{"type":"score","name":"satisfaction","score":1.8,"probabilities":[{"value":0,"label":"low","probability":0.05},{"value":1,"label":"neutral","probability":0.1},{"value":2,"label":"high","probability":0.85}],"confidence":0.7}""";
    private const string WireApproved = """{"type":"predicate","name":"approved","probability":0.95}""";
    private const string WireUsage = """{"input_tokens":164,"input_tokens_details":{"cached_tokens":0,"cache_write_tokens":0},"output_tokens":0,"output_tokens_details":{"reasoning_tokens":0},"total_tokens":164}""";
    private static string DecisionsAnswers(params string[] answers) => "[" + string.Join(",", answers) + "]";

    private const string DecisionsQuestionsBody = """[{"type":"choice","name":"category","instructions":"Classify the message","choices":[{"value":"success","description":"Successful"},{"value":"failure"}]},{"type":"score","name":"satisfaction","instructions":"Score satisfaction","levels":[{"label":"low"},{"label":"neutral"},{"label":"high"}]},{"type":"predicate","name":"approved","instructions":"Does the user approve?\n\nTrue means: Approval\nFalse means: No approval"}]""";

    private static IEnumerable<(string, Func<Task>)> DecisionsCases() =>
    [
        ("decisions.full-body-headers-and-answers-matched-by-name", async () =>
        {
            // Answers out of question order: they are matched by name.
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"model":"gpt-6-luna","answers":{{{DecisionsAnswers(WireApproved, WireSatisfaction, WireCategory)}}},"usage":{{{WireUsage}}}}"""));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client, Temperature = 1.5 });
            Equal(1, http.Requests.Count, "requests");
            var request = http.Requests[0];
            Equal("https://api.openai.com/v1/decisions", request.Url, "url");
            Names(["authorization", "content-type"], request.Headers.Keys.Order(StringComparer.Ordinal), "headers");
            Equal("Bearer secret", request.Headers["authorization"], "authorization");
            Equal("application/json", request.Headers["content-type"], "content-type");
            Body($$$"""{"model":"gpt-6-luna","input":"{\"text\":\"The deployment succeeded, thank you.\"}","questions":{{{DecisionsQuestionsBody}}}}""", request.Body, "decisions");
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop reason");
            Equal(null, result.ErrorMessage, "error");
            Names(["category", "satisfaction", "approved"], result.Answers.Select(pair => pair.Key), "answer order");
            var category = Answer<ClassifierChoiceAnswer>(result, "category");
            Equal("success", category.Choice, "choice"); Equal(0.8, category.Confidence, "confidence");
            Names(["success", "failure"], category.Probabilities.Select(pair => pair.Key), "probability labels");
            Equal(0.9, Probability(category, "success"), "p(success)"); Equal(0.1, Probability(category, "failure"), "p(failure)");
            Equal(new ClassifierScoreAnswer(1.8, 0.7), Answer<ClassifierScoreAnswer>(result, "satisfaction"), "score");
            Equal(new ClassifierBoolAnswer(0.95), Answer<ClassifierBoolAnswer>(result, "approved"), "bool");
            Check(result.Usage is { Input: 164, Output: 0, CacheRead: 0, CacheWrite: 0, TotalTokens: 164 }, "usage counts");
            Close(0.0000164, (double)result.Usage!.Cost.Total, "cost");
            Equal("openai-decisions", result.Api, "api"); Equal("openai", result.Provider, "provider"); Equal("gpt-6-luna", result.Model, "model");
        }),
        ("decisions.long-context-requests-use-the-tier-input-rate", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, WireApproved)}}},"usage":{"input_tokens":300000,"output_tokens":0}}"""));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Close(0.06, (double)result.Usage!.Cost.Total, "tier cost");
            // Exactly the threshold keeps the base rate.
            http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, WireApproved)}}},"usage":{"input_tokens":272000,"output_tokens":0}}"""));
            result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Close(0.0272, (double)result.Usage!.Cost.Total, "threshold cost");
        }),
        ("decisions.images-follow-the-state-in-one-user-message", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, WireApproved)}}}}"""));
            var context = DecisionsContext with { Images = [new("aW1hZ2U=", "image/png"), new("aW1hZ2U=", "image/jpeg")] };
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, context, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop");
            Body($$$"""{"model":"gpt-6-luna","input":[{"role":"user","content":[{"type":"input_text","text":"{\"text\":\"The deployment succeeded, thank you.\"}"},{"type":"input_image","image_url":"data:image/png;base64,aW1hZ2U="},{"type":"input_image","image_url":"data:image/jpeg;base64,aW1hZ2U="}]}],"questions":{{{DecisionsQuestionsBody}}}}""",
                http.Requests[0].Body, "image input");
        }),
        ("decisions.more-than-128-images-fail-before-sending", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("{}"));
            var context = DecisionsContext with { Images = [.. Enumerable.Repeat(new ImageContent("aW1hZ2U=", "image/png"), 129)] };
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, context, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(0, http.Requests.Count, "requests");
            Equal(ModelOperationStopReason.Error, result.StopReason, "stop");
            Equal("OpenAI Decisions accepts at most 128 images, got 129", result.ErrorMessage, "error");
            // Exactly 128 are sent.
            http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, WireApproved)}}}}"""));
            context = DecisionsContext with { Images = [.. Enumerable.Repeat(new ImageContent("aW1hZ2U=", "image/png"), 128)] };
            result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, context, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "128 images");
            Equal(129, http.Requests[0].Json.GetProperty("input")[0].GetProperty("content").GetArrayLength(), "parts");
        }),
        ("decisions.refusal-fails-and-keeps-billed-usage", async () =>
        {
            const string refusal = """{"type":"refusal","name":"approved"}""";
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, refusal)}}},"usage":{{{WireUsage}}}}"""));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(ModelOperationStopReason.Error, result.StopReason, "stop");
            Equal(0, result.Answers.Length, "answers");
            Equal("OpenAI Decisions refused to answer approved", result.ErrorMessage, "error");
            Equal(164L, result.Usage?.Input, "usage kept");
        }),
        ("decisions.missing-mistyped-and-invalid-answers-are-errors", async () =>
        {
            async Task<ClassifierResult> Run(string answers) => await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext,
                new ClassifierOptions { ApiKey = "secret", Http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{answers}}}}""")).Client });
            Equal("OpenAI Decisions did not return an answer for approved", (await Run(DecisionsAnswers(WireCategory, WireSatisfaction))).ErrorMessage, "missing");
            Equal("OpenAI Decisions did not return a predicate answer for approved",
                (await Run(DecisionsAnswers(WireCategory, WireSatisfaction, """{"type":"score","name":"approved"}"""))).ErrorMessage, "mistyped");
            Equal("OpenAI Decisions returned an invalid probability for approved",
                (await Run(DecisionsAnswers(WireCategory, WireSatisfaction, """{"type":"predicate","name":"approved","probability":"high"}"""))).ErrorMessage, "invalid");
            Equal("OpenAI Decisions returned invalid probabilities for category",
                (await Run(DecisionsAnswers("""{"type":"choice","name":"category","choice":"success","probabilities":{},"confidence":1}""", WireSatisfaction, WireApproved))).ErrorMessage, "probabilities");
            Equal("OpenAI Decisions returned an unexpected response", (await Run("{}")).ErrorMessage, "not an array");
        }),
        ("decisions.prototype-and-index-question-ids-keep-javascript-order", async () =>
        {
            var bool1 = new ClassifierBoolQuestion("Is this true?", "Yes", "No");
            var context = new ClassifierContext(Json("{}"), Questions(("__proto__", bool1), ("b", bool1), ("2", bool1), ("10", bool1)));
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":[{"type":"predicate","name":"__proto__","probability":0.75},{"type":"predicate","name":"b","probability":0.5},{"type":"predicate","name":"2","probability":0.25},{"type":"predicate","name":"10","probability":0.125}]}"""));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, context, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop");
            // Object.entries puts array-index keys first, ascending.
            Names(["2", "10", "__proto__", "b"], result.Answers.Select(pair => pair.Key), "answer order");
            Equal(new ClassifierBoolAnswer(0.75), Answer<ClassifierBoolAnswer>(result, "__proto__"), "__proto__");
            Names(["2", "10", "__proto__", "b"], http.Requests[0].Json.GetProperty("questions").EnumerateArray().Select(q => q.GetProperty("name").GetString()!), "wire order");
            Equal("\"{}\"", http.Requests[0].Json.GetProperty("input").GetRawText(), "empty state");
        }),
        ("decisions.gateway-timeouts-are-not-retried-and-explained", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Text("<!DOCTYPE html><html>Gateway time-out</html>", HttpStatusCode.GatewayTimeout, "text/html", ("retry-after-ms", "0")));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(1, http.Requests.Count, "requests");
            Equal(ModelOperationStopReason.Error, result.StopReason, "stop");
            Equal("OpenAI Decisions error (504): the request timed out at the gateway. Very large inputs (above roughly 600K tokens) currently exceed its time limit.",
                result.ErrorMessage, "error");
        }),
        ("decisions.other-server-errors-are-retried", async () =>
        {
            var http = new FakeHttp((_, attempt, _) => Task.FromResult(attempt == 1
                ? FakeHttp.Text("busy", HttpStatusCode.ServiceUnavailable, "text/plain", ("retry-after-ms", "0"))
                : FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, WireApproved)}}}}""")));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(2, http.Requests.Count, "attempts");
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop");
            Body(http.Requests[0].Body, http.Requests[1].Body, "retry repeats the same body");
        }),
        ("decisions.http-errors-carry-the-body-or-the-status-message", async () =>
        {
            const string error = """{"error":{"message":"Decision input exceeds the token limit.","type":"invalid_request_error"}}""";
            var http = FakeHttp.Always(() => FakeHttp.Json(error, HttpStatusCode.BadRequest));
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client, MaxRetries = 0 });
            Equal($"OpenAI Decisions error (400): {error}", result.ErrorMessage, "body");
            http = FakeHttp.Always(() => FakeHttp.Text("  ", HttpStatusCode.Forbidden));
            result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal("OpenAI Decisions error (403): OpenAI Decisions returned 403", result.ErrorMessage, "no body");
            var longBody = new string('x', 4005);
            http = FakeHttp.Always(() => FakeHttp.Text(longBody, HttpStatusCode.Conflict));
            result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client, MaxRetries = 0 });
            Equal($"OpenAI Decisions error (409): {new string('x', 4000)}... [truncated 5 chars]", result.ErrorMessage, "truncated body");
        }),
        ("decisions.other-apis-and-missing-keys-fail-before-sending", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("{}"));
            var other = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna with { Api = "typesafe-system-one" }, DecisionsContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            var noKey = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions { Http = http.Client });
            Equal(0, http.Requests.Count, "requests");
            Equal("Unsupported classifier API: typesafe-system-one", other.ErrorMessage, "other api");
            Equal("No API key for provider: openai", noKey.ErrorMessage, "no key");
            Equal(ModelOperationStopReason.Error, noKey.StopReason, "stop");
        }),
        ("decisions.payload-and-response-hooks", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{DecisionsAnswers(WireCategory, WireSatisfaction, WireApproved)}}}}""", HttpStatusCode.OK, ("x-request-id", "req-1")));
            var seen = new List<string>(); ProviderResponseInfo? response = null;
            var result = await OpenAIDecisionsClassifier.Instance.ClassifyAsync(Luna, DecisionsContext, new ClassifierOptions
            {
                ApiKey = "secret", Http = http.Client,
                OnPayload = (payload, model, _) =>
                {
                    seen.Add(model.Id); var replaced = payload.AsObject(); replaced["service_tier"] = "flex";
                    return ValueTask.FromResult<System.Text.Json.Nodes.JsonNode?>(replaced);
                },
                OnResponse = (info, _, _) => { response = info; return ValueTask.CompletedTask; }
            });
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop");
            Names(["gpt-6-luna"], seen, "payload hook");
            Equal("flex", http.Requests[0].Json.GetProperty("service_tier").GetString(), "replaced payload sent");
            Equal(200, response?.Status, "response status");
            Equal("req-1", response!.Headers.Single(pair => pair.Key == "x-request-id").Value, "response header");
        })
    ];
}
