using System.Threading.Tasks.Sources;

namespace PiSharp.AI.Protocols.OpenAICompletions;

// A source invocation can prepare/acquire synchronously, but a completed native
// body read must not consume the whole response before its awaiting caller owns
// the run. Hand the actual StartAsync continuation the run, then resume that read.
// This is one invocation-owned handoff, not a delay or a consumer-read dependency.
internal sealed class CompletionsStartupHandoff : IValueTaskSource<CompletionsRun>
{
    private readonly TaskCompletionSource _resume = new();
    private CompletionsRun? _run;
    private int _registered, _delivering, _consumed, _readClaimed;

    public ValueTask<CompletionsRun> Bind(CompletionsRun run)
    {
        _run = run;
        return new(this, 0);
    }

    internal async ValueTask BeforeReadCompletionAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _readClaimed, 1) != 0) return;
        await AwaitDeliveryAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask AwaitDeliveryAsync(CancellationToken token)
    {
        // Cancellation releases this handoff, then the already admitted read is
        // still awaited/observed. It never abandons a faulted or closed read DTO.
        using var registration = token.UnsafeRegister(static state =>
            ((TaskCompletionSource)state!).TrySetResult(), _resume);
        await _resume.Task.ConfigureAwait(false);
    }

    public ValueTaskSourceStatus GetStatus(short token)
    {
        Validate(token);
        return Volatile.Read(ref _delivering) == 0 ? ValueTaskSourceStatus.Pending : ValueTaskSourceStatus.Succeeded;
    }

    public CompletionsRun GetResult(short token)
    {
        Validate(token);
        if (Volatile.Read(ref _delivering) == 0 || Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException("Await the Completions start operation once.");
        return _run!;
    }

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        Validate(token);
        if (Interlocked.Exchange(ref _registered, 1) != 0)
            throw new InvalidOperationException("Await the Completions start operation once.");
        // Registration is on the awaiting caller's own execution/scheduling context;
        // invoking there preserves both flags without borrowing a worker context.
        Volatile.Write(ref _delivering, 1);
        try { continuation(state); }
        finally { _resume.TrySetResult(); }
    }

    private void Validate(short token)
    {
        if (token != 0 || _run is null) throw new InvalidOperationException("Invalid Completions start operation.");
    }
}
