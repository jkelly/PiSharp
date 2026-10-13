// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-prompt.test.ts (describes "system prompt preparation" and
// "tool loadout preparation"). Verbatim apart from the adaptations in HarnessSupport.cs, and these:
// - Backends. Upstream runs these on MemoryStorage. harness.prompt.* does the same; harness.prompt.jsonl.* and harness.prompt.sqlite.* rerun
//   the cases that plan against a stored conversation on a fresh JSONL directory and a fresh SQLite file. The three cases that use no
//   storage run once. Each case closes its Harness at the end.
// - Section maps are ordered (key, text) pairs: `[...map]` and `Object.entries(desired)` compare as sequences of pairs.
// - pi-ai's TypeBox `Type.Object({})` is its JSON Schema `{"type":"object","properties":{}}`.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessPromptCases() =>
    [
        .. PromptTests.StorageFreeCases("harness.prompt."),
        .. PromptTests.StorageCases("harness.prompt.", () => new MemoryStorage()),
        .. PromptTests.StorageCases("harness.prompt.jsonl.",
            () => JsonlStorage.OpenAsync(Temp("prompt-jsonl"), DurableContext.Background).GetAwaiter().GetResult()),
        .. PromptTests.StorageCases("harness.prompt.sqlite.",
            () => SqliteStorage.OpenAsync(Path.Combine(Temp("prompt-sqlite"), "session.sqlite")).GetAwaiter().GetResult()),
    ];
}

