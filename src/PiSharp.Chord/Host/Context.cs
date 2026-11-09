// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/{ctx,root,sleep}.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using System.Runtime.CompilerServices;

namespace PiSharp.Chord.Host;

public sealed class CallContext : IDisposable
{
    private readonly CancellationTokenSource cancellation;
    private readonly CancellationToken signal;
    private readonly List<Binding> references = [];
    private bool settled;
    private readonly ScopeStamp stamp;
    private static readonly ConditionalWeakTable<ChordContext, ScopeStamp> Scopes = new();
    internal ChordHost Host { get; }
    internal Caller Caller { get; }
    internal CallContext(ChordHost host, Caller caller, ChordContext context, CancellationToken? lifetime = null)
    {
        Host = host; Caller = caller;
        cancellation = lifetime is { } token
            ? CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None, token)
            : CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None);
        signal = cancellation.Token;
        Context = ChordContext.WithAbortSignal(signal, ChordContext.WithoutAbortSignal(context));
        stamp = new(host.Incarnation); Scopes.Add(Context, stamp);
    }
    public ChordContext Context { get; }
    public JsonObject? Principal => Caller.Principal;
    public string Incarnation => Caller.Incarnation;
    public CancellationToken AbortSignal => signal;
    public Task<IReference> GetAsync(Contract type, JsonObject parameters, GetOptions? options = null) => Host.Dispatcher.RunAsync(async () =>
    {
        if (settled) throw new Data.ChordError("released", "ctx.get after its call settled", "settled");
        AbortSignal.ThrowIfCancellationRequested();
        var reference = Host.Acquire(type, parameters, Caller, Context, options);
        references.Add(reference);
        try { await reference.ReadyAsync(Context); return (IReference)reference; }
        catch { references.Remove(reference); reference.Release(); throw; }
    });
    public void Dispose() => Host.Dispatcher.Invoke(() =>
    {
        if (settled) return;
        settled = true; stamp.Settled = true;
        try { cancellation.Cancel(); } catch (Exception error) { Host.Report(new("cancellation_failed", error)); }
        foreach (var reference in references) reference.Release();
        references.Clear(); cancellation.Dispose();
    });
    internal static void Validate(ChordHost host, ChordContext context)
    {
        if (!Scopes.TryGetValue(context, out var scope)) return;
        if (scope.Host != host.Incarnation) throw Data.DataInternal.Invalid("foreign_ctx", "a call context belongs to another host");
        if (scope.Settled) throw new Data.ChordError("released", "call context settled", "settled");
    }
    private sealed class ScopeStamp(string host) { internal string Host => host; internal bool Settled; }
}

public static class HostTiming
{
    public static Task SleepAsync(TimeSpan delay, ChordContext context, TimeProvider? timeProvider = null) =>
        Task.Delay(delay, timeProvider ?? TimeProvider.System, context.AbortSignal ?? CancellationToken.None);
    public static Task UntilAbortedAsync(ChordContext context) => Task.Delay(Timeout.InfiniteTimeSpan, context.AbortSignal ?? CancellationToken.None);
}
