// Decision 0011 (run W3, S1): the Estimation unit's part of the Spend area: ModelPrices, BillingModes (IBillingModes) and CostEstimator
// (ICostEstimator), singletons, each a TryAdd so a test or another host can put its own first. They read the final IModelCatalog and
// ISettingsStore; IDurableModels (model JSON for price tiers and base URLs for loopback detection) and ISpendLedger (the typical cost; S2's,
// absent until it is registered) are resolved on use, never at construction, so the Guard unit's IDurableModels decorator can depend on
// ICostEstimator without a cycle. The unit maps no endpoint: preflight is the Guard's, /api/spend the Pages'.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Spend;

public static partial class SpendModule
{
    /// <summary>Registers the Estimation unit's singletons: <see cref="ModelPrices"/>, <see cref="BillingModes"/> as
    /// <see cref="IBillingModes"/> and <see cref="CostEstimator"/> as <see cref="ICostEstimator"/>. Needs <see cref="IModelCatalog"/> and
    /// <see cref="ISettingsStore"/>; uses <see cref="IAccountStore"/> when registered. <see cref="AddSpendArea"/> calls it.</summary>
    public static IServiceCollection AddSpendEstimation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => new ModelPrices(provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<ISettingsStore>(),
            () => provider.GetService<IDurableModels>()));
        services.TryAddSingleton(provider => new BillingModes(provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<ModelPrices>(), provider.GetService<IAccountStore>(), () => provider.GetService<IDurableModels>()));
        services.TryAddSingleton<IBillingModes>(provider => provider.GetRequiredService<BillingModes>());
        services.TryAddSingleton(provider => new CostEstimator(provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IBillingModes>(), provider.GetRequiredService<ModelPrices>(), provider.GetService<IAccountStore>(),
            () => provider.GetService<ISpendLedger>(), provider.GetService<ILogger<CostEstimator>>()));
        services.TryAddSingleton<ICostEstimator>(provider => provider.GetRequiredService<CostEstimator>());
        return services;
    }

    static partial void AddEstimation(IServiceCollection services, WebAgentOptions options) => services.AddSpendEstimation();
}
