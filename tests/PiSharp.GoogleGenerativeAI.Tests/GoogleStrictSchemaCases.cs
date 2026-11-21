using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.Contracts;

/// <summary>Authored pinned-Source strict-schema expectations, unexecuted until native ownership is reopened.</summary>
internal static class GoogleStrictSchemaCases
{
    private static readonly ModelDescriptor Model = new("gemini-3-flash-preview", "google-generative-ai", "google");
    private const string Key = "inert-authored-schema-key";
    private static readonly JsonData Metadata = JsonData.Parse("""
        {"id":"gemini-3-flash-preview","api":"google-generative-ai","provider":"google","baseUrl":"https://google.invalid/v1beta",
        "reasoning":true,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"opaque":{"keep":null}}
        """);
    // Pi 1.6.0 constrained-sampling.ts (faac585884ae2c013a6b1400f3e9e77f10b34bac, #12049): a node without a type is not strict, and a
    // bare const does not count as typed (a bare enum does). The authored vector file predates the rule and is hash-pinned, so it
    // stays byte-identical: this names the one vector whose expectation the rule changed, and the vectors below add its cases.
    private static readonly HashSet<string> UntypedSince160 = new(StringComparer.Ordinal) { "supported-optional-null-const" };
    private const string Sync160Vectors = """
        [
          { "id": "sync160-untyped-array-items", "supported": false,
            "schema": {"type":"object","properties":{"x":{"type":"array","items":{}}}} },
          { "id": "sync160-untyped-description-only", "supported": false,
            "schema": {"type":"object","properties":{"x":{"description":"Any JSON value"}},"required":["x"]} },
          { "id": "sync160-untyped-nested-object-property", "supported": false,
            "schema": {"type":"object","properties":{"x":{"type":"object","properties":{"y":{}},"required":["y"]}},"required":["x"]} },
          { "id": "sync160-supported-enum-only", "supported": true,
            "schema": {"type":"object","properties":{"x":{"enum":["low","high"]}},"required":["x"]},
            "expectedSchema": {"type":"object","properties":{"x":{"enum":["low","high"]}},"required":["x"],"additionalProperties":false} },
          { "id": "sync160-supported-anyof-without-type", "supported": true,
            "schema": {"type":"object","properties":{"x":{"anyOf":[{"type":"string"},{"type":"number"}]}},"required":["x"]},
            "expectedSchema": {"type":"object","properties":{"x":{"anyOf":[{"type":"string"},{"type":"number"}]}},"required":["x"],"additionalProperties":false} }
        ]
        """;
    public static async Task RunAsync()
    {
        var vectors = JsonData.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "strict-schema-cases.json")));
        var root = vectors.Value;
        Check(root.GetProperty("status").GetString() == "AUTHORED_SOURCE_INFORMED_UNEXECUTED", "Unlabeled schema expectations.");
        Check(root.GetProperty("sourceBaseline").GetString() == "d86654abb8862e201933517d6f1fce9f88dd117f", "Wrong Source baseline.");
        var sync160 = JsonData.Parse(Sync160Vectors);
        Check(root.GetProperty("cases").EnumerateArray().Count(vector => UntypedSince160.Contains(vector.GetProperty("id").GetString()!)) == UntypedSince160.Count,
            "A vector renamed away from its Pi 1.6.0 expectation.");
        foreach (var vector in root.GetProperty("cases").EnumerateArray().Concat(sync160.Value.EnumerateArray()))
        foreach (var mode in new[] { "require", "prefer" })
        {
            var id = vector.GetProperty("id").GetString()!; var supported = vector.GetProperty("supported").GetBoolean() && !UntypedSince160.Contains(id);
            var schema = JsonData.FromElement(vector.GetProperty("schema")); var originalSchema = schema.ToString();
            var declaration = new JsonObject { ["role"] = "system", ["content"] = "schema control",
                ["toolsAdded"] = new JsonArray(new JsonObject { ["name"] = "inspect", ["description"] = "inert",
                    ["parameters"] = JsonNode.Parse(schema.ToString()), ["constrainedSampling"] = new JsonObject { ["type"] = "json_schema", ["strict"] = mode } }) };
            var request = new ChatRequest(Model, ImmutableArray.Create(new TranscriptEntry("system", JsonData.Parse(declaration.ToJsonString())),
                new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":123}"""))), 123);
            var originalInputs = request.Messages.Select(x => x.WireBody.ToString()).ToArray();
            var payloadHooks = 0; JsonData? sdk = null;
            var options = new GoogleGenerativeAIOptions(Metadata, Key) { Hooks = new() { OnPayload = (payload, selected, _) =>
            {
                payloadHooks++; sdk = payload;
                Check(selected.Identity == Model && JsonElement.DeepEquals(selected.Raw.Value, Metadata.Value), id + ": incomplete model observation.");
                return ValueTask.FromResult<JsonData?>(null);
            } } };
            if (!supported && mode == "require")
            {
                // Exercise public projection and real ChatClient separately, before any HTTP/payload effect.
                try { _ = GoogleRequestProjector.Project(request, options); throw new InvalidOperationException(id + ": required schema admitted."); }
                catch (GoogleGenerativeAIException error) when (error.Failure == GoogleFailure.UnsupportedValue) { }
            }
            using var handler = new Handler();
            using var client = new HttpClient(handler);
            var result = await new ChatClient(new GoogleGenerativeAIHttpTransport(client, Model, options), capacity: 1).CompleteAsync(request);
            if (!supported && mode == "require")
            {
                Check(result.Failure is not null && result.Message.StopReason == StopReason.Error, id + ": required schema accepted by consumer.");
                Check(handler.Sends == 0 && payloadHooks == 0 && sdk is null, id + ": required rejection admitted HTTP/payload effects.");
            }
            else
            {
                Check(result.Failure is null && result.Message.StopReason == StopReason.Stop && handler.Sends == 1 && payloadHooks == 1,
                    id + ": supported/preferred request failed actual consumer.");
                var expected = supported ? vector.GetProperty("expectedSchema") : schema.Value;
                var config = sdk!.Value.GetProperty("config");
                var projected = config.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parametersJsonSchema");
                Check(JsonElement.DeepEquals(expected, projected), id + ": SDK schema differs from authored expectation.");
                var wire = handler.Body!.Value;
                Check(JsonElement.DeepEquals(expected, wire.GetProperty("tools")[0].GetProperty("functionDeclarations")[0].GetProperty("parametersJsonSchema")),
                    id + ": actual REST schema differs.");
                if (supported)
                {
                    Check(config.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString() == "VALIDATED",
                        id + ": supported schema lost strict capability.");
                    Check(wire.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString() == "VALIDATED",
                        id + ": strict mode missing from actual REST request.");
                }
                else
                {
                    Check(!config.TryGetProperty("toolConfig", out _) && !wire.TryGetProperty("toolConfig", out _),
                        id + ": preferred fallback falsely asserted VALIDATED.");
                    Check(JsonElement.DeepEquals(schema.Value, projected), id + ": preferred schema was transformed.");
                }
            }
            Check(!handler.Disposed, id + ": borrowed client disposed.");
            Check(schema.ToString() == originalSchema, id + ": original schema bytes changed.");
            for (var i = 0; i < originalInputs.Length; i++)
                Check(request.Messages[i].WireBody.ToString() == originalInputs[i], id + ": input transcript mutated.");
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Sends;
        internal bool Disposed;
        internal JsonData? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; Body = JsonData.Parse(await request.Content!.ReadAsStringAsync(token));
            Check(request.RequestUri!.Host == "google.invalid" && request.Headers.GetValues("x-goog-api-key").Single() == Key,
                "Schema control left its fake key-auth endpoint.");
            return new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(
                "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n"))) };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
