// Decision 0011 (run W3, S0): the Spend Guard area's entry points, which Program.cs calls after the core's fallbacks (AddSpendArea) and
// before the core's endpoints (MapSpendArea). The area is built by several units at once without sharing a file: SpendModule is a static
// partial class whose entry points call one partial hook per unit, and each unit implements its own hooks in its own file
// (Spend/<Area>/SpendModule.<Area>.cs): AddEstimation/MapEstimation (cost estimator, billing modes), AddLedger/MapLedger (webagent.db
// ledger and caps, startup recovery), AddGuard/MapGuard (the IDurableModels decorator, GuardedRunExecutor, preflight) and AddPages/MapPages
// (Settings › Spend, History › Spend, /api/spend). A hook no unit implemented compiles to nothing, so every unit builds alone. Before the
// hooks, --faux-metered prices the faux models (Faux/FauxPricing.cs), so the guard decorates the priced ones. Registration runs after the
// fallbacks, so AddGuard decorates whatever IDurableModels the app ends with (faux, priced faux or routed).
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Spend;

/// <summary>Registration and endpoints of the Spend Guard area.</summary>
public static partial class SpendModule
{
    /// <summary>Registers the area's services: the priced faux models when <see cref="WebAgentOptions.FauxMetered"/>, then the Estimation,
    /// Ledger, Guard and Pages units' services, in that order. Call it after <see cref="WebAgentHost.AddWebAgentFallbacks"/>.</summary>
    public static IServiceCollection AddSpendArea(this IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddMeteredFaux(options);
        AddEstimation(services, options);
        AddLedger(services, options);
        AddGuard(services, options);
        AddPages(services, options);
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core): the Estimation, Ledger, Guard and Pages units', in that
    /// order. Call it before <see cref="WebAgentHost.MapWebAgentCore"/>.</summary>
    public static WebApplication MapSpendArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapEstimation(app);
        MapLedger(app);
        MapGuard(app);
        MapPages(app);
        return app;
    }

    /// <summary>The Estimation unit (S1): <see cref="ICostEstimator"/> and <see cref="IBillingModes"/>.</summary>
    static partial void AddEstimation(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Ledger unit (S2): <see cref="ISpendLedger"/>, its webagent.db migrations (1000-1999) and startup recovery.</summary>
    static partial void AddLedger(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Guard unit (S3): <see cref="ISpendGuard"/>, the <c>IDurableModels</c> decorator and the GuardedRunExecutor.</summary>
    static partial void AddGuard(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Pages unit (S4): what Settings › Spend and History › Spend need.</summary>
    static partial void AddPages(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Estimation unit's endpoints, if any.</summary>
    static partial void MapEstimation(WebApplication app);

    /// <summary>The Ledger unit's endpoints, if any.</summary>
    static partial void MapLedger(WebApplication app);

    /// <summary>The Guard unit's endpoints, if any.</summary>
    static partial void MapGuard(WebApplication app);

    /// <summary>The Pages unit's endpoints: <c>/api/spend/...</c>.</summary>
    static partial void MapPages(WebApplication app);
}
