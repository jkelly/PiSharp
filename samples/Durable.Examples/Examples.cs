// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/examples/00-conversation.ts through 04-watches.ts (05-harness.ts
// through 12-recovery.ts are in ExamplesHarness.cs; 13-chat.ts and 14-system-prompt.ts are in ExamplesChat.cs; 15-real-model.ts, 17-print.ts,
// 25-coding-agent.ts, 26-plan-mode.ts and 27-reviewer.ts are in ExamplesAgents.cs; 16-coding-tools.ts, 28-sandbox-per-conversation.ts and
// 29-tool-override.ts are in ExamplesTools.cs; 18-json.ts is in ExamplesJson.cs; 19-inbox.ts is in ExamplesInbox.cs; 20-late-join.ts is in
// ExamplesLateJoin.cs; 21-subagent-foreground.ts is in ExamplesSubagent.cs; 22-subagent-background.ts is in ExamplesBackground.cs;
// 23-child-tasks.ts is in ExamplesChildTasks.cs; 24-compaction.ts is in ExamplesCompaction.cs; 30-reload-and-restart.ts is in
// ExamplesReload.cs; 31-transcript.ts is in ExamplesTranscript.cs; 32-tests-after-edits.ts is in ExamplesTestsAfterEdits.cs;
// 33-turn-limit-and-continue.ts is in ExamplesTurnLimitAndContinue.cs; 34-submit-in-commit.ts is in ExamplesSubmitInCommit.cs).
// 35-durable-on-the-store.ts, 36-durable-views.ts and 37-harness-registry.ts run a Chord host (createHost, defineFacet), which phase 6 of
// the v1.5.0 sync ports; they are not here yet.
// Each example prints what upstream's console.log prints, as JSON. Upstream runs every example on a fresh MemoryStorage; here each one
// can also run on its own JSONL directory or SQLite file, and then the storage is reopened in a new Session and read back (milestone M0
// of decision 0005: the examples survive close and reopen).
// Pi v1.5.0 sync (closeout, b9fb2b854): the examples carry v1.5.0's names. Upstream renumbered them when it deleted 04-chord-state (area
// watches, 92ab53602: Session.documentState() is gone), so 05-watches became 04-watches and every later example moved down one; the
// examples v1.5.0 added (31-transcript through 34-submit-in-commit) follow. A file's notes from earlier areas may name an example by its
// v1.2.0 number. Area watch-folding (d8d99950a, 8b0caefc2 and 5f312dac4): 04-watches has a busy first call, three commits folded into the
// second, and the ops printed with each update.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;

namespace PiSharp.Durable.Examples;

/// <summary>The storage backend an example runs on.</summary>
public enum ExampleStorage
{
    /// <summary>MemoryStorage, as upstream runs the examples; nothing survives close.</summary>
    Memory,
    /// <summary>JsonlStorage in a directory per example.</summary>
    Jsonl,
    /// <summary>SqliteStorage in a file per example.</summary>
    Sqlite,
}

/// <summary>Upstream durable examples 00-34 of Pi v1.5.0, by their v1.5.0 names (35-37 need the Chord host of phase 6).</summary>
public static partial class Examples
{
    // Every Session call takes a context, which is used for cancellation. Background means "never cancel".
    private static readonly DurableContext Context = DurableContext.Background;

    // A document is a JSON object attached to something; here, one per conversation. Rewindable keeps old values readable, so you can ask
    // what the document looked like when a particular entry was written. AsOf: a fork starts with the value these notes had at the fork
    // entry (see 02-forks).
    private static readonly RewindableConversationDocToken Notes =
        Documents.DefineRewindableConversationDoc("example.notes", 1, DocumentFork.AsOf, () => JsonValue.Object([new("text", "")]),
            checkpointWhen: (_, _, _) => false);

    private delegate Task Example(Func<Task<IStorage>> open, TextWriter output);

