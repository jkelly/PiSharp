// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/context.ts. Messages are pi-ai wire JSON. Records and
// JSON values are immutable here, so upstream's freezeJson has no counterpart; a kept range is shared as is, and every returned view has
// its own arrays because ImmutableArray values are never shared mutably. Pi v1.5.0 sync (area harness-options): the harness baseline
// leads the context past assistant messages (upstream 5713c9507, #10904); upstream's `system !== baseline` is reference equality of the
// stored message, which Contribute passes through unchanged and an edit's replacement never is. Area view (f3255de76): the windowed view
// no longer reads the active transcript. Area events (f3255de76): activeEntries is gone with watchEvents, as upstream removed it;
// AppendActive stays for PiSharp's readers of the active transcript (Hosting's event projection, which reads its first one from the
// context, and the durable TUI). Area group-commit-scheduler (e5c6182ba): the bounds are captured on the line through the line's Storage
// (ReadOnLineAsync's argument), which waits for an unpublished commit of the conversation's entries. Area publication-caches (e5c6182ba):
// the newest entry and head marker come from the Session (session.latestEntryId, latestHeadMarker: its ConversationTails), so the current
// bounds usually cost no Storage read; a cutoff already in the previous range skips its visibility read (`visible`); a range is `current`
// when it reached the conversation's newest entry and no entry was committed while its scan ran off the line (IsKnownTail, upstream
// knownLatestEntryId), and only a current range advances with later commits (AdvanceRange; RangeEntry serves one entry of a range).
// PiSharp keeps a CaptureContextBoundsAsync over a Storage alone for a reader without the Session (Hosting's agent events), and
// ReadContextAsync's lease on the line (Session.HoldStorageOpen) for the off-line derive.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Head marker and newest visible entry that fix one committed context range (upstream <c>ContextBounds</c>).</summary>
/// <param name="Head">The newest visible entry with a head at or below the tail.</param>
/// <param name="Tail">The newest visible entry, or the cutoff.</param>
internal sealed record ContextBounds(EntryRecord? Head, EntryId Tail);

/// <summary>
/// One context read (upstream <c>ContextRange</c>): the visible entries it scanned, from the head marker's head or transcript start through
/// the tail, oldest first, and the view derived from them. Entries at or below the tail never change, so a later read with the same head
/// marker extends both. <see cref="Current"/> tells whether the range reached the conversation's newest entry when it was read, so the
/// entries of later commits extend it (<see cref="ConversationContext.AdvanceRange"/>); a range cut off at an earlier entry is not.
/// </summary>
internal sealed record ContextRange(ContextBounds Bounds, ImmutableArray<EntryRecord> Entries, ContextView View, ImmutableHashSet<EntryId> Edited,
    ImmutableArray<JsonValue> Settled, ImmutableArray<JsonValue> Open, ContextBaseline Baseline, bool Current);

/// <summary>
/// The baseline's system message of a range (upstream <c>Baseline</c>; see <c>FindBaseline</c>): <see cref="Message"/> once found, null in a
/// settled baseline when the range cannot have one (upstream <c>null</c>), or not <see cref="Settled"/> while a later entry may be one
/// (upstream <c>undefined</c>).
/// </summary>
internal readonly record struct ContextBaseline(bool Settled, JsonValue? Message)
{
    /// <summary>A later entry may still be the baseline (upstream <c>undefined</c>).</summary>
    public static ContextBaseline Pending => default;

    /// <summary>The range cannot have a baseline (upstream <c>null</c>).</summary>
    public static ContextBaseline None => new(true, null);
}

/// <summary>Context reads and model context derivation (upstream <c>context.ts</c>).</summary>
internal static class ConversationContext
{
    private const int ScanPageSize = 256;
    private static readonly HashSet<string> ExcludedStopReasons = new(StringComparer.Ordinal) { "aborted", "error", "deferred" };
    private const string MissingResultText = "Tool result unavailable: history ends before this call completed.";

