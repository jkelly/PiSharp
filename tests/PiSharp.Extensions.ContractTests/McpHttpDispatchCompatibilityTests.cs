using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Transport;

internal static class McpHttpDispatchCompatibilityTests
{
    private const string Prefix = "mcp-http-dispatch.";
    private static readonly ConcurrentQueue<Originals> Inventories = new();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "mixed-case-get-and-response-resume-media-types", MixedCase),
        (Prefix + "unsupported-post-refuses-body-acquisition-and-joins-held-faults", Unsupported)
    ];
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawCapturedOriginals()
        => Inventories.SelectMany(item => item.Export()).ToArray();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static McpHttpBinding Binding(bool openGet) => new(new Uri("https://supplied.invalid/mcp"),
        ImmutableDictionary<string, string>.Empty, new(openGet, false, 1, 0, 1));
    private static HttpResponseMessage Sse(string text, string media)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        return response;
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static async Task MixedCase()
    {
        var originals = new Originals(Prefix + "mixed-case"); Inventories.Enqueue(originals);
        var errors = new List<Exception>();
        try
        {
            foreach (var media in new[] { "text/event-stream", "Text/Event-Stream", "TEXT/EVENT-STREAM" })
            {
                var received = Gate(); var unsupported = Gate(); var posts = 0; var gets = 0;
                var wire = new McpStreamableHttpTransport(Binding(true), request =>
                {
                    HttpResponseMessage response;
                    if (request.Method == HttpMethod.Post)
                    { posts++; response = new(HttpStatusCode.Accepted); }
                    else
                    {
                        gets++;
                        response = gets == 1 ? Sse("data: {\"jsonrpc\":\"2.0\",\"method\":\"supplied\"}\n\n", media)
                            : new(HttpStatusCode.MethodNotAllowed);
                    }
                    return new Operation(originals, media + "/" + request.Method + "/" + gets,
                        Task.FromResult(response), () => { if (gets == 2 && request.Method == HttpMethod.Get) unsupported.TrySetResult(); return Task.CompletedTask; });
                });
                try
                {
                    await originals.Join(originals.Add(media + "/start", wire.StartAsync(new(message =>
                    { Require(message.Value.GetProperty("method").GetString() == "supplied", "Exact supplied notification"); received.TrySetResult(); return ValueTask.CompletedTask; }, error => { lock (errors) errors.Add(error); }), default)));
                    await originals.Join(originals.Add(media + "/initialized", wire.SendAsync(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"), default)));
                    await originals.Join(originals.Add(media + "/receive-diagnostic", received.Task.WaitAsync(TimeSpan.FromSeconds(5))));
                    await originals.Join(originals.Add(media + "/405-diagnostic", unsupported.Task.WaitAsync(TimeSpan.FromSeconds(5))));
                    Require(posts == 1 && gets == 2, "GET lifecycle and no POST replay");
                }
                finally { await originals.Join(originals.Add(media + "/close", wire.CloseAsync())); }

                posts = 0; gets = 0; var replies = 0;
                var resumed = new McpStreamableHttpTransport(Binding(false), request =>
                {
                    var response = request.Method == HttpMethod.Post
                        ? Sse("id: supplied-cursor\n\n", "text/event-stream")
                        : Sse("data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n", media);
                    if (request.Method == HttpMethod.Post) posts++;
                    else { gets++; Require(request.Headers.GetValues("Last-Event-ID").Single() == "supplied-cursor", "Actual resume cursor"); }
                    return new Operation(originals, media + "/resume/" + request.Method, Task.FromResult(response), () => Task.CompletedTask);
                });
                try
                {
                    await originals.Join(originals.Add(media + "/resume-start", resumed.StartAsync(new(message =>
                    { Require(message.Value.GetProperty("id").GetInt32() == 1, "Exact response id"); replies++; return ValueTask.CompletedTask; }, error => { lock (errors) errors.Add(error); }), default)));
                    await originals.Join(originals.Add(media + "/resume-send", resumed.SendAsync(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"supplied\"}"), default)));
                    Require(posts == 1 && gets == 1 && replies == 1, "Mixed-case resume succeeds without POST replay");
                }
                finally { await originals.Join(originals.Add(media + "/resume-close", resumed.CloseAsync())); }
            }
        }
        catch (Exception error) { errors.Add(error); }
        finally { await originals.Drain(errors); }
        originals.Finish(errors);
    }

    private static async Task Unsupported()
    {
        var originals = new Originals(Prefix + "unsupported"); Inventories.Enqueue(originals);
        var errors = new List<Exception>();
        try
        {
            foreach (var faultCleanup in new[] { false, true })
            {
                var disposeFault = new IOException("exact supplied disposal");
                var stopLeft = new IOException("exact supplied stop-left"); var stopRight = new IOException("exact supplied stop-right");
                var allowed = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
                if (faultCleanup) { allowed.Add(disposeFault); allowed.Add(stopLeft); allowed.Add(stopRight); }
                var content = new NeverAcquireContent(originals, faultCleanup ? disposeFault : null);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                var stopGate = Gate(); var stopEntered = Gate(); Task? stopOriginal = null; var sends = 0;
                async Task Stop()
                { stopEntered.TrySetResult(); await stopGate.Task; if (faultCleanup) throw new AggregateException(stopLeft, stopRight); }
                var wire = new McpStreamableHttpTransport(Binding(false), _ => new Operation(originals,
                    "unsupported/" + faultCleanup, Task.FromResult(response), () =>
                    { sends++; return stopOriginal ??= originals.Add("held-request-stop/" + faultCleanup, Stop(), error => Known(error, allowed)); }));
                Task? send = null;
                try
                {
                    await originals.Join(originals.Add("unsupported-start/" + faultCleanup, wire.StartAsync(new(_ => ValueTask.CompletedTask, error => { lock (errors) errors.Add(error); }), default)));
                    send = originals.Add("unsupported-send/" + faultCleanup,
                        wire.SendAsync(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"supplied\"}"), default), error => Known(error, allowed));
                    await originals.Join(originals.Add("disposal-entered/" + faultCleanup, content.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5))));
                    await originals.Join(originals.Add("stop-entered/" + faultCleanup, stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5))));
                    Require(content.Acquisitions == 0 && !send.IsCompleted, "Unsupported media refuses body while real retirement remains held");
                    content.Release.TrySetResult(); await originals.Join(content.DisposalOriginal!);
                    Require(!send.IsCompleted, "Request stop remains separately joined after physical disposal");
                    stopGate.TrySetResult();
                    await originals.Join(send); var direct = originals.Direct(send);
                    Require(send.IsFaulted && !send.IsCanceled && direct is not null, "Unsupported request is a Faulted original");
                    var protocol = Nodes(direct!).OfType<McpRuntimeProtocolException>().ToArray();
                    Require(protocol.Length == 1 && protocol[0].Message == "Unsupported MCP HTTP response content type", "Exact media refusal retained");
                    allowed.Add(protocol[0]); await originals.Join(send);
                    Require(content.Acquisitions == 0 && sends == 1, "No body acquisition or stop retry");
                    if (faultCleanup) Require(Nodes(direct!).Contains(disposeFault) && Nodes(direct!).Contains(stopLeft) && Nodes(direct!).Contains(stopRight), "Each independent physical cleanup fault retained");
                }
                finally
                {
                    content.Release.TrySetResult(); stopGate.TrySetResult();
                    if (send is not null) await originals.Join(send);
                    await originals.Join(originals.Add("unsupported-close/" + faultCleanup, wire.CloseAsync(), error => Known(error, allowed)));
                }
            }
            var sendLeft = new IOException("exact send-left"); var sendRight = new IOException("exact send-right");
            var sendAllowed = new HashSet<Exception>(ReferenceEqualityComparer.Instance) { sendLeft, sendRight };
            var broken = new McpStreamableHttpTransport(Binding(false), _ => new Operation(originals, "send-fault",
                originals.Add("actual-send-fault", Task.FromException<HttpResponseMessage>(new AggregateException(sendLeft, sendRight)), error => Known(error, sendAllowed)), () => Task.CompletedTask));
            try
            {
                await originals.Join(originals.Add("send-fault-start", broken.StartAsync(new(_ => ValueTask.CompletedTask, error => { lock (errors) errors.Add(error); }), default)));
                var send = originals.Add("native-send-fault", broken.SendAsync(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"supplied\"}"), default), error => Known(error, sendAllowed));
                await originals.Join(send); Require(send.IsFaulted, "Actual send failure retained");
            }
            finally { await originals.Join(originals.Add("send-fault-close", broken.CloseAsync())); }
        }
        catch (Exception error) { errors.Add(error); }
        finally { await originals.Drain(errors); }
        originals.Finish(errors);
    }
    private static Exception[] Nodes(Exception root)
    {
        var result = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var queue = new Queue<Exception>(); queue.Enqueue(root); var edges = 0;
        while (queue.TryDequeue(out var error))
        {
            if (!result.Add(error)) continue;
            if (result.Count > 1024) throw new InvalidOperationException("Fault node bound");
            if (error is AggregateException aggregate)
            { foreach (var child in aggregate.InnerExceptions) { if (++edges > 4096) throw new InvalidOperationException("Fault edge bound"); queue.Enqueue(child); } }
            else if (error.InnerException is { } inner) { if (++edges > 4096) throw new InvalidOperationException("Fault edge bound"); queue.Enqueue(inner); }
        }
        return result.ToArray();
    }
    private static bool Known(Exception root, HashSet<Exception> allowed)
        => Nodes(root).All(error => error is AggregateException aggregate ? aggregate.InnerExceptions.Count > 0 : allowed.Contains(error));
    private sealed class Operation(Originals originals, string role, Task<HttpResponseMessage> send, Func<Task> stop) : IMcpAdmittedHttpRequestOperation
    {
        private readonly object gate = new(); private Task? stopped;
        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return new(originals.Add(role + "/physical-send", send)); }
        public Task StopAsync() { lock (gate) return stopped ??= originals.Add(role + "/physical-stop", stop()); }
    }
    private sealed class NeverAcquireContent : HttpContent
    {
        private readonly Originals originals; private readonly IOException? failure;
        internal NeverAcquireContent(Originals originals, IOException? failure)
        { this.originals = originals; this.failure = failure; Headers.ContentType = new("text/plain"); }
        private readonly object gate = new(); private Task? disposal;
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate(); internal int Acquisitions;
        internal Task? DisposalOriginal { get { lock (gate) return disposal; } }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Interlocked.Increment(ref Acquisitions); return Task.FromException(new IOException("Forbidden body acquisition")); }
        protected override Task<Stream> CreateContentReadStreamAsync()
        { Interlocked.Increment(ref Acquisitions); return Task.FromException<Stream>(new IOException("Forbidden body acquisition")); }
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => CreateContentReadStreamAsync();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Task original;
                lock (gate) original = disposal ??= originals.Add("held-physical-disposal", Task.Run(async () =>
                { Entered.TrySetResult(); await Release.Task; if (failure is not null) throw failure; }), error => ReferenceEquals(error, failure) || error is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(child => ReferenceEquals(child, failure)));
                original.GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
    private sealed class Originals(string prefix)
    {
        private sealed class Record(Task task, string phase, Func<Exception, bool>? expected)
        {
            internal readonly Task Task = task; internal readonly string Phase = phase;
            internal readonly Func<Exception, bool>? Expected = expected;
            internal bool Joined; internal AggregateException? Aggregate; internal Exception? Direct;
        }
        private readonly object gate = new(); private readonly Dictionary<Task, Record> records = new(ReferenceEqualityComparer.Instance);
        internal T Add<T>(string phase, T task, Func<Exception, bool>? expected = null) where T : Task
        { lock (gate) { if (!records.ContainsKey(task)) records.Add(task, new(task, prefix + "/" + phase, expected)); } return task; }
        internal async Task Join(Task task)
        {
            Record item; lock (gate) item = records[task];
            lock (gate) if (item.Joined) return;
            Exception? direct = null; try { await task.ConfigureAwait(false); } catch (Exception error) { direct = error; }
            lock (gate) if (!item.Joined) { item.Direct = direct; item.Aggregate = task.IsFaulted ? task.Exception : null; item.Joined = true; }
        }
        internal Exception? Direct(Task task) { lock (gate) return records[task].Direct; }
        internal async Task Drain(List<Exception> errors)
        {
            while (true)
            { Task[] pending; lock (gate) pending = records.Values.Where(item => !item.Joined).Select(item => item.Task).ToArray(); if (pending.Length == 0) break; foreach (var task in pending) try { await Join(task); } catch (Exception error) { errors.Add(error); } }
            Record[] captured; lock (gate) captured = records.Values.ToArray();
            foreach (var item in captured)
            {
                if (item.Direct is null) continue;
                try { if (item.Expected is not null && item.Expected(item.Aggregate ?? item.Direct)) continue; }
                catch (Exception error) { errors.Add(error); }
                errors.Add(item.Aggregate ?? item.Direct);
            }
        }
        internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] Export()
        { lock (gate) return records.Values.Select(item => (item.Phase, item.Task, item.Aggregate, item.Direct)).ToArray(); }
        internal void Finish(List<Exception> errors)
        { if (errors.Count > 0) throw new CapturedFailure(Export().Select(item => item.Original).ToArray(), errors); }
    }
    private sealed class CapturedFailure(Task[] originals, IEnumerable<Exception> errors) : Exception("Actual HTTP dispatch compatibility control failed.", new AggregateException(errors))
    { internal Task[] Originals { get; } = originals; }
}
