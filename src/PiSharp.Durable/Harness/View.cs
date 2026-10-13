// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/view.ts (ConversationView, ConversationViews,
// conversationViews, advance, prefixed).
//
// Upstream's ConversationView is a plain JSON object {conversation, entries, docs}; here it is a typed immutable class whose ToJson() is
// that object, and every operation a view frame carries is relative to that JSON shape (["entries"] splices, ["docs", kind, ...] document
// operations), so applying a frame's operations to the previous revision's ToJson() yields the next revision's ToJson(). Revisions share
// unchanged parts by reference, as upstream's applyImmutable does: an untouched Entries array, Docs object or document is the same
// instance in the next revision.
//
// Threading: upstream runs on one event loop. Here publications and attachment run on the Session line, while a close listener and an
// observer's detach (a disposed state, a stopped watch) may run on any thread, so the mount table and each mount's observer list are
// guarded by one lock that is never held while observer code runs.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

namespace PiSharp.Durable;

/// <summary>
/// Structural mount of one conversation's active transcript and built-in documents (upstream <c>ConversationView</c>, spec §9.3). JSON form
/// (<see cref="ToJson"/>): <c>{conversation, entries, docs}</c>.
/// </summary>
public sealed class ConversationView
{
    private JsonObjectValue? json;

    internal ConversationView(ConversationRecord conversation, ImmutableArray<EntryRecord> entries, JsonObjectValue docs)
    {
        Conversation = conversation;
        Entries = entries;
        Docs = docs;
    }

    /// <summary>The conversation record.</summary>
    public ConversationRecord Conversation { get; }

    /// <summary>Raw active entries, as <see cref="ContextView.Entries"/>: the head marker, then the non-head entries from its head.</summary>
    public ImmutableArray<EntryRecord> Entries { get; }

    /// <summary>
    /// Built-in conversation documents keyed by kind (<c>pi.agent</c>, <c>pi.live</c>, <c>pi.inbox</c>, <c>pi.provider</c>,
    /// <c>pi.usage</c>); absent documents are absent. Each value is a JSON object.
    /// </summary>
    public JsonObjectValue Docs { get; }

    /// <summary>The mounted document of <paramref name="kind"/>; null when absent.</summary>
    public JsonObjectValue? Doc(string kind) => Docs.TryGetValue(kind, out var value) ? (JsonObjectValue)value : null;

    /// <summary>The mounted document of <paramref name="token"/>; null when absent or not a mounted kind.</summary>
    public JsonObjectValue? Doc(ConversationDocToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Doc(token.Definition.Kind);
    }

    /// <summary>
    /// The view as upstream's JSON object <c>{conversation, entries, docs}</c>, which the operations of view frames address. Computed once
    /// per revision.
    /// </summary>
    public JsonObjectValue ToJson()
    {
        if (Volatile.Read(ref json) is { } built) return built;
        var value = JsonValue.Object([
            KeyValuePair.Create("conversation", (JsonValue)Conversation.ToJson()),
            KeyValuePair.Create("entries", (JsonValue)JsonValue.Array(Entries.Select(entry => (JsonValue)entry.ToJson()))),
            KeyValuePair.Create("docs", (JsonValue)Docs),
        ]);
        return Interlocked.CompareExchange(ref json, value, null) ?? value;
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson().ToJson();
}

/// <summary>Receives each next revision of a mount, every publication, and the Session's close (upstream <c>ViewObserver</c>).</summary>
internal abstract class ViewObserver
{
    /// <summary>A next revision and the mount's operations that produced it.</summary>
    public virtual void Advance(ConversationView value, ImmutableArray<Op> ops, DurableContext context) { }

    /// <summary>Every publication, after the mount took it; <paramref name="ops"/> are the mount's, possibly none.</summary>
    public virtual void Publication(ConversationView before, ConversationView after, ImmutableArray<Op> ops, CommitPublication publication,
        DurableContext context) { }

    /// <summary>The Session closes.</summary>
    public abstract void CloseSession();
}

/// <summary>A view observer forwarding revisions to a committed state source or watch.</summary>
internal sealed class CommittedViewObserver(ICommittedObserver<ConversationView> target) : ViewObserver
{
    public override void Advance(ConversationView value, ImmutableArray<Op> ops, DurableContext context) => target.Advance(value, ops, context);

    public override void CloseSession() => target.CloseSession();
}

/// <summary>
/// The Harness's conversation view mounts (upstream <c>ConversationViews</c>): at most one per conversation, built on the Session line by its
/// first observer and dropped with its last. Each mount advances from the Session's commit publications, which are durable.
/// </summary>
internal sealed class ConversationViews
{
    private static readonly ConversationDocToken[] Mounted =
        [Agents.AgentDoc, Live.LiveDoc, Inbox.InboxDoc, ProviderIdentity.ProviderDoc, UsageLedger.UsageDoc];
    private static readonly HashSet<string> MountedKinds = new(Mounted.Select(token => token.Definition.Kind), StringComparer.Ordinal);
    private static readonly ImmutableArray<PathSegment> EntriesPath = [PathSegment.Of("entries")];

