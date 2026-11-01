// Decision 0011 (run W5, E3): the Drift unit's part of the Evals area. DriftAnalyzer is static (the Suites unit, E4, calls it after each
// finished run and stores the signals), so the unit registers only Price Watch and what it needs: the price snapshot store (on E0's
// eval_price_snapshots and eval_price_changes tables), the price source (the app's IModelCatalog), the suite cost estimator (the Spend
// Guard's worst case per suite run, which E4 reuses for SuiteEstimate), PriceWatch, and PriceWatchService, which checks prices when the app
// starts and every 10 minutes. Everything is TryAdd, so a test registers its own source or estimator first. The unit maps no endpoints:
// /api/evals/drift is the Suites unit's, and the drift page reads PriceWatch through the Pages unit.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    static partial void AddDrift(IServiceCollection services, WebAgentOptions options)
    {
        services.TryAddSingleton<IPriceSnapshotStore>(provider => new PriceSnapshotStore(provider.GetRequiredService<WebAgentDb>()));
        services.TryAddSingleton<IPriceSource>(provider => new CatalogPriceSource(provider.GetRequiredService<IModelCatalog>()));
        services.TryAddSingleton<ISuiteCostEstimator>(provider =>
        {
            var engine = new Lazy<RunEngine?>(() => provider.GetService<RunEngine>());
            return new SuiteCostEstimator(provider.GetRequiredService<IModelCatalog>(), provider.GetRequiredService<ISettingsStore>(),
                provider.GetRequiredService<IBillingModes>(), provider.GetRequiredService<ModelPrices>(), spec => SuiteCostEstimator.PlannedBytes(engine.Value, spec));
        });
        services.TryAddSingleton(provider => new PriceWatch(provider.GetRequiredService<IPriceSource>(), provider.GetRequiredService<IPriceSnapshotStore>(),
            provider.GetService<ISuiteCostEstimator>(), provider.GetService<TimeProvider>(), provider.GetService<ILogger<PriceWatch>>()));
        services.TryAddSingleton(provider => new PriceWatchService(provider.GetRequiredService<PriceWatch>(), provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<PriceWatchService>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PriceWatchService>(provider => provider.GetRequiredService<PriceWatchService>()));
    }
}
