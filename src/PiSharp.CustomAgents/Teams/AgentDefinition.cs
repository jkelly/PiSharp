// Decision 0012: an agent definition: the role, model and tools of one kind of agent. A team installs each definition as the extension
// "agent.<name>" (its tools, sections and hooks) and configures a conversation for it with an AgentChange that sets every field, so an
// agent never inherits its parent's model, instructions or tools.
using System.Collections.Immutable;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Teams;

/// <summary>How a <c>delegate</c> call waits for its subagent.</summary>
public enum DelegationMode
{
    /// <summary>The call waits but returns early when the subagent asks a question or runs long; the answer then arrives as a message.</summary>
    Interactive,
    /// <summary>The call waits for the subagent's answer; the subagent cannot ask its parent questions meanwhile.</summary>
    Strict,
}

/// <summary>Which agents an agent may message.</summary>
public enum MessagingScope
{
    /// <summary>No messaging tools.</summary>
    None,
    /// <summary>Its parent, its children and its siblings (the default).</summary>
    Family,
    /// <summary>Every agent of the team.</summary>
    Team,
}

/// <summary>The definition of one kind of agent.</summary>
public sealed partial record AgentDefinition
{
    /// <summary>The name (<c>^[a-z][a-z0-9_-]{0,31}$</c>), used to delegate to it and in addresses.</summary>
    public required string Name { get; init; }
    /// <summary>What the agent does; shown to agents that can delegate to it.</summary>
    public required string Description { get; init; }
    /// <summary>The model; default the team's.</summary>
    public ModelRef? Model { get; init; }
    /// <summary>The thinking level (off, minimal, low, medium, high, xhigh); default the team's.</summary>
    public string? Thinking { get; init; }
    /// <summary>The agent's instructions (the last system prompt section).</summary>
    public string? Instructions { get; init; }
    /// <summary>More system prompt sections, by key, rendered before the instructions.</summary>
    public IReadOnlyDictionary<string, string> Sections { get; init; } = ImmutableDictionary<string, string>.Empty;
    /// <summary>Tools of this agent only (see <see cref="CustomAgents.Tools.AgentTool"/>).</summary>
    public IReadOnlyList<ToolRegistration> Tools { get; init; } = [];
    /// <summary>IDs of tool sources whose tools the agent gets (see <see cref="AgentCatalog.AddSource"/>).</summary>
    public IReadOnlyList<string> ToolSources { get; init; } = [];
    /// <summary>Names of other installed durable extensions to select (for example the Durable.Tools file tools).</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];
    /// <summary>When set, only these tools are enabled (by name), whichever source has them.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }
    /// <summary>Durable hook registrations of the agent's extension.</summary>
    public IReadOnlyList<HookRegistration> Hooks { get; init; } = [];
    /// <summary>The agents this one may delegate to or spawn; empty: no orchestration tools.</summary>
    public IReadOnlyList<string> Subagents { get; init; } = [];
    /// <summary>How <c>delegate</c> waits.</summary>
    public DelegationMode Delegation { get; init; } = DelegationMode.Interactive;
    /// <summary>Which agents it may message.</summary>
    public MessagingScope Messaging { get; init; } = MessagingScope.Family;
    /// <summary>Whether it may ask the host (a person) questions with <c>ask_agent("host")</c>.</summary>
    public bool CanAskHost { get; init; }
    /// <summary>Whether it may author JavaScript tools (needs the team's JavaScript authoring enabled too).</summary>
    public bool CanCreateTools { get; init; }
    /// <summary>The working directory passed to the Harness environment.</summary>
    public string? Cwd { get; init; }

    /// <summary>The extension holding the agent's own tools, sections and hooks.</summary>
    public string ExtensionName => "agent." + Name;

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial System.Text.RegularExpressions.Regex NamePattern();

    /// <summary>Whether <paramref name="name"/> is a valid agent name.</summary>
    public static bool IsName(string? name) => name is not null && NamePattern().IsMatch(name);

    internal void Validate()
    {
        if (!IsName(Name)) throw new ArgumentException($"'{Name}' is not an agent name (lowercase letter, then up to 31 of a-z, 0-9, '_', '-').");
        if (string.IsNullOrWhiteSpace(Description)) throw new ArgumentException($"Agent '{Name}' needs a description.");
        if (Name is "host" or "parent" or "self") throw new ArgumentException($"'{Name}' is a reserved address, not an agent name.");
        foreach (var sub in Subagents)
            if (!IsName(sub)) throw new ArgumentException($"Agent '{Name}' lists '{sub}' as a subagent, which is not an agent name.");
        foreach (var key in Sections.Keys)
            if (string.IsNullOrWhiteSpace(key) || key == PiSharp.Durable.Agents.InstructionsKey)
                throw new ArgumentException($"Agent '{Name}' has an invalid section key '{key}'.");
    }
}
