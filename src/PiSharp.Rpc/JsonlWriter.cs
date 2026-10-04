using PiSharp.Contracts;

namespace PiSharp.Rpc;

/// <summary>Bounded pending calls, one awaited complete write/flush at a time, no encoded payload queue.</summary>
public sealed class JsonlWriter : IAsyncDisposable
{
    private readonly Stream _output;
    private readonly JsonlTransportOptions _options;
    private readonly JsonlStreamOwnership _ownership;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly object _sync = new();
    private int _pending; private TaskCompletionSource? _idle; private Task? _cleanup;

    public JsonlWriter(Stream output, JsonlTransportOptions? options = null, JsonlStreamOwnership ownership = JsonlStreamOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(output); _options = options ?? new(); _options.Validate(ownership);
        if (!output.CanWrite) throw new ArgumentException("JSONL output must be writable.", nameof(output));
        _output = output; _ownership = ownership; _stopToken = _stop.Token;
    }

    public async ValueTask WriteAsync(JsonData record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record); cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_cleanup is not null, this);
            if (_pending >= _options.MaximumPendingWrites) throw new JsonlTransportException(JsonlTransportFailure.PendingWriteLimit);
            if (_pending++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var acquired = false; var writing = false; var terminal = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopToken);
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false); acquired = true;
            linked.Token.ThrowIfCancellationRequested();
            var bytes = JsonlRecordCodec.Encode(record, _options);
            linked.Token.ThrowIfCancellationRequested(); writing = true;
            await _output.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await _output.FlushAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            if (writing) { terminal = true; _ = DisposeAsync(); }
            throw;
        }
        finally
        {
            if (acquired) _gate.Release();
            lock (_sync) { if (--_pending == 0) _idle!.TrySetResult(); }
            if (terminal) await DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Task idle;
        lock (_sync)
        {
            if (_cleanup is not null) return new(_cleanup);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _cleanup = completion.Task;
            idle = _pending == 0 ? Task.CompletedTask : _idle!.Task;
        }
        _ = Cleanup(completion, idle); return new(completion.Task);
    }
    private async Task Cleanup(TaskCompletionSource completion, Task idle)
    {
        Exception? failure = null;
        try
        {
            try { _stop.Cancel(); } catch (Exception error) { failure = error; }
            await idle.ConfigureAwait(false);
            try { if (_ownership == JsonlStreamOwnership.Owned) await _output.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
        }
        catch (Exception error) { completion.SetException(error); }
        finally { _gate.Dispose(); _stop.Dispose(); }
    }
}
