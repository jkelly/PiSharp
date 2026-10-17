// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/usage.ts. Usage values are pi-ai Usage wire JSON. Upstream's
// addUsage mutates a draft or a plain object; here AddUsage(DraftObject, ...) changes a draft in a commit, and the plain-value overload and
// AddUsageState return a new value. A summed key keeps its position and a new key goes last, as upstream's assignments do.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>The built-in usage ledger (upstream <c>usage.ts</c>).</summary>
public static class UsageLedger
{
    /// <summary>
    /// Ledger of one conversation's own spend (upstream <c>UsageDoc</c>): <c>{models, tools}</c>, assistant entries and summarization attempts
    /// keyed <c>provider/modelId</c>, tool results keyed by tool name.
    /// </summary>
    public static ConversationDocToken UsageDoc { get; } = Documents.DefineConversationDoc("pi.usage", 1, DocumentFork.Initial,
        () => (JsonObjectValue)JsonValue.Parse("""{"models":{},"tools":{}}"""), checkpointWhen: (_, _, _) => true);

    /// <summary>Adds <paramref name="usage"/> to one bucket (<c>models</c> or <c>tools</c>) of the conversation's <c>pi.usage</c>, in the
    /// commit that records the response (upstream <c>recordUsage</c>).</summary>
    public static async Task RecordUsageAsync(ITx tx, ConversationId conversationId, string bucket, string key, JsonObjectValue usage)
    {
        var totals = (await tx.DocAsync(UsageDoc, conversationId).ConfigureAwait(false)).GetObject(bucket);
        // Own keys only: a tool may be called `toString`.
        if (!totals.ContainsKey(key)) totals.Set(key, usage);
        else AddUsage(totals.GetObject(key), usage);
    }

    /// <summary>Adds every counter of <paramref name="usage"/> to <paramref name="total"/>; optional counters are added once either side
    /// reports them (upstream <c>addUsage</c>).</summary>
    public static void AddUsage(DraftObject total, JsonObjectValue usage)
    {
        foreach (var key in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" }) Add(total, key, Number(usage, key));
        if (usage.TryGetValue("cacheWrite1h", out var cacheWrite1h)) Add(total, "cacheWrite1h", Number(cacheWrite1h));
        if (usage.TryGetValue("reasoning", out var reasoning)) Add(total, "reasoning", Number(reasoning));
        var cost = total.GetObject("cost");
        var spent = (JsonObjectValue)usage["cost"];
        foreach (var key in new[] { "input", "output", "cacheRead", "cacheWrite", "total" }) Add(cost, key, Number(spent, key));
    }

    /// <summary>The plain-value <c>addUsage</c>: <paramref name="total"/> with every counter of <paramref name="usage"/> added.</summary>
    internal static JsonObjectValue AddUsage(JsonObjectValue total, JsonObjectValue usage)
    {
        var sum = total.ToBuilder();
        foreach (var key in new[] { "input", "output", "cacheRead", "cacheWrite", "totalTokens" }) Add(sum, key, Number(usage, key));
        if (usage.TryGetValue("cacheWrite1h", out var cacheWrite1h)) Add(sum, "cacheWrite1h", Number(cacheWrite1h));
        if (usage.TryGetValue("reasoning", out var reasoning)) Add(sum, "reasoning", Number(reasoning));
        var cost = ((JsonObjectValue)total["cost"]).ToBuilder();
        var spent = (JsonObjectValue)usage["cost"];
        foreach (var key in new[] { "input", "output", "cacheRead", "cacheWrite", "total" }) Add(cost, key, Number(spent, key));
        sum["cost"] = cost.ToImmutable();
        return sum.ToImmutable();
    }

    /// <summary>Adds every bucket of <paramref name="state"/> into <paramref name="sum"/>, both <c>pi.usage</c> values (upstream
    /// <c>addUsageState</c>). Keys are own JSON keys, so a tool named <c>__proto__</c> or <c>toString</c> is an ordinary key.</summary>
    internal static JsonObjectValue AddUsageState(JsonObjectValue sum, JsonObjectValue state)
    {
        var result = sum.ToBuilder();
        foreach (var bucket in new[] { "models", "tools" })
        {
            var totals = ((JsonObjectValue)sum[bucket]).ToBuilder();
            foreach (var (key, usage) in (JsonObjectValue)state[bucket])
                totals[key] = totals.TryGetValue(key, out var total) ? AddUsage((JsonObjectValue)total, (JsonObjectValue)usage) : usage;
            result[bucket] = totals.ToImmutable();
        }
        return result.ToImmutable();
    }

    private static void Add(JsonObjectValue.Builder total, string key, double value) =>
        total[key] = JsonValue.Number((total.TryGetValue(key, out var current) ? Number(current) : 0) + value);

    private static void Add(DraftObject total, string key, double value) =>
        total.Set(key, JsonValue.Number((total.ContainsKey(key) ? Number(total.Get(key)) : 0) + value));

    private static double Number(JsonObjectValue value, string key) => Number(value[key]);

    private static double Number(JsonValue? value) => value is JsonNumberValue number ? number.Value : double.NaN;
}