    private readonly DurableHarness session;
    private readonly IStorage storage;
    private readonly Lock gate = new();
    private readonly Dictionary<ConversationId, Mount> mounts = [];
    // Guarded by gate.
    private bool closed;

    public ConversationViews(DurableHarness session, IStorage storage)
    {
        this.session = session;
        this.storage = storage;
        session.SubscribeCommits((publication, context) =>
        {
            KeyValuePair<ConversationId, Mount>[] current;
            lock (gate) current = [.. mounts];
            foreach (var (id, mount) in current) AdvanceMount(id, mount, publication, context);
        });
        session.SubscribeClose(() =>
        {
            Mount[] current;
            lock (gate)
            {
                closed = true;
                current = [.. mounts.Values];
                mounts.Clear();
            }
            foreach (var mount in current)
                foreach (var observer in mount.Snapshot(gate)) observer.CloseSession();
        });
    }

    /// <summary>The observation scheduler of the Session (upstream <c>queueMicrotask</c>).</summary>
    public ObservationScheduler Schedule => session.Defer;

    /// <summary>The view mounts of <paramref name="harness"/>, for adapters built on them (upstream <c>conversationViews</c>).</summary>
    public static ConversationViews Of(IHarness harness) =>
        harness is DurableHarness durable ? durable.Views : throw new ArgumentException("Not a Harness", nameof(harness));

    /// <summary>A disposable read-only Chord state of the view (upstream <c>state</c>).</summary>
    public async Task<AttachedReplicatedState<ConversationView>> StateAsync(ConversationId id, DurableContext context)
    {
        var (source, detach) = await AttachAsync(id, (value, release, _) =>
        {
            var created = new CommittedStateSource<ConversationView>(value, release, Schedule);
            return Task.FromResult((created, (ViewObserver)new CommittedViewObserver(created)));
        }, context).ConfigureAwait(false);
        try
        {
            return ReplicatedState.Attach<ConversationView>(source);
        }
        catch
        {
            detach();
            throw;
        }
    }

    /// <summary>A serialized exact-frame watch of the view; cancelling <paramref name="context"/> stops it (upstream <c>watch</c>).</summary>
    public async Task<IWatchHandle<ConversationView>> WatchAsync(ConversationId id, DurableContext context)
    {
        var (watch, _) = await AttachAsync(id, (value, release, _) =>
        {
            var created = new CommittedWatch<ConversationView>(value, release, Schedule, json: view => view.ToJson());
            return Task.FromResult((created, (ViewObserver)new CommittedViewObserver(created)));
        }, context).ConfigureAwait(false);
        if (context.IsCancellationRequested)
        {
            watch.Cancel();
            context.ThrowIfCancellationRequested();
        }
        if (context.HasCancellation) watch.ObserveCancellation(context);
        return watch;
    }

    /// <summary>
    /// Registers an observer created from the current revision, atomically on the Session line: it sees every later publication and nothing
    /// earlier (upstream <c>attach</c>). <paramref name="create"/> may read committed Storage, still on the line. The returned detach drops
    /// the observer, and the mount with its last observer.
    /// </summary>
    public Task<(T Result, Action Detach)> AttachAsync<T>(ConversationId id,
        Func<ConversationView, Action, IStorage, Task<(T Result, ViewObserver Observer)>> create, DurableContext context) =>
        session.ReadOnLineAsync(async () =>
        {
            Mount? existing;
            lock (gate) mounts.TryGetValue(id, out existing);
            var mount = existing ?? await BuildAsync(id, context).ConfigureAwait(false);
            ViewObserver? observer = null;
            void Detach()
            {
                lock (gate)
                {
                    if (observer is not null) mount.Observers.Remove(observer);
                    if (mount.Observers.Count == 0 && mounts.TryGetValue(id, out var current) && current == mount) mounts.Remove(id);
                }
            }
            var (result, created) = await create(mount.Value, Detach, storage).ConfigureAwait(false);
            observer = created;
            lock (gate)
            {
                // Close or cancellation may begin while the mount hydrates; register nothing then.
                if (closed) throw HarnessUtil.ClosedError();
                context.ThrowIfCancellationRequested();
                mounts[id] = mount;
                mount.Observers.Add(observer);
            }
            return (result, (Action)Detach);
        });

    private async Task<Mount> BuildAsync(ConversationId id, DurableContext context)
    {
        var conversation = await storage.GetConversationAsync(id, context).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Conversation {id} does not exist");
        var bounds = await ConversationContext.CaptureContextBoundsAsync(storage, id, context).ConfigureAwait(false);
        var entries = await ConversationContext.ActiveEntriesAsync(storage, id, bounds, context).ConfigureAwait(false);
        var docs = new List<KeyValuePair<string, JsonValue>>();
        var incarnations = new Dictionary<string, (DocumentId Id, int Version)>(StringComparer.Ordinal);
        foreach (var token in Mounted)
        {
            if (await session.ConversationDocumentOnLineAsync(token, id, context).ConfigureAwait(false) is not { } loaded) continue;
            docs.Add(KeyValuePair.Create(token.Definition.Kind, (JsonValue)loaded.Value));
            incarnations[token.Definition.Kind] = (loaded.Record.Id, loaded.Version);
        }
        return new Mount(new ConversationView(conversation, entries, JsonObjectValue.From(docs)), incarnations);
    }

