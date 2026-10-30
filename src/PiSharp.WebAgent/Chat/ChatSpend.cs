// Decision 0011 (run W3, S5): the Spend Guard on the chat pages. Before a send, the composer's estimate and the send's own check ask the
// guard's preflight (SpendGuard.PreflightAsync: nothing reserved) about the request the send would make: the chat's model and account, its
// run (the chat id) and the request's size, which the server works out from the branch's active entries (the messages the durable engine
// sends, failed and stopped answers left out, as its context does) plus the draft; the browser never says how big a request is. A send that
// carries `spend` is checked: when the guard would ask or block, nothing is posted and the answer is {posted: false} (200, so the browser
// logs no failed request) with the cause's confirm modal (S4's _SpendConfirmModal from SpendPages.ConfirmView). The user's choice comes back on the same request id: "proceed once" or "cap the
// output" is checked as the guard would apply it, then stored as the run's one-time approval (SpendGuard.ApproveStoredAsync), which the
// guard uses on the run's next stopped call; a Block still blocks. A request id that was admitted is never checked again, so a retried
// POST reaches the engine's de-duplication and posts once; the admitted ids and the approvals they stored are kept in webagent.db
// (ChatSpendSends, W3 fix), so this holds across a restart too. A call the guard stopped inside a run (its error names the op) is reviewed
// with that stop's own decision, and continues with a "Continue." message on the same branch after the user's choice (the stopped call's
// size, plus the message, is what is checked). Nothing switches a model or an account: cheaper models are suggestions in the modal. The
// ledger side (footers, the session panel) is read at most every LedgerLifetime per chat, and again whenever the branch's entries or
// running state change.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Durable.Server;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Spend;

namespace PiSharp.WebAgent.Chat;

/// <summary>The user's answer to the Spend Guard that a send carries (<c>spend: {check: true}</c>, or the choice made in the confirm modal).</summary>
/// <param name="Choice">Proceed once or cap the output; null only checks.</param>
/// <param name="MaxOutputTokens">The output cap, for <see cref="SpendChoice.CapOutput"/>.</param>
public sealed record ChatSpendAnswer(SpendChoice? Choice = null, int? MaxOutputTokens = null)
{
    /// <summary>Check only: refuse when the guard would ask or block.</summary>
    public static ChatSpendAnswer Check { get; } = new();

    /// <summary>The answer in a command's <c>spend</c> field, or null when it has none (the send is then not checked here, and the guard
    /// still stops it inside the run).</summary>
    /// <exception cref="ChatException">The choice or output cap is not allowed (400).</exception>
    public static ChatSpendAnswer? Read(JsonNode? spend)
    {
        if (spend is null) return null;
        if (spend is not JsonObject body) throw ChatException.Invalid("spend is {check: true} or {choice, maxOutputTokens?}.");
        var tokens = ChatDisplay.Long(body, "maxOutputTokens");
        var choice = ChatDisplay.Str(body, "choice")?.Trim();
        switch (choice)
        {
            case null or "" or "check":
                return Check;
            case "proceed-once":
                return new ChatSpendAnswer(SpendChoice.ProceedOnce);
            case "cap-output":
                if (tokens is not (>= 1 and <= int.MaxValue)) throw ChatException.Invalid("Capping the output needs maxOutputTokens of 1 or more.");
                return new ChatSpendAnswer(SpendChoice.CapOutput, (int)tokens);
            default:
                throw ChatException.Invalid($"\"{choice}\" is not a spend choice: proceed-once or cap-output.");
        }
    }
}

/// <summary>What the guard says about a chat request, as the chat pages answer it.</summary>
/// <param name="Verdict"><c>allow</c>, <c>ask</c> or <c>block</c>.</param>
/// <param name="Allowed">Whether it may be sent as it is.</param>
/// <param name="Code">The spend error code, or null when allowed.</param>
/// <param name="Pane">The confirm modal's pane, or null.</param>
/// <param name="CanProceed">Whether the user may proceed once (not for a Block).</param>
/// <param name="Sentence">Why it is stopped, or null.</param>
/// <param name="Chip">The composer's chip, or null.</param>
/// <param name="Html">The composer's estimate (_ChatSpendEstimate).</param>
/// <param name="Modal">The confirm modal (_SpendConfirmModal), when it is not allowed; null otherwise.</param>
/// <param name="CapOutputTokens">The output cap the modal offers, or null.</param>
public sealed record ChatSpendResult(string Verdict, bool Allowed, string? Code, string? Pane, bool CanProceed, string? Sentence, string? Chip, string Html,
    string? Modal, int? CapOutputTokens);

