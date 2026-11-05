// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/live-support.ts (added in f3255de76), ported in the Pi v1.5.0
// sync (area run-failure): what tests of renderers' state read, every pi.live value of a conversation, and the results of nested calls.
// Its v1.5.0 changes (5f312dac4, area vocabulary): the observers take only value calls (WatchValueCall, as since area observers),
// nestedResults follows commits with followCommits (SessionTests.FollowCommits) and skips a retirement by its kind (DocumentRetireChange).
// Adaptations:
// - pi.live states and their slots are JSON objects (JsonObjectValue); a slot that is absent is null, and the pi.live of a view without one
//   is {}.
// - The observers run on the Session line, not the test's thread, so History and NestedResults lock what they collect.
// - nestedResults' followCommits is SessionTests.FollowCommits (commits as memory adopts them) with DocumentChanges; a
//   NestedToolExecutionResult is the result's JSON.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static SessionTests;

/// <summary>Upstream live-support.ts: histories of views and task graphs, and the slots and nested results they show.</summary>
internal static class LiveTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Every view of a conversation from the observation on, one per commit that changes it (upstream <c>viewHistory</c>'s
    /// result).</summary>
    internal sealed class History
    {
        private readonly List<ConversationView> views = [];
        private Observation<ConversationView>? observation;

        internal void Add(ConversationView view) { lock (views) views.Add(view); }

        internal void Start(Observation<ConversationView> attached)
        {
            observation = attached;
            lock (views) views.Insert(0, attached.Value);
        }

        /// <summary>The views so far, the attachment's first.</summary>
        public List<ConversationView> Views { get { lock (views) return [.. views]; } }

        /// <summary>The <c>pi.live</c> value of each view (upstream <c>live()</c>).</summary>
        public List<JsonObjectValue> Live() => [.. Views.Select(LiveOf)];

        public void Detach() => observation!.Detach();
    }

    /// <summary>
    /// Every view of <paramref name="conversationId"/> from now on, one per commit that changes it, unfolded: a synchronous observer, so no
    /// transition is skipped (upstream <c>viewHistory</c>). <see cref="History.Live"/> lists their <c>pi.live</c> values.
    /// </summary>
    internal static async Task<History> ViewHistory(IHarness harness, ConversationId conversationId)
    {
        var history = new History();
        history.Start(await harness.ObserveConversationAsync(conversationId, new Observer<ConversationView>((call, _) =>
        {
            if (call is WatchValueCall<ConversationView> value) history.Add(value.Value);
        }, _ => { }), Context));
        return history;
    }

    /// <summary>The <c>pi.live</c> value of <paramref name="view"/>; <c>{}</c> without one.</summary>
    internal static JsonObjectValue LiveOf(ConversationView view) => view.Doc("pi.live") as JsonObjectValue ?? JsonObjectValue.Empty;

    /// <summary>The slots of tool call <paramref name="callId"/>, model-issued or nested, in each <c>pi.live</c> value; null where it is
    /// absent (upstream <c>slotsOf</c>).</summary>
    internal static List<JsonObjectValue?> SlotsOf(IEnumerable<JsonObjectValue> history, string callId) =>
    [
        .. history.Select(live => Slots(live)
            .FirstOrDefault(slot => slot.TryGetValue("callId", out var id) && id is JsonStringValue { Value: var value } && value == callId)),
    ];

    /// <summary>The index of the first value where call <paramref name="callId"/> ended: its slot is done, or gone after it was listed; -1
    /// when it was never listed (upstream <c>endedAt</c>).</summary>
    internal static int EndedAt(IReadOnlyList<JsonObjectValue> history, string callId)
    {
        var slots = SlotsOf(history, callId);
        var listed = slots.FindIndex(slot => slot is not null);
        if (listed < 0) return -1;
        for (var index = listed; index < slots.Count; index++)
            if (slots[index] is not { } slot || Status(slot) == "done") return index;
        return -1;
    }

    /// <summary>The calls whose slot went <c>running</c>, in the order they started (upstream <c>startOrder</c>).</summary>
    internal static List<string> StartOrder(IEnumerable<JsonObjectValue> history)
    {
        var started = new List<string>();
        foreach (var live in history)
            foreach (var slot in Slots(live))
                if (Status(slot) == "running" && slot["callId"] is JsonStringValue { Value: var callId } && !started.Contains(callId)) started.Add(callId);
        return started;
    }

    /// <summary>
    /// The result of every nested call from now on, by its tool task, read from the <c>NestedCallDoc</c> its terminal commit writes (spec
    /// §7.3), the place a client reads a live caller's nested results from (upstream <c>nestedResults</c>). The results are JSON.
    /// </summary>
    internal static ConcurrentDictionary<long, JsonValue> NestedResults(IHarness harness)
    {
        var results = new ConcurrentDictionary<long, JsonValue>();
        FollowCommits(harness, (publication, _) =>
        {
            foreach (var change in DocumentChanges(publication))
            {
                if (change.Record.Kind != HarnessTasks.NestedCallDoc.Definition.Kind || change is not DocumentValueChange { Value: var state })
                    continue;
                if (state.TryGetValue("taskId", out var taskId) && taskId is JsonNumberValue id && state.TryGetValue("result", out var result))
                    results[(long)id.Value] = result;
            }
        });
        return results;
    }

    /// <summary>The result of nested call <paramref name="callId"/>, found by the task its slot names (upstream <c>nestedResultOf</c>).</summary>
    internal static JsonValue? NestedResultOf(IEnumerable<JsonObjectValue> history, IReadOnlyDictionary<long, JsonValue> results, string callId)
    {
        var slot = SlotsOf(history, callId).FirstOrDefault(each => each is not null && each.ContainsKey("taskId"));
        return slot is not null && slot["taskId"] is JsonNumberValue taskId && results.TryGetValue((long)taskId.Value, out var result) ? result : null;
    }

    /// <summary>Every task graph from the observation on, one per change, unfolded (upstream <c>graphHistory</c>'s result).</summary>
    internal sealed class GraphHistory
    {
        private readonly List<TaskGraph> graphs = [];
        private Observation<TaskGraph>? observation;

        internal void Add(TaskGraph graph) { lock (graphs) graphs.Add(graph); }

        internal void Start(Observation<TaskGraph> attached)
        {
            observation = attached;
            lock (graphs) graphs.Insert(0, attached.Value);
        }

        /// <summary>The graphs so far, the attachment's first.</summary>
        public List<TaskGraph> Graphs { get { lock (graphs) return [.. graphs]; } }

        public void Detach() => observation!.Detach();
    }

    /// <summary>Every task graph from now on, one per change, unfolded (upstream <c>graphHistory</c>).</summary>
    internal static async Task<GraphHistory> GraphHistoryOf(IHarness harness)
    {
        var history = new GraphHistory();
        history.Start(await harness.ObserveTaskGraphAsync(new Observer<TaskGraph>((call, _) =>
        {
            if (call is WatchValueCall<TaskGraph> value) history.Add(value.Value);
        }, _ => { }), Context));
        return history;
    }

    /// <summary>The live <c>pi.generation</c> nodes of <paramref name="graph"/>, in task ID order (upstream <c>generations</c>).</summary>
    internal static List<TaskGraphNode> Generations(TaskGraph graph) =>
        [.. graph.Tasks.Values.Where(node => node.Kind == "pi.generation").OrderBy(node => node.Id.Value)];

    /// <summary>The model-issued slots of <paramref name="live"/>, then its nested ones.</summary>
    private static IEnumerable<JsonObjectValue> Slots(JsonObjectValue live) => new[] { "tools", "nestedTools" }
        .SelectMany(key => live.TryGetValue(key, out var slots) && slots is JsonArrayValue array ? array.OfType<JsonObjectValue>() : []);

    private static string? Status(JsonObjectValue slot) => slot.TryGetValue("status", out var status) && status is JsonStringValue { Value: var text } ? text : null;
}
