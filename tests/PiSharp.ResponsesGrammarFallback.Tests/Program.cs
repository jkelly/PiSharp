using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

internal static class Program
{
    private static readonly ModelDescriptor Model = new("fixture", "openai-responses", "openai");
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"input\":{\"type\":\"string\"}},\"required\":[\"input\"]}";
    private static ChatRequest Request(string sampling) => new(Model,
        [new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"Offline grammar fixture.\",\"toolsAdded\":[{\"name\":\"run\",\"description\":\"fixture\",\"parameters\":" + Schema + ",\"constrainedSampling\":" + sampling + "}]}"))], 1);
    private static void Check(bool value, string anchor) { if (!value) throw new IOException(anchor); }
    private static JsonElement Tool(ChatRequest request, ResponsesToolDeclarationProjectionOptions? options = null)
        => new ResponsesToolDeclarationProjector(options).Project(request).Value[0];
    private static readonly string[] Grammars = [
        "{\"type\":\"grammar\",\"variants\":{\"openai_lark\":\"start: /.+/\"}}",
        "{\"type\":\"grammar\",\"variants\":{\"openai_regex\":\".+\"}}",
        "{\"type\":\"grammar\",\"variants\":{}}",
        "{\"type\":\"grammar\"}"
    ];
    private static Task DefaultFallback()
    {
        foreach (var grammar in Grammars)
        {
            var tool = Tool(Request(grammar));
            Check(tool.GetProperty("type").GetString() == "function" && !tool.TryGetProperty("format", out _), "default-grammar-stays-function");
            Check(tool.GetProperty("parameters").GetRawText() == Schema && !tool.TryGetProperty("strict", out _), "default-grammar-keeps-schema-and-omits-unsupported-strict");
        }
        return Task.CompletedTask;
    }
    private static Task UnsupportedStrictFallback()
    {
        var tool = Tool(Request(Grammars[0]), new(SupportsStrictMode: false));
        Check(!tool.TryGetProperty("strict", out _) && tool.GetProperty("parameters").GetRawText() == Schema, "grammar-ignore-does-not-enable-strict");
        return Task.CompletedTask;
    }
    private static Task SupportedStrictPreserved()
    {
        var grammar = Tool(Request(Grammars[0]), new(SupportsStrictMode: true, Strict: true));
        var schema = Tool(Request("{\"type\":\"json_schema\",\"strict\":\"require\"}"), new(SupportsStrictMode: true));
        Check(grammar.GetProperty("strict").GetBoolean() && !grammar.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean(), "grammar-preserves-explicit-supported-strict");
        Check(grammar.GetRawText() == schema.GetRawText(), "json-schema-required-and-explicit-strict-same-wire");
        return Task.CompletedTask;
    }
    private static Task JsonSchemaRefusalPreserved()
    {
        try { Tool(Request("{\"type\":\"json_schema\",\"strict\":\"require\"}"), new(SupportsStrictMode: false)); }
        catch (ResponsesProjectionException error) when (error.Failure == ResponsesProjectionFailure.UnsupportedContent)
        {
            var prefer = Tool(Request("{\"type\":\"json_schema\",\"strict\":\"prefer\"}"), new(SupportsStrictMode: false));
            Check(!prefer.TryGetProperty("strict", out _), "unsupported-prefer-retains-function-without-strict");
            return Task.CompletedTask;
        }
        throw new IOException("unsupported-required-json-schema-must-refuse");
    }
    private static Task BoundAndCancelPreserved()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { new ResponsesToolDeclarationProjector().Project(Request(Grammars[0]), canceled.Token); }
        catch (OperationCanceledException error) when (error.CancellationToken == canceled.Token)
        {
            try { Tool(Request(Grammars[0]), new(MaximumEntryCharacters: 8)); }
            catch (ResponsesProjectionException limit) when (limit.Failure == ResponsesProjectionFailure.ResourceLimit) { return Task.CompletedTask; }
        }
        throw new IOException("grammar-does-not-bypass-cancellation-or-original-input-budget");
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal Task<HttpResponseMessage>? Original; internal JsonData? Wire; internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Original = Capture(request, token); return Original; }
        private async Task<HttpResponseMessage> Capture(HttpRequestMessage request, CancellationToken token)
        {
            var read = (request.Content ?? throw new IOException("missing-content")).ReadAsStringAsync(token);
            try { Wire = JsonData.Parse(await read); }
            catch (Exception direct)
            { if (read.Exception is { } graph) throw new AggregateException("request-content-read-original-and-direct", graph, direct); throw; }
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"fixture\",\"status\":\"completed\",\"output\":[]}}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private static async Task PublicRoute()
    {
        using var handler = new Handler();
        using var provider = NativeProviderFactory.CreateResponses(Model, new("https://api.openai.com/v1/responses"), "synthetic-injected-key", new(Reasoning: false), handler: handler);
        var original = Drain(provider, Request(Grammars[0]));
        var faults = new List<Exception>();
        try { await original; } catch (Exception error) { faults.Add(error); }
        finally
        {
            if (original.Exception is { } graph) faults.Add(graph);
            if (handler.Original is { } send)
            {
                try { await send; } catch (Exception error) { faults.Add(error); }
                if (send.Exception is { } sendGraph) faults.Add(sendGraph);
            }
        }
        if (faults.Count != 0) throw new AggregateException("public-drain/send-full-original-inventory", faults.Distinct<Exception>(ReferenceEqualityComparer.Instance));
        Check(original.IsCompletedSuccessfully && handler.Original?.IsCompletedSuccessfully == true && handler.Calls == 1, "public-drain-and-send-joined");
        var wire = handler.Wire ?? throw new IOException("missing-captured-wire");
        var tool = wire.Value.GetProperty("tools")[0];
        Check(tool.GetProperty("type").GetString() == "function" && !tool.TryGetProperty("strict", out _), "public-factory-default-grammar-function-route-omits-unsupported-strict");
    }
    private static async Task Drain(IChatTransport transport, ChatRequest request)
    {
        var terminal = false;
        await foreach (var item in transport.StreamAsync(request))
                {
            if (item is StreamError error)
            {
                var faults = new List<Exception>();
                if (error.NativeSourceException is { } source) faults.Add(source);
                if (error.NativeCleanupExceptions is { } cleanup) faults.AddRange(cleanup);
                var originals = new List<Task>();
                if (error.NativeSourceTask is { } sourceTask) originals.Add(sourceTask);
                if (error.NativeCleanupTasks is { } cleanupTasks) originals.AddRange(cleanupTasks);
                var distinctOriginals = originals.Distinct<Task>(ReferenceEqualityComparer.Instance).ToArray();
                foreach (var original in distinctOriginals)
                {
                    try { await original; } catch (Exception direct) { faults.Add(direct); }
                    if (original.IsFaulted && original.Exception is { } graph) faults.Add(graph);
                }
                throw new PublicStreamFailure(error, distinctOriginals, faults);
            }
            if (item is StreamDone) terminal = true;
        }
        Check(terminal, "public-authoritative-terminal");
    }
    private sealed class PublicStreamFailure : IOException
    {
        internal StreamError Terminal { get; }
        internal Task[] Originals { get; }
        internal PublicStreamFailure(StreamError terminal, Task[] originals, List<Exception> faults)
            : base(DiagnosticMessage(terminal, faults),
                faults.Count == 0 ? null : new AggregateException(faults.Distinct<Exception>(ReferenceEqualityComparer.Instance)))
        { Terminal = terminal; Originals = originals; }
        private static string DiagnosticMessage(StreamError terminal, List<Exception> faults)
        {
            try { return "unexpected-public-stream-error: " + JsonSerializer.Serialize(terminal); }
            catch (Exception diagnosticFailure) { faults.Add(diagnosticFailure); return "unexpected-public-stream-error: diagnostic serialization failed"; }
        }
    }
    private static async Task Main()
    {
        (string Name, Func<Task> Run)[] groups = [
            ("default-grammar-fallback", DefaultFallback), ("unsupported-strict-fallback", UnsupportedStrictFallback),
            ("supported-json-schema-strict-preserved", SupportedStrictPreserved), ("json-schema-require-refusal-preserved", JsonSchemaRefusalPreserved),
            ("input-budget-and-cancellation-preserved", BoundAndCancelPreserved), ("actual-public-factory-wire", PublicRoute)
        ];
        var faults = new List<Exception>(); var passed = 0;
        foreach (var group in groups)
        {
            Task? original = null;
            try { original = group.Run(); await original; Check(original.IsCompletedSuccessfully, group.Name + ":original-status"); passed++; }
            catch (Exception error) { faults.Add(error); if (original?.Exception is { } graph) faults.Add(graph); }
        }
        if (faults.Count != 0) throw new AggregateException("six-original-group-fault-inventory", faults.Distinct<Exception>(ReferenceEqualityComparer.Instance));
        Console.WriteLine($"Six original groups passed: {passed}.");
    }
}