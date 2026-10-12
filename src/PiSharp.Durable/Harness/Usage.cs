// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/usage.ts (UsageDoc, recordUsage, addUsage). Usage values
// are pi-ai Usage wire JSON. TODO(P10): addUsageState and the Harness usage total join with the views phase.
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

    private static void Add(DraftObject total, string key, double value) =>
        total.Set(key, JsonValue.Number((total.ContainsKey(key) ? Number(total.Get(key)) : 0) + value));

    private static double Number(JsonObjectValue value, string key) => Number(value[key]);

    private static double Number(JsonValue? value) => value is JsonNumberValue number ? number.Value : double.NaN;
}
