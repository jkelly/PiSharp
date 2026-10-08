using System.Collections.Immutable;
using System.Net;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// test/typesafe-system-one.test.ts and test/cloudflare-workers-ai-system-one.test.ts, with full request bodies pinned.
internal static partial class Program
{
    private static readonly ClassifierModel Jev = Classifier("""
        {"type":"classifier","id":"jev-latest","name":"Jev","api":"typesafe-system-one","provider":"typesafe","baseUrl":"https://api.typesafe.ai/v1/","input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":64000}
        """);

    private static ClassifierContext SystemOneContext => new(Json("""{"text":"The deployment succeeded, thank you."}"""), Questions(
        ("category", new ClassifierChoiceQuestion("Classify the message", Criteria(("success", "Successful"), ("failure", "Failed")))),
        ("satisfaction", new ClassifierScoreQuestion("Score satisfaction", ["low", "neutral", "high"])),
        ("approved", new ClassifierBoolQuestion("Does the user approve?", "Approval", "No approval"))));

    private const string SystemOneAnswers = """{"category":{"type":"choice","choice":"success","probabilities":{"success":0.9,"failure":0.1},"confidence":0.8},"satisfaction":{"type":"score","score":2,"confidence":0.7},"approved":{"type":"noul","noul":0.95}}""";
    private const string SystemOneQuestionsBody = """{"category":{"type":"choice","instructions":"Classify the message","criteria":{"success":"Successful","failure":"Failed"}},"satisfaction":{"type":"score","instructions":"Score satisfaction","criteria":["low","neutral","high"]},"approved":{"type":"noul","instructions":"Does the user approve?","criteria":{"true":"Approval","false":"No approval"}}}""";

