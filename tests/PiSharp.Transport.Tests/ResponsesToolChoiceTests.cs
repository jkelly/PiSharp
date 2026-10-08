using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Authored from pinned buildParams; no source/native execution performed.
internal static class ResponsesToolChoiceTests
{
    private static readonly ModelDescriptor Model = new("choice-model", "openai-responses", "openai");
    private static readonly Uri Endpoint = new("https://synthetic.invalid/v1/responses");
    private static ChatRequest Request(bool tools = false) => new(Model, tools ?
        [new("system", JsonData.Parse("""{"role":"system","content":"Use tools","toolsAdded":[{"name":"lookup","description":"Lookup","parameters":{"type":"object","properties":{}}}]}"""))] : [], 1);
    private static ResponsesKeyAuthRequestFactory Factory(JsonData? choice, int bytes = 1_048_576) =>
        new(Endpoint, Model, new(false), new(ToolChoice: choice, MaximumPayloadBytes: bytes));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-tool-choice.modes-named-omission-and-declarations", Projection);
        yield return ("responses-tool-choice.invalid-shapes-and-exact-byte-boundary", Admission);
        yield return ("responses-tool-choice.fake-http-repeat-and-pre-cancel", Transport);
        yield return ("responses-tool-choice.unicode-decoding-failure-boundary", UnicodeDecoding);
    }
    private static async Task Projection()
    {
        foreach (var tools in new[] { false, true })
        foreach (var raw in new string?[] { null, "\"auto\"", "\"none\"", "\"required\"", """{"type":"function","name":"lookup"}""", """{"name":"未宣言😀","type":"function"}""" })
        {
            var choice = raw is null ? null : JsonData.Parse(raw);
            var before = choice?.ToString();
            var request = Request(tools);
            var history = request.Messages.Select(m => m.WireBody.ToString()).ToArray();
            var factory = Factory(choice);
            using var first = factory.Create(request, "inert-key");
            using var second = factory.Create(request, "inert-key");
            var text = await first.Content!.ReadAsStringAsync();
            Check(text == await second.Content!.ReadAsStringAsync(), "Request reuse changed payload");
            var payload = JsonData.Parse(text).Value;
            Check(payload.TryGetProperty("tool_choice", out var projected) == (choice is not null), "Wrong choice omission");
            if (choice is not null) Check(JsonElement.DeepEquals(choice.Value, projected), "Choice transformed");
            Check(payload.TryGetProperty("tools", out var declarations) == tools, "Choice changed active declarations");
            if (tools) Check(declarations.GetArrayLength() == 1 && declarations[0].GetProperty("name").GetString() == "lookup", "Declarations rewritten");
            Check(choice?.ToString() == before && request.Messages.Select(m => m.WireBody.ToString()).SequenceEqual(history), "Caller input mutated");
            Check(!ReferenceEquals(first.Content, second.Content), "Request body ownership reused");
        }
    }
    private static async Task Admission()
    {
        foreach (var raw in new[] { "null", "true", "0", "[]", "{}", "\"any\"", "\"AUTO\"",
            """{"type":"function"}""", """{"type":"function","name":""}""", """{"type":"function","name":false}""",
            """{"type":"custom","name":"lookup"}""", """{"type":"function","function":{"name":"lookup"}}""",
            """{"type":"function","name":"lookup","extra":1}""", """{"type":"allowed_tools","mode":"auto","tools":[]}""" })
        {
            try { Factory(JsonData.Parse(raw)); }
            catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.UnsupportedOptions) { continue; }
            throw new InvalidOperationException("Unsupported choice admitted: " + raw);
        }
        var choice = JsonData.Parse("""{"type":"function","name":"未宣言😀"}""");
        using var baseline = Factory(choice).Create(Request(), "inert-key");
        var bytes = (await baseline.Content!.ReadAsByteArrayAsync()).Length;
        using var exact = Factory(choice, bytes).Create(Request(), "inert-key");
        Check((await exact.Content!.ReadAsByteArrayAsync()).Length == bytes, "Exact byte budget rejected");
        try { using var rejected = Factory(choice, bytes - 1).Create(Request(), "inert-key"); }
        catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.ResourceLimit) { return; }
        throw new InvalidOperationException("Choice escaped aggregate payload byte budget");
    }
    private static async Task UnicodeDecoding()
    {
        foreach (var raw in new[]
        {
            "\"\\uD800\"", "\"\\uDC00\"",
            """{"type":"\uD800","name":"lookup"}""", """{"type":"\uDC00","name":"lookup"}""",
            """{"type":"function","name":"\uD800"}""", """{"type":"function","name":"\uDC00"}"""
        })
        {
            try { Factory(JsonData.Parse(raw)); }
            catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.InvalidConfiguration)
            {
                Check(!error.Message.Contains("D800", StringComparison.Ordinal) && !error.Message.Contains("DC00", StringComparison.Ordinal), "Rejected data leaked in diagnostic");
                continue;
            }
            throw new InvalidOperationException("Malformed UTF-16 choice escaped configuration boundary");
        }
        var valid = JsonData.Parse("""{"type":"function","name":"\uD83D\uDE00"}""");
        using var request = Factory(valid).Create(Request(), "inert-key");
        var payload = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
        Check(payload.GetProperty("tool_choice").GetProperty("name").GetString() == "😀", "Valid surrogate pair rejected or changed");
        foreach (var raw in new[] { "\"\\uD83D\\uDE00\"", """{"type":"\uD83D\uDE00","name":"lookup"}""" })
        {
            try { Factory(JsonData.Parse(raw)); }
            catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.UnsupportedOptions) { continue; }
            throw new InvalidOperationException("Valid Unicode unsupported mode/type misclassified");
        }
    }
    private static async Task Transport()
    {
        foreach (var raw in new[] { "\"required\"", """{"type":"function","name":"lookup"}""" })
        {
            var choice = JsonData.Parse(raw);
            var sends = 0;
            using var handler = new ResponsesReasoningTests.Handler(async request =>
            {
                sends++;
                var payload = JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value;
                Check(JsonElement.DeepEquals(payload.GetProperty("tool_choice"), choice.Value), "HTTP body lost tool choice");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
            });
            using var http = new HttpClient(handler);
            var factory = Factory(choice);
            var transport = new ResponsesHttpSseTransport(http, request => factory.Create(request, "inert-key"));
            var client = new ChatClient(transport);
            for (var index = 0; index < 2; index++)
                Check((await client.CompleteAsync(Request(true))).Failure is null, "Fake HTTP roundtrip failed");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var result = await client.CompleteAsync(Request(true), canceled.Token);
            Check(result.Failure?.Kind == ChatFailureKind.Cancelled && sends == 2, "Pre-cancel acquired HTTP");
        }
    }
}