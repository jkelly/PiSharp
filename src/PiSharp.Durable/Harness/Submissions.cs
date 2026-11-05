// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/submissions.ts. Inbox items keep a draft's content or
// entry as plain JSON (upstream's copyJson with omitUndefinedProperties: a C# draft has no undefined fields). A settled record is the
// SubmissionRecord whose status is done or unanswered (upstream SettledSubmissionRecord). Upstream's checkInputEntry guards untyped callers;
// an InputEntry is typed, so only its kind is checked.
//
// Pi v1.5.0 sync (area deferred-commits, 6c2fd73b0): the inbox observes the Session's commits as memory adopts them
// (Session.FollowCommits) and skips a held one, which holds no submission.
//
// Pi v1.5.0 sync (area telemetry, 2ec55d808): a public admission is a pi.durable.submit span: an input's is a root of the Harness telemetry
// linked to the caller's span, a write's a child of it. The commit runs under the span, so its entry and submission events land there, and
// admission stores the span's carrier on the record and links a steer to the running generation's trace. An error records only the error
// status, as its text may carry caller content. Admission returns its result with the ID (upstream Admission). Pi v1.5.0 sync
// (area vocabulary, 5f312dac4): the internal observeCommits is followCommits (Session.FollowCommits). The citation stays on v1.2.0 until
// the file's other areas land.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal;
using PiSharp.Telemetry;

namespace PiSharp.Durable;

/// <summary>What admission did (upstream <c>Admission</c>): the submission, and whether it queued, placed or settled at once
/// (<c>queued</c>, <c>placed</c> or <c>done</c>; a known request ID's settled submission, and a stale write, are done).</summary>
internal readonly record struct Admission(SubmissionId Id, string Status);

/// <summary>Admission, waits and withdrawal of the durable submissions of one Harness (upstream <c>Submissions</c>).</summary>
internal sealed class Submissions
{
    private readonly ITelemetryContext telemetry;
    private readonly Session session;
    private readonly IStorage storage;
    private readonly Func<double> now;
    /// <summary>Read at each admission, on the Session line.</summary>
    private readonly Func<Settings> queueModes;
    /// <summary>Enables task scheduling; submitting or waiting asks for progress.</summary>
    private readonly Action resume;
    private readonly Waiters<SubmissionId, SubmissionRecord> waiters = new();
    private volatile bool closed;

    public Submissions(Session session, IStorage storage, Func<double> now, Func<Settings> queueModes, Action resume,
        ITelemetryContext? telemetry = null)
    {
        this.telemetry = telemetry ?? NoopTelemetry.Context;
        this.session = session;
        this.storage = storage;
        this.now = now;
        this.queueModes = queueModes;
        this.resume = resume;
        session.FollowCommits((publication, _) => Observe(publication));
        session.SubscribeClose(() =>
        {
            closed = true;
            waiters.RejectAll(HarnessUtil.ClosedError(session));
        });
    }