    private static readonly (string Name, Example Run)[] All =
    [
        ("00-conversation", ConversationAsync),
        ("01-documents", DocumentsAsync),
        ("02-forks", ForksAsync),
        ("03-owned-conversations", OwnedConversationsAsync),
        ("04-watches", WatchesAsync),
        ("05-harness", HarnessAsync),
        ("06-configuration", ConfigurationAsync),
        ("07-harness-conversations", HarnessConversationsAsync),
        ("08-context", ContextAsync),
        ("09-registry-reload", RegistryReloadAsync),
        ("10-extension-state", ExtensionStateAsync),
        ("11-tasks", TasksAsync),
        ("12-recovery", RecoveryAsync),
        ("13-chat", ChatAsync),
        ("14-system-prompt", SystemPromptAsync),
        ("15-real-model", RealModelAsync),
        ("16-coding-tools", CodingToolsAsync),
        ("17-print", PrintAsync),
        ("18-json", JsonModeAsync),
        ("19-inbox", InboxAsync),
        ("20-late-join", LateJoinAsync),
        ("21-subagent-foreground", SubagentForegroundAsync),
        ("22-subagent-background", SubagentBackgroundAsync),
        ("23-child-tasks", ChildTasksAsync),
        ("24-compaction", CompactionAsync),
        ("25-coding-agent", CodingAgentAsync),
        ("26-plan-mode", PlanModeAsync),
        ("27-reviewer", ReviewerAsync),
        ("28-sandbox-per-conversation", SandboxPerConversationAsync),
        ("29-tool-override", ToolOverrideAsync),
        ("30-reload-and-restart", ReloadAndRestartAsync),
        ("31-transcript", TranscriptExampleAsync),
        ("32-tests-after-edits", TestsAfterEditsAsync),
        ("33-turn-limit-and-continue", TurnLimitAndContinueAsync),
        ("34-submit-in-commit", SubmitInCommitAsync),
    ];

    /// <summary>The example names, in order.</summary>
    public static IEnumerable<string> Names => All.Select(example => example.Name);

    /// <summary>Runs the selected examples (all when <paramref name="only"/> is empty; a name matches by number or full name).</summary>
    /// <param name="storage">The backend.</param>
    /// <param name="root">The directory file backends store each example under; required unless <paramref name="storage"/> is memory.</param>
    /// <param name="output">Where the examples print.</param>
    /// <param name="only">Example numbers (<c>00</c>) or names (<c>00-conversation</c>).</param>
    /// <param name="environment">Reads environment variables (15-real-model's OPENAI_API_KEY); default the process environment.</param>
    public static async Task RunAsync(ExampleStorage storage, string? root, TextWriter output, IReadOnlyCollection<string>? only = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        Examples.environment = environment ?? Environment.GetEnvironmentVariable;
        if (storage != ExampleStorage.Memory && string.IsNullOrEmpty(root)) throw new ArgumentException("File storage needs a root directory.", nameof(root));
        var selected = All.Where(example => only is null || only.Count == 0 ||
            only.Any(name => name == example.Name || name == example.Name[..2])).ToArray();
        if (only is { Count: > 0 } && selected.Length != only.Count) throw new ArgumentException("Unknown example; use " + string.Join(", ", Names) + ".");
        foreach (var (name, run) in selected)
        {
            await output.WriteLineAsync("== " + name);
            await run(Opener(storage, root, name), output);
        }
    }

    private static Func<Task<IStorage>> Opener(ExampleStorage storage, string? root, string name) => storage switch
    {
        ExampleStorage.Memory => () => Task.FromResult<IStorage>(new MemoryStorage()),
        ExampleStorage.Jsonl => async () => await JsonlStorage.OpenAsync(Directory.CreateDirectory(Path.Combine(root!, name)).FullName, Context),
        _ => async () => await SqliteStorage.OpenAsync(Path.Combine(Directory.CreateDirectory(Path.Combine(root!, name)).FullName, "session.sqlite")),
    };

    // Reopens file storage in a fresh Session and runs the read-back; memory storage has nothing to reopen.
    private static async Task ReopenAsync(Func<Task<IStorage>> open, TextWriter output, Func<ISession, Task> readBack)
    {
        var storage = await open();
        if (storage is MemoryStorage) { await storage.CloseAsync(Context); return; }
        await output.WriteLineAsync("-- reopened");
        var session = Session.Create(storage);
        await readBack(session);
        await session.CloseAsync(Context);
    }

    private static string Json(JsonValue? value) => value?.ToJson() ?? "undefined";