    /// <summary>Derives the mount's operations from one publication, applies them, and hands the revision to every observer (upstream
    /// <c>advance</c>). Runs on the Session line.</summary>
    private void AdvanceMount(ConversationId id, Mount mount, CommitPublication publication, DurableContext context)
    {
        var docOps = new List<Op>();
        var entryOps = new List<Op>();
        var entries = mount.Value.Entries;
        // Entry writes are published in ID order.
        foreach (var change in publication.Changes)
        {
            if (change is EntryWrite { Value: var entry } && entry.ConversationId == id)
            {
                JsonValue value = entry.ToJson();
                if (entry.Head is not { } target)
                {
                    entryOps.Add(new SpliceOp(EntriesPath, entries.Length, 0, [value]));
                    entries = entries.Add(entry);
                    continue;
                }
                // A head marker keeps the non-head entries from its head, which are always a suffix, and goes in front.
                var kept = entries.Length;
                for (var index = 0; index < entries.Length; index++)
                {
                    if (entries[index].Head is null && entries[index].Id.Value >= target.Value)
                    {
                        kept = index;
                        break;
                    }
                }
                entryOps.Add(new SpliceOp(EntriesPath, 0, kept, [value]));
                entries = [entry, .. entries.AsSpan()[kept..]];
                continue;
            }
            if (change is not DocumentCommitChange document || document.ConversationId != id) continue;
            var kind = document.Record.Kind;
            if (!MountedKinds.Contains(kind) || document.Record.Key is not null) continue;
            ImmutableArray<PathSegment> path = [PathSegment.Of("docs"), PathSegment.Of(kind)];
            var isMounted = mount.Docs.TryGetValue(kind, out var mounted);
            if (document.Value is null)
            {
                if (!isMounted || mounted.Id != document.Record.Id) continue;
                mount.Docs.Remove(kind);
                docOps.Add(new DeleteOp(path));
            }
            else if (isMounted && mounted.Id == document.Record.Id && mounted.Version == document.Version)
            {
                foreach (var op in document.Ops) docOps.Add(Prefixed(op, path));
            }
            else
            {
                mount.Docs[kind] = (document.Record.Id, document.Version!.Value);
                docOps.Add(new SetOp(path, document.Value));
            }
        }
        var before = mount.Value;
        ImmutableArray<Op> ops = [.. docOps, .. entryOps];
        var frameContext = context.WithoutCancellation();
        if (ops.Length > 0)
        {
            // Document operations address ["docs", kind, ...]; Docs is the "docs" object itself.
            var docs = docOps.Count == 0 ? before.Docs : (JsonObjectValue)DeltaApplyTrusted.Apply(before.Docs, docOps.Select(Unprefixed));
            mount.Value = new ConversationView(before.Conversation, entries, docs);
            foreach (var observer in mount.Snapshot(gate)) observer.Advance(mount.Value, ops, frameContext);
        }
        foreach (var observer in mount.Snapshot(gate)) observer.Publication(before, mount.Value, ops, publication, frameContext);
    }

    /// <summary><paramref name="op"/> moved under <paramref name="prefix"/>; a root replacement becomes a set of the prefix (upstream
    /// <c>prefixed</c>).</summary>
    private static Op Prefixed(Op op, ImmutableArray<PathSegment> prefix) => op switch
    {
        ReplaceOp replace => new SetOp(prefix, replace.Value),
        PathOp path => path with { Path = [.. prefix, .. path.Path] },
        _ => throw new ArgumentException($"Unknown operation {op}", nameof(op)),
    };

    /// <summary>A document operation relative to the <c>docs</c> object: its path without the leading <c>"docs"</c>.</summary>
    private static Op Unprefixed(Op op) => op is PathOp path ? path with { Path = path.Path.RemoveAt(0) } :
        throw new ArgumentException($"A view operation never replaces the root: {op}", nameof(op));

    /// <summary>One conversation's mount: its current revision, the document incarnations it shows, and its observers.</summary>
    private sealed class Mount(ConversationView value, Dictionary<string, (DocumentId Id, int Version)> docs)
    {
        /// <summary>Written on the Session line only.</summary>
        public ConversationView Value { get; set; } = value;
        /// <summary>Mounted incarnation and definition version per kind; another incarnation or version is set whole. Session line only.</summary>
        public Dictionary<string, (DocumentId Id, int Version)> Docs { get; } = docs;
        /// <summary>In registration order; guarded by the views' lock.</summary>
        public List<ViewObserver> Observers { get; } = [];

        public ViewObserver[] Snapshot(Lock gate)
        {
            lock (gate) return [.. Observers];
        }
    }
}
