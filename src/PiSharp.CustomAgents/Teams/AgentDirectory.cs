// Decision 0012: the agent directory, a session document listing every agent conversation of a team by address: the orchestrator (named
// after its definition), peers started by the host, and subagents (<definition>#<n>). Messaging resolves addresses through it, and a
// definition update is reapplied to every conversation it lists for that definition.
using System.Globalization;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.CustomAgents.Teams;

/// <summary>How an agent conversation joined the team.</summary>
public enum AgentKind
{
    /// <summary>The team's orchestrator (the root conversation).</summary>
    Orchestrator,
    /// <summary>An ownerless agent the host started.</summary>
    Peer,
    /// <summary>A subagent another agent delegated to or spawned.</summary>
    Child,
}

/// <summary>One agent conversation of a team.</summary>
/// <param name="Address">Its address (unique in the session).</param>
/// <param name="ConversationId">Its conversation.</param>
/// <param name="Definition">Its definition's name.</param>
/// <param name="Kind">How it joined.</param>
/// <param name="Parent">The address of the agent that started it (children only).</param>
/// <param name="Depth">Its depth below a root agent (0 for the orchestrator and peers).</param>
/// <param name="Closed">Whether it was stopped.</param>
public sealed record AgentEntry(string Address, ConversationId ConversationId, string Definition, AgentKind Kind, string? Parent, int Depth, bool Closed);

internal static partial class AgentDirectory
{
    /// <summary><c>{agents: {address: entry}, byConversation: {id: address}, counters: {definition: n}, waits: {toolTaskId: edge}}</c>.</summary>
    public static SessionDocToken Doc { get; } = Documents.DefineSessionDoc("pisharp.agents.directory", 1, () => JsonValue.Object([
        KeyValuePair.Create("agents", (JsonValue)JsonObjectValue.Empty),
        KeyValuePair.Create("byConversation", (JsonValue)JsonObjectValue.Empty),
        KeyValuePair.Create("counters", (JsonValue)JsonObjectValue.Empty),
        KeyValuePair.Create("waits", (JsonValue)JsonObjectValue.Empty),
    ]));

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_.-]{0,47}(#[0-9]{1,6})?$")]
    private static partial System.Text.RegularExpressions.Regex AddressPattern();

    public static bool IsAddress(string? address) => address is not null && AddressPattern().IsMatch(address) && address is not ("host" or "parent" or "self");

    /// <summary>Adds <paramref name="entry"/>; throws when its address is taken by a live agent of another conversation.</summary>
    public static async Task AddAsync(ITx tx, AgentEntry entry)
    {
        var doc = await tx.DocAsync(Doc).ConfigureAwait(false);
        var agents = doc.GetObject("agents");
        if (agents.Get(entry.Address) is JsonObjectValue existing && Read(entry.Address, existing) is { Closed: false } live &&
            live.ConversationId != entry.ConversationId)
            throw new InvalidOperationException($"The address '{entry.Address}' is taken by conversation {live.ConversationId.Value}.");
        agents.Set(entry.Address, ToJson(entry));
        doc.GetObject("byConversation").Set(Key(entry.ConversationId), entry.Address);
    }

    /// <summary>The next free address for a new agent of <paramref name="definition"/>: <c>definition#n</c>.</summary>
    public static async Task<string> NextAddressAsync(ITx tx, string definition)
    {
        var doc = await tx.DocAsync(Doc).ConfigureAwait(false);
        var counters = doc.GetObject("counters");
        var next = (counters.Get(definition) is JsonNumberValue number ? (int)number.Value : 0) + 1;
        counters.Set(definition, next);
        return definition + "#" + next.ToString(CultureInfo.InvariantCulture);
    }

    public static async Task CloseAsync(ITx tx, string address)
    {
        var doc = await tx.DocAsync(Doc).ConfigureAwait(false);
        var agents = doc.GetObject("agents");
        if (agents.Get(address) is JsonObjectValue entry) agents.Set(address, entry.SetItem("closed", true));
    }

    public static AgentEntry? Find(JsonObjectValue? doc, string address) =>
        doc is not null && doc.TryGetValue("agents", out var agents) && agents is JsonObjectValue map && map.TryGetValue(address, out var entry) &&
        entry is JsonObjectValue value ? Read(address, value) : null;

    public static AgentEntry? FindByConversation(JsonObjectValue? doc, ConversationId conversation) =>
        doc is not null && doc.TryGetValue("byConversation", out var index) && index is JsonObjectValue map &&
        map.TryGetValue(Key(conversation), out var address) && address is JsonStringValue text ? Find(doc, text.Value) : null;

    public static IReadOnlyList<AgentEntry> All(JsonObjectValue? doc) =>
        doc is not null && doc.TryGetValue("agents", out var agents) && agents is JsonObjectValue map
            ? [.. map.Where(pair => pair.Value is JsonObjectValue).Select(pair => Read(pair.Key, (JsonObjectValue)pair.Value))]
            : [];

    private static string Key(ConversationId conversation) => conversation.Value.ToString(CultureInfo.InvariantCulture);

    private static JsonObjectValue ToJson(AgentEntry entry) => Workflows.Engine.RunDocs.Object(
        ("conversationId", entry.ConversationId.Value),
        ("definition", entry.Definition),
        ("kind", entry.Kind.ToString().ToLowerInvariant()),
        ("parent", Workflows.Engine.RunDocs.Opt(entry.Parent)),
        ("depth", entry.Depth),
        ("closed", entry.Closed ? (JsonValue)true : null));

    private static AgentEntry Read(string address, JsonObjectValue value)
    {
        string? Text(string key) => value.TryGetValue(key, out var item) && item is JsonStringValue text ? text.Value : null;
        double Number(string key) => value.TryGetValue(key, out var item) && item is JsonNumberValue number ? number.Value : 0;
        var kind = Text("kind") switch { "orchestrator" => AgentKind.Orchestrator, "child" => AgentKind.Child, _ => AgentKind.Peer };
        return new AgentEntry(address, new ConversationId((long)Number("conversationId")), Text("definition") ?? "", kind, Text("parent"),
            (int)Number("depth"), value.TryGetValue("closed", out var closed) && closed is JsonBooleanValue { Value: true });
    }
}
