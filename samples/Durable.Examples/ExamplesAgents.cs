// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/16-real-model.ts, 18-print.ts, 26-coding-agent.ts,
// 27-plan-mode.ts and 28-reviewer.ts. 16 needs a real OpenAI key: without OPENAI_API_KEY (read through the environment reader the runner
// passes, as NativeDurableModels reads keys) it prints upstream's skip line; with it, it streams the answer through NativeDurableModels.
// 18 uses OpenAI when the key is set upstream and pi-ai's faux provider otherwise; here ScriptedModels always stands in, as in 26, 27 and
// 28, so they run offline. Tools reach files and processes through LocalExecutionEnv (upstream NodeExecutionEnv); on Windows its shell is
// Git Bash. Each prints what upstream's console.log prints, with JSON in place of util.inspect (JSON.stringify drops an undefined field).
// Upstream runs them on MemoryStorage; here they run on the runner's storage, and a file backend is reopened and read back.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // The process environment by default; the tests pass one without OPENAI_API_KEY.
    private static Func<string, string?> environment = Environment.GetEnvironmentVariable;

    // 16: a real model: stream an answer from OpenAI.
    private static async Task RealModelAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // Production code passes model access with real providers; the Harness never talks to a provider any other way. NativeDurableModels
        // reads OPENAI_API_KEY through the environment reader. While the answer streams, generation commits throttled partials to the
        // conversation's pi.live document. Watching that document streams the answer; the watch sees only committed values.
        if (string.IsNullOrEmpty(environment("OPENAI_API_KEY")))
        {
            await output.WriteLineAsync("skipped: OPENAI_API_KEY is not set");
            return;
        }
        using var models = new NativeDurableModels(new NativeDurableModelsOptions { Environment = environment });
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "concise", Sections = [Define.Section("preamble", "You are a concise assistant.", tag: false)] });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions { Models = models, Registry = registry }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("openai", "gpt-6-sol"), ThinkingLevel = "high" });
        var liveWatch = (await harness.WatchDocAsync(Live.LiveDoc, root.Id, Context))!;
        // Print only what each committed partial adds to the text printed so far.
        var printed = "";
        var gate = new Lock();
        void PrintText(string text)
        {
            lock (gate)
            {
                if (text.Length <= printed.Length || !text.StartsWith(printed, StringComparison.Ordinal)) return;
                output.Write(text[printed.Length..]);
                printed = text;
            }
        }
        liveWatch.Start((value, _, _) =>
        {
            if (value is not null && value.TryGetValue("generation", out var generation) && generation is JsonObjectValue live &&
                live.TryGetValue("message", out var message)) PrintText(TextOf(message as JsonObjectValue));
            return Task.CompletedTask;
        });
        harness.Resume();
        await output.WriteAsync("answer: ");
        var poem = await root.SubmitAsync(SubmissionDraft.Input("Write a long poem"), Context);
        var settledPoem = await poem.WaitAsync(Context);
        await liveWatch.StopAsync();
        if (settledPoem is { Status: SubmissionStatus.Done, Type: SubmissionType.Input, Answer: { } answer })
        {
            // The last throttle window may not have been committed as a partial; the answer entry has the rest.
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), Context);
            PrintText(TextOf(entry?.Model?[0] as JsonObjectValue));
            await output.WriteLineAsync();
        }
        else await output.WriteLineAsync($"unanswered: {settledPoem.Reason} {settledPoem.Detail?.ToJson()}");
        await harness.CloseAsync(Context);
    }

    // 18: print mode: submit one prompt and print its answer, like `pi -p`. The host awaits its own submission, not global idle.
    private static async Task PrintAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        const string prompt = "What is in this directory?";
        var models = new ScriptedModels(
            ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", "ls")]), "call-1"),
            "This directory holds the durable package sources, tests, and docs.");
        var registry = Registry.Create();
        registry.Install(new Extension
        {
            Name = "coding",
            Tools = [CodingTools.CreateReadTool(), CodingTools.CreateBashTool()],
            Sections = [Define.Section("preamble", "You are a concise coding assistant.", tag: false)],
        });
        var env = new LocalExecutionEnv(Directory.GetCurrentDirectory());
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models, Registry = registry, Env = (_, _) => Task.FromResult<IExecutionEnv?>(env),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });

        var submission = await root.SubmitAsync(SubmissionDraft.Input(prompt), Context);
        var settled = await submission.WaitAsync(Context);
        EntryId? answerId = null;
        if (settled is { Status: SubmissionStatus.Done, Type: SubmissionType.Input, Answer: { } answer })
        {
            var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), Context);
            await output.WriteLineAsync(TextOf(entry?.Model?[0] as JsonObjectValue));
            answerId = answer;
        }
        else await output.WriteLineAsync($"unanswered: {(settled.Status == SubmissionStatus.Unanswered ? settled.Reason : "done")}");
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var entry = answerId is { } id ? await reopened.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, id), Context) : null;
            await output.WriteLineAsync(TextOf(entry?.Model?[0] as JsonObjectValue));
        });
    }

    // Settings the user edits while the agent runs, like a settings.json the app watches: the getters make every Harness read see the current
    // values; nothing is copied or stored.
    private sealed class UserSettings : HarnessSettings
    {
        public bool ParallelTools { get; set; } = true;
        public int MaxRetries { get; set; } = 3;
        public override ToolExecutionMode? ToolExecution { get => ParallelTools ? ToolExecutionMode.Parallel : ToolExecutionMode.Sequential; init { } }
        public override RetrySettings? Retry { get => new() { MaxRetries = MaxRetries }; init { } }
    }

    // 26: a small coding agent: the built-in coding tools, a prompt that knows the working directory, settings backed by the app's settings
    // file, and an environment that follows the conversation's directory.
    private static async Task CodingAgentAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        var workspace = ExampleDirectory("pi-durable-agent-");
        Directory.CreateDirectory(Path.Combine(workspace, "app"));

        // The app's own prompt, next to the coding tools.
        var coding = new Extension
        {
            Name = "coding",
            Sections =
            [
                Define.Section("preamble", "You are a coding agent. Use the tools to inspect the project.", tag: false),
                Define.Section("cwd", (input, _) => ValueTask.FromResult(input.Env?.Cwd)),
            ],
        };
        var registry = Registry.Create();
        registry.Install(CodingTools.Extension);
        registry.Install(coding);
        var settings = new UserSettings();

        static ScriptedReply Pwd() => ScriptedReply.ToolCall("bash", JsonValue.Object([new("command", "pwd")]));
        var models = new ScriptedModels(Pwd(), "Done.", Pwd(), "Done.");
        // The Harness calls this for every tool call and request with the conversation's cwd, so changing a conversation's directory needs
        // no restart.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models, Registry = registry, Settings = settings,
            Env = (target, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(target.Cwd ?? workspace)),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux, Cwd = workspace });

        static string LastBashOutput(IEnumerable<EntryRecord> newestFirst) =>
            ResultText((JsonObjectValue)newestFirst.First(entry => Entries.ToolResultEntry.Is(entry)).Model![0], text => text.Trim());
        async Task<IEnumerable<EntryRecord>> Newest() => (await root.EntriesAsync(new EntryQueryBounds(), 10, null, Context)).Items;

        await (await root.SubmitAsync(SubmissionDraft.Input("Where are we?"), Context)).WaitAsync(Context);
        await output.WriteLineAsync("bash ran in: " + LastBashOutput(await Newest()));

        // The user switches the project directory and turns off parallel tools. Both apply from the next use.
        await root.ConfigureAsync(new AgentChange { Cwd = Path.Combine(workspace, "app") }, Context);
        settings.ParallelTools = false;
        await (await root.SubmitAsync(SubmissionDraft.Input("And now?"), Context)).WaitAsync(Context);
        await output.WriteLineAsync("bash ran in: " + LastBashOutput(await Newest()));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
        {
            var results = (await TranscriptAsync(reopened, root.Id, 20)).Where(entry => Entries.ToolResultEntry.Is(entry));
            foreach (var result in results)
                await output.WriteLineAsync("bash ran in: " + ResultText((JsonObjectValue)result.Model![0], text => text.Trim()));
        });
        Directory.Delete(workspace, recursive: true);
    }

    // 27: plan mode: the user switches a conversation into a read-only planning mode, the agent writes a plan into the extension's own
    // document, and switching back restores the full tool set.

    // The current plan of a conversation. A fork keeps the plan it had at the fork entry.
    private static readonly RewindableConversationDocToken PlanDoc =
        Documents.DefineRewindableConversationDoc("app.plan", 1, DocumentFork.AsOf, () => JsonValue.Object([new("steps", JsonValue.Array())]));

    private static async Task PlanModeAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ─── Product code: the plan extension ───────────────────────────────────────

        var submitPlan = Define.Tool(new ToolRegistration
        {
            Name = "submit_plan",
            Description = "Submit the plan as a list of steps.",
            Parameters = (JsonObjectValue)JsonValue.Parse(
                """{"type":"object","required":["steps"],"properties":{"steps":{"type":"array","items":{"type":"string"}}}}"""),
            Execute = async (args, api, callContext) =>
            {
                await api.CommitAsync(async tx => (await tx.DocAsync(PlanDoc, api.ConversationId)).Set("steps", ((JsonObjectValue)args)["steps"]),
                    callContext);
                return new ToolExecutionResult
                {
                    Content = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", "Plan submitted.")])]),
                    Control = new ToolControl { Terminate = true },
                };
            },
        });
        var plan = new Extension
        {
            Name = "plan",
            Tools = [submitPlan],
            Sections = [Define.Section("plan_mode", "You are in plan mode. Read the code, then call submit_plan. Change nothing.")],
        };

        // Plan mode is a change to the conversation's agent: select the plan extension and offer only reading and submitting. Clearing both
        // returns to the host's default selection and every tool.
        var read = CodingTools.CreateReadTool();
        var enterPlanMode = new AgentChange { Extensions = ExtensionSelection.Edit(add: [plan]), Tools = ToolSelection.Only(read, submitPlan) };
        var leavePlanMode = new AgentChange { Extensions = Patch<ExtensionSelection>.Clear, Tools = Patch<ToolSelection>.Clear };

        // ─── Host setup ─────────────────────────────────────────────────────────────

        var directory = ExampleDirectory("pi-durable-plan-");
        await File.WriteAllTextAsync(Path.Combine(directory, "server.ts"), "app.listen(3000);\n");
        var models = new ScriptedModels(
            ScriptedReply.ToolCall("read", JsonValue.Object([new("path", "server.ts")])),
            ScriptedReply.ToolCall("submit_plan", (JsonObjectValue)JsonValue.Parse("""{"steps":["Read PORT from the environment","Default to 3000"]}""")),
            "Implementing step 1.");
        var registry = Registry.Create();
        registry.Install(CodingTools.Extension);
        registry.Install(plan);
        // Installed, but only CodingTools is selected by default: conversations opt into plan mode.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models, Registry = registry, Settings = new HarnessSettings { Extensions = [CodingTools.Extension] },
            Env = (_, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(directory)),
        }, Context);
        var root = await harness.RootAsync(Context, new AgentChange { Model = Faux });
        async Task<string> Tools() => JsonNames((await root.AgentAsync(Context)).Tools.Select(tool => tool.Name));
        await output.WriteLineAsync("tools: " + await Tools());

        await root.ConfigureAsync(enterPlanMode, Context);
        await output.WriteLineAsync("plan mode tools: " + await Tools());
        await (await root.SubmitAsync(SubmissionDraft.Input("Make the port configurable."), Context)).WaitAsync(Context);
        await output.WriteLineAsync("plan: " + Json((await harness.SnapshotAsync(PlanDoc, root.Id, Context))?["steps"]));

        await root.ConfigureAsync(leavePlanMode, Context);
        await output.WriteLineAsync("tools again: " + await Tools());
        await (await root.SubmitAsync(SubmissionDraft.Input("Go ahead."), Context)).WaitAsync(Context);

        // The model saw each switch as a system prompt change in its transcript.
        var view = await root.ContextAsync(Context);
        foreach (var message in view.Messages.OfType<JsonObjectValue>())
        {
            if (message["role"] is not JsonStringValue { Value: "system" }) continue;
            static JsonValue? Names(JsonObjectValue message, string key) => message.TryGetValue(key, out var tools) && tools is JsonArrayValue list
                ? JsonValue.Array(list.OfType<JsonObjectValue>().Select(tool => tool["name"]))
                : null;
            var fields = new List<KeyValuePair<string, JsonValue>>();
            // A null section value removes it.
            if (message.TryGetValue("sections", out var sections)) fields.Add(new("sections", sections));
            if (Names(message, "toolsAdded") is { } added) fields.Add(new("added", added));
            if (Names(message, "toolsRemoved") is { } removed) fields.Add(new("removed", removed));
            await output.WriteLineAsync("system: " + Json(JsonValue.Object(fields)));
        }
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened =>
            await output.WriteLineAsync("plan: " + Json((await reopened.SnapshotAsync(PlanDoc, root.Id, Context))?["steps"])));
        Directory.Delete(directory, recursive: true);
    }

    // 28: a reviewer agent next to the main one: a cheaper model, a review role and loop, read-only tools, and its own checkout of the
    // project.
    private static async Task ReviewerAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // ─── Product code: the reviewer extension ───────────────────────────────────

        const string done = "No further findings.";
        // A role and a review loop: every answer that still has findings gets a second pass.
        var reviewerExtension = new Extension
        {
            Name = "reviewer",
            Sections = [Define.Section("role", "You review diffs. Report problems as a list. Never edit files.")],
            Hooks =
            [
                Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    OnYield = (answer, _, _) => Task.FromResult<JsonValue?>(TextOf(answer).Contains(done, StringComparison.Ordinal)
                        ? null
                        : JsonValue.String($"Look again for anything you missed. Say \"{done}\" when there is nothing left.")),
                }),
            ],
        };

        // ─── Host setup ─────────────────────────────────────────────────────────────

        // The reviewer works in its own checkout, in practice a `git worktree add`.
        var worktree = ExampleDirectory("pi-durable-review-");
        await File.WriteAllTextAsync(Path.Combine(worktree, "user.ts"), "export const name = (user) => user.name;\n");
        var models = new ScriptedModels(
            ScriptedReply.ToolCall("read", JsonValue.Object([new("path", "user.ts")])),
            "1. `name` does not handle a missing user.",
            $"2. `user` has no type. {done}")
        { ModelIds = ["big", "small"] };
        var registry = Registry.Create();
        registry.Install(CodingTools.Extension);
        registry.Install(reviewerExtension);
        // The main agent selects only CodingTools; the reviewer opts in.
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = models, Registry = registry, Settings = new HarnessSettings { Extensions = [CodingTools.Extension] },
            Env = (target, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(target.Cwd ?? Directory.GetCurrentDirectory())),
        }, Context);
        await harness.RootAsync(Context, new AgentChange { Model = new ModelRef("faux", "big") });

        // Everything the reviewer is, stored on its conversation: the model, exactly these extensions in this order, only the read tool, and
        // its directory. A restart keeps all of it.
        var reviewer = await harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless)
        {
            Agent = new AgentChange
            {
                Model = new ModelRef("faux", "small"),
                Extensions = ExtensionSelection.Only(CodingTools.Extension, reviewerExtension),
                Tools = ToolSelection.Only(CodingTools.CreateReadTool()),
                Cwd = worktree,
            },
        }, Context);
        var agent = await reviewer.AgentAsync(Context);
        await output.WriteLineAsync($"reviewer: {agent.Model?.ModelId} {JsonNames(agent.Extensions.Select(extension => extension.Name))} " +
            $"{JsonNames(agent.Tools.Select(tool => tool.Name))} {(agent.Cwd == worktree ? "true" : "false")}");

        await (await reviewer.SubmitAsync(SubmissionDraft.Input("Review user.ts."), Context)).WaitAsync(Context);
        var page = await reviewer.EntriesAsync(new EntryQueryBounds(), 20, null, Context);
        await PrintReviewAsync(page.Items.Reverse(), output);
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, async reopened => await PrintReviewAsync(await TranscriptAsync(reopened, reviewer.Id, 20), output));
        Directory.Delete(worktree, recursive: true);
    }

    private static async Task PrintReviewAsync(IEnumerable<EntryRecord> entries, TextWriter output)
    {
        foreach (var entry in entries)
        {
            var message = entry.Model?[0] as JsonObjectValue;
            if (message?["role"] is JsonStringValue { Value: "user" })
                await output.WriteLineAsync("> " + (message["content"] is JsonStringValue text ? text.Value : message["content"].ToJson()));
            if (Entries.AssistantEntry.Is(entry) && TextOf(message) != "") await output.WriteLineAsync("reviewer: " + TextOf(message));
        }
    }
}
