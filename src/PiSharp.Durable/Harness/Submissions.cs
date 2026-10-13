// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/submissions.ts. Inbox items keep a draft's content or
// entry as plain JSON (upstream's copyJson with omitUndefinedProperties: a C# draft has no undefined fields). A settled record is the
// SubmissionRecord whose status is done or unanswered (upstream SettledSubmissionRecord).
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Admission, waits and withdrawal of the durable submissions of one Harness (upstream <c>Submissions</c>).</summary>
internal sealed class Submissions
{
    private readonly Session session;
    private readonly IStorage storage;
    private readonly Func<double> now;
    /// <summary>Read at each admission, on the Session line.</summary>
    private readonly Func<Settings> queueModes;
    /// <summary>Enables task scheduling; submitting or waiting asks for progress.</summary>
    private readonly Action resume;
    private readonly Waiters<SubmissionId, SubmissionRecord> waiters = new();
    private volatile bool closed;

    public Submissions(Session session, IStorage storage, Func<double> now, Func<Settings> queueModes, Action resume)
    {
        this.session = session;
        this.storage = storage;
        this.now = now;
        this.queueModes = queueModes;
        this.resume = resume;
        session.SubscribeCommits((publication, _) => Observe(publication));
        session.SubscribeClose(() =>
        {
            closed = true;
            waiters.RejectAll(HarnessUtil.ClosedError());
        });
    }

    /// <summary>Admits a submission in one commit; see <see cref="AdmitSubmissionAsync"/>.</summary>
    public async Task<ISubmission> SubmitAsync(ConversationId conversationId, SubmissionDraft draft, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(draft);
        resume();
        var id = await session.CommitWithAsync(tx => AdmitSubmissionAsync(tx, conversationId, draft, now(), queueModes()), context)
            .ConfigureAwait(false);
        return new SubmissionHandle(id, this);
    }

    /// <summary>A handle for an existing submission, or null.</summary>
    public async Task<ISubmission?> GetAsync(SubmissionId id, DurableContext context)
    {
        var record = await session.ReadOnLineAsync(() => storage.GetSubmissionAsync(id, context)).ConfigureAwait(false);
        return record is null ? null : new SubmissionHandle(record.Id, this);
    }

    public async Task<SubmissionRecord> StatusAsync(SubmissionId id, DurableContext context) =>
        await session.ReadOnLineAsync(() => storage.GetSubmissionAsync(id, context)).ConfigureAwait(false) ??
        throw new InvalidOperationException($"Submission {id} does not exist");

    public async Task<SubmissionRecord> WaitAsync(SubmissionId id, DurableContext context)
    {
        resume();
        // Check and register on the line so no settling publication falls between them.
        var found = await session.ReadOnLineAsync(async () =>
        {
            var record = await storage.GetSubmissionAsync(id, context).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Submission {id} does not exist");
            if (IsSettled(record)) return Task.FromResult(record);
            // Close rejects registered waiters synchronously and may begin during the read.
            if (closed) throw HarnessUtil.ClosedError();
            return waiters.Add(id, context);
        }).ConfigureAwait(false);
        return await found.ConfigureAwait(false);
    }

    /// <summary>Withdraws a queued submission and removes its inbox item; placed inputs and settled submissions are reported.</summary>
    public Task<SubmissionAbortResult> AbortAsync(SubmissionId id, DurableContext context, ConversationId? conversationId = null) =>
        session.CommitWithAsync(async tx =>
        {
            var record = await tx.GetSubmissionAsync(id).ConfigureAwait(false);
            if (record is null || (conversationId is { } scope && record.ConversationId != scope)) return SubmissionAbortResult.NotFound;
            if (record.Status == SubmissionStatus.Queued)
            {
                tx.SettleSubmission(id, SubmissionSettlement.Unanswered("aborted"));
                await Inbox.RemoveInboxItemAsync(tx, record.ConversationId, id).ConfigureAwait(false);
                return SubmissionAbortResult.Aborted;
            }
            return record.Status == SubmissionStatus.Placed ? SubmissionAbortResult.AlreadyPlaced : SubmissionAbortResult.Settled;
        }, context);

    private void Observe(CommitPublication publication)
    {
        foreach (var change in publication.Changes)
            if (change is SubmissionWrite { Value: var record } && IsSettled(record)) waiters.Resolve(record.Id, record);
    }

    private static bool IsSettled(SubmissionRecord record) => record.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered;

