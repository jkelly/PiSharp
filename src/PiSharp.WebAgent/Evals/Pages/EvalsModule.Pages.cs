// Decision 0011 (run W5, E6): the Pages unit's part of the Evals area: EvalPages, the five pages' view-model builder, as a singleton
// (TryAdd, so a test can register its own first). The pages and their fragments are EvalsController's (MVC, mapped by the core); the unit
// maps no endpoints of its own: every write the pages make goes to the Suites, Checks and Arena units' JSON APIs.
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.WebAgent.Evals.Pages;
using PiSharp.WebAgent.Hosting;

namespace PiSharp.WebAgent.Evals;

public static partial class EvalsModule
{
    /// <summary>Registers the Pages unit: <see cref="EvalPages"/>. <see cref="AddEvalsArea"/> calls it.</summary>
    public static IServiceCollection AddEvalsPages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => new EvalPages(provider));
        return services;
    }

    static partial void AddPages(IServiceCollection services, WebAgentOptions options) => services.AddEvalsPages();
}
