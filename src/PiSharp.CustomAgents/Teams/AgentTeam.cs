// Decision 0012: an agent team: one durable registry holding the agent catalog and the workflow catalog, opened over a storage as an
// AgentSession. Opening wraps the host settings so installed extensions are not selected by default (every agent selects exactly its own),
// configures the orchestrator as the root conversation, waits for tracked loads (plugins, scripts), and only then resumes scheduling, so a
// run a restart left pending finds its tools and workflows.
using PiSharp.CustomAgents.Workflows.Engine;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.CustomAgents.Teams;

/// <summary>Options of an agent team.</summary>
public sealed record AgentTeamOptions
{
    /// <summary>The models agents call (for example <c>NativeDurableModels</c>, or <c>FauxAgentModels</c> in tests).</summary>
    public required IDurableModels Models { get; init; }
    /// <summary>The model of definitions without one.</summary>
    public ModelRef? DefaultModel { get; init; }
    /// <summary>The thinking level of definitions without one.</summary>
    public string? DefaultThinking { get; init; }
    /// <summary>The definition the root conversation runs as; null: no orchestrator.</summary>
    public string? Orchestrator { get; init; }
    /// <summary>The registry to install into; default a new one.</summary>
    public IRegistry? Registry { get; init; }
    /// <summary>Harness settings; their <c>Extensions</c> are replaced with none.</summary>
    public HarnessSettings? Settings { get; init; }
    /// <summary>How deep subagents may nest below a root agent.</summary>
    public int MaxDepth { get; init; } = 2;
    /// <summary>How many live subagents one agent may have.</summary>
    public int MaxChildrenPerAgent { get; init; } = 8;
    /// <summary>How many agents a session may have in all.</summary>
    public int MaxAgents { get; init; } = 64;
    /// <summary>How long an <c>ask_agent</c> waits by default.</summary>
    public TimeSpan AskTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>The Harness clock (tests drive time with a fake provider).</summary>
    public TimeProvider? TimeProvider { get; init; }
    /// <summary>Builds the execution environment for tools (files, shell).</summary>
    public Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? Env { get; init; }
    /// <summary>Services for workflow actions.</summary>
    public IServiceProvider? Services { get; init; }
    /// <summary>Non-fatal failures the Harness reports.</summary>
    public Action<Exception>? OnReport { get; init; }
}

