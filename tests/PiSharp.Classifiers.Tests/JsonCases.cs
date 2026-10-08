using System.Text.Json;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// types.ts ClassifierContext/ClassifierResult/ImagesContext/AssistantImages JSON shapes and JavaScript property order.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> JsonCases() =>
    [
        ("json.classifier-context-parses-in-javascript-property-order-and-round-trips", Sync(() =>
        {
            var context = ModelOperationJson.ParseClassifierContext(Json("""
                {"state":{"x":1},"images":[{"type":"image","data":"QQ==","mimeType":"image/png"}],"questions":{"b":{"type":"bool","instructions":"B?","criteria":{"true":"y","false":"n"}},"1":{"type":"choice","instructions":"C?","criteria":{"z":"","2":"two"}},"s":{"type":"score","instructions":"S?","criteria":["lo","hi"]}}}
                """).Value);
            Names(["1", "b", "s"], context.Questions.Select(pair => pair.Key), "question order");
            Names(["2", "z"], ((ClassifierChoiceQuestion)context.Questions[0].Value).Criteria.Select(pair => pair.Key), "criteria order");
            Equal(new ImageContent("QQ==", "image/png"), context.ImageList[0], "image");
            Equal("""{"state":{"x":1},"images":[{"type":"image","data":"QQ==","mimeType":"image/png"}],"questions":{"1":{"type":"choice","instructions":"C?","criteria":{"2":"two","z":""}},"b":{"type":"bool","instructions":"B?","criteria":{"true":"y","false":"n"}},"s":{"type":"score","instructions":"S?","criteria":["lo","hi"]}}}""",
                ModelOperationJson.WriteClassifierContext(context).ToString(), "round trip");
            string Fail(string json) { try { ModelOperationJson.ParseClassifierContext(Json(json).Value); return "parsed"; } catch (FormatException error) { return error.Message; } }
            Equal("context.state must be an object.", Fail("""{"state":[],"questions":{}}"""), "state");
            Equal("context.questions.q is a \"score\" question, so criteria must list the levels as strings, lowest first.",
                Fail("""{"state":{},"questions":{"q":{"type":"score","instructions":"","criteria":[1]}}}"""), "score criteria");
            Equal("context.questions.q.type must be \"choice\", \"score\", or \"bool\".", Fail("""{"state":{},"questions":{"q":{"type":"rank","instructions":""}}}"""), "type");
            Equal("context.images[0] must be an image block.", Fail("""{"state":{},"images":[{"type":"text","text":"x"}],"questions":{}}"""), "image block");
        })),
        ("json.results-and-image-contexts-use-the-upstream-shapes", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json($$$"""{"answers":{{{SystemOneAnswers}}},"usage":{"input_tokens":308,"output_tokens":23}}"""));
            var result = await TypeSafeSystemOneClassifier.Instance.ClassifyAsync(Jev with { Cost = Jev.Cost with { Input = 0.042 } }, SystemOneContext,
                new ClassifierOptions { ApiKey = "secret", Http = http.Client, TimeProvider = new FixedTime(1700000000000) });
            Equal("""{"api":"typesafe-system-one","provider":"typesafe","model":"jev-latest","answers":{"category":{"type":"choice","choice":"success","probabilities":{"success":0.9,"failure":0.1},"confidence":0.8},"satisfaction":{"type":"score","score":2,"confidence":0.7},"approved":{"type":"bool","probability":0.95}},"stopReason":"stop","timestamp":1700000000000,"usage":{"input":308,"output":23,"cacheRead":0,"cacheWrite":0,"totalTokens":331,"cost":{"input":0.000012936000000000001,"output":0,"cacheRead":0,"cacheWrite":0,"total":0.000012936000000000001}}}""",
                ModelOperationJson.WriteClassifierResult(result).ToString(), "classifier result");
            var failed = new ClassifierResult("a", "p", "m", [], ModelOperationStopReason.Aborted, 5) { ErrorMessage = "Request aborted" };
            Equal("""{"api":"a","provider":"p","model":"m","answers":{},"stopReason":"aborted","timestamp":5,"errorMessage":"Request aborted"}""",
                ModelOperationJson.WriteClassifierResult(failed).ToString(), "error result");
            var images = new AssistantImages("openrouter-images", "openrouter", "x", [new ImagesTextBlock("hi"), new ImageContent("QQ==", "image/png")], ModelOperationStopReason.Stop, 7) { ResponseId = "r" };
            Equal("""{"api":"openrouter-images","provider":"openrouter","model":"x","output":[{"type":"text","text":"hi"},{"type":"image","data":"QQ==","mimeType":"image/png"}],"stopReason":"stop","timestamp":7,"responseId":"r"}""",
                ModelOperationJson.WriteAssistantImages(images).ToString(), "images result");
            var parsed = ModelOperationJson.ParseImagesContext(Json("""{"input":[{"type":"text","text":"a cat"},{"type":"image","data":"QQ==","mimeType":"image/jpeg"}]}""").Value);
            Equal("""{"input":[{"type":"text","text":"a cat"},{"type":"image","data":"QQ==","mimeType":"image/jpeg"}]}""", ModelOperationJson.WriteImagesContext(parsed).ToString(), "images context");
            var luna = Luna.ToPublicJson().Value;
            Equal(922000, luna.GetProperty("contextWindow").GetInt32(), "model json");
            Check(!Image(GeminiImage.ToJson().ToString()).ToPublicJson().Value.TryGetProperty("headers", out _), "public json drops headers");
            Check(GeminiImage.ToJson().Value.TryGetProperty("headers", out _), "full json keeps headers");
        })
    ];

    private sealed class FixedTime(long milliseconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }
}
