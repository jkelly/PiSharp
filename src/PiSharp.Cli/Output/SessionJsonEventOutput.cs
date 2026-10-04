using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;

namespace PiSharp.Cli.Output;

public sealed record SessionJsonEventOutputOptions(int MaximumRecordBytes = 1_048_576,
    long MaximumTotalBytes = 16_777_216, int MaximumRecords = 16_384, int MaximumPendingObservations = 128,
    int MaximumJsonDepth = 32, int MaximumReturnedMessages = 1024, int MaximumPendingToolMessages = 128)
{
    internal void Validate()
    {
        if (MaximumRecordBytes is < 257 or > int.MaxValue - 1 || MaximumTotalBytes < MaximumRecordBytes ||
            MaximumRecords <= 0 || MaximumPendingObservations <= 0 || MaximumJsonDepth is < 1 or > 64 ||
            MaximumReturnedMessages <= 0 || MaximumPendingToolMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(SessionJsonEventOutputOptions), "Invalid JSON event output bounds.");
    }
}

public enum SessionJsonEventOutputFailure { ResourceLimit, ProjectionFailed, OutputFailed, Canceled, InvalidState, Disposed }
public sealed class SessionJsonEventOutputException : IOException
{
    public SessionJsonEventOutputFailure Failure { get; }
    internal SessionJsonEventOutputException(SessionJsonEventOutputFailure failure) : base(failure switch
    {
        SessionJsonEventOutputFailure.ResourceLimit => "JSON event output exceeds configured bounds.",
        SessionJsonEventOutputFailure.ProjectionFailed => "JSON event projection failed after durable admission.",
        SessionJsonEventOutputFailure.Canceled => "JSON event delivery was canceled after admission.",
        SessionJsonEventOutputFailure.InvalidState => "JSON event output lifecycle is invalid.",
        SessionJsonEventOutputFailure.Disposed => "JSON event output is closing or disposed.",
        _ => "JSON event delivery failed; inspect durable state before retrying."
    }) => Failure = failure;
}

