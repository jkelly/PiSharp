// Decision 0011 (run W5, E1): the Rater unit's part of the Evals area: BradleyTerryRater (stateless), ArenaMetrics (reads runs.db and agent
// compare's store on use, so registration order does not matter) and ArenaLeaderboards (the two together, with Settings' provisional
// threshold), singletons, each a TryAdd so a test or another host can put its own first. The unit adds no migration (a leaderboard is
// computed from E0's eval_battles and eval_votes, and its bootstrap seed is derived from the mode and tag) and maps no endpoint:
// /api/evals/leaderboard is E5's.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    /// <summary>Registers the Rater unit's singletons: <see cref="BradleyTerryRater"/>, <see cref="ArenaMetrics"/> and
    /// <see cref="ArenaLeaderboards"/>. <see cref="AddEvalsArea"/> calls it.</summary>
    public static IServiceCollection AddEvalsRater(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<BradleyTerryRater>();
        services.TryAddSingleton(provider => new ArenaMetrics(provider));
        services.TryAddSingleton(provider => new ArenaLeaderboards(provider.GetRequiredService<BradleyTerryRater>(),
            provider.GetRequiredService<ArenaMetrics>(), provider));
        return services;
    }

    static partial void AddRater(IServiceCollection services, WebAgentOptions options) => services.AddEvalsRater();
}
