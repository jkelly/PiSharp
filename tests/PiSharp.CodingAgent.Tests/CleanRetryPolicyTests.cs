using System.Text.Json;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

internal static class CleanRetryPolicyTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("clean retry policy defaults and typed invalid settings", Settings),
        ("clean retry policy original exponential cap and safe integer saturation", Delay),
        ("clean retry policy provider classification permanent exclusions and JS boundaries", Classification),
        ("clean retry settings legacy provider migration preserves unknown values and input", Migration)
    ];
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Clean retry policy assertion failed."); }
    private static void Reject(Action action)
    { var rejected = false; try { action(); } catch (JsonException) { rejected = true; } Check(rejected); }
    private static Task Settings()
    {
        Check(RetrySettingsProjection.ReadEffective(JsonData.EmptyObject) == new AgentRetryPolicy());
        Check(RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":{\"enabled\":null,\"maxRetries\":null,\"baseDelayMs\":null,\"maxAgentDelayMs\":null}}")) == AgentRetryPolicy.Default);
        Check(RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":{\"enabled\":false,\"maxRetries\":0,\"baseDelayMs\":0,\"maxAgentDelayMs\":0,\"provider\":{\"maxRetryDelayMs\":2},\"unknown\":42}}")) == new AgentRetryPolicy(false, 0, 0, 0));
        foreach (var value in new[] { "-1", "1.5", "9007199254740992", "\"3\"", "true" })
            Reject(() => RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":{\"baseDelayMs\":" + value + "}}")));
        Reject(() => RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":{\"maxRetries\":2147483648}}")));
        Reject(() => RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":{\"enabled\":\"false\"}}")));
        Reject(() => RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":[]}")));
        Check(RetrySettingsProjection.ReadEffective(JsonData.Parse("{\"retry\":{\"baseDelayMs\":2e3}}")) == AgentRetryPolicy.Default);
        return Task.CompletedTask;
    }
    private static Task Delay()
    {
        var policy = AgentRetryPolicy.Default;
        Check(policy.DelayMs(0) == 2000 && policy.DelayMs(1) == 2000 && policy.DelayMs(2) == 4000 && policy.DelayMs(6) == 60000);
        Check(policy.DelayMs(int.MaxValue) == 60000);
        var wide = new AgentRetryPolicy(baseDelayMs: 3, maxAgentDelayMs: AgentRetryPolicy.MaximumSafeInteger);
        Check(wide.DelayMs(52) == 6_755_399_441_055_744 && wide.DelayMs(53) == AgentRetryPolicy.MaximumSafeInteger);
        var zero = new AgentRetryPolicy(baseDelayMs: 0);
        Check(zero.DelayMs(1) == 0 && zero.DelayMs(1025) == 60000); // JS 0 * Infinity is NaN, then saturates.
        return Task.CompletedTask;
    }
    private static Task Classification()
    {
        foreach (var value in new[] { "overloaded", "503", "NETWORK error", "fetch failed", "WebSocket closed", "stream ended before message_stop", "ResourceExhausted", "subscription_sharing_usage_unavailable", "please retry your request", "retry delay" })
            Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, value));
        foreach (var value in new[] { "503 insufficient_quota", "429 GoUsageLimitError", "network error billing", "429 available balance", "503 subscription_sharing_usage_limit_exceeded", "bad input", "rate\nlimit", "rate\rlimit", "rate\u2028limit", "rate\u2029limit", "ſerver error", "ſocket hang up" })
            Check(!AgentRetryPolicy.IsRetryableError(StopReason.Error, value));
        Check(AgentRetryPolicy.IsRetryableError(StopReason.Error, "ratelimit") && AgentRetryPolicy.IsRetryableError(StopReason.Error, "rate-limit"));
        Check(!AgentRetryPolicy.IsRetryableError(StopReason.Aborted, "503") && !AgentRetryPolicy.IsRetryableError(StopReason.Stop, "503") && !AgentRetryPolicy.IsRetryableError(StopReason.Error, null));
        return Task.CompletedTask;
    }
    private static Task Migration()
    {
        var input = JsonData.Parse("{\"unknown\":[1,null],\"retry\":{\"maxDelayMs\":17,\"maxAgentDelayMs\":31,\"provider\":{\"unknown\":\"kept\"}}}");
        var before = input.ToString(); var result = RetrySettingsProjection.NormalizeLayer(input);
        var retry = result.Value.GetProperty("retry");
        Check(input.ToString() == before && !retry.TryGetProperty("maxDelayMs", out _) && retry.GetProperty("maxAgentDelayMs").GetInt32() == 31);
        Check(retry.GetProperty("provider").GetProperty("maxRetryDelayMs").GetInt32() == 17 && retry.GetProperty("provider").GetProperty("unknown").GetString() == "kept" && result.Value.GetProperty("unknown")[1].ValueKind == JsonValueKind.Null);
        Check(RetrySettingsProjection.ReadEffective(result).MaxAgentDelayMs == 31);
        foreach (var cap in new[] { "0", "19", "\"owned\"" })
        {
            var migrated = RetrySettingsProjection.NormalizeLayer(JsonData.Parse("{\"retry\":{\"maxDelayMs\":17,\"provider\":{\"maxRetryDelayMs\":" + cap + "}}}"));
            Check(migrated.Value.GetProperty("retry").GetProperty("provider").GetProperty("maxRetryDelayMs").GetRawText() == cap);
        }
        var nullCap = RetrySettingsProjection.NormalizeLayer(JsonData.Parse("{\"retry\":{\"maxDelayMs\":17,\"provider\":{\"maxRetryDelayMs\":null}}}"));
        Check(nullCap.Value.GetProperty("retry").GetProperty("provider").GetProperty("maxRetryDelayMs").GetInt32() == 17);
        var array = RetrySettingsProjection.NormalizeLayer(JsonData.Parse("{\"retry\":{\"maxDelayMs\":17,\"provider\":[\"preserved\",null]}}"));
        Check(array.Value.GetProperty("retry").GetProperty("provider").GetProperty("0").GetString() == "preserved" && array.Value.GetProperty("retry").GetProperty("provider").GetProperty("1").ValueKind == JsonValueKind.Null);
        var invalidLegacy = RetrySettingsProjection.NormalizeLayer(JsonData.Parse("{\"retry\":{\"maxDelayMs\":\"invalid\",\"unknown\":true}}"));
        Check(!invalidLegacy.Value.GetProperty("retry").TryGetProperty("maxDelayMs", out _) && !invalidLegacy.Value.GetProperty("retry").TryGetProperty("provider", out _));
        return Task.CompletedTask;
    }
}
