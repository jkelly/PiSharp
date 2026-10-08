using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

if (args.Length != 4 || args[0] != "--fixture" || args[2] != "--report") throw new ArgumentException("Supply explicit --fixture and --report paths.");
var fixture = Path.GetFullPath(args[1]); var report = Path.GetFullPath(args[3]);
var bytes = await File.ReadAllBytesAsync(fixture);
if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != "9449ba27778bc21eca13c17fa26d53691f05bc3d00e983b17d69b7127056e0ac") throw new InvalidOperationException("Genuine complete source fixture changed.");
using var capture = JsonDocument.Parse(bytes);
var root = capture.RootElement;
if (root.GetProperty("sourceSha").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" || root.GetProperty("prohibitedNetworkCalls").GetInt32() != 0 || root.GetProperty("sourceOrSdkEdits").GetInt32() != 0 || root.GetProperty("observations").GetArrayLength() != 17) throw new InvalidOperationException("Source provenance/matrix changed.");
var outcomes = new List<object>(); var passed = 0; var failed = 0;
foreach (var observation in root.GetProperty("observations").EnumerateArray())
{
    var id = observation.GetProperty("id").GetString()!;
    try
    {
        var sourceModel = observation.GetProperty("model"); var profile = observation.GetProperty("profile");
        var sourceOptions = observation.GetProperty("options");
        var model = new ModelDescriptor(sourceModel.GetProperty("id").GetString()!, sourceModel.GetProperty("api").GetString()!, sourceModel.GetProperty("provider").GetString()!);
        var entries = observation.GetProperty("context").GetProperty("messages").EnumerateArray().Select(message =>
            new TranscriptEntry(message.GetProperty("role").GetString()!, JsonData.FromElement(message))).ToImmutableArray();
        var originals = entries.Select(entry => entry.WireBody.ToString()).ToArray();
        var options = new CompletionsKeyAuthRequestOptions(MaxTokens: sourceOptions.GetProperty("maxTokens").GetDouble(),
            Temperature: sourceOptions.GetProperty("temperature").GetDouble(),
            ReasoningEffort: sourceOptions.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null,
            SupportsReasoningEffort: !profile.TryGetProperty("supportsReasoningEffort", out var supported) || supported.GetBoolean());
        // This observation adapter is deliberately compiled once against the accepted base. Missing
        // configuration is recorded while the real baseline factory still sends its actual request.
        // No expected body/final or production output is manufactured by reflection.
        var missing = new List<string>();
        if (profile.TryGetProperty("format", out var format))
        {
            var property = typeof(CompletionsKeyAuthRequestOptions).GetProperty("ReasoningFormat");
            if (property is null) missing.Add("ReasoningFormat");
            else property.SetValue(options, Enum.Parse(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
                format.GetString() == "openrouter" ? "OpenRouter" : "OpenAI"));
        }
        if (profile.TryGetProperty("map", out var map))
        {
            var property = typeof(CompletionsKeyAuthRequestOptions).GetProperty("ThinkingLevelMap");
            if (property is null) missing.Add("ThinkingLevelMap"); else property.SetValue(options, JsonData.FromElement(map));
        }
        var expected = observation.GetProperty("requests")[0];
        using var handler = new FixtureHandler(observation.GetProperty("wire").GetString()!);
        using var client = new HttpClient(handler); var payloadCalls = 0; var responseCalls = 0;
        var hooks = new CompletionsLifecycleHooks
        {
            OnPayload = (_, _, _) => { payloadCalls++; return ValueTask.FromResult<JsonData?>(null); },
            OnResponse = (_, _, _) => { responseCalls++; return ValueTask.CompletedTask; }
        };
        var factory = new CompletionsKeyAuthRequestFactory(new(expected.GetProperty("url").GetString()!), model,
            new(Reasoning: sourceModel.GetProperty("reasoning").GetBoolean()), options);
        var transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(client, (request, token) =>
            factory.CreateAsync(request, sourceOptions.GetProperty("apiKey").GetString()!, hooks, token), new() { Hooks = hooks });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ChatClient(transport).CompleteAsync(new(model, entries, 123), deadline.Token);
        var final = PiWireJson.WriteMessage(result.Message).Value;
        using var actualBody = JsonDocument.Parse(handler.Body!);
        var bodyMatches = JsonElement.DeepEquals(expected.GetProperty("bodyJson"), actualBody.RootElement);
        var finalMatches = JsonElement.DeepEquals(observation.GetProperty("result"), final);
        var historyUnchanged = entries.Select(entry => entry.WireBody.ToString()).SequenceEqual(originals);
        var ownership = handler.Attempts == 1 && payloadCalls == 1 && responseCalls == 1 && handler.ResponseContent!.Disposals == 1;
        try { _ = await handler.Request!.Content!.ReadAsByteArrayAsync(); ownership = false; } catch (ObjectDisposedException) { }
        client.DefaultRequestHeaders.Add("X-Borrowed-Client-Still-Usable", "authored");
        var ok = bodyMatches && finalMatches && historyUnchanged && ownership && missing.Count == 0;
        if (ok) passed++; else failed++;
        outcomes.Add(new { id, passed = ok, bodyMatches, finalMatches, historyUnchanged, ownershipJoined = ownership,
            missingConfiguration = missing, attemptedSends = handler.Attempts, payloadCalls, responseCalls,
            expectedRequest = expected, nativeRequest = new { uri = handler.RequestUri, method = handler.Method, headers = handler.Headers,
                body = handler.Body, bodySha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(handler.Body!))), bodyJson = actualBody.RootElement.Clone() },
            expectedFinal = observation.GetProperty("result").Clone(), nativeFinal = final.Clone() });
        Console.WriteLine((ok ? "PASS " : "FAIL ") + id + $" body={bodyMatches} final={finalMatches} cleanup={ownership} attempted-sends={handler.Attempts} missing={string.Join(',', missing)}");
    }
    catch (Exception error) { failed++; outcomes.Add(new { id, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + id + ": " + error); }
}
var api = typeof(CompletionsKeyAuthRequestOptions);
var modeApi = api.GetProperty("ReasoningFormat") is not null && api.GetProperty("ThinkingLevelMap") is not null;
if (modeApi) passed++; else failed++;
outcomes.Add(new { id = "native-mode-configuration-api", passed = modeApi });
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { schemaVersion = 1, sourceSha = root.GetProperty("sourceSha").GetString(),
    fixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), passed, failed, observations = outcomes,
    productionAiDll = Identity(typeof(ChatClient).Assembly), executedTestDll = Identity(Assembly.GetExecutingAssembly()),
    scope = "bounded-original-P2-08-openrouter-reasoning-provider-mode", sourceRequestObservations = "complete-genuine-SDK-fetch-init",
    nativeRequestObservations = "actual-send-entry-count-and-complete-injected-handler-body", liveProvider = false,
    fullGate = false, packageAcceptance = false, phaseAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"OpenRouter reasoning: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

static object Identity(Assembly assembly) { var file = assembly.Location; var data = File.ReadAllBytes(file); return new { path = file, bytes = data.Length,
    sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }; }
sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
{ internal int Disposals; protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); } }
sealed class FixtureHandler(string wire) : HttpMessageHandler
{
    internal int Attempts; internal HttpRequestMessage? Request; internal string? Body, RequestUri, Method; internal object[] Headers = [];
    internal TrackedContent? ResponseContent;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Attempts++; Request = request; RequestUri = request.RequestUri!.AbsoluteUri; Method = request.Method.Method;
        Body = await request.Content!.ReadAsStringAsync(token);
        Headers = request.Headers.Concat(request.Content.Headers).Select(header => (object)new { name = header.Key, values = header.Value.ToArray() }).ToArray();
        ResponseContent = new(Encoding.UTF8.GetBytes(wire)); ResponseContent.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = ResponseContent };
    }
}
