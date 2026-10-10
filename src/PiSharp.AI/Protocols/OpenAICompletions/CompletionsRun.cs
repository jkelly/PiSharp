using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>One Completions invocation. Source semantics and joined canonical ownership are separate promises.</summary>
public sealed class CompletionsRun : IAsyncDisposable
{
    private readonly Channel<Envelope> _events;
    private readonly CancellationTokenSource _work;
    private readonly CompletionsProductionContext _context = new();
    private readonly AsyncLocal<bool> _insideProducer = new();
    private readonly CompletionsHttpSseOptions _options;
    private readonly TaskCompletionSource<CompletionsCleanupOutcome> _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ChatResult> _canonical = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _dispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _detach = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _drain, _owner;
    private readonly CompletionsSourceMessage _message;
    private readonly AssistantMessage _fallback;
    private readonly SourceReader _sourceReader;
    private readonly object _emissionGate = new();
    private readonly List<CompletionsSourceSnapshot> _emissions = [];
    private readonly List<CompletionsSourcePublication> _publications = [];
    private readonly bool _retainEmissions;
    private readonly bool _awaitCanonicalDelivery;
    private long _emissionCharacters;
    private long _publicationSequence;
    private bool _publicationObserverFailed;
    private bool _captureCanonicalObservation;
    private StreamTerminalEvent? _semanticTerminal;
    private CompletionsSourceEvent? _sourceTerminal;
    private int _claimed, _disposed;
    private ChatFailure? _cancellationFailure;

