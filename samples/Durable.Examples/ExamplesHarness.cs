// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/06-harness.ts, 07-configuration.ts,
// 08-harness-conversations.ts, 09-context.ts, 10-registry-reload.ts, 11-extension-state.ts, 12-tasks.ts and 13-recovery.ts.
// Each prints what upstream's console.log prints, with JSON in place of util.inspect (so a property whose value is undefined is absent).
// Upstream's createModels() with no provider is EmptyDurableModels; 11's faux provider is ScriptedModels. On a file backend, 06, 07, 11 and
// 12 reopen their storage in a new Session and read back what they stored. 13 is itself a close-and-reopen example: on memory it keeps its
// storage in a temporary SQLite file as upstream does, and on a file backend it reopens the runner's storage.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    /// <summary><c>Type.Object({ path: Type.String() })</c>, as TypeBox prints it.</summary>
    private static readonly JsonObjectValue PathParameters =
        (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["path"],"properties":{"path":{"type":"string"}}}""");

    private static ToolExecutionResult TextResult(string text) => new()
    {
        Content = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]),
    };

    private static string StringArg(JsonValue args, string key) => ((JsonStringValue)((JsonObjectValue)args)[key]).Value;

    private static string JsonNames(IEnumerable<string> names) => Json(JsonValue.Array(names.Select(name => (JsonValue)name)));

    // 06: open a Harness with a registry of extensions.
    private static async Task HarnessAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A Harness is a Session plus conversation handles and durable tasks. Extension code (tools, prompt sections, hooks, tasks) comes in
        // named extensions installed in a registry the application owns. Nothing in the registry is saved; it is this process's code.
        var read = Define.Tool(new ToolRegistration
        {
            Name = "read",
            Description = "Read a file",
            Parameters = PathParameters,
            Execute = (args, _, _) => Task.FromResult(TextResult($"contents of {StringArg(args, "path")}")),
        });
        var files = new Extension { Name = "files", Tools = [read] };
        var registry = Registry.Create();
        registry.Install(files);

        // `Models` is the model access; generation calls models through it.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);

        // The root conversation always has ID 1. The first RootAsync() call creates it, its built-in documents, the `agent` choices, and
        // whatever `init` writes, all in one commit. Later calls, including after a restart, return it and ignore both options.
        var root = await harness.RootAsync(Context, new AgentChange { ThinkingLevel = "low" },
            async (tx, rootId) => (await tx.DocAsync(Notes, rootId)).Set("text", "root notes"));
        await output.WriteLineAsync($"root: {root.Id.Value} {Json(await harness.SnapshotAsync(Notes, root.Id, Context))}");
        // The stored choices are names; AgentAsync() resolves them against the registry.
        await output.WriteLineAsync("stored agent: " + Json(await harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context)));
        var agent = await root.AgentAsync(Context);
        await output.WriteLineAsync(
            $"resolved: {agent.ThinkingLevel} {JsonNames(agent.Extensions.Select(extension => extension.Name))} {JsonNames(agent.Tools.Select(tool => tool.Name))}");

        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            await output.WriteLineAsync($"root: {root.Id.Value} {Json(await reopened.SnapshotAsync(Notes, root.Id, Context))}");
            await output.WriteLineAsync("stored agent: " + Json(await reopened.SnapshotAsync(Agents.AgentDoc, root.Id, Context)));
        });
    }

    /// <summary>Apps may attach their own metadata to tools, such as a prompt snippet.</summary>
    private sealed record AppTool : ToolRegistration
    {
        public string? Snippet { get; init; }
    }

    /// <summary>Settings whose stream timeout is read live (upstream's <c>get stream()</c>).</summary>
    private sealed class LiveSettings(Func<double> timeoutMs) : HarnessSettings
    {
        public override ConversationStreamOptions? Stream { get => new() { TimeoutMs = timeoutMs() }; init { } }
    }

    // 07: per-conversation agent choices and Harness-wide settings.
    private static async Task ConfigurationAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        static AppTool ExampleTool(string name, string description) => Define.Tool(new AppTool
        {
            Name = name,
            Description = description,
            Parameters = PathParameters,
            Execute = (args, _, _) => Task.FromResult(TextResult($"{name} {StringArg(args, "path")}")),
            Snippet = $"Use {name} for files.",
        });

        var read = ExampleTool("read", "Read a file");
        var write = ExampleTool("write", "Write a file");
        var grep = ExampleTool("grep", "Search files");
        var files = new Extension { Name = "files", Tools = [read, write] };
        var search = new Extension { Name = "search", Tools = [grep] };
        // Sections see the offered tools, so one can render the app tools' snippets.
        var snippets = new Extension
        {
            Name = "snippets",
            Sections = [Define.Section("tool_snippets", (input, _) =>
                ValueTask.FromResult<string?>(string.Join("\n", input.Agent.Tools.Select(tool => (tool as AppTool)?.Snippet ?? ""))))],
        };

        var registry = Registry.Create();
        registry.Install(files);
        registry.Install(search);
        registry.Install(snippets);

        // Settings are Harness-wide run policy, read at every use and never stored. An overridden getter makes a value live, such as one
        // backed by the app's settings file.
        var timeoutMs = 60_000d;
        var settings = new LiveSettings(() => timeoutMs)
        {
            Retry = new RetrySettings { MaxRetries = 5 },
            ToolExecution = ToolExecutionMode.Sequential,
        };
        var harness = await Harness.OpenAsync(await open(),
            new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry, Settings = settings }, Context);
        var root = await harness.RootAsync(Context);

        // A new conversation stores no choices and follows the host: every installed extension, all of their tools.
        async Task<string> Tools() => JsonNames((await root.AgentAsync(Context)).Tools.Select(tool => tool.Name));
        await output.WriteLineAsync("default tools: " + await Tools());

        // ConfigureAsync() stores choices in the conversation's pi.agent document, one commit per call. Extensions and tools are passed as
        // objects and stored by name, so a typo cannot slip in.
        await root.ConfigureAsync(new AgentChange
        {
            Model = new ModelRef("anthropic", "claude-sonnet-4-5"),
            ThinkingLevel = "high",
            Tools = ToolSelection.Only(write, read),
        }, Context);
        await output.WriteLineAsync("stored: " + Json(await harness.SnapshotAsync(Agents.AgentDoc, root.Id, Context)));
        var agent = await root.AgentAsync(Context);
        await output.WriteLineAsync($"model: {Json(agent.Model?.ToJson())} thinking: {agent.ThinkingLevel} tools: {await Tools()}");

        // Deselect an extension; a Clear patch clears a stored field back to the host default.
        await root.ConfigureAsync(new AgentChange { Extensions = ExtensionSelection.Edit(remove: [search]), Tools = Patch<ToolSelection>.Clear }, Context);
        await output.WriteLineAsync("without search: " + await Tools());

        // Stored names outlive the code. The conversation selects exactly these two extensions; uninstalling "files" leaves it selected, and
        // requests just stop offering its tools until it is back.
        await root.ConfigureAsync(new AgentChange { Extensions = ExtensionSelection.Only(files, search) }, Context);
        registry.Uninstall(files);
        await output.WriteLineAsync("files uninstalled: " + await Tools());
        registry.Install(files);
        await output.WriteLineAsync("files reinstalled: " + await Tools());

        // Settings changes need no commit; the next request uses the new timeout.
        timeoutMs = 120_000;

        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("stored: " + Json(await reopened.SnapshotAsync(Agents.AgentDoc, root.Id, Context))));
    }

    // 08: conversation handles, typed entries, created conversations, and forks.
    private static async Task HarnessConversationsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = Registry.Create() }, Context);
        var root = await harness.RootAsync(Context);
        await root.ConfigureAsync(new AgentChange { ThinkingLevel = "high" }, Context);

        // Conversation handles are stateless; compare them by ID. They bind commits to their conversation. An entry token types an entry
        // kind's `data`.
        var message = Entries.DefineEntry<JsonObjectValue>("message");
        var hello = await root.CommitAsync(tx => tx.AppendEntryAsync(message, root.Id, new TypedEntryDraft<JsonObjectValue>
        {
            Data = JsonValue.Object([new("from", "example")]),
            Model = JsonValue.Array([JsonValue.Object([new("role", "user"), new("content", "hello"), new("timestamp", 1)])]),
        }), Context);
        await output.WriteLineAsync($"typed entry: {(message.Is(hello.Record) ? "true" : "false")} {StringArg(hello.Data, "from")}");

        // CreateConversationAsync() and ForkAsync() apply `Agent` and run `Init` in the creating commit. A fork starts with the agent the
        // parent had at the fork entry.
        var helper = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
        {
            Agent = new AgentChange { ThinkingLevel = "minimal" },
        }, Context);
        var retry = await root.ForkAsync(hello.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
        await output.WriteLineAsync("helper thinking: " + (await helper.AgentAsync(Context)).ThinkingLevel);
        await output.WriteLineAsync("fork thinking: " + (await retry.AgentAsync(Context)).ThinkingLevel);
        await output.WriteLineAsync("lookup: " + ((await harness.ConversationAsync(retry.Id, Context))?.Id == retry.Id ? "true" : "false"));

        await harness.CloseAsync(Context);
    }

    // 09: transcript history and model context.
    private static async Task ContextAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = Registry.Create() }, Context);

        // Entries are immutable. `Model` holds the messages an entry contributes to the next model request; `Data` is for the app only.
        // ContextAsync() turns the stored transcript into those request messages:
        //   - an entry with `Head` starts a new context; older entries stay stored,
        //   - `Edits` replace or omit what an earlier entry contributes,
        //   - aborted, error, and deferred assistant messages are not sent,
        //   - tool results are sent right after their call, in call order,
        //   - a call without a result gets a synthesized error result.
        static JsonValue AssistantMessage(string text, string[]? calls = null, string? stopReason = null)
        {
            calls ??= [];
            JsonValue zero = 0;
            return JsonValue.Object([
                new("role", "assistant"),
                new("content", JsonValue.Array([
                    JsonValue.Object([new("type", "text"), new("text", text)]),
                    .. calls.Select(id => (JsonValue)JsonValue.Object([new("type", "toolCall"), new("id", id), new("name", "read"), new("arguments", JsonValue.Object([]))])),
                ])),
                new("api", "example"),
                new("provider", "example"),
                new("model", "example"),
                new("usage", JsonValue.Object([
                    new("input", zero), new("output", zero), new("cacheRead", zero), new("cacheWrite", zero), new("totalTokens", zero),
                    new("cost", JsonValue.Object([new("input", zero), new("output", zero), new("cacheRead", zero), new("cacheWrite", zero), new("total", zero)])),
                ])),
                new("stopReason", stopReason ?? (calls.Length > 0 ? "toolUse" : "stop")),
                new("timestamp", 2),
            ]);
        }

        static JsonValue ToolResultMessage(string id) => JsonValue.Object([
            new("role", "toolResult"),
            new("toolCallId", id),
            new("toolName", "read"),
            new("content", JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", $"file {id}")])])),
            new("isError", false),
            new("timestamp", 3),
        ]);

        static JsonValue UserMessage(string content, double timestamp) =>
            JsonValue.Object([new("role", "user"), new("content", content), new("timestamp", timestamp)]);

        static string Show(JsonValue message)
        {
            var value = (JsonObjectValue)message;
            switch (((JsonStringValue)value["role"]).Value)
            {
                case "user":
                    return $"user: {((JsonStringValue)value["content"]).Value}";
                case "system":
                    return $"system: {Json(value.TryGetValue("sections", out var sections) ? sections : null)}";
                case "assistant":
                    return "assistant: " + string.Join(" ", ((JsonArrayValue)value["content"]).Cast<JsonObjectValue>().Select(part =>
                        ((JsonStringValue)part["type"]).Value switch
                        {
                            "text" => ((JsonStringValue)part["text"]).Value,
                            "toolCall" => $"call({((JsonStringValue)part["id"]).Value})",
                            _ => "",
                        }));
                default:
                    return $"result({((JsonStringValue)value["toolCallId"]).Value})" + (value["isError"] is JsonBooleanValue { Value: true } ? " error" : "");
            }
        }

        static string Shown(IEnumerable<JsonValue> messages) => JsonNames(messages.Select(Show));

        var transcript = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
        Task<EntryRecord> Say(string kind, params JsonValue[] model) =>
            transcript.CommitAsync(tx => tx.AppendEntryAsync(transcript.Id, new EntryDraft { Kind = kind, Model = JsonValue.Array(model) }), Context);

        var question = await Say("message", UserMessage("read a and b", 1));
        await Say("message", AssistantMessage("I crashed", [], "aborted")); // stored, never sent
        var calls = await Say("message", AssistantMessage("reading", ["a", "b"]));
        await Say("message", ToolResultMessage("b")); // results finish out of order
        await Say("pi.system", JsonValue.Object([
            new("role", "system"), new("content", ""), new("sections", JsonValue.Object([new("cwd", "<cwd>/repo</cwd>")])), new("timestamp", 4),
        ]));
        await Say("message", ToolResultMessage("a"));
        await Say("message", AssistantMessage("a and b look fine"));
        await transcript.CommitAsync(tx => tx.AppendEntryAsync(transcript.Id, new EntryDraft
        {
            Kind = "edit",
            Data = "user fixed a typo",
            Edits = [ContextEdit.Replace(question.Id, JsonValue.Array([UserMessage("read files a and b", 1)]))],
        }), Context);
        await transcript.CommitAsync(tx => tx.AppendEntryAsync(transcript.Id, new EntryDraft { Kind = "note", Data = "display only" }), Context);

        var view = await transcript.ContextAsync(Context);
        await output.WriteLineAsync("raw active entries: " + JsonNames(view.Entries.Select(entry => entry.Kind)));
        await output.WriteLineAsync("request messages: " + Shown(view.Messages));

        // A fork at the tool call has no results yet; ContextAsync() fills them in.
        var cut = await transcript.ForkAsync(calls.Id, new ConversationCreateOptions(ConversationOwnership.Ownerless), Context);
        await output.WriteLineAsync("fork messages: " + Shown((await cut.ContextAsync(Context)).Messages));

        // A headed summary replaces everything before the entry it points at. EntryHead.Self points the head at the summary entry itself.
        await transcript.CommitAsync(tx => tx.AppendEntryAsync(transcript.Id, new EntryDraft
        {
            Kind = "summary",
            Head = EntryHead.Self,
            Model = JsonValue.Array([UserMessage("Summary: a and b are fine.", 5)]),
        }), Context);
        view = await transcript.ContextAsync(Context);
        await output.WriteLineAsync($"after summary: {view.Head?.Kind} {Shown(view.Messages)}");

        // EntriesAsync() pages the stored transcript, newest first, including inherited parent entries. Nothing is ever deleted by heads or
        // edits.
        var history = await transcript.EntriesAsync(new EntryQueryBounds(), 3, null, Context);
        await output.WriteLineAsync(
            $"newest stored entries: {JsonNames(history.Items.Select(entry => entry.Kind))} more: {(history.Next is not null ? "true" : "false")}");

        await harness.CloseAsync(Context);
    }

    // 10: reload extension code through the registry.
    private static async Task RegistryReloadAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        static ToolRegistration ExampleTool(string name, string description) => Define.Tool(new ToolRegistration
        {
            Name = name,
            Description = description,
            Parameters = PathParameters,
            Execute = (args, _, _) => Task.FromResult(TextResult($"{name} {StringArg(args, "path")}")),
        });

        var read = ExampleTool("read", "Read a file");
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "files", Tools = [read, ExampleTool("grep", "Search files")] });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);
        var root = await harness.RootAsync(Context);
        async Task<string> Tools() => JsonNames((await root.AgentAsync(Context)).Tools.Select(tool => $"{tool.Name}: {tool.Description}"));

        // Reloading is installing a new object with the same name: it replaces the old one in place, in one publication, so no conversation
        // ever sees it missing. A task phase that already started keeps the snapshot it took; the next phase uses the new code.
        registry.Install(new Extension { Name = "files", Tools = [read, ExampleTool("grep", "Search files, faster")] });
        await output.WriteLineAsync("after reload: " + await Tools());

        // Another extension may wrap a tool by name. The wrap applies where the wrapping extension is selected, and survives reloads of the
        // wrapped tool.
        var audit = new Extension
        {
            Name = "audit",
            Wraps = [Define.WrapTool(read, tool => tool with { Description = $"{tool.Description} (audited)" })],
        };
        registry.Install(audit);
        await output.WriteLineAsync("with audit: " + await Tools());

        // Uninstall removes an extension by name; conversations that select it stop getting its code.
        registry.Uninstall(audit);
        await output.WriteLineAsync("after uninstall: " + await Tools());

        await harness.CloseAsync(Context);
    }

    // A todo list per conversation. Rewindable with AsOf, so a fork starts with the todos its parent had at the fork entry.
    private static readonly RewindableConversationDocToken Todos = Documents.DefineRewindableConversationDoc("example.todos", 1, DocumentFork.AsOf,
        () => JsonValue.Object([new("items", JsonValue.Array([]))]));

    // 11: an extension that keeps its own per-conversation document.
    private static async Task ExtensionStateAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // The tool writes the document; the section shows it to the model before every request. Nothing needs to create it up front:
        // tx.DocAsync() creates it on first write, and the section treats a missing document as an empty list.
        var todo = new Extension
        {
            Name = "todo",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "todo",
                    Description = "Add an item to your todo list",
                    Parameters = (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["item"],"properties":{"item":{"type":"string"}}}"""),
                    Execute = async (args, api, callContext) =>
                    {
                        var item = StringArg(args, "item");
                        await api.CommitAsync(async tx => (await tx.DocAsync(Todos, api.ConversationId)).GetArray("items").Add(item), callContext);
                        return TextResult($"added {item}");
                    },
                }),
            ],
            Sections =
            [
                Define.Section("todos", async (input, renderContext) =>
                {
                    var todos = await input.Read.SnapshotAsync(Todos, input.ConversationId, renderContext);
                    var items = todos is null ? [] : ((JsonArrayValue)todos["items"]).Select(item => ((JsonStringValue)item).Value).ToList();
                    return items.Count == 0 ? null : string.Join("\n", items);
                }),
            ],
        };

        var models = new ScriptedModels(
            ScriptedReply.ToolCall("todo", JsonValue.Object([new("item", "fix the build")])),
            "Noted.",
            "Working on it.");
        var registry = Registry.Create();
        registry.Install(todo);
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        await (await root.SubmitAsync(SubmissionDraft.Input("Remember to fix the build."), Context)).WaitAsync(Context);
        await output.WriteLineAsync("todos: " + Json(await harness.SnapshotAsync(Todos, root.Id, Context)));

        // The next request's system prompt carries the list. The first system message only announced the todo tool; no section had text yet.
        await (await root.SubmitAsync(SubmissionDraft.Input("What is next?"), Context)).WaitAsync(Context);
        var messages = (await root.ContextAsync(Context)).Messages;
        var systems = messages.Cast<JsonObjectValue>().Where(message => message["role"] is JsonStringValue { Value: "system" }).Select(message =>
        {
            var shown = new List<KeyValuePair<string, JsonValue>>();
            if (message.TryGetValue("sections", out var sections)) shown.Add(new("sections", sections));
            if (message.TryGetValue("toolsAdded", out var added))
                shown.Add(new("toolsAdded", JsonValue.Array(((JsonArrayValue)added).Select(tool => ((JsonObjectValue)tool)["name"]))));
            return (JsonValue)JsonValue.Object(shown);
        });
        await output.WriteLineAsync("system messages: " + Json(JsonValue.Array(systems)));

        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("todos: " + Json(await reopened.SnapshotAsync(Todos, root.Id, Context))));
    }

    // 12: run a durable task.
    private static async Task TasksAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A task is a small state machine. Its state, the checkpoint, is saved after every step, so after a crash the next open continues
        // from the last saved step. The usual pattern: save what you are about to do, do it, then save the result. A crash between doing and
        // saving reruns that step, so the step must be safe to repeat; here the fake payment service ignores a repeated key.
        var payments = new Dictionary<string, double>();
        var payment = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.payment",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "prepare")]),
            // One handler per phase. Each must save progress through runtime.CommitAsync(): its callback returns the next checkpoint or the
            // final outcome, and that state is saved in the same commit as everything else the callback wrote.
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["prepare"] = (task, runtime, taskContext) => runtime.CommitAsync((_, _) =>
                    NextTaskState.Running(JsonValue.Object([new("phase", "charge"), new("key", $"payment-{task.Id.Value}")])), taskContext),
                ["charge"] = (task, runtime, taskContext) =>
                {
                    var key = StringArg(task.Checkpoint, "key");
                    lock (payments)
                        if (!payments.ContainsKey(key)) payments[key] = ((JsonNumberValue)((JsonObjectValue)task.Input)["amount"]).Value * 100;
                    var receipt = payments[key];
                    return runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Object([new("receipt", receipt)])), taskContext);
                },
            },
            // Runs instead of the phases after harness.AbortTaskAsync(); it decides the outcome.
            Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
        });

        // Extensions bring task code; the Harness finds it by name in the registry. Nothing runs until Resume() or a call that waits for
        // progress, such as WaitForTaskAsync().
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "payments", Tasks = [payment] });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);
        var root = await harness.RootAsync(Context);
        // Every task names its owner. This one belongs to the conversation; a task can also own child tasks and wait for them
        // (24-child-tasks.ts).
        var paymentId = await root.CommitAsync(tx => tx.CreateTaskAsync(payment, JsonValue.Object([new("amount", 5)]),
            new TaskOptions(TaskOwnership.Conversation)), Context);
        // The finished task record is the durable receipt.
        var paid = await harness.WaitForTaskAsync(paymentId, Context);
        await output.WriteLineAsync("payment outcome: " + Json(((SettlingTaskState)paid.State).Outcome.ToJson()));

        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var record = await reopened.CommitAsync(tx => tx.GetTaskAsync(paymentId), Context);
            await output.WriteLineAsync("payment outcome: " + Json((record?.State as SettlingTaskState)?.Outcome.ToJson()));
        });
    }

    // 13: close, reopen, and continue where a task stopped.
    private static async Task RecoveryAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // Everything a task needs to continue is in storage, so a new Harness over the same storage picks up where the last one stopped.
        // Upstream keeps this example's storage in a SQLite file so it survives closing; memory storage does not, so on memory this example
        // does the same, and on a file backend it reopens the runner's storage.
        string? directory = null;
        var probe = await open();
        await probe.CloseAsync(Context);
        if (probe is MemoryStorage)
        {
            directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pi-durable-example-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            var databasePath = Path.Combine(directory, "session.sqlite");
            open = async () => await SqliteStorage.OpenAsync(databasePath);
        }

        // Called with each tick once it is printed. Upstream's first run closes the Harness right after tick 2 is printed and before that
        // phase's next commit is admitted, by microtask order; here the first run's hook holds the phase until close cancels it, a
        // deterministic barrier for the same situation.
        Func<int, CancellationToken, Task> reachedTick = (_, _) => Task.CompletedTask;
        var ticker = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.ticker",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "tick"), new("n", 1)]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["tick"] = async (task, runtime, taskContext) =>
                {
                    var n = (int)((JsonNumberValue)((JsonObjectValue)task.Checkpoint)["n"]).Value;
                    // Save the intent before the effect. A memo keeps the first value written under its name, so if the process dies after
                    // printing but before the next checkpoint is saved, the rerun sees the memo and does not print the same tick twice.
                    if (await runtime.MemoAsync($"printed-{n}", taskContext) is null)
                    {
                        await runtime.MemoAsync($"printed-{n}", JsonValue.Boolean(true), taskContext);
                        await output.WriteLineAsync($"tick {n}");
                    }
                    await reachedTick(n, runtime.Signal);
                    // Save the outcome: the next tick, or the final result.
                    var to = (int)((JsonNumberValue)((JsonObjectValue)task.Input)["to"]).Value;
                    await runtime.CommitAsync((_, _) => n == to
                        ? NextTaskState.Completed($"counted to {n}")
                        : NextTaskState.Running(JsonValue.Object([new("phase", "tick"), new("n", n + 1)])), taskContext);
                    // Wait a little between ticks. Closing the Harness cancels this wait; the checkpoint saved above is where the next Harness
                    // continues.
                    await runtime.SleepAsync(runtime.Now() + 50, taskContext);
                },
            },
            Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
        });
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "ticker", Tasks = [ticker] });
        async Task<IHarness> Open() => await Harness.OpenAsync(await open(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);

        // First run: start counting to 5, and close the Harness right after tick 2 is printed, before its next checkpoint is saved. That is
        // the same situation as a crash between the effect and saving its outcome.
        var firstRun = await Open();
        var tickerId = await (await firstRun.RootAsync(Context)).CommitAsync(tx => tx.CreateTaskAsync(ticker, JsonValue.Object([new("to", 5)]),
            new TaskOptions(TaskOwnership.Conversation)), Context);
        var tickTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reachedTick = async (n, signal) =>
        {
            if (n != 2) return;
            tickTwo.TrySetResult();
            var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (signal.Register(() => closing.TrySetResult())) await closing.Task;
        };
        firstRun.Resume();
        await tickTwo.Task;
        await firstRun.CloseAsync(Context);
        reachedTick = (_, _) => Task.CompletedTask;

        // Read the saved record through a Harness that never resumes, so nothing runs.
        var reader = await Open();
        var saved = (await reader.GetTaskAsync(tickerId, Context))!;
        await reader.CloseAsync(Context);
        await output.WriteLineAsync($"closed; saved checkpoint: {Json(saved.State.ToJson())} memos: {Json(saved.Memos)}");

        // Second run: nothing to restart by hand. Waiting for the unfinished task enables scheduling and continues it. Tick 2 runs again
        // because its outcome was never saved, but its memo says it was already printed.
        var secondRun = await Open();
        var counted = await secondRun.WaitForTaskAsync(tickerId, Context);
        await output.WriteLineAsync("after reopen: " + Json(((SettlingTaskState)counted.State).Outcome.ToJson()));
        await secondRun.CloseAsync(Context);
        if (directory is not null)
        {
            // Upstream: rm(directory, { recursive: true, force: true }).
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
