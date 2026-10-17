// PiSharp-only (decision 0008). durable-pi keeps a fixed WRITE_TOOLS list (apps/dpi-backend/src/store.ts); here the classification is a
// table a host can extend, and a tool it does not know counts as a writer, so a new tool is never offered to a read-only agent by accident.
using System.Collections.Frozen;

namespace PiSharp.Durable.Hosting;

/// <summary>What a tool can do outside the conversation.</summary>
public enum ToolEffect
{
    /// <summary>Only reads; offered to a read-only agent.</summary>
    Read,
    /// <summary>Changes files or state.</summary>
    Write,
    /// <summary>Runs commands or code, or starts other work.</summary>
    Exec,
}

/// <summary>Tool effects by tool name, which decide what the read-only switch removes.</summary>
public sealed class ToolEffects
{
    private readonly FrozenDictionary<string, ToolEffect> effects;

    private ToolEffects(IEnumerable<KeyValuePair<string, ToolEffect>> effects) =>
        this.effects = effects.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The default table. <c>read</c>, <c>read_state</c>, <c>search_history</c>, <c>list_durable_jobs</c>, <c>check_durable_job</c>,
    /// <c>tool_search</c>, <c>ask_user_question</c>, <c>exit_plan_mode</c> and <c>handoff</c> read. <c>write</c>, <c>edit</c> and
    /// <c>update_state</c> write. <c>bash</c>, <c>powershell</c>, <c>codemode</c>, <c>subagent</c>, <c>start_session</c> and
    /// <c>run_durable_job</c> execute.
    /// </summary>
    public static ToolEffects Default { get; } = new(new Dictionary<string, ToolEffect>
    {
        ["read"] = ToolEffect.Read,
        ["read_state"] = ToolEffect.Read,
        ["search_history"] = ToolEffect.Read,
        ["list_durable_jobs"] = ToolEffect.Read,
        ["check_durable_job"] = ToolEffect.Read,
        ["tool_search"] = ToolEffect.Read,
        ["ask_user_question"] = ToolEffect.Read,
        ["exit_plan_mode"] = ToolEffect.Read,
        ["handoff"] = ToolEffect.Read,
        ["write"] = ToolEffect.Write,
        ["edit"] = ToolEffect.Write,
        ["update_state"] = ToolEffect.Write,
        ["bash"] = ToolEffect.Exec,
        ["powershell"] = ToolEffect.Exec,
        ["codemode"] = ToolEffect.Exec,
        ["subagent"] = ToolEffect.Exec,
        ["start_session"] = ToolEffect.Exec,
        ["run_durable_job"] = ToolEffect.Exec,
    });

    /// <summary>A copy of this table with <paramref name="tool"/> classified as <paramref name="effect"/>.</summary>
    public ToolEffects With(string tool, ToolEffect effect)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        var copy = effects.ToDictionary(StringComparer.Ordinal);
        copy[tool] = effect;
        return new ToolEffects(copy);
    }

    /// <summary>The effect of <paramref name="tool"/>; <see cref="ToolEffect.Write"/> when the table does not know it.</summary>
    public ToolEffect Of(string tool) => effects.TryGetValue(tool, out var effect) ? effect : ToolEffect.Write;

    /// <summary>Whether a read-only agent loses <paramref name="tool"/>.</summary>
    public bool IsWriter(string tool) => Of(tool) != ToolEffect.Read;
}
