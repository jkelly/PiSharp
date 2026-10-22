// Decision 0011 (run W2): the credentials and sign-in area's entry points, which Program.cs calls. WU4 created this empty stub; WU5 owns it and fills it in.
namespace PiSharp.WebAgent.Credentials;

/// <summary>Registration and endpoints of the credentials and sign-in area.</summary>
public static class CredentialsModule
{
    /// <summary>Registers the area's services (and its webagent.db migrations). Called before the core's fallbacks, so a registration here
    /// replaces a faux default.</summary>
    public static IServiceCollection AddCredentialsArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core).</summary>
    public static WebApplication MapCredentialsArea(this WebApplication app) => app;
}
