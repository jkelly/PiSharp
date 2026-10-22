// Decision 0011 (run W2): the durable chat, approvals and stream area's entry points, which Program.cs calls. WU4 created this empty stub; WU7 owns it and fills it in.
namespace PiSharp.WebAgent.Chat;

/// <summary>Registration and endpoints of the durable chat, approvals and stream area.</summary>
public static class ChatModule
{
    /// <summary>Registers the area's services (and its webagent.db migrations). Called before the core's fallbacks, so a registration here
    /// replaces a faux default.</summary>
    public static IServiceCollection AddChatArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core).</summary>
    public static WebApplication MapChatArea(this WebApplication app) => app;
}