    /// <summary>
    /// Admits a submission inside a commit (upstream <c>admitSubmission</c>, spec §6); <c>Conversation.SubmitAsync()</c> and
    /// conversation-owned compactions share it. A known request ID returns its existing submission without writing. A busy conversation
    /// queues it in <c>pi.inbox</c>, or rejects <c>whenBusy: "reject"</c> input with <see cref="ConversationBusyException"/>. An idle
    /// conversation with queued items queues it behind them and runs a final boundary. Otherwise idle input places a user entry and starts a
    /// run, and an idle write appends its entry and settles <c>done</c>, or <c>stale</c> when its head reaches before the active range.
    /// </summary>
    internal static async Task<SubmissionId> AdmitSubmissionAsync(ITx tx, ConversationId conversationId, SubmissionDraft draft, double now,
        Settings queueModes)
    {
        if (draft.RequestId is { } requestId)
        {
            var existing = await tx.GetSubmissionByRequestAsync(conversationId, requestId).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Type != draft.Type)
                    throw new InvalidOperationException($"Request {requestId} already identifies a submission of type {Wire(existing.Type)}");
                return existing.Id;
            }
        }
        var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
        var busy = live.ContainsKey("run");
        if (busy && draft.Type == SubmissionType.Input && draft.WhenBusy == WhenBusy.Reject) throw new ConversationBusyException(conversationId);
        // A boundary reads the table, so it is prepared before the first table write; a busy one needs none.
        var boundary = busy ? null : await Inbox.PrepareBoundaryAsync(tx, conversationId, queueModes).ConfigureAwait(false);
        if (boundary is null || boundary.Inbox.GetArray("items").Count > 0)
        {
            var queued = await tx.CreateSubmissionAsync(new SubmissionCreate
            {
                ConversationId = conversationId, RequestId = draft.RequestId, Type = draft.Type, Status = SubmissionStatus.Queued,
            }).ConfigureAwait(false);
            var items = (boundary?.Inbox ?? await tx.DocAsync(Inbox.InboxDoc, conversationId).ConfigureAwait(false)).GetArray("items");
            items.Add(draft.Type == SubmissionType.Write
                ? JsonValue.Object([
                    KeyValuePair.Create("id", (JsonValue)queued.Id.ToJson()),
                    KeyValuePair.Create("mode", (JsonValue)"write"),
                    KeyValuePair.Create("entry", (JsonValue)Inbox.EntryDraftToJson(draft.Entry!)),
                ])
                : JsonValue.Object([
                    KeyValuePair.Create("id", (JsonValue)queued.Id.ToJson()),
                    KeyValuePair.Create("mode", (JsonValue)(draft.WhenBusy == WhenBusy.Steer ? "steer" : "followUp")),
                    KeyValuePair.Create("content", draft.Content!),
                ]));
            if (boundary is null) return queued.Id;
            var (users, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, now).ConfigureAwait(false);
            if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users).ConfigureAwait(false);
            return queued.Id;
        }
        if (draft.Type == SubmissionType.Write)
        {
            var entryDraft = draft.Entry!;
            if (Inbox.IsStale(boundary, entryDraft))
            {
                return (await tx.CreateSubmissionAsync(new SubmissionCreate
                {
                    ConversationId = conversationId, RequestId = draft.RequestId, Type = SubmissionType.Write, Status = SubmissionStatus.Unanswered,
                    Reason = "stale",
                }).ConfigureAwait(false)).Id;
            }
            var written = await tx.AppendEntryAsync(conversationId, entryDraft).ConfigureAwait(false);
            return (await tx.CreateSubmissionAsync(new SubmissionCreate
            {
                ConversationId = conversationId, RequestId = draft.RequestId, Type = SubmissionType.Write, Status = SubmissionStatus.Done,
                Entry = written.Id,
            }).ConfigureAwait(false)).Id;
        }
        var message = JsonValue.Object([
            KeyValuePair.Create("role", (JsonValue)"user"),
            KeyValuePair.Create("content", draft.Content!),
            KeyValuePair.Create("timestamp", (JsonValue)now),
        ]);
        var entry = await tx.AppendEntryAsync(Entries.UserEntry, conversationId, new TypedEntryDraft { Model = JsonValue.Array(message) })
            .ConfigureAwait(false);
        var input = await tx.CreateSubmissionAsync(new SubmissionCreate
        {
            ConversationId = conversationId, RequestId = draft.RequestId, Type = SubmissionType.Input, Status = SubmissionStatus.Placed, Entry = entry.Id,
        }).ConfigureAwait(false);
        await HarnessTasks.StartRunAsync(tx, conversationId, live, [input.Id]).ConfigureAwait(false);
        return input.Id;
    }

    private static string Wire(SubmissionType type) => type == SubmissionType.Input ? "input" : "write";

    /// <summary>The handle of one admitted submission (upstream <c>SubmissionHandle</c>).</summary>
    private sealed class SubmissionHandle(SubmissionId id, Submissions submissions) : ISubmission
    {
        public SubmissionId Id { get; } = id;

        public Task<SubmissionRecord> StatusAsync(DurableContext context) => submissions.StatusAsync(Id, context);

        public Task<SubmissionRecord> WaitAsync(DurableContext context) => submissions.WaitAsync(Id, context);

        public async Task<SubmissionAbortResult> AbortAsync(DurableContext context)
        {
            var result = await submissions.AbortAsync(Id, context).ConfigureAwait(false);
            if (result == SubmissionAbortResult.NotFound) throw new InvalidOperationException($"Submission {Id} does not exist");
            return result;
        }
    }
}

/// <summary>A submission whose operations first check an invocation and run under its signal (upstream <c>boundConversation</c>'s
/// submission wrapper).</summary>
internal sealed class BoundSubmission(ISubmission inner, InvocationBinding binding) : ISubmission
{
    public SubmissionId Id => inner.Id;

    public Task<SubmissionRecord> StatusAsync(DurableContext context) => Bound(callContext => inner.StatusAsync(callContext), context);

    public Task<SubmissionRecord> WaitAsync(DurableContext context) => Bound(callContext => inner.WaitAsync(callContext), context);

    public Task<SubmissionAbortResult> AbortAsync(DurableContext context) => Bound(callContext => inner.AbortAsync(callContext), context);

    private async Task<T> Bound<T>(Func<DurableContext, Task<T>> operation, DurableContext context)
    {
        binding.Check();
        return await operation(context.WithSignal(binding.Signal)).ConfigureAwait(false);
    }
}