/// <summary>Agent definitions, tool sources and workflows, ready to open over a storage.</summary>
public sealed class AgentTeam
{
    /// <summary>A team with <paramref name="options"/>.</summary>
    public AgentTeam(AgentTeamOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MaxDepth < 0 || options.MaxChildrenPerAgent < 1 || options.MaxAgents < 1) throw new ArgumentOutOfRangeException(nameof(options));
        Registry = options.Registry ?? PiSharp.Durable.Registry.Create();
        Catalog = new AgentCatalog(Registry, new AgentCatalogOptions { DefaultModel = options.DefaultModel, DefaultThinking = options.DefaultThinking });
        Workflows = new WorkflowCatalog(Registry, new WorkflowCatalogOptions
        {
            Services = options.Services,
            DefaultModel = options.DefaultModel,
            DefaultThinking = options.DefaultThinking,
            TimeProvider = options.TimeProvider,
        }) { Agents = Catalog, Team = this };
        TeamTools.Install(this);
    }

    /// <summary>The options.</summary>
    public AgentTeamOptions Options { get; }
    /// <summary>The durable registry everything is installed into.</summary>
    public IRegistry Registry { get; }
    /// <summary>The agent definitions and tool sources.</summary>
    public AgentCatalog Catalog { get; }
    /// <summary>The workflows.</summary>
    public WorkflowCatalog Workflows { get; }

    /// <summary>Run by <see cref="OpenAsync"/> before scheduling resumes, in order (satellites restore what a session stored, such as the
    /// tools agents wrote).</summary>
    internal List<Func<IHarness, DurableContext, Task>> Opening { get; } = [];

    /// <summary>Opens the team over <paramref name="storage"/> (resuming whatever a previous session left running).</summary>
    public async Task<AgentSession> OpenAsync(IStorage storage, DurableContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        context ??= DurableContext.Background;
        var orchestrator = Options.Orchestrator is { } name
            ? Catalog.Find(name) ?? throw new InvalidOperationException($"The orchestrator '{name}' is not a registered agent.")
            : null;
        var harness = await Harness.OpenAsync(storage, new HarnessOptions
        {
            Models = Options.Models,
            Registry = Registry,
            Settings = new TeamSettings(Options.Settings),
            Env = Options.Env,
            TimeProvider = Options.TimeProvider,
            OnReport = Options.OnReport,
        }, context).ConfigureAwait(false);
        try
        {
            IConversation? root = null;
            if (orchestrator is not null)
            {
                var change = Catalog.ToAgentChange(orchestrator);
                root = await harness.RootAsync(context, change).ConfigureAwait(false);
                // A reopened root keeps its stored agent; apply the definition as it is now (keeping the deferred tools it loaded).
                await harness.CommitAsync(async tx =>
                {
                    await PiSharp.Durable.Agents.ConfigureAsync(tx, root.Id, change with { ModelTools = null }).ConfigureAwait(false);
                    await AgentDirectory.AddAsync(tx, new AgentEntry(orchestrator.Name, root.Id, orchestrator.Name, AgentKind.Orchestrator, null, 0, false))
                        .ConfigureAwait(false);
                }, context).ConfigureAwait(false);
            }
            foreach (var open in Opening) await open(harness, context).ConfigureAwait(false);
            await Catalog.WhenReadyAsync(context.CancellationToken).ConfigureAwait(false);
            var session = new AgentSession(this, harness, root);
            harness.Resume();
            return session;
        }
        catch
        {
            await harness.CloseAsync(context).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The host settings with no default extensions: a conversation the team did not configure selects nothing.</summary>
    private sealed class TeamSettings(HarnessSettings? inner) : HarnessSettings
    {
        public override IReadOnlyList<Extension>? Extensions => [];
        public override ConversationStreamOptions? Stream => inner?.Stream;
        public override RetrySettings? Retry => inner?.Retry;
        public override CompactionSettings? Compaction => inner?.Compaction;
        public override ProgressSettings? Progress => inner?.Progress;
        public override ToolExecutionMode? ToolExecution => inner?.ToolExecution;
        public override QueueMode? SteeringMode => inner?.SteeringMode;
        public override QueueMode? FollowUpMode => inner?.FollowUpMode;
        public override double? ContextRetentionMs => inner?.ContextRetentionMs;
        public override double? DocumentRetentionMs => inner?.DocumentRetentionMs;
    }
}

/// <summary>An agent's answer to one input.</summary>
/// <param name="Text">The answer's text.</param>
/// <param name="Message">The assistant message (pi-ai wire JSON).</param>
/// <param name="Submission">The settled submission.</param>
public sealed record AgentReply(string Text, JsonObjectValue Message, SubmissionRecord Submission);

/// <summary>An open agent team: its Harness, orchestrator and workflow runner.</summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly Action<AgentDefinition> changed;

    internal AgentSession(AgentTeam team, IHarness harness, IConversation? orchestrator)
    {
        Team = team;
        Harness = harness;
        Orchestrator = orchestrator;
        Workflows = new WorkflowRunner(harness, team.Workflows);
        Host = new Messenger(team, MessagingPort.ForHost(harness, team), MessagingCore.HostAddress);
        changed = definition => _ = ReapplyAsync(definition);
        team.Catalog.Changed += changed;
    }

    /// <summary>The team.</summary>
    public AgentTeam Team { get; }
    /// <summary>The durable Harness.</summary>
    public IHarness Harness { get; }
    /// <summary>The orchestrator (the root conversation); null when the team has none.</summary>
    public IConversation? Orchestrator { get; }
    /// <summary>Starts and attaches workflow runs.</summary>
    public WorkflowRunner Workflows { get; }
    /// <summary>Messages as the host (address <c>host</c>): read what agents send it, answer their questions, message them.</summary>
    public IAgentMessenger Host { get; }

    /// <summary>Starts an ownerless agent of <paramref name="definition"/> at <paramref name="address"/> (default <c>definition#n</c>).</summary>
    public async Task<IConversation> StartAgentAsync(string definition, string? address = null, DurableContext? context = null)
    {
        context ??= DurableContext.Background;
        var found = Team.Catalog.Find(definition) ?? throw new InvalidOperationException($"No agent '{definition}' is registered.");
        if (address is not null && !AgentDirectory.IsAddress(address)) throw new ArgumentException($"'{address}' is not an agent address.", nameof(address));
        var id = await Harness.CommitAsync(async tx =>
        {
            var doc = await tx.DocAsync(AgentDirectory.Doc).ConfigureAwait(false);
            if (address is not null && doc.GetObject("agents").Get(address) is JsonObjectValue)
                throw new InvalidOperationException($"The address '{address}' is taken.");
            if (doc.GetObject("agents").Keys.Count >= Team.Options.MaxAgents)
                throw new InvalidOperationException($"The team already has {Team.Options.MaxAgents} agents (AgentTeamOptions.MaxAgents).");
            var at = address ?? await AgentDirectory.NextAddressAsync(tx, found.Name).ConfigureAwait(false);
            var created = await tx.CreateConversationAsync(ConversationOwnership.Ownerless).ConfigureAwait(false);
            await PiSharp.Durable.Agents.ConfigureAsync(tx, created.Id, Team.Catalog.ToAgentChange(found)).ConfigureAwait(false);
            await AgentDirectory.AddAsync(tx, new AgentEntry(at, created.Id, found.Name, AgentKind.Peer, null, 0, false)).ConfigureAwait(false);
            return created.Id;
        }, context).ConfigureAwait(false);
        return (await Harness.ConversationAsync(id, context).ConfigureAwait(false))!;
    }

    /// <summary>The agent at <paramref name="address"/>; null when there is none.</summary>
    public async Task<AgentEntry?> FindAgentAsync(string address, DurableContext? context = null) =>
        AgentDirectory.Find(await Harness.SnapshotAsync(AgentDirectory.Doc, context ?? DurableContext.Background).ConfigureAwait(false), address);

    /// <summary>Every agent of the session.</summary>
    public async Task<IReadOnlyList<AgentEntry>> DirectoryAsync(DurableContext? context = null) =>
        AgentDirectory.All(await Harness.SnapshotAsync(AgentDirectory.Doc, context ?? DurableContext.Background).ConfigureAwait(false));

    /// <summary>A handle of the agent at <paramref name="address"/>; throws when there is none.</summary>
    public async Task<IConversation> AgentAsync(string address, DurableContext? context = null)
    {
        context ??= DurableContext.Background;
        var entry = await FindAgentAsync(address, context).ConfigureAwait(false) ?? throw new InvalidOperationException($"No agent at '{address}'.");
        return (await Harness.ConversationAsync(entry.ConversationId, context).ConfigureAwait(false))!;
    }

    /// <summary>Sends <paramref name="text"/> to <paramref name="conversation"/> (default the orchestrator) as user input and waits for the
    /// answer. A <paramref name="requestId"/> makes the input idempotent: a second send with it returns the first one's answer.</summary>
    public async Task<AgentReply> AskAsync(string text, IConversation? conversation = null, string? requestId = null, DurableContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        context ??= DurableContext.Background;
        conversation ??= Orchestrator ?? throw new InvalidOperationException("The team has no orchestrator; pass a conversation.");
        var submission = await conversation.SubmitAsync(SubmissionDraft.Input(text, requestId: requestId), context).ConfigureAwait(false);
        var settled = await submission.WaitAsync(context).ConfigureAwait(false);
        if (settled.Status != SubmissionStatus.Done || settled.Answer is not { } answer)
            throw new InvalidOperationException($"The agent did not answer ({settled.Status.ToString().ToLowerInvariant()}" +
                (settled.Reason is null ? "" : ": " + settled.Reason) + ")" +
                settled.Detail switch { null => "", JsonStringValue detail => ": " + detail.Value, var detail => ": " + detail.ToJson() } + ".");
        var message = await conversation.CommitAsync(async tx =>
        {
            var entry = await tx.GetEntryAsync(Entries.AssistantEntry, answer).ConfigureAwait(false);
            return entry?.Model is { Count: > 0 } messages ? messages[0] as JsonObjectValue : null;
        }, context).ConfigureAwait(false) ?? throw new InvalidOperationException("The agent's answer entry is missing.");
        return new AgentReply(ModelCalls.Text(message), message, settled);
    }

    /// <summary>Applies <paramref name="definition"/> as it is now to every live agent of it (model, instructions, selection); deferred tools
    /// an agent loaded stay loaded.</summary>
    public async Task ReapplyAsync(AgentDefinition definition, DurableContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        context ??= DurableContext.Background;
        try
        {
            var agents = (await DirectoryAsync(context).ConfigureAwait(false)).Where(entry => entry.Definition == definition.Name && !entry.Closed).ToList();
            if (agents.Count == 0) return;
            await Harness.CommitAsync(async tx =>
            {
                foreach (var entry in agents)
                    await PiSharp.Durable.Agents.ConfigureAsync(tx, entry.ConversationId,
                        Team.Catalog.ToAgentChange(definition, new AgentPlacement(entry.Depth)) with { ModelTools = null }).ConfigureAwait(false);
            }, context).ConfigureAwait(false);
        }
        catch (Exception error) when (context.CancellationToken.IsCancellationRequested is false && error is not OperationCanceledException)
        {
            Team.Options.OnReport?.Invoke(error);
        }
    }

    /// <summary>Closes the Harness; running work resumes when the team is opened again.</summary>
    public async ValueTask DisposeAsync()
    {
        Team.Catalog.Changed -= changed;
        await Harness.CloseAsync(DurableContext.Background).ConfigureAwait(false);
    }
}
