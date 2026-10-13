// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/17-coding-tools.ts, 29-sandbox-per-conversation.ts and
// 30-tool-override.ts. Upstream stands in for a model with pi-ai's faux provider; here ScriptedModels does, so the examples run offline.
// Tools reach files and processes through LocalExecutionEnv (upstream NodeExecutionEnv) over fresh temporary directories; on Windows its
// shell is Git Bash. Each prints what upstream's console.log prints, with JSON in place of util.inspect. 17 runs on the storage the runner
// picks (upstream: JSONL in the example's directory), and on a file backend the storage is then reopened and the transcript read back.
using System.Diagnostics;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    private static string ExampleDirectory(string prefix) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..6])).FullName;

    private static string ResultText(JsonObjectValue result, Func<string, string>? part = null) =>
        string.Concat(((JsonArrayValue)result["content"]).Cast<JsonObjectValue>()
            .Select(item => item["type"] is JsonStringValue { Value: "text" } ? (part ?? (text => text))(((JsonStringValue)item["text"]).Value) : ""));

    /// <summary>Each entry's kind; a tool result also shows its tool and text, cut at 200 characters.</summary>
    private static async Task PrintTranscriptAsync(IEnumerable<EntryRecord> entries, TextWriter output)
    {
        foreach (var entry in entries)
        {
            if (!Entries.ToolResultEntry.Is(entry))
            {
                await output.WriteLineAsync(entry.Kind);
                continue;
            }
            var result = (JsonObjectValue)entry.Model![0];
            var text = ResultText(result);
            await output.WriteLineAsync($"{entry.Kind} {((JsonStringValue)result["toolName"]).Value}: " +
                Json(text.Length > 200 ? text[..200] + "\u2026" : text));
        }
    }

    // 17: a coding-agent tool turn: the model reads, edits, and runs commands, then answers.
    private static async Task CodingToolsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var directory = ExampleDirectory("pi-durable-example-");
        await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "hello world\n");

        // ScriptedModels plays the model: four tool-calling answers, then a final answer.
        var models = new ScriptedModels(
            ScriptedReply.ToolCall("read", JsonValue.Object([new("path", "notes.txt")]), "r"),
            ScriptedReply.ToolCall("edit", (JsonObjectValue)JsonValue.Parse("""{"path":"notes.txt","edits":[{"oldText":"world","newText":"durable"}]}"""), "e"),
            ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", "cat notes.txt")]), "b"),
            ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", "cat /tmp/1gb.txt")]), "c"),
            "The file now greets durable.");

        // Hooks see every call before and after execution; these time a `cat` of /tmp/1gb.txt (create it first to measure; without it that
        // call ends in an error result).
        static bool IsBigCat(JsonObjectValue call) => call["name"] is JsonStringValue { Value: "bash" } &&
            ((JsonStringValue)((JsonObjectValue)call["arguments"])["command"]).Value.Contains("1gb.txt", StringComparison.Ordinal);
        var start = 0L;
        var timing = new Extension
        {
            Name = "timing",
            Hooks =
            [
                Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                {
                    BeforeTool = (call, _, _) =>
                    {
                        if (IsBigCat(call)) start = Stopwatch.GetTimestamp();
                        return Task.FromResult<BeforeToolDecision?>(null);
                    },
                    AfterTool = async (call, _, _, _) =>
                    {
                        if (IsBigCat(call)) await output.WriteLineAsync($"cat /tmp/1gb.txt took {(long)Stopwatch.GetElapsedTime(start).TotalMilliseconds} ms");
                        return null;
                    },
                }),
            ],
        };
        var registry = Registry.Create();
        // CodingTools brings read, write, edit, and bash. Tools reach files and processes only through the environment the Harness builds
        // for each call.
        registry.Install(CodingTools.Extension);
        registry.Install(timing);

        // The environment is built per use and follows the conversation's agent cwd.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models,
            Registry = registry,
            Env = (target, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(target.Cwd ?? Directory.GetCurrentDirectory())),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux, Cwd = directory });

        // Each tool call runs as a durable pi.tool task owned by the generation, which waits for them and continues the run with the next
        // generation.
        var settled = await (await root.SubmitAsync(SubmissionDraft.Input("Greet durable instead."), Context)).WaitAsync(Context);
        await output.WriteLineAsync("status: " + settled.Status.ToString().ToLowerInvariant());
        var transcript = await root.EntriesAsync(new EntryQueryBounds(), 20, null, Context);
        await PrintTranscriptAsync(transcript.Items.Reverse(), output);
        if (settled is { Status: SubmissionStatus.Done, Type: SubmissionType.Input, Answer: { } answer })
        {
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), Context);
            var message = entry?.Model?[0] as JsonObjectValue;
            await output.WriteLineAsync("answer: " + Json(message?["role"] is JsonStringValue { Value: "assistant" } ? message["content"] : message));
        }
        await output.WriteLineAsync("file: " + Json(await File.ReadAllTextAsync(Path.Combine(directory, "notes.txt"))));
        await harness.CloseAsync(Context);
        await output.WriteLineAsync("storage: " + directory);

        await ReopenAsync(open, output, async reopened => await PrintTranscriptAsync(await TranscriptAsync(reopened, root.Id, 20), output));
        Directory.Delete(directory, recursive: true);
    }

    // 29: one sandbox per conversation. The app records each conversation's sandbox in its own document, and the Harness environment
    // function looks it up for every tool call. Here a sandbox is a directory; a hosted product would return an environment that runs inside
    // the conversation's container.
    private static readonly ConversationDocToken Sandbox =
        Documents.DefineConversationDoc("app.sandbox", 1, DocumentFork.Initial, () => JsonObjectValue.Empty);

    private static async Task SandboxPerConversationAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        static ScriptedReply Note(string text) =>
            ScriptedReply.ToolCall("write", JsonValue.Object([new("path", "note.txt"), new("content", text)]));
        var models = new ScriptedModels(Note("from alice"), "Saved.", Note("from bob"), "Saved.");

        var registry = Registry.Create();
        registry.Install(CodingTools.Extension);
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models,
            Registry = registry,
            // Committed reads only; a conversation without a sandbox gets no environment, so its tools fail cleanly.
            Env = async (target, envContext) =>
            {
                var sandbox = await target.Read.SnapshotAsync(Sandbox, target.ConversationId, envContext);
                return sandbox is not null && sandbox.TryGetValue("path", out var path) && path is JsonStringValue directory
                    ? new LocalExecutionEnv(directory.Value)
                    : null;
            },
        }, Context);

        // Each user's conversation gets a fresh sandbox in the creating commit.
        async Task<(IConversation Conversation, string Path)> ConversationFor(string user)
        {
            var path = ExampleDirectory($"pi-durable-sandbox-{user}-");
            var conversation = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
            {
                Agent = new AgentChange { Model = Faux },
                Init = async (tx, id) => (await tx.DocAsync(Sandbox, id)).Set("path", path),
            }, Context);
            return (conversation, path);
        }

        var alice = await ConversationFor("alice");
        var bob = await ConversationFor("bob");
        await (await alice.Conversation.SubmitAsync(SubmissionDraft.Input("Leave a note."), Context)).WaitAsync(Context);
        await (await bob.Conversation.SubmitAsync(SubmissionDraft.Input("Leave a note."), Context)).WaitAsync(Context);
        await output.WriteLineAsync("alice's sandbox: " + await File.ReadAllTextAsync(Path.Combine(alice.Path, "note.txt")));
        await output.WriteLineAsync("bob's sandbox: " + await File.ReadAllTextAsync(Path.Combine(bob.Path, "note.txt")));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            foreach (var (user, conversation) in new[] { ("alice", alice.Conversation.Id), ("bob", bob.Conversation.Id) })
            {
                var result = (await TranscriptAsync(reopened, conversation, 10)).First(entry => Entries.ToolResultEntry.Is(entry));
                await output.WriteLineAsync($"{user}'s write: {ResultText((JsonObjectValue)result.Model![0])}");
            }
        });
        Directory.Delete(alice.Path, recursive: true);
        Directory.Delete(bob.Path, recursive: true);
    }

    // 30: replace a tool for some conversations, and decorate whichever tool wins: a bash that runs inside a Python virtualenv, and a wrapper
    // that times every bash call.
    private static async Task ToolOverrideAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ─── Product code ───────────────────────────────────────────────

        // A bash with the same name: where selected after CodingTools, it replaces CodingTools' bash in place.
        var venv = new Extension
        {
            Name = "venv",
            Tools = [CodingTools.CreateBashTool(new BashToolOptions { CommandPrefix = "source .venv/bin/activate" })],
        };

        // Wraps the bash that won, whichever it is. Wrappers never capture a base tool, so reloading either bash keeps it.
        var timings = new List<long>();
        var timing = new Extension
        {
            Name = "timing",
            Wraps =
            [
                Define.WrapTool(CodingTools.CreateBashTool(), bash => bash with
                {
                    Execute = async (args, api, callContext) =>
                    {
                        var started = Stopwatch.GetTimestamp();
                        try { return await bash.Execute(args, api, callContext); }
                        finally { lock (timings) timings.Add((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
                    },
                }),
            ],
        };

        // ─── Host setup ─────────────────────────────────────────────────

        var project = ExampleDirectory("pi-durable-venv-");
        Directory.CreateDirectory(Path.Combine(project, ".venv", "bin"));
        await File.WriteAllTextAsync(Path.Combine(project, ".venv/bin/activate"), $"export VIRTUAL_ENV=\"{project}/.venv\"\n");

        static ScriptedReply Probe() => ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", "echo venv: $VIRTUAL_ENV")]));
        var models = new ScriptedModels(Probe(), "Done.", Probe(), "Done.");

        var registry = Registry.Create();
        registry.Install(CodingTools.Extension);
        registry.Install(timing);
        registry.Install(venv);
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models,
            Registry = registry,
            // Venv is installed but not selected by default.
            Settings = new HarnessSettings { Extensions = [CodingTools.Extension, timing] },
            Env = (_, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(project)),
        }, Context);
        var plain = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        var python = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
        {
            Agent = new AgentChange { Model = Faux, Extensions = ExtensionSelection.Edit(add: [venv]) },
        }, Context);

        async Task<string> ProbeBash(IConversation conversation)
        {
            await (await conversation.SubmitAsync(SubmissionDraft.Input("Which venv?"), Context)).WaitAsync(Context);
            var page = await conversation.EntriesAsync(new EntryQueryBounds(), 10, null, Context);
            var result = (JsonObjectValue)page.Items.First(entry => Entries.ToolResultEntry.Is(entry)).Model![0];
            return ResultText(result, text => text.Trim());
        }

        await output.WriteLineAsync("plain conversation: " + await ProbeBash(plain));
        await output.WriteLineAsync("python conversation: " + (await ProbeBash(python)).Replace(project, "<project>", StringComparison.Ordinal));
        await output.WriteLineAsync("timed bash calls: " + timings.Count);
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var result = (await TranscriptAsync(reopened, python.Id, 10)).First(entry => Entries.ToolResultEntry.Is(entry));
            await output.WriteLineAsync("python conversation: " +
                ResultText((JsonObjectValue)result.Model![0], text => text.Trim()).Replace(project, "<project>", StringComparison.Ordinal));
        });
        Directory.Delete(project, recursive: true);
    }
}
