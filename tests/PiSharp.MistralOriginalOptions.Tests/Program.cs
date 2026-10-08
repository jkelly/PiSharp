using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.AI;
using PiSharp.Contracts;

var groups = new (string Name, Func<Task> Run)[] {
    ("direct-source-option-remapping", () => Controls.Mapping(false)),
    ("simple-source-option-remapping", () => Controls.Mapping(true)),
    ("camel-wins-null-and-wire-only-fields", Controls.Collisions),
    ("nested-schema-object-only-remapping", Controls.Shapes),
    ("extension-byte-depth-and-core-admission", Controls.Admission),
    ("held-hook-original-cancel-join", Controls.Held)
};
var originals = new List<OriginalTaskRecord>();
try
{
    foreach (var group in groups)
    {
        var record = new OriginalTaskRecord(group.Name); originals.Add(record);
        try { record.Original = group.Run(); await record.Original; } catch (Exception error) { record.Direct = error; }
        finally { record.Capture(); }
    }
    var report = new { sourceDerived = true, groups = groups.Length,
        records = originals.Select(QualificationReporter.Project).ToArray(),
        held = Controls.HeldOriginals.Select(QualificationReporter.Project).ToArray() };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return originals.Any(record => record.Direct is not null || record.Original?.IsCompletedSuccessfully != true) ? 1 : 0;
}
catch (Exception reportFailure) { throw new QualificationReportingFailure(reportFailure, originals.ToArray(), Controls.HeldOriginals.ToArray()); }

static class Controls
{
    internal static readonly List<OriginalTaskRecord> HeldOriginals = [];
    private static readonly ModelDescriptor Model = new("fixture", "mistral-conversations", "mistral");
    private static readonly Uri Endpoint = new("https://api.mistral.ai/");
    private static readonly JsonData Metadata = JsonData.Parse("{\"id\":\"fixture\",\"provider\":\"mistral\",\"api\":\"mistral-conversations\",\"contextWindow\":8192,\"maxTokens\":100,\"reasoning\":false,\"input\":[\"text\"]}");
    private static ChatRequest Request => new(Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1}"))]);
    private static MistralTextOptions Options => new(Endpoint, true, new(0, 0, 0, 0), "fixture") { MaxTokens = 17 };
    internal static void Check(bool condition, [CallerLineNumber] int line = 0) { if (!condition) throw new InvalidOperationException($"Original options fixture assertion at line {line}."); }
    private static async Task<List<StreamEvent>> Drain(IChatTransport transport, CancellationToken token = default) { var events = new List<StreamEvent>(); await foreach (var frame in transport.StreamAsync(Request, token)) events.Add(frame); return events; }
    private static NativeHttpModelProvider Provider(Handler handler, MistralTextOptions options, bool simple = false) => simple
        ? NativeProviderFactory.CreateMistralSimple(Model, Endpoint, "SYNTHETIC", Metadata, options, handler)
        : NativeProviderFactory.CreateMistral(Model, Endpoint, "SYNTHETIC", options, handler);
    private static JsonData Replace(JsonData payload, Action<JsonObject> edit) { var node = JsonNode.Parse(payload.ToString())!.AsObject(); edit(node); return JsonData.Parse(node.ToJsonString()); }

    internal static async Task Mapping(bool simple)
    {
        JsonData? replacement = null; string? retained = null; var calls = 0;
        using var handler = new Handler();
        using var provider = Provider(handler, Options with { MaxTokens = simple ? null : 17d, OnPayload = (payload, _, _) => {
            calls++; Check(payload.Value.GetProperty("maxTokens").GetDouble() == (simple ? 100 : 17));
            replacement = Replace(payload, root => {
                root["topP"] = 0.75; root["randomSeed"] = 0; root["parallelToolCalls"] = false;
                root["presencePenalty"] = -0.25; root["frequencyPenalty"] = 0.5; root["safePrompt"] = false;
                root["responseFormat"] = JsonNode.Parse("{\"type\":\"json_schema\",\"jsonSchema\":{\"name\":\"answer\",\"schemaDefinition\":{\"type\":\"object\",\"properties\":{\"safePrompt\":{\"type\":\"boolean\"}}}}}");
                root["fixture_extension"] = JsonNode.Parse("{\"nullable\":null,\"list\":[1,true,\"literal\"]}");
            }); retained = replacement.ToString(); return ValueTask.FromResult<JsonData?>(replacement);
        } }, simple);
        var events = await Drain(provider); Check(calls == 1 && handler.Calls == 1 && events[^1] is StreamDone { Reason: StopReason.Stop });
        var wire = handler.Wire!.Value;
        Check(wire.GetProperty("max_tokens").GetDouble() == (simple ? 100 : 17) && !wire.TryGetProperty("maxTokens", out _));
        Check(wire.GetProperty("top_p").GetDouble() == .75 && wire.GetProperty("random_seed").GetInt32() == 0);
        Check(!wire.GetProperty("parallel_tool_calls").GetBoolean() && !wire.GetProperty("safe_prompt").GetBoolean());
        Check(wire.GetProperty("presence_penalty").GetDouble() == -.25 && wire.GetProperty("frequency_penalty").GetDouble() == .5);
        var schema = wire.GetProperty("response_format").GetProperty("json_schema");
        Check(schema.GetProperty("schema").GetProperty("properties").TryGetProperty("safePrompt", out _));
        Check(!schema.TryGetProperty("schemaDefinition", out _) && !wire.TryGetProperty("topP", out _) && !wire.TryGetProperty("responseFormat", out _));
        Check(wire.GetProperty("fixture_extension").GetProperty("list").GetArrayLength() == 3 && replacement!.ToString() == retained);
    }

