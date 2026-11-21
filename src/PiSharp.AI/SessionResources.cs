// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/src/session-resources.ts (and the registrations of
// closeOpenAICodexWebSocketSessions in packages/ai/src/api/openai-codex-responses.ts and of the message thread state in
// api/anthropic-messages.ts; api/strict-tool-limits.ts registers itself in StrictToolLimits). Pi 1.6.0 also publishes the module
// as @earendil-works/pi-ai/session-resources, so that a host such as the Durable harness can release one provider session's
// resources without loading the whole package; a .NET assembly has no subpath exports, and this class is that entry point.
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.OpenAICodexResponses;

namespace PiSharp.AI;

/// <summary>registerSessionResourceCleanup/cleanupSessionResources: the cleanups of session-scoped provider resources, which a session
/// runs when it is disposed. The Codex WebSocket connection cache and the shared Anthropic message thread state register themselves
/// (as their modules do when loaded).</summary>
public static class SessionResources
{
    private static readonly object Gate = new();
    private static readonly List<Action<string?>> Cleanups = [OpenAICodexWebSockets.CloseSessions, AnthropicMessageThreadStore.Shared.Cleanup];

    /// <summary>Adds a cleanup (once); the returned action removes it again.</summary>
    public static Action RegisterCleanup(Action<string?> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (Gate) if (!Cleanups.Contains(cleanup)) Cleanups.Add(cleanup);
        return () => { lock (Gate) Cleanups.Remove(cleanup); };
    }

    /// <summary>Runs every cleanup for the session (every session without an id); the failures, after all ran, throw together
    /// ("Failed to cleanup session resources").</summary>
    public static void Cleanup(string? sessionId = null)
    {
        Action<string?>[] cleanups;
        lock (Gate) cleanups = [.. Cleanups];
        var errors = new List<Exception>();
        foreach (var cleanup in cleanups)
        {
            try { cleanup(sessionId); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count > 0) throw new AggregateException("Failed to cleanup session resources", errors);
    }
}
