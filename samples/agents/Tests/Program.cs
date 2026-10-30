using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Samples;
using PiSharp.AI.Catalogs;
using PiSharp.Contracts;

var tests = new (string Name, Func<Task> Test)[]
{
    ("real agent executes tools and saves replayable history", ExecuteAndResume),
    ("approval denial prevents side effects", Approval),
    ("invalid schema prevents side effects", InvalidArguments),
    ("tool failures remain incomplete", ToolFailure),
    ("cancellation prevents tool admission", Cancellation),
    ("workspace escapes rejected", Paths),
    ("provider protocol defaults support modern OpenAI tool calling", ProtocolDefaults),
    ("native HTTP transport declares tools and sends results", NativeTransport),
    ("Responses API projects image input and tool continuation", NativeResponsesTransport),
    ("complete maintained models are admitted", NewMaintainedModel),
    ("incomplete maintained prices are not invented", UnknownMaintainedPrice)
};
var failures = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + SecretRedactor.Redact(ex.ToString())); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} shared runtime tests passed.");
return failures == 0 ? 0 : 1;

static SampleContext Context(bool approved = false) => new(Path.Combine(Path.GetTempPath(), "pisharp-runtime-tests", Guid.NewGuid().ToString("N")), new(), approved);
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static WorkflowSpec Workflow(Func<JsonObject, CancellationToken, Task<JsonNode?>> execute, bool approval = false, JsonObject? args = null) =>
    new("test", "test", "Use lookup.", [new("lookup", "Read a fixture item.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"), execute, approval)],
        [new("lookup", args ?? new() { ["id"] = "17" })]);

