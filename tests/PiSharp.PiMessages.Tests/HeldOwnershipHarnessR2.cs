using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

// Consumer-owned resource witnesses. The predecessor harness remains frozen and unused.
internal sealed class HeldOwnershipHarnessR2
{
    private readonly object _gate = new();
    private readonly List<object> _observations = [];
    internal readonly TaskCompletionSource Entered = NewGate(), Release = NewGate(), CancelledHookExited = NewGate();
    internal readonly TaskCompletionSource CleanupEntered = NewGate(), CleanupRelease = NewGate();
    internal readonly List<ValueObservation> Values = [];
    internal readonly List<RequestObservation> Requests = [];
    internal readonly List<FrameObservation> Publications = [], Deliveries = [];
    internal readonly List<OwnerState> Owners = [];
    internal readonly List<string> OwnershipFailures = [], EnvironmentLookups = [];
    internal int Sends, ProviderCallbacks, Reads, Effects, ProgressPublications, TerminalPublications, TerminalDeliveries;
    internal bool Unjoined, HoldCleanup;
    internal string? HookKind, HookMode, ReturnMode, ThrowMessage, CleanupFault, TerminalObserverThrow;
    internal JsonData? Replacement;
    internal int HeldProviderOrdinal;
    internal ModelDescriptor? Model;
    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed record ValueObservation(string Kind, int Turn, JsonData Value, ImmutableArray<string> OwnUndefinedPaths,
        string Id, string Api, string Provider, bool SameDescriptor);
    internal sealed record RequestObservation(int Turn, string? Uri, string Method, Dictionary<string, string[]> Headers,
        Dictionary<string, string[]> RequestHeaders, Dictionary<string, string[]> ContentHeaders, string BodyUtf8Base64, string RawBody,
        Dictionary<string, string[]> BeforeBodyReadHeaders);
    internal sealed record FrameObservation(JsonData Wire, JsonData? Snapshot);
    internal sealed class OwnerState(int turn)
    {
        internal readonly int Turn = turn;
        internal int Acquisitions, AsyncDisposalStarts, AsyncDisposalSettled, AsyncDisposalFaults, UniqueStreamDisposals;
        internal int ResponseDisposalStarts, ResponseDisposalSettled, ResponseContentDisposals;
        internal int RequestDisposalStarts, RequestDisposalSettled, OriginalRequestContentDisposals, EofReads;
        internal readonly List<int> ReadCounts = [];
        internal object Snapshot() => new { Turn, Acquisitions, AsyncDisposalStarts, AsyncDisposalSettled, AsyncDisposalFaults,
            UniqueStreamDisposals, ResponseDisposalStarts, ResponseDisposalSettled, ResponseContentDisposals,
            RequestDisposalStarts, RequestDisposalSettled, OriginalRequestContentDisposals, EofReads, readCounts = ReadCounts.ToArray() };
    }
    internal void Record(string kind, object? value) { lock (_gate) _observations.Add(new { kind, value }); }
    internal object Observations()
    {
        lock (_gate) return new { records = _observations.ToArray(), values = Values.ToArray(), requests = Requests.ToArray(),
            publications = Publications.ToArray(), deliveries = Deliveries.ToArray(), owners = Owners.Select(o => o.Snapshot()).ToArray(),
            ownershipFailures = OwnershipFailures.ToArray(), environmentLookups = EnvironmentLookups.ToArray(), Sends, ProviderCallbacks,
            Reads, Effects, TerminalPublications, TerminalDeliveries, Unjoined };
    }
    internal string? EnvironmentLookup(string name)
    {
        lock (_gate) EnvironmentLookups.Add(name);
        Record("injected-inert-environment-lookup", name); return null;
    }
    private async ValueTask Hold(string kind, CancellationToken token)
    {
        if (HookKind != kind || HookMode != "held") return;
        Record("held-enter", kind); Entered.TrySetResult();
        try { await Release.Task.WaitAsync(token); }
        finally
        {
            Record("held-exit", new { kind, released = Release.Task.IsCompleted, token.IsCancellationRequested });
            if (token.IsCancellationRequested && !Release.Task.IsCompleted) CancelledHookExited.TrySetResult();
        }
    }
    private void Value<T>(string kind, T value, ModelDescriptor model)
    {
        var type = typeof(T); var raw = (JsonData)type.GetProperty("Value")!.GetValue(value)!;
        var undefined = (ImmutableArray<string>)type.GetProperty("OwnUndefinedPaths")!.GetValue(value)!;
        var turn = kind == "payload" ? Sends : Math.Max(0, Sends - 1);
        var observation = new ValueObservation(kind, turn, raw, undefined, model.Id, model.Api, model.Provider, ReferenceEquals(model, Model));
        lock (_gate) Values.Add(observation); Record("actual-" + kind, observation);
    }
    internal Func<T, ModelDescriptor, CancellationToken, ValueTask<JsonData?>> Payload<T>() => async (value, model, token) =>
    {
        Value("payload", value, model); await Hold("payload", token);
        if (HookKind == "payload" && ThrowMessage is not null) throw new InvalidOperationException(ThrowMessage);
        return ReturnMode == "null" ? JsonData.Null : ReturnMode == "replacement" ? Replacement : null;
    };
    internal Func<T, ModelDescriptor, CancellationToken, ValueTask> Response<T>() => async (value, model, token) =>
    { Value("response", value, model); await Hold("response", token); };
    internal Func<T, ModelDescriptor, CancellationToken, ValueTask> Provider<T>() => async (value, model, token) =>
    {
        Value("provider", value, model); var ordinal = Interlocked.Increment(ref ProviderCallbacks) - 1;
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
        hooks.GetType().GetProperty("OnEventPublished")!.SetValue(hooks, (Action<StreamEvent>)Published);
    }
    private static FrameObservation Frame(StreamEvent frame) => new(PiWireJson.WriteEvent(frame), frame.SourceEmissionSnapshot);
    private void Published(StreamEvent frame)
    {
        var observation = Frame(frame); lock (_gate) Publications.Add(observation);
        Record("actual-publication-attempt", observation);
        if (frame is not StreamTerminalEvent) { Interlocked.Increment(ref ProgressPublications); return; }
        Interlocked.Increment(ref TerminalPublications);
        AssertOwnership("terminal-publication");
        if (TerminalObserverThrow is not null) throw new InvalidOperationException(TerminalObserverThrow);
    }
    internal void Delivered(StreamEvent frame)
    {
        var observation = Frame(frame); lock (_gate) Deliveries.Add(observation); Record("actual-delivery", observation);
        if (frame is not StreamTerminalEvent) return;
        Interlocked.Increment(ref TerminalDeliveries);
        AssertOwnership("terminal-delivery");
    }
    internal void AssertOwnership(string boundary)
    {
        foreach (var owner in Owners)
        {
            var complete = owner.AsyncDisposalStarts == owner.Acquisitions && owner.AsyncDisposalSettled == owner.Acquisitions &&
                owner.UniqueStreamDisposals == 1 && owner.ResponseDisposalStarts == 1 && owner.ResponseDisposalSettled == 1 &&
                owner.ResponseContentDisposals == 1 && owner.RequestDisposalStarts == 1 && owner.RequestDisposalSettled == 1 &&
                owner.OriginalRequestContentDisposals == 1;
            Record("ownership-at-" + boundary, owner.Snapshot());
            if (complete) continue;
            var failure = $"Unsettled or repeated owned release at {boundary}, turn {owner.Turn}.";
            lock (_gate) OwnershipFailures.Add(failure);
            throw new InvalidOperationException(failure);
        }
    }
    internal void ToolEffect(ToolCallContent call)
    {
        AssertOwnership("tool-effect");
        if (TerminalDeliveries != 1 || call.Arguments.Value.ValueKind != JsonValueKind.Object ||
            call.Arguments.Value.GetProperty("value").GetInt32() != 7 || Interlocked.Increment(ref Effects) != 1)
            throw new InvalidOperationException("Tool effect lacks exactly one finalized native assistant barrier.");
        Record("actual-native-tool-effect", new { call.Id, call.Name, arguments = call.Arguments.ToString(), Effects, TerminalDeliveries });
    }
    internal HttpClient Client(byte[][] bodies, JsonElement input, int[] readPlan) => new(new Handler(this, bodies, input.Clone(), readPlan));
    private sealed class Handler(HeldOwnershipHarnessR2 owner, byte[][] bodies, JsonElement input, int[] readPlan) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var turn = Interlocked.Increment(ref owner.Sends) - 1;
            if (turn >= bodies.Length) throw new InvalidOperationException("Unexpected additional HTTP attempt.");
            var originalContent = request.Content ?? throw new InvalidOperationException("Physical request has no content.");
            var beforeBodyReadHeaders = request.Headers.Concat(originalContent.Headers)
                .ToDictionary(field => field.Key.ToLowerInvariant(), field => field.Value.ToArray(), StringComparer.Ordinal);
            var bytes = await originalContent.ReadAsByteArrayAsync(token);
            var headers = request.Headers.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value.ToArray(), StringComparer.Ordinal);
            var contentHeaders = originalContent.Headers.ToDictionary(x => x.Key.ToLowerInvariant(), x => x.Value.ToArray(), StringComparer.Ordinal);
            owner.Record("physical-header-rows", new { request = request.Headers.Select(field => new { field.Key, values = field.Value.ToArray() }).ToArray(),
                content = originalContent.Headers.Select(field => new { field.Key, values = field.Value.ToArray() }).ToArray() });
            var allHeaders = headers.Concat(contentHeaders).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            owner.Record("physical-header-observation-layers", new { beforeBodyReadHeaders, afterBodyReadHeaders = allHeaders,
                materializedUtf8ByteLength = bytes.Length });
            var actual = new RequestObservation(turn, request.RequestUri?.AbsoluteUri, request.Method.Method, allHeaders, headers, contentHeaders,
                Convert.ToBase64String(bytes), Encoding.UTF8.GetString(bytes), beforeBodyReadHeaders);
            lock (owner._gate) owner.Requests.Add(actual); owner.Record("actual-physical-request", actual);
            var state = new OwnerState(turn); lock (owner._gate) owner.Owners.Add(state);
            // Transfer original content into a wrapper that witnesses request.Dispose.
            request.Content = new TrackedRequestContent(owner, state, originalContent);
            var response = new TrackedResponse(owner, state, (HttpStatusCode)input.GetProperty("httpStatus").GetInt32())
            { Content = new OwnedContent(owner, state, new ControlledStream(owner, state, bodies[turn], readPlan)) };
            if (input.TryGetProperty("statusText", out var phrase)) response.ReasonPhrase = phrase.GetString();
            foreach (var field in input.GetProperty("httpHeaders").EnumerateObject())
                if (!response.Headers.TryAddWithoutValidation(field.Name, field.Value.GetString()))
                    response.Content.Headers.TryAddWithoutValidation(field.Name, field.Value.GetString());
            return response;
        }
    }
    private sealed class TrackedRequestContent : HttpContent
    {
        private readonly HeldOwnershipHarnessR2 _owner; private readonly OwnerState _state; private readonly HttpContent _original;
        internal TrackedRequestContent(HeldOwnershipHarnessR2 owner, OwnerState state, HttpContent original)
        {
            _owner = owner; _state = state; _original = original;
            foreach (var field in original.Headers) Headers.TryAddWithoutValidation(field.Key, field.Value);
        }
        protected override bool TryComputeLength(out long length) { length = _original.Headers.ContentLength ?? 0; return _original.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _original.CopyToAsync(stream);
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            Interlocked.Increment(ref _state.RequestDisposalStarts);
            try
            {
                _original.Dispose(); Interlocked.Increment(ref _state.OriginalRequestContentDisposals); base.Dispose(true);
                if (_owner.CleanupFault == "request") throw new InvalidOperationException("Authored request disposal failure");
            }
            finally { Interlocked.Increment(ref _state.RequestDisposalSettled); _owner.Record("request-disposal-settled", _state.Snapshot()); }
        }
    }
    private sealed class TrackedResponse(HeldOwnershipHarnessR2 owner, OwnerState state, HttpStatusCode status) : HttpResponseMessage(status)
    {
        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            Interlocked.Increment(ref state.ResponseDisposalStarts);
            try { base.Dispose(true); if (owner.CleanupFault == "response") throw new InvalidOperationException("Authored response disposal failure"); }
            finally { Interlocked.Increment(ref state.ResponseDisposalSettled); owner.Record("response-disposal-settled", state.Snapshot()); }
        }
    }
    private sealed class OwnedContent(HeldOwnershipHarnessR2 owner, OwnerState state, ControlledStream stream) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => throw new InvalidOperationException("Use response stream acquisition.");
        private Task<Stream> Acquire() { Interlocked.Increment(ref state.Acquisitions); owner.Record("reader-acquired", state.Turn); return Task.FromResult<Stream>(stream); }
        protected override Task<Stream> CreateContentReadStreamAsync() => Acquire();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Acquire(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref state.ResponseContentDisposals);
            try { base.Dispose(disposing); } finally { if (disposing) stream.Dispose(); }
        }
    }
    private sealed class ControlledStream(HeldOwnershipHarnessR2 owner, OwnerState state, byte[] bytes, int[] readPlan) : MemoryStream(bytes, writable: false)
    {
        private int _disposed, _planIndex, _remaining = readPlan.Length == 0 ? 1 : readPlan[0];
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Interlocked.Increment(ref owner.Reads);
            var count = await base.ReadAsync(buffer[..Math.Min(buffer.Length, _remaining)], token);
            lock (owner._gate) state.ReadCounts.Add(count);
            if (count == 0) Interlocked.Increment(ref state.EofReads);
            if (readPlan.Length > 0 && count > 0)
            {
                _remaining -= count;
                if (_remaining == 0) { _planIndex++; _remaining = _planIndex < readPlan.Length ? readPlan[_planIndex] : int.MaxValue; }
            }
            return count;
        }
        public override async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref state.AsyncDisposalStarts); owner.Record("reader-async-disposal-enter", state.Snapshot());
            try
            {
                if (owner.HoldCleanup && state.Turn == 0)
                {
                    owner.CleanupEntered.TrySetResult(); await owner.CleanupRelease.Task;
                    owner.Record("reader-async-disposal-explicit-release", state.Turn);
                }
                Dispose();
                if (owner.CleanupFault == "reader") throw new InvalidOperationException("Authored reader disposal failure");
            }
            catch { Interlocked.Increment(ref state.AsyncDisposalFaults); throw; }
            finally { Interlocked.Increment(ref state.AsyncDisposalSettled); owner.Record("reader-async-disposal-settled", state.Snapshot()); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Increment(ref state.UniqueStreamDisposals);
            base.Dispose(disposing);
        }
    }
}