    /// <summary>
    /// Captures the bounds of the current context, or of the context cut off at the visible entry <paramref name="at"/> (upstream
    /// <c>captureContextBounds</c>). The newest entry and head marker are known from the Session's commits, so the current bounds usually
    /// cost no Storage read. Run this on the Session line, with the line's Storage; entries at or below the tail are immutable, so
    /// <see cref="DeriveContextAsync"/> can then scan them off the line. <paramref name="visible"/> is set when <paramref name="at"/> is known
    /// to be visible, as an entry of a range of the conversation is.
    /// </summary>
    public static async Task<ContextBounds?> CaptureContextBoundsAsync(Session session, IStorage storage, ConversationId conversationId,
        DurableContext context, EntryId? at = null, bool visible = false)
    {
        EntryId tail;
        if (at is not { } cutoff)
        {
            if (await session.LatestEntryIdAsync(conversationId, context).ConfigureAwait(false) is not { } newest) return null;
            tail = newest;
        }
        else
        {
            if (!visible && await storage.GetEntryAsync(conversationId, cutoff, context).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"Entry {cutoff} is not visible from conversation {conversationId}");
            tail = cutoff;
        }
        return new ContextBounds(await session.LatestHeadMarkerAsync(conversationId, tail, context).ConfigureAwait(false), tail);
    }

    /// <summary>
    /// PiSharp-only: <see cref="CaptureContextBoundsAsync(Session, IStorage, ConversationId, DurableContext, EntryId?, bool)"/> with two
    /// O(1) reads of <paramref name="storage"/>, for a reader on the line without the Session (Hosting's agent events, which reads through
    /// its view's line Storage); upstream v1.5.0 reads through the Session only.
    /// </summary>
    public static async Task<ContextBounds?> CaptureContextBoundsAsync(IStorage storage, ConversationId conversationId, DurableContext context,
        EntryId? at = null)
    {
        EntryId tail;
        if (at is not { } cutoff)
        {
            var newest = await storage.ScanEntriesAsync(new EntryQuery(conversationId), 1, null, context).ConfigureAwait(false);
            if (newest.Items.IsEmpty) return null;
            tail = newest.Items[0].Id;
        }
        else
        {
            if (await storage.GetEntryAsync(conversationId, cutoff, context).ConfigureAwait(false) is null)
                throw new InvalidOperationException($"Entry {cutoff} is not visible from conversation {conversationId}");
            tail = cutoff;
        }
        return new ContextBounds(await storage.FindLatestHeadMarkerAsync(conversationId, tail, context).ConfigureAwait(false), tail);
    }

    /// <summary>
    /// <see cref="ReadContextAsync"/> that reuses <paramref name="previous"/>, an earlier range of the same conversation: with the same head
    /// marker, only entries after its tail are scanned (upstream <c>readContextFrom</c>). Returns the view and the range to pass to the next
    /// read. A caller that keeps the range for <see cref="AdvanceRange"/> checks <see cref="ContextRange.Current"/> again where it installs
    /// it, under the lock its publications advance ranges under: a commit can be published between this function's return and the caller's
    /// continuation.
    /// </summary>
    public static async Task<(ContextView View, ContextRange? Range)> ReadContextFromAsync(Session session, IStorage storage,
        ConversationId conversationId, DurableContext context, EntryId? at, ContextRange? previous)
    {
        var visible = at is { } cutoff && previous is not null && RangeEntry(previous, cutoff) is not null;
        var bounds = await session.ReadOnLineAsync(line => CaptureContextBoundsAsync(session, line, conversationId, context, at, visible))
            .ConfigureAwait(false);
        if (bounds is null) return (ContextView.Empty, null);
        var current = at is null;
        ContextRange range;
        if (previous is null || previous.Bounds.Head?.Id != bounds.Head?.Id)
            range = DeriveRange(bounds, await ScanRangeAsync(storage, conversationId, RangeQuery(conversationId, bounds), context).ConfigureAwait(false),
                current);
        else if (bounds.Tail == previous.Bounds.Tail)
            range = previous;
        else if (bounds.Tail.Value < previous.Bounds.Tail.Value)
            range = DeriveRange(bounds, [.. previous.Entries.Where(entry => entry.Id.Value <= bounds.Tail.Value)], false);
        else
        {
            var query = new EntryQuery(conversationId) { MinEntryId = new EntryId(previous.Bounds.Tail.Value + 1), MaxEntryId = bounds.Tail };
            var added = await ScanRangeAsync(storage, conversationId, query, context).ConfigureAwait(false);
            range = ExtendRange(previous, bounds, added, current);
        }
        // Entries committed while the scan ran off the line are not in the range: it is current only if none was.
        if (range.Current && !IsKnownTail(session, conversationId, range.Bounds.Tail)) range = range with { Current = false };
        return (range.View, range);
    }

