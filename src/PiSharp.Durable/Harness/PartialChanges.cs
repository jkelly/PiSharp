// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/harness/partial-changes.ts. What a streamed answer changed in its
// partial message between two partial commits, so a commit costs the new text (da412b039; 6c2fd73b0's typing and its two tool-call
// narrowings are here too). Mappings:
// - Events are PiSharp.Contracts stream frames. PiSharp's frames that pi-ai has no event for: ThinkingCheckpoint replaces a thinking
//   block's text mid-block, so the block's pending appends are dropped until the next commit assigns the replacement (a `*_end` drops them
//   too, as upstream's does); ContentBlockFinalized ends a block like `text_end`, `thinking_end` or `toolcall_end`; ToolCallProvisionalStarted
//   starts tracking like `toolcall_start`; ToolCallCheckpoint gives the call's whole argument text: one that continues the text so far is
//   read as the missing piece, as pi-ai's ToolCallArgumentsStream.replace() pushes it as a `toolcall_delta`, and one that starts over
//   restarts the call's parse (see below).
// - `take(message)` clears the tracked calls' arguments in the message it was given; a JsonObjectValue is immutable, so PartialCommit.Message
//   is that message.
// - The snapshot. Upstream's take() records each tracked call's arguments as pi-ai's partial shows them, for the commit that finds the call
//   missing from the committed message (another writer reset it) and then commits the snapshot and stops tracking. PiSharp's partial
//   message is a PiSharp.AI AssistantStreamReducer snapshot, whose unfinished tool calls keep the arguments they started with, so there is
//   no such snapshot to take: the tracker keeps the call's argument text, take() records its length, and that commit writes a fresh parse
//   of the text up to that length. The call stays tracked, as nothing else would commit its later arguments before it ends; the changes
//   observed after take() apply on top in the next commit. A ToolCallCheckpoint that starts over commits the same way (a reseed).
// - The tracker is called from the stream (observe), the partial throttle's flush (take), the commit on the Session line (commit) and its
//   failure (restore): its own lock orders them, where upstream runs on one thread.
using System.Collections.Immutable;
using System.Text;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;

#pragma warning disable PISHARP_AI001 // PiSharp.AI's StreamingJsonParser (Pi v1.5.0) is experimental.

namespace PiSharp.Durable;

/// <summary>What one partial commit writes; from <see cref="PartialChanges.Take"/> (upstream <c>PartialCommit</c>).</summary>
/// <param name="Message">The partial message to write, with the tracked calls' arguments cleared: the commit writes them from the
/// changes.</param>
/// <param name="Plan">The plan <see cref="HarnessJson.AssignJson(DraftObject, string, JsonValue, AssignPlan?)"/> writes the message with:
/// text appends, and the arguments it leaves to <paramref name="ToolCalls"/>.</param>
/// <param name="ToolCalls">Each tracked tool call by content index: its argument changes since the last commit.</param>
internal sealed record PartialCommit(JsonObjectValue Message, AssignPlan Plan, IReadOnlyDictionary<int, PartialToolCallChanges> ToolCalls);

/// <summary>A tracked tool call's argument changes since the last commit, in order; the length of its argument text when they were taken;
/// and whether its text started over since (a reseed: the commit writes a parse of the text instead of the changes).</summary>
internal sealed record PartialToolCallChanges(ImmutableArray<JsonChange> Changes, int TextLength, bool Reseed);

/// <summary>
/// Collects, from a response's events, what changed in its partial message since the last commit, so the commit appends new text instead
/// of reassigning growing strings (upstream <c>PartialChanges</c>). Reassigning makes Chord compare the old and new string, which costs
/// O(length) per commit.
/// <list type="bullet">
/// <item>Text and thinking: the text and thinking deltas. The commit appends them where the committed string plus the deltas has the new
/// string's length, and compares otherwise. A block's end drops them, because providers may replace the text when a block ends.</item>
/// <item>Tool-call arguments: a <see cref="StreamingJsonParser"/> per call, fed with the raw argument text of each tool-call delta, reports
/// what each piece changed. The commit applies those changes to the committed arguments in order and leaves the arguments out of the
/// message assignment, so the committed arguments are exactly the parsed text so far. The call's end ends the tracking; the call's last
/// commit assigns its final arguments.</item>
/// </list>
/// </summary>
internal sealed class PartialChanges
{
    private readonly Lock gate = new();
    private Dictionary<int, PendingText> text = [];
    private readonly Dictionary<int, ToolCallState> toolCalls = [];

    /// <summary>A block's text deltas since the last commit; no text when the block's text was replaced since (no append then).</summary>
    private sealed class PendingText(string field, string? delta)
    {
        public string Field { get; } = field;
        public StringBuilder? Text { get; set; } = delta is null ? null : new StringBuilder(delta);
    }

    /// <summary>What a tracked tool call changed since the last commit, and whether any commit took its changes yet.</summary>
    private sealed class ToolCallState
    {
        public StreamingJsonParser Parser { get; set; } = new();
        public List<JsonChange> Changes { get; set; } = [];
        public bool Committed { get; set; }
        public bool Reseed { get; set; }
        /// <summary>The raw argument text so far, for a reseed.</summary>
        public StringBuilder Text { get; } = new();
    }