    // 00: a Session stores conversations, transcript entries, tasks, and documents.
    private static async Task ConversationAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());

        // All writes happen inside CommitAsync. The callback receives a transaction; everything it writes is saved together when the
        // callback returns, or discarded if it throws. Ownerless means no task created this conversation.
        var standalone = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
        await output.WriteLineAsync("standalone conversation: " + Json(standalone.ToJson()));

        await session.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var record = await reopened.CommitAsync(tx => tx.GetConversationAsync(standalone.Id), Context);
            await output.WriteLineAsync("standalone conversation: " + Json(record?.ToJson()));
        });
    }

    // 01: store document state next to transcript entries.
    private static async Task DocumentsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());
        var chat = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);

        // DocAsync returns an editable draft of the document (created on first use). Writes to it are saved when the commit finishes.
        var firstEntry = await session.CommitAsync(async tx =>
        {
            var entry = await tx.AppendEntryAsync(chat.Id, new EntryDraft { Kind = "note", Data = "hello" });
            (await tx.DocAsync(Notes, chat.Id)).Set("text", "after hello");
            return entry;
        }, Context);

        var secondEntry = await session.CommitAsync(async tx =>
        {
            var entry = await tx.AppendEntryAsync(chat.Id, new EntryDraft { Kind = "note", Data = "goodbye" });
            (await tx.DocAsync(Notes, chat.Id)).Set("text", "after goodbye");
            return entry;
        }, Context);

        // SnapshotAsync reads the latest value. SnapshotAsOfAsync reads the value that was saved in the same commit as the given entry.
        async Task PrintAsync(ISession reader)
        {
            await output.WriteLineAsync("latest notes: " + Json(await reader.SnapshotAsync(Notes, chat.Id, Context)));
            await output.WriteLineAsync("notes at first entry: " + Json(await reader.SnapshotAsOfAsync(Notes, chat.Id, firstEntry.Id, Context)));
            await output.WriteLineAsync("notes at second entry: " + Json(await reader.SnapshotAsOfAsync(Notes, chat.Id, secondEntry.Id, Context)));
        }
        await PrintAsync(session);

        await session.CloseAsync(Context);
        await ReopenAsync(open, output, PrintAsync);
    }

    // 02: fork a conversation.
    private static async Task ForksAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());
        var chat = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);
        var firstEntry = await session.CommitAsync(async tx =>
        {
            var entry = await tx.AppendEntryAsync(chat.Id, new EntryDraft { Kind = "note", Data = "hello" });
            (await tx.DocAsync(Notes, chat.Id)).Set("text", "after hello");
            return entry;
        }, Context);
        await session.CommitAsync(async tx =>
        {
            await tx.AppendEntryAsync(chat.Id, new EntryDraft { Kind = "note", Data = "goodbye" });
            (await tx.DocAsync(Notes, chat.Id)).Set("text", "after goodbye");
        }, Context);

        // A fork is a new conversation that continues from one entry of another. It sees the parent's transcript up to that entry, and
        // each document follows its own fork setting. Notes uses AsOf, so the fork starts with the notes value from the fork entry.
        var branch = await session.CommitAsync(tx => tx.ForkConversationAsync(chat.Id, firstEntry.Id, ConversationOwnership.Ownerless), Context);

        // ScanEntriesAsync pages through visible entries, newest first. The fork sees "hello" (inherited from the parent) but not
        // "goodbye", which came later.
        async Task PrintForkAsync(ISession reader)
        {
            var branchEntries = await reader.CommitAsync(tx => tx.ScanEntriesAsync(new EntryQuery(branch.Id), 10), Context);
            await output.WriteLineAsync("fork transcript: " + Json(JsonValue.Array(branchEntries.Items.Select(entry => entry.Data ?? JsonValue.Null))));
        }
        await PrintForkAsync(session);
        await output.WriteLineAsync("fork notes: " + Json(await session.SnapshotAsync(Notes, branch.Id, Context)));

        // The fork's copy is independent: editing it leaves the parent unchanged.
        await session.CommitAsync(async tx => (await tx.DocAsync(Notes, branch.Id)).Set("text", "changed only in the fork"), Context);
        async Task PrintNotesAsync(ISession reader)
        {
            await output.WriteLineAsync("fork notes after edit: " + Json(await reader.SnapshotAsync(Notes, branch.Id, Context)));
            await output.WriteLineAsync("parent notes after edit: " + Json(await reader.SnapshotAsync(Notes, chat.Id, Context)));
        }
        await PrintNotesAsync(session);

        await session.CloseAsync(Context);
        await ReopenAsync(open, output, async reopened =>
        {
            await PrintForkAsync(reopened);
            await PrintNotesAsync(reopened);
        });
    }

    // 03: a background task that owns a child conversation.
    private static async Task OwnedConversationsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());

        // A typical agent setup: a background task supervises a helper conversation, and the main conversation keeps a registry that maps
        // agent names to their conversations. All three are created in one commit, so after a crash either all of them exist or none do.

        // This example only creates the task record; a plain Session never runs it (the phase and abort handlers join with the task
        // runtime, decision 0005 phase P7).
        var supervisor = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.supervisor",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "ready")]),
        });

        // Latest keeps only the current value. Initial means forks of this conversation start without a registry, so a child doesn't
        // inherit its parent's list of agents.
        var agentRegistry = Documents.DefineConversationDoc("example.agent-registry", 1, DocumentFork.Initial,
            () => JsonValue.Object([new("agents", JsonValue.Object([]))]), checkpointWhen: (_, _, _) => false);

        var main = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), Context);

        var (supervisorId, child) = await session.CommitAsync(async tx =>
        {
            // Background: the task is side work, so waiting for the main conversation to finish does not wait for it.
            var supervisorId = await tx.CreateTaskAsync(supervisor, JsonValue.Null,
                new TaskOptions(TaskOwnership.Conversation) { ConversationId = main.Id, Background = true });

            // The child records that it belongs to the supervisor task. The task was created a few lines above in this same commit,
            // which is allowed.
            var child = await tx.CreateConversationAsync(ConversationOwnership.Task(supervisorId));

            // requestId is a fixed name for the child's first message. Later code sends that message using this requestId, so a retry
            // after a crash cannot deliver it twice.
            (await tx.DocAsync(agentRegistry, main.Id)).GetObject("agents").Set("researcher", JsonValue.Object([
                new("conversationId", child.Id.ToJson()),
                new("requestId", $"researcher:first-message:{supervisorId}"),
            ]));
            return (supervisorId, child);
        }, Context);

        await output.WriteLineAsync("supervisor task: " + supervisorId);
        await output.WriteLineAsync("child conversation: " + Json(child.ToJson()));
        await output.WriteLineAsync("registry: " + Json(await session.SnapshotAsync(agentRegistry, main.Id, Context)));

        await session.CloseAsync(Context);
        await ReopenAsync(open, output, async reopened =>
        {
            var (task, conversation) = await reopened.CommitAsync(async tx =>
                (await tx.GetTaskAsync(supervisorId), await tx.GetConversationAsync(child.Id)), Context);
            await output.WriteLineAsync("supervisor task: " + (task is null ? "missing" : $"{task.Id} {task.Kind}"));
            await output.WriteLineAsync("child conversation: " + Json(conversation?.ToJson()));
            await output.WriteLineAsync("registry: " + Json(await reopened.SnapshotAsync(agentRegistry, main.Id, Context)));
        });
    }

    // 04: serialize asynchronous document work with a watch.
    private static async Task WatchesAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());
        var chat = await session.CommitAsync(async tx =>
        {
            var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
            (await tx.DocAsync(Notes, conversation.Id)).Set("text", "first");
            return conversation;
        }, Context);

        // A watch starts from one stable acquisition revision. Callbacks never overlap: commits made while one runs arrive folded into the
        // next call, which gets the newest value and ops that turn the previously delivered value into it (one full-value replacement
        // [["r", value]] once more than 10,000 ops pile up).
        var notesWatch = await session.WatchDocAsync(Notes, chat.Id, Context) ?? throw new InvalidOperationException("notes are absent");
        await output.WriteLineAsync("watch baseline: " + Json(notesWatch.Value));
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        notesWatch.Start(async (call, _) =>
        {
            // Calls are tagged: a failed call is a failure a source recovers from, which Durable never sends (its failures end the watch,
            // see Closed).
            if (call is not WatchValueCall<JsonObjectValue> change) return;
            var ops = JsonValue.Array(change.Ops.Select(op => (JsonValue)op.ToJson()));
            await output.WriteLineAsync("watch update: " + Json(change.Value) + " " + Json(ops));
            // The first call stays busy, as a slow consumer would, while three more commits land.
            if (Interlocked.Increment(ref calls) == 1) await busy.Task;
            else done.TrySetResult();
        });
        await session.CommitAsync(async tx => (await tx.DocAsync(Notes, chat.Id)).Set("text", "observed"), Context);
        foreach (var more in new[] { " asynchronously", ", folded", "!" })
        {
            await session.CommitAsync(async tx =>
            {
                var notes = await tx.DocAsync(Notes, chat.Id);
                notes.Set("text", ((JsonStringValue)notes.Get("text")!).Value + more);
            }, Context);
        }
        busy.TrySetResult();
        await done.Task;
        await output.WriteLineAsync("listener calls: " + Volatile.Read(ref calls));
        await notesWatch.StopAsync();

        await session.CloseAsync(Context);
        await ReopenAsync(open, output, async reopened =>
        {
            var watch = await reopened.WatchDocAsync(Notes, chat.Id, Context) ?? throw new InvalidOperationException("notes are absent");
            await output.WriteLineAsync("watch baseline: " + Json(watch.Value));
            await watch.StopAsync();
        });
    }
}
