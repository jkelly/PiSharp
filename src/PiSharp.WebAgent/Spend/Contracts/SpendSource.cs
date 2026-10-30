// Decision 0011 (run W3, S0): where a provider request was started, the Spend Guard's "source" (mockups settings-spend, history-spend). A cap
// can apply to one source, and the ledger records and filters by it. The wire names are fixed (stored in webagent.db, sent in
// metadata.pisharp.source and in the pages' JSON): chat, playground, compare, agent-compare, bench, arena, drift, cache-warm.
using System.Text.Json.Serialization;

namespace PiSharp.WebAgent.Spend;

/// <summary>Where a provider request was started. Stored and sent by its wire name (<see cref="SpendSources.Wire"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpendSource>))]
public enum SpendSource
{
    /// <summary>A chat (<c>chat</c>): every model call of a durable chat session, summaries included.</summary>
    [JsonStringEnumMemberName("chat")] Chat,

    /// <summary>The Playground (<c>playground</c>, W4).</summary>
    [JsonStringEnumMemberName("playground")] Playground,

    /// <summary>Compare (<c>compare</c>, W4): one request per column.</summary>
    [JsonStringEnumMemberName("compare")] Compare,

    /// <summary>Agent compare (<c>agent-compare</c>, W4): every request of the compared agents.</summary>
    [JsonStringEnumMemberName("agent-compare")] AgentCompare,

    /// <summary>Tool Bench (<c>bench</c>, W4).</summary>
    [JsonStringEnumMemberName("bench")] Bench,

    /// <summary>Model Arena (<c>arena</c>, W5).</summary>
    [JsonStringEnumMemberName("arena")] Arena,

    /// <summary>Drift Watch suites (<c>drift</c>, W5).</summary>
    [JsonStringEnumMemberName("drift")] Drift,

    /// <summary>A cache-warming request that keeps a prompt cache alive (<c>cache-warm</c>); its spend counts as cache waste.</summary>
    [JsonStringEnumMemberName("cache-warm")] CacheWarm,
}

/// <summary>Wire names and labels of <see cref="SpendSource"/>.</summary>
public static class SpendSources
{
    private static readonly (SpendSource Source, string Wire, string Label)[] Table =
    [
        (SpendSource.Chat, "chat", "Chat"),
        (SpendSource.Playground, "playground", "Playground"),
        (SpendSource.Compare, "compare", "Compare"),
        (SpendSource.AgentCompare, "agent-compare", "Agent compare"),
        (SpendSource.Bench, "bench", "Tool Bench"),
        (SpendSource.Arena, "arena", "Arena"),
        (SpendSource.Drift, "drift", "Drift Watch"),
        (SpendSource.CacheWarm, "cache-warm", "Cache warm"),
    ];

    /// <summary>Every source, in display order.</summary>
    public static IReadOnlyList<SpendSource> All { get; } = [.. Table.Select(row => row.Source)];

    /// <summary>The source's wire name, such as <c>cache-warm</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a defined source.</exception>
    public static string Wire(this SpendSource source) => Row(source).Wire;

    /// <summary>The source's label on the pages, such as <c>Tool Bench</c> or <c>Drift Watch</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a defined source.</exception>
    public static string Label(this SpendSource source) => Row(source).Label;

    /// <summary>The source named <paramref name="wire"/> (a wire name, ignoring case and surrounding spaces).</summary>
    public static bool TryParse(string? wire, out SpendSource source)
    {
        var name = wire?.Trim();
        foreach (var row in Table)
        {
            if (string.Equals(row.Wire, name, StringComparison.OrdinalIgnoreCase))
            {
                source = row.Source;
                return true;
            }
        }
        source = default;
        return false;
    }

    /// <summary>The source named <paramref name="wire"/>.</summary>
    /// <exception cref="ArgumentException">Not a source's wire name; the message lists them.</exception>
    public static SpendSource Parse(string? wire) => TryParse(wire, out var source) ? source
        : throw new ArgumentException($"Unknown source \"{wire}\": use one of {string.Join(", ", Table.Select(row => row.Wire))}.");

    private static (SpendSource Source, string Wire, string Label) Row(SpendSource source)
    {
        foreach (var row in Table) if (row.Source == source) return row;
        throw new ArgumentOutOfRangeException(nameof(source), source, "Not a spend source.");
    }
}
