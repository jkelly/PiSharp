using System.Net;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Standalone authored executable. No shared test entry point or production changes.
internal static class NativePiMessages
{
    private sealed record Original(string Name, Task Task)
    {
        public bool Joined { get; set; }
        public AggregateException? Aggregate { get; set; }
        public Exception? Direct { get; set; }
    }
    private static readonly List<Original> Originals = [];
    private static readonly List<Exception> Faults = [];
    private static async Task<T> Join<T>(string name, Task<T> task)
    {
        var row = Originals.Find(r => ReferenceEquals(r.Task, task));
        if (row is null) { row = new(name, task); Originals.Add(row); }
        row.Joined = true;
        try { return await task.ConfigureAwait(false); }
        catch (Exception error) { row.Aggregate = task.Exception; row.Direct = error; throw; }
    }
    private static void Check(bool condition, string anchor)
    { if (!condition) throw new InvalidOperationException(anchor); }
    private static async Task<int> Main(string[] args)
    {
        var results = new List<object>();
        try
        {
            Check(args.Length == 1 && Path.IsPathFullyQualified(args[0]), "absolute paired input required");
            var bytes = File.ReadAllBytes(args[0]); Check(bytes.Length <= 65536, "input byte bound");
            using var document = JsonDocument.Parse(bytes); var input = document.RootElement;
            Check(input.GetProperty("originalCommit").GetString() == "d86654abb8862e201933517d6f1fce9f88dd117f", "original pin");
            var model = input.GetProperty("model");
            var catalog = FrozenModelCatalog.ReadProviderJson("inert-provider", Encoding.UTF8.GetBytes(
                "{\"pi-messages\":{\"chat:inert-model\":" + model.GetRawText() + "}}"));
            NativePiMessagesFramingRegression.Verify();
            var wire = BuildWire(input.GetProperty("events"));
            var names = input.GetProperty("cases").EnumerateArray().Select(e => e.GetString() ?? throw new InvalidOperationException()).ToArray();
            Check(names.SequenceEqual(new[] { "pi-messages.direct-settled-tool-alias", "pi-messages.simple-settled-tool-alias" }), "exact two cases");
            foreach (var name in names)
            {
                var handler = new Handler(wire); var client = new HttpClient(handler, disposeHandler: false);
                var frames = new List<StreamEvent>(); ToolCallContent? started = null; ToolCallContent? ended = null;
                try
                {
                    var options = new PiMessagesProviderOptions("inert-key") { EnvironmentLookup = _ => null, SessionId = "inert-session" };
                    var provider = name.Contains(".simple-", StringComparison.Ordinal) ? PiMessagesModelProvider.CreateSimple(catalog, client, options) : PiMessagesModelProvider.CreateDirect(catalog, client, options);
                    var messages = input.GetProperty("context").GetProperty("messages").EnumerateArray().Select(m => new TranscriptEntry(m.GetProperty("role").GetString()!, JsonData.Parse(m.GetRawText()))).ToImmutableArray();
                    var drain = Drain(provider.Transport.StreamAsync(new(provider.Models.Single(), messages, 123)), frames);
                    Originals.Add(new(name + ".drain", drain)); await Join(name + ".drain", drain).ConfigureAwait(false);
                    started = frames.OfType<ToolCallStarted>().Single().ToolCall; ended = frames.OfType<ToolCallEnded>().Single().ToolCall;
                    Check(frames.OfType<StreamError>().Count() == 0 && frames.OfType<StreamTerminalEvent>().Single().Reason == StopReason.ToolUse, "successful toolUse terminal");
                    Check(started.Id == "provisional-call" && !started.Arguments.Value.EnumerateObject().Any(), "earlier native immutable value");
                    Check(ended.Id == "final-call" && ended.Name == "other" && ended.Arguments.Value.GetProperty("value").GetInt32() == 7, "authoritative end");
                    Check(handler.Requests.Count == 1, "one physical send");
                }
                catch (Exception error) { Faults.Add(error); }
                finally
                {
                    // Every captured send/body/drain original is joined, including completed ones.
                    foreach (var row in Originals.Where(r => !r.Joined).ToArray())
                    {
                        row.Joined = true; try { await row.Task.ConfigureAwait(false); }
                        catch (Exception error) { row.Aggregate = row.Task.Exception; row.Direct = error; Faults.Add(error); }
                    }
                    try { client.Dispose(); } catch (Exception error) { Faults.Add(error); }
                    try { handler.Dispose(); } catch (Exception error) { Faults.Add(error); }
                }
                if (started is not null && ended is not null)
                    results.Add(new { name, request = handler.Requests.Single().Value, finalCall = Call(ended), settledStart = Call(started), sameStartEnd = ReferenceEquals(started, ended), responseDisposed = handler.Content?.Disposed });
            }
            Check(Originals.All(r => r.Joined && r.Task.IsCompletedSuccessfully), "original task completion");
            Check(Faults.Count == 0, "case faults retained");
            var report = new { schemaVersion = 1, implementation = "native", originalCommit = input.GetProperty("originalCommit").GetString(), inputSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), cases = results, originals = Originals.Select(r => new { r.Name, status = r.Task.Status.ToString(), r.Joined }) };
            Console.WriteLine(JsonSerializer.Serialize(report)); return 0;
        }
        catch (Exception error)
        {
            Faults.Add(error);
            foreach (var row in Originals) { if (row.Aggregate is not null) Faults.Add(row.Aggregate); if (row.Direct is not null) Faults.Add(row.Direct); }
            try { Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, implementation = "native", failed = true, cases = results, originals = Originals.Select(r => new { r.Name, status = r.Task.Status.ToString(), r.Joined }), faultGraph = Graph(Faults) })); }
            catch (Exception reportError) { Faults.Add(reportError); }
            // Raw task references and full aggregate/direct identities survive serializer/output failures.
            throw new RetainedFailure(Originals.ToArray(), Faults.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray());
        }
    }
    private sealed class RetainedFailure(object originals, Exception[] roots) : Exception("Paired native observation failed; raw originals retained", new AggregateException(roots))
    { public object Originals { get; } = originals; public Exception[] Roots { get; } = roots; }
    private static object[] Graph(IEnumerable<Exception> roots)
    {
        var ids = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance); var queue = new Queue<Exception>(); var nodes = new List<object>(); var edges = 0;
        int Add(Exception error) { if (ids.TryGetValue(error, out var id)) return id; Check(ids.Count < 1024, "graph node bound; raw roots retained"); id = ids.Count; ids.Add(error, id); queue.Enqueue(error); return id; }
        foreach (var error in roots) Add(error);
        while (queue.TryDequeue(out var error))
        {
            var children = error is AggregateException a ? a.InnerExceptions.ToArray() : error.InnerException is { } child ? new[] { child } : [];
            edges += children.Length; Check(edges <= 4096, "graph edge bound; raw roots retained");
            nodes.Add(new { id = ids[error], type = error.GetType().FullName, error.Message, error.StackTrace, children = children.Select(Add).ToArray() });
        }
        return nodes.ToArray();
    }
    private static object Call(ToolCallContent call) => new { type = "toolCall", id = call.Id, name = call.Name, arguments = call.Arguments.Value };
    // Match the original fixture's JSON.stringify: GetRawText preserves pretty
    // input newlines, but Pi's SSE parser consumes only the first data line.
    internal static string BuildWire(JsonElement events) => string.Concat(events.EnumerateArray()
        .Select(e => "data: " + JsonSerializer.Serialize(e) + "\n\n"));
    private static async Task<bool> Drain(IAsyncEnumerable<StreamEvent> source, List<StreamEvent> frames)
    {
        await foreach (var frame in source.ConfigureAwait(false))
        {
            frames.Add(frame); Check(frames.Count <= 16, "event count bound");
            if (frame is StreamError error)
            {
                if (error.NativeSourceException is { } sourceError) Faults.Add(sourceError);
                if (error.NativeSourceTask is { } sourceTask) Originals.Add(new("stream.source-error", sourceTask));
                if (error.NativeCleanupExceptions is { } cleanupErrors) Faults.AddRange(cleanupErrors);
                if (error.NativeCleanupTasks is { } cleanupTasks) foreach (var task in cleanupTasks) Originals.Add(new("stream.cleanup-error", task));
                throw new InvalidOperationException("unexpected genuine public stream error: " + error.Message);
            }
        }
        return true;
    }
    private sealed class Content(byte[] bytes) : ByteArrayContent(bytes)
    { public bool Disposed; protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); } }
    private sealed class Handler(string wire) : HttpMessageHandler
    {
        public readonly List<JsonData> Requests = []; public Content? Content;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var task = Exchange(request, token); Originals.Add(new("http.send", task)); return task; }
        private async Task<HttpResponseMessage> Exchange(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri?.AbsoluteUri == "https://pi-messages.invalid/base/messages", "inert endpoint");
            var read = request.Content?.ReadAsStringAsync(token) ?? throw new InvalidOperationException("request body missing");
            Requests.Add(JsonData.Parse(await Join("http.body-read", read).ConfigureAwait(false)));
            Content = new(Encoding.UTF8.GetBytes(wire)); Content.Headers.ContentType = new("text/event-stream");
            return new(HttpStatusCode.OK) { Content = Content };
        }
    }
}
