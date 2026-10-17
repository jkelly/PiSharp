// PiSharp-only (decision 0008). Naming and addressing follow durable-pi (apps/dpi-backend/src/store.ts): sessions by UUID or name, agents by
// number, "main" or name, derived agent names, and "ambiguous" for a name several share. The derived name of a second root is "Agent #n", as
// in the approved mockups (durable-pi says "Root #n").
namespace PiSharp.Durable.Hosting;

/// <summary>Session and agent names.</summary>
internal static class HostNames
{
    /// <summary>The longest name.</summary>
    public const int MaxLength = 100;

    /// <summary>The name of a session created without one.</summary>
    public const string DefaultSessionName = "New session";

    /// <summary>The thinking levels an agent accepts.</summary>
    public static IReadOnlyList<string> ThinkingLevels { get; } = ["off", "minimal", "low", "medium", "high", "xhigh"];

    /// <summary>The name an agent has without a custom one.</summary>
    public static string Derived(long id, AgentKind kind) => id == ConversationId.Root.Value ? "Main" : kind switch
    {
        AgentKind.Fork => $"Fork #{id}",
        AgentKind.Subagent => $"Subagent #{id}",
        _ => $"Agent #{id}",
    };

    /// <summary>A session name: trimmed and cut to <see cref="MaxLength"/>; blank is <see cref="DefaultSessionName"/>.</summary>
    public static string Session(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length == 0 ? DefaultSessionName : Cut(trimmed);
    }

    /// <summary>A custom agent name, or null for a blank one (which restores the derived name).</summary>
    /// <exception cref="HostInvalidException">All digits, which would read as a conversation number; or <c>main</c> for an agent other than
    /// conversation 1.</exception>
    public static string? Agent(string? name, long id)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        trimmed = Cut(trimmed);
        if (trimmed.All(char.IsAsciiDigit)) throw new HostInvalidException("An agent name cannot be only digits.");
        if (id != ConversationId.Root.Value && trimmed.Equals("main", StringComparison.OrdinalIgnoreCase))
            throw new HostInvalidException("Only agent #1 can be called main.");
        return trimmed;
    }

    /// <summary>Throws <see cref="HostInvalidException"/> for an unknown thinking level.</summary>
    public static string Thinking(string level) => ThinkingLevels.Contains(level, StringComparer.Ordinal)
        ? level
        : throw new HostInvalidException($"Unknown thinking level {level}. Levels: {string.Join(", ", ThinkingLevels)}.");

    private static string Cut(string text) => text.Length > MaxLength ? text[..MaxLength] : text;
}
