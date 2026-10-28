// Decision 0012: the agent catalog: the definitions and tool sources of a team, installed into its durable registry. Each definition is the
// extension "agent.<name>"; each source is "tools.<id>". AgentChange from ToAgentChange sets every field and selects exactly the
// extensions the definition names (plus the team's messaging and orchestration extensions), so a conversation sees only its own tools. The
// catalog refuses a definition or a source change that would give an agent two tools with one name: durable resolves duplicates silently,
// the last selected extension winning.
using System.Collections.Immutable;
using PiSharp.CustomAgents.Tools;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;

namespace PiSharp.CustomAgents.Teams;

/// <summary>Defaults of an agent catalog.</summary>
public sealed record AgentCatalogOptions
{
    /// <summary>The model of definitions without one.</summary>
    public ModelRef? DefaultModel { get; init; }
    /// <summary>The thinking level of definitions without one; default off.</summary>
    public string? DefaultThinking { get; init; }
}

/// <summary>Where a conversation sits in a team: its depth below the orchestrator (or a root peer).</summary>
internal sealed record AgentPlacement(int Depth)
{
    public static AgentPlacement Root { get; } = new(0);
}

/// <summary>The agent definitions and tool sources of a team.</summary>
public sealed class AgentCatalog : IAgentProvider
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, AgentDefinition> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolSource> sources = new(StringComparer.Ordinal);
    private readonly List<Task> loads = [];
    private readonly ToolSourceValidator validator;

    /// <summary>A catalog installing into <paramref name="registry"/>.</summary>
    public AgentCatalog(IRegistry registry, AgentCatalogOptions? options = null)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Options = options ?? new AgentCatalogOptions();
        validator = (source, proposed) =>
        {
            lock (gate)
                foreach (var definition in definitions.Values.Where(definition => definition.ToolSources.Contains(source.Id)))
                    CheckToolsLocked(definition, source.Id, proposed);
        };
    }

    /// <summary>The registry definitions and sources are installed into.</summary>
    public IRegistry Registry { get; }
    /// <summary>The defaults.</summary>
    public AgentCatalogOptions Options { get; }

    /// <summary>The definitions, by name.</summary>
    public IReadOnlyList<AgentDefinition> Definitions
    {
        get { lock (gate) return [.. definitions.Values.OrderBy(definition => definition.Name, StringComparer.Ordinal)]; }
    }

    /// <summary>The tool sources, by ID.</summary>
    public IReadOnlyList<ToolSource> Sources
    {
        get { lock (gate) return [.. sources.Values.OrderBy(source => source.Id, StringComparer.Ordinal)]; }
    }

    /// <summary>Raised after a definition was registered or updated (an agent session reapplies it to that agent's live conversations).</summary>
    public event Action<AgentDefinition>? Changed;

    /// <summary>The team's messaging and orchestration extensions for an agent at a placement (set by the team).</summary>
    internal Func<AgentDefinition, AgentPlacement, IEnumerable<Extension>> SystemExtensions { get; set; } = (_, _) => [];
    /// <summary>More sections of an agent's extension, such as its roster of subagents (set by the team).</summary>
    internal Func<AgentDefinition, IEnumerable<PromptSection>> SystemSections { get; set; } = _ => [];
    /// <summary>Wraps of an agent's extension, such as narrowing the delegate tool to its subagents (set by the team).</summary>
    internal Func<AgentDefinition, IEnumerable<Wrap>> SystemWraps { get; set; } = _ => [];

    /// <summary>Adds <paramref name="definition"/>; throws when one with its name exists (use <see cref="Update"/>).</summary>
    public AgentCatalog Register(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        lock (gate)
        {
            if (definitions.ContainsKey(definition.Name))
                throw new InvalidOperationException($"Agent '{definition.Name}' is already registered; use Update to change it.");
            CheckToolsLocked(definition, null, null);
            definitions[definition.Name] = definition;
            Registry.Install(ExtensionFor(definition));
        }
        Changed?.Invoke(definition);
        return this;
    }

    /// <summary>Replaces the definition with <paramref name="definition"/>'s name; its agents see the change at their next request, and an
    /// agent session reapplies the model, instructions and selection to their conversations.</summary>
    public AgentCatalog Update(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        lock (gate)
        {
            if (!definitions.ContainsKey(definition.Name)) throw new InvalidOperationException($"Agent '{definition.Name}' is not registered.");
            CheckToolsLocked(definition, null, null);
            definitions[definition.Name] = definition;
            Registry.Install(ExtensionFor(definition));
        }
        Changed?.Invoke(definition);
        return this;
    }

    /// <summary>Removes the definition named <paramref name="name"/>; its live agents lose their own tools and sections.</summary>
    public bool Unregister(string name)
    {
        lock (gate)
        {
            if (!definitions.Remove(name, out var definition)) return false;
            Registry.Uninstall(new Extension { Name = definition.ExtensionName });
            return true;
        }
    }

    /// <summary>The definition named <paramref name="name"/>; null when not registered.</summary>
    public AgentDefinition? Find(string name)
    {
        lock (gate) return definitions.GetValueOrDefault(name);
    }

    /// <summary>Adds <paramref name="source"/> and installs it; agents whose <see cref="AgentDefinition.ToolSources"/> name it get its tools.</summary>
    public AgentCatalog AddSource(ToolSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (gate)
        {
            if (sources.ContainsKey(source.Id)) throw new InvalidOperationException($"A tool source '{source.Id}' is already added.");
            foreach (var definition in definitions.Values.Where(definition => definition.ToolSources.Contains(source.Id)))
                CheckToolsLocked(definition, source.Id, source.Tools);
            sources[source.Id] = source;
            source.AddValidator(validator);
            source.Attach(Registry);
        }
        return this;
    }

    /// <summary>Removes the source with <paramref name="id"/> and uninstalls its tools.</summary>
    public bool RemoveSource(string id)
    {
        lock (gate)
        {
            if (!sources.Remove(id, out var source)) return false;
            source.RemoveValidator(validator);
            source.Detach(Registry);
            return true;
        }
    }

    /// <summary>The source with <paramref name="id"/>; null when not added.</summary>
    public ToolSource? FindSource(string id)
    {
        lock (gate) return sources.GetValueOrDefault(id);
    }

    /// <summary>Completes once every load the catalog tracks (plugins, scripts) has finished; a team awaits it before resuming its harness,
    /// so runs a restart left pending find their tools.</summary>
    public async Task WhenReadyAsync(CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (gate) pending = [.. loads];
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Track(Task load)
    {
        lock (gate)
        {
            loads.RemoveAll(task => task.IsCompletedSuccessfully);
            loads.Add(load);
        }
    }

    /// <inheritdoc/>
    public AgentChange? Resolve(string agent) => Find(agent) is { } definition ? ToAgentChange(definition) : null;

    /// <summary>The configuration of a conversation for <paramref name="definition"/>: every field set, so nothing is inherited.</summary>
    public AgentChange ToAgentChange(AgentDefinition definition) => ToAgentChange(definition, AgentPlacement.Root);

    internal AgentChange ToAgentChange(AgentDefinition definition, AgentPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var model = definition.Model ?? Options.DefaultModel;
        var thinking = definition.Thinking ?? Options.DefaultThinking;
        return new AgentChange
        {
            Model = model is null ? Patch<ModelRef>.Clear : Patch<ModelRef>.Set(model),
            ThinkingLevel = thinking is null ? Patch<string>.Clear : Patch<string>.Set(thinking),
            Extensions = Patch<ExtensionSelection>.Set(ExtensionSelection.Only(SelectedExtensions(definition, placement))),
            Tools = definition.AllowedTools is { } allowed ? Patch<ToolSelection>.Set(ToolSelection.Only(allowed.Select(ByName))) : Patch<ToolSelection>.Clear,
            ModelTools = Patch<ModelToolSelection>.Clear,
            Instructions = definition.Instructions is null ? Patch<string>.Clear : Patch<string>.Set(definition.Instructions),
            Cwd = definition.Cwd is null ? Patch<string>.Clear : Patch<string>.Set(definition.Cwd),
        };
    }

    /// <summary>The extensions an agent selects, in order: the team's, its own, its sources', then the named ones. Selections are stored
    /// by name, so the agent sees a source's current tools.</summary>
    private IEnumerable<Extension> SelectedExtensions(AgentDefinition definition, AgentPlacement placement) =>
        SystemExtensions(definition, placement)
            .Append(new Extension { Name = definition.ExtensionName })
            .Concat(definition.ToolSources.Select(id => new Extension { Name = "tools." + id }))
            .Concat(definition.Extensions.Select(name => new Extension { Name = name }))
            .DistinctBy(extension => extension.Name, StringComparer.Ordinal);

    private Extension ExtensionFor(AgentDefinition definition) => new()
    {
        Name = definition.ExtensionName,
        Tools = definition.Tools,
        Sections =
        [
            new PromptSection { Key = "agent", Render = (_, _) => ValueTask.FromResult<string?>($"You are '{definition.Name}': {definition.Description}") },
            .. definition.Sections.Select(section => new PromptSection { Key = section.Key, Render = (_, _) => ValueTask.FromResult<string?>(section.Value) }),
            .. SystemSections(definition),
        ],
        Hooks = definition.Hooks,
        Wraps = [.. SystemWraps(definition)],
    };

    /// <summary>Throws when <paramref name="definition"/> would see two tools with one name; <paramref name="sourceId"/>'s tools are taken from
    /// <paramref name="proposed"/> when given.</summary>
    private void CheckToolsLocked(AgentDefinition definition, string? sourceId, IReadOnlyList<ToolRegistration>? proposed)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(IEnumerable<ToolRegistration>? tools, string origin)
        {
            foreach (var tool in tools ?? [])
            {
                if (seen.TryGetValue(tool.Name, out var first) && first != origin)
                    throw new ToolConflictException($"Agent '{definition.Name}' would get two tools named '{tool.Name}', from '{first}' and '{origin}'.");
                seen[tool.Name] = origin;
            }
        }
        var snapshot = Registry.Snapshot();
        foreach (var extension in SystemExtensions(definition, AgentPlacement.Root)) Add(extension.Tools, extension.Name);
        Add(definition.Tools, definition.ExtensionName);
        foreach (var id in definition.ToolSources)
            Add(id == sourceId ? proposed : sources.GetValueOrDefault(id)?.Tools, "tools." + id);
        foreach (var name in definition.Extensions) Add(snapshot.GetExtension(name)?.Tools, name);
    }

    private static ToolRegistration ByName(string name) => new()
    {
        Name = name,
        Description = name,
        Execute = (_, _, _) => throw new InvalidOperationException("A selection placeholder never runs."),
    };
}