    internal static async Task Collisions()
    {
        using var handler = new Handler();
        using var provider = Provider(handler, Options with { OnPayload = (payload, _, _) => ValueTask.FromResult<JsonData?>(Replace(payload, root => {
            root["top_p"] = .9; root["topP"] = null; root["random_seed"] = 7;
            root["response_format"] = JsonNode.Parse("{\"type\":\"json_object\"}");
            root["responseFormat"] = JsonNode.Parse("{\"json_schema\":{\"schema\":{\"type\":\"string\"}},\"jsonSchema\":{\"schema\":{\"type\":\"number\"},\"schemaDefinition\":null}}");
        })) });
        var events = await Drain(provider); Check(handler.Calls == 1 && events[^1] is StreamDone);
        var wire = handler.Wire!.Value; Check(wire.GetProperty("top_p").ValueKind == JsonValueKind.Null && wire.GetProperty("random_seed").GetInt32() == 7);
        Check(wire.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").ValueKind == JsonValueKind.Null);
        Check(!wire.TryGetProperty("topP", out _) && !wire.TryGetProperty("responseFormat", out _));
    }

    internal static async Task Shapes()
    {
        foreach (var format in new[] { "null", "[]", "\"literal\"", "{\"jsonSchema\":null}", "{\"jsonSchema\":[]}", "{\"json_schema\":{\"schemaDefinition\":{\"type\":\"number\"}}}" })
        {
            using var handler = new Handler();
            using var provider = Provider(handler, Options with { OnPayload = (payload, _, _) => ValueTask.FromResult<JsonData?>(Replace(payload, root => root["responseFormat"] = JsonNode.Parse(format))) });
            var events = await Drain(provider); Check(handler.Calls == 1 && events[^1] is StreamDone);
            var result = handler.Wire!.Value.GetProperty("response_format");
            if (format.Contains("schemaDefinition", StringComparison.Ordinal)) Check(result.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString() == "number");
            else if (format.Contains("jsonSchema", StringComparison.Ordinal)) Check(result.TryGetProperty("json_schema", out _) && !result.TryGetProperty("jsonSchema", out _));
            else Check(result.GetRawText() == format);
        }
    }

    internal static async Task Admission()
    {
        foreach (var variant in new[] { "bytes", "depth", "model", "stream" })
        {
            using var handler = new Handler(); var options = Options with { MaximumPayloadBytes = 2048, MaximumJsonDepth = 4,
                OnPayload = (payload, _, _) => ValueTask.FromResult<JsonData?>(Replace(payload, root => {
                    if (variant == "bytes") root["fixture_extension"] = new string('x', 4096);
                    if (variant == "depth") root["fixture_extension"] = JsonNode.Parse("{\"a\":{\"b\":{\"c\":{\"d\":1}}}}");
                    if (variant == "model") root["model"] = "different";
                    if (variant == "stream") root["stream"] = false;
                })) };
            using var provider = Provider(handler, options); var events = await Drain(provider);
            Check(handler.Calls == 0 && events[^1] is StreamError { Reason: StopReason.Error });
            var terminal = (StreamError)events[^1]; Check(terminal.NativeDiagnostic?.Code == (variant is "bytes" or "depth" ? NativeChatFailureCode.ResourceLimit : NativeChatFailureCode.UnsupportedFeature));
        }
    }

    internal static async Task Held()
    {
        using var handler = new Handler(); using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = Provider(handler, Options with { OnPayload = (payload, _, _) => { entered.TrySetResult(); return new(release.Task); } });
        var drain = Drain(provider, stop.Token); Exception? controlFailure = null;
        var releaseRecord = new OriginalTaskRecord("held-payload-original", release.Task);
        var drainRecord = new OriginalTaskRecord("held-drain-original", drain);
        HeldOriginals.Add(releaseRecord); HeldOriginals.Add(drainRecord);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel(); Check(!release.Task.IsCompleted && !drain.IsCompleted && handler.Calls == 0); }
        catch (Exception error) { controlFailure = error; }
        finally { release.TrySetResult(null); }
        var faults = new List<Exception>(); if (controlFailure is not null) faults.Add(controlFailure);
        try { await release.Task; } catch (Exception error) { releaseRecord.Direct = error; } finally { releaseRecord.Capture(); }
        List<StreamEvent>? events = null; try { events = await drain; } catch (Exception error) { drainRecord.Direct = error; } finally { drainRecord.Capture(); }
        faults.AddRange(releaseRecord.Faults()); faults.AddRange(drainRecord.Faults());
        if (faults.Count != 0) throw new AggregateException("Held originals joined with faults.", faults);
        Check(events is not null && events[^1] is StreamError { Reason: StopReason.Aborted } && handler.Calls == 0);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls; internal JsonElement? Wire;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Wire = document.RootElement.Clone();
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
