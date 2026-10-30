// Decision 0011 (run W3, S3): the Guard unit's registration: its webagent.db migrations (1100-1101), SpendApprovalStore, SpendGuard as the
// app's ISpendGuard, GuardedRunExecutor (W4's seam for Playground, Compare and Bench), and the IDurableModels decorator. AddSpendArea calls
// it after the core's fallbacks and the priced faux switch, so the decorator wraps whatever IDurableModels the app ends with (faux, priced
// faux or routed): every registration of IDurableModels is removed and the last one (the one the app resolves) becomes the guard's inner
// models, with its lifetime kept. The decorator resolves its executor on the first request, never at construction, so the estimator can
// read the decorated models (catalog JSON, base URLs) without a cycle. Registering twice decorates once. MapGuard maps /api/spend's
// preflight, approvals and stops (SpendGuardEndpoints).
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Spend;

public static partial class SpendModule
{
    /// <summary>Registers the Guard unit: its migrations, <see cref="SpendApprovalStore"/>, <see cref="SpendGuard"/> as
    /// <see cref="ISpendGuard"/>, <see cref="GuardedRunExecutor"/>, and <see cref="GuardedDurableModels"/> over the last registered
    /// <see cref="IDurableModels"/>. Needs <see cref="WebAgentDb"/>, <see cref="ICostEstimator"/> and <see cref="ISpendLedger"/> (resolved on
    /// use). <see cref="AddSpendArea"/> calls it; call it after every other <see cref="IDurableModels"/> registration.</summary>
    public static IServiceCollection AddSpendGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IWebAgentMigration) && ReferenceEquals(descriptor.ImplementationInstance, SpendGuardMigrations.Approvals)))
            foreach (var migration in SpendGuardMigrations.All) services.AddWebAgentMigration(migration);
        services.TryAddSingleton(provider => new SpendApprovalStore(provider.GetRequiredService<WebAgentDb>(), provider.GetService<TimeProvider>()));
        services.TryAddSingleton(provider => new SpendGuard(provider.GetRequiredService<ICostEstimator>(), provider.GetRequiredService<ISpendLedger>(),
            provider.GetRequiredService<SpendApprovalStore>(), provider.GetService<TimeProvider>(), provider.GetService<ILogger<SpendGuard>>()));
        services.TryAddSingleton<ISpendGuard>(provider => provider.GetRequiredService<SpendGuard>());
        services.TryAddSingleton(provider => new GuardedRunExecutor(provider.GetRequiredService<SpendGuard>(), provider.GetService<ILogger<GuardedRunExecutor>>()));
        DecorateModels(services);
        return services;
    }

    static partial void AddGuard(IServiceCollection services, WebAgentOptions options) => services.AddSpendGuard();

    static partial void MapGuard(WebApplication app) => app.MapSpendGuardEndpoints();

    /// <summary>Replaces every <see cref="IDurableModels"/> registration with the guard over the last one; nothing when there is none, or when
    /// the last one is already the guard.</summary>
    private static void DecorateModels(IServiceCollection services)
    {
        var registered = services.Where(descriptor => descriptor.ServiceType == typeof(IDurableModels) && !descriptor.IsKeyedService).ToList();
        if (registered.Count == 0 || registered[^1].ImplementationFactory?.Target is GuardedModelsFactory) return;
        var factory = new GuardedModelsFactory(registered[^1]);
        foreach (var descriptor in registered) services.Remove(descriptor);
        services.Add(new ServiceDescriptor(typeof(IDurableModels), factory.Create, registered[^1].Lifetime));
    }

    /// <summary>Builds the guard over one registration of the inner models.</summary>
    private sealed class GuardedModelsFactory(ServiceDescriptor inner)
    {
        public object Create(IServiceProvider provider)
        {
            var models = inner switch
            {
                { ImplementationInstance: IDurableModels instance } => instance,
                { ImplementationFactory: { } create } => (IDurableModels)create(provider),
                { ImplementationType: { } type } => (IDurableModels)ActivatorUtilities.CreateInstance(provider, type),
                _ => throw new InvalidOperationException("The IDurableModels registration names no implementation."),
            };
            return new GuardedDurableModels(models, provider.GetRequiredService<GuardedRunExecutor>);
        }
    }
}
