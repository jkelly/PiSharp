// Decision 0011 (run W2): the durable chat, approvals and stream area's entry points, which Program.cs calls. WU4 created this empty stub;
// WU7 fills it in: the chat bindings migration (200), the device host (a hosted service), the approval broker, the chat service (which is
// also the sidebar's IRecentChats), the shared agent view feeds and the per-tab stream (GET /api/stream, PUT /api/stream/{streamId}). The
// pages and the /api/chats commands are ChatController's. W3 (S5) registers ChatSpend, the Spend Guard on the chat pages (it reads the Spend
// area's services when they are there, and does nothing without them), and the W3 fixes ChatSpendSends (migration 1400), where the sends it
// admitted are kept across restarts.
using System.Text.Json;
using PiSharp.Durable.Client;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;

namespace PiSharp.WebAgent.Chat;

/// <summary>Registration and endpoints of the durable chat, approvals and stream area.</summary>
public static class ChatModule
{
    /// <summary>Registers the area's services (and its webagent.db migrations). Called before the core's fallbacks, so a registration here
    /// replaces a faux default.</summary>
    public static IServiceCollection AddChatArea(this IServiceCollection services, Hosting.WebAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddWebAgentMigration(ChatBindings.Migration);
        services.AddSingleton(provider => new ChatBindings(provider.GetRequiredService<WebAgentDb>()));
        services.AddSingleton(_ => new ApprovalBroker());
        services.AddSingleton<DeviceHostService>();
        services.AddHostedService(provider => provider.GetRequiredService<DeviceHostService>());
        services.AddSingleton<ChatService>();
        services.AddWebAgentMigration(ChatSpendSends.Migration);
        services.AddSingleton(provider => new ChatSpendSends(provider.GetRequiredService<WebAgentDb>(), provider.GetService<TimeProvider>()));
        services.AddSingleton<ChatSpend>();
        services.AddSingleton<IRecentChats>(provider => provider.GetRequiredService<ChatService>());
        services.AddSingleton<AgentViewHub>();
        services.AddSingleton<ChatStreamMux>();
        return services;
    }

    /// <summary>Maps the area's endpoints (MVC controllers are mapped by the core): the per-tab stream.</summary>
    public static WebApplication MapChatArea(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/api/stream", (HttpContext http, ChatStreamMux mux) => mux.OpenAsync(http));
        app.MapPut("/api/stream/{streamId}", async (HttpContext http, string streamId, ChatStreamMux mux) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ChatStreamSubscriptions>(http.Request.Body, DurableWire.Options, http.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Json(new { subs = mux.Subscribe(streamId, body?.Subs ?? []) });
            }
            catch (KeyNotFoundException closed)
            {
                return Results.Json(new { error = new { code = ChatErrors.NotFound, message = closed.Message } }, statusCode: 404);
            }
            catch (Exception failure) when (failure is ArgumentException or JsonException)
            {
                return Results.Json(new { error = new { code = ChatErrors.Invalid, message = failure.Message } }, statusCode: 400);
            }
        });
        return app;
    }
}
