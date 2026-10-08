using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Interactive;

/// <summary>Persistent actual terminal tool rows. Callback UI leases are never retained as signal
/// authority: every paint has the original live registry marker and a fresh callback context.</summary>
internal sealed class TerminalToolComponentUiBroker(TerminalSessionView view, Func<long> captureGeneration)
{
    private readonly Func<long> currentGeneration = captureGeneration;
    private readonly object gate = new();
    private readonly Dictionary<ExtensionCustomComponentIdentity, Presentation> rows = [];

    internal IExtensionToolComponentPresentation Attach(IExtensionContext context, ExtensionCustomComponentCallbacks source)
    {
        var identity = source.Identity;
        if (identity.OwnerId != context.OwnerId || identity.OwnerGeneration != context.OwnerGeneration ||
            identity.SessionGeneration != currentGeneration()) throw new InvalidOperationException("Stale native tool attachment identity.");
        var row = new Presentation(this, source);
        lock (gate)
        {
            if (rows.Count >= 16 || !rows.TryAdd(identity, row)) throw new InvalidOperationException("Native tool row identity/capacity differs.");
        }
        try
        {
            row.Participant = ExtensionRegistry.BindCustomComponentContext(context, identity.ComponentId,
                identity.SessionGeneration, currentGeneration, () => Retire(row), fresh => DisposeSource(row, fresh),
                context.SessionCancellationToken);
            row.View = new(new(identity.OwnerId, identity.OwnerGeneration, identity.SessionGeneration, identity.ComponentId,
                (width, token) => Render(row, width, token)), () => row.IsCurrent);
            return row;
        }
        catch
        {
            lock (gate) rows.Remove(identity);
            throw;
        }
    }
    private Task Retire(Presentation row)
    {
        lock (gate) row.Retiring = true;
        // Clear is a real queued view/write original. It never joins its enclosing source callback.
        var original = row.View is null ? Task.CompletedTask : view.CloseToolComponentAsync(row.View, CancellationToken.None).AsTask();
        lock (gate) row.Originals.Add(original);
        return original;
    }
    private async Task DisposeSource(Presentation row, IExtensionContext context)
    {
        var faults = new List<Exception>(); Task? source = null;
        try { source = row.Source.Dispose(context) ?? throw new InvalidOperationException("Missing original tool component disposal."); await source.ConfigureAwait(false); }
        catch (Exception error) { faults.Add(source?.Exception ?? error); }
        if (source is not null) lock (gate) row.Originals.Add(source);
        Task[] originals; lock (gate) originals = row.Originals.ToArray();
        foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
            try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(original.Exception ?? error); }
        lock (gate) rows.Remove(row.Identity);
        if (faults.Count != 0) throw new AggregateException("Tool source/presentation originals failed.", faults);
    }
    private Task<TerminalCustomComponentRows> Render(Presentation row, int width, CancellationToken token)
    {
        var frame = PaintFrame.Current.Value;
        if (frame is { Active: true } && ReferenceEquals(frame.Row, row))
            return RenderOriginal(row, width, frame.Context, token);
        return row.Participant.InvokeAsync((context, actual) => RenderOriginal(row, width, context, actual), token);
    }
    private async Task<TerminalCustomComponentRows> RenderOriginal(Presentation row, int width, IExtensionContext context, CancellationToken token)
    {
        Task<ExtensionCustomComponentRows>? original = null;
        try
        {
            original = row.Source.Render(width, context, token) ?? throw new InvalidOperationException("Missing original tool render.");
            var projection = await original.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(projection.Rows, projection.CellWidths);
        }
        catch (Exception error) { throw new AggregateException("Original tool render failed.", original?.Exception ?? error); }
    }
    private Task Paint(Presentation row, bool initial, CancellationToken token)
    {
        lock (gate)
        {
            if (!row.IsCurrent || row.View is null || row.Originals.Count >= 960)
                throw new InvalidOperationException("Retired/exhausted native tool row.");
            var original = row.Participant.InvokeAsync(async (context, actual) =>
            {
                using var frame = new PaintFrame(row, context);
                Task? physical = null;
                try
                {
                    physical = (initial ? view.OpenToolComponentAsync(row.View, actual) : view.RefreshToolComponentAsync(row.View, actual)).AsTask();
                    await physical.ConfigureAwait(false); return true;
                }
                catch (Exception error) { throw new AggregateException("Native tool view/write original failed.", physical?.Exception ?? error); }
            }, token);
            row.Originals.Add(original);
            // Source invalidation returns a signal acknowledgement. Observe the same queued original
            // now and keep its full graph for actual participant disposal; no detached paint fault.
            var observation = original.ContinueWith(completed =>
            {
                if (completed.IsFaulted) { var full = completed.Exception!; lock (gate) row.Faults.Add(full); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            row.Originals.Add(observation);
            return original;
        }
    }
    private sealed class Presentation(TerminalToolComponentUiBroker owner, ExtensionCustomComponentCallbacks source) : IExtensionToolComponentPresentation
    {
        internal ExtensionCustomComponentCallbacks Source { get; } = source;
        public ExtensionCustomComponentIdentity Identity => Source.Identity;
        internal ExtensionRegistry.RegisteredExtensionComponent Participant = null!;
        internal TerminalToolComponentPresentation? View;
        internal bool Retiring;
        internal bool IsCurrent => !Retiring && Participant is not null && !Participant.IsRetired &&
            Identity.SessionGeneration == owner.currentGeneration();
        internal readonly List<Task> Originals = [];
        internal readonly List<Exception> Faults = [];
        private Task? show;
        public Task ShowAsync(CancellationToken token = default)
        { lock (owner.gate) return show ??= owner.Paint(this, true, token); }
        public Task InvalidateAsync(CancellationToken token = default)
        {
            lock (owner.gate)
            {
                if (show is null) throw new InvalidOperationException("Tool attachment has not acquired its initial paint.");
                _ = owner.Paint(this, false, token);
                return Task.CompletedTask;
            }
        }
        public ValueTask DisposeAsync() => Participant.DisposeAsync();
        public Task JoinPaintsAsync()
        {
            Participant.AssertExternalObservation();
            Task[] originals; lock (owner.gate) originals = Originals.ToArray();
            return Join(originals);
        }
        private static async Task Join(Task[] originals)
        {
            var errors = new List<Exception>();
            foreach (var original in new HashSet<Task>(originals, ReferenceEqualityComparer.Instance))
                try { await original.ConfigureAwait(false); } catch (Exception error) { errors.Add(original.Exception ?? error); }
            if (errors.Count != 0) throw new AggregateException("Actual tool paint originals failed.", errors);
        }
    }
    private sealed class PaintFrame : IDisposable
    {
        internal static readonly AsyncLocal<PaintFrame?> Current = new();
        private readonly PaintFrame? parent = Current.Value;
        internal readonly Presentation Row;
        internal readonly IExtensionContext Context;
        private int active = 1; internal bool Active => Volatile.Read(ref active) != 0;
        internal PaintFrame(Presentation row, IExtensionContext context) { Row = row; Context = context; Current.Value = this; }
        public void Dispose() { Volatile.Write(ref active, 0); Current.Value = parent; }
    }
}
