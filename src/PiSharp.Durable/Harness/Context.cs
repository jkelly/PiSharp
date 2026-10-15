// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/harness/context.ts. Messages are pi-ai wire JSON. Records and
// JSON values are immutable here, so upstream's freezeJson has no counterpart; a kept range is shared as is, and every returned view has
// its own arrays because ImmutableArray values are never shared mutably.
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
/// marker extends both.
/// </summary>
internal sealed record ContextRange(ContextBounds Bounds, ImmutableArray<EntryRecord> Entries, ContextView View, ImmutableHashSet<EntryId> Edited,
    ImmutableArray<JsonValue> Settled, ImmutableArray<JsonValue> Open);

/// <summary>Context reads and model context derivation (upstream <c>context.ts</c>).</summary>
internal static class ConversationContext
{
    private const int ScanPageSize = 256;
    private static readonly HashSet<string> ExcludedStopReasons = new(StringComparer.Ordinal) { "aborted", "error", "deferred" };
    private const string MissingResultText = "Tool result unavailable: history ends before this call completed.";

    /// <summary>
    /// Captures the bounds of the current context, or of the context cut off at the visible entry <paramref name="at"/>, with two O(1)
    /// reads (upstream <c>captureContextBounds</c>). Run this on the Session line; entries at or below the tail are immutable, so
    /// <see cref="DeriveContextAsync"/> can then scan them off the line.
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
    /// read.
    /// </summary>
    public static async Task<(ContextView View, ContextRange? Range)> ReadContextFromAsync(Session session, IStorage storage,
        ConversationId conversationId, DurableContext context, EntryId? at, ContextRange? previous)
    {
        var bounds = await session.ReadOnLineAsync(() => CaptureContextBoundsAsync(storage, conversationId, context, at)).ConfigureAwait(false);
        if (bounds is null) return (ContextView.Empty, null);
        ContextRange range;
        if (previous is null || previous.Bounds.Head?.Id != bounds.Head?.Id)
            range = DeriveRange(bounds, await ScanRangeAsync(storage, conversationId, RangeQuery(conversationId, bounds), context).ConfigureAwait(false));
        else if (bounds.Tail == previous.Bounds.Tail)
            range = previous;
        else if (bounds.Tail.Value < previous.Bounds.Tail.Value)
            range = DeriveRange(bounds, [.. previous.Entries.Where(entry => entry.Id.Value <= bounds.Tail.Value)]);
        else
        {
            var query = new EntryQuery(conversationId) { MinEntryId = new EntryId(previous.Bounds.Tail.Value + 1), MaxEntryId = bounds.Tail };
            var added = await ScanRangeAsync(storage, conversationId, query, context).ConfigureAwait(false);
            range = ExtendRange(previous, bounds, added);
        }
        return (range.View, range);
    }

    /// <summary>Committed context of one conversation: bounds captured on the Session line, entries derived off it (upstream
    /// <c>readContext</c>).</summary>
    public static async Task<ContextView> ReadContextAsync(Session session, IStorage storage, ConversationId conversationId, DurableContext context,
        EntryId? at = null)
    {
        // The lease keeps Storage open for the off-line derive when close follows the bounds job on the line.
        var (bounds, lease) = await session.ReadOnLineAsync(async () =>
            (await CaptureContextBoundsAsync(storage, conversationId, context, at).ConfigureAwait(false), session.HoldStorageOpen())).ConfigureAwait(false);
        using (lease) return await DeriveContextAsync(storage, conversationId, bounds, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Derives the active transcript and model context of one conversation within captured bounds (upstream <c>deriveContext</c>). H = newest
    /// visible head marker; the range runs from <c>H.head</c> (or transcript start) through the tail. Per target, the newest edit in the
    /// range wins. Context entries are H followed by the range's non-head entries.
    /// </summary>
    public static async Task<ContextView> DeriveContextAsync(IStorage storage, ConversationId conversationId, ContextBounds? bounds, DurableContext context)
    {
        if (bounds is null) return ContextView.Empty;
        return DeriveRange(bounds, await ScanRangeAsync(storage, conversationId, RangeQuery(conversationId, bounds), context).ConfigureAwait(false)).View;
    }

    /// <summary>The raw active entries within captured bounds, without deriving model context (upstream <c>activeEntries</c>).</summary>
    public static async Task<ImmutableArray<EntryRecord>> ActiveEntriesAsync(IStorage storage, ConversationId conversationId, ContextBounds? bounds,
        DurableContext context)
    {
        if (bounds is null) return [];
        return SelectActive(bounds.Head, await ScanRangeAsync(storage, conversationId, RangeQuery(conversationId, bounds), context).ConfigureAwait(false));
    }

    /// <summary>Derives the context view from the entries scanned within <paramref name="bounds"/>.</summary>
    private static ContextRange DeriveRange(ContextBounds bounds, ImmutableArray<EntryRecord> entries)
    {
        var edits = new Dictionary<EntryId, ContextEdit>();
        // Edits of every entry in the range count, including older head markers that SelectActive drops.
        foreach (var entry in entries) foreach (var edit in entry.Edits ?? []) edits[edit.Target] = edit;
        var active = SelectActive(bounds.Head, entries);
        var contributions = active.Select(entry => Contribute(entry, edits.GetValueOrDefault(entry.Id))).ToImmutableArray();
        var (settled, open) = Settle([], [.. contributions.SelectMany(messages => messages)]);
        var messages = LeadWithSystem([.. settled, .. OrderToolResults(open)]);
        var view = new ContextView { Head = bounds.Head, Entries = active, Contributions = contributions, Messages = messages };
        return new ContextRange(bounds, entries, view, [.. edits.Keys], settled, open);
    }

    /// <summary>
    /// <paramref name="previous"/> extended by the entries <paramref name="added"/> after its tail under the same head marker: only their
    /// contributions and the open messages are derived. An added edit can change an earlier entry, so it derives the whole range again.
    /// </summary>
    private static ContextRange ExtendRange(ContextRange previous, ContextBounds bounds, ImmutableArray<EntryRecord> added)
    {
        var entries = previous.Entries.AddRange(added);
        if (added.Any(entry => entry.Edits is not null || entry.Head is not null || previous.Edited.Contains(entry.Id))) return DeriveRange(bounds, entries);
        var contributions = added.Select(entry => Contribute(entry, null)).ToImmutableArray();
        var (settled, open) = Settle(previous.Settled, [.. previous.Open, .. contributions.SelectMany(messages => messages)]);
        var view = new ContextView
        {
            Head = bounds.Head,
            Entries = previous.View.Entries.AddRange(added),
            Contributions = previous.View.Contributions.AddRange(contributions),
            Messages = LeadWithSystem([.. settled, .. OrderToolResults(open)]),
        };
        return new ContextRange(bounds, entries, view, previous.Edited, settled, open);
    }

    /// <summary>
    /// Moves a system message that only user messages precede to the front (upstream <c>leadWithSystem</c>). A run's input is committed
    /// before generation renders the system prompt, so a transcript, or the range after a compaction or reset, starts with user messages
    /// followed by the baseline system message. Providers treat only a leading system message as the initial prompt and tool set.
    /// </summary>
    private static ImmutableArray<JsonValue> LeadWithSystem(ImmutableArray<JsonValue> messages)
    {
        var index = -1;
        for (var at = 0; at < messages.Length; at++)
            if (Role(messages[at]) != "user") { index = at; break; }
        if (index <= 0 || Role(messages[index]) != "system") return messages;
        return [messages[index], .. messages.Take(index), .. messages.Skip(index + 1)];
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
