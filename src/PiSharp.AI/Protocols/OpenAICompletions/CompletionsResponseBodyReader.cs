using System.Collections.Immutable;

namespace PiSharp.AI.Protocols.OpenAICompletions;

/// <summary>An invocation-owned reader lease. The response owner independently owns the physical body.</summary>
public interface ICompletionsResponseBodyReader
{
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default);
    ValueTask CancelAsync();
    void Release();
}

/// <summary>Creates a real executor after header observation over borrowed input: prefetched source input or the canonical physical body.</summary>
public delegate ValueTask<ICompletionsResponseBodyReader> CompletionsResponseBodyReaderFactory(
    Stream body, CancellationToken cancellationToken);

public static class CompletionsResponseBodyReader
{
    /// <summary>Actual cooperative read cancellation and lease release; does not dispose the borrowed body.</summary>
    public static ICompletionsResponseBodyReader FromStream(Stream body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!body.CanRead) throw new ArgumentException("A Completions body must be readable.", nameof(body));
        return new StreamReaderLease(body);
    }

    private sealed class StreamReaderLease(Stream body) : ICompletionsResponseBodyReader, ICompletionsIndependentRelease
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly AsyncLocal<bool> _insideRead = new();
        private TaskCompletionSource<int>? _reading;
        private TaskCompletionSource? _canceling;
        private bool _released;
        private bool _resourceDisposed;

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destination.Length == 0) throw new ArgumentException("A read destination must be nonempty.", nameof(destination));
            TaskCompletionSource<int> settlement;
            lock (_gate)
            {
                if (_released || _canceling is not null) throw Closed();
                if (_reading is not null) throw new InvalidOperationException("A Completions reader has one active read.");
                _reading = settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            // The retained settlement owns all admitted read work. This worker catches every
            // failure and settles only after its cancellation registration has been released.
            _ = ReadCoreAsync(destination, cancellationToken, settlement);
            return new(settlement.Task);
        }

        private async Task ReadCoreAsync(Memory<byte> destination, CancellationToken token, TaskCompletionSource<int> settlement)
        {
            var count = 0; Exception? failure = null;
            try
            {
                _insideRead.Value = true;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                linked.Token.ThrowIfCancellationRequested();
                count = await body.ReadAsync(destination, linked.Token).ConfigureAwait(false);
                if (count < 0 || count > destination.Length) throw new InvalidOperationException("Invalid Completions read count.");
            }
            catch (Exception error) { failure = error; }
            finally { _insideRead.Value = false; }
            lock (_gate)
            {
                if (failure is null) settlement.TrySetResult(count); else settlement.TrySetException(failure);
                _reading = null;
                if (_released && !_resourceDisposed && _canceling is not { Task.IsCompleted: false })
                { _stop.Dispose(); _resourceDisposed = true; }
            }
        }

        public ValueTask CancelAsync()
        {
            if (_insideRead.Value) throw new InvalidOperationException("A reader execution cannot join its own cancellation.");
            TaskCompletionSource settlement; Task<int>? reading;
            lock (_gate)
            {
                if (_released) throw Closed();
                if (_canceling is not null) return new(_canceling.Task);
                _canceling = settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
                reading = _reading?.Task;
            }
            _ = CancelCoreAsync(reading, settlement);
            return new(settlement.Task);
        }

        private async Task CancelCoreAsync(Task<int>? reading, TaskCompletionSource settlement)
        {
            Exception? failure = null;
            try { _stop.Cancel(); } catch (Exception error) { failure = error; }
            if (reading is not null)
                try { await reading.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception error) { failure ??= error; }
            lock (_gate)
                if (_released && !_resourceDisposed) { _stop.Dispose(); _resourceDisposed = true; }
            if (failure is null) settlement.TrySetResult(); else settlement.TrySetException(failure);
        }

        public void Release()
        {
            lock (_gate)
            {
                if (_released) throw Closed();
                if ((_reading is not null || _canceling is { Task.IsCompleted: false }) &&
                    (body is not CompletionsResponseInput || _canceling is null))
                    throw new InvalidOperationException("Reader release requires joined read/cancellation settlement.");
                _released = true;
                // Source input owns the physical pull independently. On interruption,
                // close this logical lease now while its existing read/cancel workers
                // retain their token resource until actual settlement.
                if (_reading is null && _canceling is not { Task.IsCompleted: false })
                { _stop.Dispose(); _resourceDisposed = true; }
            }
        }

        public bool TryReleaseIndependent()
        {
            lock (_gate)
            {
                if (_released) return true;
                if (_reading is not null && body is not CompletionsResponseInput) return false;
                _released = true; // Actual read/cancel authority closes here.
                if (_reading is null && _canceling is not { Task.IsCompleted: false }) { _stop.Dispose(); _resourceDisposed = true; }
                // Otherwise CancelCore owns and disposes this CTS before its retained task settles.
                return true;
            }
        }

        private static InvalidOperationException Closed() => new("The Completions reader lease is closed.");
    }
}