    /// <summary>Admits a submission in one commit; see <see cref="AdmitSubmissionAsync"/>.</summary>
    public async Task<ISubmission> SubmitAsync(ConversationId conversationId, SubmissionDraft draft, DurableContext context)
    {
        ArgumentNullException.ThrowIfNull(draft);
        resume();
        var input = draft.Type == SubmissionType.Input;
        var parent = input ? telemetry : context.Telemetry;
        return await TelemetrySchema.CreateTypedSpanStarter(parent, DurableTelemetry.Schema).StartSpanAsync<ISubmission>(
            DurableTelemetry.SubmitSpanName,
            new SpanAttributes { ["submission.type"] = input ? "input" : "write", ["conversation.id"] = conversationId.Value },
            async (span, _) =>
            {
                if (input) span.AddLink(new SpanLink(context.Telemetry));
                try
                {
                    var admission = await session.CommitWithAsync(
                        tx => AdmitSubmissionAsync(tx, conversationId, draft, now(), queueModes(), span.Span),
                        context.WithTelemetry(span.Span)).ConfigureAwait(false);
                    span.SetAttributes(new SpanAttributes { ["submission.id"] = admission.Id.Value, ["result"] = admission.Status });
                    return new SubmissionHandle(admission.Id, this);
                }
                catch
                {
                    // Error text may contain caller content: only the status is recorded.
                    span.SetStatus(SpanStatus.Failed());
                    throw;
                }
            }).ConfigureAwait(false);
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
            if (closed) throw HarnessUtil.ClosedError(session);
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

    private void Observe(ObservedCommit publication)
    {
        // A commit held in memory changes documents only.
        if (publication.IsMemory) return;
        foreach (var change in publication.Changes)
            if (change is SubmissionWrite { Value: var record } && IsSettled(record)) waiters.Resolve(record.Id, record);
    }

    private static bool IsSettled(SubmissionRecord record) => record.Status is SubmissionStatus.Done or SubmissionStatus.Unanswered;

    /// <summary>Rejects an input entry whose kind is empty or starts with <c>pi.</c> (upstream <c>checkInputEntry</c>).</summary>
    private static void CheckInputEntry(InputEntry entry)
    {
        var kind = entry.Kind;
        if (string.IsNullOrEmpty(kind) || kind.StartsWith("pi.", StringComparison.Ordinal))
            throw new ArgumentException($"An input entry's kind must be a non-empty string not starting with \"pi.\", got {kind ?? "undefined"}");
    }

    /// <summary>
    /// Admits a submission inside a commit (upstream <c>admitSubmission</c>, spec §6); <c>Conversation.SubmitAsync()</c> and
    /// conversation-owned compactions share it. A known request ID returns its existing submission without writing. A display-only write
    /// (only kind and data) is appended and settled <c>done</c> at once, busy or not, as it changes nothing the model sees; only behind a
    /// queued reset it queues, so it lands after the reset instead of before it in the context the reset ends. A busy conversation queues
    /// anything else in <c>pi.inbox</c>, or rejects <c>whenBusy: "reject"</c> input with <see cref="ConversationBusyException"/>. An idle
    /// conversation with queued items queues it behind them and runs a final boundary. Otherwise idle input places its entry
    /// (<c>pi.user</c>, or the input's own) and starts a run, and an idle write appends its entry and settles <c>done</c>, or <c>stale</c>
    /// when its head reaches before the active range.
    /// </summary>
    internal static async Task<Admission> AdmitSubmissionAsync(ITx tx, ConversationId conversationId, SubmissionDraft draft, double now,
        Settings queueModes, ITelemetrySpan? span = null)
    {
        if (draft.Type == SubmissionType.Input && draft.InputEntry is { } inputEntry) CheckInputEntry(inputEntry);
        if (draft.RequestId is { } requestId)
        {
            var existing = await tx.GetSubmissionByRequestAsync(conversationId, requestId).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Type != draft.Type)
                    throw new InvalidOperationException($"Request {requestId} already identifies a submission of type {Wire(existing.Type)}");
                return new Admission(existing.Id, existing.Status == SubmissionStatus.Unanswered ? "done" : WireName.Of(existing.Status));
            }
        }
        var trace = span?.Propagation?.Inject(span);
        if (draft.Type == SubmissionType.Write && Inbox.IsDisplayOnly(draft.Entry!))
        {
            var queuedItems = (await tx.DocAsync(Inbox.InboxDoc, conversationId).ConfigureAwait(false)).GetArray("items");
            if (!Inbox.HasQueuedReset(Enumerable.Range(0, queuedItems.Count).Select(index => (JsonObjectValue)queuedItems.Get(index))))
            {
                var appended = await tx.AppendEntryAsync(conversationId, draft.Entry!).ConfigureAwait(false);
                return new Admission((await tx.CreateSubmissionAsync(new SubmissionCreate
                {
                    ConversationId = conversationId, RequestId = draft.RequestId, Trace = trace, Type = SubmissionType.Write,
                    Status = SubmissionStatus.Done, Entry = appended.Id,
                }).ConfigureAwait(false)).Id, "done");
            }
        }
        var live = await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false);
        var busy = live.ContainsKey("run");
        if (draft.Type == SubmissionType.Input && draft.WhenBusy == WhenBusy.Steer && HarnessTasks.RunTaskId(live) is { } runTask &&
            span?.Propagation is { } propagation)
        {
            var run = await tx.GetTaskAsync(new TaskId(runTask)).ConfigureAwait(false);
            if (run?.Trace is { } runTrace) span.AddLink(new SpanLink(propagation.Extract(runTrace)));
        }
        if (busy && draft.Type == SubmissionType.Input && draft.WhenBusy == WhenBusy.Reject) throw new ConversationBusyException(conversationId);
        // A boundary reads the table, so it is prepared before the first table write; a busy one needs none.
        var boundary = busy ? null : await Inbox.PrepareBoundaryAsync(tx, conversationId, queueModes).ConfigureAwait(false);
        if (boundary is null || boundary.Inbox.GetArray("items").Count > 0)
        {
            var queued = await tx.CreateSubmissionAsync(new SubmissionCreate
            {
                ConversationId = conversationId, RequestId = draft.RequestId, Trace = trace, Type = draft.Type, Status = SubmissionStatus.Queued,
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
                    .. draft.InputEntry is { } queuedEntry ? [KeyValuePair.Create("entry", (JsonValue)Inbox.PickInputEntry(queuedEntry))] :
                        Array.Empty<KeyValuePair<string, JsonValue>>(),
                ]));
            if (boundary is null) return new Admission(queued.Id, "queued");
            var (users, _) = await Inbox.ApplyBoundaryAsync(tx, boundary, true, now).ConfigureAwait(false);
            if (!users.IsEmpty) await HarnessTasks.StartRunAsync(tx, conversationId, live, users).ConfigureAwait(false);
            return new Admission(queued.Id,
                draft.Type == SubmissionType.Write ? "done" : !users.IsEmpty && users[^1] == queued.Id ? "placed" : "queued");
        }
        if (draft.Type == SubmissionType.Write)
        {
            var entryDraft = draft.Entry!;
            if (Inbox.IsStale(boundary, entryDraft))
            {
                return new Admission((await tx.CreateSubmissionAsync(new SubmissionCreate
                {
                    ConversationId = conversationId, RequestId = draft.RequestId, Trace = trace, Type = SubmissionType.Write,
                    Status = SubmissionStatus.Unanswered, Reason = "stale",
                }).ConfigureAwait(false)).Id, "done");
            }
            var written = await tx.AppendEntryAsync(conversationId, entryDraft).ConfigureAwait(false);
            return new Admission((await tx.CreateSubmissionAsync(new SubmissionCreate
            {
                ConversationId = conversationId, RequestId = draft.RequestId, Trace = trace, Type = SubmissionType.Write,
                Status = SubmissionStatus.Done, Entry = written.Id,
            }).ConfigureAwait(false)).Id, "done");
        }
        var entry = await Inbox.AppendInputAsync(tx, conversationId, draft.Content!, draft.InputEntry, now).ConfigureAwait(false);
        var input = await tx.CreateSubmissionAsync(new SubmissionCreate
        {
            ConversationId = conversationId, RequestId = draft.RequestId, Trace = trace, Type = SubmissionType.Input,
            Status = SubmissionStatus.Placed, Entry = entry.Id,
        }).ConfigureAwait(false);
        await HarnessTasks.StartRunAsync(tx, conversationId, live, [input.Id]).ConfigureAwait(false);
        return new Admission(input.Id, "placed");
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
