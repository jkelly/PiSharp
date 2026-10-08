using System.Net;
using System.Reflection;
using System.Text;
using PiSharp.Contracts;

// Source-only consumer support. Requires a separately allocated native runtime to execute.
internal sealed class HeldOwnershipHarness
{
    private readonly object _gate = new();
    private readonly List<object> _observations = [];
    internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Sends, ProviderCallbacks, Published, Reads, Disposals;
    internal bool Unjoined;
    internal string? HookKind, HookMode, ReturnMode, ThrowMessage;
    internal JsonData? Replacement;
    internal int HeldProviderOrdinal = 0;
    internal ModelDescriptor? Model;
    internal void Record(string kind, object? value) { lock (_gate) _observations.Add(new { kind, value }); }
    internal object[] Observations() { lock (_gate) return _observations.ToArray(); }
    private async ValueTask Hold(string kind, CancellationToken token)
    {
        if (HookKind != kind || HookMode != "held") return;
        Record("held-enter", kind); Entered.TrySetResult();
        try { await Release.Task.WaitAsync(token); }
        finally { Record("held-exit", new { kind, released = Release.Task.IsCompleted, token.IsCancellationRequested }); }
    }
    private void Value<T>(string kind, T value, ModelDescriptor model)
    {
        var type = typeof(T); var raw = (JsonData)type.GetProperty("Value")!.GetValue(value)!;
        Record(kind, new { rawJson = raw.ToString(), ownUndefinedPaths = type.GetProperty("OwnUndefinedPaths")!.GetValue(value),
            model.Id, model.Api, model.Provider, sameDescriptor = ReferenceEquals(model, Model) });
    }
    internal Func<T, ModelDescriptor, CancellationToken, ValueTask<JsonData?>> Payload<T>() => async (value, model, token) =>
    {
        Value("actual-payload", value, model); await Hold("payload", token);
        if (HookKind == "payload" && ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
        return ReturnMode == "null" ? JsonData.Parse("null") : ReturnMode == "replacement" ? Replacement : null;
    };
    internal Func<T, ModelDescriptor, CancellationToken, ValueTask> Response<T>() => async (value, model, token) =>
    { Value("actual-response", value, model); await Hold("response", token); };
    internal Func<T, ModelDescriptor, CancellationToken, ValueTask> Provider<T>() => async (value, model, token) =>
    {
        Value("actual-provider", value, model); var ordinal = Interlocked.Increment(ref ProviderCallbacks) - 1;
        if (ordinal == HeldProviderOrdinal) await Hold("provider", token);
    };
    internal void BindHooks(object hooks)
    {
        foreach (var pair in new[] { ("OnPayload", nameof(Payload)), ("OnResponse", nameof(Response)), ("OnProviderStreamEvent", nameof(Provider)) })
        {
            var property = hooks.GetType().GetProperty(pair.Item1)!;
            var valueType = property.PropertyType.GenericTypeArguments[0];
            var factory = GetType().GetMethod(pair.Item2, BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(valueType);
            property.SetValue(hooks, factory.Invoke(this, null));
        }
        hooks.GetType().GetProperty("OnEventPublished")!.SetValue(hooks, (Action<StreamEvent>)(frame =>
        { Interlocked.Increment(ref Published); Record("actual-publication", new { wire = PiWireJson.WriteEvent(frame).ToString(), source = frame.SourceEmissionSnapshot?.ToString() }); }));
    }
    internal HttpClient Client(byte[][] bodies, int status, string? statusText, int readSize)
        => new(new Handler(this, bodies, status, statusText, readSize));
    private sealed class Handler(HeldOwnershipHarness owner, byte[][] bodies, int status, string? statusText, int readSize) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var turn = Interlocked.Increment(ref owner.Sends) - 1;
            if (turn >= bodies.Length) throw new InvalidOperationException("Unexpected additional HTTP attempt.");
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            owner.Record("actual-request", new { turn, uri = request.RequestUri?.ToString(), method = request.Method.Method,
                headers = request.Headers.Select(x => new { x.Key, values = x.Value.ToArray() }).ToArray(),
                contentHeaders = request.Content.Headers.Select(x => new { x.Key, values = x.Value.ToArray() }).ToArray(),
                bodyUtf8Base64 = Convert.ToBase64String(bytes), rawBody = Encoding.UTF8.GetString(bytes) });
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new OwnedContent(new OwnedStream(owner, bodies[turn], readSize)) };
            if (statusText is not null) response.ReasonPhrase = statusText;
            response.Content.Headers.TryAddWithoutValidation("content-type", "text/event-stream");
            return response;
        }
    }
    private sealed class OwnedContent(OwnedStream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new InvalidOperationException("Use actual response stream acquisition.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(stream);
    }
    private sealed class OwnedStream(HeldOwnershipHarness owner, byte[] bytes, int readSize) : MemoryStream(bytes, writable: false)
    {
        private int _disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Interlocked.Increment(ref owner.Reads); return base.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], token); }
        public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        protected override void Dispose(bool disposing)
        { if (Interlocked.Exchange(ref _disposed, 1) == 0) { Interlocked.Increment(ref owner.Disposals); owner.Record("actual-stream-disposed", true); } base.Dispose(disposing); }
    }
}
