// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/native.ts
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Host;

internal interface INativeSlot
{
    Task OpenAsync(ChordContext context);
    Task CloseAsync(NativeCloseReason reason);
}

internal sealed class NativeHandle<T>(Attempt attempt, int position, Func<ChordContext, Task<T>> open,
    Func<T, NativeCloseReason, ChordContext, Task> close, NativeOptions? options) : INative<T>, INativeSlot
{
    private string state = "unopened";
    private T value = default!;
    private NativeCloseReason reason;
    // A successfully opened resource may need its defining generation again when it eventually closes.
    // Keep that generation continuously, including an abandoned open and a close past its public deadline.
    private IDisposable? lifetime = attempt.Owner.Host.RetainCallbacks(attempt.Served.DeclaringFacet, open, close);
    private void ReleaseLifetime() => Interlocked.Exchange(ref lifetime, null)?.Dispose();
    public T Value => attempt.Owner.Host.Dispatcher.Invoke(() => state == "open" ? value
        : throw (state is "unopened" or "opening" ? HostErrors.NotReady($"native {position}") : new ChordError("released", "native closed", "closed")));
    public async Task OpenAsync(ChordContext context)
    {
        if (state != "unopened") return;
        state = "opening";
        T result;
        try { result = await attempt.Owner.Host.InvokeCallbackAsync(open, () => open(context), attempt.Served.DeclaringFacet); }
        catch { ReleaseLifetime(); throw; }
        if (state == "abandoned")
        {
            state = "closed";
            try { await CloseValue(result, reason); } catch (Exception error) { attempt.Owner.Host.Report(new("close_failed", error, attempt.Owner.Label, Position: position)); }
            return;
        }
        value = result; state = "open";
    }
    public Task CloseAsync(NativeCloseReason closing)
    {
        if (state is "closed" or "abandoned") return Task.CompletedTask;
        reason = closing;
        if (state == "opening") { state = "abandoned"; return Task.CompletedTask; }
        if (state == "unopened") { state = "closed"; ReleaseLifetime(); return Task.CompletedTask; }
        state = "closed"; var current = value; value = default!;
        return CloseValue(current, closing);
    }
    private async Task CloseValue(T current, NativeCloseReason closing)
    {
        var host = attempt.Owner.Host;
        var cancelled = new CancellationTokenSource();
        var context = ChordContext.WithAbortSignal(cancelled.Token, ChordContext.WithoutAbortSignal(attempt.Owner.Context));
        var original = CloseOriginal();
        try { await original.WaitAsync(options?.CloseTimeout ?? host.Options.CloseTimeout, host.Options.TimeProvider); }
        catch (TimeoutException)
        {
            try { cancelled.Cancel(); } catch (Exception error) { host.Report(new("cancellation_failed", error, attempt.Owner.Label)); }
            host.Report(new("close_timeout", Object: attempt.Owner.Label, Position: position));
        }
        catch (Exception error) { host.Report(new("close_failed", error, attempt.Owner.Label, Position: position)); throw; }
        async Task CloseOriginal()
        {
            try { await host.InvokeCallbackAsync(close, () => close(current, closing, context), attempt.Served.DeclaringFacet); }
            finally { cancelled.Dispose(); ReleaseLifetime(); }
        }
    }
}
