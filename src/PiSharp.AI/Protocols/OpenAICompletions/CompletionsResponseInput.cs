using System.Collections.Immutable;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>An actual immutable byte enqueue, or physical EOF, in the bounded response input.</summary>
public sealed record CompletionsInputPublication(long Offset, ImmutableArray<byte> Value, bool PhysicalEof);

/// <summary>One bounded prefetched packet over a borrowed physical body. Async disposal joins its read and cancellation.</summary>
public sealed class CompletionsResponseInput : Stream
{
    private readonly object _gate = new();
    private readonly CompletionsBodyReader _physical;
    private readonly CancellationTokenSource _stop;
    private readonly Action<CompletionsInputPublication>? _observe;
    private readonly Func<CancellationToken, ValueTask>? _beforeRefill;
    private readonly AsyncLocal<bool> _insidePull = new();
    private TaskCompletionSource<CompletionsBodyReadResult> _next = new();
    private TaskCompletionSource? _cancel;
    private CompletionsBodyReadResult? _head;
    private int _headOffset, _reading, _buffered, _peak;
    private long _enqueued;
    private bool _closed, _disposed;

    public CompletionsResponseInput(Stream body, int maximumReadBytes = 65_536,
        Action<CompletionsInputPublication>? observe = null, CancellationToken cancellationToken = default)
        : this(body, maximumReadBytes, observe, cancellationToken, null) { }

    internal CompletionsResponseInput(Stream body, int maximumReadBytes,
        Action<CompletionsInputPublication>? observe, CancellationToken token,
        Func<CancellationToken, ValueTask>? beforeRefill)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (maximumReadBytes is < 1 or > 65_536) throw new ArgumentOutOfRangeException(nameof(maximumReadBytes));
        if (!body.CanRead) throw new ArgumentException("The response input requires a readable body.", nameof(body));
        _physical = new(CompletionsResponseBodyReader.FromStream(body), maximumReadBytes);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _observe = observe; _beforeRefill = beforeRefill;
        _ = PullAsync(_next, false);
    }

    public long BytesEnqueued { get { lock (_gate) return _enqueued; } }
    public int BufferedBytes { get { lock (_gate) return _buffered; } }
    public int PeakBufferedBytes { get { lock (_gate) return _peak; } }
    public bool ReachedPhysicalEof => _physical.ReachedEof;
    public override bool CanRead { get { lock (_gate) return !_disposed; } }
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    private async Task PullAsync(TaskCompletionSource<CompletionsBodyReadResult> next, bool refill)
    {
        CompletionsBodyReadResult? result = null; Exception? failure = null;
        try
        {
            _insidePull.Value = true;
            if (refill && _beforeRefill is not null) await _beforeRefill(_stop.Token).ConfigureAwait(false);
            lock (_gate) { if (_closed) result = CompletionsBodyReadResult.Canceled; }
            if (result is null) result = await _physical.ReadAsync(_stop.Token).ConfigureAwait(false);
            CompletionsInputPublication? publication = null;
            lock (_gate)
            {
                if (_closed) result = CompletionsBodyReadResult.Canceled;
                else if (result.PhysicalEof) { _closed = true; publication = new(_enqueued, [], true); }
                else if (!result.Done)
                {
                    publication = new(_enqueued, result.Value, false);
                    _enqueued = checked(_enqueued + result.Value.Length);
                    _buffered = checked(_buffered + result.Value.Length); _peak = Math.Max(_peak, _buffered);
                }
                else _closed = true;
            }
            if (publication is not null) _observe?.Invoke(publication);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { result = CompletionsBodyReadResult.Canceled; }
        catch (Exception error) { failure = error; }
        finally { _insidePull.Value = false; }
        if (failure is null) next.TrySetResult(result!); else next.TrySetException(failure);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (destination.IsEmpty) throw new ArgumentException("A response input read must be nonempty.", nameof(destination));
        Task<CompletionsBodyReadResult>? pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reading != 0) throw new InvalidOperationException("A response input has one active read.");
            _reading = 1; pending = _head is null ? _next.Task : null;
        }
        try
        {
            var next = pending is null ? null : await pending.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TaskCompletionSource<CompletionsBodyReadResult>? refill = null; int count;
            lock (_gate)
            {
                if (_cancel is not null) return 0;
                _head ??= next;
                if (_head!.Done) return 0;
                count = Math.Min(destination.Length, _head.Value.Length - _headOffset);
                _head.Value.AsSpan(_headOffset, count).CopyTo(destination.Span);
                _headOffset += count; _buffered -= count;
                if (_headOffset == _head.Value.Length)
                {
                    _head = null; _headOffset = 0; _next = refill = new();
                }
            }
            if (refill is not null) _ = PullAsync(refill, true);
            return count;
        }
        finally { lock (_gate) _reading = 0; }
    }

    /// <summary>Closes enqueue admission, joins even a noncooperative pull, and releases the physical read lease once.</summary>
    public ValueTask CancelAsync()
    {
        if (_insidePull.Value) throw new InvalidOperationException("A response input pull cannot join its own cancellation.");
        TaskCompletionSource settled; Task<CompletionsBodyReadResult> pending;
        lock (_gate)
        {
            if (_cancel is not null) return new(_cancel.Task);
            _closed = true; _cancel = settled = new(TaskCreationOptions.RunContinuationsAsynchronously); pending = _next.Task;
        }
        _ = CancelCoreAsync(settled, pending); return new(settled.Task);
    }

    private async Task CancelCoreAsync(TaskCompletionSource settled, Task<CompletionsBodyReadResult> pending)
    {
        Exception? failure = null;
        try { _stop.Cancel(); } catch (Exception error) { failure = error; }
        if (!_physical.ReachedEof)
            try { await _physical.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        try { await pending.ConfigureAwait(false); } catch (Exception error) { failure ??= error; }
        try { _physical.Release(); } catch (Exception error) { failure ??= error; }
        lock (_gate) { _head = null; _headOffset = 0; _buffered = 0; }
        _stop.Dispose();
        if (failure is null) settled.TrySetResult(); else settled.TrySetException(failure);
    }

    public override async ValueTask DisposeAsync()
    {
        try { await CancelAsync().ConfigureAwait(false); }
        finally { lock (_gate) _disposed = true; GC.SuppressFinalize(this); }
    }
    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        var cancellation = CancelAsync();
        if (!cancellation.IsCompleted) throw new InvalidOperationException("Use asynchronous response input disposal to join pending work.");
        try { cancellation.GetAwaiter().GetResult(); }
        finally { lock (_gate) _disposed = true; }
        base.Dispose(disposing);
    }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