/// <summary>A send the Spend Guard would not let through as it is; nothing was posted. ChatController answers it 200
/// <c>{posted: false, code, message, spend}</c>: a refusal the page expects and answers with the confirm modal, not a failed request.</summary>
/// <param name="result">The guard's answer, with the confirm modal.</param>
public sealed class ChatSpendException(ChatSpendResult result) : Exception(result.Sentence ?? "This request is over a spend cap.")
{
    /// <summary>The guard's answer.</summary>
    public ChatSpendResult Result { get; } = result;

    /// <summary>The spend error code.</summary>
    public string Code => Result.Code ?? SpendErrors.Ask;
}

/// <summary>The Spend Guard on the chat pages. A singleton; without the Spend area's services it shows nothing and checks nothing.</summary>
public sealed class ChatSpend(DeviceHostService device, IModelCatalog catalog, FragmentRenderer renderer, IServiceProvider services, ILogger<ChatSpend> logger)
{
    /// <summary>The message that continues a run after the user answered a stopped call.</summary>
    public const string ContinueText = "Continue.";

    /// <summary>The composer's estimate partial.</summary>
    public const string EstimatePartial = "~/Views/Chat/_ChatSpendEstimate.cshtml";

    /// <summary>The bytes a message adds around its text in the request (role, content block, timestamp).</summary>
    public const int MessageOverhead = 64;