/// <summary>Awaited bounded JSONL subscription over a borrowed idle session and borrowed writer.</summary>
public sealed class SessionJsonEventOutput : IAgentEventSink, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _inside = new();
    private readonly SemaphoreSlim _wire = new(1, 1);
    private PersistentAgentSession _session;
    private readonly ReplaceableAgentSession? _sessionOwner;
    private readonly Func<AgentSessionReplacement, ValueTask>? _replacementCallback;
    private readonly TextWriter _output;
    private readonly SessionJsonEventOutputOptions _options;
    private readonly RpcAgentEventProjector _projector;
    private readonly JsonlTransportOptions _framing;
    private IDisposable _subscription;
    private readonly JsonData _header;
    private int _historyLength;
    private TaskCompletionSource? _idle;
    private Task? _disposal;
    private SessionJsonEventOutputException? _fault;
    private int _pending, _records;
    private long _bytes;
    private bool _starting, _started, _closed;

    public SessionJsonEventOutput(PersistentAgentSession session, TextWriter output, SessionJsonEventOutputOptions? options = null)
        : this(session, output, options, null) { }
    public SessionJsonEventOutput(PersistentAgentSession session, TextWriter output, SessionJsonEventOutputOptions? options,
        ReplaceableAgentSession? sessionOwner)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(output);
        _options = options ?? new(); _options.Validate();
        var snapshot = session.Snapshot;
        if (snapshot.IsDisposed || snapshot.IsRetired || snapshot.IsConfiguring || snapshot.IsAppendingExtensionEntry || snapshot.Agent.IsRunning || snapshot.Fault is not null ||
            sessionOwner is not null && (!ReferenceEquals(sessionOwner.Current.Session, session) || sessionOwner.AttachmentChanged is not null))
            throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState);
        _session = session; _output = output; _header = snapshot.Log.Header.WireBody;
        _historyLength = snapshot.Agent.Messages.Length;
        _projector = new(new(MaximumOutputBytes: _options.MaximumRecordBytes - 1,
            MaximumJsonDepth: _options.MaximumJsonDepth, MaximumReturnedMessages: _options.MaximumReturnedMessages,
            MaximumPendingToolMessages: _options.MaximumPendingToolMessages));
        _framing = new(MaximumFrameBytes: _options.MaximumRecordBytes - 1, MaximumJsonDepth: _options.MaximumJsonDepth);
        // The command owns exclusive prompt submission. Subscribe before the opening header and before that submission.
        _subscription = session.Subscribe(this);
        _sessionOwner = sessionOwner;
        if (sessionOwner is not null) sessionOwner.AttachmentChanged = _replacementCallback = ReplaceAsync;
    }

    public bool IsPoisoned { get { lock (_gate) return _fault is not null; } }
    public SessionJsonEventOutputFailure? Failure { get { lock (_gate) return _fault?.Failure; } }
    public int AcknowledgedRecords { get { lock (_gate) return _records; } }
    public long AcknowledgedBytes { get { lock (_gate) return _bytes; } }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_inside.Value) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState);
        lock (_gate)
        {
            ThrowOpen();
            if (_starting || _started) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState);
            cancellationToken.ThrowIfCancellationRequested(); _starting = true; Admit();
        }
        return StartCoreAsync(cancellationToken);
    }
    private async Task StartCoreAsync(CancellationToken token)
    {
        var prior = _inside.Value; _inside.Value = true;
        try
        {
            await _wire.WaitAsync().ConfigureAwait(false);
            try
            {
                await WriteAsync(_header, token).ConfigureAwait(false);
                lock (_gate) _started = true;
            }
            finally { _wire.Release(); }
        }
        finally { Leave(); _inside.Value = prior; }
    }

    public async ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        try
        {
            if (_inside.Value) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState);
            lock (_gate)
            {
                ThrowOpen();
                if (!_started) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState);
                Admit();
            }
        }
        catch (SessionJsonEventOutputException error) { throw Poison(error.Failure); }
        var prior = _inside.Value; _inside.Value = true;
        try
        {
            // Source-compatible progress may overlap. Admission bounds every waiter; mapping and physical writes stay serial.
            await _wire.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfFailed();
                System.Collections.Immutable.ImmutableArray<JsonData> records;
                try { records = _projector.Project(observation, _session.Snapshot, _historyLength); }
                catch (RpcDispatchException error) { throw Poison(error.Failure == RpcDispatchFailure.ResourceLimit ?
                    SessionJsonEventOutputFailure.ResourceLimit : SessionJsonEventOutputFailure.ProjectionFailed); }
                catch (Exception) { throw Poison(SessionJsonEventOutputFailure.ProjectionFailed); }
                foreach (var record in records) await WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }
            finally { _wire.Release(); }
        }
        finally { Leave(); _inside.Value = prior; }
    }

    private async ValueTask ReplaceAsync(AgentSessionReplacement replacement)
    {
        if (_inside.Value) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState);
        lock (_gate) { ThrowOpen(); if (!_started) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.InvalidState); Admit(); }
        var prior = _inside.Value; _inside.Value = true;
        try
        {
            await _wire.WaitAsync().ConfigureAwait(false);
            try
            {
                ThrowIfFailed();
                var session = replacement.Current.Session; var next = session.Subscribe(this); var previous = _subscription;
                _session = session; _subscription = next; _historyLength = session.Snapshot.Agent.Messages.Length;
                previous.Dispose();
                await WriteAsync(JsonData.Parse(JsonSerializer.Serialize(new
                {
                    type = "session_switched", sessionFile = session.Path, sessionId = session.Snapshot.Log.Header.Id,
                    generation = replacement.Current.Generation
                })), CancellationToken.None).ConfigureAwait(false);
            }
            finally { _wire.Release(); }
        }
        finally { Leave(); _inside.Value = prior; }
    }

    private async Task WriteAsync(JsonData record, CancellationToken token)
    {
        ThrowIfFailed();
        string text; int count;
        try
        {
            // Bound retained input before shared strict reparsing/compaction allocates a second view.
            if (record.ToString().Length >= _options.MaximumRecordBytes)
                throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.ResourceLimit);
            text = JsonlRecordFormatter.Format(record, _framing);
            count = Utf8.GetByteCount(text);
            lock (_gate)
                if (count > _options.MaximumRecordBytes || _records >= _options.MaximumRecords || count > _options.MaximumTotalBytes - _bytes)
                    throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.ResourceLimit);
        }
        catch (Exception) { throw Poison(SessionJsonEventOutputFailure.ResourceLimit); }
        try
        {
            token.ThrowIfCancellationRequested();
            await _output.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
            await _output.FlushAsync(token).ConfigureAwait(false);
            // A completed write/flush remains an acknowledged delivery even if cancellation arrives immediately afterwards.
            lock (_gate) { _records++; _bytes += count; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Poison(SessionJsonEventOutputFailure.Canceled); throw; }
        catch (Exception) { throw Poison(SessionJsonEventOutputFailure.OutputFailed); }
    }

    public void ThrowIfFailed()
    { lock (_gate) if (_fault is not null) throw _fault; }
    private SessionJsonEventOutputException Poison(SessionJsonEventOutputFailure failure)
    {
        SessionJsonEventOutputException fault;
        lock (_gate) fault = _fault ??= new(failure);
        // Abort is synchronous admission only. Awaiting this generation inside its own callback would deadlock.
        // Trusted cancellation callback failures must not replace the original projection/output failure.
        try { if (_sessionOwner is not null) _sessionOwner.Abort(); else _session.Abort(); } catch (Exception) { }
        return fault;
    }
    private void ThrowOpen()
    {
        if (_fault is not null) throw _fault;
        if (_closed) throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.Disposed);
    }
    private void Admit()
    {
        if (_pending >= _options.MaximumPendingObservations)
            throw new SessionJsonEventOutputException(SessionJsonEventOutputFailure.ResourceLimit);
        if (_pending++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private void Leave()
    {
        TaskCompletionSource? idle = null;
        lock (_gate) if (--_pending == 0) { idle = _idle; _idle = null; }
        idle?.TrySetResult();
    }
    public ValueTask DisposeAsync()
    {
        if (_inside.Value) throw new InvalidOperationException("JSON event callbacks cannot await their own disposal.");
        TaskCompletionSource completion; Task idle;
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _closed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task;
            idle = _idle?.Task ?? Task.CompletedTask;
        }
        // The shared task owns the short shutdown transition; callers join it, including every admitted callback.
        _ = DisposeCoreAsync(idle, completion); return new(completion.Task);
    }
    private async Task DisposeCoreAsync(Task idle, TaskCompletionSource completion)
    {
        try
        {
            if (_sessionOwner is not null && _sessionOwner.AttachmentChanged == _replacementCallback) _sessionOwner.AttachmentChanged = null;
            await idle.ConfigureAwait(false);
            try { _subscription.Dispose(); }
            finally { _wire.Dispose(); }
            completion.TrySetResult();
        }
        catch (Exception) { completion.TrySetException(new SessionJsonEventOutputException(SessionJsonEventOutputFailure.OutputFailed)); }
        // Every admitted record already joined its own flush. Never retry a failed writer or close borrowed resources.
    }
}
