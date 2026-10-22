// Decision 0011 (run W2): the settings UI area's entry points, which Program.cs calls. WU4 created this empty stub; WU9 owns it and fills it in.
namespace PiSharp.WebAgent.Settings;

/// <summary>Registration and endpoints of the settings UI area.</summary>
public static class SettingsModule
{
    /// <summary>Registers the area's services (and its webagent.db migrations). Called before the core's fallbacks, so a registration here
    /// replaces a faux default.</summary>
    public static IServiceCollection AddSettingsArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core).</summary>
    public static WebApplication MapSettingsArea(this WebApplication app) => app;
}
