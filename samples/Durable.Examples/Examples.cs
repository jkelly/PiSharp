// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/00-conversation.ts through 05-watches.ts (06-harness.ts
// through 13-recovery.ts are in ExamplesHarness.cs; 14-chat.ts and 15-system-prompt.ts are in ExamplesChat.cs; 17-coding-tools.ts,
// 29-sandbox-per-conversation.ts and 30-tool-override.ts are in ExamplesTools.cs; 20-inbox.ts is in ExamplesInbox.cs; 25-compaction.ts is
// in ExamplesCompaction.cs).
// Each example prints what upstream's console.log prints, as JSON. Upstream runs every example on a fresh MemoryStorage; here each one
// can also run on its own JSONL directory or SQLite file, and then the storage is reopened in a new Session and read back (milestone M0
// of decision 0005: the examples survive close and reopen).
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

/// <summary>Upstream durable examples 00-15, 17, 20, 25, 29 and 30.</summary>
public static partial class Examples
{
    // Every Session call takes a context, which is used for cancellation. Background means "never cancel".
    private static readonly DurableContext Context = DurableContext.Background;

    // A document is a JSON object attached to something; here, one per conversation. Rewindable keeps old values readable, so you can ask
    // what the document looked like when a particular entry was written. AsOf: a fork starts with the value these notes had at the fork
    // entry (see 02-forks).
    private static readonly RewindableConversationDocToken Notes =
        Documents.DefineRewindableConversationDoc("example.notes", 1, DocumentFork.AsOf, () => JsonValue.Object([new("text", "")]));

    private delegate Task Example(Func<Task<IStorage>> open, TextWriter output);

    private static readonly (string Name, Example Run)[] All =
    [
        ("00-conversation", ConversationAsync),
        ("01-documents", DocumentsAsync),
        ("02-forks", ForksAsync),
        ("03-owned-conversations", OwnedConversationsAsync),
        ("04-chord-state", ChordStateAsync),
        ("05-watches", WatchesAsync),
        ("06-harness", HarnessAsync),
        ("07-configuration", ConfigurationAsync),
        ("08-harness-conversations", HarnessConversationsAsync),
        ("09-context", ContextAsync),
        ("10-registry-reload", RegistryReloadAsync),
        ("11-extension-state", ExtensionStateAsync),
        ("12-tasks", TasksAsync),
        ("13-recovery", RecoveryAsync),
        ("14-chat", ChatAsync),
        ("15-system-prompt", SystemPromptAsync),
        ("17-coding-tools", CodingToolsAsync),
        ("20-inbox", InboxAsync),
        ("25-compaction", CompactionAsync),
        ("29-sandbox-per-conversation", SandboxPerConversationAsync),
        ("30-tool-override", ToolOverrideAsync),
    ];

    /// <summary>The example names, in order.</summary>
    public static IEnumerable<string> Names => All.Select(example => example.Name);

    /// <summary>Runs the selected examples (all when <paramref name="only"/> is empty; a name matches by number or full name).</summary>
    /// <param name="storage">The backend.</param>
    /// <param name="root">The directory file backends store each example under; required unless <paramref name="storage"/> is memory.</param>
    /// <param name="output">Where the examples print.</param>
    /// <param name="only">Example numbers (<c>00</c>) or names (<c>00-conversation</c>).</param>
    public static async Task RunAsync(ExampleStorage storage, string? root, TextWriter output, IReadOnlyCollection<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(output);
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
            () => JsonValue.Object([new("agents", JsonValue.Object([]))]));

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

    // 04: expose a document through Chord.
    private static async Task ChordStateAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());
        var chat = await session.CommitAsync(async tx =>
        {
            var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
            (await tx.DocAsync(Notes, conversation.Id)).Set("text", "first");
            return conversation;
        }, Context);

        // DocumentStateAsync never creates a document. It returns a hydrated read-only Chord state bound to the current concrete
        // incarnation.
        var notesState = await session.DocumentStateAsync(Notes, chat.Id, Context) ?? throw new InvalidOperationException("notes are absent");
        var stopNotes = notesState.Subscribe((value, _, delivery) =>
            output.WriteLine($"Chord notes: {delivery.Kind.ToString().ToLowerInvariant()} {delivery.Sequence} {Json(value)}"));
        await session.CommitAsync(async tx => (await tx.DocAsync(Notes, chat.Id)).Set("text", "published through Chord"), Context);
        stopNotes.Dispose();
        notesState.Dispose();

        await session.CloseAsync(Context);
        await ReopenAsync(open, output, async reopened =>
        {
            var state = await reopened.DocumentStateAsync(Notes, chat.Id, Context) ?? throw new InvalidOperationException("notes are absent");
            using (state.Subscribe((value, _, delivery) =>
                output.WriteLine($"Chord notes: {delivery.Kind.ToString().ToLowerInvariant()} {delivery.Sequence} {Json(value)}"))) { }
            state.Dispose();
        });
    }

    // 05: serialize asynchronous document work with a watch.
    private static async Task WatchesAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var session = Session.Create(await open());
        var chat = await session.CommitAsync(async tx =>
        {
            var conversation = await tx.CreateConversationAsync(ConversationOwnership.Ownerless);
            (await tx.DocAsync(Notes, conversation.Id)).Set("text", "first");
            return conversation;
        }, Context);

        // A watch starts from one stable acquisition revision. Slow callbacks never overlap; exact committed frames buffer, with a
        // full-value reset after 100.
        var notesWatch = await session.WatchDocAsync(Notes, chat.Id, Context) ?? throw new InvalidOperationException("notes are absent");
        await output.WriteLineAsync("watch baseline: " + Json(notesWatch.Value));
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        notesWatch.Start(async (value, _, _) =>
        {
            await output.WriteLineAsync("watch update: " + Json(value));
            delivered.TrySetResult();
        });
        await session.CommitAsync(async tx => (await tx.DocAsync(Notes, chat.Id)).Set("text", "observed asynchronously"), Context);
        await delivered.Task;
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
