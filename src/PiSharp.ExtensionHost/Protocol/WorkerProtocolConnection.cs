using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PiSharp.ExtensionHost.Protocol;

/// <summary>One connection epoch; injected borrowed I/O. No broker authorization or JavaScript module execution.</summary>
public sealed class WorkerProtocolConnection : IAsyncDisposable
{
    private static readonly ImmutableArray<string> DefaultFeatures = ["requests", "callbacks", "progress", "cancel", "tagged-values"];
    private readonly WorkerProtocolTransport _transport; private readonly WorkerProtocolOptions _options;
    private readonly long _worker, _session; private readonly ImmutableArray<string> _features;
    private readonly WorkerRequestHandler? _handler; private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<WriteWork> _writes;
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly Dictionary<long, Incoming> _incoming = [];
    private readonly Dictionary<string, Registered> _handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, bool Active)> _owners = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readTask, _writeTask, _cleanup;
    private TaskCompletionSource? _writeIdle;
    private ImmutableHashSet<string>? _negotiated;
    private WorkerProtocolException? _failure;
    private bool _started, _stopped, _helloWritten;
    private long _nextId, _nextHandle, _lastIncomingId, _buffered;
    private int _writeCount;

    public WorkerProtocolConnection(WorkerProtocolTransport transport, long workerGeneration, long sessionGeneration,
        WorkerRequestHandler? handler = null, WorkerProtocolOptions? options = null, IEnumerable<string>? features = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport)); _options = options ?? transport.Options; _options.Validate();
        if (_options != transport.Options) throw new ArgumentException("Transport and coordinator limits must match.", nameof(options));
        WorkerFrameCodec.CheckIdentity(workerGeneration); WorkerFrameCodec.CheckIdentity(sessionGeneration);
        _worker = workerGeneration; _session = sessionGeneration; _handler = handler;
        _features = features is null ? DefaultFeatures : ReadFeatures(features);
        // Apply the same schema to the declared local feature set before creating activity.
        _ = _transport.Codec.Encode(new(WorkerMessageKind.Hello, _worker, _session, Features: _features));
        _writes = Channel.CreateBounded<WriteWork>(new BoundedChannelOptions(_options.MaximumPendingWrites)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }
    public Task Ready => _ready.Task;
    /// <summary>After stop this is the shared joined cleanup task, including any actual transport/protocol fault.</summary>
    public Task Completion => _joined.Task;
    public WorkerProtocolSnapshot Snapshot
    {
        get { lock (_sync) return new(_pending.Count, _incoming.Count, _writeCount, _handles.Count, _buffered, _ready.Task.IsCompletedSuccessfully, _stopped); }
    }
    public Task StartAsync()
    {
        lock (_sync)
        {
            EnsureOpen();
            if (_started) return _ready.Task;
            _started = true; _readTask = ReadPump(); _writeTask = WritePump();
        }
        try { _ = Queue(new(WorkerMessageKind.Hello, _worker, _session, Features: _features), awaitWrite: false); }
        catch (Exception error) { Stop(ToFailure(error)); }
        return _ready.Task;
    }
    /// <summary>No pre-handshake request queue. Caller cancellation retains admission until real settlement.</summary>
    public WorkerCall StartRequest(string method, WorkerValue value, WorkerCallbackHandle? handle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value); WorkerFrameCodec.Identifier(method); cancellationToken.ThrowIfCancellationRequested();
        Pending pending;
        lock (_sync)
        {
            EnsureOpen(); if (!_ready.Task.IsCompletedSuccessfully) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
            if (_pending.Count >= _options.MaximumPendingCalls) throw new WorkerProtocolException(WorkerProtocolFailure.PendingLimit);
            if (handle is not null && !_negotiated!.Contains("callbacks")) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
            if (_nextId == WorkerFrameCodec.MaximumIdentity) throw new WorkerProtocolException(WorkerProtocolFailure.Correlation);
            pending = new(++_nextId, _options.MaximumProgressPerCall); _pending.Add(pending.Id, pending);
        }
        try
        {
            _ = Queue(new(WorkerMessageKind.Request, _worker, _session, pending.Id, method, value, handle), false, pending);
            var registration = cancellationToken.Register(() => CancelRequest(pending.Id));
            lock (_sync)
            {
                pending.Cancellation = registration;
                if (!pending.Attached) registration.Unregister();
            }
        }
        catch
        {
            lock (_sync) Detach(pending);
            throw;
        }
        return new(pending.Result.Task, ReadProgress(pending)) { CancellationWrite = pending.CancellationWrite.Task };
    }
    public async Task<WorkerValue> RequestAsync(string method, WorkerValue value, WorkerCallbackHandle? handle = null,
        CancellationToken cancellationToken = default) =>
        await StartRequest(method, value, handle, cancellationToken).Result.ConfigureAwait(false);

    public WorkerCallbackHandle RegisterCallback(string ownerId, long ownerGeneration, WorkerRequestHandler callback)
    {
        WorkerFrameCodec.Identifier(ownerId); WorkerFrameCodec.CheckIdentity(ownerGeneration); ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            EnsureOpen();
            if (_handles.Count >= _options.MaximumHandles || _nextHandle == WorkerFrameCodec.MaximumIdentity)
                throw new WorkerProtocolException(WorkerProtocolFailure.HandleLimit);
            if (_owners.TryGetValue(ownerId, out var owner))
            {
                if (!owner.Active && ownerGeneration > owner.Generation) _owners[ownerId] = (ownerGeneration, true);
                else if (owner.Generation != ownerGeneration || !owner.Active) throw new WorkerProtocolException(WorkerProtocolFailure.StaleGeneration);
            }
            else
            {
                if (_owners.Count >= _options.MaximumOwners) throw new WorkerProtocolException(WorkerProtocolFailure.HandleLimit);
                _owners.Add(ownerId, (ownerGeneration, true));
            }
            var n = (++_nextHandle).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var handle = new WorkerCallbackHandle(ownerId, ownerGeneration, "r" + n, "c" + n);
            _handles.Add(handle.CallbackId, new(handle, callback)); return handle;
        }
    }
    /// <summary>Revokes all current handles and cancels active callbacks without releasing their slots early.</summary>
    public bool RevokeOwner(string ownerId, long ownerGeneration)
    {
        Incoming[] active;
        lock (_sync)
        {
            if (!_owners.TryGetValue(ownerId, out var owner) || !owner.Active || owner.Generation != ownerGeneration) return false;
            _owners[ownerId] = (ownerGeneration, false);
            foreach (var key in _handles.Where(item => item.Value.Handle.OwnerId == ownerId).Select(item => item.Key).ToArray()) _handles.Remove(key);
            active = _incoming.Values.Where(item => item.Message.Handle?.OwnerId == ownerId).ToArray();
        }
        foreach (var call in active) CancelCooperatively(call.Stop);
        return true;
    }
    /// <summary>A new epoch requires a new negotiated connection. The old epoch is fenced and joined.</summary>
    public ValueTask InvalidateSessionAsync(long replacementGeneration)
    {
        WorkerFrameCodec.CheckIdentity(replacementGeneration);
        if (replacementGeneration <= _session) throw new ArgumentOutOfRangeException(nameof(replacementGeneration));
        Stop(new(WorkerProtocolFailure.StaleGeneration, WorkerOutcome.Unknown)); return DisposeAsync();
    }
    public async ValueTask ShutdownAsync()
    {
        await Queue(new(WorkerMessageKind.Shutdown, _worker, _session)).ConfigureAwait(false);
        await DisposeAsync().ConfigureAwait(false);
    }

    private Task Queue(WorkerMessage message, bool awaitWrite = true, Pending? pending = null, Pending? cancellationOwner = null)
    {
        lock (_sync)
        {
            EnsureOpen();
            if (_writeCount >= _options.MaximumPendingWrites) throw new WorkerProtocolException(WorkerProtocolFailure.WriteLimit);
            if (_writeCount++ == 0) _writeIdle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var retained = 0; var enqueued = false;
        try
        {
            var text = _transport.Codec.Encode(message); retained = WorkerFrameCodec.Utf8.GetByteCount(text);
            var work = new WriteWork(text, retained, message.Kind == WorkerMessageKind.Hello, pending, awaitWrite, cancellationOwner);
            lock (_sync)
            {
                EnsureOpen(); Reserve(retained);
                if (!_writes.Writer.TryWrite(work)) { Release(retained); throw new WorkerProtocolException(WorkerProtocolFailure.WriteLimit); }
                enqueued = true;
            }
            return work.Settled?.Task ?? Task.CompletedTask;
        }
        finally { if (!enqueued) lock (_sync) ReleaseWrite(); }
    }
    private async Task WritePump()
    {
        await Task.Yield();
        try
        {
            await foreach (var work in _writes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var skipped = false; var completed = false;
                try
                {
                    lock (_sync)
                    {
                        if (work.Pending is { } pending)
                        {
                            skipped = pending.CancelRequested;
                            if (!skipped) pending.MayHaveBeenSent = true;
                            else pending.NotSent = true;
                        }
                    }
                    if (!skipped) await _transport.WriteAsync(work.Text, _stop.Token).ConfigureAwait(false);
                    completed = true;
                }
                catch (Exception error)
                {
                    work.CancellationOwner?.CancellationWrite.TrySetException(error is OperationCanceledException && _stop.IsCancellationRequested
                        ? _failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed, WorkerOutcome.Unknown)
                        : _failure ?? ToFailure(error));
                    if (error is OperationCanceledException && _stop.IsCancellationRequested)
                        work.Settled?.TrySetException(_failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed, WorkerOutcome.Unknown));
                    else { var failure = ToFailure(error); work.Settled?.TrySetException(failure); Stop(failure); }
                    break;
                }
                finally
                {
                    lock (_sync)
                    {
                        Release(work.Bytes); ReleaseWrite();
                        if (completed)
                        {
                            if (work.Hello) { _helloWritten = true; SetReady(); }
                            if (work.Pending is { } pending) { pending.WriteSettled = true; Finish(pending); }
                            work.CancellationOwner?.CancellationWrite.TrySetResult(WorkerCancellationWriteDisposition.Written);
                        }
                    }
                    if (completed) work.Settled?.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Stop(ToFailure(error)); }
        finally
        {
            while (_writes.Reader.TryRead(out var queued))
            {
                queued.Settled?.TrySetException(_failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed));
                queued.CancellationOwner?.CancellationWrite.TrySetException(_failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed));
                lock (_sync) { Release(queued.Bytes); ReleaseWrite(); }
            }
        }
    }
    private async Task ReadPump()
    {
        await Task.Yield();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = await _transport.ReadAsync(_stop.Token).ConfigureAwait(false);
                if (frame is null) { Stop(new(WorkerProtocolFailure.EndOfInput, WorkerOutcome.Unknown)); return; }
                Receive(frame.Value.Message, frame.Value.Bytes);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Stop(ToFailure(error)); }
    }
    private void Receive(WorkerMessage message, int bytes)
    {
        if (message.WorkerGeneration != _worker || message.SessionGeneration != _session)
            throw new WorkerProtocolException(WorkerProtocolFailure.StaleGeneration, WorkerOutcome.Unknown);
        if (message.Kind == WorkerMessageKind.Hello)
        {
            lock (_sync)
            {
                if (_negotiated is not null) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
                _negotiated = message.Features.Intersect(_features, StringComparer.Ordinal).ToImmutableHashSet(StringComparer.Ordinal);
                if (!_negotiated.IsSupersetOf(["requests", "cancel", "tagged-values"])) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
                SetReady();
            }
            return;
        }
        lock (_sync) if (_negotiated is null) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
        if (message.Kind == WorkerMessageKind.Shutdown) { Stop(null); return; }
        if (message.Kind == WorkerMessageKind.Request) { AdmitCallback(message, bytes); return; }
        if (message.Kind == WorkerMessageKind.Cancel)
        {
            Incoming? call;
            lock (_sync)
            {
                if (!_incoming.TryGetValue(message.Id!.Value, out call))
                {
                    // Cancellation can cross a reply that already settled. It cannot revive an old ID.
                    if (message.Id.Value <= _lastIncomingId) return;
                    throw new WorkerProtocolException(WorkerProtocolFailure.Correlation);
                }
            }
            CancelCooperatively(call.Stop); return;
        }
        lock (_sync)
        {
            if (!_pending.TryGetValue(message.Id!.Value, out var pending) || !pending.MayHaveBeenSent || pending.ResponseSeen)
                throw new WorkerProtocolException(WorkerProtocolFailure.Correlation);
            if (message.Kind == WorkerMessageKind.Progress)
            {
                if (!_negotiated!.Contains("progress")) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
                Reserve(bytes);
                if (!pending.Progress.Writer.TryWrite((message.Value!, bytes))) { Release(bytes); throw new WorkerProtocolException(WorkerProtocolFailure.ProgressLimit); }
                pending.ProgressBytes += bytes; return;
            }
            if (message.Kind is not (WorkerMessageKind.Response or WorkerMessageKind.Error))
                throw new WorkerProtocolException(WorkerProtocolFailure.InvalidEnvelope);
            Reserve(bytes); pending.ResponseBytes = bytes; pending.ResponseSeen = true; pending.Response = message;
            Finish(pending);
        }
    }
    private void AdmitCallback(WorkerMessage message, int bytes)
    {
        Incoming? call = null; string? rejection = null;
        lock (_sync)
        {
            EnsureOpen();
            if (message.Id!.Value <= _lastIncomingId) throw new WorkerProtocolException(WorkerProtocolFailure.Correlation);
            _lastIncomingId = message.Id.Value;
            WorkerRequestHandler? handler = _handler;
            if (message.Handle is { } h)
            {
                if (!_negotiated!.Contains("callbacks")) throw new WorkerProtocolException(WorkerProtocolFailure.Handshake);
                if (!_handles.TryGetValue(h.CallbackId, out var registered) || registered.Handle != h)
                    rejection = "StaleHandle";
                else handler = registered.Handler;
            }
            if (rejection is null && handler is null) rejection = "UnsupportedMethod";
            if (rejection is null && _incoming.Count >= _options.MaximumCallbacks) rejection = "CallbackLimit";
            if (rejection is null)
            {
                Reserve(bytes); call = new(message, bytes, handler!, _stop.Token);
                _incoming.Add(message.Id.Value, call);
            }
        }
        if (rejection is not null)
        { _ = Queue(new(WorkerMessageKind.Error, _worker, _session, message.Id, ErrorCode: rejection, Outcome: WorkerOutcome.NotSent), false); return; }
        _ = RunCallback(call!); // Bounded by _incoming; RunCallback yields before executing user code.
    }
    private async Task RunCallback(Incoming call)
    {
        await Task.Yield();
        WorkerMessage response;
        try
        {
            call.Token.ThrowIfCancellationRequested();
            var request = new WorkerRequestContext(call.Message, (value, token) => ReportProgress(call, value, token));
            var result = await call.Handler(request, call.Token).ConfigureAwait(false);
            call.Token.ThrowIfCancellationRequested();
            response = new(WorkerMessageKind.Response, _worker, _session, call.Message.Id, Value: result);
        }
        catch (OperationCanceledException) when (call.Stop.IsCancellationRequested)
        { response = new(WorkerMessageKind.Error, _worker, _session, call.Message.Id, ErrorCode: "Cancelled", Outcome: WorkerOutcome.Unknown); }
        catch
        { response = new(WorkerMessageKind.Error, _worker, _session, call.Message.Id, ErrorCode: "CallbackFailed", Outcome: WorkerOutcome.Unknown); }
        try
        {
            Task? responseWrite;
            // Callback response admission shares Stop's lifecycle lock. A callback
            // retiring after Stop has no reply to enqueue; an admitted write still
            // owns its real write/flush outcome and retains the ordinary fault path.
            lock (_sync) responseWrite = _stopped ? null : Queue(response);
            if (responseWrite is not null) await responseWrite.ConfigureAwait(false);
        }
        catch (Exception error) { if (!_stop.IsCancellationRequested) Stop(ToFailure(error)); }
        finally
        {
            call.Stop.Dispose();
            lock (_sync) { Release(call.Bytes); _incoming.Remove(call.Message.Id!.Value); call.Settled.TrySetResult(); }
        }
    }
    private async ValueTask ReportProgress(Incoming call, WorkerValue value, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(value); token.ThrowIfCancellationRequested(); call.Token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureOpen();
            if (!_negotiated!.Contains("progress") || !_incoming.ContainsKey(call.Message.Id!.Value))
                throw new WorkerProtocolException(WorkerProtocolFailure.Correlation);
        }
        await Queue(new(WorkerMessageKind.Progress, _worker, _session, call.Message.Id, Value: value)).WaitAsync(token).ConfigureAwait(false);
    }
    private void CancelRequest(long id)
    {
        Pending? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(id, out pending) || pending.CancelRequested) return;
            pending.CancelRequested = true;
            var send = pending.MayHaveBeenSent;
            Exception? queueFailure = null;
            if (send)
            {
                try
                {
                    // Enqueue the real cancel before publishing caller cancellation.
                    // A subsequent source-settlement fence cannot overtake it.
                    pending.CancellationQueued = true;
                    _ = Queue(new(WorkerMessageKind.Cancel, _worker, _session, id), false, cancellationOwner: pending);
                }
                catch (Exception error) { pending.CancellationWrite.TrySetException(ToFailure(error)); queueFailure = error; }
            }
            pending.Result.TrySetException(new WorkerProtocolException(WorkerProtocolFailure.Cancelled,
                send ? WorkerOutcome.Unknown : WorkerOutcome.NotSent));
            if (queueFailure is not null) Stop(ToFailure(queueFailure));
        }
    }
    private async IAsyncEnumerable<WorkerValue> ReadProgress(Pending pending,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref pending.ProgressClaimed, 1) != 0)
            throw new InvalidOperationException("A worker call has one progress reader.");
        await foreach (var item in pending.Progress.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_sync) if (pending.Attached) { pending.ProgressBytes -= item.Bytes; Release(item.Bytes); }
            yield return item.Value;
        }
    }
    private void Finish(Pending pending)
    {
        if (_stopped || !pending.WriteSettled || !(pending.NotSent || pending.ResponseSeen)) return;
        if (!pending.CancelRequested && pending.Response is { } response)
        {
            if (response.Kind == WorkerMessageKind.Error)
                pending.Result.TrySetException(new WorkerProtocolException(WorkerProtocolFailure.RemoteError, response.Outcome, response.ErrorCode));
            else pending.Result.TrySetResult(response.Value!);
        }
        Detach(pending);
    }
    private void Detach(Pending pending)
    {
        if (!pending.Attached) return;
        if (!pending.CancelRequested) pending.CancellationWrite.TrySetResult(WorkerCancellationWriteDisposition.NotRequested);
        else if (pending.NotSent && pending.WriteSettled) pending.CancellationWrite.TrySetResult(WorkerCancellationWriteDisposition.NotSent);
        pending.Attached = false; pending.Cancellation.Unregister();
        _pending.Remove(pending.Id); Release(pending.ResponseBytes + pending.ProgressBytes);
        pending.ResponseBytes = pending.ProgressBytes = 0;
        pending.Progress.Writer.TryComplete(); // Remaining owned values transfer to the caller's WorkerCall.
    }
    private void SetReady()
    { if (_helloWritten && _negotiated is not null) _ready.TrySetResult(); }
    private static ImmutableArray<string> ReadFeatures(IEnumerable<string> features)
    {
        var result = ImmutableArray.CreateBuilder<string>(16);
        foreach (var feature in features)
        {
            if (result.Count == 16) throw new WorkerProtocolException(WorkerProtocolFailure.InvalidEnvelope);
            result.Add(WorkerFrameCodec.Identifier(feature));
        }
        return result.ToImmutable();
    }
    private void EnsureOpen()
    { if (_stopped) throw _failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed); }
    private void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > _options.MaximumBufferedBytes - _buffered) throw new WorkerProtocolException(WorkerProtocolFailure.BufferedLimit);
        _buffered += bytes;
    }
    private void Release(long bytes) => _buffered -= bytes;
    private void ReleaseWrite()
    { if (--_writeCount == 0) _writeIdle!.TrySetResult(); }
    private void CancelCooperatively(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { } // An already settled bounded callback may race a cancellation snapshot.
        catch (Exception error) { Stop(ToFailure(error)); }
    }
    private static WorkerProtocolException ToFailure(Exception error) => error as WorkerProtocolException ??
        new(WorkerProtocolFailure.Transport, WorkerOutcome.Unknown);

    private void Stop(WorkerProtocolException? failure)
    {
        TaskCompletionSource joined;
        lock (_sync)
        {
            if (_stopped) { _failure ??= failure; return; }
            _stopped = true; _failure = failure; _handles.Clear(); _writes.Writer.TryComplete();
            _ready.TrySetException(failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed));
            joined = _joined; _cleanup = joined.Task;
            foreach (var pending in _pending.Values)
            {
                pending.Result.TrySetException(new WorkerProtocolException(failure?.Failure ?? WorkerProtocolFailure.Closed,
                    pending.MayHaveBeenSent ? WorkerOutcome.Unknown : WorkerOutcome.NotSent));
                // A scheduled cancellation frame is settled by its actual writer.
                // Other calls must wake callback waiters before cleanup joins them.
                if (!pending.CancellationQueued)
                    pending.CancellationWrite.TrySetException(failure ?? new WorkerProtocolException(WorkerProtocolFailure.Closed));
            }
        }
        _ = Cleanup(joined);
    }
    private async Task Cleanup(TaskCompletionSource joined)
    {
        await Task.Yield();
        Exception? cleanupError = null;
        try
        {
            CancelCooperatively(_stop);
            if (_readTask is { } read) await read.ConfigureAwait(false);
            if (_writeTask is { } write) await write.ConfigureAwait(false);
            Task idle; Task[] callbacks;
            lock (_sync)
            {
                idle = _writeCount == 0 ? Task.CompletedTask : _writeIdle!.Task;
                callbacks = _incoming.Values.Select(call => call.Settled.Task).ToArray();
            }
            await idle.ConfigureAwait(false); await Task.WhenAll(callbacks).ConfigureAwait(false);
            lock (_sync) foreach (var pending in _pending.Values.ToArray()) Detach(pending);
        }
        catch (Exception error) { cleanupError = error; }
        finally { _stop.Dispose(); }
        if (cleanupError is { } settlementError) joined.TrySetException(settlementError);
        else if (_failure is { } failure) joined.TrySetException(failure);
        else joined.TrySetResult();
    }
    public ValueTask DisposeAsync()
    {
        Stop(null);
        lock (_sync) return new(_cleanup!);
    }
    private sealed class WriteWork(string text, int bytes, bool hello, Pending? pending, bool awaitWrite, Pending? cancellationOwner)
    {
        public string Text { get; } = text; public int Bytes { get; } = bytes; public bool Hello { get; } = hello;
        public Pending? Pending { get; } = pending;
        public Pending? CancellationOwner { get; } = cancellationOwner;
        public TaskCompletionSource? Settled { get; } = awaitWrite ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
    }
    private sealed class Pending(long id, int maximumProgress)
    {
        public long Id { get; } = id;
        public TaskCompletionSource<WorkerValue> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WorkerCancellationWriteDisposition> CancellationWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<(WorkerValue Value, int Bytes)> Progress { get; } = Channel.CreateBounded<(WorkerValue, int)>(
            new BoundedChannelOptions(maximumProgress) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public bool Attached = true, MayHaveBeenSent, WriteSettled, CancelRequested, CancellationQueued, NotSent, ResponseSeen;
        public int ProgressClaimed;
        public long ResponseBytes, ProgressBytes; public WorkerMessage? Response; public CancellationTokenRegistration Cancellation;
    }
    private sealed record Registered(WorkerCallbackHandle Handle, WorkerRequestHandler Handler);
    private sealed class Incoming
    {
        public WorkerMessage Message { get; } public int Bytes { get; }
        public WorkerRequestHandler Handler { get; }
        public CancellationTokenSource Stop { get; } public CancellationToken Token { get; }
        public Incoming(WorkerMessage message, int bytes, WorkerRequestHandler handler, CancellationToken stop)
        {
            Message = message; Bytes = bytes; Handler = handler;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(stop); Token = Stop.Token;
        }
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
public enum WorkerCancellationWriteDisposition { NotRequested, NotSent, Written }

public sealed record WorkerCall(Task<WorkerValue> Result, IAsyncEnumerable<WorkerValue> Progress)
{
    /// <summary>Written follows the actual cancel write/flush; faults still require connection cleanup.</summary>
    public Task<WorkerCancellationWriteDisposition> CancellationWrite { get; init; } =
        Task.FromResult(WorkerCancellationWriteDisposition.NotRequested);
}
