// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-view-run.test.ts (added in f3255de76, area view): what a
// renderer sees of a run through the conversation view, the transcript window and pi.live, every change a commit makes rebuilt from
// operations alone. describe("a run as the conversation view shows it") is harness.view-run.a-run-as-the-conversation-view-shows-it.* and
// describe("streamed partials") harness.view-run.streamed-partials.*, on MemoryStorage as upstream. Verbatim apart from the adaptations in
// ChatSupport.cs, HarnessSupport.cs and SessionSupport.cs, and these:
// - live-support.ts's viewHistory and slotsOf are ViewHistory and SlotsOf below (LiveSupport.cs is area run-failure's); pi.live states and
//   their slots are JSON objects, a missing slot is null. followCommits and documentValues are SessionTests.ObserveCommits and
//   DocumentChanges with a value (commits as memory adopts them, upstream 6c2fd73b0's observeCommits).
// - listen() checks each frame's operations as JSON values (JsonValue.DeepEquals) where upstream compares JSON.stringify texts; replays use
//   DeltaApply.Apply. drained() (setTimeout(0)) is ViewTests.Drained, a Session line barrier; waitFor is Eventually. A distinct partial is
//   one whose JSON differs from the previous one, where upstream compares references.
// - Tools sleep with Task.Delay where upstream uses setTimeout: their output must cross the progress throttle in real time, as upstream's
//   does. An aborted controller is a DurableCancellation cancelled with Exception("cancelled").
// - Not yet v1.5.0, each left to the area that ports it: a run's pi.live.run is f3255de76's { taskId, inputs } without turns and first (area
//   live-turns); "ends a call that never runs ..." is f3255de76's form, the call not offered listed done with its result at once (v1.5.0's
//   "ends a call that never runs through its own task, ..." lists it pending with its task, #11043: area tool-tasks); and the two runFailure
//   checks are left out (pi.live.runFailure: area run-failure).
// - Streamed partials (#9265): the cases moved here from HarnessEventsCases.cs (area partial-commits ported them in harness-events.test.ts's
//   form, on the event stream); here they read the view's operations, as upstream's v1.5.0 form does. Two of them pace the faux stream on
//   commits instead of real time (CommitPaced: .NET's timers on Windows tick every 15.6 ms), and they count allocations with
//   GC.GetTotalAllocatedBytes and the heap with GC.GetTotalMemory in place of V8's GCProfiler and heap statistics.
using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using PiSharp.Durable.Storage;
using static ChatTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessViewRunCases() => ViewRunTests.Cases("harness.view-run.");
}

