namespace PiSharp.AI.Protocols.ProviderShared;

/// <summary>Shared Simple output allowance, after each provider estimates the current transcript.</summary>
internal static class SimpleOutputBudget
{
    // Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT), packages/ai/src/api/simple-options.ts.
    // Keep JavaScript number semantics: estimates and explicit limits can be fractional.
    internal static double ClampToContext(double contextWindow, double inputTokens, double maxTokens)
    {
        if (contextWindow <= 0) return Math.Max(1, maxTokens);
        var margin = Math.Max(4096, Math.Ceiling(inputTokens * 0.15));
        var room = contextWindow - inputTokens - margin;
        // The ratio margin is useful only while it leaves a substantial response allowance.
        // Near the limit, restore the fixed margin but cap output at 32k.
        if (room < 32768) room = Math.Min(32768, contextWindow - inputTokens - 4096);
        return Math.Min(maxTokens, Math.Max(1, room));
    }
}
