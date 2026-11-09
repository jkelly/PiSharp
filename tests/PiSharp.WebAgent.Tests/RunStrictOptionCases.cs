// PiSharp-only: run patches follow the pinned v1.5.0 Responses converter's root and inline tool declarations.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RunStrictOptionCases() =>
    [
        Case("runs.options.strict-root-functions-keep-each-apis-shape", () =>
        {
            foreach (var api in new[] { "openai-completions", "openai-responses", "azure-openai-responses", "openai-codex-responses" })
            {
                var body = JsonNode.Parse(api == "openai-completions"
                    ? """{"tools":[{"type":"function","function":{"name":"t","parameters":{"type":"object"},"strict":false}},{"type":"function","function":{"name":"other","strict":false}}]}"""
                    : """{"tools":[{"type":"function","name":"t","parameters":{"type":"object"},"strict":false},{"type":"function","name":"other","strict":false}]}""")!.AsObject();
                Equal(0, RunModelOptions.ApplyToBody(body, null, ["t"], api).Count, "root declaration found on " + api);
                var definition = api == "openai-completions" ? body["tools"]![0]!["function"]! : body["tools"]![0]!;
                var other = api == "openai-completions" ? body["tools"]![1]!["function"]! : body["tools"]![1]!;
                Equal(true, definition["strict"]?.GetValue<bool>(), "root function marked on " + api);
                Equal("object", definition["parameters"]?["type"]?.GetValue<string>(), "schema retained on " + api);
                Equal(false, other["strict"]?.GetValue<bool>(), "unrequested function unchanged on " + api);
            }
        }),

        Case("runs.options.strict-inline-initial-and-incremental-declarations-follow-the-responses-family", () =>
        {
            const string json = """
                {"tools":[{"type":"function","name":"root"}],"input":[
                  {"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t","description":"initial"}]},
                  {"role":"user","content":"continue"},
                  {"role":"developer","content":"This is an incremental tools update."},
                  {"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t","description":"replacement"},{"type":"function","name":"later"},{"type":"function","name":"other","strict":false}]}]}
                """;
            foreach (var api in new[] { "openai-responses", "azure-openai-responses", "openai-codex-responses" })
            {
                var body = JsonNode.Parse(json)!.AsObject();
                Equal(0, RunModelOptions.ApplyToBody(body, null, ["root", "t", "later"], api).Count, "all declarations found on " + api);
                Equal(true, body["tools"]![0]!["strict"]?.GetValue<bool>(), "distinct root function marked");
                Equal(true, body["input"]![0]!["tools"]![0]!["strict"]?.GetValue<bool>(), "initial inline function marked");
                Equal(true, body["input"]![3]!["tools"]![0]!["strict"]?.GetValue<bool>(), "replacement also marked");
                Equal(true, body["input"]![3]!["tools"]![1]!["strict"]?.GetValue<bool>(), "incremental addition marked");
                Equal(false, body["input"]![3]!["tools"]![2]!["strict"]?.GetValue<bool>(), "unrequested inline function unchanged");
                Equal("replacement", body["input"]![3]!["tools"]![0]!["description"]?.GetValue<string>(), "replacement schema/content retained");
            }
            foreach (var api in new[] { "openai-completions", "anthropic-messages", "google-generative-ai", "unrelated-api" })
                Equal("t", string.Join(",", RunModelOptions.ApplyToBody(JsonNode.Parse(json)!.AsObject(), null, ["t"], api)),
                    "inline Responses declarations do not authorize strict tools on " + api);
        }),

        Case("runs.capture.strict-missing-ambiguous-and-malformed-declarations-never-reach-http", async () =>
        {
            string[] invalid =
            [
                """{}""",
                """{"tools":{}}""",
                """{"tools":[{"type":"function","name":"T"}]}""",
                """{"tools":[{"type":"function","name":"t"},{"type":"function","name":"t"}]}""",
                """{"tools":[{"type":"custom","name":"t"}]}""",
                """{"tools":[{"type":"function","function":{"name":"t"}}]}""",
                """{"input":[{"type":"additional_tools","role":"developer","tools":null}]}""",
                """{"input":[{"type":"additional_tools","role":"user","tools":[{"type":"function","name":"t"}]}]}""",
                """{"input":[{"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t"},{"type":"function","name":"t"}]}]}""",
                """{"input":[{"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t"}]},{"type":"additional_tools","role":"developer","tools":[{"type":"custom","name":"t"}]}]}""",
                """{"tools":[{"type":"function","name":"t"}],"input":[{"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t"}]}]}""",
                """{"input":[{"role":"user","content":[{"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t"}]}]}]}""",
            ];
            using var provider = new ProviderFake();
            using var handler = new RunCaptureHandler(provider, ownsInner: false);
            using var client = new HttpMessageInvoker(handler, disposeHandler: false);
            var prior = RunHttpScope.Current;
            try
            {
                foreach (var json in invalid)
                {
                    Equal("t", string.Join(",", RunModelOptions.ApplyToBody(JsonNode.Parse(json)!.AsObject(), null, ["t"], "openai-codex-responses")),
                        "required tool refused: " + json);
                    var context = new RunHttpContext { RunId = "strict-refusal", Api = "openai-codex-responses", StrictTools = ["t"], MaxRetries = 2 };
                    RunHttpScope.Current = context;
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/codex/responses")
                    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                    var error = await ThrowsAsync<RunPatchException>(() => client.SendAsync(request, CancellationToken.None), "strict refusal before HTTP");
                    Check(error.Message.Contains("t", StringComparison.Ordinal) && error.Message.Contains("not sent", StringComparison.Ordinal), "refusal names the tool");
                    Equal((0, 0, 0), (provider.Requests.Count, context.Patched, context.Retries), "no provider request, successful patch, or retry");
                }
                // A literal invalid UTF-8 byte inside an otherwise valid string must not become U+FFFD before JSON parsing.
                var malformedUtf8 = Encoding.UTF8.GetBytes("""{"tools":[{"type":"function","name":"t"}],"value":"?"}""");
                malformedUtf8[^3] = 0xff;
                foreach (var compressed in new[] { false, true })
                {
                    var context = new RunHttpContext { RunId = "strict-invalid-utf8", Api = "openai-codex-responses", StrictTools = ["t"], MaxRetries = 2 };
                    RunHttpScope.Current = context;
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/codex/responses")
                    { Content = new ByteArrayContent(compressed ? PiSharp.AI.Compression.Zstd.Compress(malformedUtf8) : malformedUtf8) };
                    request.Content.Headers.ContentType = new("application/json");
                    if (compressed) request.Content.Headers.ContentEncoding.Add("zstd");
                    var error = await ThrowsAsync<RunPatchException>(() => client.SendAsync(request, CancellationToken.None), "invalid UTF-8 refused before HTTP");
                    Check(error.Message.Contains("not sent", StringComparison.Ordinal), "invalid UTF-8 reports refusal");
                    Equal((0, 0, 0), (provider.Requests.Count, context.Patched, context.Retries), "invalid UTF-8 causes no HTTP, successful patch, or retry");
                }
            }
            finally { RunHttpScope.Current = prior; }
        }),

        Case("runs.capture.strict-inline-patching-preserves-options-headers-compression-and-utf16", async () =>
        {
            const string json = """
                {"model":"codex","stream":true,"text":{"format":{"type":"text"}},"prompt_cache_key":"session-1","input":[
                  {"type":"additional_tools","role":"developer","tools":[{"type":"function","name":"t","parameters":{"type":"object","properties":{"\ud800":{"type":"string","default":"\udfff"}}}}]},
                  {"role":"user","content":"hello"}]}
                """;
            using var provider = new ProviderFake();
            using var handler = new RunCaptureHandler(provider, ownsInner: false);
            using var client = new HttpMessageInvoker(handler, disposeHandler: false);
            var prior = RunHttpScope.Current;
            try
            {
                foreach (var compressed in new[] { false, true })
                {
                    var context = new RunHttpContext
                    {
                        RunId = "strict-preserve", Api = "openai-codex-responses", StrictTools = ["t"],
                        Patch = JsonNode.Parse("""{"service_tier":"priority","text":{"verbosity":"high"},"parallel_tool_calls":false}""")!.AsObject(),
                        Cache = new RunCacheEdit("long", "responses"), Headers = new Dictionary<string, string> { ["x-run-test"] = "kept" },
                    };
                    RunHttpScope.Current = context;
                    var bytes = Encoding.UTF8.GetBytes(json);
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/codex/responses")
                    { Content = new ByteArrayContent(compressed ? PiSharp.AI.Compression.Zstd.Compress(bytes) : bytes) };
                    request.Content.Headers.ContentType = new("application/json");
                    request.Content.Headers.ContentLanguage.Add("en");
                    if (compressed) request.Content.Headers.ContentEncoding.Add("zstd");
                    request.Headers.TryAddWithoutValidation("session-id", "session-1");
                    using var response = await client.SendAsync(request, CancellationToken.None);
                    var sent = provider.Requests.Last();
                    using var body = JsonDocument.Parse(sent.Body!);
                    var root = body.RootElement;
                    Equal("priority", root.GetProperty("service_tier").GetString(), "tier patched");
                    Equal(("high", "text"), (root.GetProperty("text").GetProperty("verbosity").GetString(), root.GetProperty("text").GetProperty("format").GetProperty("type").GetString()), "text options merged");
                    Equal(false, root.GetProperty("parallel_tool_calls").GetBoolean(), "parallel option patched");
                    Equal(("session-1", "24h"), (root.GetProperty("prompt_cache_key").GetString(), root.GetProperty("prompt_cache_retention").GetString()), "session and cache retained");
                    var tool = root.GetProperty("input")[0].GetProperty("tools")[0];
                    Equal(true, tool.GetProperty("strict").GetBoolean(), "inline strict flag patched");
                    var property = tool.GetProperty("parameters").GetProperty("properties").EnumerateObject().Single();
                    Equal("\ud800", JsonUtf16.GetName(property), "schema property name retains its UTF-16 code unit");
                    Equal("\udfff", JsonUtf16.GetString(property.Value.GetProperty("default")), "schema value retains its UTF-16 code unit");
                    Equal(("session-1", "kept", "en"), (sent.Header("session-id"), sent.Header("x-run-test"), sent.Header("content-language")), "request and content headers retained");
                    Equal(compressed ? "zstd" : null, sent.Header("content-encoding"), "original compression retained");
                    Equal(1, context.Patched, "patch counted for the current run");
                }
            }
            finally { RunHttpScope.Current = prior; }
        }),
    ];
}
