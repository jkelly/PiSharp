using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Contracts;

// Handwritten expectations from pinned transform-messages.ts first and second passes.
// AUTHORED_UNCOMPILED_UNEXECUTED; fake HTTP is not an SDK or Source capture.
internal static class GoogleHistoryIdCases
{
    private const string ModelId = "gemini-3-flash-preview";
    private static TranscriptEntry Call(string provider = "foreign", string stop = "toolUse", string api = "google-generative-ai") =>
        new("assistant", JsonData.Parse(new JsonObject
        {
            ["role"] = "assistant", ["provider"] = provider, ["api"] = api, ["model"] = ModelId,
            ["stopReason"] = stop, ["timestamp"] = 1,
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "toolCall", ["id"] = "call|one", ["name"] = "inspect",
                ["arguments"] = new JsonObject(), ["thoughtSignature"] = "c2ln"
            })
        }.ToJsonString()));
    private static TranscriptEntry Result() => new("toolResult", JsonData.Parse(
        """{"role":"toolResult","toolCallId":"call|one","toolName":"inspect","content":[{"type":"text","text":"ok"}],"isError":false,"timestamp":2}"""));
    private static IEnumerable<(string Name, string Model, ImmutableArray<TranscriptEntry> Messages, string[] Expected)> Vectors()
    {
        yield return ("failed-foreign-error", ModelId, [Call(stop: "error"), Result()], ["user:result:call_one:output:ok"]);
        yield return ("failed-foreign-aborted", ModelId, [Call(stop: "aborted"), Result()], ["user:result:call_one:output:ok"]);
        yield return ("same-model-reuse-retains-prior-map", ModelId, [Call(), Call("google"), Result()],
            ["model:call:call_one:unsigned", "user:result:call_one:error:No result provided",
             "model:call:call|one:signed", "user:result:call_one:output:ok", "user:result:call|one:error:No result provided"]);
        yield return ("ordinary-foreign-answer-no-orphan", ModelId, [Call(), Result()],
            ["model:call:call_one:unsigned", "user:result:call_one:output:ok"]);
        yield return ("same-model-without-prior-map", ModelId, [Call("google"), Result()],
            ["model:call:call|one:signed", "user:result:call|one:output:ok"]);
        yield return ("different-api-is-foreign", ModelId, [Call("google", api: "other-api"), Result()],
            ["model:call:call_one:unsigned", "user:result:call_one:output:ok"]);
        yield return ("pre3-omits-identities", "gemini-2.5-flash", [Call(), Result()],
            ["model:call:<absent>:unsigned", "user:result:<absent>:output:ok"]);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void CheckContents(JsonElement contents, string[] expected, string name)
    {
        var actual = new List<string>();
        foreach (var turn in contents.EnumerateArray())
        {
            var role = turn.GetProperty("role").GetString();
            foreach (var part in turn.GetProperty("parts").EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var call))
                {
                    Check(call.GetProperty("name").GetString() == "inspect", name + ": call name");
                    Check(call.GetProperty("args").EnumerateObject().Count() == 0, name + ": args");
                    var id = call.TryGetProperty("id", out var value) ? value.GetString() : "<absent>";
                    var signed = part.TryGetProperty("thoughtSignature", out var signature);
                    if (signed) Check(signature.GetString() == "c2ln", name + ": signature");
                    actual.Add($"{role}:call:{id}:{(signed ? "signed" : "unsigned")}");
                }
                else
                {
                    var response = part.GetProperty("functionResponse");
                    Check(response.GetProperty("name").GetString() == "inspect", name + ": result name");
                    var id = response.TryGetProperty("id", out var value) ? value.GetString() : "<absent>";
                    var body = response.GetProperty("response");
                    var fields = body.EnumerateObject().ToArray();
                    Check(fields.Length == 1, name + ": result shape");
                    actual.Add($"{role}:result:{id}:{fields[0].Name}:{fields[0].Value.GetString()}");
                }
            }
        }
        Check(expected.SequenceEqual(actual), name + ": history identity/order mismatch: " + string.Join("; ", actual));
    }
    internal static async Task RunAsync()
    {
        foreach (var vector in Vectors())
        {
            var model = new ModelDescriptor(vector.Model, "google-generative-ai", "google");
            var metadata = JsonNode.Parse("""{"id":"","api":"google-generative-ai","provider":"google","baseUrl":"https://google.invalid/v1beta","reasoning":true,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""")!;
            metadata["id"] = model.Id;
            var options = new GoogleGenerativeAIOptions(JsonData.Parse(metadata.ToJsonString()), "inert-history-test-key");
            var request = new ChatRequest(model, vector.Messages, 123);
            var before = request.Messages.Select(m => m.WireBody.ToString()).ToArray();
            CheckContents(GoogleRequestProjector.Project(request, options).Value.GetProperty("contents"), vector.Expected, vector.Name + ": direct");
            var hooks = 0; var sends = 0;
            options = options with { Hooks = new() { OnPayload = (payload, _, _) =>
            {
                hooks++;
                CheckContents(payload.Value.GetProperty("contents"), vector.Expected, vector.Name + ": hook");
                return ValueTask.FromResult<JsonData?>(null);
            } } };
            using var handler = new Handler(async (http, token) =>
            {
                sends++;
                var wire = JsonData.Parse(await http.Content!.ReadAsStringAsync(token));
                CheckContents(wire.Value.GetProperty("contents"), vector.Expected, vector.Name + ": REST");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}]}\n\n", Encoding.UTF8, "text/event-stream")
                };
            });
            using var client = new HttpClient(handler);
            var result = await new ChatClient(new GoogleGenerativeAIHttpTransport(client, model, options), capacity: 1).CompleteAsync(request);
            Check(result.Message.StopReason == StopReason.Stop, vector.Name + ": transport failed");
            Check(hooks == 1 && sends == 1 && !handler.Disposed, vector.Name + ": effects/borrowed ownership");
            Check(before.SequenceEqual(request.Messages.Select(m => m.WireBody.ToString())), vector.Name + ": caller mutation");
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
