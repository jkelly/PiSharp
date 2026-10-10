// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/session-resources.ts (and the registration of
// closeOpenAICodexWebSocketSessions in packages/ai/src/api/openai-codex-responses.ts).
using PiSharp.AI.Protocols.OpenAICodexResponses;

namespace PiSharp.AI;

/// <summary>registerSessionResourceCleanup/cleanupSessionResources: the cleanups of session-scoped provider resources, which a session
/// runs when it is disposed. The Codex WebSocket connection cache registers itself (as its module does when loaded).</summary>
public static class SessionResources
{
    private static readonly object Gate = new();
    private static readonly List<Action<string?>> Cleanups = [OpenAICodexWebSockets.CloseSessions];

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
