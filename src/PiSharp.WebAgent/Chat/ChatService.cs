// Decision 0011 (run W2, WU7): what the chat pages and commands do, on the durable sessions of DeviceHostService. A chat is a durable session
// plus its binding (ChatBindings). Create, send, fork and edit-and-resend take the browser's request id, which the engine de-duplicates
// (SpawnKey, SendRequest.RequestId, creation keys), so a retried POST never posts twice. The model, thinking level, service tier and account
// are the binding's: a send aligns its agent to them while it is idle, and changing them is refused while the chat runs (409), so a request
// never changes model or account on its own. A new chat binds the provider's active account (IChatAccounts) and keeps it. Approvals,
// questions and plan reviews answer the waiting tool calls. Markdown export renders the stored transcript with the Client's Transcript. The
// sidebar's recent chats come from the bindings, with the device's running flags.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PiSharp.Durable;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Server;
using PiSharp.Durable.Tools;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Chat;

/// <summary>A new chat: its first message and, optionally, what it runs with (else Settings › Defaults and Tools).</summary>
/// <param name="RequestId">The browser's id for this request (1 to 128 characters); a retry with the same id returns the same chat.</param>
/// <param name="Text">The first message.</param>
public sealed record ChatCreateRequest(string RequestId, string Text)
{
    /// <summary>The provider; with <see cref="Model"/>, else the defaults.</summary>
    public string? Provider { get; init; }

    /// <summary>The model of <see cref="Provider"/>.</summary>
    public string? Model { get; init; }

    /// <summary>The thinking level (clamped to the model), else the default.</summary>
    public string? Thinking { get; init; }

    /// <summary>The service tier (Speed), or null for the provider's default.</summary>
    public string? Tier { get; init; }

    /// <summary>An existing absolute folder for the chat's tools, else Settings › Tools' working directory.</summary>
    public string? Cwd { get; init; }

    /// <summary>The title, else one from the first message.</summary>
    public string? Title { get; init; }
}

/// <summary>A created chat.</summary>
/// <param name="Binding">Its binding.</param>
/// <param name="SubmissionId">The first message's submission.</param>
/// <param name="Status">Where the first message stands (<c>Queued</c>, <c>Done</c>...).</param>
public sealed record ChatCreated(ChatBinding Binding, long SubmissionId, string Status);

/// <summary>A message for a chat.</summary>
/// <param name="RequestId">The browser's id for this request; a retry with the same id is admitted once.</param>
/// <param name="Text">The message.</param>
public sealed record ChatSendRequest(string RequestId, string Text)
{
    /// <summary>The agent (branch) to send to; null is the chat's current branch.</summary>
    public long? AgentId { get; init; }

    /// <summary>While the agent runs: steer the run (true) or queue a follow-up (false, the default).</summary>
    public bool Steer { get; init; }
}

/// <summary>A sent message.</summary>
/// <param name="AgentId">The agent it went to.</param>
/// <param name="SubmissionId">Its submission.</param>
/// <param name="Status">Where it stands.</param>
public sealed record ChatSent(long AgentId, long SubmissionId, string Status);

/// <summary>A branch a fork or edit-and-resend made (the chat now shows it).</summary>
/// <param name="AgentId">The new agent.</param>
/// <param name="Name">Its name, such as <c>Fork #2</c>.</param>
public sealed record ChatBranch(long AgentId, string Name);

/// <summary>A change of what a chat's future requests run with; null fields stay.</summary>
public sealed record ChatModelChange
{
    /// <summary>The provider (with <see cref="Model"/>).</summary>
    public string? Provider { get; init; }

    /// <summary>The model.</summary>
    public string? Model { get; init; }

    /// <summary>The thinking level (clamped to the model).</summary>
    public string? Thinking { get; init; }

    /// <summary>The service tier; the empty string returns to the provider's default.</summary>
    public string? Tier { get; init; }

    /// <summary>Another account of the provider, chosen explicitly.</summary>
    public string? AccountId { get; init; }
}

