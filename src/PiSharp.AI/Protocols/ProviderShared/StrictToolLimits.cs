// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/api/strict-tool-limits.ts.
namespace PiSharp.AI.Protocols.ProviderShared;

/// <summary>Sessions whose preferred strict tools exceeded Claude's limits, shared by Anthropic and Bedrock.
/// Stores only session identifiers. Repeated marks keep insertion order, matching the upstream Set.</summary>
internal static class StrictToolLimits
{
    internal const int MaximumSessions = 10_000;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<string>> Sessions = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> Order = new();

    static StrictToolLimits() => SessionResources.RegisterCleanup(Cleanup);

    internal static bool HasExceeded(string? sessionId)
    { lock (Gate) return sessionId is not null && Sessions.ContainsKey(sessionId); }

    internal static void MarkExceeded(string? sessionId)
    {
        if (sessionId is null) return;
        lock (Gate)
        {
            if (Sessions.ContainsKey(sessionId)) return;
            Sessions.Add(sessionId, Order.AddLast(sessionId));
            if (Sessions.Count > MaximumSessions)
            {
                Sessions.Remove(Order.First!.Value);
                Order.RemoveFirst();
            }
        }
    }

    private static void Cleanup(string? sessionId)
    {
        lock (Gate)
        {
            if (sessionId is null) { Sessions.Clear(); Order.Clear(); }
            else if (Sessions.Remove(sessionId, out var node)) Order.Remove(node);
        }
    }
}
