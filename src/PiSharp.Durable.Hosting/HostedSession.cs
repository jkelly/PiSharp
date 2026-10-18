// PiSharp-only (decision 0008): one session of a durable device, after durable-pi's per-session store logic (apps/dpi-backend/src/store.ts):
// agents by number or name with derived names, busy state from the task graph, idempotent sends that can wait for the answer, forks, the
// read-only switch, per-session compaction, and model re-routing on reopen.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Tools;

namespace PiSharp.Durable.Hosting;

/// <summary>One durable session of a device: one Harness on one SQLite file, in its own directory. Agents are its conversations.</summary>
public sealed class HostedSession
{
    /// <summary>The longest instructions an agent keeps.</summary>
    public const int MaxInstructionsLength = 12000;

    /// <summary>The longest message.</summary>
    public const int MaxMessageLength = 100_000;

    private readonly DurableDeviceHost host;
    private readonly ExecutionEnvPool envs;
    private volatile string name;
    private volatile ModelRef? model;
    private volatile SessionCompaction? stored;
    private volatile Exception? error;
    private volatile bool closing;
    private IHarness? harness;
    private FileLock? fileLock;
    private IDisposable? commits;
    private IRegistry? registry;
    private IReadOnlyDictionary<string, DurableTask> jobTasks = new Dictionary<string, DurableTask>();
    private readonly SemaphoreSlim creations = new(1, 1);

    /// <summary>A creation key's record (<c>{conversation, request}</c>), written in the commit that creates the conversation, so a retried
    /// creation finds the first one's agent (the device server's <c>Idempotency-Key</c>).</summary>
    private static readonly SessionDocFamilyToken CreationKeys = Documents.DefineSessionDocFamily("pisharp.creation-key", 1, _ => JsonObjectValue.Empty);