/// <summary>The chat operations. A singleton; it also lists the sidebar's chats (<see cref="IRecentChats"/>).</summary>
public sealed class ChatService(DeviceHostService device, ChatBindings bindings, ApprovalBroker broker, ISettingsStore settings, IModelCatalog catalog,
    MarkdownRenderer markdown, IServiceProvider services, ILogger<ChatService> logger) : IRecentChats
{
    private readonly ConcurrentDictionary<string, long> compactions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChatBranch> branches = new(StringComparer.Ordinal);

    private IChatAccounts Accounts => services.GetService<IChatAccounts>() ?? NoChatAccounts.Instance;

    /// <summary>The binding of a chat that exists and was not deleted.</summary>
    /// <exception cref="ChatException">404.</exception>
    public ChatBinding Require(string chatId) =>
        bindings.Get(chatId) is { IsArchived: false } binding ? binding : throw ChatException.NotFound($"No chat {chatId}.");

    /// <summary>The binding of a chat, deleted ones included, or null.</summary>
    public ChatBinding? Find(string chatId) => bindings.Get(chatId);

    /// <summary>The open session of a chat.</summary>
    public HostedSession Session(string chatId) =>
        device.Host.Sessions.FirstOrDefault(session => session.Id == chatId) ?? throw ChatException.NotFound($"Chat {chatId} has no session on this device.");

    // ─── Create and send ────────────────────────────────────────────────────

    /// <summary>Creates a chat and sends its first message. A retry with the same request id returns the same chat and sends nothing new.</summary>
    public async Task<ChatCreated> CreateAsync(ChatCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckRequest(request.RequestId, request.Text);
        var binding = bindings.FindByRequest(request.RequestId);
        if (binding is null)
        {
            var (provider, model) = ResolveModel(request.Provider, request.Model);
            var defaults = settings.Get<DefaultsSettings>();
            var thinking = Clamp(model, request.Thinking ?? defaults.Thinking ?? (model.Reasoning ? "medium" : "off"));
            var tier = CheckTier(request.Tier);
            var cwd = request.Cwd is { Length: > 0 } chosen ? CheckDirectory(chosen) : settings.Get<ToolsSettings>().EffectiveWorkingDirectory;
            var title = string.IsNullOrWhiteSpace(request.Title) ? ChatDisplay.TitleFrom(request.Text) : ChatDisplay.Cut(request.Title.Trim(), 100);
            var account = await Accounts.BindAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            var session = await device.Host.CreateSessionAsync(new SessionCreateRequest
            {
                Cwd = cwd,
                Name = title,
                Model = new ModelRef(provider.Id, model.Id),
                ThinkingLevel = thinking,
                SpawnKey = "chat:" + request.RequestId,
            }, cancellationToken).ConfigureAwait(false);
            var now = bindings.Now();
            binding = bindings.Add(new ChatBinding(session.Id, session.Name, provider.Id, model.Id, account?.Id, thinking, tier, 1, now, now, null,
                request.RequestId));
            logger.LogInformation("Chat {Chat} created with {Provider}/{Model}.", session.Id, provider.Id, model.Id);
        }
        if (binding.IsArchived) return new ChatCreated(binding, 0, "Deleted");
        var result = await Session(binding.SessionId).SendAsync(1, new SendRequest(request.Text, request.RequestId), cancellationToken).ConfigureAwait(false);
        _ = device.RefreshAsync(binding.SessionId);
        return new ChatCreated(binding, result.SubmissionId.Value, result.Status.ToString());
    }

    /// <summary>Sends a message to a branch of the chat (its current one by default). While the agent runs it queues as a follow-up, or
    /// steers the run. The branch becomes the chat's current one.</summary>
    public async Task<ChatSent> SendAsync(string chatId, ChatSendRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckRequest(request.RequestId, request.Text);
        var binding = Require(chatId);
        var session = Session(chatId);
        var agentId = request.AgentId ?? binding.AgentId;
        var agent = (await session.AgentsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == agentId)
            ?? throw ChatException.NotFound($"No agent {agentId} in this chat.");
        if (!agent.Busy && agent.Kind != AgentKind.Subagent) await AlignAsync(session, agentId, binding, cancellationToken).ConfigureAwait(false);
        var result = await session.SendAsync(agentId, new SendRequest(request.Text, request.RequestId) { WhenBusy = request.Steer ? WhenBusy.Steer : null },
            cancellationToken).ConfigureAwait(false);
        bindings.Update(chatId, current => current with
        {
            AgentId = agent.Kind == AgentKind.Subagent ? current.AgentId : agentId,
            Updated = bindings.Now(),
        });
        _ = device.RefreshAsync(chatId);
        return new ChatSent(agentId, result.SubmissionId.Value, result.Status.ToString());
    }

    /// <summary>Stops the agent's run (its queued messages are withdrawn); resolves once it is idle.</summary>
    public async Task AbortAsync(string chatId, long? agentId = null, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        await Session(chatId).AbortAsync(agentId ?? binding.AgentId, false, cancellationToken).ConfigureAwait(false);
        _ = device.RefreshAsync(chatId);
    }

    /// <summary>Withdraws a queued message (follow-up or steer) before the agent takes it; true when it was still queued.</summary>
    public async Task<bool> WithdrawAsync(string chatId, long? agentId, long submissionId, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        var result = await Session(chatId).WithdrawAsync(agentId ?? binding.AgentId, submissionId, cancellationToken).ConfigureAwait(false);
        return result == SubmissionAbortResult.Aborted;
    }

    // ─── Chat management ────────────────────────────────────────────────────

    /// <summary>Renames the chat (the sidebar title and the session's name).</summary>
    public async Task<ChatBinding> RenameAsync(string chatId, string title, CancellationToken cancellationToken = default)
    {
        Require(chatId);
        if (string.IsNullOrWhiteSpace(title)) throw ChatException.Invalid("A chat needs a name.");
        var session = await device.Host.RenameSessionAsync(chatId, title.Trim(), cancellationToken).ConfigureAwait(false);
        return bindings.Update(chatId, binding => binding with { Title = session.Name, Updated = bindings.Now() });
    }

    /// <summary>Deletes the chat: its work stops and its session moves to the device's archive (nothing is erased). Deleting a deleted chat
    /// does nothing.</summary>
    public async Task ArchiveAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var binding = bindings.Get(chatId) ?? throw ChatException.NotFound($"No chat {chatId}.");
        if (binding.IsArchived) return;
        try { await device.Host.ArchiveSessionAsync(chatId, cancellationToken).ConfigureAwait(false); }
        catch (HostNotFoundException) { /* Already gone from the device. */ }
        broker.Forget(chatId, notify: true);
        var now = bindings.Now();
        bindings.Update(chatId, current => current with { Archived = now, Updated = now });
        _ = device.RefreshAsync(chatId);
    }

    /// <summary>Forks a branch at one of its entries; the chat then shows the fork, which runs with the chat's model. A retry with the same
    /// request id returns the same fork.</summary>
    public async Task<ChatBranch> ForkAsync(string chatId, long? agentId, long entryId, string requestId, CancellationToken cancellationToken = default)
    {
        CheckRequestId(requestId);
        var binding = Require(chatId);
        if (branches.TryGetValue($"{chatId}/fork:{requestId}", out var earlier)) return earlier;
        var session = Session(chatId);
        var fork = await session.ForkAsync(agentId ?? binding.AgentId, entryId, null, "fork:" + requestId, cancellationToken).ConfigureAwait(false);
        await AlignAsync(session, fork.Id, binding, cancellationToken).ConfigureAwait(false);
        bindings.Update(chatId, current => current with { AgentId = fork.Id, Updated = bindings.Now() });
        return Remember($"{chatId}/fork:{requestId}", new ChatBranch(fork.Id, fork.Name));
    }

    /// <summary>Keeps a branch command's answer for its retries in this process (the host's creation key covers a retry with the same
    /// source agent after a restart).</summary>
    private ChatBranch Remember(string key, ChatBranch branch)
    {
        if (branches.Count > 1000) branches.Clear();
        branches[key] = branch;
        return branch;
    }

    /// <summary>Edit-and-resend: forks the branch just before one of its user messages and sends the new text there; the chat then shows the
    /// new branch, and the old one stays. A retry with the same request id makes and sends nothing new.</summary>
    public async Task<ChatBranch> EditAsync(string chatId, long? agentId, long entryId, string text, string requestId, CancellationToken cancellationToken = default)
    {
        CheckRequest(requestId, text);
        var binding = Require(chatId);
        if (branches.TryGetValue($"{chatId}/edit:{requestId}", out var earlier)) return earlier;
        var session = Session(chatId);
        var source = agentId ?? binding.AgentId;
        var entries = await AgentFrames.ActiveEntriesAsync(session, source, cancellationToken).ConfigureAwait(false);
        var index = -1;
        for (var i = 0; i < entries.Count; i++)
            if (ChatDisplay.Long(entries[i], "id") == entryId) index = i;
        if (index < 0 || ChatDisplay.Str(entries[index], "kind") != "pi.user")
            throw ChatException.Invalid("Only a message you wrote in this branch's current context can be edited.");
        var previous = index > 0 ? ChatDisplay.Long(entries[index - 1], "id") : null;
        var branch = previous is { } at
            ? await session.ForkAsync(source, at, null, "edit:" + requestId, cancellationToken).ConfigureAwait(false)
            : await session.CreateAgentAsync(source, null, "edit:" + requestId, cancellationToken).ConfigureAwait(false);
        await AlignAsync(session, branch.Id, binding, cancellationToken).ConfigureAwait(false);
        await session.SendAsync(branch.Id, new SendRequest(text, requestId), cancellationToken).ConfigureAwait(false);
        bindings.Update(chatId, current => current with { AgentId = branch.Id, Updated = bindings.Now() });
        _ = device.RefreshAsync(chatId);
        return Remember($"{chatId}/edit:{requestId}", new ChatBranch(branch.Id, branch.Name));
    }

    /// <summary>Compacts a branch's context now. Refused while the chat runs (409). A retry with the same request id starts nothing new.</summary>
    public async Task<long> CompactAsync(string chatId, long? agentId, string? instructions, string requestId, CancellationToken cancellationToken = default)
    {
        CheckRequestId(requestId);
        var binding = Require(chatId);
        if (compactions.TryGetValue(chatId + "/" + requestId, out var earlier)) return earlier;
        var session = Session(chatId);
        await RequireIdleAsync(session, "Compacting", cancellationToken).ConfigureAwait(false);
        var task = await session.CompactAsync(agentId ?? binding.AgentId, instructions, cancellationToken).ConfigureAwait(false);
        compactions[chatId + "/" + requestId] = task.Value;
        _ = device.RefreshAsync(chatId);
        return task.Value;
    }

    /// <summary>Changes the model, thinking level, service tier or account of the chat's future requests. Refused while the chat runs (409).
    /// A new provider binds that provider's active account unless the change names one; an account the accounts do not know is refused.</summary>
    public async Task<ChatBinding> ChangeModelAsync(string chatId, ChatModelChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var binding = Require(chatId);
        var session = Session(chatId);
        await RequireIdleAsync(session, "Changing the model", cancellationToken).ConfigureAwait(false);
        if ((change.Provider is null) != (change.Model is null)) throw ChatException.Invalid("Choose both a provider and a model.");
        var (provider, model) = change.Provider is null ? ResolveModel(binding.Provider, binding.Model, allowMissing: true) : ResolveModel(change.Provider, change.Model);
        var thinking = Clamp(model, change.Thinking ?? binding.Thinking);
        var tier = change.Tier is null ? binding.Tier : CheckTier(change.Tier);
        var account = binding.AccountId;
        if (change.AccountId is { Length: > 0 } chosen)
            account = (await Accounts.FindAsync(provider.Id, chosen, cancellationToken).ConfigureAwait(false))?.Id
                ?? throw ChatException.Invalid($"{provider.DisplayName} has no account {chosen}.");
        else if (provider.Id != binding.Provider)
            account = (await Accounts.BindAsync(provider.Id, cancellationToken).ConfigureAwait(false))?.Id;
        var next = bindings.Update(chatId, current => current with
        {
            Provider = provider.Id,
            Model = model.Id,
            Thinking = thinking,
            Tier = tier,
            AccountId = account,
            Updated = bindings.Now(),
        });
        await AlignAsync(session, next.AgentId, next, cancellationToken).ConfigureAwait(false);
        return next;
    }

    // ─── Waiting tool calls ─────────────────────────────────────────────────

    /// <summary>Decides a pending approval; the first decision wins. "Allow in this chat" grants the tool to the chat; "Allow always" also
    /// adds the call (each command part for bash, else the tool) to Settings › Tools' allow-list.</summary>
    public async Task<ApprovalOutcome> DecideApprovalAsync(string chatId, long taskId, ApprovalDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Require(chatId);
        var outcome = await broker.DecideAsync(chatId, taskId, decision, cancellationToken).ConfigureAwait(false);
        if (outcome is { Status: ApprovalOutcomeStatus.Decided, Request: { } request })
        {
            if (decision.Choice == ApprovalChoice.AllowChat)
                await broker.GrantAsync(chatId, [new ToolAllowRule(request.Tool)], cancellationToken).ConfigureAwait(false);
            else if (decision.Choice == ApprovalChoice.AllowAlways)
            {
                var rules = ToolAllowList.RulesFor(request.Tool, request.Arguments);
                await settings.UpdateAsync<ToolsSettings>(tools => tools with { AllowList = [.. tools.AllowList ?? [], .. rules] }, cancellationToken)
                    .ConfigureAwait(false);
                await broker.GrantAsync(chatId, rules, cancellationToken).ConfigureAwait(false);
            }
        }
        return outcome;
    }

    /// <summary>Answers (or, with a null response, declines) a waiting <c>ask_user_question</c> call.</summary>
    public async Task AnswerQuestionsAsync(string chatId, long? agentId, long taskId, QuestionResponse? response, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        await Session(chatId).AnswerQuestionsAsync(agentId ?? binding.AgentId, taskId, response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Approves or rejects the plan of a waiting <c>exit_plan_mode</c> call.</summary>
    public async Task ReviewPlanAsync(string chatId, long? agentId, long taskId, bool approved, string? feedback, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        await Session(chatId).ReviewPlanAsync(agentId ?? binding.AgentId, taskId, approved, feedback, cancellationToken).ConfigureAwait(false);
    }

    // ─── Reading ────────────────────────────────────────────────────────────

    /// <summary>The chat page: the stored transcript of the agent (default the chat's current branch).</summary>
    public async Task<ChatPage> OpenAsync(string chatId, long? agentId = null, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        var session = Session(chatId);
        var agents = await session.AgentsAsync(cancellationToken).ConfigureAwait(false);
        var agent = agents.FirstOrDefault(item => item.Id == (agentId ?? binding.AgentId))
            ?? (agentId is null ? agents.FirstOrDefault(item => item.Id == 1) : null)
            ?? throw ChatException.NotFound($"No agent {agentId} in this chat.");
        var entries = await AgentFrames.ActiveEntriesAsync(session, agent.Id, cancellationToken).ConfigureAwait(false);
        var account = binding.AccountId is { } id ? await Accounts.FindAsync(binding.Provider, id, cancellationToken).ConfigureAwait(false) : null;
        var model = catalog.FindModel(binding.Provider, binding.Model);
        return new ChatPage
        {
            Binding = binding,
            Agent = agent,
            Agents = agents,
            Items = Transcript.Build(entries),
            Context = RenderContext(binding, agent.Id, account?.Label),
            Cwd = session.Cwd,
            Provider = catalog.FindProvider(binding.Provider),
            Model = model,
            Account = account,
            ThinkingLevels = ChatDisplay.HostThinkingLevels(model?.ThinkingLevels),
            ApprovalMode = settings.Get<ToolsSettings>().ApprovalMode,
            Busy = agents.Any(item => item.Busy),
        };
    }

    /// <summary>The new chat page's defaults.</summary>
    public NewChatPage NewChat()
    {
        var tools = settings.Get<ToolsSettings>();
        CatalogModel model;
        try { model = ResolveModel(null, null).Model; }
        catch (ChatException) { return new NewChatPage(null, null, "off", ["off"], tools.EffectiveWorkingDirectory); }
        var defaults = settings.Get<DefaultsSettings>();
        return new NewChatPage(model.Provider, model.Id, Clamp(model, defaults.Thinking ?? (model.Reasoning ? "medium" : "off")),
            ChatDisplay.HostThinkingLevels(model.ThinkingLevels), tools.EffectiveWorkingDirectory);
    }

    /// <summary>The render context of an agent of the chat: its pending approvals by call id, and the account label.</summary>
    public ChatRenderContext RenderContext(ChatBinding binding, long agentId, string? accountLabel)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var approvals = new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
        foreach (var approval in broker.Pending(binding.SessionId))
            if (approval.Request.AgentId == agentId && approval.Request.ParentCallId is null && approval.Request.CallId.Length > 0)
                approvals[approval.Request.CallId] = approval;
        return new ChatRenderContext { SessionId = binding.SessionId, AgentId = agentId, Markdown = markdown, Approvals = approvals, AccountLabel = accountLabel };
    }

    /// <summary>The account label of a binding, or null.</summary>
    public async Task<string?> AccountLabelAsync(ChatBinding binding, CancellationToken cancellationToken = default) =>
        binding.AccountId is { } id ? (await Accounts.FindAsync(binding.Provider, id, cancellationToken).ConfigureAwait(false))?.Label : null;

    /// <summary>A branch's stored transcript as Markdown, under the chat's title.</summary>
    public async Task<string> ExportMarkdownAsync(string chatId, long? agentId = null, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        var entries = await AgentFrames.ActiveEntriesAsync(Session(chatId), agentId ?? binding.AgentId, cancellationToken).ConfigureAwait(false);
        return $"# {binding.Title}\n\n" + Transcript.ToMarkdown(Transcript.Build(entries));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RecentChat>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RecentChat>>([.. bindings.List().Take(Math.Max(0, limit))
            .Select(binding => new RecentChat(binding.SessionId, binding.Title, binding.Updated, device.IsBusy(binding.SessionId)))]);

    // ─── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Points an idle agent at the binding's model and thinking level (a fork keeps the model it had at its entry; a chat's model
    /// is the binding's).</summary>
    private static async Task AlignAsync(HostedSession session, long agentId, ChatBinding binding, CancellationToken cancellationToken)
    {
        var model = new ModelRef(binding.Provider, binding.Model);
        var configuration = await session.ConfigurationAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (configuration.Model == model && configuration.ThinkingLevel == binding.Thinking) return;
        await session.ConfigureAsync(agentId, new AgentConfigurationChange { Model = model, ThinkingLevel = binding.Thinking }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task RequireIdleAsync(HostedSession session, string what, CancellationToken cancellationToken)
    {
        if ((await session.AgentsAsync(cancellationToken).ConfigureAwait(false)).Any(agent => agent.Busy))
            throw ChatException.Busy($"{what} waits until the chat is idle. Stop the run or wait for it to finish.");
    }

    private (CatalogProvider Provider, CatalogModel Model) ResolveModel(string? providerId, string? modelId, bool allowMissing = false)
    {
        if (providerId is not null || modelId is not null)
        {
            var provider = providerId is null ? null : catalog.FindProvider(providerId);
            var model = provider is null || modelId is null ? null : catalog.FindModel(provider.Id, modelId);
            if (provider is not null && model is not null) return (provider, model);
            if (allowMissing && providerId is not null && modelId is not null)
                return (provider ?? new CatalogProvider(providerId, providerId, null, [], BillingKind.Unknown), new CatalogModel(providerId, modelId, modelId));
            throw ChatException.Invalid($"{providerId}/{modelId} is not one of the models this app offers.");
        }
        var defaults = settings.Get<DefaultsSettings>();
        if (defaults.Provider is { } p && defaults.Model is { } m && catalog.FindProvider(p) is { } chosenProvider && catalog.FindModel(p, m) is { } chosenModel)
            return (chosenProvider, chosenModel);
        var hidden = settings.Get<ModelsSettings>();
        foreach (var provider in catalog.Providers)
        {
            var models = catalog.Models(provider.Id);
            if ((models.FirstOrDefault(model => !hidden.IsHidden(provider.Id, model.Id)) ?? models.FirstOrDefault()) is { } first) return (provider, first);
        }
        throw ChatException.Invalid("No model is available. Connect a provider in Settings › Providers.");
    }

    private static string Clamp(CatalogModel model, string level) =>
        PiSharp.AI.ThinkingLevels.Clamp([.. ChatDisplay.HostThinkingLevels(model.ThinkingLevels)], level);

    private static string? CheckTier(string? tier)
    {
        var trimmed = tier?.Trim() ?? "";
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > 32 || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw ChatException.Invalid($"\"{tier}\" is not a service tier.");
        return trimmed;
    }

    private static string CheckDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw ChatException.Invalid("The working directory must be a full path.");
        if (!Directory.Exists(path)) throw ChatException.Invalid($"The working directory {path} does not exist.");
        return Path.GetFullPath(path);
    }

    private static void CheckRequestId(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId) || requestId.Length > 128) throw ChatException.Invalid("A request id must have 1 to 128 characters.");
    }

    private static void CheckRequest(string? requestId, string? text)
    {
        CheckRequestId(requestId);
        if (string.IsNullOrWhiteSpace(text)) throw ChatException.Invalid("Write a message first.");
        if (text.Length > HostedSession.MaxMessageLength) throw ChatException.Invalid($"A message must have at most {HostedSession.MaxMessageLength} characters.");
    }
}

/// <summary>Reads question answers from a command body.</summary>
public static class ChatAnswers
{
    /// <summary>The response of <c>{answers: [{selected: [...], other?}]}</c> or <c>{decline: true}</c> (null: declined).</summary>
    /// <exception cref="ChatException">Neither is given (400).</exception>
    public static QuestionResponse? Read(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (ChatDisplay.Bool(body, "decline")) return null;
        if (body["answers"] is not JsonArray answers) throw ChatException.Invalid("answers or decline is required.");
        return QuestionResponse.Answered(answers.Select(answer => answer is JsonObject value
            ? new QuestionAnswer([.. (value["selected"] as JsonArray ?? []).Select(item => item?.GetValue<string>() ?? "")], ChatDisplay.Str(value, "other"))
            : throw ChatException.Invalid("Each answer is {selected, other?}.")));
    }
}