static async Task ExecuteAndResume()
{
    var context = Context(); var calls = 0;
    var spec = Workflow((a, _) => { calls++; return Task.FromResult<JsonNode?>(new JsonObject { ["found"] = a.Text("id") }); });
    var first = await SampleHost.ExecuteAsync(spec, context);
    Check(first.Success && calls == 1, "Native tool did not execute exactly once.");
    var transcriptPath = context.ResolvePath("transcript.json");
    var firstHistory = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsArray();
    Check(firstHistory.Any(m => m?["role"]?.GetValue<string>() == "toolResult"), "Missing actual tool result.");
    var resumed = new SampleContext(context.Workspace, new()) { ResumePath = transcriptPath };
    var second = await SampleHost.ExecuteAsync(spec, resumed);
    var secondHistory = JsonNode.Parse(await File.ReadAllTextAsync(transcriptPath))!.AsArray();
    Check(second.Success && calls == 2 && secondHistory.Count > firstHistory.Count, "History did not survive reopen.");
}
static async Task Approval()
{
    var called = false;
    var result = await SampleHost.ExecuteAsync(Workflow((_, _) => { called = true; return Task.FromResult<JsonNode?>(new JsonObject()); }, true), Context());
    Check(!called && !result.Success, "Unapproved tool ran or denial reported success.");
}
static async Task InvalidArguments()
{
    var called = false;
    var result = await SampleHost.ExecuteAsync(Workflow((_, _) => { called = true; return Task.FromResult<JsonNode?>(new JsonObject()); }, args: new() { ["id"] = 17 }), Context());
    Check(!called && !result.Success, "Invalid argument entered tool.");
    Check(SchemaValidation.Validate(SampleJson.Schema(), new JsonObject { ["unexpected"] = true }) is not null, "Extra argument accepted.");
}
static async Task ToolFailure()
{
    var result = await SampleHost.ExecuteAsync(Workflow((_, _) => throw new IOException("Authored source unavailable")), Context());
    Check(!result.Success, "Tool failure claimed success.");
}
static async Task Cancellation()
{
    using var cts = new CancellationTokenSource(); cts.Cancel(); var called = false;
    try { await SampleHost.ExecuteAsync(Workflow((_, _) => { called = true; return Task.FromResult<JsonNode?>(new JsonObject()); }), Context(), cts.Token); }
    catch (OperationCanceledException) { }
    Check(!called, "Canceled tool executed.");
}
static Task Paths()
{
    var context = Context();
    foreach (var path in new[] { "../outside.json", Path.GetFullPath(Path.Combine(context.Workspace, "..", "outside.json")) })
    {
        var rejected = false;
        try { context.ResolvePath(path); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Path escape accepted.");
    }
    return Task.CompletedTask;
}
static Task ProtocolDefaults()
{
    Check(LiveProvider.DefaultApi("openai") == "openai-responses", "OpenAI should default to Responses for tool calling.");
    Check(LiveProvider.DefaultApi("openrouter") == "openai-completions", "OpenRouter protocol changed.");
    Check(LiveProvider.DefaultApi("anthropic") == "anthropic-messages", "Anthropic protocol changed.");
    Check(LiveProvider.DefaultApi("compatible") == "openai-completions", "Compatible-provider protocol changed.");
    return Task.CompletedTask;
}
static async Task NativeTransport()
{
    const string keyName = "PISHARP_SAMPLES_TEST_KEY";
    Environment.SetEnvironmentVariable(keyName, "fixture-value-for-intercepted-http");
    try
    {
        var handler = new FakeProvider();
        var imagePath = Path.Combine(Context().Workspace, "receipt.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jPZkAAAAASUVORK5CYII="));
        var context = new SampleContext(Context().Workspace, new() { ["task"] = "Look up record 17 and report the tool result." }, approved: true, live: true)
        { Model = "fixture-model", Provider = "openai", Api = "openai-completions", KeyEnvironment = keyName, HttpHandler = handler, ImagePath = imagePath };
        var result = await SampleHost.ExecuteAsync(Workflow((a, _) => Task.FromResult<JsonNode?>(new JsonObject { ["actualToolOutput"] = a.Text("id") })), context);
        Check(result.Success && handler.Requests == 2 && handler.SawToolDeclaration && handler.SawToolResult && handler.SawImageInput && handler.SawTask && handler.SawApproval, "Native transport lost tool declarations, continuation, image input, task or approval context.");
    }
    finally { Environment.SetEnvironmentVariable(keyName, null); }
}
static JsonObject CatalogEntry()
{
    var metadata = new JsonObject { ["id"] = "new-model", ["provider"] = "openai", ["api"] = "openai-completions", ["type"] = "chat",
        ["runtimeCandidate"] = true, ["name"] = "New model", ["input"] = new JsonArray("text"), ["baseUrl"] = "https://api.openai.com/v1",
        ["contextWindow"] = 32000, ["maxTokens"] = 4096 };
    var prices = new JsonArray();
    foreach (var kind in new[] { "input", "output", "cache-read", "cache-write" }) prices.Add(new JsonObject
        { ["kind"] = kind, ["currency"] = "USD", ["unit"] = "million-tokens", ["amount"] = 1m, ["tier"] = "unspecified", ["region"] = "unspecified", ["conditions"] = new JsonObject() });
    return new JsonObject { ["schemaVersion"] = 1, ["providers"] = new JsonObject { ["openai"] = new JsonObject
        { ["new-model"] = new JsonObject { ["metadata"] = metadata, ["prices"] = prices } } } };
}
static async Task NativeResponsesTransport()
{
    const string keyName = "PISHARP_SAMPLES_RESPONSES_TEST_KEY";
    Environment.SetEnvironmentVariable(keyName, "fixture-responses-intercepted-http");
    try
    {
        var handler = new FakeResponsesProvider();
        var imagePath = Path.Combine(Context().Workspace, "receipt.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jPZkAAAAASUVORK5CYII="));
        var context = new SampleContext(Context().Workspace, new(), live: true)
        { Model = "fixture-model", Provider = "openai", Api = "openai-responses", KeyEnvironment = keyName, HttpHandler = handler, ImagePath = imagePath };
        var result = await SampleHost.ExecuteAsync(Workflow((a, _) => Task.FromResult<JsonNode?>(new JsonObject { ["actualToolOutput"] = a.Text("id") })), context);
        Check(result.Success && handler.Requests == 2 && handler.SawImage && handler.SawToolResult, "Responses image/tool continuation was dropped.");
    }
    finally { Environment.SetEnvironmentVariable(keyName, null); }
}
static Task NewMaintainedModel()
{
    var result = MaintainedCatalogOverlay.Apply("openai", [], JsonData.Parse(CatalogEntry().ToJsonString()), ["openai-completions"]);
    Check(result.Models.Length == 1 && result.Warnings.IsEmpty, "Complete supported model was not added.");
    return Task.CompletedTask;
}
static Task UnknownMaintainedPrice()
{
    var overlay = CatalogEntry(); overlay["providers"]!["openai"]!["new-model"]!["prices"]![0]!["amount"] = null;
    var result = MaintainedCatalogOverlay.Apply("openai", [], JsonData.Parse(overlay.ToJsonString()), ["openai-completions"]);
    Check(result.Models.IsEmpty && !result.Warnings.IsEmpty, "Unknown price was converted into a runtime price.");
    return Task.CompletedTask;
}
sealed class FakeProvider : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool SawToolDeclaration { get; private set; }
    public bool SawToolResult { get; private set; }
    public bool SawImageInput { get; private set; }
    public bool SawTask { get; private set; }
    public bool SawApproval { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        SawToolDeclaration |= body["tools"]?.AsArray().Any(t => t?["function"]?["name"]?.GetValue<string>() == "lookup") == true;
        SawToolResult |= body["messages"]!.AsArray().Any(m => m?["role"]?.GetValue<string>() == "tool" && m?["content"]?.ToJsonString().Contains("actualToolOutput", StringComparison.Ordinal) == true);
        SawImageInput |= body["messages"]!.ToJsonString().Contains("data:image/png;base64,", StringComparison.Ordinal);
        SawTask |= body["messages"]!.ToJsonString().Contains("Look up record 17", StringComparison.Ordinal);
        SawApproval |= body["messages"]!.ToJsonString().Contains("host has granted approval", StringComparison.Ordinal);
        var chunk = ++Requests == 1
            ? "{\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-fixture\",\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"arguments\":\"{\\\"id\\\":\\\"17\\\"}\"}}]},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}"
            : "{\"id\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Done\"},\"finish_reason\":null}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}";
        return new(HttpStatusCode.OK) { Content = new StringContent("data: " + chunk + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    }
}
sealed class FakeResponsesProvider : HttpMessageHandler
{
    public int Requests { get; private set; }
    public bool SawImage { get; private set; }
    public bool SawToolResult { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject();
        var input = body["input"]!.ToJsonString();
        SawImage |= input.Contains("input_image", StringComparison.Ordinal) && input.Contains("data:image/png;base64,", StringComparison.Ordinal);
        SawToolResult |= input.Contains("function_call_output", StringComparison.Ordinal) && input.Contains("actualToolOutput", StringComparison.Ordinal);
        var frames = ++Requests == 1 ? new[]
        {
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-fixture","call_id":"call-fixture","name":"lookup","arguments":""}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc-fixture","call_id":"call-fixture","name":"lookup","arguments":"{\"id\":\"17\"}"}}""",
            """{"type":"response.completed","response":{"id":"resp-fixture","status":"completed","output":[]}}"""
        } : new[]
        {
            """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg-fixture","role":"assistant","content":[]}}""",
            """{"type":"response.content_part.added","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""",
            """{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"Done"}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg-fixture","role":"assistant","content":[{"type":"output_text","text":"Done","annotations":[]}]}}""",
            """{"type":"response.completed","response":{"id":"resp-final","status":"completed","output":[]}}"""
        };
        return new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n\n", frames.Select(f => "data: " + f)) + "\n\n", Encoding.UTF8, "text/event-stream") };
    }
}