    internal HostedSession(DurableDeviceHost host, CatalogSession row)
    {
        this.host = host;
        Id = row.Id;
        name = row.Name;
        Cwd = row.Cwd;
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt);
        model = row.Provider is { } provider && row.Model is { } modelId ? new ModelRef(provider, modelId) : null;
        DirectoryPath = Path.Join(host.SessionsDirectory, row.Id);
        envs = new ExecutionEnvPool(row.Cwd);
        try
        {
            stored = SessionCompaction.FromJson(row.Compaction);
        }
        catch (System.Text.Json.JsonException failure)
        {
            host.Options.OnReport?.Invoke(row.Id, failure);
        }
    }

    /// <summary>The session's UUID.</summary>
    public string Id { get; }

    /// <summary>Its display name; several sessions may share one.</summary>
    public string Name
    {
        get => name;
        internal set => name = value;
    }

    /// <summary>The working directory its agents start in.</summary>
    public string Cwd { get; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Its directory: <c>sessions/&lt;id&gt;</c> under the data directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>Its database.</summary>
    public string DatabasePath => Path.Join(DirectoryPath, "agent.sqlite");

    /// <summary>Main's model.</summary>
    public ModelRef? Model => model;

    /// <summary>Why the session is unavailable; null while it works.</summary>
    public Exception? Error => error;

    /// <summary>Whether the session is open and has not failed.</summary>
    public bool IsAvailable => error is null && Volatile.Read(ref harness) is not null;

    /// <summary>The compaction settings every agent of the session uses: the stored ones, or the defaults for Main's model window.</summary>
    public SessionCompaction Compaction => stored ?? SessionCompaction.DefaultFor(host.ContextWindow(model));

    /// <summary>Whether <see cref="Compaction"/> was set for this session rather than following the defaults.</summary>
    public bool HasCustomCompaction => stored is not null;

    /// <summary>The session's Harness.</summary>
    /// <exception cref="SessionUnavailableException">The session failed to open, or failed since.</exception>
    public IHarness GetHarness()
    {
        if (error is { } failure) throw new SessionUnavailableException($"Session {Name} is unavailable: {failure.Message}", failure);
        return Volatile.Read(ref harness) ?? throw new SessionUnavailableException($"Session {Name} is not open.", null);
    }

    /// <summary>The session's summary: its agents, or its error when unavailable.</summary>
    public async Task<HostedSessionSummary> SummaryAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HostedAgent> agents = [];
        var failure = error?.Message;
        if (failure is null)
        {
            try { agents = await AgentsAsync(cancellationToken).ConfigureAwait(false); }
            catch (SessionUnavailableException unavailable) { failure = unavailable.Message; }
        }
        return new HostedSessionSummary(Id, Name, Cwd, Model, CreatedAt, DatabasePath, agents.Any(agent => agent.Busy), failure, agents, Compaction);
    }

    /// <summary>Every agent, by conversation number.</summary>
    public async Task<IReadOnlyList<HostedAgent>> AgentsAsync(CancellationToken cancellationToken = default)
    {
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var records = await ConversationsAsync(opened, context).ConfigureAwait(false);
        using var graph = await opened.TaskGraphAsync(context).ConfigureAwait(false);
        var nodes = graph.Value.Tasks.Values.ToList();
        var names = host.Catalog.AgentNames(Id);
        return [.. records.Select(record => Summarize(record, names, nodes))];
    }

    /// <summary>The agent with this conversation number, <c>main</c>, or this case-insensitive name (custom or derived).</summary>
    /// <exception cref="HostNotFoundException">No such agent.</exception>
    /// <exception cref="HostAmbiguousException">Several agents have the name.</exception>
    public async Task<HostedAgent> ResolveAgentAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        var trimmed = address.Trim();
        var agents = await AgentsAsync(cancellationToken).ConfigureAwait(false);
        if (trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit))
            return long.TryParse(trimmed, out var number) && agents.FirstOrDefault(agent => agent.Id == number) is { } byNumber
                ? byNumber
                : throw new HostNotFoundException($"No agent {trimmed} in {Name}.");
        var matches = agents.Where(agent => agent.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0 && trimmed.Equals("main", StringComparison.OrdinalIgnoreCase))
            matches = [.. agents.Where(agent => agent.Id == ConversationId.Root.Value)];
        return matches.Count switch
        {
            0 => throw new HostNotFoundException($"No agent {trimmed} in {Name}."),
            1 => matches[0],
            _ => throw new HostAmbiguousException($"{matches.Count} agents in {Name} are called {trimmed}. Use the agent's number."),
        };
    }

    /// <summary>A new root agent that starts with <paramref name="from"/>'s model, thinking level, instructions and working directory
    /// (default Main's), and an empty conversation. With <paramref name="creationKey"/>, a retry returns the agent the first call
    /// created.</summary>
    /// <exception cref="HostInvalidException">The creation key was used for a different request.</exception>
    public async Task<HostedAgent> CreateAgentAsync(long? from = null, string? agentName = null, string? creationKey = null,
        CancellationToken cancellationToken = default)
    {
        var custom = HostNames.Agent(agentName, 0);
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var request = $"agent:{from ?? ConversationId.Root.Value}:{custom}";
        return await CreateOnceAsync(opened, creationKey, request, async init =>
        {
            var source = await ConversationAsync(opened, from ?? ConversationId.Root.Value, context).ConfigureAwait(false);
            var agent = await source.AgentAsync(context).ConfigureAwait(false);
            var created = await opened.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
            {
                Agent = new AgentChange
                {
                    Model = agent.Model is { } chosen ? chosen : (Patch<ModelRef>?)null,
                    ThinkingLevel = agent.ThinkingLevel,
                    Instructions = agent.Instructions is { } instructions ? instructions : (Patch<string>?)null,
                    Cwd = agent.Cwd is { } cwd ? cwd : (Patch<string>?)null,
                },
                Init = init,
            }, context).ConfigureAwait(false);
            if (custom is not null) SetAgentName(created.Id.Value, custom);
            return created.Id.Value;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forks an agent at one of its visible entries. The fork keeps the conversation up to that entry, and the agent as it was
    /// then; both carry on separately. With <paramref name="creationKey"/>, a retry returns the fork the first call made.</summary>
    /// <exception cref="HostInvalidException">The entry is not visible to the agent, or the creation key was used for a different
    /// request.</exception>
    public async Task<HostedAgent> ForkAsync(long agentId, long entryId, string? forkName = null, string? creationKey = null,
        CancellationToken cancellationToken = default)
    {
        var custom = HostNames.Agent(forkName, 0);
        var opened = GetHarness();
        var context = Context(cancellationToken);
        return await CreateOnceAsync(opened, creationKey, $"fork:{agentId}:{entryId}:{custom}", async init =>
        {
            var source = await ConversationAsync(opened, agentId, context).ConfigureAwait(false);
            IConversation fork;
            try
            {
                fork = await source.ForkAsync(new EntryId(entryId), new ConversationCreateOptions(ConversationOwnership.Ownerless) { Init = init },
                    context).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException)
            {
                throw new HostInvalidException($"Cannot fork agent {agentId} at entry {entryId}: {failure.Message}");
            }
            await RerouteAsync(fork, context).ConfigureAwait(false);
            if (custom is not null) SetAgentName(fork.Id.Value, custom);
            return fork.Id.Value;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a creation once per key: the key's record is written in the creating commit, so a retry after a lost response, or after
    /// a crash, finds the agent the first call created.</summary>
    private async Task<HostedAgent> CreateOnceAsync(IHarness opened, string? creationKey, string request, Func<ConversationInit?, Task<long>> create,
        CancellationToken cancellationToken)
    {
        if (creationKey is null) return await AgentAsync(await create(null).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        if (creationKey.Length is 0 or > 200) throw new HostInvalidException("A creation key must have 1 to 200 characters.");
        await creations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await opened.SnapshotAsync(CreationKeys, creationKey, Context(cancellationToken)).ConfigureAwait(false);
            if (record is not null && record.TryGetValue("conversation", out var value) && value is JsonNumberValue earlier)
            {
                if (!(record.TryGetValue("request", out var used) && used is JsonStringValue text && text.Value == request))
                    throw new HostInvalidException("This creation key was already used for a different request.");
                return await AgentAsync((long)earlier.Value, cancellationToken).ConfigureAwait(false);
            }
            var id = await create(async (tx, conversationId) =>
            {
                var doc = await tx.DocAsync(CreationKeys, creationKey, JsonObjectValue.Empty).ConfigureAwait(false);
                doc.Set("conversation", conversationId.Value);
                doc.Set("request", request);
            }).ConfigureAwait(false);
            return await AgentAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            creations.Release();
        }
    }

    /// <summary>Gives an agent a custom name; blank restores its derived name.</summary>
    public async Task<HostedAgent> RenameAgentAsync(long agentId, string? agentName, CancellationToken cancellationToken = default)
    {
        await ConversationAsync(GetHarness(), agentId, Context(cancellationToken)).ConfigureAwait(false);
        SetAgentName(agentId, HostNames.Agent(agentName, agentId));
        return await AgentAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a message. The device admits each request ID once per agent, so a retry returns the first send's submission. Without a wait,
    /// or with a submission that already settled, it returns at once; with <see cref="SendRequest.Wait"/> it waits up to that long for the
    /// answer and returns <see cref="SendStatus.Pending"/> when the answer is not ready. A cancelled wait never cancels the work.
    /// </summary>
    /// <exception cref="HostBusyException">The agent is busy and the request said <see cref="WhenBusy.Reject"/>.</exception>
    public async Task<SendResult> SendAsync(long agentId, SendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is null || request.Content.Length > MaxMessageLength)
            throw new HostInvalidException($"A message must have at most {MaxMessageLength} characters.");
        if (string.IsNullOrEmpty(request.RequestId) || request.RequestId.Length > 128)
            throw new HostInvalidException("A request ID must have 1 to 128 characters.");
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(opened, agentId, context).ConfigureAwait(false);
        ISubmission submission;
        try
        {
            submission = await conversation.SubmitAsync(SubmissionDraft.Input(request.Content, request.WhenBusy, request.RequestId), context).ConfigureAwait(false);
        }
        catch (ConversationBusyException busy)
        {
            throw new HostBusyException($"Agent {agentId} is busy. Send it as a follow-up or a steer to queue it.", busy);
        }
        var record = await submission.StatusAsync(context).ConfigureAwait(false);
        if (record.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered || request.Wait is not { } wait || wait <= TimeSpan.Zero)
            return await SubmissionAnswers.ResultAsync(opened, record, SendStatus.Queued, context).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            record = await submission.WaitAsync(DurableContext.Background.WithCancellation(timeout.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SendResult(record.Id, SendStatus.Pending);
        }
        return await SubmissionAnswers.ResultAsync(opened, record, SendStatus.Pending, context).ConfigureAwait(false);
    }

    /// <summary>Where a submission of this session stands; with <paramref name="wait"/>, waits up to that long for it to settle (durable-pi
    /// <c>GET /v1/sessions/:s/submissions/:id?waitMs=</c>).</summary>
    /// <exception cref="HostNotFoundException">No such submission.</exception>
    public async Task<SendResult> SubmissionAsync(long submissionId, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var submission = await opened.SubmissionAsync(new SubmissionId(submissionId), context).ConfigureAwait(false)
            ?? throw new HostNotFoundException($"No submission {submissionId} in {Name}.");
        var record = await submission.StatusAsync(context).ConfigureAwait(false);
        if (record.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered || wait is not { } limit || limit <= TimeSpan.Zero)
            return await SubmissionAnswers.ResultAsync(opened, record, record.Status == SubmissionStatus.Queued ? SendStatus.Queued : SendStatus.Pending,
                context).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            record = await submission.WaitAsync(DurableContext.Background.WithCancellation(timeout.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SendResult(record.Id, SendStatus.Pending);
        }
        return await SubmissionAnswers.ResultAsync(opened, record, SendStatus.Pending, context).ConfigureAwait(false);
    }

    /// <summary>Withdraws a queued message of the agent.</summary>
    public async Task<SubmissionAbortResult> WithdrawAsync(long agentId, long submissionId, CancellationToken cancellationToken = default) =>
        await GetHarness().AbortSubmissionAsync(new SubmissionId(submissionId), Context(cancellationToken), new ConversationId(agentId)).ConfigureAwait(false);

    /// <summary>Interrupts an agent: queued messages are withdrawn and its foreground work stops; with <paramref name="background"/>, its
    /// background work too. Resolves once it is idle.</summary>
    public async Task AbortAsync(long agentId, bool background = false, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        await conversation.AbortAsync(context, new ConversationAbortOptions { Background = background }).ConfigureAwait(false);
    }

    /// <summary>Starts a manual compaction of the agent; returns its task.</summary>
    public async Task<TaskId> CompactAsync(long agentId, string? instructions = null, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        return await conversation.CompactAsync(string.IsNullOrWhiteSpace(instructions) ? null : instructions, context).ConfigureAwait(false);
    }

    /// <summary>Starts a new context for the agent, from a handoff note when given. Older entries stay stored.</summary>
    public async Task ResetAsync(long agentId, string? note = null, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        await conversation.ResetAsync(string.IsNullOrWhiteSpace(note) ? null : note, context).ConfigureAwait(false);
    }

    /// <summary>How the agent runs now.</summary>
    public async Task<AgentConfiguration> ConfigurationAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var agent = await (await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false)).AgentAsync(context).ConfigureAwait(false);
        return new AgentConfiguration(agent.Model, agent.ThinkingLevel, agent.Instructions, agent.Cwd,
            !agent.Tools.Any(tool => host.Options.ToolEffects.IsWriter(tool.Name)));
    }

    /// <summary>
    /// Changes how the agent runs, in one commit, from its next request. Read-only removes every tool the device's
    /// <see cref="ToolEffects"/> counts as a writer; turning it off clears the agent's tool filter. A new model for Main is recorded for the
    /// session, and compaction settings that no longer fit its window go back to the defaults.
    /// </summary>
    public async Task<AgentConfiguration> ConfigureAsync(long agentId, AgentConfigurationChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        var update = new AgentChange();
        var touched = false;
        if (change.Model is { } newModel)
        {
            host.RequireModel(newModel);
            update = update with { Model = newModel };
            touched = true;
        }
        if (change.ThinkingLevel is { } level)
        {
            update = update with { ThinkingLevel = HostNames.Thinking(level) };
            touched = true;
        }
        if (change.Instructions is { } instructions)
        {
            update = update with
            {
                Instructions = instructions.Length == 0 ? Patch<string>.Clear
                    : instructions.Length > MaxInstructionsLength ? instructions[..MaxInstructionsLength] : instructions,
            };
            touched = true;
        }
        if (change.Cwd is { } cwd)
        {
            update = update with { Cwd = host.ResolveDirectory(cwd) };
            touched = true;
        }
        if (change.ReadOnly is { } readOnly)
        {
            var agent = await conversation.AgentAsync(context).ConfigureAwait(false);
            update = update with
            {
                Tools = readOnly
                    ? ToolSelection.Without(AvailableTools(agent).Where(tool => host.Options.ToolEffects.IsWriter(tool.Name)))
                    : Patch<ToolSelection>.Clear,
            };
            touched = true;
        }
        if (touched) await conversation.ConfigureAsync(update, context).ConfigureAwait(false);
        if (agentId == ConversationId.Root.Value && change.Model is { } mainModel) SetMainModel(mainModel);
        return await ConfigurationAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every tool the agent's extensions provide to the model, with whether its next request offers it.</summary>
    public async Task<IReadOnlyList<HostedTool>> ToolsAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var agent = await (await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false)).AgentAsync(context).ConfigureAwait(false);
        var offered = agent.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var deferred = agent.Deferred.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        return [.. AvailableTools(agent).Select(tool => new HostedTool(tool.Name, tool.Description, tool.Replay ?? ToolReplay.Unsafe,
            host.Options.ToolEffects.Of(tool.Name), offered.Contains(tool.Name), tool.Deferred || deferred.Contains(tool.Name), tool.Parameters))];
    }

    /// <summary>Changes the session's compaction settings; they apply to every agent from its next request. Null fields keep their
    /// value.</summary>
    /// <exception cref="HostInvalidException">A value out of range.</exception>
    public SessionCompaction UpdateCompaction(SessionCompactionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = Compaction;
        var next = new SessionCompaction(change.Enabled ?? current.Enabled, change.ReserveTokens ?? current.ReserveTokens,
            change.BackgroundTokens ?? current.BackgroundTokens, change.KeepRecentTokens ?? current.KeepRecentTokens).Validate();
        host.Catalog.SetCompaction(Id, next.ToJson());
        stored = next;
        host.Notify(new DeviceChange(DeviceChangeKind.Catalog, Id));
        return next;
    }

    /// <summary>Attaches to an agent's events: a snapshot, then one batch per commit. Dispose to stop; the agent keeps working.</summary>
    public async Task<AgentEventFeed> WatchAgentAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var opened = GetHarness();
        await ConversationAsync(opened, agentId, Context(cancellationToken)).ConfigureAwait(false);
        return await AgentEventFeed.StartAsync(opened, new ConversationId(agentId), host.Options.FeedCapacity).ConfigureAwait(false);
    }

    /// <summary>Attaches to the session's live task graph. Dispose to stop.</summary>
    public Task<TaskGraphFeed> WatchTaskGraphAsync() => TaskGraphFeed.StartAsync(GetHarness());

    /// <summary>The live tasks of the session (for a task panel).</summary>
    public async Task<TaskGraph> TaskGraphAsync(CancellationToken cancellationToken = default)
    {
        using var graph = await GetHarness().TaskGraphAsync(Context(cancellationToken)).ConfigureAwait(false);
        return graph.Value;
    }

    /// <summary>Aborts one task, such as a job run; its owned work stops first.</summary>
    public async Task<TaskAbortResult> AbortTaskAsync(long taskId, CancellationToken cancellationToken = default) =>
        await GetHarness().AbortTaskAsync(new TaskId(taskId), Context(cancellationToken)).ConfigureAwait(false);

    // ─── Extensions ─────────────────────────────────────────────────────────

    /// <summary>The agent's optional extensions (on or off, with the prompt checklist) and the built-in ones it always has.</summary>
    public async Task<AgentExtensions> ExtensionsAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(opened, agentId, context).ConfigureAwait(false);
        var agent = await conversation.AgentAsync(context).ConfigureAwait(false);
        var selected = agent.Extensions.Select(extension => extension.Name).ToHashSet(StringComparer.Ordinal);
        var checklist = await opened.SnapshotAsync(SystemPrompts.ChecklistDoc, conversation.Id, context).ConfigureAwait(false);
        var checkedPrompts = SystemPrompts.Enabled(checklist, host.Options.DefaultPrompts);
        var installed = Installed();
        var optional = new List<HostedExtension>();
        if (installed.GetExtension(SystemPrompts.ExtensionName) is not null)
            optional.Add(new HostedExtension(SystemPrompts.ExtensionName, "System prompts",
                "Adds the checked prompt files from the device's prompts folder to this agent's system prompt.", selected.Contains(SystemPrompts.ExtensionName),
                [.. SystemPrompts.Load(host.PromptsDirectory).Select(prompt => new AgentPrompt(prompt.Id, prompt.Description, prompt.Text, checkedPrompts.Contains(prompt.Id)))]));
        if (installed.GetExtension(CommandGuard.ExtensionName) is not null)
            optional.Add(new HostedExtension(CommandGuard.ExtensionName, "Command guard",
                "Checks every bash and powershell command before it runs and blocks destructive or irreversible ones. Each verdict is a durable memo, so a restart never asks twice.",
                selected.Contains(CommandGuard.ExtensionName), []));
        return new AgentExtensions(optional, [.. agent.Extensions.Select(extension => extension.Name).Where(extensionName => !DeviceApps.Optional.Contains(extensionName))]);
    }

    /// <summary>Turns an optional extension on or off for the agent, from its next request.</summary>
    /// <exception cref="HostInvalidException">Not an optional extension of this device.</exception>
    public async Task<AgentExtensions> SetExtensionAsync(long agentId, string extensionName, bool enabled, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        var installed = Installed();
        if (!DeviceApps.Optional.Contains(extensionName) || installed.GetExtension(extensionName) is null)
            throw new HostInvalidException($"{extensionName} is not an optional extension of this device.");
        var current = (await conversation.AgentAsync(context).ConfigureAwait(false)).Extensions.Select(extension => extension.Name).ToHashSet(StringComparer.Ordinal);
        if (enabled) current.Add(extensionName); else current.Remove(extensionName);
        await conversation.ConfigureAsync(new AgentChange
        {
            Extensions = ExtensionSelection.Only(installed.Installed().Where(extension => current.Contains(extension.Name))),
        }, context).ConfigureAwait(false);
        return await ExtensionsAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks or unchecks one prompt file of the agent's <c>system-prompts</c> checklist.</summary>
    public async Task<AgentExtensions> SetPromptAsync(long agentId, string promptId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (SystemPrompts.Load(host.PromptsDirectory).All(prompt => prompt.Id != promptId)) throw new HostNotFoundException($"No prompt {promptId}.");
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        await conversation.CommitAsync(async tx =>
        {
            var doc = await tx.DocAsync(SystemPrompts.ChecklistDoc, conversation.Id).ConfigureAwait(false);
            var next = SystemPrompts.Enabled(doc.ToValue() as JsonObjectValue, host.Options.DefaultPrompts).Where(id => id != promptId).ToList();
            if (enabled) next.Add(promptId);
            doc.Set("enabled", JsonValue.Array(next.Select(id => (JsonValue)id)));
        }, context).ConfigureAwait(false);
        return await ExtensionsAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    // ─── Jobs ───────────────────────────────────────────────────────────────

    /// <summary>Starts a job for the agent, owned by its conversation in the background, with an anchor entry in its transcript where the
    /// run's card shows.</summary>
    public async Task<TaskId> RunJobAsync(long agentId, string jobId, JsonObjectValue? input = null, CancellationToken cancellationToken = default)
    {
        var job = host.Options.Jobs.FirstOrDefault(definition => definition.Id == jobId) ?? throw new HostNotFoundException($"No job {jobId} on {host.Info.Name}.");
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        return await conversation.CommitAsync(async tx =>
        {
            var id = await Jobs.StartAsync(tx, conversation.Id, job, jobTasks[job.Id], input, TaskOwnership.Conversation, background: true, "you", null,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            await tx.AppendEntryAsync(conversation.Id, new EntryDraft
            {
                Kind = "pisharp.job",
                Data = JsonValue.Object([new("taskId", id.Value), new("job", job.Id), new("text", $"Started {job.Title}")]),
            }).ConfigureAwait(false);
            return id;
        }, context).ConfigureAwait(false);
    }

    /// <summary>The agent's job runs, newest first, as their run records.</summary>
    public async Task<JsonArrayValue> JobRunsAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(opened, agentId, context).ConfigureAwait(false);
        return JsonValue.Array(Jobs.Items(await opened.SnapshotAsync(Jobs.RunsDoc, conversation.Id, context).ConfigureAwait(false), "items"));
    }

    /// <summary>Approves or declines a job run's pending approval. The job resumes from its saved checkpoint.</summary>
    /// <exception cref="AppStateConflictException">The run does not wait for approval, or was decided the other way.</exception>
    public async Task DecideJobAsync(long agentId, long jobTaskId, bool approved, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        await conversation.CommitAsync(tx => Jobs.DecideAsync(tx, conversation.Id, new TaskId(jobTaskId), approved, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            context).ConfigureAwait(false);
    }

    // ─── App State and questions ────────────────────────────────────────────

    /// <summary>Changes the device's App State on the agent's behalf and notes the change in its transcript. With
    /// <paramref name="expectedRevision"/>, a stale change is refused.</summary>
    public async Task<AppStateValue> ChangeAppStateAsync(long agentId, AppStateChange change, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        var result = host.AppState.Change(change, expectedRevision);
        await conversation.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = AppTools.StateChangeEntry, Data = DeviceAppState.Describe(change, result.Revision) }),
            context).ConfigureAwait(false);
        return result;
    }

    /// <summary>The agent's tool calls that wait for a person: question forms and plan reviews.</summary>
    public async Task<IReadOnlyList<PendingInteraction>> PendingInteractionsAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        return await UserInteraction.PendingAsync(await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false), context).ConfigureAwait(false);
    }

    /// <summary>Answers, or declines with a null response, the questions of a waiting <c>ask_user_question</c> call.</summary>
    /// <exception cref="AppStateConflictException">The call no longer waits.</exception>
    public async Task AnswerQuestionsAsync(long agentId, long toolTaskId, QuestionResponse? response, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        try
        {
            if (response is null) await UserQuestions.DeclineAsync(conversation, new TaskId(toolTaskId), context).ConfigureAwait(false);
            else await UserQuestions.AnswerAsync(conversation, new TaskId(toolTaskId), response, context).ConfigureAwait(false);
        }
        catch (InteractionNotPendingException stale)
        {
            throw new AppStateConflictException(stale.Message);
        }
        catch (ArgumentException invalid)
        {
            throw new HostInvalidException(invalid.Message);
        }
    }

    /// <summary>Whether the agent is in plan mode.</summary>
    public async Task<bool> PlanModeAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var opened = GetHarness();
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(opened, agentId, context).ConfigureAwait(false);
        return (await conversation.AgentAsync(context).ConfigureAwait(false)).Extensions.Any(extension => extension.Name == PlanMode.ExtensionName);
    }

    /// <summary>Enters or leaves plan mode. In plan mode the agent may only read, write its plan under the session's <c>plans</c> folder and
    /// ask; <c>exit_plan_mode</c> then submits the plan for review (<see cref="ReviewPlanAsync"/>). Leaving cancels a pending review.</summary>
    public async Task<bool> SetPlanModeAsync(long agentId, bool enabled, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        var installed = registry ?? throw new SessionUnavailableException($"Session {Name} is not open.", null);
        if (enabled)
        {
            var directory = Path.Join(DirectoryPath, "plans", agentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            try { await PlanMode.EnterAsync(conversation, installed, directory, context).ConfigureAwait(false); }
            catch (InvalidOperationException missing) { throw new HostInvalidException(missing.Message); }
        }
        else await PlanMode.ExitAsync(conversation, installed, context).ConfigureAwait(false);
        return await PlanModeAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approves or rejects (with feedback) the plan a waiting <c>exit_plan_mode</c> call submitted.</summary>
    /// <exception cref="AppStateConflictException">The call no longer waits.</exception>
    public async Task ReviewPlanAsync(long agentId, long toolTaskId, bool approved, string? feedback = null, CancellationToken cancellationToken = default)
    {
        var context = Context(cancellationToken);
        var conversation = await ConversationAsync(GetHarness(), agentId, context).ConfigureAwait(false);
        var installed = registry ?? throw new SessionUnavailableException($"Session {Name} is not open.", null);
        try
        {
            if (approved) await PlanMode.ApproveAsync(conversation, installed, new TaskId(toolTaskId), stop: false, context).ConfigureAwait(false);
            else await PlanMode.RejectAsync(conversation, new TaskId(toolTaskId), feedback, context).ConfigureAwait(false);
        }
        catch (InteractionNotPendingException stale)
        {
            throw new AppStateConflictException(stale.Message);
        }
        catch (ArgumentException invalid)
        {
            throw new HostInvalidException(invalid.Message);
        }
    }

    internal async Task<IConversation> ConversationForToolAsync(ConversationId id, DurableContext context) =>
        await GetHarness().ConversationAsync(id, context).ConfigureAwait(false) ?? throw new HostNotFoundException($"No agent {id.Value} in {Name}.");

    private IRegistrySnapshot Installed() => (registry ?? throw new SessionUnavailableException($"Session {Name} is not open.", null)).Snapshot();

    internal async Task OpenAsync(ModelRef? createModel, string? createThinking)
    {
        var context = DurableContext.Background;
        FileLock? acquired = null;
        IStorage? storage = null;
        IHarness? opened = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            acquired = FileLock.TryAcquire(Path.Join(DirectoryPath, "session.lock"), $"pid {Environment.ProcessId}")
                ?? throw new IOException($"Session {Id} is open in another process.");
            storage = host.Options.OpenStorage is { } open
                ? await open(DatabasePath).ConfigureAwait(false)
                : await SqliteStorage.OpenAsync(DatabasePath).ConfigureAwait(false);
            var (apps, tasks) = DeviceApps.Create(host, this);
            var sessionContext = new HostedSessionContext(Id, Cwd, DirectoryPath) { Apps = apps };
            var created = host.Options.CreateRegistry?.Invoke(sessionContext) ?? DurableDeviceHost.DefaultRegistry(sessionContext);
            registry = created;
            jobTasks = tasks;
            opened = await Harness.OpenAsync(storage, new HarnessOptions
            {
                Models = host.Options.Models,
                Registry = created,
                Settings = new SessionHarnessSettings(host.Options.Settings, () => Compaction,
                    () => [.. created.Snapshot().Installed().Where(extension => !DeviceApps.Optional.Contains(extension.Name))]),
                Env = envs.EnvAsync,
                TimeProvider = host.Options.TimeProvider,
                OnReport = report => host.Options.OnReport?.Invoke(Id, report),
            }, context).ConfigureAwait(false);
            await opened.RootAsync(context, createModel is null ? null : new AgentChange
            {
                Cwd = Cwd,
                Model = createModel,
                ThinkingLevel = createThinking ?? "off",
            }).ConfigureAwait(false);
            await RerouteAllAsync(opened, context).ConfigureAwait(false);
            commits = opened.SubscribeCommits((_, _) => host.Notify(new DeviceChange(DeviceChangeKind.Session, Id)));
            fileLock = acquired;
            acquired = null;
            error = null;
            Volatile.Write(ref harness, opened);
            _ = WatchClosedAsync(opened);
            opened.Resume();
        }
        catch (Exception failure)
        {
            if (opened is not null) await CloseQuietlyAsync(opened.CloseAsync(context)).ConfigureAwait(false);
            else if (storage is not null) await CloseQuietlyAsync(storage.CloseAsync(context)).ConfigureAwait(false);
            acquired?.Dispose();
            error = failure;
            host.Options.OnReport?.Invoke(Id, failure);
        }
    }

    /// <summary>Stops all of the session's work, background too, waiting at most 10 s, then closes it (archive).</summary>
    internal async Task StopAndCloseAsync()
    {
        if (error is null && Volatile.Read(ref harness) is { } opened)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var context = DurableContext.Background.WithCancellation(limit.Token);
            try
            {
                foreach (var record in await ConversationsAsync(opened, context).ConfigureAwait(false))
                    if (record.Owner is null && await opened.ConversationAsync(record.Id, context).ConfigureAwait(false) is { } conversation)
                        await conversation.AbortAsync(context, new ConversationAbortOptions { Background = true }).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                host.Options.OnReport?.Invoke(Id, failure);
            }
        }
        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the Harness and releases the session's lock; unfinished work resumes when the session opens again.</summary>
    internal async Task CloseAsync()
    {
        closing = true;
        Interlocked.Exchange(ref commits, null)?.Dispose();
        if (Interlocked.Exchange(ref harness, null) is { } opened)
        {
            try { await opened.CloseAsync(DurableContext.Background).ConfigureAwait(false); }
            catch (Exception failure) { host.Options.OnReport?.Invoke(Id, failure); }
        }
        try { await envs.CleanupAsync(DurableContext.Background).ConfigureAwait(false); }
        catch (Exception failure) { host.Options.OnReport?.Invoke(Id, failure); }
        Interlocked.Exchange(ref fileLock, null)?.Dispose();
    }

    private async Task<HostedAgent> AgentAsync(long agentId, CancellationToken cancellationToken) =>
        (await AgentsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(agent => agent.Id == agentId)
            ?? throw new HostNotFoundException($"No agent {agentId} in {Name}.");

    private void SetAgentName(long agentId, string? custom)
    {
        host.Catalog.SetAgentName(Id, agentId, custom);
        host.Notify(new DeviceChange(DeviceChangeKind.Catalog, Id));
    }

    private void SetMainModel(ModelRef mainModel)
    {
        if (stored is { } current && !current.Fits(host.ContextWindow(mainModel)))
        {
            host.Catalog.SetCompaction(Id, null);
            stored = null;
        }
        model = mainModel;
        host.Catalog.SetSessionModel(Id, mainModel.Provider, mainModel.ModelId);
        host.Notify(new DeviceChange(DeviceChangeKind.Catalog, Id));
    }

    /// <summary>Re-routes every conversation whose stored model the device does not know (durable-pi does so when a session reopens), and
    /// records Main's model for the session.</summary>
    private async Task RerouteAllAsync(IHarness opened, DurableContext context)
    {
        foreach (var record in await ConversationsAsync(opened, context).ConfigureAwait(false))
            if (await opened.ConversationAsync(record.Id, context).ConfigureAwait(false) is { } conversation)
                await RerouteAsync(conversation, context).ConfigureAwait(false);
        if (await opened.ConversationAsync(ConversationId.Root, context).ConfigureAwait(false) is { } root &&
            (await root.AgentAsync(context).ConfigureAwait(false)).Model is { } mainModel && mainModel != model)
        {
            model = mainModel;
            host.Catalog.SetSessionModel(Id, mainModel.Provider, mainModel.ModelId);
        }
    }

    private async Task RerouteAsync(IConversation conversation, DurableContext context)
    {
        if (host.Info.DefaultModel is not { } fallback) return;
        var agent = await conversation.AgentAsync(context).ConfigureAwait(false);
        if (agent.Model is { } current && host.Options.Models.GetModel(current.Provider, current.ModelId) is null)
            await conversation.ConfigureAsync(new AgentChange { Model = fallback }, context).ConfigureAwait(false);
    }

    private async Task WatchClosedAsync(IHarness opened)
    {
        var end = await opened.Closed.ConfigureAwait(false);
        if (closing || end.Reason != SessionEndReason.Failed) return;
        error = new SessionUnavailableException($"Session {Name} failed: {end.Error?.Message}", end.Error);
        host.Options.OnReport?.Invoke(Id, error);
        host.Notify(new DeviceChange(DeviceChangeKind.Session, Id));
    }

    private async Task<IConversation> ConversationAsync(IHarness opened, long agentId, DurableContext context) =>
        await opened.ConversationAsync(new ConversationId(agentId), context).ConfigureAwait(false)
            ?? throw new HostNotFoundException($"No agent {agentId} in {Name}.");

    private static async Task<IReadOnlyList<ConversationRecord>> ConversationsAsync(IHarness opened, DurableContext context)
    {
        var all = new List<ConversationRecord>();
        JsonObjectValue? cursor = null;
        do
        {
            var from = cursor;
            var page = await opened.CommitAsync(tx => tx.ScanConversationsAsync(new ConversationQuery(), 256, from), context).ConfigureAwait(false);
            all.AddRange(page.Items);
            cursor = page.Next;
        } while (cursor is not null);
        return all;
    }

    private static HostedAgent Summarize(ConversationRecord record, IReadOnlyDictionary<long, string> names, IReadOnlyList<TaskGraphNode> nodes)
    {
        var kind = record.Parent is not null ? AgentKind.Fork : record.Owner is not null ? AgentKind.Subagent : AgentKind.Root;
        var custom = names.TryGetValue(record.Id.Value, out var customName) ? customName : null;
        return new HostedAgent(record.Id.Value, custom ?? HostNames.Derived(record.Id.Value, kind), custom is not null, kind,
            record.Parent?.ConversationId.Value, record.Parent?.At.Value, record.Owner?.ConversationId.Value, record.Owner?.TaskId.Value,
            nodes.Any(node => node.ConversationId == record.Id && !InBackground(node, nodes)),
            nodes.Count(node => node.ConversationId == record.Id && node.Background));
    }

    /// <summary>Whether a task is background work: it is, or a task that owns it is (a job's approval task under a background run).</summary>
    private static bool InBackground(TaskGraphNode node, IReadOnlyList<TaskGraphNode> nodes)
    {
        for (var (current, guard) = (node, 0); current is not null && guard < 64; guard++)
        {
            if (current.Background) return true;
            var owner = current.Owner;
            current = owner is null ? null! : nodes.FirstOrDefault(other => other.Id == owner);
        }
        return false;
    }

    /// <summary>The tools the agent's extensions provide to the model, a later extension's tool replacing an earlier one of the same
    /// name.</summary>
    private static IReadOnlyList<ToolRegistration> AvailableTools(Agent agent)
    {
        var byName = new Dictionary<string, ToolRegistration>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var extension in agent.Extensions)
            foreach (var tool in extension.Tools ?? [])
            {
                if (tool.Callers is { } callers && !callers.Contains(ToolCaller.Model)) continue;
                if (!byName.ContainsKey(tool.Name)) order.Add(tool.Name);
                byName[tool.Name] = tool;
            }
        return [.. order.Select(toolName => byName[toolName])];
    }

    private static DurableContext Context(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? DurableContext.Background.WithCancellation(cancellationToken) : DurableContext.Background;

    private static async Task CloseQuietlyAsync(Task close)
    {
        try { await close.ConfigureAwait(false); }
        catch (Exception) { /* The open already failed; that failure is the one reported. */ }
    }
}