    /// <summary>Whether <paramref name="tail"/> is the newest entry of <paramref name="conversationId"/> the Session knows right now (upstream
    /// <c>session.knownLatestEntryId(conversationId) === range.bounds.tail</c>); false when it knows none.</summary>
    internal static bool IsKnownTail(Session session, ConversationId conversationId, EntryId tail) =>
        session.KnownLatestEntryId(conversationId, out var newest) && newest == tail;

    /// <summary>Committed context of one conversation: bounds captured on the Session line, entries derived off it (upstream
    /// <c>readContext</c>).</summary>
    public static async Task<ContextView> ReadContextAsync(Session session, IStorage storage, ConversationId conversationId, DurableContext context,
        EntryId? at = null)
    {
        // The lease admits the off-line derive's first Storage call when close follows the bounds job on the line, as upstream's single
        // thread starts it before the closing job runs (Session.HoldStorageOpen); a later page is refused once close has begun.
        var (bounds, lease) = await session.ReadOnLineAsync(async line =>
            (await CaptureContextBoundsAsync(session, line, conversationId, context, at).ConfigureAwait(false), session.HoldStorageOpen()))
            .ConfigureAwait(false);
        using (lease) return await DeriveContextAsync(lease.Storage, conversationId, bounds, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Derives the active transcript and model context of one conversation within captured bounds (upstream <c>deriveContext</c>). H = newest
    /// visible head marker; the range runs from <c>H.head</c> (or transcript start) through the tail. Per target, the newest edit in the
    /// range wins. Context entries are H followed by the range's non-head entries.
    /// </summary>
    public static async Task<ContextView> DeriveContextAsync(IStorage storage, ConversationId conversationId, ContextBounds? bounds, DurableContext context)
    {
        if (bounds is null) return ContextView.Empty;
        return DeriveRange(bounds, await ScanRangeAsync(storage, conversationId, RangeQuery(conversationId, bounds), context).ConfigureAwait(false),
            false).View;
    }

    /// <summary>Entry <paramref name="id"/> of <paramref name="range"/>, by binary search (upstream <c>rangeEntry</c>): a range holds its
    /// entries in ID order, every one visible from its conversation.</summary>
    internal static EntryRecord? RangeEntry(ContextRange range, EntryId id)
    {
        var entries = range.Entries;
        int low = 0, high = entries.Length - 1;
        while (low <= high)
        {
            var middle = (int)((uint)(low + high) >> 1);
            var entry = entries[middle];
            if (entry.Id == id) return entry;
            if (entry.Id.Value < id.Value) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    /// <summary>
    /// <paramref name="range"/> with the entries one commit appended to its conversation, in ID order, without reading Storage (upstream
    /// <c>advanceRange</c>): the conversation's own new entries are its newest, and a new head marker's head never lies before the range's
    /// start. A range that is not <see cref="ContextRange.Current"/> stays as it is; the next read extends it from Storage. Null drops the
    /// range.
    /// </summary>
    internal static ContextRange? AdvanceRange(ContextRange range, IReadOnlyList<EntryRecord> appended)
    {
        if (!range.Current || appended.Count == 0) return range;
        ImmutableArray<EntryRecord> added = [.. appended];
        var head = range.Bounds.Head;
        foreach (var entry in added) if (entry.Head is not null) head = entry;
        var bounds = new ContextBounds(head, added[^1].Id);
        if (head?.Id == range.Bounds.Head?.Id) return ExtendRange(range, bounds, added, true);
        var start = head!.Head!.Value;
        // Heads only move forward: compaction keeps a later entry, a reset starts at itself, an older head is stale.
        if (range.Bounds.Head is { Head: { } previousStart } && start.Value < previousStart.Value) return null;
        return DeriveRange(bounds, [.. range.Entries.Concat(added).Where(entry => entry.Id.Value >= start.Value)], true);
    }

    /// <summary>Derives the context view from the entries scanned within <paramref name="bounds"/>.</summary>
    private static ContextRange DeriveRange(ContextBounds bounds, ImmutableArray<EntryRecord> entries, bool current)
    {
        var edits = new Dictionary<EntryId, ContextEdit>();
        // Edits of every entry in the range count, including older head markers that SelectActive drops.
        foreach (var entry in entries) foreach (var edit in entry.Edits ?? []) edits[edit.Target] = edit;
        var active = SelectActive(bounds.Head, entries);
        var contributions = active.Select(entry => Contribute(entry, edits.GetValueOrDefault(entry.Id))).ToImmutableArray();
        var (settled, open) = Settle([], [.. contributions.SelectMany(messages => messages)]);
        var baseline = FindBaseline(bounds.Head, active);
        var messages = LeadWithSystem([.. settled, .. OrderToolResults(open)], baseline);
        var view = new ContextView { Head = bounds.Head, Entries = active, Contributions = contributions, Messages = messages };
        return new ContextRange(bounds, entries, view, [.. edits.Keys], settled, open, baseline, current);
    }

    /// <summary>
    /// <paramref name="previous"/> extended by the entries <paramref name="added"/> after its tail under the same head marker: only their
    /// contributions and the open messages are derived. An added edit can change an earlier entry, so it derives the whole range again.
    /// </summary>
    private static ContextRange ExtendRange(ContextRange previous, ContextBounds bounds, ImmutableArray<EntryRecord> added, bool current)
    {
        var entries = previous.Entries.AddRange(added);
        if (added.Any(entry => entry.Edits is not null || entry.Head is not null || previous.Edited.Contains(entry.Id)))
            return DeriveRange(bounds, entries, current);
        var contributions = added.Select(entry => Contribute(entry, null)).ToImmutableArray();
        var (settled, open) = Settle(previous.Settled, [.. previous.Open, .. contributions.SelectMany(messages => messages)]);
        var baseline = previous.Baseline.Settled ? previous.Baseline : FindBaseline(bounds.Head, added);
        var view = new ContextView
        {
            Head = bounds.Head,
            Entries = previous.View.Entries.AddRange(added),
            Contributions = previous.View.Contributions.AddRange(contributions),
            Messages = LeadWithSystem([.. settled, .. OrderToolResults(open)], baseline),
        };
        return new ContextRange(bounds, entries, view, previous.Edited, settled, open, baseline, current);
    }

    /// <summary>
    /// The baseline among <paramref name="entries"/>, oldest first (upstream <c>findBaseline</c>): the system message of the first
    /// <c>pi.system</c> entry after the head marker, or of the transcript without one, when generation wrote it with every section and tool in
    /// effect. After a head marker it always does, and omits the retained <c>pi.system</c> entries. Without one it does when it saw no system
    /// message before, which holds unless an earlier entry carries one, even if an edit omits it later. An edit that targets the baseline
    /// contributes other messages, or none, so none leads; a later entry does not take its place.
    /// </summary>
    private static ContextBaseline FindBaseline(EntryRecord? head, IEnumerable<EntryRecord> entries)
    {
        foreach (var entry in entries)
        {
            if (head is not null && entry.Id.Value <= head.Id.Value) continue;
            if (Entries.SystemEntry.Is(entry))
                return new ContextBaseline(true, (entry.Model ?? JsonArrayValue.Empty).FirstOrDefault(message => Role(message) == "system"));
            if (head is null && CarriesSystemMessage(entry)) return ContextBaseline.None;
        }
        return ContextBaseline.Pending;
    }

    /// <summary>Whether an entry's own messages, or the replacement messages of its edits, include a system message (upstream
    /// <c>carriesSystemMessage</c>).</summary>
    private static bool CarriesSystemMessage(EntryRecord entry) =>
        (entry.Model?.Any(message => Role(message) == "system") ?? false) ||
        (entry.Edits?.Any(edit => edit.Action == ContextEditAction.Replace && edit.Messages!.Any(message => Role(message) == "system")) ?? false);

    /// <summary>
    /// Moves the first system message to the front when only user messages precede it, or when it is the baseline's (upstream
    /// <c>leadWithSystem</c>). A run's input is committed before generation renders the system prompt, so a transcript starts with the input
    /// followed by the baseline, and the range after a compaction with the kept history, assistant messages included. Providers treat only a
    /// leading system message as the initial prompt and tool set; without it, a later tool change rewrites the request's tool list and
    /// invalidates the whole prompt cache. Only the first system message moves, so replaying system messages in order gives the same sections
    /// and tools. Any other system message, before an assistant message, stays where it was committed: moving it would change what it means
    /// to the model.
    /// </summary>
    private static ImmutableArray<JsonValue> LeadWithSystem(ImmutableArray<JsonValue> messages, ContextBaseline baseline)
    {
        var index = -1;
        for (var at = 0; at < messages.Length; at++)
            if (Role(messages[at]) == "system") { index = at; break; }
        if (index <= 0) return messages;
        var system = messages[index];
        if (!ReferenceEquals(system, baseline.Message) && messages.Take(index).Any(message => Role(message) != "user")) return messages;
        return [system, .. messages.Take(index), .. messages.Skip(index + 1)];
    }

    /// <summary>One active entry's model messages after its edit and excluded stop reasons, before tool result ordering.</summary>
    private static ImmutableArray<JsonValue> Contribute(EntryRecord entry, ContextEdit? edit)
    {
        if (edit?.Action == ContextEditAction.Omit) return [];
        var contributed = edit?.Action == ContextEditAction.Replace ? edit.Messages! : entry.Model ?? JsonArrayValue.Empty;
        return [.. contributed.Where(message => Role(message) != "assistant" || !ExcludedStopReasons.Contains(Text(message, "stopReason") ?? ""))];
    }

    /// <summary>
    /// Moves the ordered messages before the last assistant message of <paramref name="open"/> to <paramref name="settled"/> (upstream
    /// <c>settle</c>). Ordering a sequence equals the concatenation over its parts when each later part starts with an assistant message.
    /// </summary>
    private static (ImmutableArray<JsonValue> Settled, ImmutableArray<JsonValue> Open) Settle(ImmutableArray<JsonValue> settled,
        ImmutableArray<JsonValue> open)
    {
        var last = -1;
        for (var index = open.Length - 1; index >= 0; index--)
            if (Role(open[index]) == "assistant") { last = index; break; }
        if (last <= 0) return (settled, open);
        return ([.. settled, .. OrderToolResults([.. open.Take(last)])], [.. open.Skip(last)]);
    }

    /// <summary>The visible range of <paramref name="bounds"/>: from the head marker's head, or transcript start, through the tail.</summary>
    private static EntryQuery RangeQuery(ConversationId conversationId, ContextBounds bounds) =>
        bounds.Head is { Head: { } head }
            ? new EntryQuery(conversationId) { MinEntryId = head, MaxEntryId = bounds.Tail }
            : new EntryQuery(conversationId) { MaxEntryId = bounds.Tail };

    /// <summary>Visible entries within <paramref name="range"/>, oldest first.</summary>
    private static async Task<ImmutableArray<EntryRecord>> ScanRangeAsync(IStorage storage, ConversationId conversationId, EntryQuery range,
        DurableContext context)
    {
        var entries = new List<EntryRecord>();
        JsonObjectValue? cursor = null;
        do
        {
            var page = await storage.ScanEntriesAsync(range with { ConversationId = conversationId }, ScanPageSize, cursor, context).ConfigureAwait(false);
            entries.AddRange(page.Items);
            cursor = page.Next;
        } while (cursor is not null);
        entries.Reverse();
        return [.. entries];
    }

    /// <summary>
    /// <paramref name="active"/>, raw active entries, after <paramref name="entry"/> is appended: a non-head entry goes last, and a head
    /// marker goes in front of the non-head entries from its head, which are always a suffix (upstream v1.2.0 view.ts's head-marker cut).
    /// A head that targets an entry before the ones <paramref name="active"/> holds keeps every non-head entry it holds; the entries it
    /// never held stay absent.
    /// </summary>
    internal static ImmutableArray<EntryRecord> AppendActive(ImmutableArray<EntryRecord> active, EntryRecord entry)
    {
        if (entry.Head is not { } target) return active.Add(entry);
        var kept = active.Length;
        for (var index = 0; index < active.Length; index++)
        {
            if (active[index].Head is null && active[index].Id.Value >= target.Value)
            {
                kept = index;
                break;
            }
        }
        return [entry, .. active.AsSpan()[kept..]];
    }

    /// <summary>The head marker followed by the range's non-head entries, or the whole range without a marker.</summary>
    private static ImmutableArray<EntryRecord> SelectActive(EntryRecord? head, ImmutableArray<EntryRecord> range) =>
        head is null ? range : [head, .. range.Where(entry => entry.Head is null)];

    /// <summary>
    /// Places each assistant's tool results directly after it in call order (upstream <c>orderToolResults</c>). Results are taken from the
    /// messages before the next assistant; a missing result is synthesized and unmatched results are dropped.
    /// </summary>
    public static ImmutableArray<JsonValue> OrderToolResults(ImmutableArray<JsonValue> messages)
    {
        var ordered = ImmutableArray.CreateBuilder<JsonValue>();
        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            if (Role(message) == "toolResult") continue;
            ordered.Add(message);
            if (Role(message) != "assistant") continue;
            var calls = ((message as JsonObjectValue)?.GetValueOrDefault("content") as JsonArrayValue ?? JsonArrayValue.Empty)
                .Where(content => Text(content, "type") == "toolCall").ToList();
            if (calls.Count == 0) continue;
            var results = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var next = index + 1; next < messages.Length && Role(messages[next]) != "assistant"; next++)
            {
                var candidate = messages[next];
                if (Role(candidate) == "toolResult" && Text(candidate, "toolCallId") is { } callId && !results.ContainsKey(callId)) results[callId] = next;
            }
            foreach (var call in calls)
            {
                var id = Text(call, "id") ?? "";
                ordered.Add(results.TryGetValue(id, out var resultIndex)
                    ? messages[resultIndex]
                    : MissingResult(call, (message as JsonObjectValue)?.GetValueOrDefault("timestamp")));
            }
        }
        return ordered.ToImmutable();
    }

    private static JsonValue MissingResult(JsonValue call, JsonValue? timestamp) => JsonValue.Object(new KeyValuePair<string, JsonValue>?[]
    {
        KeyValuePair.Create("role", (JsonValue)"toolResult"),
        KeyValuePair.Create("toolCallId", (JsonValue)(Text(call, "id") ?? "")),
        KeyValuePair.Create("toolName", (JsonValue)(Text(call, "name") ?? "")),
        KeyValuePair.Create("content", (JsonValue)JsonValue.Array(JsonValue.Object(
            [KeyValuePair.Create("type", (JsonValue)"text"), KeyValuePair.Create("text", (JsonValue)MissingResultText)]))),
        KeyValuePair.Create("isError", (JsonValue)true),
        KeyValuePair.Create("details", (JsonValue)JsonValue.Object([KeyValuePair.Create("reason", (JsonValue)"missing_result")])),
        timestamp is null ? null : KeyValuePair.Create("timestamp", timestamp),
    }.OfType<KeyValuePair<string, JsonValue>>());

    private static string? Role(JsonValue message) => Text(message, "role");

    private static string? Text(JsonValue value, string key) =>
        value is JsonObjectValue item && item.TryGetValue(key, out var field) && field is JsonStringValue text ? text.Value : null;
}