    internal CompletionsRun(OpenAICompletionsWireSource mapper, ChatRequest request, CompletionsHttpSseOptions options,
        CancellationToken token, bool sourceView, bool awaitCanonicalDelivery, CompletionsStartupHandoff? startup = null)
    {
        ArgumentNullException.ThrowIfNull(mapper); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Model);
        ArgumentNullException.ThrowIfNull(options);
        if (request.Model.Api != "openai-completions" || string.IsNullOrWhiteSpace(request.Model.Provider) || string.IsNullOrWhiteSpace(request.Model.Id))
            throw new ArgumentException("Invalid Completions model.", nameof(request));
        CompletionsJson.Unicode(request.Model.Api); CompletionsJson.Unicode(request.Model.Provider); CompletionsJson.Unicode(request.Model.Id);
        _options = options;
        _context.Startup = startup;
        _retainEmissions = sourceView;
        _awaitCanonicalDelivery = awaitCanonicalDelivery;
        _fallback = new(request.Model.Api, request.Model.Provider, request.Model.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending);
        // Pure initial admission precedes any linked CTS, reader resource, worker or HTTP effect.
        var admittedInitial = Snapshot(JsonData.Parse("{\"value\":" + PiWireJson.WriteMessage(_fallback) + ",\"ownUndefinedPaths\":[]}"));
        _ = new CompletionsSourceSnapshot(CompletionsSourceEventProjection.Capture(new StreamStarted(_fallback), _fallback, []),
            options.MaximumSourceValueCharacters, options.MaximumSourceValueBytes);
        _events = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(options.SourceEventCapacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = sourceView });
        _work = CancellationTokenSource.CreateLinkedTokenSource(token);
        _message = new(admittedInitial);
        _sourceReader = new(this);
        _drain = DrainReturnedAsync();
        SourceResult = ProduceAsync(mapper, request, sourceView);
        _owner = JoinOwnerAsync();
    }

    /// <summary>The actual semantic producer task. This promise alone grants no tool/durable authority.</summary>
    public Task<CompletionsSourceMessage> SourceResult { get; }
    public Task<CompletionsCleanupOutcome> CleanupCompletion => _cleanup.Task;
    public Task<ChatResult> CanonicalCompletion => _canonical.Task;
    public bool SourceTerminalPublished => Volatile.Read(ref _sourceTerminal) is not null;
    public bool IsCancellationRequested => _work.IsCancellationRequested;
    public ImmutableArray<CompletionsSourceSnapshot> SourceEmissions
    { get { lock (_emissionGate) return _emissions.ToImmutableArray(); } }
    public ImmutableArray<CompletionsSourcePublication> SourcePublications
    { get { lock (_emissionGate) return _publications.ToImmutableArray(); } }

    public IAsyncEnumerable<CompletionsSourceEvent> ReadSourceEventsAsync(CancellationToken token = default)
    { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return _sourceReader.WithToken(token); }

    public ValueTask<CompletionsIteratorResult> NextAsync(CancellationToken token = default)
    { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return _sourceReader.NextAsync(token); }

    /// <summary>Closes the outer consumer and transfers its bounded queue to one owned discard drain.</summary>
    public ValueTask<CompletionsIteratorResult> ReturnAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CompletionsRun));
        return _sourceReader.ReturnAsync();
    }

    internal async IAsyncEnumerable<StreamEvent> ReadCanonicalEventsAsync([EnumeratorCancellation] CancellationToken token)
    {
        Claim();
        try
        {
            await foreach (var envelope in _events.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (envelope.FinalBatch)
                {
                    // CloseSourceAsync starts this actual owner task before admitting the
                    // final batch. Waiting for SourceResult here would deadlock a full queue.
                    var succeeded = true;
                    try { await _context.Cleanup.ConfigureAwait(false); }
                    catch (Exception) { succeeded = false; }
                    if (!succeeded || Volatile.Read(ref _cancellationFailure) is not null)
                    { envelope.Advance?.TrySetResult(); continue; }
                }
                yield return envelope.Canonical;
                // The next actual canonical advance permits the same producer to resume.
                // Closing at any frame cancels its outstanding owned-token handoff.
                envelope.Advance?.TrySetResult();
            }
            _detach.TrySetResult(false); // This reader has consumed every admitted channel envelope.
            await CanonicalCompletion.ConfigureAwait(false);
            yield return _semanticTerminal!;
        }
        finally
        {
            if (!CanonicalCompletion.IsCompleted) { Cancel(); _detach.TrySetResult(true); }
        }
    }

    private async Task<CompletionsSourceMessage> ProduceAsync(OpenAICompletionsWireSource mapper, ChatRequest request, bool sourceView)
    {
        _insideProducer.Value = true;
        try
        {
            await foreach (var frame in mapper.StreamOwnedAsync(request, _context, _work.Token, sourceView).ConfigureAwait(false))
            {
                _captureCanonicalObservation |= frame.SourceEmissionSnapshot is not null;
                PublishMessage(frame);
                var source = frame is ToolCallHeaderUpdated ? null : SourceFrame(frame);
                if (frame is StreamTerminalEvent terminal)
                { _semanticTerminal = ObserveTerminal(terminal, source); Volatile.Write(ref _sourceTerminal, source); break; }
                var envelope = new Envelope(frame, source, _context.FinalBatch,
                    _awaitCanonicalDelivery ? new() : null);
                try
                {
                    await _events.Writer.WriteAsync(envelope, _work.Token).ConfigureAwait(false);
                    // Public source consumption acknowledges bounded enqueue; canonical
                    // consumption also acknowledges each actual immutable frame delivery.
                    if (envelope.Advance is { } advance)
                        await advance.Task.WaitAsync(_work.Token).ConfigureAwait(false);
                }
                finally { envelope.Advance?.TrySetCanceled(_work.Token); }
            }
        }
        catch (Exception error)
        {
            _context.Admit(error);
            var aborted = _work.IsCancellationRequested;
            var native = (_context.Current ?? _fallback) with
            { StopReason = aborted ? StopReason.Aborted : StopReason.Error };
            _semanticTerminal = new StreamError(native.StopReason, native with { ExtraProperties = (native.ExtraProperties ?? JsonFields.Empty)
                .Set("errorMessage", JsonData.Parse(aborted ? "\"Completions stream was cancelled.\"" : "\"Completions stream did not complete.\"")) })
            { NativeDiagnostic = OpenAICompletionsWireSource.Diagnostic(aborted ? OpenAICompletionsWireFailure.Cancelled :
                error is StreamLimitException ? OpenAICompletionsWireFailure.ResourceLimit : OpenAICompletionsWireFailure.SourceFailed) };
            PublishMessage(_semanticTerminal);
            var source = SourceFrame(_semanticTerminal);
            _semanticTerminal = ObserveTerminal(_semanticTerminal, source);
            Volatile.Write(ref _sourceTerminal, source);
        }
        finally
        {
            _events.Writer.TryComplete(); _insideProducer.Value = false;
        }
        return _message;
    }

    private async Task JoinOwnerAsync()
    {
        Exception? cleanupFailure = null;
        try { await SourceResult.ConfigureAwait(false); }
        catch (Exception)
        {
            _semanticTerminal ??= new StreamError(StopReason.Error, _fallback with { StopReason = StopReason.Error,
                ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"Completions source value admission failed.\"")) })
            { NativeDiagnostic = OpenAICompletionsWireSource.Diagnostic(OpenAICompletionsWireFailure.SourceFailed) };
        }
        try { await _context.Cleanup.ConfigureAwait(false); } catch (Exception error) { cleanupFailure ??= error; }
        var failure = cleanupFailure is null ? Volatile.Read(ref _cancellationFailure) :
            new ChatFailure(ChatFailureKind.Provider, "Completions owned cleanup failed.")
            { NativeDiagnostic = OpenAICompletionsWireSource.Diagnostic(OpenAICompletionsWireFailure.CleanupFailed) };
        var outcome = new CompletionsCleanupOutcome(failure is null, failure);
        if (_events.Reader.Count == 0) _detach.TrySetResult(false);
        await _drain.ConfigureAwait(false);
        _cleanup.TrySetResult(outcome);
        var terminal = _semanticTerminal!;
        if (failure?.NativeDiagnostic is { } nativeCleanup)
            terminal = terminal with { NativeCleanupDiagnostic = nativeCleanup };
        var semanticError = terminal is StreamError { Reason: StopReason.Error };
        if (failure is not null && terminal is StreamDone)
        {
            var message = terminal.Message with { StopReason = StopReason.Error,
                ExtraProperties = (terminal.Message.ExtraProperties ?? JsonFields.Empty)
                    .Set("errorMessage", JsonData.Parse("\"Completions stream did not complete.\"")) };
            terminal = new StreamError(StopReason.Error, message)
            { NativeDiagnostic = OpenAICompletionsWireSource.Diagnostic(OpenAICompletionsWireFailure.SourceFailed),
                NativeCleanupDiagnostic = terminal.NativeCleanupDiagnostic };
        }
        if (semanticError) terminal = ProjectAdmittedCanonicalFailure(terminal);
        var chatFailure = terminal is StreamError ? new ChatFailure(terminal.Reason == StopReason.Aborted ? ChatFailureKind.Cancelled : ChatFailureKind.Provider,
            terminal.Reason == StopReason.Aborted ? "Completions stream was cancelled." : "Completions stream did not complete.")
            { NativeDiagnostic = terminal.NativeDiagnostic } : null;
        _semanticTerminal = terminal; _canonical.TrySetResult(new(terminal.Message, chatFailure)
        { NativeDiagnostic = terminal.NativeDiagnostic, NativeCleanupDiagnostic = terminal.NativeCleanupDiagnostic });
    }

    private StreamTerminalEvent ProjectAdmittedCanonicalFailure(StreamTerminalEvent terminal)
    {
        if (!SourceResult.IsCompletedSuccessfully || _context.PublicFailure is not { } admitted) return terminal;
        var projected = terminal with { Message = terminal.Message with
        { ExtraProperties = (terminal.Message.ExtraProperties ?? JsonFields.Empty)
            .Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(admitted.Message))) } };
        try
        {
            // Projection is data admission, never a new terminal or a larger configured view.
            _ = Snapshot(JsonData.Parse("{\"value\":" + PiWireJson.WriteMessage(projected.Message) + ",\"ownUndefinedPaths\":[]}"));
            return projected;
        }
        catch (Exception error) when (error is StreamLimitException or JsonException) { return terminal; }
    }

    private void PublishMessage(StreamEvent frame)
    {
        var captured = _context.Capture(frame).Value;
        var member = frame is StreamTerminalEvent ? frame is StreamDone ? "message" : "error" : "partial";
        var message = PiSharp.Contracts.JsonUtf16.MutableNode(captured.GetProperty("value").GetProperty(member))!.AsObject();
        if (frame is StreamError)
        {
            message["errorMessage"] = frame is StreamError { Reason: StopReason.Aborted } ? "Request was aborted" :
                _context.PublicFailure?.Message ?? "Completions operation failed.";
        }
        var paths = captured.GetProperty("ownUndefinedPaths").EnumerateArray().Select(path => path.GetString()!)
            .Where(path => path.StartsWith("/" + member + "/", StringComparison.Ordinal)).Select(path => path[(member.Length + 1)..]);
        _message.Update(Snapshot(JsonData.Parse("{\"value\":" + PiSharp.Contracts.JsonUtf16.ToJsonString(message) + ",\"ownUndefinedPaths\":" + JsonSerializer.Serialize(paths) + "}")));
    }

    private CompletionsSourceEvent SourceFrame(StreamEvent frame)
    {
        var emission = frame.SourceEmissionSnapshot ?? _context.Capture(frame);
        if (frame is StreamTerminalEvent)
        {
            var member = frame is StreamDone ? "message" : "error";
            var value = PiSharp.Contracts.JsonUtf16.MutableNode(emission.Value.GetProperty("value"))!.AsObject();
            value[member] = PiSharp.Contracts.JsonUtf16.MutableNode(_message.Snapshot.Raw.Value.GetProperty("value"));
            var paths = _message.Snapshot.OwnUndefinedPaths.Select(path => "/" + member + path);
            emission = JsonData.Parse("{\"value\":" + PiSharp.Contracts.JsonUtf16.ToJsonString(value) + ",\"ownUndefinedPaths\":" + JsonSerializer.Serialize(paths) + "}");
        }
        var result = new CompletionsSourceEvent(emission, _message, _options.MaximumSourceValueCharacters, _options.MaximumSourceValueBytes);
        var publication = new CompletionsSourcePublication(++_publicationSequence, Stopwatch.GetTimestamp(), result.Emission);
        if (_retainEmissions)
        {
            lock (_emissionGate)
            {
                var size = result.Emission.Raw.ToString().Length + (long)result.Emission.SerializedJson.Length;
                if (_emissions.Count >= 4096 || size > 4_194_304 - _emissionCharacters)
                    throw new StreamLimitException("Completions source emission ledger exceeds its limits.");
                _emissionCharacters += size; _emissions.Add(result.Emission); _publications.Add(publication);
            }
        }
        if (!_publicationObserverFailed && _options.Hooks?.OnSourcePublished is { } observer)
        {
            try { observer(publication); }
            catch (Exception) { _publicationObserverFailed = true; throw; }
        }
        return result;
    }

    private StreamTerminalEvent ObserveTerminal(StreamTerminalEvent terminal, CompletionsSourceEvent? source) =>
        _captureCanonicalObservation && source is not null ? terminal with
        { SourceEmissionSnapshot = source.Emission.Raw, SourceDrainSnapshot = source.Emission.Raw } : terminal;

    private CompletionsSourceSnapshot Snapshot(JsonData value) => new(value, _options.MaximumSourceValueCharacters, _options.MaximumSourceValueBytes);
    private void Claim()
    { if (Interlocked.Exchange(ref _claimed, 1) != 0) throw new InvalidOperationException("A Completions run has one event reader."); }
    private async Task DrainReturnedAsync()
    { if (await _detach.Task.ConfigureAwait(false)) await foreach (var _ in _events.Reader.ReadAllAsync().ConfigureAwait(false)) { } }
    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        RequestCancellation();
    }
    private void RequestCancellation()
    {
        try { _work.Cancel(); }
        catch (AggregateException) { Interlocked.CompareExchange(ref _cancellationFailure,
            new(ChatFailureKind.Provider, "Completions cancellation callback failed.")
            { NativeDiagnostic = OpenAICompletionsWireSource.Diagnostic(OpenAICompletionsWireFailure.CleanupFailed) }, null); }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0) { }
    }
    public ValueTask DisposeAsync()
    {
        if (_insideProducer.Value) throw new InvalidOperationException("A Completions producer cannot join its own run disposal.");
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = DisposeCoreAsync(); return new(_dispose.Task);
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            RequestCancellation();
            try
            {
                try { await _sourceReader.CloseIfClaimedAsync().ConfigureAwait(false); }
                finally
                {
                    if (Volatile.Read(ref _claimed) == 0) { Claim(); _detach.TrySetResult(true); }
                    await _owner.ConfigureAwait(false);
                }
            }
            finally { _sourceReader.DisposeResource(); _work.Dispose(); }
            _dispose.TrySetResult();
        }
        catch (Exception) { _dispose.TrySetException(new StreamProtocolException("Completions run disposal failed.")); }
    }
    private sealed record Envelope(StreamEvent Canonical, CompletionsSourceEvent? Source, bool FinalBatch,
        TaskCompletionSource? Advance);

    private sealed class SourceReader(CompletionsRun run) : IAsyncEnumerable<CompletionsSourceEvent>, IAsyncEnumerator<CompletionsSourceEvent>
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _return = new();
        private CancellationToken _token;
        private TaskCompletionSource<bool>? _reading;
        private Task<CompletionsIteratorResult>? _nextOperation;
        private Task<CompletionsIteratorResult>? _returning;
        private bool _claimed, _returned, _terminalRead;
        public CompletionsSourceEvent Current { get; private set; } = null!;
        public IAsyncEnumerable<CompletionsSourceEvent> WithToken(CancellationToken token)
        { lock (_gate) { if (_claimed) throw new InvalidOperationException("A Completions run has one event reader."); _token = token; } return this; }
        public IAsyncEnumerator<CompletionsSourceEvent> GetAsyncEnumerator(CancellationToken token = default)
        {
            lock (_gate)
            {
                if (_claimed) throw new InvalidOperationException("A Completions run has one event reader.");
                if (token.CanBeCanceled && _token.CanBeCanceled && token != _token)
                    throw new ArgumentException("Supply one source reader cancellation token.", nameof(token));
                if (token.CanBeCanceled) _token = token;
                run.Claim(); _claimed = true; return this;
            }
        }
        public ValueTask<bool> MoveNextAsync()
        {
            TaskCompletionSource<bool> settlement;
            lock (_gate)
            {
                if (_returned || _terminalRead) return ValueTask.FromResult(false);
                if (_reading is not null) throw new InvalidOperationException("A source event reader has one active read.");
                _reading = settlement = new();
            }
            _ = ReadCoreAsync(settlement); return new(settlement.Task);
        }
        public ValueTask<CompletionsIteratorResult> NextAsync(CancellationToken token)
        {
            TaskCompletionSource<CompletionsIteratorResult> settlement;
            lock (_gate)
            {
                if (_nextOperation is not null) throw new InvalidOperationException("A source event reader has one active next operation.");
                if (!_claimed)
                { run.Claim(); _claimed = true; _token = token; }
                else if (token != _token) throw new ArgumentException("A source reader retains its initial cancellation token.", nameof(token));
                settlement = new(); _nextOperation = settlement.Task;
            }
            _ = NextCoreAsync(settlement); return new(settlement.Task);
        }
        private async Task NextCoreAsync(TaskCompletionSource<CompletionsIteratorResult> settlement)
        {
            CompletionsIteratorResult? result = null; Exception? failure = null;
            try { result = new(await MoveNextAsync().ConfigureAwait(false) ? Current : null); }
            catch (Exception error) { failure = error; }
            // Release admission before settling: a waiting consumer may immediately ask
            // for its next event, return, cancel or dispose from its actual continuation.
            lock (_gate) _nextOperation = null;
            if (failure is null) settlement.TrySetResult(result!); else settlement.TrySetException(failure);
        }
        private async Task ReadCoreAsync(TaskCompletionSource<bool> settlement)
        {
            var moved = false; Exception? failure = null;
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, _return.Token);
                using var registration = _token.Register(run.Cancel);
                while (await run._events.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    _token.ThrowIfCancellationRequested();
                    if (run._events.Reader.TryRead(out var envelope) && envelope.Source is { } frame)
                    { Current = frame; moved = true; break; }
                }
                if (!moved && !_return.IsCancellationRequested)
                {
                    _token.ThrowIfCancellationRequested();
                    run._detach.TrySetResult(false);
                    await run.SourceResult.ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested();
                    if (run._sourceTerminal is { } terminal) { Current = terminal; moved = true; _terminalRead = true; }
                }
                // Producer cancellation can complete the channel before this linked token's
                // callback runs. Admission to deliver still belongs to the actual reader token.
                _token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (_return.IsCancellationRequested && !_token.IsCancellationRequested) { }
            catch (Exception error)
            {
                failure = error;
                if (_token.IsCancellationRequested)
                {
                    run.Cancel(); run._detach.TrySetResult(true);
                    await run._owner.ConfigureAwait(false);
                }
            }
            lock (_gate) _reading = null;
            if (failure is null) settlement.TrySetResult(moved); else settlement.TrySetException(failure);
        }
        public ValueTask<CompletionsIteratorResult> ReturnAsync()
        {
            Task<bool>? reading; Task<CompletionsIteratorResult>? next; TaskCompletionSource<CompletionsIteratorResult> settlement;
            lock (_gate)
            {
                if (_returning is not null) return new(_returning);
                if (!_claimed) { run.Claim(); _claimed = true; }
                _returned = true; reading = _reading?.Task; next = _nextOperation;
                settlement = new(TaskCreationOptions.RunContinuationsAsynchronously); _returning = settlement.Task;
            }
            _ = ReturnCoreAsync(reading, next, settlement); return new(settlement.Task);
        }
        private async Task ReturnCoreAsync(Task<bool>? reading, Task<CompletionsIteratorResult>? next, TaskCompletionSource<CompletionsIteratorResult> settlement)
        {
            try
            {
                _return.Cancel();
                if (reading is not null) try { await reading.ConfigureAwait(false); } catch (OperationCanceledException) { }
                if (next is not null) try { await next.ConfigureAwait(false); } catch (OperationCanceledException) { }
                run._detach.TrySetResult(true);
                if (_token.IsCancellationRequested) { run.Cancel(); await run._owner.ConfigureAwait(false); }
                settlement.TrySetResult(new(null));
            }
            catch (Exception)
            { run._detach.TrySetResult(true); settlement.TrySetException(new StreamProtocolException("Source reader return failed.")); }
        }
        public async ValueTask DisposeAsync() => _ = await ReturnAsync().ConfigureAwait(false);
        public async ValueTask CloseIfClaimedAsync()
        { if (_claimed) _ = await ReturnAsync().ConfigureAwait(false); }
        public void DisposeResource() => _return.Dispose();
    }
}
