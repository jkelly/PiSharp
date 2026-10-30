// Decision 0011 (run W3, S5): the Spend Guard on the chat pages. Before a send, the composer's estimate and the send's own check ask the
// guard's preflight (SpendGuard.PreflightAsync: nothing reserved) about the request the send would make: the chat's model and account, its
// run (the chat id) and the request's size, which the server works out from the branch's active entries (the messages the durable engine
// sends, failed and stopped answers left out, as its context does) plus the draft; the browser never says how big a request is. A send that
// carries `spend` is checked: when the guard would ask or block, nothing is posted and the answer is 409 with the cause's confirm modal
// (S4's _SpendConfirmModal from SpendPages.ConfirmView). The user's choice comes back on the same request id: "proceed once" or "cap the
// output" is checked as the guard would apply it, then stored as the one-time approval of that message (SpendGuard.ApproveStoredAsync),
// which the guard uses when the call that sends the message is stopped; a Block still blocks. A request id that was admitted is never
// checked again, so a retried POST reaches the engine's de-duplication and posts once. A call the guard stopped inside a run (its error
// names the op) is reviewed
// with that stop's own decision, and continues with a "Continue." message on the same branch after the user's choice (the stopped call's
// size, plus the message, is what is checked). Nothing switches a model or an account: cheaper models are suggestions in the modal. The
// ledger side (footers, the session panel) is read at most every LedgerLifetime per chat, and again whenever the branch's entries or
// running state change.
//
// W3 fix (chat spend): the one-time approval belongs to the message the user approved (SpendApproval.Submission, the key of its text), so
// only the call that sends that message uses it: not the running turn's next call while the message waits as a follow-up, not a
// compaction, not another send. Every check here ignores approvals that wait (PreflightAsync withApprovals: false), so a send the user has
// not answered is always asked about. A send key's check, approval and send run one at a time (EnterAsync), so two copies of one POST
// store one approval; a send that fails after its approval was stored withdraws it (WithdrawAsync) and is checked again when retried.
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

/// <summary>A send the Spend Guard would not let through as it is (409); nothing was posted.</summary>
/// <param name="result">The guard's answer, with the confirm modal.</param>
public sealed class ChatSpendException(ChatSpendResult result) : Exception(result.Sentence ?? "This request is over a spend cap.")
{
    /// <summary>The HTTP status.</summary>
    public const int Status = 409;

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

