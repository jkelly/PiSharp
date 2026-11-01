// Decision 0012: tool sources. A source is a named, changing set of tools installed as one durable extension, "tools.<id>"; agents select
// it by name, so a change reaches every agent that selects it at its next model request without reconfiguring them. A change installs the
// whole extension again (Registry.Install replaces by name). When a tool is removed mid-run, the next request no longer offers it, a call
// that has not started gets a tool_unavailable error, and a call already executing finishes on the code it started with. A change and a
// catalog check each hold ChangeGate first: a change validates against catalogs (their lock inside its own) while a catalog reads sources'
// tools (their locks inside its own), so without one lock taken first the two orders could deadlock.
using System.Collections.Immutable;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Tools;

/// <summary>Raised when a tool change would give an agent two tools with one name.</summary>
public sealed class ToolConflictException(string message) : InvalidOperationException(message);

/// <summary>Checks a proposed tool set of a source before it applies; throws <see cref="ToolConflictException"/> to refuse it.</summary>
internal delegate void ToolSourceValidator(ToolSource source, IReadOnlyList<ToolRegistration> proposed);

/// <summary>
/// A named, changing set of tools, installed as the extension <see cref="ExtensionName"/>. Derive from it to load tools from somewhere
/// (scripts, plugins) and call <see cref="Publish"/> with each new set; <see cref="DynamicToolSource"/> is the one changed in code.
/// </summary>
public abstract class ToolSource
{
    /// <summary>Held around every tool change and every catalog check of tools, before the source's or the catalog's own lock.</summary>
    internal static readonly Lock ChangeGate = new();

    private readonly Lock gate = new();
    private readonly List<IRegistry> registries = [];
    private readonly List<ToolSourceValidator> validators = [];
    private ImmutableArray<ToolRegistration> tools = [];

    /// <summary>A source named <paramref name="id"/> (lowercase letters, digits, <c>_</c>, <c>.</c>, <c>-</c>).</summary>
    protected ToolSource(string id)
    {
        Id = SourceIds.Validate(id);
        ExtensionName = "tools." + id;
    }

    /// <summary>The source ID.</summary>
    public string Id { get; }
    /// <summary>The durable extension holding the tools: <c>tools.&lt;id&gt;</c>.</summary>
    public string ExtensionName { get; }
    /// <summary>The current tools.</summary>
    public IReadOnlyList<ToolRegistration> Tools { get { lock (gate) return tools; } }
    /// <summary>Raised after the tools changed.</summary>
    public event Action<ToolSource>? Changed;

    /// <summary>
    /// Replaces the source's tools with <paramref name="next"/>, computed from the current ones, in one install. Throws
    /// <see cref="ArgumentException"/> for two tools with one name and <see cref="ToolConflictException"/> when an agent selecting this source
    /// would see a tool name twice; the tools are then unchanged.
    /// </summary>
    protected void Publish(Func<ImmutableArray<ToolRegistration>, ImmutableArray<ToolRegistration>> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (ChangeGate)
        lock (gate)
        {
            var proposed = next(tools);
            if (proposed == tools) return;
            var duplicate = proposed.GroupBy(tool => tool.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null) throw new ArgumentException($"Tool source '{Id}' would have two tools named '{duplicate.Key}'.");
            foreach (var validate in validators) validate(this, proposed);
            tools = proposed;
            var extension = Extension(proposed);
            foreach (var registry in registries) registry.Install(extension);
        }
        Changed?.Invoke(this);
    }

    /// <summary>Installs the source into <paramref name="registry"/> and keeps it current there.</summary>
    internal void Attach(IRegistry registry)
    {
        lock (gate)
        {
            if (registries.Contains(registry)) return;
            registries.Add(registry);
            registry.Install(Extension(tools));
        }
    }

    /// <summary>Uninstalls the source from <paramref name="registry"/>.</summary>
    internal void Detach(IRegistry registry)
    {
        lock (gate)
        {
            if (!registries.Remove(registry)) return;
            registry.Uninstall(Extension(tools));
        }
    }

    internal void AddValidator(ToolSourceValidator validator) { lock (gate) validators.Add(validator); }

    internal void RemoveValidator(ToolSourceValidator validator) { lock (gate) validators.Remove(validator); }

    private Extension Extension(ImmutableArray<ToolRegistration> list) => new() { Name = ExtensionName, Tools = list };
}

/// <summary>A tool source changed in code: add, replace and remove tools while agents run.</summary>
public sealed class DynamicToolSource : ToolSource
{
    /// <summary>A source named <paramref name="id"/>, optionally with initial tools.</summary>
    public DynamicToolSource(string id, IEnumerable<ToolRegistration>? tools = null) : base(id)
    {
        if (tools is not null) Set(tools);
    }

    /// <summary>Adds <paramref name="tool"/>; throws when the source already has a tool with its name.</summary>
    public DynamicToolSource Add(ToolRegistration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Publish(current => current.Any(item => item.Name == tool.Name)
            ? throw new ArgumentException($"Tool source '{Id}' already has a tool '{tool.Name}'; use Replace.", nameof(tool))
            : current.Add(tool));
        return this;
    }

    /// <summary>Adds <paramref name="tool"/>, or replaces the tool with its name.</summary>
    public DynamicToolSource Replace(ToolRegistration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        Publish(current => IndexOf(current, tool.Name) is var index and >= 0 ? current.SetItem(index, tool) : current.Add(tool));
        return this;
    }

    /// <summary>Removes the tool named <paramref name="name"/>; false when there is none.</summary>
    public bool Remove(string name)
    {
        var removed = false;
        Publish(current =>
        {
            var index = IndexOf(current, name);
            removed = index >= 0;
            return removed ? current.RemoveAt(index) : current;
        });
        return removed;
    }

    /// <summary>Replaces every tool at once (one install).</summary>
    public DynamicToolSource Set(IEnumerable<ToolRegistration> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var list = replacement.ToImmutableArray();
        Publish(_ => list);
        return this;
    }

    private static int IndexOf(ImmutableArray<ToolRegistration> tools, string name)
    {
        for (var index = 0; index < tools.Length; index++) if (tools[index].Name == name) return index;
        return -1;
    }
}

internal static partial class SourceIds
{
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();

    public static string Validate(string id) => id is not null && Pattern().IsMatch(id) ? id
        : throw new ArgumentException($"'{id}' is not a tool source ID (lowercase letters, digits, '_', '.' or '-').", nameof(id));
}
