namespace PiSharp.Extensions.Runtime;

public sealed partial class RegistrationScope
{
    /// <summary>Shared registry bus. The optional async facade retains callbacks through owner settlement.</summary>
    public IExtensionEventBus Events => new OwnerEventBus(this);

    private sealed class OwnerEventBus(RegistrationScope scope) : IAsyncExtensionEventBus
    {
        public void Emit(string channel, object? data) => scope.Registry.EmitEventBus(scope, channel, data);

        public IDisposable On(string channel, Action<object?> handler) =>
            Own((IExtensionEventBusSubscription)scope.Registry.SubscribeEventBus(scope, channel, handler));

        public IExtensionEventBusSubscription OnAsync(string channel, Func<object?, Task> handler) =>
            Own(scope.Registry.SubscribeEventBusAsync(scope, channel, handler));

        public IExtensionEventBusSubscription OnValueTask(string channel, Func<object?, ValueTask> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return OnAsync(channel, data => { var original = handler(data); return original.AsTask(); });
        }

        public ValueTask DrainAsync() => scope.Registry.DrainEventBus(scope);

        private OwnedSubscription Own(IExtensionEventBusSubscription subscription)
        {
            var owned = new OwnedSubscription(scope, subscription);
            owned.Attach(scope.ExtensionLifetimeCancellationToken);
            return owned;
        }
    }

    private sealed class OwnedSubscription(RegistrationScope scope, IExtensionEventBusSubscription original)
        : IExtensionEventBusSubscription
    {
        private readonly object gate = new();
        private bool retired;
        private CancellationTokenRegistration cancellation;

        internal void Attach(CancellationToken lifetime)
        {
            var registration = lifetime.Register(static state => ((OwnedSubscription)state!).Dispose(), this);
            bool alreadyRetired;
            lock (gate)
            {
                alreadyRetired = retired;
                if (!alreadyRetired) cancellation = registration;
            }
            if (alreadyRetired) registration.Dispose();
        }

        public void Dispose()
        {
            CancellationTokenRegistration registration;
            lock (gate)
            {
                if (retired) return;
                retired = true;
                registration = cancellation;
                cancellation = default;
            }
            original.Dispose();
            registration.Dispose();
        }

        public ValueTask DrainAsync()
        {
            scope.Registry.GuardEventBusSettlement(scope);
            return original.DrainAsync();
        }

        public ValueTask DisposeAsync()
        {
            // Reject lifecycle reentry before retiring either native ownership or bus membership.
            scope.Registry.GuardEventBusSettlement(scope);
            Dispose();
            return original.DrainAsync();
        }
    }
}