    /// <summary>Record what <paramref name="item"/> changed (upstream <c>observe</c>).</summary>
    public void Observe(StreamEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            switch (item)
            {
                case TextDelta delta: AddText(delta.ContentIndex, "text", delta.Delta); return;
                case ThinkingDelta delta: AddText(delta.ContentIndex, "thinking", delta.Delta); return;
                case ToolCallStarted start: toolCalls[start.ContentIndex] = new ToolCallState(); return;
                case ToolCallProvisionalStarted start: toolCalls[start.ContentIndex] = new ToolCallState(); return;
                case ToolCallDelta delta:
                    if (toolCalls.TryGetValue(delta.ContentIndex, out var call)) Read(delta.ContentIndex, call, delta.Delta);
                    return;
                case ToolCallCheckpoint checkpoint:
                {
                    if (!toolCalls.TryGetValue(checkpoint.ContentIndex, out var replaced)) return;
                    var sofar = replaced.Text.Length;
                    if (checkpoint.Json.Length >= sofar && replaced.Text.Equals(checkpoint.Json.AsSpan(0, sofar)))
                    {
                        Read(checkpoint.ContentIndex, replaced, checkpoint.Json[sofar..]);
                        return;
                    }
                    // Started over: the committed arguments no longer lead to the new text, so the next commit writes its parse.
                    replaced.Parser = new StreamingJsonParser();
                    replaced.Changes = [];
                    replaced.Text.Clear();
                    replaced.Reseed = true;
                    Read(checkpoint.ContentIndex, replaced, checkpoint.Json);
                    replaced.Changes = [];
                    return;
                }
                case TextEnded end: text.Remove(end.ContentIndex); return;
                case ThinkingEnded end: text.Remove(end.ContentIndex); return;
                case ThinkingCheckpoint checkpoint: text[checkpoint.ContentIndex] = new PendingText("thinking", null); return;
                case ToolCallEnded end: toolCalls.Remove(end.ContentIndex); return;
                case ContentBlockFinalized finalized:
                    text.Remove(finalized.ContentIndex);
                    toolCalls.Remove(finalized.ContentIndex);
                    return;
            }
        }
    }

    private void AddText(int index, string field, string delta)
    {
        if (text.TryGetValue(index, out var pending) && pending.Field == field) pending.Text?.Append(delta);
        else text[index] = new PendingText(field, delta);
    }

    private void Read(int index, ToolCallState call, string piece)
    {
        call.Text.Append(piece);
        foreach (var change in call.Parser.Append(piece))
        {
            // Arguments are an object: any other root shows as no arguments, so the snapshot is committed instead.
            if (change.Path.IsEmpty && !(change is JsonSetChange set && IsRecord(set.Value)))
            {
                toolCalls.Remove(index);
                return;
            }
            call.Changes.Add(change);
        }
    }

    /// <summary>
    /// Take what changed since the last call, for a commit of <paramref name="message"/>, the partial (upstream <c>take</c>). The returned
    /// message has the arguments of tracked tool calls cleared: the commit writes them from the changes. Call <see cref="Restore"/> if the
    /// commit fails.
    /// </summary>
    public PartialCommit Take(JsonObjectValue message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            var blocks = new Dictionary<int, AssignPlan>();
            foreach (var (index, pending) in text)
            {
                if (pending.Text is not { } delta) continue;
                blocks[index] = new AssignPlan.Fields(Members: new Dictionary<string, AssignPlan>(StringComparer.Ordinal)
                {
                    [pending.Field] = new AssignPlan.Append(delta.ToString()),
                });
            }
            text = [];
            var calls = new Dictionary<int, PartialToolCallChanges>();
            var content = message.TryGetValue("content", out var items) && items is JsonArrayValue array ? array : JsonArrayValue.Empty;
            var cleared = content.Items;
            foreach (var (index, call) in toolCalls)
            {
                // A tracked call is the tool call its start named; the check only guards the shape.
                if (index >= content.Count || content[index] is not JsonObjectValue block || !IsToolCall(block)) continue;
                calls[index] = new PartialToolCallChanges([.. call.Changes], call.Text.Length, call.Reseed);
                cleared = cleared.SetItem(index, block.SetItem("arguments", JsonObjectValue.Empty));
                blocks[index] = new AssignPlan.Fields(Members: new Dictionary<string, AssignPlan>(StringComparer.Ordinal)
                {
                    ["arguments"] = AssignPlan.Keep,
                });
                call.Changes = [];
                call.Reseed = false;
            }
            var written = calls.Count == 0 ? message : message.SetItem("content", JsonValue.Array(cleared));
            var plan = new AssignPlan.Fields(Members: new Dictionary<string, AssignPlan>(StringComparer.Ordinal)
            {
                ["content"] = new AssignPlan.Fields(Items: blocks),
            });
            return new PartialCommit(written, plan, calls);
        }
    }

    /// <summary>
    /// Write what <paramref name="commit"/> took into <c>generation.message</c>: the message leaf by leaf with the plan, then the argument
    /// changes (upstream <c>commit</c>). A tracked call whose block is new here gets its arguments from its changes, which then start at its
    /// first piece; when the committed message lost a call whose changes were already committed (another writer reset it), or the call's
    /// text started over, the commit writes a parse of the call's text so far instead (see the file header).
    /// </summary>
    public void Commit(DraftObject generation, PartialCommit commit)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(commit);
        lock (gate)
        {
            var committedBlocks = generation.ContainsKey("message") && generation.Get("message") is JsonObjectValue committed &&
                committed.TryGetValue("content", out var blocks) && blocks is JsonArrayValue list ? list.Count : 0;
            var message = commit.Message;
            var apply = new List<(int Index, IReadOnlyList<JsonChange> Changes)>();
            foreach (var (index, taken) in commit.ToolCalls)
            {
                if (!toolCalls.TryGetValue(index, out var call)) continue;
                if (taken.Reseed || (index >= committedBlocks && call.Committed))
                {
                    var snapshot = Parse(call.Text, taken.TextLength);
                    call.Committed = true;
                    // A new block is assigned whole, with the snapshot; an existing one gets it as a root set of its arguments.
                    if (index >= committedBlocks) message = WithArguments(message, index, snapshot);
                    else apply.Add((index, [new JsonSetChange([], snapshot.ToData())]));
                    continue;
                }
                call.Committed = true;
                apply.Add((index, taken.Changes));
            }
            HarnessJson.AssignJson(generation, "message", message, commit.Plan);
            if (apply.Count == 0) return;
            var content = generation.GetObject("message").GetArray("content");
            foreach (var (index, changes) in apply)
            {
                if (index >= content.Count || content.Get(index) is not JsonObjectValue block || !IsToolCall(block)) continue;
                var draft = content.GetObject(index);
                foreach (var change in changes) ApplyChange(draft, change);
            }
        }
    }

    /// <summary>Put back what a failed commit took, so the next commit applies it first (upstream <c>restore</c>).</summary>
    public void Restore(PartialCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (gate)
        {
            foreach (var (index, taken) in commit.ToolCalls)
            {
                if (!toolCalls.TryGetValue(index, out var call)) continue;
                call.Changes = [.. taken.Changes, .. call.Changes];
                if (taken.Reseed) call.Reseed = true;
            }
        }
    }

    /// <summary>Apply one argument change to the committed arguments of <paramref name="block"/> (upstream <c>applyChange</c>).</summary>
    private static void ApplyChange(DraftObject block, JsonChange change)
    {
        var path = change.Path;
        if (path.IsEmpty)
        {
            // Only an object root is tracked (Observe drops any other), and only a set starts it.
            if (change is JsonSetChange root && IsRecord(root.Value)) block.Set("arguments", JsonValue.FromData(root.Value));
            return;
        }
        // The path follows the parsed argument text, not a type: each segment names a container Observe saw set.
        var parent = block.GetDraft("arguments");
        foreach (var segment in path[..^1])
            parent = segment.Key is { } name ? parent.AsObject().GetDraft(name) : parent.AsArray().GetDraft(segment.Index);
        var last = path[^1];
        switch (change)
        {
            case JsonSetChange set when last.Key is { } key: parent.AsObject().Set(key, JsonValue.FromData(set.Value)); break;
            case JsonSetChange set: parent.AsArray().Set(last.Index, JsonValue.FromData(set.Value)); break;
            // AppendText takes object members; a string in an array is reassigned, which compares it whole.
            case JsonAppendChange append when last.Key is { } key: parent.AsObject().AppendText(key, append.Text); break;
            case JsonAppendChange append:
            {
                var items = parent.AsArray();
                items.Set(last.Index, JsonStringValue.Concat((JsonStringValue)items.Get(last.Index), append.Text));
                break;
            }
        }
    }

    /// <summary>Arguments of the first <paramref name="length"/> characters of a call's text: an object, or none.</summary>
    private static JsonObjectValue Parse(StringBuilder text, int length)
    {
        var parser = new StreamingJsonParser();
        parser.Append(text.ToString(0, length));
        return parser.Snapshot() is { } snapshot && IsRecord(snapshot) ? (JsonObjectValue)JsonValue.FromData(snapshot) : JsonObjectValue.Empty;
    }

    private static JsonObjectValue WithArguments(JsonObjectValue message, int index, JsonObjectValue arguments)
    {
        var content = (JsonArrayValue)message["content"];
        var block = ((JsonObjectValue)content[index]).SetItem("arguments", arguments);
        return message.SetItem("content", JsonValue.Array(content.Items.SetItem(index, block)));
    }

    private static bool IsToolCall(JsonObjectValue block) =>
        block.TryGetValue("type", out var type) && type is JsonStringValue { Value: "toolCall" };

    private static bool IsRecord(JsonData value) => value.Value.ValueKind == System.Text.Json.JsonValueKind.Object;
}
