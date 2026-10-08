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

if (args.Length != 4 || args[0] != "--fixture" || args[2] != "--report") throw new ArgumentException("Supply --fixture and --report.");
var bytes = await File.ReadAllBytesAsync(args[1]);
if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != "a61f14329141bcb29295f7af933f80d3afeb6243ce071c8be7e446f44aa24800") throw new InvalidOperationException("Complete genuine Source fixture changed.");
using var capture = JsonDocument.Parse(bytes); var root = capture.RootElement;
if (root.GetProperty("sourceSha").GetString() != "d86654abb8862e201933517d6f1fce9f88dd117f" || root.GetProperty("prohibitedNetworkCalls").GetInt32() != 0 || root.GetProperty("sourceOrSdkEdits").GetInt32() != 0 || root.GetProperty("observations").GetArrayLength() != 22) throw new InvalidOperationException("Capture provenance changed.");
var outcomes = new List<object>(); var passed = 0; var failed = 0; var allTurns = 0; var allEmissions = 0;
foreach (var observation in root.GetProperty("observations").EnumerateArray())
{
    var id = observation.GetProperty("id").GetString()!;
    try
    {
        var sourceModel = observation.GetProperty("model"); var profile = observation.GetProperty("profile"); var sourceOptions = observation.GetProperty("options");
        var model = new ModelDescriptor(sourceModel.GetProperty("id").GetString()!, sourceModel.GetProperty("api").GetString()!, sourceModel.GetProperty("provider").GetString()!);
        var history = observation.GetProperty("initialHistory").EnumerateArray().Select(Entry).ToImmutableArray();
        var originals = history.Select(entry => entry.WireBody.ToString()).ToArray();
        var options = new CompletionsKeyAuthRequestOptions(MaxTokens: sourceOptions.GetProperty("maxTokens").GetDouble(), Temperature: sourceOptions.GetProperty("temperature").GetDouble(),
            CacheRetention: Enum.Parse<CompletionsCacheRetention>(sourceOptions.GetProperty("cacheRetention").GetString()!, true),
            SupportsLongCacheRetention: !profile.TryGetProperty("supportsLong", out var supportsLong) || supportsLong.GetBoolean(),
            SessionId: sourceOptions.TryGetProperty("sessionId", out var session) ? session.GetString() : null,
            SendSessionAffinityHeaders: !profile.TryGetProperty("affinity", out var affinity) || affinity.GetBoolean(),
            SessionAffinityFormat: profile.TryGetProperty("format", out var format) ? format.GetString()! : "openrouter",
            ModelHeaders: sourceModel.TryGetProperty("headers", out var modelHeaders) ? JsonData.FromElement(modelHeaders) : null,
            Headers: sourceOptions.TryGetProperty("headers", out var callerHeaders) ? JsonData.FromElement(callerHeaders) : null);
        var projection = new CompletionsTranscriptProjectionOptions(Reasoning: true, SupportsMidConversationSystemMessages: profile.TryGetProperty("midSystem", out var midSystem) && midSystem.GetBoolean()) { ModelSupportsImages = true };
        var turnOutcomes = new List<object>(); var profileMatches = true;
        foreach (var turn in observation.GetProperty("turns").EnumerateArray())
        {
            allTurns++; var expected = turn.GetProperty("requests")[0]; var publicationValues = new List<JsonElement>(); var publicationJson = new List<string>();
            using var handler = new FixtureHandler(turn.GetProperty("wire").GetString()!); using var client = new HttpClient(handler);
            var payloadCalls = 0; var responseCalls = 0;
            var hooks = new CompletionsLifecycleHooks
            {
                OnPayload = (_, _, _) => { payloadCalls++; return ValueTask.FromResult<JsonData?>(null); },
                OnResponse = (_, _, _) => { responseCalls++; return ValueTask.CompletedTask; },
                OnSourcePublished = item => { publicationValues.Add(item.Emission.Raw.Value.Clone()); publicationJson.Add(item.Emission.SerializedJson); }
            };
            var factory = new CompletionsKeyAuthRequestFactory(new(expected.GetProperty("url").GetString()!), model, projection, options);
            var transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(client, (request, token) => factory.CreateAsync(request, sourceOptions.GetProperty("apiKey").GetString()!, hooks, token), new() { Hooks = hooks });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = new ChatRequest(model, history, 123);
            var expectedHistory = turn.GetProperty("context").GetProperty("messages");
            var historyMatches = JsonElement.DeepEquals(expectedHistory, JsonSerializer.SerializeToElement(history.Select(entry => entry.WireBody.Value)));
            await using var run = await transport.StartAsync(request, deadline.Token);
            var delivered = new List<JsonElement>();
            await foreach (var frame in run.ReadSourceEventsAsync(deadline.Token)) delivered.Add(frame.Snapshot.Raw.Value.Clone());
            var semantic = await run.SourceResult.WaitAsync(deadline.Token); var canonical = await run.CanonicalCompletion.WaitAsync(deadline.Token);
            var cleanup = await run.CleanupCompletion.WaitAsync(deadline.Token);
            var final = PiWireJson.WriteMessage(canonical.Message).Value;
            var expectedEmissions = turn.GetProperty("emissions").EnumerateArray().ToArray(); allEmissions += publicationValues.Count;
            var eventMatches = publicationValues.Count == expectedEmissions.Length && publicationValues.Select((item, index) =>
                JsonElement.DeepEquals(item.GetProperty("value"), expectedEmissions[index].GetProperty("value")) &&
                JsonElement.DeepEquals(item.GetProperty("ownUndefinedPaths"), expectedEmissions[index].GetProperty("ownUndefinedPaths"))).All(value => value);
            var eventJsonBytesMatch = publicationJson.SequenceEqual(expectedEmissions.Select(item => item.GetProperty("serializedJson").GetString()));
            var eventJsonMatches = publicationJson.Count == expectedEmissions.Length && publicationJson.Select((text, index) =>
                JsonElement.DeepEquals(JsonData.Parse(text).Value, JsonData.Parse(expectedEmissions[index].GetProperty("serializedJson").GetString()!).Value)).All(value => value);
            using var body = JsonDocument.Parse(handler.Body!);
            var bodyMatches = JsonElement.DeepEquals(expected.GetProperty("bodyJson"), body.RootElement);
            var affinityNames = new[] { "x-session-id", "session_id", "x-client-request-id", "x-session-affinity", "x-authored" };
            var expectedHeaders = expected.GetProperty("headers").EnumerateArray().ToDictionary(item => item[0].GetString()!, item => item[1].GetString()!, StringComparer.OrdinalIgnoreCase);
            var affinityMatches = affinityNames.All(name => expectedHeaders.GetValueOrDefault(name) == handler.Headers.GetValueOrDefault(name));
            var headerDifferences = expectedHeaders.Keys.Union(handler.Headers.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Where(name => expectedHeaders.GetValueOrDefault(name) != handler.Headers.GetValueOrDefault(name))
                .Select(name => new { name, source = expectedHeaders.GetValueOrDefault(name), native = handler.Headers.GetValueOrDefault(name) }).ToArray();
            var finalMatches = JsonElement.DeepEquals(turn.GetProperty("result"), final) && JsonElement.DeepEquals(turn.GetProperty("result"), semantic.Snapshot.Raw.Value.GetProperty("value"));
            var unchanged = originals.SequenceEqual(history.Take(originals.Length).Select(entry => entry.WireBody.ToString()));
            var ownership = handler.Attempts == 1 && payloadCalls == 1 && responseCalls == 1 && handler.ResponseContent!.Disposals == 1 && run.CleanupCompletion.IsCompletedSuccessfully && canonical.Failure is null;
            try { _ = await handler.Request!.Content!.ReadAsByteArrayAsync(); ownership = false; } catch (ObjectDisposedException) { }
            client.DefaultRequestHeaders.Add("X-Borrowed-Client-Still-Usable", "authored");
            var ok = bodyMatches && affinityMatches && eventMatches && eventJsonMatches && finalMatches && historyMatches && unchanged && ownership && handler.Method == "POST" && handler.RequestUri == expected.GetProperty("url").GetString();
            profileMatches &= ok;
            turnOutcomes.Add(new { turn = turn.GetProperty("turn").GetInt32(), passed = ok, bodyMatches, affinityMatches, completePublicationEventsMatch = eventMatches, completePublicationJsonMatches = eventJsonMatches,
                completePublicationJsonBytesMatch = eventJsonBytesMatch, finalMatches, initialOrNativeContinuedHistoryMatches = historyMatches, canonicalOriginalHistoryUnchanged = unchanged, ownershipJoined = ownership, attemptedSends = handler.Attempts, payloadCalls, responseCalls,
                sourceRequest = expected.Clone(), nativeRequest = new { uri = handler.RequestUri, method = handler.Method, headers = handler.Headers, body = handler.Body,
                    bodySha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(handler.Body!))), bodyJson = body.RootElement.Clone() }, completeRawHeaderDifferences = headerDifferences,
                sourceEmissions = expectedEmissions, nativeEmissions = publicationValues, nativeEmissionJson = publicationJson, nativeDeliveredEvents = delivered,
                sourceFinal = turn.GetProperty("result").Clone(), nativeSemanticFinal = semantic.Snapshot.Raw.Value.Clone(), nativeCanonicalFinal = final.Clone() });
            Console.WriteLine((ok ? "PASS " : "FAIL ") + id + ":" + turn.GetProperty("turn").GetInt32() + $" body={bodyMatches} affinity={affinityMatches} events={eventMatches}/{eventJsonMatches} final={finalMatches} history={historyMatches} cleanup={ownership}");
            if (canonical.Message.StopReason == StopReason.ToolUse)
            {
                // Continue with the actual production final, rather than a source-supplied assistant stand-in.
                history = history.Add(Entry(final)).Add(new("toolResult", JsonData.Parse("{\"role\":\"toolResult\",\"toolCallId\":\"cache-call\",\"toolName\":\"inspect\",\"content\":[{\"type\":\"text\",\"text\":\"Observed seven.\"},{\"type\":\"image\",\"data\":\"Ag==\",\"mimeType\":\"image/png\"}],\"isError\":false,\"timestamp\":123,\"details\":{\"opaque\":{\"keep\":null}}}")));
            }
        }
        if (profileMatches) passed++; else failed++; outcomes.Add(new { id, passed = profileMatches, turns = turnOutcomes });
    }
    catch (Exception error) { failed++; outcomes.Add(new { id, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + id + ": " + error); }
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(new { schemaVersion = 1, fixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), passed, failed, actualTurns = allTurns, completePublicationEvents = allEmissions,
    observations = outcomes, productionAiDll = Identity(typeof(ChatClient).Assembly), executedTestDll = Identity(Assembly.GetExecutingAssembly()),
    scope = "original-P2-08-bounded-OpenRouter-cache-profile", eventBoundary = "complete immutable actual producer publications, including own undefined presence and complete ECMAScript JSON",
    rawHeaders = "All Source/native headers retained and every raw difference reported; affinity-family parity is this unit's header criterion",
    deliveredAliasParity = false, serializedEventPropertyOrderParity = false, liveProvider = false, fullGate = false, packageAcceptance = false, phaseAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"OpenRouter cache profile: {passed} passed, {failed} failed; {allTurns} turns, {allEmissions} complete publications.");
return failed == 0 ? 0 : 1;

static TranscriptEntry Entry(JsonElement body) => new(body.GetProperty("role").GetString()!, JsonData.FromElement(body));
static object Identity(Assembly assembly) { var file = assembly.Location; var data = File.ReadAllBytes(file); return new { path = file, bytes = data.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }; }
sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
{ internal int Disposals; protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); } }
sealed class FixtureHandler(string wire) : HttpMessageHandler
{
    internal int Attempts; internal HttpRequestMessage? Request; internal string? Body, RequestUri, Method; internal Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    internal TrackedContent? ResponseContent;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Attempts++; Request = request; RequestUri = request.RequestUri!.AbsoluteUri; Method = request.Method.Method;
        Body = await request.Content!.ReadAsStringAsync(token);
        Headers = request.Headers.Concat(request.Content.Headers).ToDictionary(header => header.Key.ToLowerInvariant(), header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        ResponseContent = new(Encoding.UTF8.GetBytes(wire)); ResponseContent.Headers.ContentType = new("text/event-stream");
        return new(HttpStatusCode.OK) { Content = ResponseContent };
    }
}
