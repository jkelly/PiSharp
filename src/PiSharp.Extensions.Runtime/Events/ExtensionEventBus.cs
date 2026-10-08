using System.Runtime.ExceptionServices;

namespace PiSharp.Extensions.Runtime;

/// <summary>Ordered synchronous emission with owned asynchronous listener settlement.
/// Callback failures are diagnosed; a missing error-channel listener remains an emitting error.</summary>
public sealed class ExtensionEventBus : IAsyncExtensionEventBus
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<Subscription>> listeners = new(StringComparer.Ordinal);
    private readonly HashSet<Delivery> pending = [];
    private readonly Action<string, Exception> reportError;

    public ExtensionEventBus(Action<string, Exception>? reportError = null) =>
        this.reportError = reportError ?? ((channel, error) => Console.Error.WriteLine($"Event handler error ({channel}): {error}"));

    public IDisposable On(string channel, Action<object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return OnAsync(channel, data => { handler(data); return Task.CompletedTask; });
    }

    public IExtensionEventBusSubscription OnAsync(string channel, Func<object?, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, channel, handler);
        lock (gate)
        {
            if (!listeners.TryGetValue(channel, out var entries)) listeners.Add(channel, entries = []);
            entries.Add(subscription);
        }
        return subscription;
    }

    public IExtensionEventBusSubscription OnValueTask(string channel, Func<object?, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Capture and materialize exactly once, including IValueTaskSource-backed originals.
        return OnAsync(channel, data => { var original = handler(data); return original.AsTask(); });
    }

    public void Emit(string channel, object? data)
    {
        ArgumentNullException.ThrowIfNull(channel);
        Subscription[] captured;
        lock (gate) captured = listeners.TryGetValue(channel, out var entries) ? entries.ToArray() : [];
        if (captured.Length == 0 && channel == "error")
        {
            if (data is Exception error) ExceptionDispatchInfo.Capture(error).Throw();
            throw new ExtensionEventBusUnhandledErrorException(data);
        }
        // Membership is captured by Node EventEmitter; standalone unsubscribe during Emit
        // does not suppress another listener in this snapshot. Owner admission is separate.
        foreach (var subscription in captured)
        {
            var delivery = new Delivery(subscription);
            lock (gate) pending.Add(delivery);
            // Every observer is retained before invocation and supplied to all admitted drains.
            // DeliverAsync invokes the listener immediately and directly joins its one original.
            var work = DeliverAsync(delivery, data);
            delivery.Started.SetResult(work);
        }
    }

    public void Clear()
    {
        lock (gate) listeners.Clear(); // Pending originals remain owned and drainable.
    }

    public ValueTask DrainAsync()
    {
        if (DeliveryFrame.IsExecuting(this)) throw new InvalidOperationException("Event bus drain from its callback would deadlock.");
        Delivery[] captured;
        lock (gate) captured = pending.ToArray();
        return JoinAsync(captured);
    }

    private async Task DeliverAsync(Delivery delivery, object? data)
    {
        using var frame = new DeliveryFrame(this, delivery.Subscription);
        Task? original = null;
        try
        {
            original = delivery.Subscription.Handler(data) ?? throw new InvalidOperationException("Event listener returned no original Task.");
            await original.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Retain the original aggregate object and every nested fault for Task failures.
            // Cancellation has no Task.Exception: preserve the exception caught from its direct await.
            ReportFailure(delivery.Subscription.Channel, original?.Exception ?? error);
        }
        finally { lock (gate) pending.Remove(delivery); }
    }

    internal void ReportFailure(string channel, Exception error)
    {
        try { reportError(channel, error); }
        catch { /* Diagnostic failure is isolated, synchronously completed, and never detached. */ }
    }

    private static async ValueTask JoinAsync(Delivery[] deliveries)
    {
        foreach (var delivery in deliveries)
        {
            var originalObserver = await delivery.Started.Task.ConfigureAwait(false);
            await originalObserver.ConfigureAwait(false);
        }
    }

    private ValueTask DrainSubscription(Subscription subscription, bool retire)
    {
        if (DeliveryFrame.IsExecuting(subscription))
            throw new InvalidOperationException("Subscription settlement from its callback would deadlock.");
        Delivery[] captured;
        lock (gate)
        {
            if (retire) Remove(subscription);
            captured = pending.Where(item => ReferenceEquals(item.Subscription, subscription)).ToArray();
        }
        return JoinAsync(captured);
    }

    private void Remove(Subscription subscription)
    {
        lock (gate)
        {
            if (!listeners.TryGetValue(subscription.Channel, out var entries)) return;
            entries.Remove(subscription);
            if (entries.Count == 0) listeners.Remove(subscription.Channel);
        }
    }

    private sealed class Delivery(Subscription subscription)
    {
        internal Subscription Subscription { get; } = subscription;
        internal TaskCompletionSource<Task> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription(ExtensionEventBus bus, string channel, Func<object?, Task> handler)
        : IExtensionEventBusSubscription
    {
        internal string Channel { get; } = channel;
        internal Func<object?, Task> Handler { get; } = handler;
        public void Dispose() => bus.Remove(this);
        public ValueTask DrainAsync() => bus.DrainSubscription(this, retire: false);
        public ValueTask DisposeAsync() => bus.DrainSubscription(this, retire: true);
    }

    private sealed class DeliveryFrame : IDisposable
    {
        private static readonly AsyncLocal<DeliveryFrame?> current = new();
        private readonly DeliveryFrame? parent;
        private readonly ExtensionEventBus bus;
        private readonly Subscription subscription;
        private bool active = true;
        internal DeliveryFrame(ExtensionEventBus bus, Subscription subscription)
        {
            this.bus = bus; this.subscription = subscription; parent = current.Value; current.Value = this;
        }
        private static IEnumerable<DeliveryFrame> Frames()
        {
            for (var frame = current.Value; frame is not null; frame = frame.parent)
                if (Volatile.Read(ref frame.active)) yield return frame;
        }
        internal static bool IsExecuting(ExtensionEventBus bus) => Frames().Any(frame => ReferenceEquals(frame.bus, bus));
        internal static bool IsExecuting(Subscription subscription) => Frames().Any(frame => ReferenceEquals(frame.subscription, subscription));
        public void Dispose() { Volatile.Write(ref active, false); current.Value = parent; }
    }
}