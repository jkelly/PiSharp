using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// SOURCE ONLY. These cases acquire IPv4 loopback resources only when a separately admitted runner
// executes this family. No listener/client/socket/process was acquired while authoring this file.
internal static class McpAdmittedHttpLoopbackTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-http-loopback.exact-entry-admission-before-effects", Admission),
        ("mcp-http-loopback.actual-preopen-runtime-factory-binds-discovered-catalog", PreOpenFactory),
        ("mcp-http-loopback.actual-host-normal-close-aborts-held-headers", () => HostClose(Mode.Headers, false)),
        ("mcp-http-loopback.actual-host-owner-shutdown-aborts-held-headers", () => HostClose(Mode.Headers, true)),
        ("mcp-http-loopback.actual-host-normal-close-disposes-held-post-body", () => HostClose(Mode.PostBody, false)),
        ("mcp-http-loopback.actual-host-owner-shutdown-disposes-held-post-body", () => HostClose(Mode.PostBody, true)),
        ("mcp-http-loopback.actual-host-normal-close-disposes-held-get-body", () => HostClose(Mode.GetBody, false)),
        ("mcp-http-loopback.actual-host-owner-shutdown-disposes-held-get-body", () => HostClose(Mode.GetBody, true)),
        ("mcp-http-loopback.concurrent-and-later-request-survive-peer-abort", IndependentRequests),
        ("mcp-http-loopback.normal-close-joins-late-initialize-headers", () => LateStartup(false)),
        ("mcp-http-loopback.owner-shutdown-joins-late-initialize-headers", () => LateStartup(true))
    ];
    private enum Mode { Headers, PostBody, GetBody, LateInitialize }
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new IOException("MCP loopback ownership assertion failed"); }
    private static async Task Watch(Task signal) => await signal.WaitAsync(ObservationBound);
    private static async Task<Exception?> Observe(Task original) { try { await original; return null; } catch (Exception error) { return error; } }
    private static void Rethrow(Exception? assertion, params Exception?[] originals)
    {
        var errors = originals.Where(item => item is not null).Cast<Exception>().ToList();
        if (assertion is not null) errors.Insert(0, assertion);
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static McpServerEntry Entry(Uri endpoint) => new("demo", McpConfigurationReader.Validate("demo",
        JsonData.Parse(JsonSerializer.Serialize(new { url = endpoint.AbsoluteUri, exposure = "direct" })).Value).Config!, "explicit-local-fixture", McpConfigurationScope.Extension);
    private sealed class InertHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; throw new IOException("No HTTP effects admitted in metadata control"); }
    }
    private static async Task Admission()
    {
        using var handler = new InertHandler(); using var client = new HttpClient(handler, false);
        var entry = Entry(new("http://127.0.0.1:1/mcp"));
        var binding = new McpHttpBinding(new("http://127.0.0.1:1/mcp"), ImmutableDictionary<string, string>.Empty, new(false, false));
        var options = new McpRuntimeOptions(1, "0.99.1");
        var factory = AdmittedMcpHttpChannelFactory.Create(entry, binding, client, options);
        try { AdmittedMcpHttpChannelFactory.Create(entry, binding, client, options, new(MaximumFrameBytes: options.Limits.MaximumResponseBytes + 1)); throw new IOException("Oversized wire ceiling accepted"); }
        catch (ArgumentException) { }
        try { await factory(entry with { }, default); throw new IOException("Foreign copied entry accepted"); }
        catch (InvalidOperationException) { }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await factory(entry, canceled.Token); throw new IOException("Canceled admission accepted"); }
        catch (OperationCanceledException) { }
        var channel = await factory(entry, default); await channel.CloseAsync(); Check(handler.Calls == 0);
    }
    private sealed class Resource(Func<Task> release) : IAsyncDisposable
    {
        private readonly Lazy<Task> close = new(release);
        public ValueTask DisposeAsync() => new(close.Value);
    }
    private static async Task PreOpenFactory()
    {
        await using var endpoint = new Endpoint(Mode.Headers);
        using var sockets = Handler(); using var client = new HttpClient(sockets, false) { Timeout = Timeout.InfiniteTimeSpan };
        var extensions = new ExtensionRegistry(); var policy = new Policy(); var nativeReleased = 0;
        var backend = new SessionStorageBackend(Path.Combine(Path.GetTempPath(), "mcp-http-preopen-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
        var native = new SessionRuntimeRegistry([new(Model, new Chat())], [], policy, new() { BindNestedCallsToSessionOwner = true });
        var scope = await extensions.ActivateAsync("http-preopen", new EmptyExtension()); var entry = Entry(endpoint.Address);
        var binding = new McpHttpBinding(endpoint.Address, ImmutableDictionary<string, string>.Empty, new(false, false));
        McpPreOpenServerCapture? captured = null;
        var factory = new McpSessionRuntimeFactory((cwd, generation, token) =>
        {
            Check(cwd == backend.Directory && generation == 1);
            var options = new McpRuntimeOptions(generation, "0.99.1");
            return ValueTask.FromResult(new McpSessionRuntimeAdmission(native,
                new Resource(() => { nativeReleased++; return Task.CompletedTask; }), new Resource(() => extensions.DisposeAsync().AsTask()),
                policy, new([entry], []),
                [new("demo", actual => Check(ReferenceEquals(actual, entry)), async (actual, current, cancellation) =>
                {
                    captured = await McpPreOpenServerCapture.AcquireAsync(actual, extensions, scope, current, policy,
                        (name, arguments, validationToken) => ValueTask.FromResult(true),
                        AdmittedMcpHttpChannelFactory.Create(entry, binding, client, options), options,
                        (registry, prepared) => prepared.PreparedHooks ?? registry.PreparedToolHooks, cancellation);
                    return captured;
                })], false, (plan, current) => new(current, [])));
        });
        var lifecycle = new PersistentSessionLifecycle(native, () => 1, () => Guid.NewGuid().ToString("N"), backend: backend, runtimeForAttachment: factory.AcquireAsync);
        PersistentAgentSession? initial = null; ReplaceableAgentSession? owner = null; Exception? assertion = null;
        Task<PersistentAgentSession>? creating = null; Task<ReplaceableAgentSession>? attaching = null;
        try
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "http-preopen", timestamp = "2026-10-05T00:00:00.000Z", cwd = backend.Directory }));
            creating = lifecycle.CreateAsync(Path.Combine(backend.Directory, "http-preopen.jsonl"), header, Model);
            await Watch(creating); initial = await creating;
            attaching = lifecycle.AttachAsync(initial); await Watch(attaching); owner = await attaching;
            var bound = owner.Current.Session.CaptureToolCatalogRegistry();
            Check(captured is not null && captured.ReservedGeneration == owner.Current.Generation && bound.InvocationOwnerGeneration == 1);
            Check(captured!.CatalogSnapshot.Connected && ReferenceEquals(captured.CatalogSnapshot.Entry, entry));
            Check(bound.RegisteredTools.Count(tool => tool.Adapter.Name == "mcp__demo__a") == 1 && endpoint.InitializeCount == 1);
            var stop = owner.StopAdmissionAndJoinAsync(); await stop; Check(ReferenceEquals(stop, owner.StopAdmissionAndJoinAsync()));
            Check(extensions.CaptureSnapshot().Tools.IsEmpty); await owner.DisposeAsync(); Check(nativeReleased == 1);
            // Ordinary host close does not take ownership of the explicitly borrowed client.
            using var laterRequest = Request(endpoint.Address, "ping", 99); var later = AdmittedHttpClientRequestFactory.Create(client)(laterRequest);
            try { using var response = await later.SendAsync(default); Check(response.IsSuccessStatusCode); }
            finally { await later.StopAsync(); }
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            var endpointClose = endpoint.CloseAsync(); var errors = new List<Exception?>();
            if (creating is not null)
            {
                errors.Add(await Observe(creating));
                if (initial is null && creating.IsCompletedSuccessfully) initial = creating.Result;
            }
            if (attaching is not null)
            {
                errors.Add(await Observe(attaching));
                if (owner is null && attaching.IsCompletedSuccessfully) owner = attaching.Result;
            }
            if (owner is not null) errors.Add(await Observe(owner.DisposeAsync().AsTask()));
            else if (initial is not null) errors.Add(await Observe(initial.DisposeAsync().AsTask()));
            if (captured is not null && owner is null) errors.Add(await Observe(captured.CloseAsync()));
            errors.Add(await Observe(extensions.DisposeAsync().AsTask())); errors.Add(await Observe(endpointClose));
            Rethrow(assertion, errors.ToArray());
        }
    }

    private static async Task HostClose(Mode mode, bool shutdown)
    {
        await using var fixture = await HostFixture.Create(mode);
        var connect = fixture.Server.ConnectAsync(); Task? run = null, close = null; Task<int>? heldRead = null; Exception? assertion = null;
        try
        {
            await Watch(connect); await connect;
            if (mode != Mode.GetBody) run = fixture.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"invoke\",\"timestamp\":1}")));
            await Watch(fixture.Endpoint.HeldEntered.Task);
            if (mode != Mode.Headers)
            {
                await Watch(fixture.Observer.HeldRead.Task);
                heldRead = await fixture.Observer.HeldRead.Task;
                Check(!heldRead.IsCompleted);
            }
            close = shutdown ? fixture.Owner.StopAdmissionAndJoinAsync() : fixture.Server.CloseAsync();
            Check(ReferenceEquals(close, shutdown ? fixture.Owner.StopAdmissionAndJoinAsync() : fixture.Server.CloseAsync()));
            // Header release is never the cause of successful abort. For body controls the native
            // ReadAsync deliberately ignores the operation token: owned disposal must unblock it.
            await Watch(fixture.Endpoint.PeerAborted.Task);
            if (heldRead is not null) { await Watch(fixture.Observer.DisposalEntered.Task); await Observe(heldRead); }
            var closeFailure = await Observe(close); var runFailure = run is null ? null : await Observe(run);
            if (runFailure is OperationCanceledException) runFailure = null;
            Rethrow(null, closeFailure, runFailure);
            Check(!fixture.Endpoint.HeadersRelease.Task.IsCompleted && fixture.Server.CatalogWithdrawalAcknowledged);
            Check(fixture.Extensions.CaptureSnapshot().Tools.IsEmpty && fixture.Owner.Current.LifetimeToken.IsCancellationRequested == shutdown);
            Check(fixture.Policy.Calls == (mode == Mode.GetBody ? 0 : 1));
            var requests = fixture.Endpoint.RequestCount;
            await fixture.Server.CloseAsync(); Check(fixture.Endpoint.RequestCount == requests);
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            // On failed qualification initiate endpoint stop before joining original effects. This
            // cleanup does not turn the earlier abort/deadline failure into a passing result.
            var endpointClose = fixture.Endpoint.CloseAsync();
            var ownerClose = fixture.Owner.StopAdmissionAndJoinAsync();
            var errors = new List<Exception?> { await Observe(endpointClose), await Observe(ownerClose) };
            errors.Add(await Observe(connect));
            if (heldRead is not null) await Observe(heldRead);
            if (close is not null) errors.Add(await Observe(close));
            if (run is not null)
            {
                var runFailure = await Observe(run);
                if (runFailure is not OperationCanceledException) errors.Add(runFailure);
            }
            Rethrow(assertion, errors.ToArray());
        }
    }
    private static async Task LateStartup(bool shutdown)
    {
        await using var fixture = await HostFixture.Create(Mode.LateInitialize);
        var connect = fixture.Server.ConnectAsync(); Task? close = null; Exception? assertion = null;
        try
        {
            await Watch(fixture.Endpoint.HeldEntered.Task);
            close = shutdown ? fixture.Owner.StopAdmissionAndJoinAsync() : fixture.Server.CloseAsync();
            await Watch(fixture.Observer.HeaderCanceled.Task);
            // Release races owner stop; either actual abort or transferred headers is valid, but all
            // acquired originals must settle and no initialize POST may be replayed after shutdown.
            fixture.Endpoint.HeadersRelease.TrySetResult();
            var connectionFailure = await Observe(connect); Check(connectionFailure is not null);
            Rethrow(null, await Observe(close)); Check(fixture.Endpoint.InitializeCount == 1);
            Check(ReferenceEquals(close, shutdown ? fixture.Owner.StopAdmissionAndJoinAsync() : fixture.Server.CloseAsync()));
            Check(fixture.Server.CatalogWithdrawalAcknowledged);
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            var endpointClose = fixture.Endpoint.CloseAsync(); var ownerClose = fixture.Owner.StopAdmissionAndJoinAsync();
            var errors = new List<Exception?> { await Observe(endpointClose), await Observe(ownerClose) };
            if (close is not null) errors.Add(await Observe(close)); await Observe(connect);
            Rethrow(assertion, errors.ToArray());
        }
    }
    private static async Task IndependentRequests()
    {
        await using var endpoint = new Endpoint(Mode.Headers);
        using var sockets = Handler(); using var client = new HttpClient(sockets, false) { Timeout = Timeout.InfiniteTimeSpan };
        var factory = AdmittedHttpClientRequestFactory.Create(client);
        using var firstRequest = Request(endpoint.Address, "tools/call", 1); using var secondRequest = Request(endpoint.Address, "ping", 2);
        var first = factory(firstRequest); var second = factory(secondRequest);
        var one = first.SendAsync(default).AsTask(); var two = second.SendAsync(default).AsTask(); Task? stop = null; Exception? assertion = null;
        HttpResponseMessage? secondResponse = null, laterResponse = null;
        try
        {
            await Watch(endpoint.HeldEntered.Task); await Watch(endpoint.SecondEntered.Task);
            Check(!one.IsCompleted && !two.IsCompleted);
            stop = first.StopAsync(); await Watch(endpoint.PeerAborted.Task); Rethrow(null, await Observe(stop));
            Check(await Observe(one) is OperationCanceledException && !endpoint.HeadersRelease.Task.IsCompleted);
            // The other physical request is still active when the first request aborts. Its own
            // peer must survive, then complete only after its independently admitted release.
            Check(!two.IsCompleted && !endpoint.SecondAborted.Task.IsCompleted);
            endpoint.SecondRelease.TrySetResult(); await Watch(two); secondResponse = await two;
            Check(secondResponse.IsSuccessStatusCode); await second.StopAsync();
            using var laterRequest = Request(endpoint.Address, "ping", 3); var later = factory(laterRequest);
            try { laterResponse = await later.SendAsync(default); Check(laterResponse.IsSuccessStatusCode); }
            finally { await later.StopAsync(); }
            Check(endpoint.CallCount == 1 && endpoint.RequestCount == 3 && ReferenceEquals(stop, first.StopAsync()));
        }
        catch (Exception error) { assertion = error; }
        finally
        {
            var endpointClose = endpoint.CloseAsync(); var firstStop = first.StopAsync(); var secondStop = second.StopAsync();
            endpoint.SecondRelease.TrySetResult();
            var errors = new[] { await Observe(endpointClose), await Observe(firstStop), await Observe(secondStop) };
            await Observe(one); await Observe(two); secondResponse?.Dispose(); laterResponse?.Dispose();
            if (secondResponse is null && two.IsCompletedSuccessfully) two.Result.Dispose();
            Rethrow(assertion, errors);
        }
    }
    private static HttpRequestMessage Request(Uri endpoint, string method, int id) => new(HttpMethod.Post, endpoint)
    { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method }), Encoding.UTF8, "application/json") };
    private static SocketsHttpHandler Handler() => new()
    { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = ObservationBound, MaxConnectionsPerServer = 8 };

    private sealed class Observer(SocketsHttpHandler sockets) : DelegatingHandler(sockets)
    {
        internal readonly TaskCompletionSource DisposalEntered = Gate(), HeaderCanceled = Gate();
        internal readonly TaskCompletionSource<Task<int>> HeldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var registration = token.Register(() => HeaderCanceled.TrySetResult());
            var response = await base.SendAsync(request, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream") response.Content = new ObservedContent(response.Content, this);
            return response;
        }
    }
    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent original; private readonly Observer observer;
        internal ObservedContent(HttpContent original, Observer observer)
        { this.original = original; this.observer = observer; foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException("Body buffering is not admitted");
        protected override async Task<Stream> CreateContentReadStreamAsync() => new ObservedStream(await original.ReadAsStreamAsync(), observer);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => new ObservedStream(await original.ReadAsStreamAsync(token), observer);
        protected override void Dispose(bool disposing) { observer.DisposalEntered.TrySetResult(); if (disposing) original.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ObservedStream(Stream original, Observer observer) : Stream
    {
        private int receivedPreludeBytes;
        public override bool CanRead => original.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var physical = original.ReadAsync(buffer, CancellationToken.None).AsTask();
            // The endpoint supplies exactly eight decoded SSE comment bytes before holding all
            // further body data. Ignore transient reads while that prelude is still arriving;
            // publish the exact next held native original once the entire prelude was consumed.
            if (receivedPreludeBytes >= 8 && !physical.IsCompleted) observer.HeldRead.TrySetResult(physical);
            var read = await physical.ConfigureAwait(false); receivedPreludeBytes += read; return read;
        }
        public override ValueTask DisposeAsync() { observer.DisposalEntered.TrySetResult(); return original.DisposeAsync(); }
        protected override void Dispose(bool disposing) { observer.DisposalEntered.TrySetResult(); if (disposing) original.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource lifetime = new();
        private readonly object gate = new(); private readonly List<(TcpClient Client, Task Original)> clients = [];
        private readonly Lazy<Task> close; private readonly Task accepting; private readonly Mode mode;
        internal readonly TaskCompletionSource HeldEntered = Gate(), PeerAborted = Gate(), HeadersRelease = Gate();
        internal readonly TaskCompletionSource SecondEntered = Gate(), SecondRelease = Gate(), SecondAborted = Gate();
        internal int RequestCount, InitializeCount, CallCount;
        internal Uri Address { get; }
        internal Endpoint(Mode mode)
        {
            this.mode = mode;
            try
            {
                listener.Start(8); Address = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/mcp");
                close = new(CloseCoreAsync); accepting = AcceptAsync();
            }
            catch { listener.Stop(); lifetime.Dispose(); throw; }
        }
        private async Task AcceptAsync()
        {
            try
            {
                for (var count = 0; count < 32; count++)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    lock (gate) clients.Add((client, ServeAsync(client)));
                }
                throw new IOException("Loopback fixture connection bound exceeded");
            }
            catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
                while (true)
                {
                    if (header.Count >= 8192) throw new IOException("Loopback header bound exceeded");
                    if (await stream.ReadAsync(one, lifetime.Token) == 0) throw new IOException("Incomplete loopback request");
                    header.Add(one[0]); if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n"); var get = lines[0].StartsWith("GET ", StringComparison.Ordinal);
                var contentLength = lines.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var length = contentLength is null ? 0 : int.Parse(contentLength.Split(':', 2)[1], System.Globalization.CultureInfo.InvariantCulture);
                if (length is < 0 or > 1_048_576) throw new IOException("Loopback request body bound exceeded");
                var body = new byte[length]; var offset = 0;
                while (offset < length) { var read = await stream.ReadAsync(body.AsMemory(offset), lifetime.Token); if (read == 0) throw new IOException("Incomplete loopback body"); offset += read; }
                Interlocked.Increment(ref RequestCount);
                using var json = length == 0 ? null : JsonDocument.Parse(body); var method = get ? "GET" : json!.RootElement.GetProperty("method").GetString();
                if (method == "initialize") Interlocked.Increment(ref InitializeCount);
                if (method == "tools/call") Interlocked.Increment(ref CallCount);
                if (mode == Mode.Headers && method == "ping" && json!.RootElement.TryGetProperty("id", out var peerId) && peerId.GetInt32() == 2)
                {
                    SecondEntered.TrySetResult(); var secondPeer = PeerAsync(stream, SecondAborted, SecondRelease.Task);
                    await Task.WhenAny(secondPeer, SecondRelease.Task);
                    Exception? replyFailure = null;
                    if (SecondRelease.Task.IsCompleted && !secondPeer.IsCompleted)
                    {
                        try { await Reply(stream, json.RootElement, method); }
                        catch (Exception error) { replyFailure = error; }
                        finally { client.Dispose(); }
                    }
                    Rethrow(replyFailure, await Observe(secondPeer)); return;
                }
                var held = method == "tools/call" && (mode is Mode.Headers or Mode.PostBody) || get && mode == Mode.GetBody || method == "initialize" && mode == Mode.LateInitialize;
                if (held)
                {
                    if (mode is Mode.PostBody or Mode.GetBody)
                        await Send(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n8\r\n: held\n\n\r\n");
                    HeldEntered.TrySetResult();
                    var peer = PeerAsync(stream);
                    if (mode == Mode.LateInitialize)
                    {
                        await Task.WhenAny(peer, HeadersRelease.Task);
                        if (HeadersRelease.Task.IsCompleted)
                        {
                            try { await Reply(stream, json!.RootElement, method); }
                            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
                            client.Dispose();
                        }
                    }
                    await peer; return;
                }
                if (get) await Send(stream, "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                else await Reply(stream, json!.RootElement, method);
            }
            catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            finally { client.Dispose(); }
        }
        private async Task PeerAsync(NetworkStream stream, TaskCompletionSource? aborted = null, Task? completedResponse = null)
        {
            aborted ??= PeerAborted;
            int read;
            try { read = await stream.ReadAsync(new byte[1], lifetime.Token); }
            catch (IOException) when (!lifetime.IsCancellationRequested) { aborted.TrySetResult(); return; }
            catch (ObjectDisposedException) when (completedResponse?.IsCompleted == true || mode == Mode.LateInitialize && HeadersRelease.Task.IsCompleted) { return; }
            catch (Exception error) when (lifetime.IsCancellationRequested && error is OperationCanceledException or IOException or ObjectDisposedException) { return; }
            if (read != 0) throw new IOException("Unexpected bytes after one loopback request");
            aborted.TrySetResult();
        }
        private Task Reply(NetworkStream stream, JsonElement request, string? method)
        {
            if (!request.TryGetProperty("id", out var id)) return Send(stream, "HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            var result = method switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"local\",\"version\":\"1\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"a\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"local\"}]}"
            };
            var body = "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}";
            return Send(stream, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
        }
        private async Task Send(NetworkStream stream, string text) { await stream.WriteAsync(Encoding.UTF8.GetBytes(text), lifetime.Token); await stream.FlushAsync(lifetime.Token); }
        internal Task CloseAsync() => close.Value;
        private async Task CloseCoreAsync()
        {
            var errors = new List<Exception>(); Task cancellation = Task.CompletedTask;
            try { cancellation = lifetime.CancelAsync(); } catch (Exception error) { errors.Add(error); }
            try { listener.Stop(); } catch (Exception error) { errors.Add(error); }
            (TcpClient Client, Task Original)[] owned; lock (gate) owned = clients.ToArray();
            foreach (var item in owned) try { item.Client.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { await accepting; } catch (Exception error) { errors.Add(error); }
            lock (gate) owned = clients.ToArray(); foreach (var item in owned) try { item.Client.Dispose(); } catch (Exception error) { errors.Add(error); }
            foreach (var item in owned) try { await item.Original; } catch (Exception error) { errors.Add(error); }
            try { await cancellation; } catch (Exception error) { errors.Add(error); }
            try { lifetime.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException(errors);
        }
        public ValueTask DisposeAsync() => new(CloseAsync());
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        internal readonly Endpoint Endpoint; internal readonly Observer Observer; private readonly HttpClient client;
        internal readonly ExtensionRegistry Extensions = new(); internal readonly Policy Policy = new();
        internal PersistentAgentSession Session = null!; internal ReplaceableAgentSession Owner = null!; internal McpPreparedServer Server = null!;
        private readonly SessionStorageBackend backend = new(Path.Combine(Path.GetTempPath(), "mcp-http-local-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
        private HostFixture(Mode mode)
        {
            Observer = new(Handler()); client = new(Observer, false) { Timeout = Timeout.InfiniteTimeSpan };
            try { Endpoint = new(mode); }
            catch { client.Dispose(); Observer.Dispose(); throw; }
        }
        internal static async Task<HostFixture> Create(Mode mode)
        {
            var fixture = new HostFixture(mode);
            try
            {
                var registry = new SessionRuntimeRegistry([new(Model, new Chat())], [], fixture.Policy, new() { BindNestedCallsToSessionOwner = true });
                var lifecycle = new PersistentSessionLifecycle(registry, () => 1, () => Guid.NewGuid().ToString("N"), backend: fixture.backend);
                var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "local", timestamp = "2026-10-05T00:00:00.000Z", cwd = fixture.backend.Directory }));
                fixture.Session = await lifecycle.CreateAsync(Path.Combine(fixture.backend.Directory, "local.jsonl"), header, Model); fixture.Owner = lifecycle.Attach(fixture.Session);
                var scope = await fixture.Extensions.ActivateAsync("local-http", new EmptyExtension()); var entry = Entry(fixture.Endpoint.Address);
                var binding = new McpHttpBinding(fixture.Endpoint.Address, ImmutableDictionary<string, string>.Empty, new(mode == Mode.GetBody, false));
                var options = new McpRuntimeOptions(1, "0.99.1");
                fixture.Server = new(entry, fixture.Extensions, scope, fixture.Owner, fixture.Policy, (tool, arguments, token) => ValueTask.FromResult(true),
                    AdmittedMcpHttpChannelFactory.Create(entry, binding, fixture.client, options), options, (current, prepared) => prepared.PreparedHooks ?? current.PreparedToolHooks);
                return fixture;
            }
            catch (Exception original)
            {
                try { await fixture.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception?>(); var endpointClose = Endpoint.CloseAsync();
            if (Owner is not null) errors.Add(await Observe(Owner.StopAdmissionAndJoinAsync()));
            if (Server is not null) errors.Add(await Observe(Server.CloseAsync()));
            if (Owner is not null) errors.Add(await Observe(Owner.DisposeAsync().AsTask()));
            else if (Session is not null) errors.Add(await Observe(Session.DisposeAsync().AsTask()));
            errors.Add(await Observe(Extensions.DisposeAsync().AsTask())); errors.Add(await Observe(endpointClose));
            try { client.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { Observer.Dispose(); } catch (Exception error) { errors.Add(error); }
            Rethrow(null, errors.ToArray());
        }
    }
    private static readonly ModelDescriptor Model = new("http-loopback", "openai-responses", "synthetic");
    private sealed class Policy : IToolActionPolicy
    {
        internal int Calls;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class EmptyExtension : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Chat : IChatTransport
    {
        private int requests;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            AssistantContent content = Interlocked.Increment(ref requests) == 1 ? new ToolCallContent("http-call", "mcp__demo__a", JsonData.EmptyObject) : new TextContent("done");
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 1, [content], TokenUsage.Zero, content is ToolCallContent ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (content is ToolCallContent call) { yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            await Task.CompletedTask; yield return new StreamDone(final.StopReason, final);
        }
    }
}
