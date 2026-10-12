// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-context.test.ts. Verbatim apart from the adaptations in
// HarnessSupport.cs and HarnessConversationsSupport.cs, and these:
// - Every case runs on the storage the group is given; countingSetup's Proxy is CountingStorage, which decorates that storage.
// - toEqual on views is SameView (deep JSON equality of head, entries, contributions and messages).
// - Expectations upstream asserts inside task handlers run there too; HandlerFailures records the first failure so the test body
//   rethrows it by name instead of reporting only a faulted task.
// - The pseudo-random transcript uses the same 32-bit LCG with uint arithmetic (Math.imul(...) >>> 0), so it draws the same sequence.
// - "keeps returned views and kept ranges apart" and "freezes kept entries deeply ...": upstream mutates a returned view's arrays and
//   expects a frozen message to throw TypeError. Here every view field is an ImmutableArray and messages are immutable JsonValues, so
//   there is no mutation to attempt (Context.cs has no freezeJson). The ports replace the returned view's arrays (`view with { ... }`, the
//   only change a caller can make) and assert the next read is unaffected, and read through a storage that hands out fresh copies of
//   every scanned record; both keep upstream's final equality checks.
// - "drops an idle conversation's context read on a timer ...": vi.useFakeTimers is ManualTimeProvider (TimeProvider and Now). Idle
//   waiters resolve on the thread pool, so the expiry timer the same idle check schedules is awaited with Eventually before the count
//   is asserted.
using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class HarnessConversationTests
{
    private sealed record ContextSetup(IHarness Harness, IConversation Root, Func<EntryDraft, Task<EntryRecord>> Append,
        Func<JsonValue, string, Task<EntryRecord>> Message);

    private static async Task<ContextSetup> Setup(IStorage storage)
    {
        var (harness, _) = await OpenHarness(storage);
        var root = await harness.RootAsync(Context);
        Task<EntryRecord> Append(EntryDraft draft) => root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, draft), Context);
        return new ContextSetup(harness, root, Append, (model, kind) => Append(Message(model, kind)));
    }

    private static Task<EntryRecord> Msg(this ContextSetup setup, JsonValue model, string kind = "message") => setup.Message(model, kind);

    private static EntryDraft Edit(params ContextEdit[] edits) => new() { Kind = "edit", Edits = [.. edits] };

    private static JsonArrayValue Messages(params JsonValue[] messages) => JsonValue.Array(messages);

    internal static IEnumerable<(string, Func<Task>)> ContextCases(string prefix, Func<IStorage> storage)
    {
        yield return Case(prefix + "returns-the-whole-transcript-without-a-head-and-excludes-model-less-entries-from-messages", async () =>
        {
            var setup = await Setup(storage());
            var first = await setup.Msg(User("hi"));
            var note = await setup.Append(new EntryDraft { Kind = "note", Data = O("""{"text":"display only"}""") });
            var answer = await setup.Msg(Assistant("hello"));
            var view = await setup.Root.ContextAsync(Context);
            Check(view.Head is null, "no head");
            Seq([first.Id, note.Id, answer.Id], Ids(view.Entries), "entries");
            Seq(["user:hi", "assistant:hello"], Described(view.Messages), "messages");
            await setup.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "excludes-aborted-error-and-deferred-assistant-messages-but-keeps-their-raw-entries", async () =>
        {
            var setup = await Setup(storage());
            await setup.Msg(User("q"));
            var aborted = await setup.Msg(Assistant("partial", stopReason: "aborted"));
            await setup.Msg(Assistant("failed", stopReason: "error"));
            await setup.Msg(Assistant("later", stopReason: "deferred"));
            await setup.Msg(Assistant("done", stopReason: "length"));
            var view = await setup.Root.ContextAsync(Context);
            Equal(5, view.Entries.Length, "entries");
            Equal(aborted.Id, view.Entries[1].Id, "aborted entry");
            Seq(["user:q", "assistant:done"], Described(view.Messages), "messages");
            await setup.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "resolves-self-heads-and-uses-the-newest-head-marker", async () =>
        {
            var setup = await Setup(storage());
            var root = setup.Root;
            await setup.Msg(User("old"));
            var reset = await setup.Append(new EntryDraft { Kind = "reset", Head = EntryHead.Self, Model = Messages(User("fresh start")) });
            Equal(reset.Id, reset.Head, "self head");
            var after = await setup.Msg(Assistant("after reset"));
            var view = await root.ContextAsync(Context);
            Equal(reset.Id, view.Head?.Id, "head");
            Seq([reset.Id, after.Id], Ids(view.Entries), "entries");
            Seq(["user:fresh start", "assistant:after reset"], Described(view.Messages), "messages");

            // A compaction summary heads an earlier kept entry; older head markers in range drop out.
            var summary = await setup.Append(new EntryDraft { Kind = "summary", Head = after.Id, Model = Messages(User("summary")) });
            var tail = await setup.Msg(User("next"));
            view = await root.ContextAsync(Context);
            Equal(summary.Id, view.Head?.Id, "summary head");
            Seq([summary.Id, after.Id, tail.Id], Ids(view.Entries), "summary entries");
            Seq(["user:summary", "assistant:after reset", "user:next"], Described(view.Messages), "summary messages");
            await setup.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "applies-the-newest-edit-per-target-within-the-active-range", async () =>
        {
            var setup = await Setup(storage());
            var root = setup.Root;
            var first = await setup.Msg(User("first"));
            var second = await setup.Msg(User("second"));
            await setup.Append(Edit(ContextEdit.Replace(first.Id, Messages(User("first v2")))));
            await setup.Append(Edit(ContextEdit.Replace(first.Id, Messages(User("first v3")))));
            await setup.Append(Edit(ContextEdit.Omit(second.Id)));
            var view = await root.ContextAsync(Context);
            Equal(5, view.Entries.Length, "entries");
            Seq(["user:first v3"], Described(view.Messages), "messages");

            // Edits before the active range no longer apply.
            var reset = await setup.Append(new EntryDraft { Kind = "reset", Head = second.Id });
            view = await root.ContextAsync(Context);
            Equal(reset.Id, view.Head?.Id, "head");
            Seq([], Described(view.Messages), "messages after reset");
            await setup.Append(Edit(ContextEdit.Replace(second.Id, Messages(User("second v2")))));
            view = await root.ContextAsync(Context);
            Seq(["user:second v2"], Described(view.Messages), "messages after edit");
            await setup.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-positional-system-messages-and-orders-tool-results-by-call-order", async () =>
        {
            var setup = await Setup(storage());
            await setup.Msg(SystemMessage(("preamble", "You help.")), "pi.system");
            await setup.Msg(User("run tools"));
            await setup.Msg(Assistant("calling", ["b", "a"]));
            await setup.Msg(ToolResult("a"));
            await setup.Append(new EntryDraft { Kind = "pi.system", Model = Messages(SystemMessage(("cwd", "/repo"))) });
            await setup.Msg(ToolResult("b"));
            await setup.Msg(ToolResult("zz"));
            await setup.Msg(Assistant("done"));
            var view = await setup.Root.ContextAsync(Context);
            Seq(["system:preamble", "user:run tools", "assistant:calling", "result:b:result b", "result:a:result a", "system:cwd", "assistant:done"],
                Described(view.Messages), "messages");
            await setup.Harness.CloseAsync(Context);
        });

        // #10542
        yield return Case(prefix + "leads-with-a-system-message-that-only-user-messages-precede-and-keeps-later-ones-in-place", async () =>
        {
            var setup = await Setup(storage());
            await setup.Msg(User("first"));
            await setup.Msg(User("steered"));
            await setup.Msg(SystemMessage(("preamble", "You help.")), "pi.system");
            await setup.Msg(Assistant("answer"));
            await setup.Msg(User("next"));
            await setup.Msg(SystemMessage(("cwd", "/repo")), "pi.system");
            var view = await setup.Root.ContextAsync(Context);
            Seq(["system:preamble", "user:first", "user:steered", "assistant:answer", "user:next", "system:cwd"], Described(view.Messages), "messages");
            // Stored order and contributions stay as committed.
            Seq(["user:first", "user:steered", "system:preamble"], Described(view.Contributions.SelectMany(messages => messages)).Take(3), "contributions");

            // After a reset, the baseline written after the handoff leads too.
            await setup.Append(new EntryDraft { Kind = "reset", Head = EntryHead.Self, Model = Messages(User("handoff")) });
            await setup.Msg(SystemMessage(("preamble", "Baseline.")), "pi.system");
            view = await setup.Root.ContextAsync(Context);
            Seq(["system:preamble", "user:handoff"], Described(view.Messages), "messages after reset");
            await setup.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "synthesizes-missing-tool-results-after-a-fork-and-drops-results-cut-from-their-call", async () =>
        {
            var setup = await Setup(storage());
            var root = setup.Root;
            await setup.Msg(User("go"));
            var call = await setup.Msg(Assistant("calling", ["x", "y"]));
            await setup.Msg(ToolResult("x"));
            var second = await setup.Msg(ToolResult("y"));
            var child = await root.ForkAsync(call.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            var childView = await child.ContextAsync(Context);
            Seq(["user:go", "assistant:calling", "result:x:error", "result:y:error"], Described(childView.Messages), "child messages");
            var missing = (JsonObjectValue)childView.Messages[2];
            Equal("toolResult", S(missing["role"]), "role");
            Equal("tool-x", S(missing["toolName"]), "tool name");
            SameJson("""{"reason":"missing_result"}""", missing["details"], "details");

            // A head between a call and its results leaves stray results that are not sent.
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "reset", Head = second.Id }), Context);
            var parentView = await root.ContextAsync(Context);
            Seq([], Described(parentView.Messages), "parent messages");
            Seq(["reset", "message"], parentView.Entries.Select(entry => entry.Kind), "parent entries");
            await setup.Harness.CloseAsync(Context);
        });

        // #10512
        yield return Case(prefix + "reads-the-context-as-of-an-earlier-entry-as-a-fork-at-that-entry-starts", async () =>
        {
            var setup = await Setup(storage());
            var root = setup.Root;
            var first = await setup.Msg(User("first"));
            var call = await setup.Msg(Assistant("calling", ["x", "y"]));
            var result = await setup.Msg(ToolResult("x"));
            await setup.Msg(ToolResult("y"));
            var edit = await setup.Append(Edit(ContextEdit.Replace(first.Id, Messages(User("first v2")))));
            var reset = await setup.Append(new EntryDraft { Kind = "reset", Head = EntryHead.Self, Model = Messages(User("fresh start")) });
            var tail = await setup.Msg(Assistant("after reset"));

            foreach (var at in new[] { first, call, result, edit, reset, tail })
            {
                var fork = await root.ForkAsync(at.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
                SameView(await fork.ContextAsync(Context), await root.ContextAsync(Context, at.Id), $"at {at.Id}");
            }
            // Stepped back before the edit and the reset: neither applies, and a cut call gets synthesized results.
            Seq(["user:first", "assistant:calling", "result:x:error", "result:y:error"], Described((await root.ContextAsync(Context, call.Id)).Messages),
                "at the call");
            SameView(await root.ContextAsync(Context), await root.ContextAsync(Context, tail.Id), "at the tail");
            SameView(await root.ContextAsync(Context), await root.ContextAsync(Context, null), "without a cutoff");

            var other = await root.ForkAsync(first.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
            await RejectsAsync(() => other.ContextAsync(Context, tail.Id), "is not visible", "invisible cutoff");
            await setup.Harness.CloseAsync(Context);
        });

        yield return Case(prefix + "extends-a-tasks-context-read-with-only-newer-entries", async () =>
        {
            var (harness, registry, root, first, scanned) = await CountingSetup(storage());
            var failures = new HandlerFailures();
            var reads = ContextTask("test.context-reads", (_, runtime, ctx) => failures.Guard(async () =>
            {
                Task Write(EntryDraft draft) => runtime.CommitAsync(async (tx, _) => { await tx.AppendEntryAsync(root.Id, draft); }, ctx);
                async Task<(ContextView View, int Rows)> Read(EntryId? at = null)
                {
                    var before = scanned.Rows;
                    var view = await runtime.ContextAsync(root.Id, ctx, at);
                    return (view, scanned.Rows - before);
                }
                var initial = await Read();
                SameView(await root.ContextAsync(ctx), initial.View, "initial");

                // Three new entries: the bounds probe reads one row, the extension the three new ones.
                await Write(Message(User("new")));
                await Write(new EntryDraft { Kind = "note", Data = O("""{"text":"display only"}""") });
                await Write(Message(Assistant("answer")));
                var extended = await Read();
                Equal(4, extended.Rows, "extended rows");
                SameView(await root.ContextAsync(ctx), extended.View, "extended");

                // A newer edit of an older entry applies to the extended range.
                await Write(Edit(ContextEdit.Omit(first.Id)));
                var edited = await Read();
                Equal(2, edited.Rows, "edited rows");
                SameView(await root.ContextAsync(ctx), edited.View, "edited");
                Check(!Described(edited.View.Messages).Contains("user:first"), "the omitted entry is gone");

                // An earlier cutoff reuses the range.
                var cutoff = await Read(extended.View.Entries[^1].Id);
                Equal(0, cutoff.Rows, "cutoff rows");
                SameView(extended.View, cutoff.View, "cutoff");

                // A new head marker changes the range: read it whole.
                await Write(new EntryDraft { Kind = "reset", Head = EntryHead.Self, Model = Messages(User("fresh")) });
                var reset = await Read();
                SameView(await root.ContextAsync(ctx), reset.View, "reset");
                Seq(["user:fresh"], Described(reset.View.Messages), "reset messages");

                await runtime.CommitAsync((_, _) => Done(), ctx);
            }));
            AddTask(registry, reads);
            var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(reads, JsonObjectValue.Empty, OwnedBy(root.Id)), Context);
            harness.Resume();
            var settled = await harness.WaitForTaskAsync(id, Context);
            failures.ThrowIfAny();
            SameJson(DoneOutcome, OutcomeOf(settled), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-a-conversations-context-read-across-its-tasks-and-for-the-retention-period-once-idle", async () =>
        {
            double clock = 1_000;
            var (harness, registry, root, _, scanned) = await CountingSetup(storage(), now: () => Volatile.Read(ref clock));
            var rows = new RowLog();
            var failures = new HandlerFailures();
            async Task Read(string name, Func<Task<ContextView>> load, DurableContext ctx)
            {
                var before = scanned.Rows;
                var view = await load();
                rows.Set(name, scanned.Rows - before);
                SameView(await root.ContextAsync(ctx), view, name);
            }
            var child = ContextTask("test.context-child", (_, runtime, ctx) => failures.Guard(async () =>
            {
                await runtime.CommitAsync(async (tx, _) => { await tx.AppendEntryAsync(root.Id, Message(User("from child"))); }, ctx);
                await Read("child", () => runtime.ContextAsync(root.Id, ctx), ctx);
                await runtime.CommitAsync((_, _) => Done(), ctx);
            }));
            var parent = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.context-parent",
                Version = 1,
                Initial = _ => O("""{"phase":"start"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["start"] = (_, runtime, ctx) => failures.Guard(async () =>
                    {
                        await Read("parent", () => runtime.ContextAsync(root.Id, ctx), ctx);
                        await runtime.CommitAsync(async (tx, _) =>
                        {
                            var childId = await tx.CreateTaskAsync(child, JsonObjectValue.Empty,
                                new TaskOptions(TaskOwnership.Task(runtime.TaskId)) { ConversationId = root.Id });
                            return (TaskState?)NextTaskState.Waiting(O("""{"phase":"after"}"""), [childId], JoinPolicy.AllSettled);
                        }, ctx);
                    }),
                    ["after"] = (_, runtime, ctx) => failures.Guard(async () =>
                    {
                        await Read("after", () => runtime.ContextAsync(root.Id, ctx), ctx);
                        await runtime.CommitAsync((_, _) => Done(), ctx);
                    }),
                },
                Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedState(), ctx),
            });
            var probeTask = ProbeTask((name, load, ctx) => failures.Guard(() => Read(name, load, ctx)));
            foreach (var task in new[] { child, parent, probeTask }) AddTask(registry, task);
            harness.Resume();
            var parentId = await harness.CommitAsync(tx => tx.CreateTaskAsync(parent, JsonObjectValue.Empty, OwnedBy(root.Id)), Context);
            var parentRecord = await harness.WaitForTaskAsync(parentId, Context);
            failures.ThrowIfAny();
            SameJson(DoneOutcome, OutcomeOf(parentRecord), "parent outcome");
            await root.WaitForIdleAsync(Context);
            async Task Probe(string name)
            {
                var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(probeTask, Obj(("name", name)), OwnedBy(root.Id)), Context);
                var record = await harness.WaitForTaskAsync(id, Context);
                failures.ThrowIfAny();
                SameJson(DoneOutcome, OutcomeOf(record), name + " outcome");
                await root.WaitForIdleAsync(Context);
            }
            await Probe("idle");
            Volatile.Write(ref clock, clock + 600_000);
            await Probe("expired");
            // Bounds probes read one row each. The child scans its own new entry; the waiting parent's later invocation and the probe
            // within the retention period scan nothing new. After it, the probe reads the whole transcript again.
            SameRows([("parent", 22), ("child", 2), ("after", 1), ("idle", 1), ("expired", 23)], rows, "rows");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "derives-the-same-view-from-extended-reads-as-from-whole-reads", async () =>
        {
            var (harness, registry, root, _, _) = await CountingSetup(storage());
            // Deterministic pseudo-random transcript (32-bit LCG, high bits): calls and results in any order, missing and stray results,
            // excluded stop reasons, system entries, notes, edits of earlier entries, and head markers.
            uint seed = 7;
            int Random(int n)
            {
                seed = unchecked(seed * 1_664_525u + 1_013_904_223u);
                return (int)Math.Floor(seed / 4294967296.0 * n);
            }
            var seen = new ConcurrentDictionary<string, int>();
            void Count(string category) => seen.AddOrUpdate(category, 1, (_, count) => count + 1);
            var failures = new HandlerFailures();
            var steps = ContextTask("test.context-steps", (_, runtime, ctx) => failures.Guard(async () =>
            {
                var written = new List<EntryRecord>();
                var calls = new List<string>();
                var next = 0;
                EntryDraft Draft()
                {
                    var pick = Random(14);
                    if (pick < 3)
                    {
                        Count("user");
                        return Message(User($"u{next++}"));
                    }
                    if (pick < 5)
                    {
                        var length = Random(3);
                        var ids = Enumerable.Range(0, length).Select(_ => $"c{next++}").ToList();
                        calls.AddRange(ids);
                        string?[] reasons = ["aborted", "error", "deferred"];
                        var index = Random(9);
                        var stopReason = index < reasons.Length ? reasons[index] : null;
                        Count(stopReason ?? "assistant");
                        return Message(Assistant($"a{next++}", ids, stopReason));
                    }
                    if (pick < 8)
                    {
                        var matched = calls.Count > 0 && Random(5) > 0;
                        Count(matched ? "result" : "stray result");
                        string id;
                        if (matched)
                        {
                            var at = Random(calls.Count);
                            id = calls[at];
                            calls.RemoveAt(at);
                        }
                        else id = $"stray{next++}";
                        return Message(ToolResult(id));
                    }
                    if (pick < 9)
                    {
                        Count("system");
                        return Message(SystemMessage(("s", $"v{next++}")));
                    }
                    if (pick < 10)
                    {
                        Count("note");
                        return new EntryDraft { Kind = "note", Data = Obj(("n", next++)) };
                    }
                    var targetIndex = Random(written.Count);
                    var target = targetIndex < written.Count ? written[targetIndex] : null;
                    if (pick < 12 && target is not null)
                    {
                        var omit = Random(2) == 0;
                        Count(omit ? "omit" : "replace");
                        return Edit(omit ? ContextEdit.Omit(target.Id) : ContextEdit.Replace(target.Id, Messages(User($"r{next++}"))));
                    }
                    if (pick < 13 && target is not null)
                    {
                        Count("head");
                        return new EntryDraft { Kind = "summary", Head = target.Id, Model = Messages(User($"h{next++}")) };
                    }
                    Count("user");
                    return Message(User($"u{next++}"));
                }
                Task Append(ConversationId conversationId) => runtime.CommitAsync(async (tx, _) =>
                {
                    var added = 1 + Random(3);
                    for (var index = 0; index < added; index++) written.Add(await tx.AppendEntryAsync(conversationId, Draft()));
                }, ctx);
                for (var step = 0; step < 150; step++)
                {
                    await Append(root.Id);
                    SameView(await root.ContextAsync(ctx), await runtime.ContextAsync(root.Id, ctx), $"step {step}");
                    if (step % 10 == 9)
                    {
                        // Overlapping reads, one with an earlier cutoff, share the kept range.
                        var at = written[Random(written.Count)].Id;
                        var whole = runtime.ContextAsync(root.Id, ctx);
                        var cut = runtime.ContextAsync(root.Id, ctx, at);
                        await Task.WhenAll(whole, cut);
                        SameView(await root.ContextAsync(ctx), await whole, $"step {step} whole");
                        SameView(await root.ContextAsync(ctx, at), await cut, $"step {step} cut at {at}");
                    }
                }
                // A fork sees its parent's entries through the fork point and extends with its own.
                var fork = await root.ForkAsync(written[Random(written.Count)].Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), ctx);
                for (var step = 0; step < 20; step++)
                {
                    SameView(await fork.ContextAsync(ctx), await runtime.ContextAsync(fork.Id, ctx), $"fork step {step}");
                    await Append(fork.Id);
                }
                await runtime.CommitAsync((_, _) => Done(), ctx);
            }));
            AddTask(registry, steps);
            harness.Resume();
            var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(steps, JsonObjectValue.Empty, OwnedBy(root.Id)), Context);
            var record = await harness.WaitForTaskAsync(id, Context);
            failures.ThrowIfAny();
            SameJson(DoneOutcome, OutcomeOf(record), "outcome");
            string[] categories = ["user", "assistant", "aborted", "error", "deferred", "result", "stray result", "system", "note"];
            foreach (var category in categories.Concat(["omit", "replace", "head"]))
                Check(seen.GetValueOrDefault(category) > 0, $"category {category} was drawn");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-returned-views-and-kept-ranges-apart", async () =>
        {
            var (harness, registry, root, _, _) = await CountingSetup(storage());
            var failures = new HandlerFailures();
            var mutate = ContextTask("test.context-mutate", (_, runtime, ctx) => failures.Guard(async () =>
            {
                var view = await runtime.ContextAsync(root.Id, ctx);
                // The only change a caller can make: replace the view's arrays (upstream empties messages and pops an entry).
                var changed = view with { Messages = [], Entries = view.Entries.RemoveAt(view.Entries.Length - 1) };
                Equal(0, changed.Messages.Length, "changed messages");
                // Kept entries are immutable records and JSON values: the contributions keep their committed content.
                Equal("first", S(((JsonObjectValue)view.Contributions[0][0])["content"]), "kept contribution");
                SameView(await root.ContextAsync(ctx), await runtime.ContextAsync(root.Id, ctx), "next read");
                await runtime.CommitAsync((_, _) => Done(), ctx);
            }));
            AddTask(registry, mutate);
            harness.Resume();
            var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(mutate, JsonObjectValue.Empty, OwnedBy(root.Id)), Context);
            var record = await harness.WaitForTaskAsync(id, Context);
            failures.ThrowIfAny();
            SameJson(DoneOutcome, OutcomeOf(record), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "freezes-kept-entries-deeply-even-when-storage-freezes-them-shallowly", async () =>
        {
            // Fresh copies of every scanned record; upstream freezes only the record itself, leaving its messages mutable.
            var counting = new CountingStorage(storage())
            {
                MapScan = page => page with { Items = [.. page.Items.Select(item => EntryRecord.FromJson(JsonValue.Parse(item.ToJson().ToJson())))] },
            };
            var registry = Registry.Create();
            var harness = await PiSharp.Durable.Harness.OpenAsync(counting, new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);
            var root = await harness.RootAsync(Context);
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, Message(User("hello"))), Context);
            var failures = new HandlerFailures();
            var mutate = ContextTask("test.context-shallow", (_, runtime, ctx) => failures.Guard(async () =>
            {
                var view = await runtime.ContextAsync(root.Id, ctx);
                // A message is an immutable JsonObjectValue: it has no setter, so it keeps its content.
                Check(view.Messages[0] is JsonObjectValue, "an immutable message");
                Equal("hello", S(((JsonObjectValue)view.Messages[0])["content"]), "message content");
                SameView(await root.ContextAsync(ctx), view, "view");
                await runtime.CommitAsync((_, _) => Done(), ctx);
            }));
            AddTask(registry, mutate);
            harness.Resume();
            var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(mutate, JsonObjectValue.Empty, OwnedBy(root.Id)), Context);
            var record = await harness.WaitForTaskAsync(id, Context);
            failures.ThrowIfAny();
            SameJson(DoneOutcome, OutcomeOf(record), "outcome");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "does-not-keep-a-read-that-finishes-after-its-task-ended", async () =>
        {
            var (harness, registry, root, _, scanned) = await CountingSetup(storage(), settings: new HarnessSettings { ContextRetentionMs = 0 });
            var scans = new HarnessTests.Deferred();
            var probeStart = new HarnessTests.Deferred();
            Task<object?>? late = null;
            var lateTask = ContextTask("test.context-late", async (_, runtime, ctx) =>
            {
                scanned.HoldNextScan = scans.Task;
                late = runtime.ContextAsync(root.Id, ctx).ContinueWith(read => read.IsFaulted ? (object?)read.Exception!.InnerException : read.Result,
                    TaskScheduler.Default);
                await runtime.CommitAsync((_, _) => Done(), ctx);
            });
            var rows = new RowLog();
            var probeTask = ProbeTask(async (name, load, _) =>
            {
                await probeStart.Task;
                var before = scanned.Rows;
                await load();
                rows.Set(name, scanned.Rows - before);
            });
            foreach (var task in new[] { lateTask, probeTask }) AddTask(registry, task);
            harness.Resume();
            var lateId = await harness.CommitAsync(tx => tx.CreateTaskAsync(lateTask, JsonObjectValue.Empty, OwnedBy(root.Id)), Context);
            SameJson(DoneOutcome, OutcomeOf(await harness.WaitForTaskAsync(lateId, Context)), "late outcome");
            // The conversation is busy again when the late read finishes; its range must not be kept.
            var probeId = await harness.CommitAsync(tx => tx.CreateTaskAsync(probeTask, Obj(("name", "probe")), OwnedBy(root.Id)), Context);
            scans.Resolve();
            await late!;
            probeStart.Resolve();
            SameJson(DoneOutcome, OutcomeOf(await harness.WaitForTaskAsync(probeId, Context)), "probe outcome");
            SameRows([("probe", 22)], rows, "rows");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-an-idle-conversations-context-read-on-a-timer-when-nothing-else-runs", async () =>
        {
            var time = new ManualTimeProvider();
            var (harness, registry, root, _, scanned) = await CountingSetup(storage(), settings: new HarnessSettings { ContextRetentionMs = 1_000 },
                now: time.NowMs, timeProvider: time);
            var rows = new RowLog();
            var probeTask = ProbeTask(async (name, load, _) =>
            {
                var before = scanned.Rows;
                await load();
                rows.Set(name, scanned.Rows - before);
            });
            AddTask(registry, probeTask);
            harness.Resume();
            async Task Probe(string name)
            {
                var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(probeTask, Obj(("name", name)), OwnedBy(root.Id)), Context);
                SameJson(DoneOutcome, OutcomeOf(await harness.WaitForTaskAsync(id, Context)), name + " outcome");
                await root.WaitForIdleAsync(Context);
            }
            var idle = time.TimerCount;
            await Probe("first");
            // The idle check that resolves the wait also schedules the expiry; the waiter continues on the thread pool (see the header).
            await Eventually(() => time.TimerCount == idle + 1);
            Equal(idle + 1, time.TimerCount, "timers once idle");
            time.Advance(TimeSpan.FromMilliseconds(1_000));
            Equal(idle, time.TimerCount, "timers after expiry");
            await Probe("second");
            SameRows([("first", 22), ("second", 22)], rows, "rows");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "drops-a-conversations-context-read-once-idle-with-zero-retention", async () =>
        {
            var (harness, registry, root, _, scanned) = await CountingSetup(storage(), settings: new HarnessSettings { ContextRetentionMs = 0 });
            var rows = new RowLog();
            var probeTask = ProbeTask(async (name, load, _) =>
            {
                var before = scanned.Rows;
                await load();
                rows.Set(name, scanned.Rows - before);
            });
            AddTask(registry, probeTask);
            harness.Resume();
            foreach (var name in new[] { "first", "second" })
            {
                var id = await harness.CommitAsync(tx => tx.CreateTaskAsync(probeTask, Obj(("name", name)), OwnedBy(root.Id)), Context);
                SameJson(DoneOutcome, OutcomeOf(await harness.WaitForTaskAsync(id, Context)), name + " outcome");
                await root.WaitForIdleAsync(Context);
            }
            SameRows([("first", 22), ("second", 22)], rows, "rows");
            await harness.CloseAsync(Context);
        });
    }
}