/// <summary>The harness.view-run.* cases and their support.</summary>
internal static class ViewRunTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    internal sealed record Listening(IWatchHandle<ConversationView> Watch, SyncList<ViewTests.Frame> Frames, Func<int> Mismatches);

    /// <summary>Starts a watch of <paramref name="conversation"/> that records every call and, with <paramref name="check"/>, checks that its
    /// operations turn the value before it into its value (upstream <c>listen</c>).</summary>
    private static async Task<Listening> Listen(IConversation conversation, bool check = true)
    {
        var watch = await conversation.WatchAsync(Context);
        var frames = new SyncList<ViewTests.Frame>();
        JsonValue replayed = watch.Value.ToJson();
        var mismatches = 0;
        watch.Start((call, _) =>
        {
            var (value, ops, _) = Change(call);
            if (check)
            {
                replayed = DeltaApply.Apply(replayed, ops);
                if (!JsonValue.DeepEquals(replayed, value.ToJson())) Interlocked.Increment(ref mismatches);
            }
            frames.Add(new ViewTests.Frame(value, ops));
            return Task.CompletedTask;
        });
        return new Listening(watch, frames, () => Volatile.Read(ref mismatches));
    }

    /// <summary>Every view of a conversation from the observation on, one per commit that changes it (upstream live-support.ts's
    /// <c>viewHistory</c>).</summary>
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

        public List<ConversationView> Views { get { lock (views) return [.. views]; } }

        /// <summary>The <c>pi.live</c> value of each view.</summary>
        public List<JsonObjectValue> Live() => [.. Views.Select(LiveOf)];

        public void Detach() => observation!.Detach();
    }

    /// <summary>A synchronous observer of <paramref name="conversationId"/>'s view, so no transition is skipped (upstream
    /// <c>viewHistory</c>).</summary>
    internal static async Task<History> ViewHistory(IHarness harness, ConversationId conversationId)
    {
        var history = new History();
        history.Start(await harness.ObserveConversationAsync(conversationId, new Observer<ConversationView>((call, _) =>
        {
            if (call is WatchValueCall<ConversationView> value) history.Add(value.Value);
        }, _ => { }), Context));
        return history;
    }

    /// <summary>The slots of tool call <paramref name="callId"/>, model-issued or nested, in each <c>pi.live</c> value; null where it is
    /// absent (upstream <c>slotsOf</c>).</summary>
    internal static List<JsonObjectValue?> SlotsOf(IEnumerable<JsonObjectValue> history, string callId) =>
    [
        .. history.Select(live => new[] { "tools", "nestedTools" }
            .SelectMany(key => live.TryGetValue(key, out var slots) && slots is JsonArrayValue array ? array.OfType<JsonObjectValue>() : [])
            .FirstOrDefault(slot => slot.TryGetValue("callId", out var id) && id is JsonStringValue { Value: var value } && value == callId)),
    ];

    private static JsonObjectValue LiveOf(ConversationView view) => view.Doc("pi.live") as JsonObjectValue ?? JsonObjectValue.Empty;

    private static JsonValue? Field(JsonObjectValue? value, string key) => value is not null && value.TryGetValue(key, out var item) ? item : null;

    private static string? Status(JsonObjectValue? slot) => Field(slot, "status") is JsonStringValue { Value: var status } ? status : null;

    private static JsonObjectValue? Generation(JsonObjectValue live) => Field(live, "generation") as JsonObjectValue;

    private static List<long> RunInputs(JsonObjectValue? live) =>
        Field(Field(live, "run") as JsonObjectValue, "inputs") is JsonArrayValue inputs ? [.. inputs.Select(id => (long)N(id))] : [];

    private static string OpsJson(IEnumerable<Op> ops) => JsonValue.Array(ops.Select(op => (JsonValue)op.ToJson())).ToJson();

    private static ChatSetup Slow() => Setup(new FauxOptions { TokensPerSecond = 400, MinTokenSize = 1, MaxTokenSize = 1 });

    /// <summary>Committed partials of the Harness's generations, one per commit that has one (upstream <c>partialsOf</c>).</summary>
    private static SyncList<JsonObjectValue> PartialsOf(IHarness harness)
    {
        var partials = new SyncList<JsonObjectValue>();
        ObserveCommits(harness, (publication, _) =>
        {
            foreach (var change in DocumentChanges(publication))
            {
                if (change.Record.Kind == "pi.live" && change.Value is { } live && Field(Generation(live), "message") is JsonObjectValue message)
                    partials.Add(message);
            }
        });
        return partials;
    }

    /// <summary>The distinct in-flight partials in <paramref name="states"/>, in order (upstream <c>partialsIn</c>).</summary>
    private static List<JsonObjectValue> PartialsIn(IEnumerable<JsonObjectValue> states)
    {
        var partials = new List<JsonObjectValue>();
        foreach (var state in states)
        {
            if (Field(Generation(state), "message") is JsonObjectValue message && (partials.Count == 0 || !JsonValue.DeepEquals(message, partials[^1])))
                partials.Add(message);
        }
        return partials;
    }

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    private static ToolRegistration ToolOf(string name, string description, ToolExecute execute, JsonObjectValue? parameters = null) =>
        Define.Tool(new ToolRegistration { Name = name, Description = description, Parameters = parameters ?? NoParameters, Execute = execute });

    private static async Task<TaskId> RunTask(IHarness harness, IConversation root) =>
        new((long)N(((JsonObjectValue)(await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context))!["run"])["taskId"]));

    private static FauxStep Held(Task release, JsonObjectValue answer) => new FauxResponseFactory(async request =>
    {
        await await Task.WhenAny(release, HarnessTests.Aborted(request.Signal));
        return answer;
    });

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix) =>
        [.. Run(prefix + "a-run-as-the-conversation-view-shows-it."), .. StreamedPartials(prefix + "streamed-partials.")];

    private static IEnumerable<(string, Func<Task>)> Run(string prefix)
    {
        yield return Case(prefix + "streams-the-answer-as-operations-that-rebuild-every-committed-partial-then-appends-it", async () =>
        {
            var setup = Slow();
            var text = string.Concat(Enumerable.Repeat("streamed answer text ", 20));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText(text)]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var partials = PartialsOf(harness);
            var history = await ViewHistory(harness, root.Id);
            var listening = await Listen(root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            history.Detach();
            Equal(0, listening.Mismatches(), "mismatches");
            var states = history.Live();
            var views = history.Views;
            Check(partials.Count > 1, $"partials: {partials.Count}");
            Seq(partials.Snapshot().Select(TextOf), PartialsIn(states).Select(TextOf), "the committed partials");
            // The run shows from the input's commit until the commit that appends the answer and clears the partial.
            var started = states.FindIndex(state => state.ContainsKey("run"));
            // f3255de76's run; v1.5.0's adds turns and first (area live-turns).
            MatchExactly($$"""{"taskId":"<number>","inputs":[{{submission.Id.Value}}]}""", states[started]["run"], "the run");
            Equal("pi.user", views[started].Entries[^1].Kind, "the input's commit");
            var ended = states.FindIndex(started + 1, state => !state.ContainsKey("run"));
            Check(!states[ended].ContainsKey("generation"), "no partial once the run ends");
            Equal(text, TextOf(views[ended].Entries[^1].Model![0]), "the answer");
            SameJson("{}", states[^1], "pi.live at the end");
            await listening.Watch.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-a-tool-calls-slot-from-pending-through-its-running-output-to-its-result-entry", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HarnessTests.AddTool(setup.Registry, ToolOf("print", "Prints", async (_, api, _) =>
            {
                api.Output("one\n");
                await Task.Delay(150);
                api.Output("two\n");
                await gate.Task;
                return new ToolExecutionResult();
            }, O("""{"type":"object","properties":{"n":{"type":"number"}},"required":["n"]}""")));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("print", Obj(("n", 1)), "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var history = await ViewHistory(harness, root.Id);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await HarnessTests.Eventually(() => SlotsOf(history.Live(), "c1") is [.., var last] && Field(last, "output") is JsonStringValue { Value: "one\ntwo\n" });
            gate.Resolve();
            await submission.WaitAsync(Context);
            history.Detach();
            var views = history.Views;
            var slots = SlotsOf(history.Live(), "c1");
            var listed = slots.FindIndex(slot => slot is not null);
            // Listed pending with the tool-calling answer, in the same commit.
            MatchExactly("""{"callId":"c1","name":"print","status":"pending","taskId":"<number>"}""", slots[listed], "listed");
            Equal("pi.assistant", views[listed].Entries[^1].Kind, "the listing commit");
            var outputs = slots.Where(slot => Status(slot) == "running").Select(slot => Field(slot, "output") is JsonStringValue { Value: var output } ? output : "");
            Seq(["", "one\n", "one\ntwo\n"], outputs.Distinct(), "running outputs");
            // Done with its result entry, appended in the same commit.
            var done = slots.FindIndex(slot => Status(slot) == "done");
            MatchExactly($$"""{"callId":"c1","name":"print","status":"done","taskId":"<number>","entry":{{views[done].Entries[^1].Id.Value}}}""", slots[done], "done");
            MatchJson("""{"role":"toolResult","toolCallId":"c1"}""", views[done].Entries[^1].Model![0], "the result entry");
            // The run's two generations, the tool round and the answer, hand the run over.
            var runTasks = history.Live().Select(state => Field(Field(state, "run") as JsonObjectValue, "taskId")).OfType<JsonNumberValue>()
                .Select(id => id.Value).ToHashSet();
            Equal(2, runTasks.Count, "run tasks");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "shows-queued-inputs-in-the-inbox-a-retry-backoff-and-the-next-run", async () =>
        {
            var setup = Setup();
            var release = new HarnessTests.Deferred();
            var error = FauxAssistantMessage([], stopReason: "error", errorMessage: "503 Service Unavailable");
            setup.Faux.SetResponses(Held(release.Task, FauxAssistantMessage([FauxText("first")])), error, FauxAssistantMessage([FauxText("second")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            setup.Retry = new RetrySettings { Enabled = true, MaxRetries = 1, BaseDelayMs = 1 };
            var history = await ViewHistory(harness, root.Id);
            var first = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            static JsonArrayValue Inbox(ConversationView view) => (JsonArrayValue)((JsonObjectValue)view.Doc("pi.inbox")!)["items"];
            MatchJson($$"""[{"id":{{followUp.Id.Value}},"mode":"followUp"}]""", Inbox(history.Views[^1]), "the queued follow-up");
            release.Resolve();
            await followUp.WaitAsync(Context);
            history.Detach();
            var states = history.Live();
            static JsonObjectValue? Retry(JsonObjectValue state) => Field(Generation(state), "retry") as JsonObjectValue;
            Check(states.Any(state => Field(Retry(state), "error") is JsonStringValue { Value: "503 Service Unavailable" }), "the retry backoff");
            var retried = states.FindIndex(state => Retry(state) is not null);
            Check(states.Skip(retried).Any(state => Field(Generation(state), "attempt") is JsonNumberValue { Value: 2 }), "the second attempt");
            var runs = new List<List<long>>();
            for (var index = 0; index < states.Count; index++)
            {
                var inputs = RunInputs(states[index]);
                if (inputs.Count > 0 && (index == 0 || !inputs.SequenceEqual(RunInputs(states[index - 1])))) runs.Add(inputs);
            }
            Seq([first.Id.Value], runs[0], "the first run");
            Seq([followUp.Id.Value], runs[^1], "the next run");
            Equal(0, Inbox(history.Views[^1]).Count, "the inbox at the end");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rebuilds-every-committed-partial-of-thinking-text-and-tool-call-arguments-from-the-views-operations", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 150, MinTokenSize = 1, MaxTokenSize = 1 });
            var message = FauxAssistantMessage(
            [
                FauxThinking(string.Concat(Enumerable.Repeat("thinking about it ", 10))),
                FauxText(string.Concat(Enumerable.Repeat("some text ", 10))),
                FauxToolCall("missing", Obj(("path", string.Concat(Enumerable.Repeat("a/long/path/", 10))), ("note", new string('x', 60))), "c1"),
            ], stopReason: "toolUse");
            setup.Faux.SetResponses(message, FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var partials = PartialsOf(harness);
            var views = new SyncList<ConversationView>();
            var ops = new SyncList<ImmutableArray<Op>>();
            var observation = await harness.ObserveConversationAsync(root.Id, new Observer<ConversationView>((call, _) =>
            {
                if (call is not WatchValueCall<ConversationView> value) return;
                views.Add(value.Value);
                ops.Add(value.Ops);
            }, _ => { }), Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            observation.Detach();
            // Replaying the operations alone, from the observation's value, gives every committed partial.
            JsonValue replayed = observation.Value.ToJson();
            var rebuilt = new List<JsonValue>();
            foreach (var batch in ops.Snapshot())
            {
                replayed = DeltaApply.Apply(replayed, batch);
                var live = Field((JsonObjectValue)((JsonObjectValue)replayed)["docs"], "pi.live") as JsonObjectValue ?? JsonObjectValue.Empty;
                var partial = Field(Generation(live), "message");
                if (partial is not null && (rebuilt.Count == 0 || !JsonValue.DeepEquals(partial, rebuilt[^1]))) rebuilt.Add(partial);
            }
            Check(JsonValue.DeepEquals(replayed, views.Snapshot()[^1].ToJson()), "the replayed view");
            Check(partials.Count > 2, $"partials: {partials.Count}");
            Seq(partials.Snapshot().Select(partial => partial["content"].ToJson()), rebuilt.Select(partial => ((JsonObjectValue)partial)["content"].ToJson()),
                "the rebuilt partials");
            // Growing text and thinking travel as appends.
            var appends = ops.Snapshot().SelectMany(batch => batch).Count(op => op is AppendOp { Path: [_, _, { Key: "generation" }, ..] });
            Check(appends > 2, $"appends: {appends}");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rebuilds-a-sliding-tail-window-from-output-trims-and-appends", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HarnessTests.AddTool(setup.Registry, ToolOf("tail", "Prints lines", async (_, api, _) =>
            {
                for (var line = 0; line < 6; line++)
                {
                    api.Output($"line {line}\n");
                    await Task.Delay(120);
                }
                await gate.Task;
                return new ToolExecutionResult();
            }) with { OutputLimits = new ToolOutputLimits { MaxLines = 3, Retain = ToolOutputRetention.Tail } });
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("tail", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            static string? FirstOutput(JsonObjectValue? live) =>
                Field(live, "tools") is JsonArrayValue { Count: > 0 } tools && Field(tools[0] as JsonObjectValue, "output") is JsonStringValue { Value: var output }
                    ? output : null;
            var outputs = new SyncList<string>();
            ObserveCommits(harness, (publication, _) =>
            {
                foreach (var change in DocumentChanges(publication))
                {
                    if (change.Record.Kind == "pi.live" && FirstOutput(change.Value) is { } output && (outputs.Count == 0 || outputs[outputs.Count - 1] != output))
                        outputs.Add(output);
                }
            });
            var listening = await Listen(root);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await HarnessTests.Eventually(() => outputs.Snapshot() is [.., var last] && last.EndsWith("line 5\n", StringComparison.Ordinal));
            gate.Resolve();
            await submission.WaitAsync(Context);
            await ViewTests.Drained(harness);
            await listening.Watch.StopAsync();
            Equal(0, listening.Mismatches(), "mismatches");
            var shown = new List<string>();
            foreach (var frame in listening.Frames.Snapshot())
            {
                if (FirstOutput(LiveOf(frame.Value)) is { } output && (shown.Count == 0 || shown[^1] != output)) shown.Add(output);
            }
            Seq(outputs.Snapshot(), shown, "the shown windows");
            Check(listening.Frames.Snapshot().Any(frame => frame.Ops.Any(op => op is TruncateOp)), "a trim");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "ends-a-run-and-starts-the-queued-follow-ups-in-one-value", async () =>
        {
            var setup = Setup();
            var release = new HarnessTests.Deferred();
            setup.Faux.SetResponses(Held(release.Task, FauxAssistantMessage([FauxText("first")])), FauxAssistantMessage([FauxText("second")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("f"), Context);
            var history = await ViewHistory(harness, root.Id);
            release.Resolve();
            await followUp.WaitAsync(Context);
            history.Detach();
            var states = history.Live();
            var views = history.Views;
            var boundary = states.FindIndex(state => RunInputs(state) is [var head, ..] && head == followUp.Id.Value);
            Seq([input.Id.Value], RunInputs(states[boundary - 1]), "the run before");
            var before = views[boundary - 1];
            var after = views[boundary];
            var appended = after.Entries.Skip(before.Entries.Length);
            Seq(["pi.assistant first", "pi.user f"], appended.Select(entry => $"{entry.Kind} {TextOf(entry.Model?[0])}"), "appended");
            Equal(0, ((JsonArrayValue)((JsonObjectValue)after.Doc("pi.inbox")!)["items"]).Count, "the inbox");
            await harness.CloseAsync(Context);
        });

        // f3255de76's form (see the file header): v1.5.0's "ends a call that never runs through its own task, ..." is area tool-tasks'.
        yield return Case(prefix + "ends-a-call-that-never-runs-with-its-result-at-once-and-a-tool-aborted-with-its-generation", async () =>
        {
            var setup = Setup();
            HarnessTests.AddTool(setup.Registry, ToolOf("wait", "Waits until aborted", async (_, _, callContext) =>
            {
                await HarnessTests.Aborted(callContext.CancellationToken);
                throw new InvalidOperationException("unreachable");
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("ghost", JsonObjectValue.Empty, "c1"), FauxToolCall("wait", JsonObjectValue.Empty, "c2")],
                stopReason: "toolUse"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var history = await ViewHistory(harness, root.Id);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await HarnessTests.Eventually(() => SlotsOf(history.Live(), "c2").Any(slot => Status(slot) == "running"));
            // Aborting the generation aborts its round: the tool ends with its aborted result first (spec §8.5).
            await harness.AbortTaskAsync(await RunTask(harness, root), Context);
            await submission.WaitAsync(Context);
            // PiSharp: the submission can settle before the commit that ends the aborted tool reaches the observer, so wait for it.
            await HarnessTests.Eventually(() => SlotsOf(history.Live(), "c2").Any(slot => Status(slot) == "done"));
            history.Detach();
            var states = history.Live();
            var views = history.Views;
            // The call not offered is listed done, with its result entry, in the commit of its calling answer.
            var ghosts = SlotsOf(states, "c1");
            var listed = ghosts.FindIndex(slot => slot is not null);
            var ghost = ghosts[listed]!;
            Equal("done", Status(ghost), "the call not offered");
            var listing = views[listed].Entries;
            Seq(["pi.assistant", "pi.tool-result"], listing.Select(entry => entry.Kind).TakeLast(2), "the listing commit");
            Equal((double)listing[^1].Id.Value, N(ghost["entry"]), "its result entry");
            // The aborted tool ends with its result entry, then the run ends without an answer.
            var waits = SlotsOf(states, "c2");
            var done = waits.FindIndex(slot => Status(slot) == "done");
            Equal((double)views[done].Entries[^1].Id.Value, N(waits[done]!["entry"]), "the aborted tool's result entry");
            // Upstream then checks pi.live.runFailure (area run-failure).
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-steer-in-the-current-run-shows-nothing-for-other-conversations-and-appends-a-reset", async () =>
        {
            var setup = Setup();
            var gate = new HarnessTests.Deferred();
            HarnessTests.AddTool(setup.Registry, ToolOf("hold", "Waits", async (_, _, _) =>
            {
                await gate.Task;
                return new ToolExecutionResult();
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var other = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var history = await ViewHistory(harness, root.Id);
            var input = await root.SubmitAsync(SubmissionDraft.Input("a"), Context);
            await HarnessTests.Eventually(() => SlotsOf(history.Live(), "c1").Any(slot => Status(slot) == "running"));
            var steer = await root.SubmitAsync(SubmissionDraft.Input("s", WhenBusy.Steer), Context);
            var count = history.Views.Count;
            await other.CommitAsync(tx => tx.AppendEntryAsync(other.Id, new EntryDraft { Kind = "note" }), Context);
            Equal(count, history.Views.Count, "views after another conversation's commit");
            gate.Resolve();
            await input.WaitAsync(Context);
            await root.ResetAsync(null, Context);
            history.Detach();
            var states = history.Live();
            // One run: the steer joined it.
            Seq([input.Id.Value], states.Select(RunInputs).Where(inputs => inputs.Count > 0).Select(inputs => inputs[0]).Distinct(), "runs");
            Check(states.Any(state => RunInputs(state).Contains(steer.Id.Value)), "the steer in the run");
            // The reset is appended in place, with no run.
            Equal("pi.reset", history.Views[^1].Entries[^1].Kind, "the reset");
            Check(!states[^1].ContainsKey("run"), "no run after the reset");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "converts-the-streamed-partial-into-one-aborted-answer-entry-when-the-run-is-aborted", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 100, MinTokenSize = 1, MaxTokenSize = 1 });
            setup.Faux.SetResponses(FauxAssistantMessage([FauxText(new string('x', 400))]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var history = await ViewHistory(harness, root.Id);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await HarnessTests.Eventually(() => history.Live().Any(state => Field(Generation(state), "message") is not null));
            await harness.AbortTaskAsync(await RunTask(harness, root), Context);
            await submission.WaitAsync(Context);
            // PiSharp: the submission can settle before the commit that ends the run reaches the observer (seen under load), so wait for
            // the value that ends it.
            await HarnessTests.Eventually(() => history.Live().Skip(1).Any(state => !state.ContainsKey("run")));
            history.Detach();
            var states = history.Live();
            var views = history.Views;
            var ended = states.FindIndex(1, state => !state.ContainsKey("run"));
            Check(Field(Generation(states[ended - 1]), "message") is not null, "the partial before");
            var appended = views[ended].Entries.Skip(views[ended - 1].Entries.Length).ToList();
            Seq(["pi.assistant"], appended.Select(entry => entry.Kind), "appended");
            MatchJson("""{"role":"assistant","stopReason":"aborted"}""", appended[0].Model![0], "the aborted answer");
            // Upstream then checks pi.live.runFailure (area run-failure).
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "applies-later-operations-to-an-overflow-replacement-that-holds-an-in-flight-partial", async () =>
        {
            var (harness, root) = await OpenChat(new MemoryStorage(), Setup());
            var partial = FauxAssistantMessage([FauxText("hel")]);
            var watch = await root.WatchAsync(Context, new ConversationWindow { Min = 1, Max = 1 });
            await root.CommitAsync(async tx => (await tx.DocAsync(Live.LiveDoc, root.Id)).Set("generation", Obj(("attempt", 1), ("message", partial))), Context);
            // Past 10,000 pending operations: an append, a splice, and a `before` per commit.
            for (var index = 0; index < 3400; index++) await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            var frames = new SyncList<ViewTests.Frame>();
            watch.Start((call, _) =>
            {
                var (value, ops, _) = Change(call);
                frames.Add(new ViewTests.Frame(value, ops));
                return Task.CompletedTask;
            });
            await ViewTests.Drained(harness);
            var replacement = frames[0];
            Check(replacement.Ops is [ReplaceOp { Value: var whole }] && JsonValue.DeepEquals(whole, replacement.Value.ToJson()), "one replacement");
            await root.CommitAsync(async tx =>
            {
                var block = (await tx.DocAsync(Live.LiveDoc, root.Id)).GetObject("generation").GetObject("message").GetArray("content").GetObject(0);
                block.Set("text", S(block.Get("text")) + "lo");
            }, Context);
            await ViewTests.Drained(harness);
            var update = frames.Snapshot()[^1];
            Equal("""[["a",["docs","pi.live","generation","message","content",0,"text"],"lo"]]""", OpsJson(update.Ops), "the update");
            var rebuilt = (JsonObjectValue)DeltaApply.Apply(replacement.Value.ToJson(), update.Ops);
            SameJson("""[{"type":"text","text":"hello"}]""",
                ((JsonObjectValue)((JsonObjectValue)((JsonObjectValue)((JsonObjectValue)rebuilt["docs"])["pi.live"])["generation"])["message"])["content"], "rebuilt");
            await watch.StopAsync();
            await harness.CloseAsync(Context);
        });
    }

    /// <summary>
    /// The faux provider with each streamed piece waiting for the partial commit it makes, for at most a second, and then for
    /// <see cref="AfterPiece"/>; once a piece makes no commit, the rest stream unpaced. Upstream paces the faux stream in real time (1 ms a
    /// piece) under a 1 ms partial interval, which gives about one commit a piece on Node; .NET's timers on Windows tick every 15.6 ms, so a
    /// piece would take 15.6 ms and the cases far longer than upstream's. The cases that use it run with a partial interval of 0 instead and
    /// get one commit a piece whatever the timers.
    /// </summary>
    private sealed class CommitPaced(IDurableModels inner) : IDurableModels
    {
        private long commits;
        private TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool pacing = true;

        public long Commits => Interlocked.Read(ref commits);

        /// <summary>Runs after each paced piece's commit, with the stream held until it returns.</summary>
        public Func<StreamEvent, Task>? AfterPiece { get; set; }

        /// <summary>Called for every commit of the Harness.</summary>
        public void Committed()
        {
            Interlocked.Increment(ref commits);
            Interlocked.Exchange(ref next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        private async Task<bool> After(long seen)
        {
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (Commits <= seen)
            {
                var waiting = Volatile.Read(ref next).Task;
                if (Commits > seen) return true;
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                try { await waiting.WaitAsync(left); }
                catch (TimeoutException) { return false; }
            }
            return true;
        }

        public DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

        public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in inner.StreamSimple(model, messages, options, cancellationToken).WithCancellation(cancellationToken))
            {
                var seen = Commits;
                yield return item;
                if (!pacing || item is not (TextDelta or ThinkingDelta or ToolCallDelta)) continue;
                pacing = await After(seen);
                if (pacing && AfterPiece is { } after) await after(item);
            }
        }

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, cancellationToken);
    }

    /// <summary>A setup whose faux stream is paced on commits (see <see cref="CommitPaced"/>), and the pacer to subscribe.</summary>
    private static (ChatSetup Setup, CommitPaced Paced) PacedSetup(int tokenSize)
    {
        var setup = Setup(new FauxOptions { MinTokenSize = tokenSize, MaxTokenSize = tokenSize });
        var paced = new CommitPaced(setup.Faux);
        setup.Models = paced;
        setup.Progress = new ProgressSettings { PartialIntervalMs = 0, OutputIntervalMs = 100 };
        return (setup, paced);
    }

    /// <summary>Heap bytes in use after full collections (upstream <c>heapInUse</c>).</summary>
    private static async Task<long> HeapInUse(IHarness harness)
    {
        for (var round = 0; round < 3; round++)
        {
            await ViewTests.Drained(harness);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    // #9265
    private static IEnumerable<(string, Func<Task>)> StreamedPartials(string prefix)
    {
        yield return Case(prefix + "streams-a-tool-call-partial-as-appends-at-its-argument-paths", async () =>
        {
            var setup = Setup(new FauxOptions { TokensPerSecond = 500, MinTokenSize = 1, MaxTokenSize = 1 });
            setup.Progress = new ProgressSettings { PartialIntervalMs = 20, OutputIntervalMs = 100 };
            var arguments = Obj(("path", "src/file.ts"), ("content", string.Concat(Enumerable.Repeat("const a = 1;\n", 150))));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("missing", arguments, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var partials = PartialsOf(harness);
            var ops = new SyncList<Op>();
            var observation = await harness.ObserveConversationAsync(root.Id, new Observer<ConversationView>((call, _) =>
            {
                if (call is WatchValueCall<ConversationView> value) foreach (var op in value.Ops) ops.Add(op);
            }, _ => { }), Context);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            observation.Detach();
            string[] message = ["docs", "pi.live", "generation", "message"];
            // Operations on the in-flight message, with their paths relative to it.
            var onMessage = ops.Snapshot().SelectMany(op => op is PathOp { Path: var path } && path.Length >= message.Length &&
                message.Select((segment, index) => path[index].Key == segment).All(same => same)
                    ? [(Verb: op.Verb, Path: path[message.Length..])]
                    : Array.Empty<(string Verb, ImmutableArray<PathSegment> Path)>()).ToList();
            var argumentAppends = onMessage.Where(op => op.Verb == "a" && op.Path.Length > 2 && op.Path[2].Key == "arguments").ToList();
            Check(argumentAppends.Count > 10, $"argument appends: {argumentAppends.Count}");
            Check(argumentAppends.All(op => op.Path[^1].Key is "path" or "content"), "every append at a top-level argument");
            // The call is set whole only when a member appears (at most once per argument); growing strings are appends.
            // With the raw argument text on the block, every commit resent the whole call.
            var blockSets = onMessage.Count(op => op.Verb != "a" && op.Path.Length == 2);
            Check(blockSets <= 2, $"block sets: {blockSets}");
            // The committed partials carry only the parsed arguments, never the raw argument text.
            foreach (var partial in partials.Snapshot())
            {
                foreach (var block in ((JsonArrayValue)partial["content"]).Cast<JsonObjectValue>())
                    Equal("arguments,id,name,type", string.Join(",", block.Keys.Order(StringComparer.Ordinal)), "block members");
            }
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "commits-a-growing-text-and-tool-call-at-a-cost-that-follows-the-new-text-not-the-string-lengths", async () =>
        {
            // Bytes allocated while streaming one answer of `size` characters of text and as much tool-call content.
            static async Task<(long Bytes, long Commits)> Stream(int size)
            {
                var (setup, paced) = PacedSetup(64);
                // Keep `pi.live` cached between commits, as the default retention does. Evicted after each use (the suite's evicting run),
                // every commit replays the document's delta chain: a cost of eviction, not of appending.
                setup.DocumentRetentionMs = 60_000;
                var text = string.Concat(Enumerable.Repeat("streamed text with \"quotes\" and \\ escapes\n", size / 40));
                setup.Faux.SetResponses(
                    FauxAssistantMessage([FauxText(text), FauxToolCall("missing", Obj(("path", "a.ts"), ("content", text)), "c1")],
                        stopReason: "toolUse"),
                    FauxAssistantMessage([FauxText("done")]));
                var (harness, root) = await OpenChat(new MemoryStorage(), setup);
                using var subscription = ObserveCommits(harness, (_, _) => paced.Committed());
                // Upstream's allocatedBytes: the bytes allocated meanwhile, on every thread.
                GC.Collect();
                var before = GC.GetTotalAllocatedBytes(precise: true);
                await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
                var bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
                var commits = paced.Commits;
                await harness.CloseAsync(Context);
                return (bytes, commits);
            }
            var small = await Stream(40_000);
            var large = await Stream(400_000);
            Check(large.Commits > small.Commits * 5, $"commits: large {large.Commits}, small {small.Commits}");
            // Per commit, the cost stays flat: comparing the whole strings on every commit made it grow with their length.
            var ratio = (double)large.Bytes / large.Commits / ((double)small.Bytes / small.Commits);
            Check(ratio < 2, $"per-commit bytes, large over small: {ratio:F2} (large {large.Bytes} in {large.Commits}, small {small.Bytes} in {small.Commits})");
        });

        yield return Case(prefix + "keeps-a-watchers-memory-proportional-to-a-long-streamed-text-and-tool-call-not-to-their-commits", async () =>
        {
            var (setup, paced) = PacedSetup(16);
            static string Line(int index) => $"line {index}: some streamed source text with \"quotes\" and \\ escapes\n";
            var text = string.Concat(Enumerable.Range(0, 2_000).Select(Line));
            var content = string.Concat(Enumerable.Range(0, 2_000).Select(Line));
            setup.Faux.SetResponses(
                FauxAssistantMessage([FauxText(text), FauxToolCall("missing", Obj(("path", "a.ts"), ("content", content)), "c1")], stopReason: "toolUse"),
                FauxAssistantMessage([FauxText("done")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            using var subscription = ObserveCommits(harness, (_, _) => paced.Committed());
            var listening = await Listen(root, check: false);
            var before = await HeapInUse(harness);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            async Task<int> Shown(int index)
            {
                var live = await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context);
                if (live is null || Field(Generation(live), "message") is not JsonObjectValue message) return 0;
                var blocks = (JsonArrayValue)message["content"];
                if (index >= blocks.Count) return 0;
                var block = (JsonObjectValue)blocks[index];
                if (S(block["type"]) == "text") return S(block["text"]).Length;
                return S(block["type"]) == "toolCall" && ((JsonObjectValue)block["arguments"]).TryGetValue("content", out var shown) ? S(shown).Length : 0;
            }
            var retained = new List<long>();
            // Measure while the text, then the tool call, is almost complete: the watcher's view holds the most then. Upstream polls for it
            // while the stream goes on; the stream is held for it here (its pace is the commits', see CommitPaced).
            var raw = new[] { text.Length, Obj(("path", "a.ts"), ("content", content)).ToJson().Length };
            var streamed = new int[2];
            var measured = 0;
            paced.AfterPiece = async item =>
            {
                var (index, length) = item switch { TextDelta delta => (delta.ContentIndex, delta.Delta.Length),
                    ToolCallDelta delta => (delta.ContentIndex, delta.Delta.Length), _ => (-1, 0) };
                if (index != measured) return;
                streamed[index] += length;
                if (streamed[index] < raw[index] * 0.9 || await Shown(index) <= (index == 0 ? text.Length : content.Length) * 0.95) return;
                retained.Add(await HeapInUse(harness) - before);
                measured++;
            };
            await submission.WaitAsync(Context);
            Equal(2, retained.Count, "measures");
            var commits = listening.Frames.Count;
            Check(commits > 500, $"frames: {commits}");
            // The two 140 KB strings, the watcher's recorded frames and the rest of the stream: about 3 MB upstream. Sliced append ops made
            // the watcher's view hold a copy of the text per commit: well over 100 MB.
            foreach (var bytes in retained) Check(bytes < 20_000_000, $"retained bytes: {string.Join(", ", retained)}");
            await listening.Watch.StopAsync();
            await harness.CloseAsync(Context);
        });
    }
}
