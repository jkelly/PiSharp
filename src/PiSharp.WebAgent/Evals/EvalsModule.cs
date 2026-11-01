// Decision 0011 (run W5, E0): the Evals area's entry points (Flagship C: Model Arena + Drift Watch), which Program.cs calls after the Spend
// Guard, the run engine and the W4 areas (AddEvalsArea), and before the core's endpoints (MapEvalsArea). The area is built by several units at
// once without sharing a file: EvalsModule is a static partial class whose entry points call one partial hook per unit, and each unit
// implements its own hooks in its own file (Evals/<Area>/EvalsModule.<Area>.cs): AddRater/MapRater (E1, Evals/Rating: the Bradley-Terry
// rater), AddChecks/MapChecks (E2, Evals/Checks: checks and the rubric judge), AddDrift/MapDrift (E3, Evals/Drift: the drift analyzer and
// price watch), AddSuites/MapSuites (E4, Evals/Suites and Evals/Store: the store, runner, scheduler and /api/evals/suites, cases, runs),
// AddArena/MapArena (E5, Evals/Arena: blind battles, /api/evals/arena and leaderboard), AddPages/MapPages (E6, Evals/Pages: the five Evals
// pages) and AddPins/MapPins (E7, Evals/Pins: Pin as test case, /api/evals/pins). A hook no unit implemented compiles to nothing, so every
// unit builds alone. Before the hooks, the area registers the contracts' webagent.db migrations (2000-2099), so every unit's store finds
// the evals tables; a unit's own migrations go in its block of the range (EvalMigrations.Ranges).
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Evals;

/// <summary>Registration and endpoints of the Evals area.</summary>
public static partial class EvalsModule
{
    /// <summary>Registers the area's services: the contracts' webagent.db migrations, then the Rater, Checks, Drift, Suites, Arena, Pages
    /// and Pins units' services, in that order. Call it after the Spend Guard and the run engine are registered.</summary>
    public static IServiceCollection AddEvalsArea(IServiceCollection services, WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        foreach (var migration in EvalMigrations.All) services.AddWebAgentMigration(migration);
        AddRater(services, options);
        AddChecks(services, options);
        AddDrift(services, options);
        AddSuites(services, options);
        AddArena(services, options);
        AddPages(services, options);
        AddPins(services, options);
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core): the Rater, Checks, Drift, Suites, Arena, Pages and Pins
    /// units', in that order. Call it before <see cref="WebAgentHost.MapWebAgentCore"/>.</summary>
    public static WebApplication MapEvalsArea(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapRater(app);
        MapChecks(app);
        MapDrift(app);
        MapSuites(app);
        MapArena(app);
        MapPages(app);
        MapPins(app);
        return app;
    }

    /// <summary>The Rater unit (E1, <c>Evals/Rating/EvalsModule.Rating.cs</c>): the Bradley-Terry rater.</summary>
    static partial void AddRater(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Checks unit (E2, <c>Evals/Checks/EvalsModule.Checks.cs</c>): the check runner and the rubric judge.</summary>
    static partial void AddChecks(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Drift unit (E3, <c>Evals/Drift/EvalsModule.Drift.cs</c>): the drift analyzer and the price watch.</summary>
    static partial void AddDrift(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Suites unit (E4, <c>Evals/Suites/EvalsModule.Suites.cs</c>): the eval store, the suite runner and the scheduler.</summary>
    static partial void AddSuites(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Arena unit (E5, <c>Evals/Arena/EvalsModule.Arena.cs</c>): blind battles, votes and the leaderboard.</summary>
    static partial void AddArena(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Pages unit (E6, <c>Evals/Pages/EvalsModule.Pages.cs</c>): what the Evals pages need.</summary>
    static partial void AddPages(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Pins unit (E7, <c>Evals/Pins/EvalsModule.Pins.cs</c>): Pin as test case.</summary>
    static partial void AddPins(IServiceCollection services, WebAgentOptions options);

    /// <summary>The Rater unit's endpoints, if any.</summary>
    static partial void MapRater(WebApplication app);

    /// <summary>The Checks unit's endpoints, if any.</summary>
    static partial void MapChecks(WebApplication app);

    /// <summary>The Drift unit's endpoints, if any.</summary>
    static partial void MapDrift(WebApplication app);

    /// <summary>The Suites unit's endpoints: <c>/api/evals/suites</c>, <c>/api/evals/cases</c>, <c>/api/evals/runs</c>, <c>/api/evals/drift</c>.</summary>
    static partial void MapSuites(WebApplication app);

    /// <summary>The Arena unit's endpoints: <c>/api/evals/arena</c>, <c>/api/evals/leaderboard</c>.</summary>
    static partial void MapArena(WebApplication app);

    /// <summary>The Pages unit's endpoints (pages and their fragments under <c>/evals</c>), if any besides its controller.</summary>
    static partial void MapPages(WebApplication app);

    /// <summary>The Pins unit's endpoints: <c>/api/evals/pins</c>.</summary>
    static partial void MapPins(WebApplication app);
}
