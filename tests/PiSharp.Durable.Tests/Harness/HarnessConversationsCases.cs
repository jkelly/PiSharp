// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-conversations.test.ts. Verbatim apart from the
// adaptations in HarnessSupport.cs and HarnessConversationsSupport.cs, and these:
// - Every case runs on the storage the group is given (ControlledStorage-wrapped where upstream uses ControlledStorage); "keeps root and
//   conversation identity and state across reopen" runs on a reopenable store (a SQLite file, as upstream, and a JSONL directory), and is
//   skipped by the plain SQLite group, whose factory opens a fresh file per call. Storage closes with the Harness before every reopen.
// - `init: () => expect.unreachable()` is an init that throws.
// - The NoteDoc text and the conversationCreated hook read the document draft; `${agent.cwd}` of an unset cwd is "undefined", as in
//   JavaScript.
// - "paginates fork-aware history ...": upstream passes a query with a foreign conversationId through `as never` to show it is ignored.
//   EntryQueryBounds has no conversation field, so the bounds query cannot name one; the bounded scan is otherwise the same.
// - toMatchObject over tools compares tool registrations; the stored agent is pi.agent JSON (deep equality, not key order).
// - Expectations upstream asserts inside init callbacks and transactions still run there: a failure throws, which fails the commit and
//   the case.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Chord.Delta;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class HarnessConversationTests
{
    private static readonly System.Text.RegularExpressions.Regex UuidV7 =
        new("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$");

    private static readonly RewindableConversationDocToken NoteDoc = Documents.DefineRewindableConversationDoc("test.note", 1, DocumentFork.AsOf,
        () => O("""{"text":""}"""));

    private static readonly Entry MessageEntry = Entries.DefineEntry("message");

    private static Task<EntryRecord> Append(IConversation conversation, string text) =>
        conversation.CommitAsync(tx => tx.AppendEntryAsync(conversation.Id, Message(User(text))), Context);

    /// <summary>Every entry's first user text, two per page (upstream <c>allEntries</c>).</summary>
    private static async Task<List<string>> AllEntries(IConversation conversation, ScanOrder? order = null)
    {
        var texts = new List<string>();
        JsonObjectValue? cursor = null;
        do
        {
            var page = await conversation.EntriesAsync(new EntryQueryBounds { Order = order }, 2, cursor, Context);
            foreach (var entry in page.Items) texts.Add(S(((JsonObjectValue)entry.Model![0])["content"]));
            cursor = page.Next;
        } while (cursor is not null);
        return texts;
    }

    private static async Task<string?> ProviderSessionId(ISession session, ConversationId id) =>
        (await session.SnapshotAsync(ProviderIdentity.ProviderDoc, id, Context)) is { } provider && provider.TryGetValue("sessionId", out var value)
            ? S(value)
            : null;

    private static void IsUuidV7(string? value, string what) => Check(value is not null && UuidV7.IsMatch(value), $"{what}: expected a UUIDv7, actual {value}");

    private static string Text(DraftObject note) => S(note.Get("text"));

    private static ConversationCreateOptions Ownerless => new(ConversationOwnership.Ownerless);

    private static List<string> ToolNames(Agent agent) => [.. agent.Tools.Select(each => each.Name)];

    private static Task Unreachable(ITx tx, ConversationId id) => throw new InvalidOperationException("init must not run");

    /// <summary>
    /// The harness.conversations.* cases on storages from <paramref name="storage"/>. <paramref name="reopenable"/> gives a fresh location
    /// whose factory opens the same store each call; null skips the reopen case.
    /// </summary>
    internal static IEnumerable<(string, Func<Task>)> ConversationCases(string prefix, Func<IStorage> storage, Func<Func<IStorage>>? reopenable)
    {
        ControlledStorage Controlled() => new(storage());

        // ─── Harness root and conversations ───

        yield return Case(prefix + "creates-the-root-lazily-with-its-agent-change-and-init-in-one-commit", async () =>
        {
            var controlled = Controlled();
            var (harness, _) = await OpenHarness(controlled, ["read", "bash"]);
            Equal(0, controlled.Commits.Count, "commits before root");
            var root = await harness.RootAsync(Context, new AgentChange { ThinkingLevel = "high" }, async (tx, id) =>
            {
                // The agent change applied before init.
                Equal("high", S((await tx.DocAsync(Agents.AgentDoc, id)).Get("thinkingLevel")), "agent in init");
                (await tx.DocAsync(NoteDoc, id)).Set("text", "root note");
            });
            Equal(ConversationId.Root, root.Id, "root id");
            Equal(1, controlled.Commits.Count, "commits");
            // Conversation, five built-in documents, and the init note.
            Seq(["conversation", "document.create", "document.create", "document.create", "document.create", "document.create", "document.create"],
                controlled.Commits[0].Select(write => write.Type), "write types");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            var provider = await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, root.Id, Context);
            Seq(["sessionId"], provider!.Keys, "provider keys");
            IsUuidV7(S(provider["sessionId"]), "provider session");
            SameJson("""{"thinkingLevel":"high"}""", await harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context), "agent");
            var agent = await root.AgentAsync(Context);
            Equal("high", agent.ThinkingLevel, "thinking level");
            Seq(["read", "bash"], ToolNames(agent), "tools");
            SameJson("""{"text":"root note"}""", await harness.SnapshotAsync(NoteDoc, root.Id, Context), "note");

            var again = await harness.RootAsync(Context, new AgentChange { ThinkingLevel = "low" }, Unreachable);
            Equal(root.Id, again.Id, "again");
            Equal(1, controlled.Commits.Count, "commits after again");
            await harness.CloseAsync(Context);
        });

        if (reopenable is not null)
        {
            yield return Case(prefix + "keeps-root-and-conversation-identity-and-state-across-reopen", async () =>
            {
                var open = reopenable();
                var first = await OpenHarness(open(), ["read"]);
                var root = await first.Harness.RootAsync(Context);
                await root.ConfigureAsync(new AgentChange { Model = new ModelRef("anthropic", "claude"), Cwd = "/repo" }, Context);
                var entry = await Append(root, "hello");
                var child = await first.Harness.CreateConversationAsync(Ownerless, Context);
                var fork = await root.ForkAsync(entry.Id, Ownerless, Context);
                var providerSessionIds = new List<string?>();
                foreach (var conversation in new[] { root, child, fork }) providerSessionIds.Add(await ProviderSessionId(first.Harness, conversation.Id));
                // Regression coverage for #10424: a fork must not inherit its parent's provider identity.
                foreach (var sessionId in providerSessionIds) IsUuidV7(sessionId, "provider session");
                Equal(3, providerSessionIds.Distinct().Count(), "distinct provider sessions");
                await first.Harness.CloseAsync(Context);
                await ThrowsAsync<Exception>(() => root.AgentAsync(Context), "agent after close");

                // The new process installs nothing: the stored choices survive, the tools do not resolve.
                first = await OpenHarness(open(), []);
                var reopened = await first.Harness.RootAsync(Context, null, Unreachable);
                Equal(ConversationId.Root, reopened.Id, "reopened root");
                var agent = await reopened.AgentAsync(Context);
                Equal(new ModelRef("anthropic", "claude"), agent.Model, "model");
                Equal("/repo", agent.Cwd, "cwd");
                Equal(0, agent.Tools.Length, "tools");
                Seq(["hello"], await AllEntries(reopened), "root entries");
                Equal(child.Id, (await first.Harness.ConversationAsync(child.Id, Context))?.Id, "child");
                var reopenedIds = new List<string?>();
                foreach (var id in new[] { root.Id, child.Id, fork.Id }) reopenedIds.Add(await ProviderSessionId(first.Harness, id));
                Seq(providerSessionIds, reopenedIds, "provider sessions after reopen");
                var reopenedFork = await first.Harness.ConversationAsync(fork.Id, Context);
                Seq(["hello"], await AllEntries(reopenedFork!), "fork entries");
                Equal(new ModelRef("anthropic", "claude"), (await reopenedFork!.AgentAsync(Context)).Model, "fork model");
                Check(await first.Harness.ConversationAsync(new ConversationId(999), Context) is null, "absent conversation");
                await first.Harness.CloseAsync(Context);
            });
        }

        yield return Case(prefix + "creates-independent-conversations-atomically-with-init-and-rolls-back-failures", async () =>
        {
            var controlled = Controlled();
            var (harness, registry) = await OpenHarness(controlled, ["read"]);
            var created = await harness.CreateConversationAsync(Ownerless with
            {
                Init = async (tx, id) => await tx.AppendEntryAsync(id, Message(User("seed"))),
            }, Context);
            Equal(1, controlled.Commits.Count, "commits");
            Seq(["seed"], await AllEntries(created), "entries");

            var before = controlled.Commits.Count;
            await RejectsAsync(() => harness.CreateConversationAsync(Ownerless with
            {
                Agent = new AgentChange { ThinkingLevel = "high" },
                Init = (_, _) => throw new InvalidOperationException("init failed"),
            }, Context), "init failed", "failing init");
            Equal(before, controlled.Commits.Count, "commits after failure");

            // A conversation on the default selection follows installs live.
            AddTool(registry, Tool("bash"));
            Seq(["read", "bash"], ToolNames(await created.AgentAsync(Context)), "tools");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "forks-at-a-concrete-entry-with-the-as-of-agent-and-applies-agent-and-init-overrides", async () =>
        {
            var (harness, registry) = await OpenHarness(storage(), ["read"]);
            var legacy = Tool("legacy");
            AddTool(registry, legacy);
            var root = await harness.RootAsync(Context);
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "low" }, Context);
            var at = await Append(root, "one");
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "high", Tools = ToolSelection.Only(Tool("read")) }, Context);
            await Append(root, "two");

            var child = await root.ForkAsync(at.Id, Ownerless, Context);
            SameJson("""{"thinkingLevel":"low"}""", await harness.SnapshotAsync(Agents.AgentDoc, child.Id, Context), "child agent");
            Seq(["one"], await AllEntries(child), "child entries");

            var overridden = await root.ForkAsync(at.Id, Ownerless with
            {
                Agent = new AgentChange { ThinkingLevel = "minimal", Tools = ToolSelection.Only(legacy) },
                Init = async (tx, id) => Equal("minimal", S((await tx.DocAsync(Agents.AgentDoc, id)).Get("thinkingLevel")), "agent in init"),
            }, Context);
            SameJson("""{"thinkingLevel":"minimal","tools":["legacy"]}""", await harness.SnapshotAsync(Agents.AgentDoc, overridden.Id, Context), "overridden agent");
            SameJson("""{"thinkingLevel":"high","tools":["read"]}""", await harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context), "root agent");

            var unrelated = await harness.CreateConversationAsync(Ownerless, Context);
            Check(await root.ForkAsync(at.Id, Ownerless, Context) is not null, "fork resolves");
            await RejectsAsync(() => unrelated.ForkAsync(at.Id, Ownerless, Context), "is not visible", "unrelated fork");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "paginates-fork-aware-history-through-deep-ancestor-caps-and-same-commit-prefixes", async () =>
        {
            var (harness, _) = await OpenHarness(storage());
            var root = await harness.RootAsync(Context);
            await Append(root, "r1");
            var (r2, _) = await root.CommitAsync(async tx => (
                await tx.AppendEntryAsync(root.Id, Message(User("r2"))),
                await tx.AppendEntryAsync(root.Id, Message(User("r3")))), Context);
            var child = await root.ForkAsync(r2.Id, Ownerless, Context);
            var c1 = await Append(child, "c1");
            await Append(child, "c2");
            var grandchild = await child.ForkAsync(c1.Id, Ownerless, Context);
            await Append(grandchild, "g1");

            Seq(["r3", "r2", "r1"], await AllEntries(root), "root");
            Seq(["c2", "c1", "r2", "r1"], await AllEntries(child), "child");
            Seq(["g1", "c1", "r2", "r1"], await AllEntries(grandchild), "grandchild");
            // #10546
            Seq(["r1", "r2", "c1", "g1"], await AllEntries(grandchild, ScanOrder.Ascending), "grandchild ascending");
            var bounded = await grandchild.EntriesAsync(new EntryQueryBounds { MinEntryId = r2.Id, MaxEntryId = c1.Id }, 10, null, Context);
            Seq([c1.Id, r2.Id], Ids(bounded.Items), "bounded");
            Check(MessageEntry.Is(bounded.Items[0]), "message kind");
            Check(!MessageEntry.Is(null), "absent entry");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "binds-commits-and-task-creation-to-the-conversation", async () =>
        {
            var (harness, _) = await OpenHarness(storage());
            var conversation = await harness.CreateConversationAsync(Ownerless, Context);
            var task = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.work",
                Version = 1,
                Initial = _ => O("""{"phase":"run"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["run"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var taskId = await conversation.CommitAsync(tx => tx.CreateTaskAsync(task, O("""{"n":1}"""), new TaskOptions(TaskOwnership.Conversation)), Context);
            var record = await harness.CommitAsync(tx => tx.GetTaskAsync(taskId), Context);
            Equal(conversation.Id, record?.ConversationId, "task conversation");
            await RejectsAsync(() => harness.CommitAsync(tx => tx.CreateTaskAsync(task, O("""{"n":2}"""), new TaskOptions(TaskOwnership.Conversation)), Context),
                "requires options.conversationId", "unbound task");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "runs-conversation-created-in-every-creating-commit-after-the-built-ins-and-before-agent-and-init", async () =>
        {
            var seen = new SyncList<string>();
            var harness = await PiSharp.Durable.Harness.OpenAsync(storage(), new HarnessOptions
            {
                Models = EmptyDurableModels.Instance,
                Registry = Registry.Create(),
                ConversationCreated = async (tx, conversation) =>
                {
                    var agent = await tx.DocAsync(Agents.AgentDoc, conversation.Id);
                    var cwd = agent.Get("cwd") is JsonStringValue text ? text.Value : "undefined";
                    seen.Add($"{conversation.Id.Value}:{(conversation.Parent is null ? "new" : "fork")}:{cwd}");
                    var note = await tx.DocAsync(NoteDoc, conversation.Id);
                    if (Text(note).Length == 0) note.Set("text", "created");
                    if (cwd == "/fail") throw new InvalidOperationException("no");
                },
            }, Context);
            var root = await harness.RootAsync(Context, new AgentChange { Cwd = "/root" },
                async (tx, id) => seen.Add($"init:{Text(await tx.DocAsync(NoteDoc, id))}"));
            // A raw creation in a commit, as in a tool, and a fork, which already has the asOf copies.
            var raw = await harness.CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id, Context);
            var entry = await Append(root, "hello");
            var fork = await root.ForkAsync(entry.Id, Ownerless, Context);
            Seq([$"{root.Id.Value}:new:undefined", "init:created", $"{raw.Value}:new:undefined", $"{fork.Id.Value}:fork:/root"], seen.Snapshot(), "seen");
            SameJson("""{"text":"created"}""", await harness.SnapshotAsync(NoteDoc, raw, Context), "raw note");
            // A throw fails the creating commit.
            await root.ConfigureAsync(new AgentChange { Cwd = "/fail" }, Context);
            Check(await root.ForkAsync(entry.Id, Ownerless, Context) is not null, "fork before the change resolves");
            var failing = await Append(root, "after");
            await RejectsAsync(() => root.ForkAsync(failing.Id, Ownerless, Context), "no", "failing fork");
            await harness.CloseAsync(Context);
        });

        // ─── Harness agent ───

        yield return Case(prefix + "replaces-whole-fields-clears-them-with-null-and-leaves-undefined-fields-alone", async () =>
        {
            var (harness, registry) = await OpenHarness(storage());
            var read = Tool("read");
            var bash = Tool("bash");
            var edit = Tool("edit");
            registry.Install(Define.Extension(new Extension { Name = "coding", Tools = [read, bash, edit] }));
            var root = await harness.RootAsync(Context);
            Task<JsonObjectValue?> Stored() => harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context);
            async Task<List<string>> Offered() => ToolNames(await root.AgentAsync(Context));
            var initial = await root.AgentAsync(Context);
            Equal("off", initial.ThinkingLevel, "thinking level");
            Seq([read, bash, edit], initial.Tools, "tools");
            Check(initial.Model is null, "model unset");

            await root.ConfigureAsync(new AgentChange { Model = new ModelRef("openai", "gpt"), ThinkingLevel = "medium" }, Context);
            await root.ConfigureAsync(new AgentChange { Model = null, Instructions = "Be terse." }, Context);
            SameJson("""{"model":{"provider":"openai","modelId":"gpt"},"thinkingLevel":"medium","instructions":"Be terse."}""", await Stored(), "stored");
            await root.ConfigureAsync(new AgentChange { Model = Patch<ModelRef>.Clear, Instructions = Patch<string>.Clear }, Context);
            SameJson("""{"thinkingLevel":"medium"}""", await Stored(), "cleared");

            await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Without(edit) }, Context);
            Seq(["read", "bash"], await Offered(), "without edit");
            // A new filter replaces the old one: edit is offered again.
            await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Without(bash) }, Context);
            Seq(["read", "edit"], await Offered(), "without bash");
            await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(edit, read) }, Context);
            Seq(["edit", "read"], await Offered(), "only");
            await root.ConfigureAsync(new AgentChange { Tools = Patch<ToolSelection>.Clear }, Context);
            Seq(["read", "bash", "edit"], await Offered(), "default");
            // Names are stored without checking the registry.
            await root.ConfigureAsync(new AgentChange { Tools = ToolSelection.Only(Tool("missing"), read) }, Context);
            SameJson("""["missing","read"]""", (await Stored())!["tools"], "stored names");
            Seq(["read"], await Offered(), "offered");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "gives-conversations-created-through-tx-their-documents-empty-an-owner-copy-or-the-forks-as-of-copy", async () =>
        {
            var (harness, _) = await OpenHarness(storage(), ["read"]);
            var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "m"), Instructions = "Main role.", Cwd = "/repo" });
            var owner = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.owner",
                Version = 1,
                Initial = _ => O("""{"phase":"never"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["never"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var (taskId, plain, owned) = await root.CommitAsync(async tx =>
            {
                var taskId = await tx.CreateTaskAsync(owner, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Conversation));
                var plain = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
                var owned = await tx.CreateConversationAsync(ConversationOwnership.Task(taskId));
                // The copy exists when createConversation() returns, so a configure() in the same callback overrides it.
                Equal("/repo", S((await tx.DocAsync(Agents.AgentDoc, owned.Id)).Get("cwd")), "owned cwd in the transaction");
                await Agents.ConfigureAsync(tx, owned.Id, new AgentChange { Cwd = "/worktree" });
                return (taskId, plain.Id, owned.Id);
            }, Context);
            SameJson("{}", await harness.SnapshotAsync(Agents.AgentDoc, plain, Context), "plain agent");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, plain, Context), "plain live");
            var plainProvider = await harness.SnapshotAsync(ProviderIdentity.ProviderDoc, plain, Context);
            Seq(["sessionId"], plainProvider!.Keys, "plain provider keys");
            IsUuidV7(S(plainProvider["sessionId"]), "plain provider session");
            Check(await ProviderSessionId(harness, owned) != await ProviderSessionId(harness, root.Id), "owned provider identity");
            SameJson("""{"model":{"provider":"faux","modelId":"m"},"instructions":"Main role.","cwd":"/worktree"}""",
                await harness.SnapshotAsync(Agents.AgentDoc, owned, Context), "owned agent");

            // A later owner change does not reach the child.
            await root.ConfigureAsync(new AgentChange { ThinkingLevel = "high" }, Context);
            Check((await harness.SnapshotAsync(Agents.AgentDoc, owned, Context))?.ContainsKey("thinkingLevel") == false, "owned thinking level");

            // A task-owned fork keeps its as-of copy of its fork parent, not its owner's agent.
            var at = await root.CommitAsync(async tx => (await tx.AppendEntryAsync(plain, new EntryDraft { Kind = "note" })).Id, Context);
            var fork = await root.CommitAsync(async tx => (await tx.ForkConversationAsync(plain, at, ConversationOwnership.Task(taskId))).Id, Context);
            SameJson("{}", await harness.SnapshotAsync(Agents.AgentDoc, fork, Context), "fork agent");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, fork, Context), "fork live");
            Check(await ProviderSessionId(harness, fork) != await ProviderSessionId(harness, plain), "fork provider identity");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "reads-an-absent-agent-for-conversations-a-plain-session-created-without-writing", async () =>
        {
            var controlled = Controlled();
            var id = await new Session(controlled).CommitAsync(async tx => (await tx.CreateConversationAsync(ConversationOwnership.Ownerless)).Id, Context);
            var (harness, _) = await OpenHarness(controlled, ["read"]);
            var raw = await harness.ConversationAsync(id, Context);
            Check(await harness.SnapshotAsync(Live.LiveDoc, id, Context) is null, "no live document");
            var commits = controlled.Commits.Count;
            var agent = await raw!.AgentAsync(Context);
            Equal("off", agent.ThinkingLevel, "thinking level");
            Seq(["read"], ToolNames(agent), "tools");
            Equal(commits, controlled.Commits.Count, "commits");
            await raw.ConfigureAsync(new AgentChange { ThinkingLevel = "low" }, Context);
            SameJson("""{"thinkingLevel":"low"}""", await harness.SnapshotAsync(Agents.AgentDoc, id, Context), "agent");
            await harness.CloseAsync(Context);
        });

        // ─── Harness lifecycle ───

        yield return Case(prefix + "returns-stateless-handles-and-rejects-operations-after-close", async () =>
        {
            var (harness, _) = await OpenHarness(storage());
            var root = await harness.RootAsync(Context);
            var again = await harness.RootAsync(Context);
            Check(!ReferenceEquals(again, root), "a new handle");
            Equal(root.Id, again.Id, "same id");
            await harness.CloseAsync(Context);
            await RejectsAsync(() => harness.RootAsync(Context), "Harness is closed", "root");
            await RejectsAsync(() => harness.CreateConversationAsync(Ownerless, Context), "Harness is closed", "create");
            await RejectsAsync(() => harness.ConversationAsync(root.Id, Context), "Harness is closed", "conversation");
        });

        yield return Case(prefix + "forwards-generic-session-document-apis", async () =>
        {
            var (harness, _) = await OpenHarness(storage());
            var root = await harness.RootAsync(Context);
            var entry = await root.CommitAsync(async tx =>
            {
                (await tx.DocAsync(NoteDoc, root.Id)).Set("text", "first");
                return await tx.AppendEntryAsync(root.Id, Message(User("m")));
            }, Context);
            await harness.CommitAsync(async tx => (await tx.DocAsync(NoteDoc, root.Id)).Set("text", "second"), Context);
            SameJson("""{"text":"second"}""", await harness.SnapshotAsync(NoteDoc, root.Id, Context), "snapshot");
            SameJson("""{"text":"first"}""", await harness.SnapshotAsOfAsync(NoteDoc, root.Id, entry.Id, Context), "snapshot as of");
            var state = await harness.DocumentStateAsync(NoteDoc, root.Id, Context);
            SameJson("""{"text":"second"}""", state?.Value, "document state");
            state?.Dispose();
            await harness.CloseAsync(Context);
        });
    }
}
