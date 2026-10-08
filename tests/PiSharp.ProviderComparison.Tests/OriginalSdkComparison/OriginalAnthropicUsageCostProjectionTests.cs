using System.Reflection;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.Contracts;

internal static class OriginalAnthropicUsageCostProjectionTests
{
    // Dedicated source-only controls. A future qualifier must explicitly invoke these.
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("zero-before-usage-and-default-rates", Zero);
        yield return ("binary64-order-not-final-decimal-cast", Fractional);
        yield return ("mixed-short-and-one-hour-cache-write", Mixed);
        yield return ("error-partial-reuses-owned-usage-without-mutation", Partial);
        yield return ("large-count-subtraction-uses-number-operands", LargeCounts);
        yield return ("invalid-cache-split-refused", Invalid);
    }

    private static JsonData Project(AnthropicTokenRates rates, TokenUsage usage, long oneHour)
    {
        var type = typeof(AnthropicTokenRates).Assembly.GetType(
            "PiSharp.AI.Protocols.AnthropicMessages.OriginalAnthropicUsageCostProjection", throwOnError: true)!;
        var method = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (JsonData)method.Invoke(null, [rates, usage, oneHour])!;
    }

    private static TokenUsage Usage(long input, long output, long read, long write) =>
        new(input, output, read, write, 0, new(999, 999, 999, 999, 999));

    private static void Bits(JsonData value, params ulong[] expected)
    {
        string[] names = ["input", "output", "cacheRead", "cacheWrite", "total"];
        if (value.Value.EnumerateObject().Count() != names.Length) throw new InvalidOperationException("Cost shape");
        for (var i = 0; i < names.Length; i++)
            if (unchecked((ulong)BitConverter.DoubleToInt64Bits(value.Value.GetProperty(names[i]).GetDouble())) != expected[i])
                throw new InvalidOperationException("Original binary64 bits: " + names[i]);
    }

    private static void Zero()
    {
        Bits(Project(new(1, 2, .5m, 1.25m), TokenUsage.Zero, 0), 0, 0, 0, 0, 0);
        Bits(Project(new(), Usage(11, 13, 17, 19), 0), 0, 0, 0, 0, 0);
    }

    private static void Fractional() => Bits(Project(new(1, 2, .5m, 1.25m), Usage(3, 5, 7, 11), 0),
        0x3ec92a737110e454, 0x3ee4f8b588e368f0, 0x3ecd5c31593e5fb7, 0x3eecd5f99c38b04b, 0x3effb82c2bd7f51e);

    private static void Mixed() => Bits(Project(new(3, 15, .3m, 3.75m), Usage(101, 23, 17, 19), 7),
        0x3f33db7f1737542a, 0x3f369c23b7952d23, 0x3ed56415534e5bad, 0x3f16ce789e774eec, 0x3f484068a5dbc73a);

    private static void Partial()
    {
        var usage = Usage(36, 0, 0, 0);
        var nativeCost = usage.Cost;
        var first = Project(new(1, 2, .5m, 1.25m), usage, 0);
        Bits(first, 0x3f02dfd694ccab3f, 0, 0, 0, 0x3f02dfd694ccab3f);
        _ = Project(new(9, 10, 11, 12), Usage(999, 999, 999, 999), 0);
        Bits(first, 0x3f02dfd694ccab3f, 0, 0, 0, 0x3f02dfd694ccab3f);
        if (!ReferenceEquals(nativeCost, usage.Cost) || usage.Cost.Total != 999)
            throw new InvalidOperationException("Native decimal usage mutated");
    }

    private static void LargeCounts()
    {
        // JS rounds both counts to 2^53 before subtraction, so shortWrite is zero.
        Bits(Project(new(0, 0, 0, 1), Usage(0, 0, 0, 9_007_199_254_740_993), 9_007_199_254_740_992),
            0, 0, 0, 0, 0);
    }

    private static void Invalid()
    {
        try { _ = Project(new(), Usage(0, 0, 0, 1), 2); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Invalid cache split accepted");
    }
}
