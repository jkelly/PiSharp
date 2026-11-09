// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/ai/src/cache-warming.ts.
namespace PiSharp.AI.ModelOperations;

/// <summary>Host-independent prompt-cache replay policy. Hosts retain scheduling, cancellation and usage persistence.</summary>
public static class CacheWarmingPolicy
{
    public const double MaximumAgeMs = 60 * 60_000;
    public const double MaximumIdleAgeMs = 30 * 60_000;
    public const double MinimumExpectedSavings = 0.05;
    public const double IdleContinuationProbability = 0.15;
    public static int ReplayMaximumTokens => 1;
    public static int ReplayMaximumRetries => 0;
    public static ChatRequest PrepareReplay(ChatRequest request) => request with { DisableContinuation = true };
    public static double? DelayMs(double ttlMs) => ttlMs <= 10_000 ? null : Math.Max(1, Math.Floor(Math.Min(ttlMs * 0.9, ttlMs - 10_000)));
    public static bool IsReplayable(string api, bool adaptiveThinking, string? reasoning) =>
        string.IsNullOrEmpty(reasoning) || api != "anthropic-messages" || adaptiveThinking;
    public static double? TtlMs(double? shortSeconds, double? longSeconds, string? retention = null,
        IReadOnlyDictionary<string, string?>? environment = null, Func<string, string?>? processEnvironment = null)
    {
        string? scoped = null;
        if (environment is not null) environment.TryGetValue("PI_CACHE_RETENTION", out scoped);
        var resolved = retention ?? ((!string.IsNullOrEmpty(scoped) ? scoped : (processEnvironment ?? Environment.GetEnvironmentVariable)("PI_CACHE_RETENTION")) == "long" ? "long" : "short");
        return (resolved switch { "short" => shortSeconds, "long" => longSeconds, _ => null }) * 1000;
    }
    public static PromptCacheWarmingDecision Evaluate(ModelCost cost, double promptTokens, string phase)
    {
        var hit = cost.Calculate(0, 0, promptTokens, 0).Total;
        var miss = cost.CacheWrite > 0 ? cost.Calculate(0, 0, 0, promptTokens).Total : cost.Calculate(promptTokens, 0, 0, 0).Total;
        var warm = cost.Calculate(0, 1, promptTokens, 0).Total;
        var extra = Math.Max(0, miss - hit);
        var probability = phase == "idle" ? IdleContinuationProbability : 1;
        var savings = probability * extra - warm;
        return new(phase, warm, extra, probability, savings, promptTokens > 0 && (hit > 0 || miss > 0), savings >= MinimumExpectedSavings ? "warm" : "stop");
    }
}
public sealed record PromptCacheWarmingDecision(string Phase, double WarmCost, double MissCost, double ContinuationProbability,
    double ExpectedSavings, bool EconomicsAvailable, string Action);
