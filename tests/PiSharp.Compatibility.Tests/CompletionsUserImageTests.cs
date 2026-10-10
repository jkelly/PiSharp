using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

internal static class CompletionsUserImageTests
{
    private const string FixtureHash = "eb5f5e5c1c4170e3e997ac5d9738d520f3044012bbcc165c3714f61edd9e22a0";
    private static readonly ModelDescriptor Model = new("image-model", "openai-completions", "openai");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        using var fixture = ReadFixture();
        foreach (var observation in fixture.RootElement.GetProperty("observations").EnumerateArray())
        {
            var captured = observation.Clone();
            yield return ("completions-user-images.source-complete-request." + captured.GetProperty("id").GetString(), () => SourceRequest(captured));
        }
        yield return ("completions-user-images.typed-admission-and-canonical-retention", Admission);
        yield return ("completions-user-images.exact-budgets-expansion-and-cancellation", Budgets);
    }
    private static async Task SourceRequest(JsonElement captured)
    {
        var modelBody = captured.GetProperty("model");
        var model = new ModelDescriptor(modelBody.GetProperty("id").GetString()!, modelBody.GetProperty("api").GetString()!, modelBody.GetProperty("provider").GetString()!);
        var options = captured.GetProperty("options");
        var request = new ChatRequest(model, captured.GetProperty("context").GetProperty("messages").EnumerateArray().Select(Entry).ToImmutableArray(), 123);
        var originals = request.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
        var fetch = captured.GetProperty("requests")[0];
        var rawSource = fetch.GetProperty("body").GetString()!;
        Equal(fetch.GetProperty("bodyUtf8Sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawSource))));
        using var parsedSource = JsonDocument.Parse(rawSource);
        Check(JsonElement.DeepEquals(parsedSource.RootElement, fetch.GetProperty("bodyJson")), "Complete source raw and parsed capture differ.");
        var factory = new CompletionsKeyAuthRequestFactory(new(fetch.GetProperty("url").GetString()!), model,
            new(RequiresAssistantAfterToolResult: captured.GetProperty("projection").GetProperty("requiresAssistantAfterToolResult").GetBoolean())
                { ModelSupportsImages = modelBody.GetProperty("input").EnumerateArray().Any(value => value.GetString() == "image") },
            new(MaxTokens: Number(options, "maxTokens"), Temperature: Number(options, "temperature")));
        // Repeated materialization must preserve content order and borrow the same untouched canonical input.
        for (var repeat = 0; repeat < 2; repeat++)
        {
            using var http = factory.Create(request, "authored-inert-image-key");
            using var body = JsonDocument.Parse(await http.Content!.ReadAsStringAsync());
            Check(JsonElement.DeepEquals(fetch.GetProperty("bodyJson"), body.RootElement), "Complete request differs from unchanged Pi/SDK capture: " + captured.GetProperty("id").GetString());
            Check(originals.SequenceEqual(request.Messages.Select(entry => entry.WireBody.ToString())), "User image projection changed canonical history.");
        }
        Equal(1, captured.GetProperty("payloadCalls").GetInt32()); Equal(1, captured.GetProperty("responseCalls").GetInt32());
        Equal("stop", captured.GetProperty("result").GetProperty("stopReason").GetString());
        Check(captured.GetProperty("canonicalContextUnchanged").GetBoolean(), "Source mutated canonical context.");
    }
    private static Task Admission()
    {
        foreach (var content in new[]
        {
            """[{"type":"image","data":"private-image-data"}]""",
            """[{"type":"image","mimeType":"image/png"}]""",
            """[{"type":"image","mimeType":null,"data":"private-image-data"}]""",
            """[{"type":"image","mimeType":"image/png","data":7}]"""
        })
        {
            var request = Request(content); var original = request.Messages[0].WireBody.ToString();
            Reject(CompletionsRequestFailure.InvalidTranscript, () => Factory().Create(request, "authored-inert-image-key"));
            Equal(original, request.Messages[0].WireBody.ToString());
        }
        Reject(CompletionsRequestFailure.UnsupportedContent, () => Factory().Create(Request("""[{"type":"audio","data":"private-image-data","mimeType":"audio/wav"}]"""), "authored-inert-image-key"));
        var retained = new ChatRequest(Model, [new("user", JsonData.Parse("""{"role":"user","content":[{"type":"image","data":"AA==","mimeType":"image/png","opaque":{"wide":9007199254740993,"n":1.00,"keep":null}}],"timestamp":123,"opaque":{"keep":null}}"""))], 123);
        var raw = retained.Messages[0].WireBody.ToString(); var parts = new CompletionsTranscriptProjector(new() { ModelSupportsImages = true }).Project(retained).Value[0].GetProperty("content");
        Equal("data:image/png;base64,AA==", parts[0].GetProperty("image_url").GetProperty("url").GetString());
        Equal(2, parts[0].EnumerateObject().Count()); Equal(raw, retained.Messages[0].WireBody.ToString());
        // Owned JSON rejects ambiguity before any provider request can be constructed.
        try { _ = Request("""[{"type":"image","mimeType":"image/png","mimeType":"image/jpeg","data":"private-image-data"}]"""); throw new InvalidOperationException("Duplicate MIME was admitted."); }
        catch (JsonException) { }
        var oldParameters = Enumerable.Repeat(typeof(bool), 7).Concat([typeof(CompletionsToolDeclarationProjectionOptions)]).Concat(Enumerable.Repeat(typeof(int), 8)).ToArray();
        Check(typeof(CompletionsTranscriptProjectionOptions).GetConstructor(oldParameters) is not null, "Existing positional constructor changed.");
        Equal(16, typeof(CompletionsTranscriptProjectionOptions).GetMethod("Deconstruct")!.GetParameters().Length);
        Check(new CompletionsTranscriptProjectionOptions().ModelSupportsImages == false, "Text-only default changed.");
        return Task.CompletedTask;
    }
    private static Task Budgets()
    {
        var request = Request("""[{"type":"text","text":"π"},{"type":"image","mimeType":"image/png","data":"AA=="}]""");
        var raw = request.Messages[0].WireBody.ToString(); var projection = new CompletionsTranscriptProjector(new() { ModelSupportsImages = true }).Project(request).ToString();
        var bytes = Encoding.UTF8.GetByteCount(projection); Check(bytes > projection.Length, "UTF8 budget control has no multibyte content.");
        _ = new CompletionsTranscriptProjector(new(MaximumEntryCharacters: raw.Length, MaximumInputCharacters: raw.Length, MaximumContentBlocks: 2,
            MaximumOutputCharacters: projection.Length, MaximumOutputBytes: bytes) { ModelSupportsImages = true }).Project(request);
        foreach (var options in new[]
        {
            new CompletionsTranscriptProjectionOptions(MaximumEntryCharacters: raw.Length - 1),
            new CompletionsTranscriptProjectionOptions(MaximumInputCharacters: raw.Length - 1),
            new CompletionsTranscriptProjectionOptions(MaximumContentBlocks: 1),
            new CompletionsTranscriptProjectionOptions(MaximumOutputCharacters: projection.Length - 1),
            new CompletionsTranscriptProjectionOptions(MaximumOutputBytes: bytes - 1),
            new CompletionsTranscriptProjectionOptions(MaximumJsonDepth: 3)
        }) Reject(CompletionsRequestFailure.ResourceLimit, () => new CompletionsTranscriptProjector(options with { ModelSupportsImages = true }).Project(request));
        Reject(CompletionsRequestFailure.ResourceLimit, () => Factory(new(MaximumPayloadBytes: 64)).Create(request, "authored-inert-image-key"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { using var unexpected = Factory().Create(request, "authored-inert-image-key", cancelled.Token); throw new InvalidOperationException("Cancelled image request acquired HTTP ownership."); }
        catch (OperationCanceledException error) { Equal(cancelled.Token, error.CancellationToken); }
        Equal(raw, request.Messages[0].WireBody.ToString()); return Task.CompletedTask;
    }
    private static JsonDocument ReadFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository missing.");
        var bytes = File.ReadAllBytes(Path.Combine(directory.FullName, "fixtures/native/completions-user-images-source.json"));
        Equal(FixtureHash, Convert.ToHexStringLower(SHA256.HashData(bytes))); var document = JsonDocument.Parse(bytes);
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f", document.RootElement.GetProperty("sourceSha").GetString());
        Equal(230, document.RootElement.GetProperty("sourceFilesVerified").GetInt32()); Equal(229, document.RootElement.GetProperty("actualLoadedSourceFiles").GetArrayLength());
        foreach (var name in new[] { "sourceOrSdkEdits", "originalFixtureEdits", "prohibitedNetworkCalls" }) Equal(0, document.RootElement.GetProperty(name).GetInt32());
        Equal(25, document.RootElement.GetProperty("observations").GetArrayLength()); return document;
    }
    private static double? Number(JsonElement body, string name) => body.TryGetProperty(name, out var value) ? value.GetDouble() : null;
    private static TranscriptEntry Entry(JsonElement value) => new(value.GetProperty("role").GetString()!, JsonData.FromElement(value));
    private static ChatRequest Request(string content) => new(Model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":" + content + ",\"timestamp\":123}"))], 123);
    private static CompletionsKeyAuthRequestFactory Factory(CompletionsKeyAuthRequestOptions? options = null) => new(new("https://completions.invalid/v1/chat/completions"), Model, options: options);
    private static void Reject(CompletionsRequestFailure failure, Action action)
    {
        try { action(); } catch (CompletionsRequestException error)
        { Equal(failure, error.Failure); Check(!error.Message.Contains("private-image-data", StringComparison.Ordinal), "Private image data leaked into diagnostics."); return; }
        throw new InvalidOperationException("Expected " + failure);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