internal interface ICompletionsIndependentRelease { bool TryReleaseIndependent(); }

/// <summary>A single-owner bounded result reader over the actual executor; borrows its physical body.</summary>
public sealed class CompletionsBodyReader
{
    private readonly ICompletionsResponseBodyReader _executor;
    private readonly ICompletionsResponseBodyResultReader? _resultExecutor;
    private readonly int _maximumReadBytes;
    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _insideRead = new();
    private TaskCompletionSource<CompletionsBodyReadResult>? _reading;
    private TaskCompletionSource? _canceling;
    private bool _released;
    private bool _reachedEof;
    private bool _closed;
    public bool ReachedEof { get { lock (_gate) return _reachedEof; } }
    public bool Released { get { lock (_gate) return _released; } }

    public CompletionsBodyReader(ICompletionsResponseBodyReader executor, int maximumReadBytes = 4096)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumReadBytes, 65_536);
        _executor = executor; _maximumReadBytes = maximumReadBytes; _buffer = new byte[maximumReadBytes];
    }

    /// <summary>Uses an actual bounded result executor, retaining empty-chunk and physical-EOF authority.</summary>
    public static CompletionsBodyReader FromResultReader(ICompletionsResponseBodyResultReader executor, int maximumReadBytes = 4096)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return new(executor, maximumReadBytes);
    }

    private CompletionsBodyReader(ICompletionsResponseBodyResultReader executor, int maximumReadBytes)
        : this(new ResultLifecycle(executor), maximumReadBytes) => _resultExecutor = executor;

    private sealed class ResultLifecycle(ICompletionsResponseBodyResultReader executor) : ICompletionsResponseBodyReader
    {
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default) =>
            throw new InvalidOperationException("A result executor does not perform count-only reads.");
        public ValueTask CancelAsync() => executor.CancelAsync();
        public void Release() => executor.Release();
    }

    public ValueTask<CompletionsBodyReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(_maximumReadBytes, cancellationToken);

    internal ValueTask<CompletionsBodyReadResult> ReadAsync(int requestedBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestedBytes is 0 || requestedBytes > _maximumReadBytes) throw new InvalidOperationException("Invalid Completions read buffer.");
        TaskCompletionSource<CompletionsBodyReadResult> settlement;
        lock (_gate)
        {
            if (_released) throw new InvalidOperationException("The Completions reader is closed.");
            if (_reading is not null) throw new InvalidOperationException("A Completions reader has one active read.");
            if (_closed) return ValueTask.FromResult(_reachedEof ? CompletionsBodyReadResult.End : CompletionsBodyReadResult.Canceled);
            _reading = settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = ReadCoreAsync(requestedBytes, cancellationToken, settlement);
        return new(settlement.Task);
    }

    private async Task ReadCoreAsync(int requestedBytes, CancellationToken token, TaskCompletionSource<CompletionsBodyReadResult> settlement)
    {
        CompletionsBodyReadResult? result = null; Exception? failure = null;
        try
        {
            _insideRead.Value = true;
            if (_resultExecutor is not null)
            {
                result = await _resultExecutor.ReadAsync(requestedBytes, token).ConfigureAwait(false);
                if (result is null || result.Value.Length > requestedBytes || result.Done && !result.Value.IsEmpty)
                    throw new InvalidOperationException("Invalid Completions read result.");
            }
            else
            {
                var count = await _executor.ReadAsync(_buffer.AsMemory(0, requestedBytes), token).ConfigureAwait(false);
                if (count < 0 || count > requestedBytes) throw new InvalidOperationException("Invalid Completions read count.");
                result = count == 0 ? CompletionsBodyReadResult.End : new(ImmutableArray.Create(_buffer, 0, count), false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || CancellationStarted())
        {
            // This is the completed canceled read, not a fabricated physical EOF. Its
            // executor cancellation remains independently owned/joined by CancelAsync.
            result = CompletionsBodyReadResult.Canceled;
        }
        catch (Exception error) { failure = error; }
        finally { _insideRead.Value = false; }
        lock (_gate)
        {
            if (failure is null)
            { _closed |= result!.Done; _reachedEof |= result.PhysicalEof; settlement.TrySetResult(result); }
            else settlement.TrySetException(failure);
            _reading = null;
        }
    }
    private bool CancellationStarted() { lock (_gate) return _canceling is not null; }

    public ValueTask CancelAsync()
    {
        if (_insideRead.Value) throw new InvalidOperationException("A reader execution cannot join its own cancellation.");
        TaskCompletionSource settlement; Task<CompletionsBodyReadResult>? reading;
        lock (_gate)
        {
            if (_released) throw new InvalidOperationException("The Completions reader lease is released.");
            if (_canceling is not null) return new(_canceling.Task);
            _closed = true;
            _canceling = settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            reading = _reading?.Task;
        }
        _ = CancelCoreAsync(reading, settlement);
        return new(settlement.Task);
    }

    private async Task CancelCoreAsync(Task<CompletionsBodyReadResult>? reading, TaskCompletionSource settlement)
    {
        Exception? failure = null;
        try { await _executor.CancelAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        if (reading is not null)
            try { await reading.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure ??= error; }
        if (failure is null) settlement.TrySetResult(); else settlement.TrySetException(failure);
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_released) throw new InvalidOperationException("The Completions reader lease is released.");
            if (_reading is not null || _canceling is { Task.IsCompleted: false })
                throw new InvalidOperationException("Reader release requires joined read/cancellation settlement.");
            _released = true;
        }
        _executor.Release();
    }

    internal bool TryReleaseIndependent(bool permitPendingSourceRead = false)
    {
        lock (_gate) { if (_released) return true; if (_reading is not null && !permitPendingSourceRead) return false; }
        if (_executor is ICompletionsIndependentRelease independent && !independent.TryReleaseIndependent()) return false;
        if (_executor is not ICompletionsIndependentRelease)
        {
            try { _executor.Release(); }
            catch (InvalidOperationException) { return false; } // An executor can require its own cancel settlement first.
            catch { lock (_gate) _released = true; throw; }
        }
        lock (_gate) _released = true;
        return true;
    }

    internal async ValueTask JoinActiveReadAsync()
    {
        Task<CompletionsBodyReadResult>? reading;
        lock (_gate) reading = _reading?.Task;
        if (reading is not null)
            try { await reading.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
    }

}

// The same admitted result supplies both the public DTO and the actual decoder bytes.
// No second executor, read, byte queue or synthetic EOF is used by this count adapter.
internal sealed class CompletionsReaderStream : Stream
{
    private readonly CompletionsBodyReader _reader;
    private readonly Action<CompletionsBodyReadResult>? _observe;
    private readonly CompletionsStartupHandoff? _startup;
    private readonly int _maximumConsecutiveEmptyReads;
    private readonly bool _permitPendingSourceReadRelease;
    internal CompletionsReaderStream(ICompletionsResponseBodyReader executor, int maximumReadBytes,
        Action<CompletionsBodyReadResult>? observe = null, CompletionsStartupHandoff? startup = null, bool permitPendingSourceReadRelease = false)
        : this(new CompletionsBodyReader(executor, maximumReadBytes), observe, startup, 4096, permitPendingSourceReadRelease) { }
    private CompletionsReaderStream(CompletionsBodyReader reader, Action<CompletionsBodyReadResult>? observe,
        CompletionsStartupHandoff? startup, int maximumConsecutiveEmptyReads, bool permitPendingSourceReadRelease)
    { _reader = reader; _observe = observe; _startup = startup; _maximumConsecutiveEmptyReads = maximumConsecutiveEmptyReads; _permitPendingSourceReadRelease = permitPendingSourceReadRelease; }
    internal static CompletionsReaderStream FromResultReader(ICompletionsResponseBodyResultReader executor, int maximumReadBytes,
        int maximumConsecutiveEmptyReads, Action<CompletionsBodyReadResult>? observe, CompletionsStartupHandoff? startup, bool permitPendingSourceReadRelease = false) =>
        new(CompletionsBodyReader.FromResultReader(executor, maximumReadBytes), observe, startup, maximumConsecutiveEmptyReads, permitPendingSourceReadRelease);
    public bool ReachedEof => _reader.ReachedEof;
    public bool Released => _reader.Released;
    public override bool CanRead => !Released;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        var emptyReads = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = _reader.ReadAsync(destination.Length, cancellationToken);
            if (_startup is not null) await _startup.BeforeReadCompletionAsync(cancellationToken).ConfigureAwait(false);
            var result = await operation.ConfigureAwait(false);
            _observe?.Invoke(result);
            if (result.Done) return 0;
            if (!result.Value.IsEmpty)
            { result.Value.AsSpan().CopyTo(destination.Span); return result.Value.Length; }
            // A real nonterminal empty result must not flush UTF-8 or dispatch
            // pending SSE data. Bound producer work before requesting another one.
            if (++emptyReads > _maximumConsecutiveEmptyReads)
                throw new StreamLimitException("Completions consecutive empty reads exceed configured limits.");
        }
    }
    public ValueTask CancelAsync() => _reader.CancelAsync();
    public void Release() => _reader.Release();
    internal bool TryReleaseIndependent() => _reader.TryReleaseIndependent(_permitPendingSourceReadRelease);
    internal ValueTask JoinActiveReadAsync() => _reader.JoinActiveReadAsync();
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