/// <summary>The harness.prompt.* cases (upstream harness-prompt.test.ts).</summary>
internal static class PromptTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly PromptInput Input = new()
    {
        ConversationId = new ConversationId(1),
        Agent = new Agent { ThinkingLevel = "off", Extensions = [], Tools = [], Sections = [] },
        Env = null,
        Shown = new Dictionary<string, string>(),
        Read = NullReader.Instance,
    };

    private static KeyValuePair<string, string> Pair(string key, string value) => KeyValuePair.Create(key, value);

    private static string PairsText(IEnumerable<KeyValuePair<string, string>> pairs) => string.Join(", ", pairs.Select(pair => $"[{pair.Key}, {pair.Value}]"));

    private static void SamePairs(IEnumerable<KeyValuePair<string, string>> expected, IEnumerable<KeyValuePair<string, string>> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected {PairsText(expected)}, actual {PairsText(actual)}");

    /// <summary>Plans against the current context, appends the plan, and checks that replay then yields <paramref name="desired"/> in order
    /// (upstream <c>apply</c>). Returns each entry as <c>{sections, omit?}</c>.</summary>
    private static async Task<JsonArrayValue> Apply(IConversation conversation, params KeyValuePair<string, string>[] desired)
    {
        var drafts = HarnessPrompt.PlanSystemEntries(await conversation.ContextAsync(Context), desired, Array.Empty<JsonObjectValue>(), 7);
        await conversation.CommitAsync(async tx =>
        {
            foreach (var draft in drafts) await tx.AppendEntryAsync(Entries.SystemEntry, conversation.Id, draft);
        }, Context);
        var replayed = HarnessPrompt.ReplaySections((await conversation.ContextAsync(Context)).Messages);
        SamePairs(desired, replayed, "replayed sections");
        return JsonValue.Array(drafts.Select(draft =>
        {
            var message = (JsonObjectValue)draft.Model![0];
            ChatTests.MatchJson("""{"role":"system","content":"","timestamp":7}""", message, "planned message");
            var sections = message["sections"];
            if (draft.Edits is not { } edits) return (JsonValue)Obj(("sections", sections));
            foreach (var edit in edits) Equal(ContextEditAction.Omit, edit.Action, "edit action");
            return Obj(("sections", sections), ("omit", JsonValue.Array(edits.Select(edit => (JsonValue)edit.Target.ToJson()))));
        }));
    }

    private static async Task<(IHarness Harness, IConversation Root)> Root(Func<IStorage> fresh)
    {
        var (harness, _) = await OpenHarness(fresh());
        return (harness, await harness.RootAsync(Context));
    }

    private static async Task<EntryId> LastSystemId(IConversation conversation)
    {
        var page = await conversation.EntriesAsync(new EntryQueryBounds(), 100, null, Context);
        return page.Items.First(entry => entry.Kind == "pi.system").Id;
    }

    private static Task<EntryId> Marker(IConversation conversation, EntryHead head) =>
        conversation.CommitAsync(async tx => (await tx.AppendEntryAsync(conversation.Id,
            new EntryDraft { Kind = "summary", Head = head, Model = JsonValue.Array(User("summary")) })).Id, Context);

    private static PromptSection Section(string key, PromptRender render, bool? tag = null) => new() { Key = key, Render = render, Tag = tag };

    private static ValueTask<string?> Text(string? text) => ValueTask.FromResult(text);

    /// <summary>A pi-ai tool declaration (upstream <c>declaration</c>).</summary>
    private static JsonObjectValue Declaration(string name, string? description = null) =>
        Obj(("name", name), ("description", description ?? name), ("parameters", O("""{"type":"object","properties":{}}""")));

    /// <summary>Plans tools only, appends the plan, checks that replay offers <paramref name="tools"/> in order, and returns each message's
    /// changes as <c>{removed?, added?, sections?}</c> (upstream <c>applyTools</c>).</summary>
    private static async Task<JsonArrayValue> ApplyTools(IConversation conversation, IReadOnlyList<JsonObjectValue> tools,
        params KeyValuePair<string, string>[] sections)
    {
        var drafts = HarnessPrompt.PlanSystemEntries(await conversation.ContextAsync(Context), sections, tools, 7);
        await conversation.CommitAsync(async tx =>
        {
            foreach (var draft in drafts) await tx.AppendEntryAsync(Entries.SystemEntry, conversation.Id, draft);
        }, Context);
        var offered = PiAiTranscript.GetCurrentTools((await conversation.ContextAsync(Context)).Messages);
        SameJson(JsonValue.Array(tools.Select(tool => (JsonValue)PiAiTranscript.ToToolDeclaration(tool))).ToJson(), JsonValue.Array(offered), "offered tools");
        return JsonValue.Array(drafts.Select(draft =>
        {
            var message = (JsonObjectValue)draft.Model![0];
            var changes = new List<(string, JsonValue)>();
            if (message.TryGetValue("toolsRemoved", out var removed))
                changes.Add(("removed", JsonValue.Array(((JsonArrayValue)removed).Select(tool => ((JsonObjectValue)tool)["name"]))));
            if (message.TryGetValue("toolsAdded", out var added))
                changes.Add(("added", JsonValue.Array(((JsonArrayValue)added).Select(tool => ((JsonObjectValue)tool)["name"]))));
            if (message.TryGetValue("sections", out var planned)) changes.Add(("sections", planned));
            return (JsonValue)Obj([.. changes]);
        }));
    }

    internal static IEnumerable<(string, Func<Task>)> StorageFreeCases(string prefix)
    {
        yield return Case(prefix + "renders-sections-in-order-with-tags-omissions-wrappers-and-failures", async () =>
        {
            var registry = PiSharp.Durable.Registry.Create();
            AddSection(registry, "preamble", (_, _) => Text("You are helpful."), tag: false);
            AddSection(registry, "cwd", async (_, _) => { await Task.Yield(); return "/repo"; });
            AddSection(registry, "skipped", (_, _) => Text(null));
            AddSection(registry, "failing", (_, _) => throw new InvalidOperationException("render failed"));
            AddSection(registry, "new-failing", (_, _) => throw new InvalidOperationException("also failed"));
            registry.Install(Define.Extension(new Extension
            {
                Name = "git",
                Wraps = [Define.WrapSection("cwd", inner => inner with
                {
                    Render = async (value, ctx) => $"{await inner.Render(value, ctx)} (git)",
                })],
            }));
            var reports = new List<Exception>();
            var shown = new Dictionary<string, string> { ["failing"] = "<failing>\nold\n</failing>", ["cwd"] = "stale" };
            var agent = Agents.ResolveAgent(null, registry.Snapshot(), Agents.ResolveSettings(null), error => throw error);
            var desired = await HarnessPrompt.RenderSectionsAsync(agent.Sections, Input, shown, reports.Add, Context);
            SamePairs([Pair("preamble", "You are helpful."), Pair("cwd", "<cwd>\n/repo (git)\n</cwd>"), Pair("failing", "<failing>\nold\n</failing>")],
                desired, "desired");
            Seq(["render failed", "also failed"], reports.Select(error => error.Message), "reports");
        });

        yield return Case(prefix + "propagates-section-errors-after-cancellation", async () =>
        {
            var controller = Context.WithCancel();
            controller.Cancel(new InvalidOperationException("cancelled"));
            var failing = Section("a", (_, _) => throw new InvalidOperationException("cancelled"));
            await RejectsAsync(() => HarnessPrompt.RenderSectionsAsync([failing], Input, new Dictionary<string, string>(), _ => { }, controller.Context),
                "cancelled", "render");
        });

        yield return Case(prefix + "replays-sections-in-place-deletes-on-null-and-appends-re-additions", () =>
        {
            static JsonObjectValue System(params (string Key, string? Text)[] sections) => Obj(("role", "system"), ("content", ""),
                ("sections", JsonValue.Object(sections.Select(section => KeyValuePair.Create(section.Key, section.Text is null ? (JsonValue)JsonValue.Null : section.Text)))),
                ("timestamp", 1));
            var shown = HarnessPrompt.ReplaySections([System(("a", "1"), ("b", "2"), ("c", "3")), User("x"), System(("b", "20"), ("a", null)), System(("a", "10"))]);
            SamePairs([Pair("b", "20"), Pair("c", "3"), Pair("a", "10")], shown, "shown");
        });
    }

    internal static IEnumerable<(string, Func<Task>)> StorageCases(string prefix, Func<IStorage> fresh)
    {
        yield return Case(prefix + "emits-minimal-value-patches-removals-and-additions", async () =>
        {
            var (harness, conversation) = await Root(fresh);
            SameJson("""[{"sections":{"a":"1","b":"2","c":"3"}}]""", await Apply(conversation, Pair("a", "1"), Pair("b", "2"), Pair("c", "3")), "first");
            SameJson("""[{"sections":{"b":"20","d":"4"}}]""", await Apply(conversation, Pair("a", "1"), Pair("b", "20"), Pair("c", "3"), Pair("d", "4")), "patch");
            SameJson("""[{"sections":{"b":null}}]""", await Apply(conversation, Pair("a", "1"), Pair("c", "3"), Pair("d", "4")), "removal");
            SameJson("[]", await Apply(conversation, Pair("a", "1"), Pair("c", "3"), Pair("d", "4")), "unchanged");
            SameJson("""[{"sections":{"a":null,"c":null,"d":null}}]""", await Apply(conversation), "all removed");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "rewrites-order-only-changes-and-re-additions-as-two-entries", async () =>
        {
            var (harness, conversation) = await Root(fresh);
            await Apply(conversation, Pair("a", "1"), Pair("b", "2"));
            SameJson("""[{"sections":{"a":null,"b":null}},{"sections":{"b":"2","a":"1"}}]""", await Apply(conversation, Pair("b", "2"), Pair("a", "1")), "reorder");
            await harness.CloseAsync(Context);
            var (other, readded) = await Root(fresh);
            await Apply(readded, Pair("a", "1"), Pair("b", "2"), Pair("c", "3"));
            SameJson("""[{"sections":{"b":null}}]""", await Apply(readded, Pair("a", "1"), Pair("c", "3")), "removal");
            // Patching would append `b` after `c`.
            SameJson("""[{"sections":{"a":null,"c":null}},{"sections":{"a":"1","b":"2","c":"3"}}]""",
                await Apply(readded, Pair("a", "1"), Pair("b", "2"), Pair("c", "3")), "re-addition");
            await other.CloseAsync(Context);
        });

        yield return Case(prefix + "rebaselines-after-a-head-marker-omitting-retained-deltas-on-both-sides-of-it", async () =>
        {
            var (harness, conversation) = await Root(fresh);
            await Apply(conversation, Pair("a", "1"), Pair("b", "2"));
            await conversation.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "pi.user", Model = JsonValue.Array(User("hi")) }),
                Context);
            await Apply(conversation, Pair("a", "1"), Pair("b", "20"));
            var delta = await LastSystemId(conversation);
            // The head keeps the delta but cuts its baseline: replay alone would show only `b`.
            await Marker(conversation, delta);
            SameJson($$"""[{"sections":{"a":"1","b":"20"},"omit":[{{delta}}]}]""", await Apply(conversation, Pair("a", "1"), Pair("b", "20")), "rebaseline");
            var baseline = await LastSystemId(conversation);
            // A system entry follows the marker now, so later changes are ordinary patches.
            SameJson("""[{"sections":{"b":"21"}}]""", await Apply(conversation, Pair("a", "1"), Pair("b", "21")), "patch");
            var after = await LastSystemId(conversation);
            // A second marker keeps deltas from both sides of the first one.
            await Marker(conversation, delta);
            SameJson($$"""[{"sections":{"a":"1","b":"21"},"omit":[{{delta}},{{baseline}},{{after}}]}]""",
                await Apply(conversation, Pair("a", "1"), Pair("b", "21")), "second rebaseline");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "writes-a-complete-post-head-baseline-even-when-replay-already-matches", async () =>
        {
            var (harness, conversation) = await Root(fresh);
            await Apply(conversation, Pair("a", "1"));
            var baseline = await LastSystemId(conversation);
            await Marker(conversation, baseline);
            SameJson($$"""[{"sections":{"a":"1"},"omit":[{{baseline}}]}]""", await Apply(conversation, Pair("a", "1")), "rebaseline");
            await Marker(conversation, EntryHead.Self);
            SameJson("""[{"sections":{}}]""", await Apply(conversation), "empty baseline");
            SameJson("[]", await Apply(conversation), "unchanged");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "adds-removes-replaces-changed-declarations-and-rewrites-the-order-when-needed", async () =>
        {
            var (harness, conversation) = await Root(fresh);
            var (a, b, c) = (Declaration("a"), Declaration("b"), Declaration("c"));
            SameJson("""[{"added":["a","b"]}]""", await ApplyTools(conversation, [a, b]), "add");
            SameJson("[]", await ApplyTools(conversation, [a, b]), "unchanged");
            SameJson("""[{"added":["c"]}]""", await ApplyTools(conversation, [a, b, c]), "append");
            SameJson("""[{"removed":["b"]}]""", await ApplyTools(conversation, [a, c]), "remove");
            // A changed declaration at the end is removed and re-added in place.
            var c2 = Declaration("c", "changed");
            SameJson("""[{"removed":["c"],"added":["c"]}]""", await ApplyTools(conversation, [a, c2]), "replace last");
            // A changed declaration in the middle would move to the end, so the whole order is rewritten.
            var a2 = Declaration("a", "changed");
            SameJson("""[{"removed":["a","c"],"added":["a","c"]}]""", await ApplyTools(conversation, [a2, c2]), "replace first");
            // Order-only change.
            SameJson("""[{"removed":["a","c"],"added":["c","a"]}]""", await ApplyTools(conversation, [c2, a2]), "reorder");
            SameJson("""[{"removed":["c","a"]}]""", await ApplyTools(conversation, []), "remove all");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "puts-tool-changes-on-the-last-section-entry-and-re-declares-every-tool-after-a-head-cut", async () =>
        {
            var (harness, conversation) = await Root(fresh);
            var (a, b) = (Declaration("a"), Declaration("b"));
            SameJson("""[{"added":["a"],"sections":{"x":"1","y":"2"}}]""", await ApplyTools(conversation, [a], Pair("x", "1"), Pair("y", "2")), "first");
            // Section order changes need two entries; the tool change rides on the second.
            SameJson("""[{"sections":{"x":null,"y":null}},{"added":["b"],"sections":{"y":"2","x":"1"}}]""",
                await ApplyTools(conversation, [a, b], Pair("y", "2"), Pair("x", "1")), "reorder");
            await Marker(conversation, EntryHead.Self);
            SameJson("""[{"added":["a","b"],"sections":{"y":"2","x":"1"}}]""", await ApplyTools(conversation, [a, b], Pair("y", "2"), Pair("x", "1")), "after cut");
            await harness.CloseAsync(Context);
        });
    }
}