    private readonly ConcurrentDictionary<string, byte> admitted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> approved = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> withdrawn = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Chat, long Submission), string> posted = new();
    private readonly Dictionary<string, SendGate> gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Stamp, DateTimeOffset At, IReadOnlyList<Reservation> Rows)> reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ChatSpendSummary Summary)> summaries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Session, long Agent), (DateTimeOffset At, long Bytes)> contexts = new();

    /// <summary>The Spend Guard, or null when the Spend area is not registered.</summary>
    public SpendGuard? Guard => services.GetService<SpendGuard>();

    private SpendPages? Pages => services.GetService<SpendPages>();

    private ISpendLedger? Ledger => services.GetService<ISpendLedger>();

    private IChatAccounts Accounts => services.GetService<IChatAccounts>() ?? NoChatAccounts.Instance;

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
    /// durable context leaves out (stopped, failed or deferred).</summary>
    public static long ContextBytes(IEnumerable<JsonObject> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        long bytes = 0;
        foreach (var entry in entries)
            foreach (var message in (entry["model"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (ChatDisplay.Str(message, "role") == "assistant" && ChatDisplay.Str(message, "stopReason") is "aborted" or "error" or "deferred") continue;
                bytes += Encoding.UTF8.GetByteCount(message.ToJsonString());
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

    /// <summary>What the guard says about <paramref name="call"/> now (nothing reserved; an approval that waits for another send is not
    /// counted), with the composer's estimate and, when it is not allowed and <paramref name="modal"/>, the confirm modal; null without a
    /// Spend Guard.</summary>
    public async Task<ChatSpendResult?> CheckAsync(SpendCall call, bool modal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (Guard is not { } guard) return null;
        var decision = await guard.PreflightAsync(call, withApprovals: false, cancellationToken).ConfigureAwait(false);
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
            return await ViewAsync(call, await guard.PreflightAsync(call, withApprovals: false, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
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
            return await ViewAsync(call, await guard.PreflightAsync(call, withApprovals: false, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
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

    /// <summary>Lets a send through, or refuses it (409, nothing posted): a request id admitted before passes at once; else the guard's
    /// preflight decides (an approval that waits for another send is not counted), and the user's choice, when given, is checked as the
    /// guard would apply it. Returns the one-time approval to store before the send goes out (<see cref="ApproveAsync"/>), scoped to the
    /// message the send posts, or null when none is needed. Call it inside the send key's <see cref="EnterAsync"/>.</summary>
    /// <param name="call">The request the send makes.</param>
    /// <param name="key">The send's de-duplication key (chat and request id).</param>
    /// <param name="answer">The check, or the user's choice.</param>
    /// <param name="run">The run the approval is for (the chat id; a new chat's once it exists).</param>
    /// <param name="text">The message the send posts: the approval answers only the call that sends it.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="ChatSpendException">The guard would ask or block.</exception>
    public async Task<SpendApproval?> AdmitAsync(SpendCall call, string key, ChatSpendAnswer answer, string? run, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(text);
        if (admitted.ContainsKey(key) || Guard is not { } guard) return null;
        var decision = await guard.PreflightAsync(call, withApprovals: false, cancellationToken).ConfigureAwait(false);
        if (decision.Allowed)
        {
            Admit(key);
            return null;
        }
        var refused = call;
        if (answer.Choice is { } choice)
        {
            var answered = call with { Approved = true, MaxTokens = choice == SpendChoice.CapOutput ? answer.MaxOutputTokens : call.MaxTokens };
            var second = await guard.PreflightAsync(answered, withApprovals: false, cancellationToken).ConfigureAwait(false);
            if (second.Allowed)
            {
                Admit(key);
                logger.LogInformation("Chat send {Key} admitted after the user's answer ({Choice}).", key, SpendApprovalStore.Wire(choice));
                return new SpendApproval(run ?? call.Run ?? "", choice)
                {
                    MaxOutputTokens = choice == SpendChoice.CapOutput ? answer.MaxOutputTokens : null,
                    Submission = GuardedDurableModels.SubmissionKey(text),
                };
            }
            (refused, decision) = (answered, second);
        }
        throw new ChatSpendException(await ResultAsync(refused, decision, modal: true, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Stores <paramref name="approval"/> as the one-time approval of the send <paramref name="key"/>, once per send key (call it
    /// inside the key's <see cref="EnterAsync"/>). Returns the stored approval, or null when none was stored now.</summary>
    public async Task<StoredApproval?> ApproveAsync(SpendApproval approval, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (Guard is not { } guard || approved.ContainsKey(key)) return null;
        var stored = await guard.ApproveStoredAsync(approval, cancellationToken).ConfigureAwait(false);
        if (approved.Count > 2000) approved.Clear();
        approved[key] = stored.Id;
        return stored;
    }

    /// <summary>Undoes the admission of the send <paramref name="key"/> after its send failed: its approval, when one was stored, is retired
    /// (<see cref="SpendApprovalStore.Withdrawn"/>) so it answers nothing else, and a retry of the key is checked again. A failure to retire
    /// it is logged, not thrown (the send's own failure is what the caller reports).</summary>
    public async Task WithdrawAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (admitted.TryRemove(key, out _))
        {
            if (withdrawn.Count > 2000) withdrawn.Clear();
            withdrawn[key] = 0;
        }
        await RetireAsync(key, "failed").ConfigureAwait(false);
    }

    /// <summary>Notes that the send <paramref name="key"/> was posted as <paramref name="submissionId"/> of the chat, when it stored an
    /// approval, so withdrawing that queued message (<see cref="WithdrawPostedAsync"/>) also withdraws its approval.</summary>
    public void Posted(string chatId, long submissionId, string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(chatId);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!approved.ContainsKey(key)) return;
        if (posted.Count > 2000) posted.Clear();
        posted[(chatId, submissionId)] = key;
    }

    /// <summary>After the user withdrew the queued message <paramref name="submissionId"/> of the chat: the approval its send stored, when
    /// one still waits, is retired (<see cref="SpendApprovalStore.Withdrawn"/>), so it never answers a later copy of the same text.</summary>
    public async Task WithdrawPostedAsync(string chatId, long submissionId)
    {
        if (posted.TryRemove((chatId, submissionId), out var key)) await RetireAsync(key, "was withdrawn").ConfigureAwait(false);
    }

    private async Task RetireAsync(string key, string why)
    {
        if (!approved.TryRemove(key, out var id) || Guard is not { } guard) return;
        try
        {
            if (await guard.Approvals.RetireAsync(id, SpendApprovalStore.Withdrawn).ConfigureAwait(false))
                logger.LogInformation("Chat send {Key} {Why}; its spend approval was withdrawn.", key, why);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The send's own outcome is what the caller reports; the approval then answers only its own message until it expires.
            logger.LogWarning(error, "The spend approval of chat send {Key} could not be withdrawn.", key);
        }
    }

    /// <summary>Whether a send key was admitted (a retry of it is not checked again).</summary>
    public bool WasAdmitted(string key) => admitted.ContainsKey(key);

    /// <summary>Whether a send key was admitted and then withdrawn because its send failed, and not admitted again since (a retry of it is
    /// checked again even where the first copy already made something, such as a new chat).</summary>
    public bool WasWithdrawn(string key) => withdrawn.ContainsKey(key);

    /// <summary>Waits until no other request holds the send <paramref name="key"/>, then holds it until the result is disposed: one copy of a
    /// send at a time is checked, approved and sent, so two copies of one POST never store two approvals.</summary>
    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        SendGate? gate;
        lock (gates)
        {
            if (!gates.TryGetValue(key, out gate)) gates[key] = gate = new SendGate();
            gate.Users++;
        }
        try
        {
            await gate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, gate);
            throw;
        }
        return new SendHold(this, key, gate);
    }

    private void Leave(string key, SendGate gate)
    {
        lock (gates)
        {
            if (--gate.Users == 0) gates.Remove(key);
        }
    }

    private void Admit(string key)
    {
        if (admitted.Count > 2000) admitted.Clear();
        admitted[key] = 0;
        withdrawn.TryRemove(key, out _);
    }

    /// <summary>One send key's lock and how many requests hold or wait for it.</summary>
    private sealed class SendGate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public int Users { get; set; }
    }

    /// <summary>A held send key; disposing releases it once.</summary>
    private sealed class SendHold(ChatSpend owner, string key, SendGate gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            gate.Lock.Release();
            owner.Leave(key, gate);
        }
    }

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
