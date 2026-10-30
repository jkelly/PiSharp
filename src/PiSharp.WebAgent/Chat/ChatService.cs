// Decision 0011 (run W2, WU7): what the chat pages and commands do, on the durable sessions of DeviceHostService. A chat is a durable session
// plus its binding (ChatBindings). Create, send, fork and edit-and-resend take the browser's request id, which the engine de-duplicates
// (SpawnKey, SendRequest.RequestId, creation keys), so a retried POST never posts twice. The model, thinking level, service tier and account
// are the binding's: a send aligns its agent to them while it is idle, and changing them is refused while the chat runs (409), so a request
// never changes model or account on its own. A new chat binds the provider's active account (IChatAccounts) and keeps it. Approvals,
// questions and plan reviews answer the waiting tool calls. Markdown export renders the stored transcript with the Client's Transcript. The
// sidebar's recent chats come from the bindings, with the device's running flags. WU8 adds Retry (fork before the message an answer replies
// to and send it again, with the chat's own model and account), the session tree (every branch's entries merged by id), the session
// details panel, the banners and the automatic compaction switch. W3 (S5) adds the Spend Guard (ChatSpend): a create or send that carries
// `spend` is checked before anything is posted (409 with the confirm modal when the guard would ask or block; the user's choice comes back
// on the same request id), a call stopped inside a run continues after the user's choice, the composer's estimate, the answers' cost marks,
// the chat's spend in the details panel and the banner of a stopped call. Billing comes from the Spend area's billing modes when present.
// W3 fix (chat spend): a checked create, send or continue holds its send key (ChatSpend.EnterAsync) from the check to the send, so two
// copies of one POST store one approval, which answers only the message it was given for; a send that fails after it was admitted
// withdraws its approval, and its retry is checked again.
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

    /// <summary>The Spend Guard check (or the user's answer to it) to make before the chat is created; null makes none here.</summary>
    public ChatSpendAnswer? Spend { get; init; }
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

    /// <summary>The Spend Guard check (or the user's answer to it) to make before the message is posted; null makes none here (the guard
    /// still checks the request when it is made, and stops it inside the run).</summary>
    public ChatSpendAnswer? Spend { get; init; }
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
    private readonly ConcurrentDictionary<(string Session, long Agent), AgentDetails> details = new();

    /// <summary>How long an agent's name, tools and instructions are kept for the session details panel before they are read again.</summary>
    public static TimeSpan DetailsLifetime { get; } = TimeSpan.FromSeconds(5);

    private IChatAccounts Accounts => services.GetService<IChatAccounts>() ?? NoChatAccounts.Instance;

    /// <summary>The Spend Guard on the chat pages, or null when the Chat area runs without it.</summary>
    public ChatSpend? Spending => services.GetService<ChatSpend>();

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
        var spend = request.Spend is null ? null : Spending;
        var key = "new/" + request.RequestId;
        // One copy of a checked create at a time: a parallel copy waits, then finds the chat and the admission the first one made.
        using var hold = spend is null ? null : await spend.EnterAsync(key, cancellationToken).ConfigureAwait(false);
        var admitting = false;
        PiSharp.Durable.Hosting.SendResult result;
        ChatBinding? binding;
        try
        {
            binding = bindings.FindByRequest(request.RequestId);
            if (binding is null)
            {
                var (provider, model) = ResolveModel(request.Provider, request.Model);
                var defaults = settings.Get<DefaultsSettings>();
                var thinking = Clamp(model, request.Thinking ?? defaults.Thinking ?? (model.Reasoning ? "medium" : "off"));
                var tier = CheckTier(request.Tier);
                var cwd = request.Cwd is { Length: > 0 } chosen ? CheckDirectory(chosen) : settings.Get<ToolsSettings>().EffectiveWorkingDirectory;
                var title = string.IsNullOrWhiteSpace(request.Title) ? ChatDisplay.TitleFrom(request.Text) : ChatDisplay.Cut(request.Title.Trim(), 100);
                var account = await Accounts.BindAsync(provider.Id, cancellationToken).ConfigureAwait(false);
                admitting = spend is not null && !spend.WasAdmitted(key);
                var approval = spend is null ? null : await spend.AdmitAsync(ChatSpend.NewChatCall(provider.Id, model.Id, account?.Id, request.Text, request.RequestId),
                    key, request.Spend!, null, request.Text, cancellationToken).ConfigureAwait(false);
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
                if (approval is not null) await spend!.ApproveAsync(approval with { Run = session.Id }, key, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Chat {Chat} created with {Provider}/{Model}.", session.Id, provider.Id, model.Id);
            }
            else if (spend is not null && spend.WasWithdrawn(key) && !binding.IsArchived)
            {
                // The first copy made the chat but its message failed: this retry is checked again, as a send to the chat.
                admitting = true;
                var call = await spend.CallAsync(binding, 1, request.Text, cancellationToken).ConfigureAwait(false);
                var approval = await spend.AdmitAsync(call, key, request.Spend!, binding.SessionId, request.Text, cancellationToken).ConfigureAwait(false);
                if (approval is not null) await spend.ApproveAsync(approval, key, cancellationToken).ConfigureAwait(false);
            }
            if (binding.IsArchived) return new ChatCreated(binding, 0, "Deleted");
            result = await Session(binding.SessionId).SendAsync(1, new SendRequest(request.Text, request.RequestId), cancellationToken).ConfigureAwait(false);
        }
        catch when (admitting)
        {
            // Nothing may wait to answer a message that was not posted; a retry is checked again.
            await spend!.WithdrawAsync(key).ConfigureAwait(false);
            throw;
        }
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
        var spend = request.Spend is null ? null : Spending;
        var key = chatId + "/" + request.RequestId;
        // One copy of a checked send at a time, so two copies of one POST store one approval.
        using var hold = spend is null ? null : await spend.EnterAsync(key, cancellationToken).ConfigureAwait(false);
        var admitting = false;
        PiSharp.Durable.Hosting.SendResult result;
        try
        {
            if (spend is not null && !spend.WasAdmitted(key))
            {
                admitting = true;
                var call = await spend.CallAsync(binding, agentId, request.Text, cancellationToken).ConfigureAwait(false);
                var approval = await spend.AdmitAsync(call, key, request.Spend!, chatId, request.Text, cancellationToken).ConfigureAwait(false);
                if (approval is not null) await spend.ApproveAsync(approval, key, cancellationToken).ConfigureAwait(false);
            }
            if (!agent.Busy && agent.Kind != AgentKind.Subagent) await AlignAsync(session, agentId, binding, cancellationToken).ConfigureAwait(false);
            result = await session.SendAsync(agentId, new SendRequest(request.Text, request.RequestId) { WhenBusy = request.Steer ? WhenBusy.Steer : null },
                cancellationToken).ConfigureAwait(false);
        }
        catch when (admitting)
        {
            // Nothing may wait to answer a message that was not posted; a retry is checked again.
            await spend!.WithdrawAsync(key).ConfigureAwait(false);
            throw;
        }
        // A queued message the user withdraws takes its approval with it.
        Spending?.Posted(chatId, result.SubmissionId.Value, key);
        bindings.Update(chatId, current => current with
        {
            AgentId = agent.Kind == AgentKind.Subagent ? current.AgentId : agentId,
            Updated = bindings.Now(),
        });
        _ = device.RefreshAsync(chatId);
        return new ChatSent(agentId, result.SubmissionId.Value, result.Status.ToString());
    }

    /// <summary>Continues a branch after a call the Spend Guard stopped inside its run (<paramref name="op"/>, named in the stopped answer's
    /// error): the stopped request plus a "Continue." message is checked with the user's <paramref name="answer"/> (proceed once, or cap
    /// the output, stored as the chat's one-time approval when it is needed), then the message is sent on the branch. Refused (409, nothing
    /// sent) when the guard still would not let it through. A retry with the same request id sends nothing new.</summary>
    /// <exception cref="ChatSpendException">The guard would still ask or block.</exception>
    public async Task<ChatSent> ContinueAsync(string chatId, long? agentId, string op, string requestId, ChatSpendAnswer answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        CheckRequestId(requestId);
        var binding = Require(chatId);
        var spend = Spending ?? throw ChatException.NotFound("The Spend Guard isn't running, so no request of this chat was stopped.");
        var key = chatId + "/" + requestId;
        using var hold = await spend.EnterAsync(key, cancellationToken).ConfigureAwait(false);
        var admitting = false;
        try
        {
            if (!spend.WasAdmitted(key))
            {
                admitting = true;
                var stop = await spend.StopAsync(chatId, op, cancellationToken).ConfigureAwait(false)
                    ?? throw ChatException.NotFound($"No request of this chat was stopped as {op}.");
                var call = ChatSpend.ContinueCall(stop) with { Provider = binding.Provider, Model = binding.Model, Account = binding.AccountId };
                var approval = await spend.AdmitAsync(call, key, answer, chatId, ChatSpend.ContinueText, cancellationToken).ConfigureAwait(false);
                if (approval is not null) await spend.ApproveAsync(approval, key, cancellationToken).ConfigureAwait(false);
            }
            // Not checked again inside (no spend answer), so the held key is not entered twice.
            return await SendAsync(chatId, new ChatSendRequest(requestId, ChatSpend.ContinueText) { AgentId = agentId }, cancellationToken).ConfigureAwait(false);
        }
        catch when (admitting)
        {
            await spend.WithdrawAsync(key).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The composer's estimate of <paramref name="draft"/> on a branch of the chat; null without the Spend Guard.</summary>
    public async Task<ChatSpendResult?> EstimateAsync(string chatId, long? agentId, string? draft, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        if (Spending is not { } spend) return null;
        return await spend.RenderAsync(await spend.EstimateAsync(binding, agentId, draft, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>The composer's estimate of a new chat's first message with a model (else the defaults); null without the Spend Guard.</summary>
    public async Task<ChatSpendResult?> EstimateNewAsync(string? providerId, string? modelId, string? draft, CancellationToken cancellationToken = default)
    {
        if (Spending is not { } spend) return null;
        var (provider, model) = ResolveModel(providerId, modelId);
        return await spend.RenderAsync(await spend.EstimateNewAsync(provider.Id, model.Id, draft, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>The confirm modal of a call the guard stopped in this chat: when continuing would now go through as it is, an allowed answer
    /// (no modal); else the stop's own decision. Null without the Spend Guard.</summary>
    public async Task<ChatSpendResult?> StopReviewAsync(string chatId, string op, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        if (Spending is not { } spend) return null;
        var stop = await spend.StopAsync(chatId, op, cancellationToken).ConfigureAwait(false)
            ?? throw ChatException.NotFound($"No request of this chat was stopped as {op}.");
        var now = await spend.CheckAsync(ChatSpend.ContinueCall(stop) with { Provider = binding.Provider, Model = binding.Model, Account = binding.AccountId },
            modal: false, cancellationToken).ConfigureAwait(false);
        if (now is { Allowed: true }) return now;
        return await spend.StopResultAsync(stop, cancellationToken).ConfigureAwait(false);
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
        if (result != SubmissionAbortResult.Aborted) return false;
        if (Spending is { } spend) await spend.WithdrawPostedAsync(chatId, submissionId).ConfigureAwait(false);
        return true;
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
        var index = IndexOf(entries, entryId);
        if (index < 0 || ChatDisplay.Str(entries[index], "kind") != "pi.user")
            throw ChatException.Invalid("Only a message you wrote in this branch's current context can be edited.");
        var branch = await ResendAsync(session, binding, source, entries, index, text, requestId, "edit:", cancellationToken).ConfigureAwait(false);
        return Remember($"{chatId}/edit:{requestId}", branch);
    }

    /// <summary>Retry (and Regenerate): forks the branch just before the message of yours that an answer replies to, and sends that message
    /// again there, with the chat's own model and account (never another account, and never a model the chat does not show). The chat then
    /// shows the new branch; the old one, with the failed or earlier answer, stays. A retry with the same request id makes and sends nothing
    /// new.</summary>
    public async Task<ChatBranch> RetryAsync(string chatId, long? agentId, long entryId, string requestId, CancellationToken cancellationToken = default)
    {
        CheckRequestId(requestId);
        var binding = Require(chatId);
        if (branches.TryGetValue($"{chatId}/retry:{requestId}", out var earlier)) return earlier;
        var session = Session(chatId);
        var source = agentId ?? binding.AgentId;
        var entries = await AgentFrames.ActiveEntriesAsync(session, source, cancellationToken).ConfigureAwait(false);
        var index = IndexOf(entries, entryId);
        if (index < 0 || ChatDisplay.Str(entries[index], "kind") != "pi.assistant")
            throw ChatException.Invalid("Only an answer in this branch's current context can be retried.");
        var user = index - 1;
        while (user >= 0 && ChatDisplay.Str(entries[user], "kind") != "pi.user") user--;
        if (user < 0) throw ChatException.Invalid("This answer has no message of yours before it to send again.");
        var text = string.Join("\n", (entries[user]["model"] as JsonArray ?? []).OfType<JsonObject>().Select(Transcript.MessageText));
        if (string.IsNullOrWhiteSpace(text)) throw ChatException.Invalid("The message before this answer has no text to send again.");
        var branch = await ResendAsync(session, binding, source, entries, user, text, requestId, "retry:", cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Chat {Chat}: retried entry {Entry} on {Branch}.", chatId, entryId, branch.Name);
        return Remember($"{chatId}/retry:{requestId}", branch);
    }

    /// <summary>Forks <paramref name="source"/> just before <paramref name="entries"/>[<paramref name="index"/>] (a new agent from the same
    /// start when it is the first entry), points it at the binding's model and sends <paramref name="text"/>; the chat shows the new branch.</summary>
    private async Task<ChatBranch> ResendAsync(HostedSession session, ChatBinding binding, long source, IReadOnlyList<JsonObject> entries, int index, string text,
        string requestId, string kind, CancellationToken cancellationToken)
    {
        var previous = index > 0 ? ChatDisplay.Long(entries[index - 1], "id") : null;
        var branch = previous is { } at
            ? await session.ForkAsync(source, at, null, kind + requestId, cancellationToken).ConfigureAwait(false)
            : await session.CreateAgentAsync(source, null, kind + requestId, cancellationToken).ConfigureAwait(false);
        await AlignAsync(session, branch.Id, binding, cancellationToken).ConfigureAwait(false);
        await session.SendAsync(branch.Id, new SendRequest(text, requestId), cancellationToken).ConfigureAwait(false);
        bindings.Update(binding.SessionId, current => current with { AgentId = branch.Id, Updated = bindings.Now() });
        _ = device.RefreshAsync(binding.SessionId);
        return new ChatBranch(branch.Id, branch.Name);
    }

    private static int IndexOf(IReadOnlyList<JsonObject> entries, long entryId)
    {
        for (var i = 0; i < entries.Count; i++)
            if (ChatDisplay.Long(entries[i], "id") == entryId) return i;
        return -1;
    }

    /// <summary>Turns the session's automatic compaction on or off (from the next request of every branch).</summary>
    public SessionCompaction SetAutoCompaction(string chatId, bool enabled)
    {
        Require(chatId);
        var session = Session(chatId);
        details.Clear();
        return session.UpdateCompaction(new SessionCompactionChange { Enabled = enabled });
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
        var items = Transcript.Build(entries);
        var waiting = broker.Pending(chatId).Any(approval => approval.Request.AgentId == agent.Id);
        var spend = Spending;
        var facts = spend is null ? ChatSpendFacts.Empty : await spend.FactsAsync(binding, entries, agent.Busy, account?.Label, cancellationToken).ConfigureAwait(false);
        return new ChatPage
        {
            Binding = binding,
            Agent = agent,
            Agents = agents,
            Items = items,
            Context = RenderContext(binding, agent.Id, account?.Label, entries, items, new ChatRunState(agent.Busy, false, waiting, false), facts),
            Cwd = session.Cwd,
            Provider = catalog.FindProvider(binding.Provider),
            Model = model,
            Account = account,
            ThinkingLevels = ChatDisplay.HostThinkingLevels(model?.ThinkingLevels),
            ApprovalMode = settings.Get<ToolsSettings>().ApprovalMode,
            Busy = agents.Any(item => item.Busy),
            Info = await InfoAsync(binding, agent.Id, entries, ContextTokens(entries), null, account?.Label, cancellationToken).ConfigureAwait(false),
            Banners = Banners(binding, account?.Label, entries),
            Estimate = spend is null ? null : await spend.EstimateAsync(binding, agent.Id, null, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>The session details of a branch: model, account, context and usage, its tools and instructions, compaction and the
    /// durable session. The agent's name, tools and instructions are read at most every <see cref="DetailsLifetime"/>.</summary>
    public async Task<ChatInfoView> InfoAsync(ChatBinding binding, long agentId, IReadOnlyList<JsonObject> entries, double contextTokens, UsageSummary? usage,
        string? accountLabel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(entries);
        var session = device.Host.Sessions.FirstOrDefault(item => item.Id == binding.SessionId);
        var agent = session is null ? null : await DetailsAsync(session, agentId, cancellationToken).ConfigureAwait(false);
        var newest = entries.LastOrDefault(entry => ChatDisplay.Str(entry, "kind") == "pi.assistant");
        SessionCompaction? compaction = null;
        try { compaction = session?.Compaction; }
        catch (Exception error) when (error is DurableHostException or InvalidOperationException) { /* Unknown. */ }
        return new ChatInfoView
        {
            Binding = binding,
            AgentId = agentId,
            AgentName = agent?.Name ?? $"Agent {agentId}",
            Provider = catalog.FindProvider(binding.Provider),
            Model = catalog.FindModel(binding.Provider, binding.Model),
            AccountLabel = accountLabel,
            ContextTokens = contextTokens,
            Usage = usage,
            Api = ChatEntryMeta.Of(newest)?.Api,
            ApprovalMode = settings.Get<ToolsSettings>().ApprovalMode,
            Tools = agent?.Tools ?? [],
            AskTools = agent?.AskTools ?? [],
            Instructions = agent?.Instructions,
            Compaction = compaction,
            Cwd = session?.Cwd ?? settings.Get<ToolsSettings>().EffectiveWorkingDirectory,
            SessionPath = session?.DatabasePath,
            UserCount = entries.Count(entry => ChatDisplay.Str(entry, "kind") == "pi.user"),
            AssistantCount = entries.Count(entry => ChatDisplay.Str(entry, "kind") == "pi.assistant"),
            ToolCount = entries.Where(entry => ChatDisplay.Str(entry, "kind") == "pi.tool-result")
                .Sum(entry => (entry["model"] as JsonArray ?? []).OfType<JsonObject>().Count(message => ChatDisplay.Str(message, "role") == "toolResult")),
            Spend = Spending is { } spend ? await spend.SummaryAsync(binding, accountLabel, cancellationToken).ConfigureAwait(false) : null,
        };
    }

    /// <summary>The banners of a branch: "No providers configured" when the catalog is empty, and the expired sign-in when the branch's
    /// newest answer failed because the chat's account could not be used.</summary>
    public ChatBannersView Banners(ChatBinding binding, string? accountLabel, IReadOnlyList<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(entries);
        var none = catalog.Providers.Count == 0;
        var newest = entries.Where(entry => ChatDisplay.Str(entry, "kind") is "pi.user" or "pi.assistant").MaxBy(entry => ChatDisplay.Long(entry, "id") ?? 0);
        if (ChatDisplay.Str(newest, "kind") == "pi.assistant" && ChatSpendStopView.From(ChatEntryMeta.Of(newest)?.ErrorMessage, ChatDisplay.Long(newest, "id")) is { } stop)
            return new ChatBannersView(none) { Spend = stop };
        var (kind, meta) = ChatFacts.LastFailure(entries);
        if (kind != ChatFailureKind.Expired) return none ? new ChatBannersView(true) : ChatBannersView.None;
        var provider = meta?.Provider ?? binding.Provider;
        return new ChatBannersView(none, provider, catalog.FindProvider(provider)?.DisplayName ?? provider, accountLabel,
            ChatFailures.Display(meta?.ErrorMessage));
    }

    /// <summary>About the size of a branch's context: the tokens of its newest answer's request (as AgentModel.ContextTokens).</summary>
    public static double ContextTokens(IReadOnlyList<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries.Reverse())
        {
            if (ChatDisplay.Str(entry, "kind") != "pi.assistant") continue;
            if (ChatEntryMeta.Of(entry) is { HasUsage: true } meta) return meta.Input + meta.Output + meta.CacheRead + meta.CacheWrite;
        }
        return 0;
    }

    /// <summary>The session tree: every branch's entries (yours, answers, compactions and resets) merged by entry id, so branches share the
    /// entries before their fork; each branch's newest entry carries the branch, and a running branch says so.</summary>
    public async Task<ChatTreeView> TreeAsync(string chatId, long? agentId, CancellationToken cancellationToken = default)
    {
        var binding = Require(chatId);
        var session = Session(chatId);
        var shown = agentId ?? binding.AgentId;
        var agents = (await session.AgentsAsync(cancellationToken).ConfigureAwait(false)).Where(agent => agent.Kind != AgentKind.Subagent).ToList();
        var nodes = new Dictionary<long, ChatTreeNode>();
        var roots = new List<ChatTreeNode>();
        long? leaf = null;
        foreach (var agent in agents.OrderBy(agent => agent.Id == shown ? 0 : 1).ThenBy(agent => agent.Id))
        {
            IReadOnlyList<JsonObject> entries;
            try { entries = await AgentFrames.ActiveEntriesAsync(session, agent.Id, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (ChatErrors.From(error) is not null) { continue; }
            ChatTreeNode? parent = null;
            ChatTreeNode? last = null;
            long? previous = null;
            foreach (var entry in entries)
            {
                var id = ChatDisplay.Long(entry, "id") ?? 0;
                var role = ChatDisplay.Str(entry, "kind") switch
                {
                    "pi.user" => "user",
                    "pi.assistant" => "assistant",
                    "pi.compaction" => "compaction",
                    "pi.reset" => "reset",
                    _ => null,
                };
                if (role is null)
                {
                    previous = id;
                    continue;
                }
                if (!nodes.TryGetValue(id, out var node))
                {
                    node = new ChatTreeNode { EntryId = id, PreviousId = previous, Role = role, Text = TreeText(entry, role), Tools = ToolCalls(entry), AgentId = agent.Id };
                    nodes[id] = node;
                    if (parent is null) roots.Add(node);
                    else parent.Children.Add(node);
                }
                if (agent.Id == shown) node.OnCurrentPath = true;
                parent = node;
                last = node;
                previous = id;
            }
            if (last is null) continue;
            last.LeafOf.Add(agent.Id);
            if (agent.Busy) last.Running = true;
            if (agent.Id == shown)
            {
                last.Current = true;
                leaf = last.EntryId;
            }
        }
        static void Sort(List<ChatTreeNode> list)
        {
            list.Sort((a, b) => a.EntryId.CompareTo(b.EntryId));
            foreach (var node in list) Sort(node.Children);
        }
        Sort(roots);
        return new ChatTreeView(chatId, shown, roots, agents.Count, agents.Count(agent => agent.Busy), nodes.Count, leaf,
            agents.ToDictionary(agent => agent.Id, agent => agent.Name));
    }

    private static string TreeText(JsonObject entry, string role)
    {
        var messages = (entry["model"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        switch (role)
        {
            case "user":
                return ChatDisplay.Preview(string.Join(" ", messages.Select(Transcript.MessageText)));
            case "assistant":
            {
                var text = ChatDisplay.Preview(string.Join(" ", messages.Where(message => ChatDisplay.Str(message, "role") == "assistant").Select(Transcript.MessageText)));
                if (text.Length > 0) return text;
                var meta = ChatEntryMeta.Of(entry);
                if (meta?.ErrorMessage is { } error) return "Failed: " + ChatDisplay.Preview(ChatFailures.Display(error), 80);
                var calls = ToolCalls(entry);
                return calls > 0 ? $"{calls} tool call{(calls == 1 ? "" : "s")}" : "(no text)";
            }
            case "compaction":
                return "Context compacted";
            default:
                return "New context started";
        }
    }

    private static int ToolCalls(JsonObject entry) =>
        (entry["model"] as JsonArray ?? []).OfType<JsonObject>().Where(message => ChatDisplay.Str(message, "role") == "assistant")
            .Sum(message => (message["content"] as JsonArray ?? []).OfType<JsonObject>().Count(block => ChatDisplay.Str(block, "type") == "toolCall"));

    /// <summary>An agent's name, offered tools and instructions, read at most every <see cref="DetailsLifetime"/>.</summary>
    private async Task<AgentDetails?> DetailsAsync(HostedSession session, long agentId, CancellationToken cancellationToken)
    {
        if (details.TryGetValue((session.Id, agentId), out var known) && DateTimeOffset.UtcNow - known.Read < DetailsLifetime) return known;
        try
        {
            var agents = await session.AgentsAsync(cancellationToken).ConfigureAwait(false);
            var tools = await session.ToolsAsync(agentId, cancellationToken).ConfigureAwait(false);
            var configuration = await session.ConfigurationAsync(agentId, cancellationToken).ConfigureAwait(false);
            var offered = tools.Where(tool => tool.Offered).ToList();
            var read = new AgentDetails(agents.FirstOrDefault(agent => agent.Id == agentId)?.Name ?? $"Agent {agentId}",
                [.. offered.Select(tool => tool.Name)], [.. offered.Where(tool => tool.Effect != ToolEffect.Read).Select(tool => tool.Name)],
                string.IsNullOrWhiteSpace(configuration.Instructions) ? null : configuration.Instructions, DateTimeOffset.UtcNow);
            if (details.Count > 500) details.Clear();
            details[(session.Id, agentId)] = read;
            return read;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Best effort: the panel shows what it knew (or leaves the tools out) rather than failing the page.
            logger.LogDebug(error, "Reading agent {Agent} of chat {Chat} for its details failed.", agentId, session.Id);
            return known;
        }
    }

    private sealed record AgentDetails(string Name, IReadOnlyList<string> Tools, IReadOnlyList<string> AskTools, string? Instructions, DateTimeOffset Read);

    /// <summary>The new chat page's defaults, with the send estimate of an empty first message.</summary>
    public async Task<NewChatPage> NewChatAsync(CancellationToken cancellationToken = default)
    {
        var page = NewChat();
        if (Spending is not { } spend || page.Provider is not { } provider || page.Model is not { } model) return page;
        return page with { Estimate = await spend.EstimateNewAsync(provider, model, null, cancellationToken).ConfigureAwait(false) };
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

    /// <summary>The render context of an agent of the chat: its pending approvals by call id, the account label, the provider's billing
    /// kind and, with the branch's committed <paramref name="entries"/> and <paramref name="items"/>, the items' facts.</summary>
    public ChatRenderContext RenderContext(ChatBinding binding, long agentId, string? accountLabel, IReadOnlyList<JsonObject>? entries = null,
        IReadOnlyList<TranscriptItem>? items = null, ChatRunState? run = null, ChatSpendFacts? spend = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var approvals = new Dictionary<string, PendingApproval>(StringComparer.Ordinal);
        foreach (var approval in broker.Pending(binding.SessionId))
            if (approval.Request.AgentId == agentId && approval.Request.ParentCallId is null && approval.Request.CallId.Length > 0)
                approvals[approval.Request.CallId] = approval;
        entries ??= [];
        return new ChatRenderContext
        {
            SessionId = binding.SessionId,
            AgentId = agentId,
            Markdown = markdown,
            Approvals = approvals,
            AccountLabel = accountLabel,
            Provider = binding.Provider,
            Billing = Spending?.BillingOf(binding.Provider, binding.AccountId) ?? catalog.FindProvider(binding.Provider)?.Billing ?? BillingKind.Unknown,
            Catalog = catalog,
            Facts = items is null ? new Dictionary<string, ChatItemFacts>(StringComparer.Ordinal) : ChatFacts.Build(items, entries, run),
            Results = ChatFacts.ToolResults(entries),
            Spend = spend ?? ChatSpendFacts.Empty,
        };
    }

    /// <summary>The answers' cost marks of a branch from the spend ledger (empty without the Spend area).</summary>
    public Task<ChatSpendFacts> SpendFactsAsync(ChatBinding binding, IReadOnlyList<JsonObject> entries, bool running, string? accountLabel,
        CancellationToken cancellationToken = default) =>
        Spending is { } spend ? spend.FactsAsync(binding, entries, running, accountLabel, cancellationToken) : Task.FromResult(ChatSpendFacts.Empty);

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
