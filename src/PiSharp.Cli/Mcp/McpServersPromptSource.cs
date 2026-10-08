// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts
// (before_agent_start: renderServersSection(servers) into systemPromptOptions.sections).
using System.Collections.Immutable;
using PiSharp.Extensions.Mcp.Configuration;

namespace PiSharp.Cli.Mcp;

/// <summary>The servers the `mcp_servers` prompt section lists, as they are now: every configured server of an acquired
/// runtime generation, with the instructions of those that connected. The session renders it at every prompt start;
/// a changed section is appended to the conversation (see <see cref="McpServersSection.Diff"/>), for example after a
/// background server connected. It holds metadata only and grants no connection or tool authority.</summary>
public sealed class McpServersPromptSource
{
    /// <summary>Generations kept for rendering; older ones belong to retired attachments.</summary>
    private const int RetainedGenerations = 8;
    private readonly object gate = new();
    private readonly SortedDictionary<long, State> generations = [];

    private sealed record State(ImmutableArray<McpServerEntry> Servers, ImmutableDictionary<string, string> Instructions);

    /// <summary>Records the configured servers of <paramref name="generation"/> and the servers already connected.</summary>
    public void Publish(long generation, McpServerCatalog catalog, IEnumerable<McpServerToolSnapshot> connected)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1); ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(connected);
        if (catalog.Servers.IsDefault) throw new ArgumentException("Initialized server catalog required.", nameof(catalog));
        var instructions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in connected)
            if (snapshot.Connected && snapshot.Instructions is { } text) instructions[snapshot.Entry.Name] = text;
        lock (gate)
        {
            generations[generation] = new(catalog.Servers, instructions.ToImmutable());
            while (generations.Count > RetainedGenerations) generations.Remove(generations.Keys.First());
        }
    }

    /// <summary>Records a server of <paramref name="generation"/> that connected later. False when that generation is unknown.</summary>
    public bool Connected(long generation, McpServerToolSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            if (!generations.TryGetValue(generation, out var state) || !state.Servers.Any(server => server.Name == snapshot.Entry.Name)) return false;
            var instructions = snapshot.Connected && snapshot.Instructions is { } text ? state.Instructions.SetItem(snapshot.Entry.Name, text)
                : state.Instructions.Remove(snapshot.Entry.Name);
            generations[generation] = state with { Instructions = instructions };
            return true;
        }
    }

    /// <summary>The configured servers of <paramref name="generation"/> changed (`/mcp` enabled, disabled or changed the exposure of
    /// one), as the original renders the section from its current servers. Instructions of servers still listed are kept.</summary>
    public bool ReplaceServers(long generation, ImmutableArray<McpServerEntry> servers)
    {
        if (servers.IsDefault) throw new ArgumentException("Initialized servers required.", nameof(servers));
        lock (gate)
        {
            if (!generations.TryGetValue(generation, out var state)) return false;
            var names = servers.Select(server => server.Name).ToHashSet(StringComparer.Ordinal);
            generations[generation] = new(servers, state.Instructions.RemoveRange(state.Instructions.Keys.Where(name => !names.Contains(name))));
            return true;
        }
    }

    /// <summary>The untagged section content for <paramref name="generation"/> (the latest when null), or null when no
    /// enabled server has codemode or deferred tools, or nothing was published for it.</summary>
    public string? Render(long? generation = null)
    {
        State? state;
        lock (gate)
        {
            if (generation is { } exact) generations.TryGetValue(exact, out state);
            else state = generations.Count == 0 ? null : generations.Last().Value;
        }
        return state is null ? null : McpServersSection.Render(state.Servers.Select(server =>
            new McpServerListing(server, state.Instructions.GetValueOrDefault(server.Name))));
    }
}
