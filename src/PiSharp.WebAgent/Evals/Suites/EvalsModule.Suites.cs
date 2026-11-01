// Decision 0011 (run W5, E4): the Suites unit's part of the Evals area: the eval store (webagent.db's suites, cases, runs, results, slots,
// baselines and signals), the suite service, the suite runner (one group reservation per run, through the run engine), the scheduler (a
// hosted service: decision 0009's slot rules, and pausing schedules on price changes) and the /api/evals suites, cases, runs and drift
// endpoints. Everything is TryAdd, so a test can register its own first; the scheduler is a hosted service only through its singleton.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Evals.Drift;
using PiSharp.WebAgent.Evals.Store;
using PiSharp.WebAgent.Evals.Suites;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    static partial void AddSuites(IServiceCollection services, WebAgentOptions options)
    {
        services.TryAddSingleton(provider => new EvalStore(provider.GetRequiredService<WebAgentDb>()));
        services.TryAddSingleton(provider => new SuiteService(provider.GetRequiredService<EvalStore>(), provider.GetRequiredService<CheckRunner>(),
            provider.GetRequiredService<ISuiteCostEstimator>(), provider.GetRequiredService<RunStore>(), provider.GetService<PriceWatch>(),
            provider.GetService<TimeProvider>(), provider.GetService<ILogger<SuiteService>>()));
        services.TryAddSingleton(provider => new SuiteRunner(provider.GetRequiredService<EvalStore>(), provider.GetRequiredService<RunEngine>(),
            provider.GetRequiredService<RunStore>(), provider.GetRequiredService<CheckRunner>(), provider.GetRequiredService<ISuiteCostEstimator>(), provider,
            provider.GetService<PriceWatch>(), provider.GetService<TimeProvider>(), provider.GetService<ILogger<SuiteRunner>>(),
            provider.GetService<IHostApplicationLifetime>()));
        services.TryAddSingleton(provider => new EvalScheduler(provider.GetRequiredService<EvalStore>(), provider.GetRequiredService<SuiteService>(),
            provider.GetRequiredService<SuiteRunner>(), provider.GetService<PriceWatch>(), provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<EvalScheduler>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EvalScheduler>(provider => provider.GetRequiredService<EvalScheduler>()));
    }

    static partial void MapSuites(WebApplication app) => app.MapSuiteEndpoints();
}
