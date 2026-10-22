// Decision 0011 (run W2): the routing area's entry points, which Program.cs calls. WU4 created this empty stub; WU6 owns it and fills it in. With
// options.Faux (--faux) it must register no provider code path: the core replaces IDurableModels and IModelCatalog with the faux ones anyway.
namespace PiSharp.WebAgent.Routing;

/// <summary>Registration and endpoints of the routing area.</summary>
public static class RoutingModule
{
    /// <summary>Registers the area's services (and its webagent.db migrations). Called before the core's fallbacks, so a registration here
    /// replaces a faux default.</summary>
    public static IServiceCollection AddRoutingArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core).</summary>
    public static WebApplication MapRoutingArea(this WebApplication app) => app;
}
