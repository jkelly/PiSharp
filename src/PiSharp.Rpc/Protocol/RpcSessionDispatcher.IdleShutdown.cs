using System.Runtime.CompilerServices;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private bool _shutdownWhenIdle;
    private bool _inputReady;
    private Task<bool>? _inputRead;
    private Task? _idleShutdownWait;

    private async IAsyncEnumerable<JsonlFrameAdmission> ReadAttachedInputAsync(JsonlReader input,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using var iterator = input.ReadAdmissionsAsync(token).GetAsyncEnumerator();
        token.ThrowIfCancellationRequested();
        // MoveNext starts the reader's activity before an idle shutdown may dispose it.
        var pending = BeginRead();
        while (await pending.ConfigureAwait(false))
        {
            yield return iterator.Current;
            // Shutdown may have disposed the suspended reader while handling this record.
            token.ThrowIfCancellationRequested();
            pending = BeginRead();
        }

        Task<bool> BeginRead()
        {
            var read = iterator.MoveNextAsync().AsTask();
            lock (_gate) { _inputRead = read; _inputReady = true; }
            ScheduleIdleShutdownCheck();
            return read;
        }
    }

    /// <summary>Requests graceful shutdown after startup, every accepted command response and the
    /// active run settle. Safe in extension callbacks: never joins the requesting callback.</summary>
    public void RequestShutdownWhenIdle()
    {
        lock (_gate)
        {
            if (_closed) return;
            _shutdownWhenIdle = true;
        }
        ScheduleIdleShutdownCheck();
    }

    private void ScheduleIdleShutdownCheck()
    {
        lock (_gate) if (!_shutdownWhenIdle || _closed) return;
        // No extension/session AsyncLocal scope may flow into disposal: a timer can retain
        // precisely the context whose originals shutdown needs to join.
        ThreadPool.UnsafeQueueUserWorkItem(static (RpcSessionDispatcher owner) =>
        {
            try
            {
                lock (owner._gate)
                {
                    if (!owner._shutdownWhenIdle || owner._closed || !owner._inputReady ||
                        !owner._startupReady.Task.IsCompletedSuccessfully || owner._pending != 0 ||
                        owner._run is not null || owner._activePostOrigins != 0) return;
                    // A completed read may hold a decoded frame whose command has not been
                    // admitted yet. Let the input loop account for it before claiming idle.
                    // A completed EOF/fault is settled by the input loop's existing shutdown.
                    if (owner._inputRead?.IsCompleted != false) return;
                    // An extension can own compaction or another session operation outside a
                    // dispatcher command/run. Wait for that actual original too, without aborting it.
                    var sessionIdle = owner._session.WaitForIdleAsync();
                    if (!sessionIdle.IsCompletedSuccessfully)
                    {
                        owner._idleShutdownWait ??= owner.WaitForSessionIdleAsync(sessionIdle);
                        return;
                    }
                    // Admission and closing share the gate, so a new command cannot slip between
                    // the idle check and shutdown. BeginShutdown retains and owns its original task.
                    _ = owner.BeginShutdown();
                }
            }
            catch (Exception error) { owner.SignalFatal(RpcDispatchFailure.SessionRunFailed, error); }
        }, this, preferLocal: false);
    }

    private async Task WaitForSessionIdleAsync(Task idle)
    {
        await Task.Yield(); // Publish this owned task before a synchronous failure can close the host.
        try { await idle.ConfigureAwait(false); }
        catch (Exception error) { SignalFatal(RpcDispatchFailure.SessionRunFailed, error); }
        finally
        {
            lock (_gate) _idleShutdownWait = null;
            ScheduleIdleShutdownCheck();
        }
    }
}
