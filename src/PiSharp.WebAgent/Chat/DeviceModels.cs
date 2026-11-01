// Decision 0011 (run W4, fix 2): the hook an area uses to wrap the models the app's durable device streams through, and only the device's.
// DeviceHostService opens its host over the app's IDurableModels wrapped by every registered IDeviceModelsDecorator, in registration order
// (the first registered is the innermost); the IDurableModels registration itself is never replaced, so the run engine and everything else
// keep the plain models. Agent compare registers its gate this way (AgentCompareModels) instead of replacing DeviceHostService's
// registration. A decorator is built once, when DeviceHostService is made.
using PiSharp.Durable;

namespace PiSharp.WebAgent.Chat;

/// <summary>Wraps the models the app's durable device streams through. Register one with
/// <see cref="DeviceModels.AddDeviceModelsDecorator"/>.</summary>
public interface IDeviceModelsDecorator
{
    /// <summary>The models the device streams through over <paramref name="models"/> (the app's, or an earlier decorator's).</summary>
    IDurableModels Decorate(IDurableModels models);
}

/// <summary>Registration of <see cref="IDeviceModelsDecorator"/>s.</summary>
public static class DeviceModels
{
    /// <summary>Adds a decorator of the device's models: <paramref name="decorate"/> gets the services and the models underneath and returns
    /// the models the device streams through. Decorators apply in registration order (the first registered is the innermost).</summary>
    public static IServiceCollection AddDeviceModelsDecorator(this IServiceCollection services, Func<IServiceProvider, IDurableModels, IDurableModels> decorate)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);
        services.AddSingleton<IDeviceModelsDecorator>(provider => new Decorator(provider, decorate));
        return services;
    }

    /// <summary>The device's models: <paramref name="models"/> under every decorator, the first one innermost.</summary>
    public static IDurableModels Apply(IDurableModels models, IEnumerable<IDeviceModelsDecorator>? decorators)
    {
        ArgumentNullException.ThrowIfNull(models);
        var current = models;
        foreach (var decorator in decorators ?? [])
            current = decorator.Decorate(current) ?? throw new InvalidOperationException($"{decorator.GetType().Name} returned no models for the device.");
        return current;
    }

    private sealed class Decorator(IServiceProvider provider, Func<IServiceProvider, IDurableModels, IDurableModels> decorate) : IDeviceModelsDecorator
    {
        public IDurableModels Decorate(IDurableModels models) => decorate(provider, models);
    }
}