    /// <summary>How long a chat's ledger rows and spend summary are kept before they are read again (they are also read again when the
    /// branch's entries or running state change).</summary>
    public static TimeSpan LedgerLifetime { get; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a branch's context size is kept while the draft changes.</summary>
    public static TimeSpan ContextLifetime { get; } = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, (string Stamp, DateTimeOffset At, IReadOnlyList<Reservation> Rows)> reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ChatSpendSummary Summary)> summaries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Session, long Agent), (DateTimeOffset At, long Bytes)> contexts = new();

    /// <summary>The Spend Guard, or null when the Spend area is not registered.</summary>
    public SpendGuard? Guard => services.GetService<SpendGuard>();

    private SpendPages? Pages => services.GetService<SpendPages>();

    private ISpendLedger? Ledger => services.GetService<ISpendLedger>();

    private IChatAccounts Accounts => services.GetService<IChatAccounts>() ?? NoChatAccounts.Instance;

    /// <summary>The sends admitted so far and the approvals they stored (webagent.db; in memory when the chat area's store is missing).</summary>
    public ChatSpendSends Sends => sends ??= services.GetService<ChatSpendSends>() ?? new ChatSpendSends(null);

    private ChatSpendSends? sends;

    /// <summary>How <paramref name="provider"/> through <paramref name="account"/> is billed: the Spend area's billing modes (the user's
    /// override included), else the catalog's.</summary>
    public BillingKind BillingOf(string provider, string? account)
    {
        try
        {
            if (services.GetService<IBillingModes>() is { } modes) return modes.Resolve(provider, account).Mode;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Resolving the billing of {Provider} failed.", provider);
        }
        return catalog.FindProvider(provider)?.Billing ?? BillingKind.Unknown;
    }

    // ─── Request size ───────────────────────────────────────────────────────

    /// <summary>The UTF-8 size of the messages a branch's next request sends: every active entry's messages, without the answers the
    /// durable context leaves out (stopped, failed or deferred) and without the field that names an answer's ledger row (the guard drops it
    /// before a request is sent).</summary>
    public static long ContextBytes(IEnumerable<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        long bytes = 0;
        foreach (var entry in entries)
            foreach (var message in (entry["model"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (ChatDisplay.Str(message, "role") == "assistant" && ChatDisplay.Str(message, "stopReason") is "aborted" or "error" or "deferred") continue;
                var sent = message;
                if (message.ContainsKey(GuardedDurableModels.AnswerField))
                {
                    sent = (JsonObject)message.DeepClone();
                    sent.Remove(GuardedDurableModels.AnswerField);
                }
                bytes += Encoding.UTF8.GetByteCount(sent.ToJsonString());
            }
        return bytes;
    }

    /// <summary>The size a draft adds as a user message (0 for none).</summary>
    public static long DraftBytes(string? draft) => string.IsNullOrEmpty(draft) ? 0 : Encoding.UTF8.GetByteCount(draft) + MessageOverhead;

    /// <summary>The call a send of <paramref name="draft"/> to a branch of the chat would make (no op: nothing is reserved).</summary>
    public async Task<SpendCall> CallAsync(ChatBinding binding, long? agentId, string? draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var bytes = await BranchBytesAsync(binding.SessionId, agentId ?? binding.AgentId, cancellationToken).ConfigureAwait(false);
        return new SpendCall
        {
            Provider = binding.Provider,
            Model = binding.Model,
            Account = binding.AccountId,
            Source = SpendSource.Chat,
            Run = binding.SessionId,
            InputBytes = bytes + DraftBytes(draft),
        };
    }

    /// <summary>The call a new chat's first message would make. Its run is a stand-in key with nothing spent, so per-chat caps apply.</summary>
    public static SpendCall NewChatCall(string provider, string model, string? account, string? draft, string? requestId = null) => new()
    {
        Provider = provider,
        Model = model,
        Account = account,
        Source = SpendSource.Chat,
        Run = "chat-new:" + (string.IsNullOrEmpty(requestId) ? "preflight" : requestId),
        InputBytes = DraftBytes(draft),
    };

    private async Task<long> BranchBytesAsync(string sessionId, long agentId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (contexts.TryGetValue((sessionId, agentId), out var known) && now - known.At < ContextLifetime) return known.Bytes;
        var session = device.Host.Sessions.FirstOrDefault(item => item.Id == sessionId);
        if (session is null) return 0;
        var entries = await AgentFrames.ActiveEntriesAsync(session, agentId, cancellationToken).ConfigureAwait(false);
        var bytes = ContextBytes(entries);
        if (contexts.Count > 500) contexts.Clear();
        contexts[(sessionId, agentId)] = (now, bytes);
        return bytes;
    }

    // ─── Checks ─────────────────────────────────────────────────────────────

    /// <summary>What the guard says about <paramref name="call"/> now (nothing reserved), with the composer's estimate and, when it is not
    /// allowed and <paramref name="modal"/>, the confirm modal; null without a Spend Guard.</summary>
    public async Task<ChatSpendResult?> CheckAsync(SpendCall call, bool modal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (Guard is not { } guard) return null;
        var decision = await guard.PreflightAsync(call, cancellationToken).ConfigureAwait(false);
        return await ResultAsync(call, decision, modal, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The composer's estimate of a draft on a branch of the chat, or <see cref="ChatSpendEstimateView.None"/> without a guard (or
    /// when it can't be worked out: the page still sends, and the guard still checks the request itself).</summary>
    public async Task<ChatSpendEstimateView> EstimateAsync(ChatBinding binding, long? agentId, string? draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (Guard is not { } guard) return ChatSpendEstimateView.None;
        try
        {
            var call = await CallAsync(binding, agentId, draft, cancellationToken).ConfigureAwait(false);
            return await ViewAsync(call, await guard.PreflightAsync(call, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && ChatErrors.From(error) is null)
        {
            logger.LogDebug(error, "The send estimate of chat {Chat} could not be worked out.", binding.SessionId);
            return ChatSpendEstimateView.None;
        }
    }

    /// <summary>The composer's estimate of a new chat's first message.</summary>
    public async Task<ChatSpendEstimateView> EstimateNewAsync(string provider, string model, string? draft, CancellationToken cancellationToken = default)
    {
        if (Guard is not { } guard) return ChatSpendEstimateView.None;
        try
        {
            var account = (await Accounts.BindAsync(provider, cancellationToken).ConfigureAwait(false))?.Id;
            var call = NewChatCall(provider, model, account, draft);
            return await ViewAsync(call, await guard.PreflightAsync(call, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && ChatErrors.From(error) is null)
        {
            logger.LogDebug(error, "The send estimate of a new chat with {Provider}/{Model} could not be worked out.", provider, model);
            return ChatSpendEstimateView.None;
        }
    }

    /// <summary>The composer's estimate as the pages answer it (the partial rendered; no modal).</summary>
    public async Task<ChatSpendResult> RenderAsync(ChatSpendEstimateView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var html = await renderer.RenderAsync(EstimatePartial, view).ConfigureAwait(false);
        return new ChatSpendResult(view.Verdict, view.Verdict == "allow", view.Code, view.Pane, view.Verdict == "ask", null, view.Chip, html, null, null);
    }

    /// <summary>Lets a send through, or refuses it (nothing posted): a request id admitted before passes at once; else the guard's
    /// preflight decides, and the user's choice, when given, is checked as the guard would apply it. Returns the one-time approval to store
    /// for the run before the send goes out (<see cref="ApproveAsync"/>), or null when none is needed.</summary>
    /// <param name="call">The request the send makes.</param>
    /// <param name="key">The send's de-duplication key (chat and request id).</param>
    /// <param name="answer">The check, or the user's choice.</param>
    /// <param name="run">The run the approval is for (the chat id; a new chat's once it exists).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="ChatSpendException">The guard would ask or block.</exception>
    public async Task<SpendApproval?> AdmitAsync(SpendCall call, string key, ChatSpendAnswer answer, string? run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(answer);
        if (Guard is not { } guard || Sends.IsAdmitted(key)) return null;
        var decision = await guard.PreflightAsync(call, cancellationToken).ConfigureAwait(false);
        if (decision.Allowed)
        {
            Admit(key);
            return null;
        }
        var refused = call;
        if (answer.Choice is { } choice)
        {
            var answered = call with { Approved = true, MaxTokens = choice == SpendChoice.CapOutput ? answer.MaxOutputTokens : call.MaxTokens };
            var second = await guard.PreflightAsync(answered, cancellationToken).ConfigureAwait(false);
            if (second.Allowed)
            {
                Admit(key);
                logger.LogInformation("Chat send {Key} admitted after the user's answer ({Choice}).", key, SpendApprovalStore.Wire(choice));
                return new SpendApproval(run ?? call.Run ?? "", choice) { MaxOutputTokens = choice == SpendChoice.CapOutput ? answer.MaxOutputTokens : null };
            }
            (refused, decision) = (answered, second);
        }
        throw new ChatSpendException(await ResultAsync(refused, decision, modal: true, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Stores <paramref name="approval"/> as the run's one-time approval, once per send key.</summary>
    public async Task ApproveAsync(SpendApproval approval, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (Guard is not { } guard || Sends.ApprovalOf(key) is not null) return;
        var stored = await guard.ApproveStoredAsync(approval, cancellationToken).ConfigureAwait(false);
        Sends.Approved(key, stored.Id);
    }

    /// <summary>Whether a send key was admitted (a retry of it is not checked again, also after a restart).</summary>
    public bool WasAdmitted(string key) => Sends.IsAdmitted(key);

    private void Admit(string key) => Sends.Admit(key);

    /// <summary>A stopped call of the chat: the guard's own record of it (op, size, decision), or null when there is none (or it belongs to
    /// another run).</summary>
    public async Task<SpendStop?> StopAsync(string chatId, string op, CancellationToken cancellationToken = default)
    {
        if (Guard is not { } guard || string.IsNullOrWhiteSpace(op)) return null;
        var stop = await guard.StoppedAsync(op.Trim(), cancellationToken).ConfigureAwait(false);
        return stop?.Call.Run == chatId ? stop : null;
    }

    /// <summary>The confirm modal of a stopped call, from the stop's own decision.</summary>
    public async Task<ChatSpendResult?> StopResultAsync(SpendStop stop, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stop);
        if (Guard is null) return null;
        return await ResultAsync(stop.Call, stop.Decision, modal: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The call that continuing after <paramref name="stop"/> makes: the stopped request plus the "Continue." message.</summary>
    public static SpendCall ContinueCall(SpendStop stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return stop.Call with { Op = "", Approved = false, Note = null, InputBytes = stop.Call.InputBytes + DraftBytes(ContinueText) };
    }

    private async Task<ChatSpendResult> ResultAsync(SpendCall call, SpendDecision decision, bool modal, CancellationToken cancellationToken)
    {
        var view = await ViewAsync(call, decision, cancellationToken).ConfigureAwait(false);
        var html = await renderer.RenderAsync(EstimatePartial, view).ConfigureAwait(false);
        string? confirm = null;
        int? capOutput = null;
        if (modal && !decision.Allowed && decision.Causes.Count > 0 && Pages is { } pages)
        {
            var confirmView = pages.ConfirmView(call, decision);
            capOutput = confirmView.CapOutputTokens;
            confirm = await renderer.RenderAsync(SpendEndpoints.ConfirmPartial, confirmView).ConfigureAwait(false);
        }
        return new ChatSpendResult(view.Verdict, decision.Allowed, decision.ErrorCode, view.Pane, !decision.Allowed && decision.Verdict != SpendVerdict.Block,
            decision.Allowed ? null : SpendErrors.Describe(decision), view.Chip, html, confirm, capOutput);
    }

    private async Task<ChatSpendEstimateView> ViewAsync(SpendCall call, SpendDecision decision, CancellationToken cancellationToken)
    {
        var label = await AccountLabelAsync(call.Provider, call.Account, cancellationToken).ConfigureAwait(false);
        var pages = Pages;
        return ChatSpendText.Estimate(decision, new ChatSpendNames(catalog.FindProvider(call.Provider)?.DisplayName ?? call.Provider, label)
        {
            CapName = pages is null ? null : pages.CapName,
        });
    }

    private async Task<string?> AccountLabelAsync(string provider, string? account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(account)) return null;
        try
        {
            return (await Accounts.FindAsync(provider, account, cancellationToken).ConfigureAwait(false))?.Label;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Reading the label of an account of {Provider} failed.", provider);
            return null;
        }
    }

    // ─── Ledger ─────────────────────────────────────────────────────────────

    /// <summary>Raised with a chat id after one of its ledger rows changed (settled, released, made uncertain, resolved): that chat's cached
    /// rows and summaries are dropped first, so its next render reads them again (W3 fix: a Resolve on History › Spend shows in the open
    /// chat's footers without a reload). The first handler hooks the ledger's <see cref="SpendLedger.Changed"/>; without the Spend area it is
    /// never raised.</summary>
    public event Action<string>? RunChanged
    {
        add
        {
            HookLedger();
            lock (hooks) runChanged += value;
        }
        remove
        {
            lock (hooks) runChanged -= value;
        }
    }

    private readonly Lock hooks = new();
    private Action<string>? runChanged;
    private int hooked;

    /// <summary>Drops chat <paramref name="run"/>'s cached ledger rows and spend summaries.</summary>
    public void Forget(string run)
    {
        ArgumentException.ThrowIfNullOrEmpty(run);
        reads.TryRemove(run, out _);
        foreach (var key in summaries.Keys)
        {
            if (key.StartsWith(run + "|", StringComparison.Ordinal)) summaries.TryRemove(key, out _);
        }
    }

    private void HookLedger()
    {
        if (Interlocked.Exchange(ref hooked, 1) == 1) return;
        if (services.GetService<SpendLedger>() is { } ledger) ledger.Changed += LedgerChanged;
    }

    private void LedgerChanged(Reservation row)
    {
        if (row.Run is not { Length: > 0 } run) return;
        Forget(run);
        Action<string>? handlers;
        lock (hooks) handlers = runChanged;
        handlers?.Invoke(run);
    }

    /// <summary>The footer marks of a branch's answers and the running answer's reservation, from the chat's ledger rows.</summary>
    public async Task<ChatSpendFacts> FactsAsync(ChatBinding binding, IReadOnlyList<JsonObject> entries, bool running, string? accountLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(entries);
        if (Ledger is not { } ledger) return ChatSpendFacts.Empty;
        var stamp = $"{entries.Count}|{entries.Select(entry => ChatDisplay.Long(entry, "id") ?? 0).DefaultIfEmpty().Max()}|{running}";
        var now = DateTimeOffset.UtcNow;
        if (!reads.TryGetValue(binding.SessionId, out var read) || read.Stamp != stamp || now - read.At > LedgerLifetime)
        {
            try
            {
                var page = await ledger.QueryAsync(new SpendLedgerQuery { Run = binding.SessionId, Limit = SpendLedgerQuery.MaxLimit }, cancellationToken).ConfigureAwait(false);
                read = (stamp, now, page.Items);
                if (reads.Count > 500) reads.Clear();
                reads[binding.SessionId] = read;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogDebug(error, "Reading the spend ledger of chat {Chat} failed.", binding.SessionId);
                return ChatSpendFacts.Empty;
            }
        }
        return ChatSpendText.Link(entries, read.Rows, running, row => ChatSpendText.Mark(row, row.Account == binding.AccountId ? accountLabel : null));
    }

    /// <summary>The chat's spend for the session panel, or null without a ledger.</summary>
    public async Task<ChatSpendSummary?> SummaryAsync(ChatBinding binding, string? accountLabel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (Ledger is not { } ledger) return null;
        var now = DateTimeOffset.UtcNow;
        var key = binding.SessionId + "|" + binding.Provider + "|" + binding.AccountId + "|" + accountLabel;
        if (summaries.TryGetValue(key, out var known) && now - known.At < LedgerLifetime) return known.Summary;
        try
        {
            var total = await ledger.TotalAsync(new SpendLedgerQuery { Run = binding.SessionId }, cancellationToken).ConfigureAwait(false);
            var caps = await ledger.CapStatusAsync(binding.SessionId, cancellationToken).ConfigureAwait(false);
            var pages = Pages;
            var summary = ChatSpendText.Summary(binding.SessionId, total,
                caps.Where(status => status.Cap.AppliesTo(binding.Provider, binding.AccountId, SpendSource.Chat)),
                BillingOf(binding.Provider, binding.AccountId), accountLabel, pages is null ? null : pages.CapName);
            if (summaries.Count > 500) summaries.Clear();
            summaries[key] = (now, summary);
            return summary;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Reading the spend of chat {Chat} failed.", binding.SessionId);
            return null;
        }
    }
}
