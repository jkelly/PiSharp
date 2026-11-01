// Decision 0011 (run W5, E2): the Checks unit's hooks of the Evals area (EvalsModule's AddChecks and MapChecks): the rubric judge and the
// check runner as singletons over the run engine, the run store, the model catalog and the cost estimator (all registered before the Evals
// area), and the check editor's endpoints under /api/evals/checks. The unit adds no webagent.db migration (its block, 2200-2299, is unused):
// checks live in their test case's JSON.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    static partial void AddChecks(IServiceCollection services, WebAgentOptions options)
    {
        services.TryAddSingleton<RubricJudge>();
        services.TryAddSingleton<CheckRunner>();
    }

    static partial void MapChecks(WebApplication app) => app.MapCheckEndpoints();
}
