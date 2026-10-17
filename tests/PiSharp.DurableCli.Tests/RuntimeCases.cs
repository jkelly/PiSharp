// Authored for PiSharp (upstream packages/coding-agent/src/experimental/durable has no tests): the `pisharp durable` runtime
// (src/PiSharp.Cli/Durable: DurableRuntime, DurableHarnessSetup, DurableModels, DurablePrompt, DurableSubagent, DurableMain) against a
// Harness on a temporary agent directory, with a scripted model in place of the provider or a fake HTTP endpoint behind the CLI's live
// routes. runtime.kill-resume is milestone M3's headless case: a child process opens the runtime, is killed mid-turn, and the turn finishes
// after `--continue`.
using System.Collections.Immutable;
using System.Net;
using System.Text;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RuntimeCases() =>
    [
        Case("runtime.open.new-session-view", async () =>
        {
            await using var agent = new TestAgent("open");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue());
            var view = await UntilView(durable.View, current => current.Tasks is not null, "task panel open at start");
            Equal(durable.Location.Id, view.Session.Id, "session id");
            Equal(DurableSessions.RealPath(agent.Cwd), view.Session.Cwd, "session cwd");
            Equal(Path.Join(agent.AgentDir, "settings.json"), durable.Settings.GlobalPath, "settings in the agent directory");
            Equal("main", view.Conversations.Single().Label, "main conversation");
            Equal<string?>(null, view.Conversations.Single().Title, "main has no title");
            var agentDoc = DurableRuntime.AgentOf(view.Conversation);
            Equal(new ModelRef("faux", "faux-1"), DurableRuntime.ModelOf(agentDoc), "pi's default model from settings.json");
            Equal("low", DurableRuntime.ThinkingLevelOf(agentDoc), "pi's default thinking level");
            Equal(view.Session.Cwd, agentDoc.TryGetValue("cwd", out var cwd) && cwd is JsonStringValue text ? text.Value : null, "agent cwd");
            Equal("faux/faux-1 Faux One 128000,faux/faux-2 Faux Two 64000",
                string.Join(",", view.Models.Select(model => $"{model.Provider}/{model.ModelId} {model.Name} {model.ContextWindow}")), "available models");
            Check(view.Notices.IsEmpty, "no notices: " + string.Join(" | ", view.Notices.Select(notice => notice.Message)));
        }),
        Case("runtime.submit.prompt-steer-and-follow-up", async () =>
        {
            await using var agent = new TestAgent("submit");
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streams = new ScriptedStreams(request => ScriptedRequest.Text(request.Last) switch
            {
                "first" => ScriptedReply.Slow("first answer", 40, TimeSpan.FromMilliseconds(50)),
                var text => ScriptedReply.Answer("answer to " + text),
            });
            await using var durable = await agent.OpenAsync(streams);
            var notified = 0;
            using var subscription = durable.View.Subscribe(() => Interlocked.Increment(ref notified));
            await durable.Controller.SubmitAsync("first", WhenBusy.Steer);
            await UntilView(durable.View, view => !Idle(view), "busy");
            await durable.Controller.SubmitAsync("later", WhenBusy.FollowUp);
            var view = await UntilView(durable.View, current => Idle(current) && LastAnswer(current) == "answer to later", "follow-up answered");
            Equal("first,later", string.Join(",", view.Conversation.Entries.Where(entry => entry.Kind == "pi.user").Select(UserText)), "user entries");
            Check(view.Conversation.Entries.Any(entry => entry.Kind == "pi.assistant" && LastAnswerOf(entry) == "first answer"), "first answered");
            Check(Volatile.Read(ref notified) > 2, "listeners notified");
            // Idle again: Enter's steer prompts.
            await durable.Controller.SubmitAsync("again", WhenBusy.Steer);
            await UntilView(durable.View, current => LastAnswer(current) == "answer to again" && Idle(current), "idle steer prompts");
        }),
        Case("runtime.models.cycle-thinking-and-set-model", async () =>
        {
            await using var agent = new TestAgent("thinking");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue());
            string? Level(DurableView view) => DurableRuntime.ThinkingLevelOf(DurableRuntime.AgentOf(view.Conversation));
            await durable.Controller.CycleThinkingAsync();
            await UntilView(durable.View, view => Level(view) == "medium", "low -> medium");
            await durable.Controller.CycleThinkingAsync();
            await durable.Controller.CycleThinkingAsync();
            await UntilView(durable.View, view => Level(view) == "off", "high wraps to off");
            await durable.Controller.CycleThinkingAsync();
            await UntilView(durable.View, view => Level(view) == "minimal", "off -> minimal");
            // A model without reasoning: the level is clamped to off and cycling is refused.
            await durable.Controller.SetModelAsync(new ModelRef("faux", "faux-2"));
            var view = await UntilView(durable.View, current => DurableRuntime.ModelOf(DurableRuntime.AgentOf(current.Conversation))?.ModelId == "faux-2", "model set");
            Equal("off", Level(view), "clamped");
            await durable.Controller.CycleThinkingAsync();
            await UntilView(durable.View, current => current.Notices.Any(notice => notice.Message == "Current model does not support thinking"), "refused");
            await durable.Controller.SetModelAsync(new ModelRef("faux", "nope"));
            view = await UntilView(durable.View, current => current.Notices.Any(notice => notice.Message == "Unknown model: faux/nope"), "unknown model");
            Equal(DurableNoticeLevel.Error, view.Notices[^1].Level, "an error notice");
            Check(view.Notices.Select(notice => notice.Id).SequenceEqual(view.Notices.Select(notice => notice.Id).Order()), "notices keep their order");
        }),
        Case("runtime.tasks.toggle-panel", async () =>
        {
            await using var agent = new TestAgent("tasks");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue(ScriptedReply.Slow("done", 20, TimeSpan.FromMilliseconds(50))));
            await UntilView(durable.View, view => view.Tasks is not null, "open");
            await durable.Controller.SubmitAsync("go", WhenBusy.Steer);
            await UntilView(durable.View, view => view.Tasks is { Tasks.Count: > 0 }, "the running turn's task in the graph");
            await durable.Controller.ToggleTasksAsync();
            await UntilView(durable.View, view => view.Tasks is null, "closed");
            await durable.Controller.ToggleTasksAsync();
            await UntilView(durable.View, view => view.Tasks is not null, "reopened");
            await UntilView(durable.View, view => Idle(view) && view.Tasks is { Tasks.Count: 0 }, "graph empty once idle");
        }),
        Case("runtime.compact.nothing-to-compact", async () =>
        {
            await using var agent = new TestAgent("compact");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue(ScriptedReply.Answer("hello")));
            await durable.Controller.SubmitAsync("hi", WhenBusy.Steer);
            await UntilView(durable.View, view => LastAnswer(view) == "hello" && Idle(view), "answered");
            await durable.Controller.CompactAsync(null);
            await UntilView(durable.View, view => view.Notices.Any(notice =>
                notice.Message == "Nothing to compact: the context fits in the recent window that is kept verbatim."), "nothing to compact");
        }),
        Case("runtime.abort.ends-the-turn-without-a-notice", async () =>
        {
            await using var agent = new TestAgent("abort");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue(ScriptedReply.Endless(TimeSpan.FromMilliseconds(30))));
            await durable.Controller.SubmitAsync("go", WhenBusy.Steer);
            await UntilView(durable.View, view => !Idle(view), "busy");
            await durable.Controller.AbortAsync();
            var view = await UntilView(durable.View, Idle, "aborted");
            await Task.Delay(300);
            Check(!durable.View.Current.Notices.Any(notice => notice.Message.StartsWith("No answer", StringComparison.Ordinal)), "an aborted turn has no notice");
        }),
        Case("runtime.notice.unanswered-turn", async () =>
        {
            // pi's retry setting off, so the provider error settles the turn at once.
            await using var agent = new TestAgent("unanswered", settings: """{"defaultProvider":"faux","defaultModel":"faux-1","retry":{"enabled":false}}""");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue(ScriptedReply.Failure("boom")));
            await durable.Controller.SubmitAsync("go", WhenBusy.Steer);
            var view = await UntilView(durable.View, current => current.Notices.Any(notice => notice.Message.StartsWith("No answer: ", StringComparison.Ordinal)), "notice");
            Check(view.Notices.Last().Level == DurableNoticeLevel.Error, "error notice: " + view.Notices.Last().Message);
        }),
        Case("runtime.notice.no-model-and-unavailable-model", async () =>
        {
            await using (var empty = new TestAgent("nomodel", models: null, settings: null))
            await using (var durable = await empty.OpenAsync(ScriptedStreams.Queue()))
            {
                var view = await UntilView(durable.View, current => current.Notices.Length > 0, "notice");
                Equal("No model configured; select one with /model.", view.Notices[0].Message, "no model");
                Equal(DurableNoticeLevel.Warning, view.Notices[0].Level, "warning");
                Check(view.Models.IsEmpty, "no models");
                await durable.Controller.CycleThinkingAsync();
                await UntilView(durable.View, current => current.Notices.Any(notice => notice.Message == "No model selected"), "cycle without a model");
            }
            await using var agent = new TestAgent("unavailable");
            await (await agent.OpenAsync(ScriptedStreams.Queue())).DisposeAsync();
            File.Delete(Path.Combine(agent.AgentDir, "models.json"));
            await using var reopened = await agent.OpenAsync(ScriptedStreams.Queue(), continueSession: true);
            var after = await UntilView(reopened.View, current => current.Notices.Length > 0, "notice");
            Equal("Saved model is unavailable: faux/faux-1", after.Notices[0].Message, "unavailable");
        }),
        Case("runtime.continue.reopens-and-releases", async () =>
        {
            await using var agent = new TestAgent("continue");
            string directory;
            await using (var first = await agent.OpenAsync(ScriptedStreams.Queue(ScriptedReply.Answer("one"))))
            {
                directory = first.Location.Directory;
                await first.Controller.SubmitAsync("hi", WhenBusy.Steer);
                await UntilView(first.View, view => LastAnswer(view) == "one" && Idle(view), "answered");
                // A second process cannot open it while it is open.
                await ThrowsAsync<DurableSessionLockedException>(() => DurableRuntime.OpenAsync(new OpenDurableOptions { ContinueSession = true },
                    agent.Host(ScriptedStreams.Queue(), new PiLockOptions())), "locked");
            }
            Check(!Directory.Exists(directory + ".lock"), "dispose released the lock");
            await using var second = await agent.OpenAsync(ScriptedStreams.Queue(), continueSession: true);
            Equal(directory, second.Location.Directory, "same session");
            var view = second.View.Current;
            Equal("one", LastAnswer(view), "transcript survived");
            Check(view.Notices.IsEmpty, "no notices on continue");
            // A new session (no --continue) starts empty beside it.
            await using var third = await agent.OpenAsync(ScriptedStreams.Queue());
            Check(third.Location.Directory != directory && third.View.Current.Conversation.Entries.IsEmpty, "new session");
        }),
        Case("runtime.subagent.owned-child-conversation", async () =>
        {
            await using var agent = new TestAgent("subagent");
            var streams = new ScriptedStreams(request =>
            {
                var last = request.Last;
                if (last is not null && ScriptedRequest.Role(last) == "toolResult") return ScriptedReply.Answer("The subagent says: " + ScriptedRequest.Text(last));
                return ScriptedRequest.Text(last) == "Name three prime numbers."
                    ? ScriptedReply.Answer("2, 3, and 5.")
                    : ScriptedReply.Call("subagent", """{"task":"Name three prime numbers."}""", "call-1");
            });
            await using var durable = await agent.OpenAsync(streams);
            await durable.Controller.SubmitAsync("Use the subagent tool to find three prime numbers.", WhenBusy.Steer);
            var view = await UntilView(durable.View, current => LastAnswer(current) == "The subagent says: 2, 3, and 5." && Idle(current), "answered");
            var child = view.Conversations.Single(summary => summary.Id != ConversationId.Root);
            Equal($"subagent {child.Id.Value}", child.Label, "label");
            Equal("Name three prime numbers.", child.Title, "title is the task");
            // The child is owned by the subagent call's task, and its input carries the request id subagent:<taskId>.
            var record = await durable.Harness.CommitAsync(tx => tx.GetConversationAsync(child.Id), DurableRuntime.Context);
            Check(record?.Owner is { } owner, "owned");
            var taskId = record!.Owner!.TaskId;
            var task = await durable.Harness.GetTaskAsync(taskId, DurableRuntime.Context);
            Equal("pi.tool", task?.Kind, "owned by the tool task");
            var submission = await durable.Harness.CommitAsync(tx => tx.GetSubmissionByRequestAsync(child.Id, $"subagent:{taskId.Value}"), DurableRuntime.Context);
            Equal(SubmissionStatus.Done, submission?.Status, "the subagent's input by request id");
            // The child agent does not offer the subagent tool.
            var childAgent = await (await durable.Harness.ConversationAsync(child.Id, DurableRuntime.Context))!.AgentAsync(DurableRuntime.Context);
            Check(!childAgent.Tools.Any(tool => tool.Name == "subagent") && childAgent.Tools.Any(tool => tool.Name == "read"), "child tools");
            var result = view.Conversation.Entries.Single(entry => entry.Kind == "pi.tool-result");
            Check(result.Model is { Count: > 0 } resultMessages && resultMessages[0] is JsonObjectValue resultMessage &&
                resultMessage.TryGetValue("details", out var details) && details.ToJson() == $"{{\"conversationId\":{child.Id.Value}}}",
                "result details name the child: " + result.Model?.ToJson());
            // Switch to the child: the view shows and talks to it.
            await durable.Controller.SwitchConversationAsync(child.Id);
            var shown = await UntilView(durable.View, current => current.Conversation.Conversation.Id == child.Id, "switched");
            Equal("2, 3, and 5.", LastAnswer(shown), "child transcript");
            await durable.Controller.SwitchConversationAsync(new ConversationId(999));
            await UntilView(durable.View, current => current.Notices.Any(notice => notice.Message == "Conversation 999 does not exist"), "unknown conversation");
        }),
        Case("runtime.prompt.pi-sections", async () =>
        {
            await using var agent = new TestAgent("prompt");
            File.WriteAllText(Path.Combine(agent.Cwd, "AGENTS.md"), "Run the checks after changes.");
            var streams = ScriptedStreams.Queue(ScriptedReply.Answer("ok"));
            await using var durable = await agent.OpenAsync(streams);
            await durable.Controller.SubmitAsync("hi", WhenBusy.Steer);
            await UntilView(durable.View, view => LastAnswer(view) == "ok" && Idle(view), "answered");
            var system = streams.Requests[0].Messages.OfType<JsonObjectValue>().First(message => ScriptedRequest.Role(message) == "system");
            var sections = (JsonObjectValue)system["sections"];
            string Section(string key) => sections.TryGetValue(key, out var value) && value is JsonStringValue text ? text.Value : "";
            Equal("You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.",
                Section("preamble"), "preamble");
            Check(Section("tools").StartsWith("<tools>\n- read: Read file contents\n- write: Create or overwrite files\n- edit: Make precise", StringComparison.Ordinal) &&
                Section("tools").Contains("\n- bash: Execute bash commands (ls, grep, find, etc.)", StringComparison.Ordinal) &&
                !Section("tools").Contains("subagent", StringComparison.Ordinal), "tools: " + Section("tools"));
            Check(Section("rules").Contains("- Use bash for file operations like ls, rg, find\n- Use read to examine files instead of cat or sed.", StringComparison.Ordinal), "rules: " + Section("rules"));
            Check(Section("docs").StartsWith("<docs>\nPi documentation", StringComparison.Ordinal), "docs");
            Check(Section("project_context").Contains("Run the checks after changes.", StringComparison.Ordinal), "AGENTS.md");
            Equal($"<cwd>\n{durable.Location.Cwd.Replace('\\', '/')}\n</cwd>", Section("cwd"), "cwd");
            Equal("preamble,tools,rules,docs,project_context,cwd", string.Join(",", sections.Select(pair => pair.Key)), "pi's section order");
        }),
        Case("runtime.settings.bridge-reads-at-each-access", () =>
        {
            var settings = PiSettings.FromObjects(new JsonObject());
            var bridge = DurableHarnessSetup.CreateHarnessSettings(settings);
            Equal(300000d, bridge.Stream!.TimeoutMs, "idle timeout default");
            Equal(60000d, bridge.Stream!.MaxRetryDelayMs, "provider retry delay default");
            Equal<int?>(null, bridge.Stream!.MaxRetries, "no provider retries");
            Equal(new RetrySettings { Enabled = true, MaxRetries = 3, BaseDelayMs = 2000, MaxAgentDelayMs = 60000 }, bridge.Retry, "retry defaults");
            Equal(new CompactionSettings { Enabled = true, ReserveTokens = 16384, KeepRecentTokens = 20000 }, bridge.Compaction, "compaction defaults");
            Equal<QueueMode?>(QueueMode.OneAtATime, bridge.SteeringMode, "steering default");
            settings.ApplyOverrides(JsonNode.Parse("""
                {"httpIdleTimeoutMs":0,"retry":{"enabled":false,"maxRetries":5,"provider":{"maxRetries":2}},"compaction":{"keepRecentTokens":100},
                 "steeringMode":"all","followUpMode":"all"}
                """)!.AsObject());
            Equal(2147483647d, bridge.Stream!.TimeoutMs, "disabled idle timeout");
            Equal<int?>(2, bridge.Stream!.MaxRetries, "provider retries");
            Equal(new RetrySettings { Enabled = false, MaxRetries = 5, BaseDelayMs = 2000, MaxAgentDelayMs = 60000 }, bridge.Retry, "retry read again");
            Equal(100d, bridge.Compaction!.KeepRecentTokens, "compaction read again");
            Equal<QueueMode?>(QueueMode.All, bridge.FollowUpMode, "follow-up mode");
            settings.ApplyOverrides(JsonNode.Parse("""{"retry":{"provider":{"timeoutMs":1234}},"compaction":{"reserveTokens":-1}}""")!.AsObject());
            Equal(1234d, bridge.Stream!.TimeoutMs, "provider timeout wins");
            Equal("Invalid compaction.reserveTokens setting: -1. Expected a non-negative safe integer.",
                Throws<InvalidDataException>(() => _ = bridge.Compaction, "invalid").Message, "upstream message");
        }),
        Case("runtime.models.live-route-auth-and-headers", async () =>
        {
            await using var agent = new TestAgent("models", models: """
                {"providers":{"lab":{"baseUrl":"http://127.0.0.1:8123/v1","apiKey":"${LAB_KEY}","api":"openai-completions","authHeader":true,"headers":{"X-Team":"red"},
                  "models":[{"id":"lab-coder","reasoning":false,"input":["text"],"contextWindow":32768,"maxTokens":4096}]}}}
                """);
            // auth.json's stored key owns openai.
            File.WriteAllText(Path.Combine(agent.AgentDir, "auth.json"), """{"openai":{"type":"api_key","key":"sk-stored-openai"}}""");
            var endpoint = new FakeEndpoint();
            var runtime = agent.Runtime(() => endpoint, TestAgent.Env(("LAB_KEY", "lab-secret")));
            await using var models = await DurableModels.CreateAsync(runtime);
            var lab = models.GetModel("lab", "lab-coder")!;
            Equal("lab-coder", lab.Name, "models.json name defaults to the id");
            Check(models.GetAvailableSnapshot().Any(model => model.Provider == "lab") && models.GetAvailableSnapshot().Any(model => model.Provider == "openai"),
                "available: models.json and stored auth");
            Check(!models.GetAvailableSnapshot().Any(model => model.Provider == "anthropic"), "no anthropic auth");
            ImmutableArray<JsonValue> messages = [JsonValue.Parse("""{"role":"user","content":"hi","timestamp":1}""")];
            var answer = await models.CompleteSimpleAsync(lab, messages, null, CancellationToken.None);
            Equal(StopReason.Stop, answer.StopReason, "lab answered: " + answer.ExtraProperties);
            var seen = endpoint.Snapshot()[^1];
            Equal("http://127.0.0.1:8123/v1/chat/completions", seen.Url, "custom base URL");
            Equal("Bearer lab-secret", seen.Headers["Authorization"], "models.json key from the environment");
            Equal("red", seen.Headers["X-Team"], "models.json header");
            var openai = models.GetModel("openai", models.GetAvailableSnapshot().First(model => model.Provider == "openai").Id)!;
            answer = await models.CompleteSimpleAsync(openai, messages, new DurableStreamOptions { SessionId = "s1" }, CancellationToken.None);
            Equal(StopReason.Stop, answer.StopReason, "openai answered: " + answer.ExtraProperties);
            Equal("Bearer sk-stored-openai", endpoint.Snapshot()[^1].Headers["Authorization"], "auth.json key");
            // A compaction summary (another output cap, no caching) goes through the summary route.
            answer = await models.CompleteSimpleAsync(lab, messages, new DurableStreamOptions { MaxTokens = 1000, CacheRetention = "none" }, CancellationToken.None);
            Equal(StopReason.Stop, answer.StopReason, "summary answered");
            using (var body = System.Text.Json.JsonDocument.Parse(endpoint.Snapshot()[^1].Body!))
                Check(body.RootElement.ToString().Contains("1000", StringComparison.Ordinal), "summary cap sent: " + body.RootElement);
            // A provider without auth ends the stream with an error message instead of throwing.
            var anthropic = models.GetModel("anthropic", "claude-sonnet-4-5")!;
            answer = await models.CompleteSimpleAsync(anthropic, messages, null, CancellationToken.None);
            Equal(StopReason.Error, answer.StopReason, "anthropic without auth");
            // Thinking levels as pi-ai computes them.
            Equal("off", string.Join(",", DurableModels.SupportedThinkingLevels(lab)), "no reasoning");
            Check(DurableModels.SupportedThinkingLevels(anthropic).Contains("high"), "reasoning levels");
            Equal("off", DurableModels.ClampThinkingLevel(lab, "high"), "clamped");
        }),
        Case("runtime.models.host-transport-routes-github-copilot", async () =>
        {
            // GitHub Copilot needs a token exchange NativeDurableModels cannot do; the CLI's live route does it through the host hook.
            await using var agent = new TestAgent("copilot", models: null);
            File.WriteAllText(Path.Combine(agent.AgentDir, "auth.json"), """{"github-copilot":{"type":"api_key","key":"copilot-pat"}}""");
            var endpoint = new FakeEndpoint();
            await using var models = await DurableModels.CreateAsync(agent.Runtime(() => endpoint));
            var copilot = models.GetAvailableSnapshot().FirstOrDefault(model => model.Provider == "github-copilot" && model.Api == "openai-completions");
            Check(copilot is not null, "copilot available");
            Check(!NativeDurableModels.Supports(copilot!), "NativeDurableModels alone refuses it");
            var answer = await models.CompleteSimpleAsync(copilot!, [JsonValue.Parse("""{"role":"user","content":"hi","timestamp":1}""")], null, CancellationToken.None);
            Check(endpoint.Snapshot().Length > 0, "a request was sent through the live route: " + answer.ExtraProperties);
        }),
        Case("runtime.dispatch.unknown-argument-and-experimental-help", async () =>
        {
            await using var agent = new TestAgent("dispatch");
            var stdout = new StringWriter(); var stderr = new StringWriter();
            var host = new PiHost
            {
                Cwd = agent.Cwd, Home = agent.Home, GetEnvironment = TestAgent.Env(("PI_CODING_AGENT_DIR", agent.AgentDir)), Stdout = stdout, Stderr = stderr,
                LiveRuntime = agent.Runtime(),
            };
            Equal(1, await PiCommand.RunAsync(["durable", "--help"], host, CancellationToken.None), "exit code");
            Equal("Unknown argument: --help", stderr.ToString().Trim(), "upstream parseArgs message");
            Equal("", stdout.ToString(), "nothing on stdout");
            Check(!Directory.Exists(Path.Combine(agent.AgentDir, "experimental")), "no session opened");
            Equal(new OpenDurableOptions { ContinueSession = true }, DurableMain.ParseArgs(["-c"]), "-c");
            Equal(new OpenDurableOptions { ContinueSession = true }, DurableMain.ParseArgs(["--continue", "--continue"]), "--continue");
            Equal(new OpenDurableOptions(), DurableMain.ParseArgs([]), "no arguments");
            // Decision 0005 (1.2.0.1): listed in --help as experimental, PiSharp-only; not among the commands that take --help.
            var help = PiHelp.Text();
            Check(help.Contains("\n  pisharp durable [--continue]      EXPERIMENTAL: PiSharp's crash-safe durable agent (-c continues the newest session)\n",
                StringComparison.Ordinal), "listed in --help as experimental");
            Check(help.Split('\n').Count(line => line.Contains("durable", StringComparison.OrdinalIgnoreCase)) == 1, "one --help line names durable");
        }),
        Case("runtime.kill-resume.headless-turn-finishes-after-continue", async () =>
        {
            await using var agent = new TestAgent("kill");
            string directory;
            await using (var child = new Child(["--durable-runtime-child", agent.Root]))
            {
                var line = await child.WaitForLineAsync("CHECKPOINT ", TimeSpan.FromSeconds(90));
                directory = line["CHECKPOINT ".Length..];
                await child.KillAsync();
            }
            Check(Directory.Exists(directory + ".lock"), "the killed child left its lock");
            // The reopened process waits for the lock to go stale (10 s after the child's last renewal) and resumes the turn.
            var started = DateTime.UtcNow;
            var streams = ScriptedStreams.Queue(ScriptedReply.Answer("resumed answer"));
            await using var durable = await agent.OpenAsync(streams, continueSession: true, lockOptions: DurableSessions.DefaultLock);
            Equal(directory, durable.Location.Directory, "the killed session");
            Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(14), "opened within the stale time and retries");
            var view = await UntilView(durable.View, current => LastAnswer(current) == "resumed answer" && Idle(current), "turn finished", seconds: 30);
            Equal("go", string.Join(",", view.Conversation.Entries.Where(entry => entry.Kind == "pi.user").Select(UserText)), "one user entry");
            // The partial streamed before the kill stays as an aborted assistant entry; the resumed request gives the one answer.
            var answers = view.Conversation.Entries.Where(entry => entry.Kind == "pi.assistant").Select(entry =>
                (Text: LastAnswerOf(entry), Stop: entry.Model is { Count: > 0 } list && list[0] is JsonObjectValue message && message.TryGetValue("stopReason", out var stop) ? stop.ToJson() : "")).ToList();
            Equal(1, answers.Count(answer => answer.Stop == "\"stop\""), "one answer: " + string.Join(" | ", answers));
            Check(answers.Take(answers.Count - 1).All(answer => answer.Stop == "\"aborted\"" && answer.Text!.StartsWith("tick ", StringComparison.Ordinal)),
                "the interrupted partial: " + string.Join(" | ", answers));
            Check(!view.Notices.Any(notice => notice.Level == DurableNoticeLevel.Error), "no error notice: " + string.Join(" | ", view.Notices.Select(notice => notice.Message)));
            Check(streams.Requests.Count == 1, "the interrupted request was sent again once");
        }),
    ];

    private static string? LastAnswerOf(EntryRecord entry) =>
        entry.Model is { Count: > 0 } messages && messages[0] is JsonObjectValue message ? ScriptedRequest.Text(message) : null;

    /// <summary><c>--durable-runtime-child &lt;root&gt;</c>: opens a new durable session in the TestAgent layout under <c>root</c> with an
    /// endless scripted answer, submits "go", prints <c>CHECKPOINT &lt;session dir&gt;</c> once a streamed partial is committed, and waits to
    /// be killed.</summary>
    internal static async Task<int> RunRuntimeChildAsync(string root)
    {
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Environment.Exit(3), TaskScheduler.Default);
        try
        {
            var host = new DurableRuntimeHost
            {
                AgentDir = Path.Combine(root, "agent"), Home = Path.Combine(root, "home"), Cwd = Path.Combine(root, "project"), GetEnvironment = TestAgent.Env(),
                ApplicationDirectory = Path.Combine(root, "app"),
                LiveRuntime = new LiveSessionRuntime(TestAgent.Env(), () => null, Path.Combine(root, "agent", "auth.json"), ModelsPath: Path.Combine(root, "agent", "models.json")),
                Streams = ScriptedStreams.Queue(ScriptedReply.Endless(TimeSpan.FromMilliseconds(20))),
            };
            var durable = (DurableSession)await DurableRuntime.OpenAsync(new OpenDurableOptions(), host);
            await durable.Controller.SubmitAsync("go", WhenBusy.Steer);
            await UntilView(durable.View, view => view.Conversation.Doc("pi.live") is { } live && live.TryGetValue("generation", out var generation) &&
                generation is JsonObjectValue current && current.TryGetValue("message", out var message) && message is JsonObjectValue partial &&
                ScriptedRequest.Text(partial).Length > 0, "a committed partial", seconds: 60);
            Emit("CHECKPOINT " + durable.Location.Directory);
            await new TaskCompletionSource().Task;
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Emit("ERROR " + error.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }

    /// <summary>A fake provider endpoint: OpenAI completions and responses streams that say "ok"; it records each request.</summary>
    internal sealed class FakeEndpoint : HttpMessageHandler
    {
        internal sealed record Seen(string Url, Dictionary<string, string> Headers, string? Body);
        private readonly List<Seen> requests = [];
        internal Seen[] Snapshot() { lock (requests) return [.. requests]; }

        private const string CompletionsSse = "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        private const string ResponsesSse = "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[]}}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"item_id\":\"m\",\"delta\":\"ok\"}\n\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}}}\n\n";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase);
            var url = request.RequestUri!.ToString();
            var body0 = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (requests) requests.Add(new(url, headers, body0));
            if (url.Contains("copilot_internal", StringComparison.Ordinal) || url.Contains("/token", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"token":"tid=1;exp=9999999999;proxy-ep=proxy.individual.githubcopilot.com","expires_at":9999999999}""", Encoding.UTF8, "application/json") };
            var body = url.Contains("/chat/completions", StringComparison.Ordinal) ? CompletionsSse : url.EndsWith("/responses", StringComparison.Ordinal) ? ResponsesSse : null;
            return body is null
                ? new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"authored fake endpoint\"}}", Encoding.UTF8, "application/json") }
                : new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }
}