    private static IEnumerable<(string, Func<Task>)> SystemOneCases() =>
    [
        ("system-one.typesafe-full-body-noul-mapping-and-catalog-pricing", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}}}"""));
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client, Temperature = 1.5 });
            Equal("https://api.typesafe.ai/v1/systemone", http.Requests[0].Url, "url");
            Names(["authorization", "content-type"], http.Requests[0].Headers.Keys.Order(StringComparer.Ordinal), "headers");
            Equal("Bearer secret", http.Requests[0].Headers["authorization"], "authorization");
            // System One has no temperature field; the option is ignored.
            Body($$$"""{"model":"jev-latest","state":{"text":"The deployment succeeded, thank you."},"questions":{{{SystemOneQuestionsBody}}}}""", http.Requests[0].Body, "typesafe");
            Equal(ModelOperationStopReason.Stop, result.StopReason, "stop");
            Equal(new ClassifierBoolAnswer(0.95), Answer<ClassifierBoolAnswer>(result, "approved"), "bool");
            Equal("success", Answer<ClassifierChoiceAnswer>(result, "category").Choice, "choice");
            Equal(new ClassifierScoreAnswer(2, 0.7), Answer<ClassifierScoreAnswer>(result, "satisfaction"), "score");
            Equal(null, result.Usage, "no usage reported");
            var priced = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev with { Cost = Jev.Cost with { Input = 0.042 } }, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}},"usage":{"input_tokens":308,"output_tokens":23}}""")).Client });
            Check(priced.Usage is { Input: 308, Output: 23, TotalTokens: 331 }, "priced usage");
            Close(0.000012936, (double)priced.Usage!.Cost.Total, "priced cost");
            // JavaScript binary64: (0.042 / 1000000) * 308.
            Equal("{\"input\":0.000012936000000000001,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0.000012936000000000001}",
                PiSharp.Contracts.Compatibility.EcmaScriptJsonProjection.Project(priced.Usage.Cost.SourceBinary64Cost!), "binary64 cost");
        }),
        ("system-one.openrouter-serves-the-same-protocol", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"id":"gen-dec-1","provider":"TypeSafe","answers":{{{SystemOneAnswers}}},"usage":{"input_tokens":308,"output_tokens":23,"cost":0.000012936}}"""));
            var model = Jev with { Id = "typesafe/jev-1.13", Provider = "openrouter", BaseUrl = "https://openrouter.ai/api/v1", Cost = Jev.Cost with { Input = 0.042 } };
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(model, SystemOneContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal("https://openrouter.ai/api/v1/systemone", http.Requests[0].Url, "url");
            Body($$$"""{"model":"typesafe/jev-1.13","state":{"text":"The deployment succeeded, thank you."},"questions":{{{SystemOneQuestionsBody}}}}""", http.Requests[0].Body, "openrouter");
            Equal(new ClassifierBoolAnswer(0.95), Answer<ClassifierBoolAnswer>(result, "approved"), "bool");
            // Priced from the catalog like chat usage; matches OpenRouter's reported cost.
            Close(0.000012936, (double)result.Usage!.Cost.Total, "cost");
        }),
        ("system-one.other-apis-and-image-input-fail-before-sending", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("{}"));
            var other = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev with { Api = "cloudflare-workers-ai-system-one" }, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            var images = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev with { Input = ["text", "image"] },
                SystemOneContext with { Images = [new("aW1hZ2U=", "image/png")] }, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(0, http.Requests.Count, "requests");
            Equal("Unsupported classifier API: cloudflare-workers-ai-system-one", other.ErrorMessage, "other api");
            Equal("System One API does not support image input", images.ErrorMessage, "images");
            Equal(ModelOperationStopReason.Error, images.StopReason, "stop");
        }),
        ("system-one.headers-merge-case-insensitively-with-null-suppression", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}}}"""));
            var model = Jev with { Headers = [new("authorization", "Bearer model"), new("X-Source", "model")] };
            await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(model, SystemOneContext, new ClassifierOptions
            { ApiKey = "secret", Http = http.Client, Headers = [new("Authorization", "Bearer request"), new("x-source", "request")] });
            await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(model, SystemOneContext, new ClassifierOptions
            { ApiKey = "secret", Http = http.Client, Headers = [new("Authorization", null)] });
            await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(model, SystemOneContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal("Bearer request", http.Requests[0].Headers["authorization"], "request wins");
            Equal("request", http.Requests[0].Headers["x-source"], "request source");
            Check(!http.Requests[1].Headers.ContainsKey("authorization"), "null suppresses authorization");
            Equal("model", http.Requests[1].Headers["x-source"], "model header kept");
            Equal("Bearer model", http.Requests[2].Headers["authorization"], "model header beats the key default");
        }),
        ("system-one.prototype-question-ids-are-preserved", async () =>
        {
            var context = new ClassifierContext(Json("{}"), Questions(("__proto__", new ClassifierBoolQuestion("Is this true?", "Yes", "No"))));
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"__proto__":{"type":"noul","noul":0.75}}}"""));
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, context, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Names(["__proto__"], result.Answers.Select(pair => pair.Key), "ids");
            Equal(new ClassifierBoolAnswer(0.75), Answer<ClassifierBoolAnswer>(result, "__proto__"), "answer");
            Body("""{"model":"jev-latest","state":{},"questions":{"__proto__":{"type":"noul","instructions":"Is this true?","criteria":{"true":"Yes","false":"No"}}}}""", http.Requests[0].Body, "proto");
            Equal("""{"type":"bool","probability":0.75}""", ModelOperationJson.WriteClassifierResult(result).Value.GetProperty("answers").GetProperty("__proto__").GetRawText(), "serialized");
        }),
        ("system-one.timeouts-are-errors-not-cancellation", async () =>
        {
            var http = new FakeHttp(async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client, TimeoutMs = 5, MaxRetries = 0 });
            Equal(ModelOperationStopReason.Error, result.StopReason, "stop");
            Equal("Request timed out after 5ms", result.ErrorMessage, "error");
        }),
        ("system-one.every-retry-gets-a-fresh-timeout", async () =>
        {
            var http = new FakeHttp(async (_, attempt, token) =>
            {
                if (attempt == 1) { await Task.Delay(200, token); return FakeHttp.Text("retry", HttpStatusCode.InternalServerError, "text/plain", ("retry-after-ms", "0")); }
                await Task.Delay(200, token);
                return FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}}}""");
            });
            // Each attempt takes 200 ms of a 300 ms budget: one shared timeout would expire during the second attempt.
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client, TimeoutMs = 300, MaxRetries = 1 });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal(2, http.Requests.Count, "attempts");
            // Timeouts themselves are retried (no status).
            var slow = new FakeHttp(async (_, attempt, token) =>
            {
                if (attempt == 1) await Task.Delay(Timeout.Infinite, token);
                return FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}}}""");
            });
            result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = slow.Client, TimeoutMs = 20, MaxRetries = 1, MaxRetryDelayMs = 0 });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "retried timeout");
        }),
        ("system-one.malformed-answers-keep-billed-usage-and-malformed-usage-is-ignored", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{},"usage":{"input_tokens":10,"output_tokens":2}}"""));
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext, new ClassifierOptions { ApiKey = "secret", Http = http.Client });
            Equal(ModelOperationStopReason.Error, result.StopReason, "stop");
            Equal(0, result.Answers.Length, "answers");
            Equal("System One API did not return an answer for category", result.ErrorMessage, "error");
            Check(result.Usage is { Input: 10, Output: 2 }, "usage kept");
            var malformed = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext, new ClassifierOptions
            { ApiKey = "secret", Http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}},"usage":{"input_tokens":"many","output_tokens":3}}""")).Client });
            Equal(ModelOperationStopReason.Stop, malformed.StopReason, "stop");
            Check(malformed.Usage is { Input: 0, Output: 3, TotalTokens: 3 }, "malformed count is zero");
            var without = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext, new ClassifierOptions
            { ApiKey = "secret", Http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}},"usage":{"cost":0.1}}""")).Client });
            Equal(null, without.Usage, "no token counts");
            var wrongType = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext, new ClassifierOptions
            { ApiKey = "secret", Http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":{"category":{"type":"choice","choice":"success","probabilities":{"success":0.9},"confidence":0.8},"satisfaction":{"type":"score","score":2,"confidence":0.7},"approved":{"type":"bool","noul":0.95}}}""")).Client });
            Equal("System One API did not return a bool answer for approved", wrongType.ErrorMessage, "bool type");
        }),
        ("system-one.cancellation-and-retry-delay-cap", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var http = FakeHttp.Always(() => FakeHttp.Json("{}"));
            var aborted = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client }, cancelled.Token);
            Equal(ModelOperationStopReason.Aborted, aborted.StopReason, "aborted");
            Equal("Request aborted", aborted.ErrorMessage, "abort message");
            var capped = FakeHttp.Always(() => FakeHttp.Text("slow down", HttpStatusCode.TooManyRequests, "text/plain", ("retry-after", "120")));
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext, new ClassifierOptions { ApiKey = "secret", Http = capped.Client });
            Equal(1, capped.Requests.Count, "no retry beyond the cap");
            Equal("Server requested 120s retry delay (max: 60s). System One API returned 429", result.ErrorMessage, "cap message");
            var directive = FakeHttp.Always(() => FakeHttp.Text("no", HttpStatusCode.InternalServerError, "text/plain", ("x-should-retry", "false")));
            result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev, SystemOneContext, new ClassifierOptions { ApiKey = "secret", Http = directive.Client });
            Equal(1, directive.Requests.Count, "x-should-retry false");
            Equal("System One API error (500): no", result.ErrorMessage, "500 body");
        })
    ];

    private static ClassifierContext CloudflareContext => new(Json("""{"message":"Help! My payouts have been failing for 3 days."}"""), Questions(
        ("is_urgent", new ClassifierBoolQuestion("Does this convey urgency?", "Explicitly time-sensitive", "No urgency expressed")),
        ("department", new ClassifierChoiceQuestion("Which team should handle this?", Criteria(("billing", "Payments"), ("technical", "Bugs"))))));

    private const string CloudflareCatalog = "https://api.cloudflare.com/client/v4/accounts/{CLOUDFLARE_ACCOUNT_ID}/ai";
    private static OperationModel CloudflareModel(string id, double input) => OperationModel.FromJson(Json($$$"""
        {"type":"classifier","id":"{{{id}}}","name":"{{{id}}}","api":"cloudflare-workers-ai-system-one","provider":"cloudflare-workers-ai","baseUrl":"{{{CloudflareCatalog}}}","input":["text"],"cost":{"input":{{{input.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":128000}
        """));

    private static ModelOperationsRegistry CloudflareRegistry() =>
        new ModelOperationsRegistry(ModelOperationsAuth.Standard(_ => null)).With(BuiltinModelOperationProviders.Create("cloudflare-workers-ai",
            [CloudflareModel("typesafe/jev", 0.1), CloudflareModel("@cf/cloudflare/clef", 0.24), CloudflareModel("@cf/cloudflare/clef-flash", 0.09)]));

    private static ModelOperationsRegistry With(this ModelOperationsRegistry registry, ModelOperationsProvider provider)
    { registry.SetProvider(provider); return registry; }

    private static ClassifierOptions CloudflareAuth(FakeHttp http) => new()
    { ApiKey = "cf-key", Env = ImmutableDictionary<string, string>.Empty.Add("CLOUDFLARE_ACCOUNT_ID", "account-id"), Http = http.Client };

    private const string JevOutput = """{"model":"jev-1.13.0","answers":{"is_urgent":{"type":"noul","noul":0.95},"department":{"type":"choice","choice":"billing","confidence":0.8,"probabilities":{"billing":0.87,"technical":0.13}}},"usage":{"input_tokens":426,"output_tokens":73}}""";
    private const string ClefOutput = """{"model":"clef","answers":{"is_urgent":{"type":"noul","noul":0.9912},"department":{"type":"choice","choice":"technical","probabilities":{"billing":0.1632,"technical":0.8368},"confidence":0.4538}},"usage":{"input_tokens":222,"output_tokens":0}}""";
    private const string CloudflareQuestionsBody = """{"is_urgent":{"type":"noul","instructions":"Does this convey urgency?","criteria":{"true":"Explicitly time-sensitive","false":"No urgency expressed"}},"department":{"type":"choice","instructions":"Which team should handle this?","criteria":{"billing":"Payments","technical":"Bugs"}}}""";

    private static IEnumerable<(string, Func<Task>)> CloudflareCases() =>
    [
        ("cloudflare.jev-runs-on-the-account-scoped-run-endpoint", async () =>
        {
            var registry = CloudflareRegistry();
            var jev = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "cloudflare-workers-ai", "typesafe/jev")!;
            Equal(null, registry.GetModelOfType(ModelType.Chat, "cloudflare-workers-ai", "typesafe/jev"), "classifier only");
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"result":{"state":"Completed","result":{{{JevOutput}}},"gatewayMetadata":{"keySource":"Unified"}},"success":true,"errors":[],"messages":[]}"""));
            var result = await registry.ClassifyAsync(jev, CloudflareContext, CloudflareAuth(http));
            Equal("https://api.cloudflare.com/client/v4/accounts/account-id/ai/run", http.Requests[0].Url, "url");
            Equal("Bearer cf-key", http.Requests[0].Headers["authorization"], "authorization");
            Body($$$"""{"model":"typesafe/jev","input":{"state":{"message":"Help! My payouts have been failing for 3 days."},"questions":{{{CloudflareQuestionsBody}}}}}""", http.Requests[0].Body, "jev");
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal(new ClassifierBoolAnswer(0.95), Answer<ClassifierBoolAnswer>(result, "is_urgent"), "urgent");
            var department = Answer<ClassifierChoiceAnswer>(result, "department");
            Equal("billing", department.Choice, "choice"); Equal(0.8, department.Confidence, "confidence");
            Check(result.Usage is { Input: 426, Output: 73, TotalTokens: 499 }, "usage");
        }),
        ("cloudflare.hosted-models-return-their-output-directly", async () =>
        {
            foreach (var (id, price) in new[] { ("@cf/cloudflare/clef", 0.24), ("@cf/cloudflare/clef-flash", 0.09) })
            {
                var registry = CloudflareRegistry();
                var clef = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "cloudflare-workers-ai", id)!;
                var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"result":{{{ClefOutput}}},"success":true,"errors":[],"messages":[]}"""));
                var result = await registry.ClassifyAsync(clef, CloudflareContext, CloudflareAuth(http));
                Equal("https://api.cloudflare.com/client/v4/accounts/account-id/ai/run", http.Requests[0].Url, "url");
                Equal(id, http.Requests[0].Json.GetProperty("model").GetString(), "model");
                Equal(new ClassifierBoolAnswer(0.9912), Answer<ClassifierBoolAnswer>(result, "is_urgent"), "urgent");
                Equal("technical", Answer<ClassifierChoiceAnswer>(result, "department").Choice, "choice");
                Check(result.Usage is { Input: 222, Output: 0, TotalTokens: 222 }, "usage");
                Close(222 * price / 1_000_000, (double)result.Usage!.Cost.Input, "input cost");
            }
        }),
        ("cloudflare.unfinished-runs-and-envelope-errors", async () =>
        {
            var registry = CloudflareRegistry();
            var jev = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "cloudflare-workers-ai", "typesafe/jev")!;
            var queued = await registry.ClassifyAsync(jev, CloudflareContext, CloudflareAuth(FakeHttp.Always(() =>
                FakeHttp.Json("""{"result":{"state":"Queued","result":null},"success":true,"errors":[],"messages":[]}"""))));
            Equal(ModelOperationStopReason.Error, queued.StopReason, "stop");
            Equal("Cloudflare Workers AI run did not complete (state: Queued)", queued.ErrorMessage, "queued");
            var failed = await registry.ClassifyAsync(jev, CloudflareContext, CloudflareAuth(FakeHttp.Always(() =>
                FakeHttp.Json("""{"success":false,"errors":[{"code":5007,"message":"No such model"},{"code":1}],"result":null}"""))));
            Equal("Cloudflare Workers AI error: No such model", failed.ErrorMessage, "envelope");
            var bare = await registry.ClassifyAsync(jev, CloudflareContext, CloudflareAuth(FakeHttp.Always(() => FakeHttp.Json("""{"success":false}"""))));
            Equal("Cloudflare Workers AI request failed", bare.ErrorMessage, "no messages");
            var missingState = await registry.ClassifyAsync(jev, CloudflareContext, CloudflareAuth(FakeHttp.Always(() => FakeHttp.Json("""{"success":true,"result":{}}"""))));
            Equal("Cloudflare Workers AI run did not complete (state: undefined)", missingState.ErrorMessage, "undefined state");
        }),
        ("cloudflare.account-id-from-the-environment-and-missing-configuration", async () =>
        {
            var env = new Dictionary<string, string> { ["CLOUDFLARE_API_KEY"] = "env-key", ["CLOUDFLARE_ACCOUNT_ID"] = "env-account" };
            var registry = new ModelOperationsRegistry(ModelOperationsAuth.Standard(name => env.GetValueOrDefault(name)))
                .With(BuiltinModelOperationProviders.Create("cloudflare-workers-ai", [CloudflareModel("typesafe/jev", 0.1)]));
            var jev = (ClassifierModel)registry.GetModelOfType(ModelType.Classifier, "cloudflare-workers-ai", "typesafe/jev")!;
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"result":{{{ClefOutput}}},"success":true}"""));
            var result = await registry.ClassifyAsync(jev, CloudflareContext, new ClassifierOptions { Http = http.Client });
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal("https://api.cloudflare.com/client/v4/accounts/env-account/ai/run", http.Requests[0].Url, "env account");
            Equal("Bearer env-key", http.Requests[0].Headers["authorization"], "env key");
            env.Remove("CLOUDFLARE_ACCOUNT_ID");
            var unconfigured = await registry.ClassifyAsync(jev, CloudflareContext, new ClassifierOptions { Http = http.Client });
            Equal("Provider is not configured: cloudflare-workers-ai", unconfigured.ErrorMessage, "missing account");
            Equal(1, http.Requests.Count, "no request without an account");
        })
    ];
}
