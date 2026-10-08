namespace PiSharp.Extensions;

/// <summary>Shared extension communication. Emit invokes the ordered listener snapshot synchronously.</summary>
public interface IExtensionEventBus
{
    void Emit(string channel, object? data);
    IDisposable On(string channel, Action<object?> handler);
}

/// <summary>Optional native Promise mapping. Emit still returns immediately; admitted listener work is owned
/// until settlement. Drain joins the deliveries already admitted at its call; later emissions are excluded.</summary>
public interface IAsyncExtensionEventBus : IExtensionEventBus
{
    IExtensionEventBusSubscription OnAsync(string channel, Func<object?, Task> handler);
    IExtensionEventBusSubscription OnValueTask(string channel, Func<object?, ValueTask> handler);
    ValueTask DrainAsync();
}

/// <summary>Dispose retires future membership. DisposeAsync also joins already admitted deliveries.
/// Draining or asynchronously disposing from this subscription's own callback is rejected before retirement.</summary>
public interface IExtensionEventBusSubscription : IDisposable, IAsyncDisposable
{
    ValueTask DrainAsync();
}

public interface IExtensionEventBusRegistry : IExtensionRegistry
{
    IExtensionEventBus Events { get; }
}

/// <summary>Native equivalent of an unhandled EventEmitter error whose payload is not an Exception.</summary>
public sealed class ExtensionEventBusUnhandledErrorException(object? payload)
    : Exception("Unhandled event bus error")
{
    public object? Payload { get; } = payload;
}