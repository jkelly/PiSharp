// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/google-generative-ai.ts, google-vertex.ts and google-shared.ts
// (the SDK parameters), sent through @google/genai 2.21.0 generateContentParametersToMldev / generateContentParametersToVertex.
using System.Collections.Immutable;
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.AI.Protocols.GoogleGenerativeAI;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Expected bodies were captured by running the installed pi-ai 1.1.0 google-generative-ai and google-vertex streamSimple (with
// @google/genai 2.21.0) against a local HTTP server, for exactly this transcript.
internal static partial class Program
{
    private static ImmutableArray<TranscriptEntry> GoogleWireTranscript(string api, string provider, string id) =>
    [
        Entry("""{"role":"system","content":"SYS","toolsAdded":[{"name":"read","description":"Read a file","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}],"timestamp":0}"""),
        Entry("""{"role":"user","content":[{"type":"text","text":"look <here> & π"},{"type":"image","mimeType":"image/png","data":"AA=="}],"timestamp":1}"""),
        Entry("""{"role":"assistant","content":[{"type":"thinking","thinking":"plan","thinkingSignature":"c2ln"},{"type":"text","text":"reading","textSignature":"dGV4dA=="},{"type":"toolCall","id":"call-1","name":"read","arguments":{"path":"a.txt"},"thoughtSignature":"dG9vbA=="}],""" +
            "\"api\":\"" + api + "\",\"provider\":\"" + provider + "\",\"model\":\"" + id + "\",\"usage\":" +
            """{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}},"stopReason":"toolUse","timestamp":2}"""),
        Entry("""{"role":"toolResult","toolCallId":"call-1","toolName":"read","content":[{"type":"text","text":"file body"}],"isError":false,"timestamp":3}"""),
        Entry("""{"role":"user","content":"next","timestamp":4}"""),
    ];

    private static string GoogleWireExpected(string id, bool vertex, string generation)
    {
        var ids = id.StartsWith("gemini-3", StringComparison.Ordinal);
        var call = vertex ? "{\"name\":\"read\",\"args\":{\"path\":\"a.txt\"}" + (ids ? ",\"id\":\"call-1\"" : "") + "}"
            : "{\"args\":{\"path\":\"a.txt\"}" + (ids ? ",\"id\":\"call-1\"" : "") + ",\"name\":\"read\"}";
        var inline = vertex ? "{\"mimeType\":\"image/png\",\"data\":\"AA==\"}" : "{\"data\":\"AA==\",\"mimeType\":\"image/png\"}";
        return "{\"contents\":[{\"parts\":[{\"text\":\"look <here> & π\"},{\"inlineData\":" + inline + "}],\"role\":\"user\"}," +
            "{\"parts\":[{\"text\":\"plan\",\"thought\":true,\"thoughtSignature\":\"c2ln\"},{\"text\":\"reading\",\"thoughtSignature\":\"dGV4dA==\"},{\"functionCall\":" + call +
            ",\"thoughtSignature\":\"dG9vbA==\"}],\"role\":\"model\"}," +
            "{\"parts\":[{\"functionResponse\":{\"name\":\"read\",\"response\":{\"output\":\"file body\"}" + (ids ? ",\"id\":\"call-1\"" : "") + "}}],\"role\":\"user\"}," +
            "{\"parts\":[{\"text\":\"next\"}],\"role\":\"user\"}]," +
            "\"systemInstruction\":{\"parts\":[{\"text\":\"SYS\"}],\"role\":\"user\"}," +
            "\"tools\":[{\"functionDeclarations\":[{\"name\":\"read\",\"description\":\"Read a file\",\"parametersJsonSchema\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}}]}]," +
            "\"generationConfig\":" + generation + "}";
    }

    private static async Task GoogleWireBodies()
    {
        var mismatches = new List<string>();
        foreach (var id in new[] { "gemini-2.5-flash", "gemini-3-flash-preview" })
        {
            var budget = id == "gemini-2.5-flash";
            foreach (var (level, temperature, maximum, generation) in new[]
            {
                ("medium", (double?)0.2, 1000d, "{\"temperature\":0.2,\"maxOutputTokens\":1000,\"thinkingConfig\":{\"includeThoughts\":true," +
                    (budget ? "\"thinkingBudget\":8192" : "\"thinkingLevel\":\"MEDIUM\"") + "}}"),
                ((string?)null, (double?)null, 65536d, "{\"maxOutputTokens\":65536,\"thinkingConfig\":{" + (budget ? "\"thinkingBudget\":0" : "\"thinkingLevel\":\"MINIMAL\"") + "}}"),
            })
            {
                // google-generative-ai through the catalog route.
                string? sent = null;
                using (var handler = new Handler(async request =>
                {
                    sent = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }))
                {
                    var model = new ModelDescriptor(id, "google-generative-ai", "google"); var row = CatalogRow("google", id);
                    using var provider = NativeProviderFactory.CreateCatalogGoogle(model, Key, row,
                        new GoogleGenerativeAIOptions(row) { MaxTokens = maximum, Temperature = temperature }, handler);
                    await new ChatClient(provider.Transport).CompleteAsync(new(model, GoogleWireTranscript(model.Api, model.Provider, id), 5) { ThinkingLevel = level }).WaitAsync(Deadline);
                }
                var expected = GoogleWireExpected(id, false, generation);
                if (sent != expected) mismatches.Add("google " + id + " " + (level ?? "-") + "\n  expected " + expected + "\n  actual   " + sent);

                // google-vertex through the live Vertex route (API-key express mode; this route sets no temperature).
                string? vertexSent = null;
                using (var handler = new Handler(async request =>
                {
                    vertexSent = await request.Content!.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }))
                {
                    var model = new ModelDescriptor(id, "google-vertex", "google-vertex"); var row = CatalogRow("google-vertex", id);
                    using var provider = NativeProviderFactory.CreateGoogleVertexRoute(model, row, _ => ValueTask.FromResult(new GoogleVertexRequestAuth(
                        PiSharp.AI.Protocols.GoogleVertex.GoogleVertexEndpoints.ApiKey(id, null), "vertex-key", true, "-", "-")), maximum, false, handler);
                    await new ChatClient(provider.Transport).CompleteAsync(new(model, GoogleWireTranscript(model.Api, model.Provider, id), 5) { ThinkingLevel = level }).WaitAsync(Deadline);
                }
                var vertexExpected = GoogleWireExpected(id, true, generation.Replace("\"temperature\":0.2,", "", StringComparison.Ordinal));
                if (vertexSent != vertexExpected) mismatches.Add("vertex " + id + " " + (level ?? "-") + "\n  expected " + vertexExpected + "\n  actual   " + vertexSent);
            }
        }
        if (mismatches.Count != 0) throw new InvalidOperationException(string.Join("\n", mismatches));
    }
}
