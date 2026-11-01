// Decision 0011 (run W2, WU6): what the routing area reads from outside itself, in one place so tests can replace it: the HTTP handler
// provider requests go through, the OpenAI Codex transport, and the clock. The environment, the auth server, the sign-in client and the
// home folder are the credentials area's (CredentialsOptions), so routing never reads a variable the accounts did not see.
namespace PiSharp.WebAgent.Routing;

/// <summary>Options of the routing area (registered as a singleton by <see cref="RoutingModule.AddRoutingArea"/> unless one is registered
/// first).</summary>
public sealed class RoutingOptions
{
    /// <summary>The handler every provider request is sent through, or null for the routes' own (redirects off). Caller-owned: the area
    /// never disposes it. Tests pass a fake, so no request leaves the process.</summary>
    public HttpMessageHandler? ProviderHandler { get; init; }

    /// <summary>The OpenAI Codex transport: <c>sse</c>, <c>websocket</c>, or null for <c>auto</c> (WebSocket first, then SSE).</summary>
    public string? CodexTransport { get; init; }

    /// <summary>The clock of request timestamps and OAuth expiries; null: the credentials area's (<see cref="Credentials.CredentialsOptions.Time"/>).</summary>
    public TimeProvider? Time { get; init; }
}
