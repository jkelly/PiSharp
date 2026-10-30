// Decision 0011 (run W5, E5): the Arena unit's part of the Evals area: its webagent.db migration (2500, eval_arena_rounds), ArenaStore,
// ArenaService (follows runs.db and agent compare's store for the battles it holds) and ArenaPages, singletons registered with TryAdd, and
// the Rater's singletons (E1's AddEvalsRater, TryAdd too) that the leaderboard endpoint and the reveal's metrics use. The battle page and
// its fragments are ArenaController's (GET /compare/arena/{id}, MVC, mapped by the core); the JSON endpoints are ArenaEndpoints'.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    /// <summary>Registers the Arena unit: its migration, <see cref="ArenaStore"/>, <see cref="ArenaService"/>, <see cref="ArenaPages"/> and
    /// the rater (<see cref="AddEvalsRater"/>). <see cref="AddEvalsArea"/> calls it; the evals contracts' migrations must be registered too.</summary>
    public static IServiceCollection AddEvalsArena(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWebAgentMigration(ArenaStore.Rounds);
        services.AddEvalsRater();
        services.TryAddSingleton(provider => new ArenaStore(provider.GetRequiredService<WebAgentDb>()));
        services.TryAddSingleton(provider => new ArenaService(provider.GetRequiredService<ArenaStore>(), provider));
        services.TryAddSingleton(provider => new ArenaPages(provider.GetRequiredService<ArenaService>(), provider));
        return services;
    }

    static partial void AddArena(IServiceCollection services, WebAgentOptions options) => services.AddEvalsArena();

    static partial void MapArena(WebApplication app) => ArenaEndpoints.Map(app);
}
