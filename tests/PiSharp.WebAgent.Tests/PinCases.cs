using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Arena;
using PiSharp.WebAgent.Evals.Pins;
using PiSharp.WebAgent.Runs;

// Decision 0011 (run W5, E7): Pin as test case. The brief's cases: pinning from each source (the Playground, History, the inspector, Compare,
// an Arena reveal after the vote and a chat message, plus the suite page's Add case and Edit case) stores a case a suite run answers, every
// check passing (Compare's column too, W5 fix 1); the over-cap pane says the schedule pauses and saving pauses it, its text plain with an
// "over its cap" warn chip; nothing pinned holds a secret (the modal, the save's answer, the case, the suite export, eval_cases and the log,
// with planted environment and stored keys; a run whose stored options carry a credential header, a credential's name at any depth of the
// provider options, a key-shaped value under a neutral header name, or a stored key's value anywhere is refused without echoing it, and so
// is the same request sent to the cases API). Also: targets, refusals (a run still going, a
// stopped run, an agent run, a blind battle before its vote), the chat transcript replayed as the provider received it, the suggested
// checks and the modal's texts. Faux and priced-faux transports only.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> PinCases() =>
    [
        Case("pins.target.reads-one-thing-to-pin", () =>
        {
            var run = PinTarget.From("playground", "r1", null, null, null, null, null);
            Check(run is { Source: PinSource.Playground, RunId: "r1", Mode: PinMode.Pin }, "a run: " + run);
            var chat = PinTarget.From(null, null, " chat-1 ", "3", "17", null, null);
            Check(chat is { Source: PinSource.Chat, ChatId: "chat-1", AgentId: 3, EntryId: 17, Mode: PinMode.Pin }, "a chat message, trimmed: " + chat);
            var suiteId = EvalIds.New(EvalIds.Suite);
            var caseId = EvalIds.New(EvalIds.Case);
            Check(PinTarget.From(null, null, null, null, null, suiteId, null) is { Source: PinSource.Suite, Mode: PinMode.Add }, "a suite: Add case");
            Check(PinTarget.From(null, null, null, null, null, null, caseId) is { Source: PinSource.Suite, Mode: PinMode.Edit }, "a case: Edit case");
            Check(PinTarget.From(null, "r1", null, null, null, null, null).Source == PinSource.History, "a run without a source is History's");
            var json = chat.ToJson();
            Equal("{\"source\":\"chat\",\"chat\":\"chat-1\",\"agent\":\"3\",\"entry\":\"17\"}", json.ToJsonString(), "as pin.js sends it back");
            Equal(chat, PinTarget.FromJson(json), "and read again");
            Equal(17L, PinTarget.FromJson(JsonNode.Parse("{\"chat\":\"c\",\"entry\":17}")!.AsObject()).EntryId, "a number is read as one");
            Throws<ArgumentException>(() => PinTarget.From(null, "r1", "c", null, "1", null, null), "a run and a chat message at once");
            Throws<ArgumentException>(() => PinTarget.From("chat", null, "c", null, null, null, null), "a chat without the message");
            Throws<ArgumentException>(() => PinTarget.From(null, null, null, null, null, null, null), "nothing to pin");
            Throws<ArgumentException>(() => PinTarget.From("nope", "r1", null, null, null, null, null), "an unknown source");
            Throws<ArgumentException>(() => PinTarget.From(null, null, "c", "x", "1", null, null), "an agent that is not a number");
            Throws<ArgumentException>(() => PinTarget.From(null, null, null, null, null, "not-a-suite", null), "a suite id of the wrong kind");
            Throws<ArgumentException>(() => PinTarget.From(null, "r\u0001", null, null, null, null, null), "a control character");
            Throws<ArgumentException>(() => PinTarget.FromJson(null), "no target");
        }),

        Case("pins.rules.offer-a-finished-call-and-say-why-not", () =>
        {
            Check(PinRules.Offered(PinRun("ok")), "a completed Playground call");
            Check(!PinRules.Offered(null), "no run");
            Equal("This run is still going. Pin it once it finishes.", PinRules.Refusal(PinRun("live", status: RunStatus.Running)), "running");
            Check(PinRules.Refusal(PinRun("bad", status: RunStatus.Error))!.EndsWith("(it failed).", StringComparison.Ordinal), "failed");
            Check(PinRules.Refusal(PinRun("stop", status: RunStatus.Aborted))!.EndsWith("(it was stopped).", StringComparison.Ordinal), "stopped");
            Check(PinRules.Refusal(PinRun("gone", status: RunStatus.Interrupted))!.EndsWith("(it was interrupted).", StringComparison.Ordinal), "interrupted");
            Equal(PinRules.AgentLater, PinRules.Refusal(PinRun("parent") with { Kind = RunKind.Parent }), "an agent compare's parent");
            Equal(PinRules.AgentLater, PinRules.Refusal(PinRun("turn", source: "agent-compare")), "an agent compare's turn");
            Check(PinRules.Offered(PinRun("chat", source: "chat") with { Session = "chat-1", MessagesRef = "chat:chat-1" }), "a chat's call (its request is the chat)");
            Check(PinRules.Refusal(PinRun("orphan", source: "chat") with { MessagesRef = "chat:x" })!.Contains("doesn't name its chat", StringComparison.Ordinal), "a chat call without its chat");
            Check(PinRules.Refusal(PinRun("ref") with { MessagesRef = "elsewhere" })!.Contains("didn't store its request", StringComparison.Ordinal), "a call whose request is elsewhere");
            Check(PinRules.Offered(PinRun("arena", source: "arena")) && PinRules.Offered(PinRun("bench", source: "bench")), "Arena and Tool Bench calls (a battle's vote is checked when pinned)");
        }),

        Case("pins.chat.replays-the-transcript-as-the-provider-received-it", () =>
        {
            static JsonObject Entry(long id, string kind, JsonObject message) => new() { ["id"] = id, ["kind"] = kind, ["model"] = new JsonArray(message) };
            static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
            var entries = new List<JsonObject>
            {
                Entry(1, "pi.system", new JsonObject
                {
                    ["role"] = "system", ["content"] = "Base prompt.",
                    ["sections"] = new JsonObject { ["10"] = "ten", ["tools"] = "<tools>", ["2"] = "two" },
                    ["toolsAdded"] = new JsonArray(
                        new JsonObject { ["name"] = "read", ["description"] = "Read a file.", ["parameters"] = new JsonObject { ["type"] = "object" } },
                        new JsonObject { ["name"] = "bash", ["description"] = "Run a command." }),
                }),
                Entry(2, "pi.user", new JsonObject
                {
                    ["role"] = "user", ["content"] = new JsonArray(Text("Look at this."), new JsonObject { ["type"] = "image", ["data"] = "aGk=", ["mimeType"] = "image/png" }),
                }),
                Entry(3, "pi.assistant", new JsonObject { ["role"] = "assistant", ["stopReason"] = "error", ["content"] = new JsonArray(Text("half an answer")) }),
                Entry(4, "pi.system", new JsonObject
                {
                    ["role"] = "system", ["sections"] = new JsonObject { ["2"] = "TWO", ["tools"] = null },
                    ["toolsRemoved"] = new JsonArray(new JsonObject { ["name"] = "bash" }),
                }),
                Entry(5, "pi.assistant", new JsonObject
                {
                    ["role"] = "assistant", ["stopReason"] = "toolUse",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "thinking", ["thinking"] = "Read it." },
                        new JsonObject { ["type"] = "toolCall", ["id"] = "call-1", ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = "a.txt" } }),
                }),
                Entry(6, "pi.toolResult", new JsonObject
                {
                    ["role"] = "toolResult", ["toolCallId"] = "call-1", ["toolName"] = "read", ["isError"] = false, ["content"] = new JsonArray(Text("hello file")),
                }),
                Entry(7, "pi.assistant", new JsonObject { ["role"] = "assistant", ["stopReason"] = "stop", ["content"] = new JsonArray(Text("It says "), Text("hello file.")) }),
            };
            var (system, messages, tools) = PinMessages.Request(entries, 6);
            Equal("Base prompt.\n\nTWO\n\nten", system, "the system text, then the sections (numbered first, in order; a removed one gone)");
            Equal("user,assistant,toolResult", string.Join(",", messages.Select(message => message.Role)), "every message before the answer, without the failed one");
            Check(messages[0] is { Text: "Look at this.", Images: [{ MimeType: "image/png", Data: "aGk=" }] }, "the user's text and image");
            Check(messages[1] is { ToolCalls: [{ Id: "call-1", Name: "read" }], Thinking: [{ Text: "Read it." }] }, "the tool call and its thinking");
            Check(messages[2] is { ToolCallId: "call-1", ToolName: "read", Text: "hello file", IsError: false }, "the tool result");
            Check(tools is [{ Name: "read", Description: "Read a file." }], "the tools still declared (bash was removed): " + string.Join(",", tools.Select(tool => tool.Name)));
            Equal("It says hello file.", PinMessages.Text(entries[6]["model"]![0]!.AsObject()), "the answer's text");
            var spec = TestCase.CaseSpec(new RunSpec { Provider = "faux", Model = "echo", SystemPrompt = system, Messages = messages, Tools = tools, Source = "chat", Account = "acct_1" });
            Check(spec is { Account: null, Source: null } && spec.Messages.Count == 3 && spec.Tools is { Count: 1 }, "a valid case request, without the chat's source or account");
            var (none, first, noTools) = PinMessages.Request(entries, 0);
            Check(none is null && first.Count == 0 && noTools.Count == 0, "nothing before the first entry");
        }),

        Case("pins.text.limits-checks-line-panes-and-toast", () =>
        {
            Check(PinService.Limits("$0.03 30 s") is { Kind: CheckKind.Limits, MaxCostMicros: 30_000, MaxLatencyMs: 30_000 }, "cost and time");
            Check(PinService.Limits("500ms") is { MaxCostMicros: null, MaxLatencyMs: 500 }, "milliseconds");
            Check(PinService.Limits("2 min") is { MaxLatencyMs: 120_000 }, "minutes");
            Check(PinService.Limits("$1") is { MaxCostMicros: 1_000_000, MaxLatencyMs: null }, "a dollar");
            Throws<ArgumentException>(() => PinService.Limits("soon"), "neither a cost nor a time");

            Equal("none yet", PinText.ChecksLine(0, 0, true), "no checks");
            Equal("it passes on the reference output", PinText.ChecksLine(1, 1, true), "one passing");
            Equal("all 3 pass on the reference output", PinText.ChecksLine(3, 3, true), "all passing");
            Equal("1 of 3 pass on the reference output", PinText.ChecksLine(3, 1, true), "some failing");
            Equal("2 checks · no reference output to try them on", PinText.ChecksLine(2, 0, false), "no reference");

            var daily = new SuiteSchedule { Repeat = ScheduleRepeat.Daily, At = "07:00", TimeZone = "UTC", Enabled = true };
            var suite = new Suite { Id = EvalIds.New(EvalIds.Suite), Name = "Regex", Targets = [new SuiteTarget("faux", "echo")], K = 1, CapMicros = 100, Schedule = daily };
            static SuiteEstimate Worst(long micros) => new(micros, null, 0, []);
            var (over, isOver, pauses) = PinText.Pane(suite, Worst(50), Worst(150));
            Check(isOver && pauses && over.StartsWith("Runs on 1 target with k 1, daily at 07:00. Adds 1 answer per run", StringComparison.Ordinal)
                && over.Contains(", above the ", StringComparison.Ordinal) && over.EndsWith("The schedule pauses until you raise the cap.", StringComparison.Ordinal), "over the cap: " + over);
            var (within, isOverWithin, pausesWithin) = PinText.Pane(suite, Worst(50), Worst(90));
            Check(!isOverWithin && !pausesWithin && within.Contains(", within the ", StringComparison.Ordinal) && !within.Contains("pauses", StringComparison.Ordinal), "within: " + within);
            var (byHand, byHandOver, byHandPauses) = PinText.Pane(suite with { Schedule = null, K = 2 }, Worst(50), Worst(150));
            Check(byHandOver && !byHandPauses && byHand.StartsWith("Runs on 1 target with k 2, by hand only. Adds 2 answers per run", StringComparison.Ordinal)
                && byHand.EndsWith("A run of it is refused until you raise the cap.", StringComparison.Ordinal), "over, by hand: " + byHand);
            Check(PinText.Pane(suite with { CapMicros = null }, Worst(50), Worst(150)).Text.EndsWith("It has no suite-run cap.", StringComparison.Ordinal), "no cap");
            Check(PinText.Pane(suite with { Targets = [] }, Worst(0), Worst(0)).Text.StartsWith("Has no targets yet", StringComparison.Ordinal), "no targets");
            var paused = daily with { Paused = new SchedulePause("over-cap", "Its worst case is above its cap.", SuStart) };
            Check(PinText.Pane(suite with { Schedule = paused }, Worst(50), Worst(90)).Text.Contains("daily at 07:00 (paused).", StringComparison.Ordinal), "a paused schedule");

            Check(PinText.Saved(suite with { Name = "New" }, true, SuStart).StartsWith("Made the suite New, with this request's model as its only target.", StringComparison.Ordinal), "a new suite");
            Equal("Added to Regex. It runs with the suite tomorrow at 07:00.", PinText.Saved(suite, false, SuStart), "after 07:00, tomorrow");
            Equal("Added to Regex. It runs with the suite today at 12:00.", PinText.Saved(suite with { Schedule = daily with { At = "12:00" } }, false, SuStart), "before 12:00, today");
            Equal("Added to Regex. Its schedule is paused: Its worst case is above its cap.", PinText.Saved(suite with { Schedule = paused }, false, SuStart), "paused");
            Equal("Added to Regex. It runs when you run the suite.", PinText.Saved(suite with { Schedule = null }, false, SuStart), "by hand");
        }),

        Case("pins.suggested-checks-come-from-the-reference", () =>
        {
            var spec = new RunSpec
            {
                Provider = "faux", Model = "echo", Messages = [new RunMessage { Role = "user", Text = "Weather?" }],
                ResponseFormat = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "w", ["schema"] = new JsonObject { ["type"] = "object" } } },
            };
            var reference = new TestCaseReference
            {
                Text = "",
                Output = JsonNode.Parse("""{"role":"assistant","content":[{"type":"toolCall","id":"c1","name":"get_weather","arguments":{"city":"Oslo"}}],"stopReason":"toolUse"}"""),
                CostMicros = 12_345,
                DurationMs = 18_200,
            };
            var checks = PinService.Suggested(spec, reference);
            Equal("tool,json-schema,limits", string.Join(",", checks.Select(check => EvalJson.Wire(check.Kind))), "a tool, a schema and a limits check");
            Check(checks[0] is { Tool: "get_weather", Calls: ToolCallCount.AtLeastOnce }, "the first tool the reference called");
            Equal("{\"type\":\"object\"}", checks[1].Schema?.ToJsonString(), "the request's schema");
            Check(checks[2] is { MaxCostMicros: 20_000, MaxLatencyMs: 30_000 }, $"1.5 times, up to a cent and 5 s: {checks[2].MaxCostMicros} {checks[2].MaxLatencyMs}");
            Check(PinService.Suggested(spec with { ResponseFormat = null }, new TestCaseReference { Text = "hi", CostMicros = 10, DurationMs = 100 }) is [{ Kind: CheckKind.Limits, MaxCostMicros: 10_000, MaxLatencyMs: 5_000 }],
                "a cheap, fast answer: the floors");
            Equal(0, PinService.Suggested(spec with { ResponseFormat = null }, null).Count, "no reference: nothing to suggest");
        }),

        Case("pins.case-spec.refuses-a-credential-at-any-depth", () =>
        {
            var basic = new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Role = "user", Text = "hello" }] };
            static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();
            var key = Planted("sk-proj-");
            var stored = Planted("plain-");

            // RunSpec (so a run too): a credential's name anywhere in the provider or Bedrock options, named by its path.
            foreach (var (options, path) in new[]
            {
                ("""{"extra_body":{"api_key":"x"}}""", "extra_body.api_key"),
                ("""{"metadata":{"auth":{"user":"me"}}}""", "metadata.auth"),
                ("""{"list":[{"ok":1},{"access_token":"x"}]}""", "list[1].access_token"),
            })
            {
                var error = Throws<ArgumentException>(() => (basic with { ProviderOptions = Json(options) }).Normalize(), path + " is refused");
                Check(error.Message.Contains("\"" + path + "\"", StringComparison.Ordinal) && error.Message.Contains("own credentials", StringComparison.Ordinal), "it names the path: " + error.Message);
            }
            Check((basic with { ProviderOptions = Json("""{"generationConfig":{"maxOutputTokens":64},"metadata":{"user":"me"}}""") }).Normalize().ProviderOptions is not null,
                "plain options pass, and an output option at depth is left to RunModelOptions' own refusal");
            Check(Throws<ArgumentException>(() => (basic with { Bedrock = Json("""{"profile":{"secretAccessKey":"x"}}""") }).Normalize(), "a credential inside a Bedrock option")
                .Message.Contains("\"profile.secretAccessKey\"", StringComparison.Ordinal), "named by its path");

            // TestCase.CaseSpec: a value that looks like a key or token, at any depth, named by where it is and never repeated.
            foreach (var (spec, where) in new (RunSpec, string)[]
            {
                (basic with { Headers = new Dictionary<string, string> { ["X-Trace"] = key } }, "The header X-Trace"),
                (basic with { Headers = new Dictionary<string, string> { ["X-Note"] = "Bearer abc.def.ghi" } }, "The header X-Note"),
                (basic with { SessionId = key }, "The session id"),
                (basic with { ProviderOptions = Json("{\"metadata\":{\"note\":\"" + key + "\"}}") }, "providerOptions.metadata.note"),
                (basic with { ProviderOptions = Json("{\"list\":[\"ok\",\"" + key + "\"]}") }, "providerOptions.list[1]"),
                (basic with { ProviderOptions = Json("{\"metadata\":{\"" + key + "\":1}}") }, "A name in providerOptions.metadata"),
            })
            {
                Check(spec.Normalize() is not null, where + ": a run may still send it");
                var error = Throws<ArgumentException>(() => TestCase.CaseSpec(spec), where + " is refused in a case");
                Check(error.Message.StartsWith(where, StringComparison.Ordinal) && error.Message.Contains("never stores a credential", StringComparison.Ordinal), "it says where: " + error.Message);
                NoSecret(error.Message, "the refusal", key, "abc.def");
            }

            // A stored credential with no key's shape: only the vault's matcher knows it, wherever it is.
            bool Stored(string? text) => text?.Contains(stored, StringComparison.Ordinal) == true;
            var plain = basic with { Headers = new Dictionary<string, string> { ["X-Note"] = "id " + stored } };
            Check(TestCase.CaseSpec(plain).Headers is { Count: 1 }, "without a vault, a value with no key's shape passes");
            NoSecret(Throws<ArgumentException>(() => TestCase.CaseSpec(plain, Stored), "a stored credential in a header").Message, "the refusal", stored);
            Throws<ArgumentException>(() => TestCase.CaseSpec(basic with { ProviderOptions = Json("{\"a\":{\"b\":[\"" + stored + "\"]}}") }, Stored), "a stored credential deep in the options");
            Throws<ArgumentException>(() => TestCase.CaseSpec(basic with { SessionId = stored }, Stored), "a stored credential as the session id");
            var clean = TestCase.CaseSpec(basic with
            {
                Headers = new Dictionary<string, string> { ["X-Trace"] = "on" }, SessionId = "run-1", ProviderOptions = Json("""{"metadata":{"user":"me","tags":["a","b"]}}"""),
            }, Stored);
            Check(clean is { Headers.Count: 1, SessionId: "run-1", ProviderOptions: not null }, "plain values pass");
            var testCase = new TestCase { Id = EvalIds.New(EvalIds.Case), SuiteId = EvalIds.New(EvalIds.Suite), Name = "Leaky", Spec = plain, Origin = new TestCaseOrigin(PinSource.Suite) };
            Check(testCase.Normalize().Spec.Headers is { Count: 1 }, "a case normalized without a vault");
            Throws<ArgumentException>(() => testCase.Normalize(Stored), "a case normalized with the vault's matcher is refused");
        }),

        Case("pins.in-process.a-case-holds-no-account-and-a-credential-run-is-refused", async () =>
        {
            await using var host = SuHost.Create("pin-inproc");
            var pins = host.Services.GetRequiredService<PinService>();
            var key = Planted("sk-proj-");
            host.Runs.Insert(PinRun("r-acct") with { Account = "acct_work" });
            var headers = PinRun("r-header");
            headers.Options!["headers"] = new JsonObject { ["Authorization"] = "Bearer " + key };
            host.Runs.Insert(headers);
            var options = PinRun("r-option");
            options.Options!["providerOptions"] = new JsonObject { ["apiKey"] = key };
            host.Runs.Insert(options);
            var nested = PinRun("r-nested");
            nested.Options!["providerOptions"] = new JsonObject { ["extra_body"] = new JsonObject { ["api_key"] = key } };
            host.Runs.Insert(nested);
            var shaped = PinRun("r-shaped");
            shaped.Options!["headers"] = new JsonObject { ["X-Trace"] = key };
            host.Runs.Insert(shaped);
            host.Runs.Insert(PinRun("r-live", status: RunStatus.Running));
            host.Runs.Insert(PinRun("r-turn", source: "agent-compare"));

            var draft = await pins.DraftAsync(new PinTarget(PinSource.History, "r-acct"));
            Check(draft.Spec is { Account: null, Source: null, SystemPrompt: "Be brief.", MaxTokens: 64 } && draft.Account == "acct_work", "the request without its account; the account kept apart");
            Check(draft.Reference is { Text: "Echo: hello pin", RunId: "r-acct" } && draft.Reference.Output?["pisharp"] is null, "the reference without the Spend Guard's field");
            var view = pins.Modal(draft);
            Check(view is { Mode: PinMode.Pin, NewSuiteSelected: true, HasReference: true } && view.AccountLine!.StartsWith("The account is not part of the case", StringComparison.Ordinal), "the modal: " + view.AccountLine);
            var saved = await pins.SaveAsync(new PinSaveRequest(draft.Target, view.CaseId!, NewSuiteId: view.NewSuiteId, NewSuiteName: "In process"));
            Check(saved is { Created: true, SuiteCreated: true } && saved.Case.Spec.Account is null && saved.Suite.Targets is [{ Provider: "faux", Model: "echo", Account: "acct_work" }],
                "the case has no account; the new suite's target runs on the pinned run's account");
            var again = await pins.SaveAsync(new PinSaveRequest(draft.Target, view.CaseId!, NewSuiteId: view.NewSuiteId, NewSuiteName: "In process"));
            Check(again is { Created: false, SuiteCreated: false } && host.Store.Suites().Count == 1, "a retry saves once");

            foreach (var id in new[] { "r-header", "r-option", "r-nested", "r-shaped" })
            {
                var refused = await pins.ModalAsync(new PinTarget(PinSource.Playground, id));
                Check(refused.Refusal is { Length: > 0 } && !refused.Refusal.Contains(key, StringComparison.Ordinal), $"{id} is refused without its secret: {refused.Refusal}");
                var error = await PinThrowsAsync<PinRefusedException>(() => pins.SaveAsync(new PinSaveRequest(new PinTarget(PinSource.Playground, id), EvalIds.New(EvalIds.Case),
                    NewSuiteId: EvalIds.New(EvalIds.Suite), NewSuiteName: "Never")), id + " can't be saved");
                NoSecret(error.Message, "the refusal", key);
            }
            Check((await pins.ModalAsync(new PinTarget(PinSource.Playground, "r-live"))).Refusal == "This run is still going. Pin it once it finishes.", "a run still going");
            Equal(PinRules.AgentLater, (await pins.ModalAsync(new PinTarget(PinSource.History, "r-turn"))).Refusal, "an agent run");
            await PinThrowsAsync<KeyNotFoundException>(() => pins.ModalAsync(new PinTarget(PinSource.History, "r-none")), "an unknown run");
            Check((await pins.ModalAsync(new PinTarget(PinSource.Chat, ChatId: "c", EntryId: 1))).Refusal!.Contains("Chats aren't available", StringComparison.Ordinal), "no chats in this host");
            Equal(1, host.Store.Suites().Count, "nothing else was made");
            var stored = EvalJson.Serialize(host.Store.Cases(saved.Suite.Id)) + EvalJson.Serialize(host.Store.Suites());
            NoSecret(stored, "the stored cases and suites", key);
        }),

        Case("pins.playground-history-inspector-and-suite-page-pins-run-in-their-suite", async () =>
        {
            var root = Temp("pin-runs");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                const string question = "Is 1.0.0-rc.1 valid SemVer?";
                var first = await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["source"] = "playground", ["systemPrompt"] = "Be brief.", ["maxTokens"] = 64,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = question }),
                });
                var output = await app.Get($"/playground/runs/{first}/output");
                Check(output.Text.Contains($"data-pin-run=\"{first}\" data-pin-source=\"playground\"", StringComparison.Ordinal), "the Playground's output offers Pin as test: " + Cut(output.Text));
                var playground = (await app.Get("/playground?run=" + first)).Text;
                Check(playground.Contains("id=\"modal-test-case\"", StringComparison.Ordinal) && playground.Contains("/js/pin.js", StringComparison.Ordinal), "the Playground hosts the modal and pin.js");
                Equal(200, (await app.Get("/js/pin.js")).Status, "pin.js is served");

                var modal = await PinModalAsync(app, $"source=playground&run={first}");
                Equal("ready", modal.State, "ready: " + Cut(modal.Html));
                var text = modal.Text;
                Check(modal.Html.Contains("data-pin-mode=\"pin\"", StringComparison.Ordinal) && modal.Html.Contains("<option value=\"new\" selected=\"selected\">", StringComparison.Ordinal),
                    "pin mode, New suite… chosen while there is no suite");
                Check(text.Contains($"value=\"{question}\"", StringComparison.Ordinal), "named after the prompt");
                Check(text.Contains("Echo: " + question, StringComparison.Ordinal), "the reference output");
                Check(text.Contains("maxTokens 64", StringComparison.Ordinal) && text.Contains("System prompt and 1 user message", StringComparison.Ordinal) && text.Contains(first, StringComparison.Ordinal),
                    "the options, the messages and the run: " + Cut(PinBody(text)));
                Check(text.Contains("The account is not part of the case", StringComparison.Ordinal), "the account line");
                Check(modal.Checks is [{ } limits] && limits["kind"]?.GetValue<string>() == "limits" && text.Contains("it passes on the reference output", StringComparison.Ordinal),
                    "a cost and latency check to start with, passing: " + string.Join(" ", modal.Checks.Select(check => check.ToJsonString())));
                Equal(first, modal.Target?["run"]?.GetValue<string>(), "the target names the run");

                async Task<JsonNode> Row(string kind, string value, int status = 200)
                {
                    var answer = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins + "/checks",
                        new JsonObject { ["target"] = modal.Target!.DeepClone(), ["add"] = new JsonObject { ["kind"] = kind, ["value"] = value } }, origin: app.Origin);
                    Equal(status, answer.Status, kind + ": " + answer.Text);
                    Check(answer.Response.Headers.CacheControl?.NoStore == true, "a check preview is never cached");
                    return answer.Body!;
                }
                var contains = (await Row("contains", "SemVer"))["item"]!;
                Check(contains["verdict"]?.GetValue<string>() == "passed" && contains["label"]?.GetValue<string>() == "Contains" && contains["code"]?.GetValue<string>() == "SemVer",
                    "a contains check, passing on the reference: " + contains.ToJsonString());
                var regex = (await Row("regex", "^Nope"))["item"]!;
                Check(regex["verdict"]?.GetValue<string>() == "failed" && regex["limit"]?.GetValue<string>() == "100 ms limit", "a regex that fails on the reference: " + regex.ToJsonString());
                Check((await Row("limits", "$0.03 30 s"))["item"]?["verdict"]?.GetValue<string>() == "passed", "a limits check");
                Check((await Row("json-schema", ""))["item"]?["verdict"]?.GetValue<string>() == "failed", "a plain answer is not a JSON object");
                Check((await Row("judge", "", 400))["error"]?["message"]?.GetValue<string>().Contains("full check editor", StringComparison.Ordinal) == true, "a judge needs the suite page");
                await Row("contains", "", 400);

                var body = PinBodyOf(modal, newSuite: "Pins", name: "SemVer question", tags: "smoke, semver", checks: [.. modal.Checks, contains["check"]!.AsObject()]);
                Equal(403, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, body.DeepClone(), origin: "http://evil.example")).Status, "no cross-origin save");
                var unnamed = PinBodyOf(modal, newSuite: " ");
                Equal(400, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, unnamed, origin: app.Origin)).Status, "a new suite needs a name");
                Equal(0, (await app.Get(EvalRoutes.ApiSuites)).Body?["items"]?.AsArray().Count, "and no suite was made for it");
                var saved = await PinSaveAsync(app, (JsonObject)body.DeepClone());
                Check(saved["created"]?.GetValue<bool>() == true && saved["suiteCreated"]?.GetValue<bool>() == true && saved["suite"]?["paused"]?.GetValue<bool>() == false,
                    "saved in a new suite: " + saved.ToJsonString());
                var suiteId = saved["suite"]!["id"]!.GetValue<string>();
                Equal(EvalRoutes.Suite(suiteId), saved["url"]?.GetValue<string>(), "Open suite");
                Check(saved["toast"]?["title"]?.GetValue<string>() == "Test case saved" && saved["toast"]?["body"]?.GetValue<string>().StartsWith("Made the suite Pins,", StringComparison.Ordinal) == true,
                    "the toast: " + saved["toast"]?.ToJsonString());
                var again = await PinSaveAsync(app, (JsonObject)body.DeepClone());
                Check(again["created"]?.GetValue<bool>() == false && again["suiteCreated"]?.GetValue<bool>() == false && again["suite"]?["id"]?.GetValue<string>() == suiteId,
                    "a retry saves once: " + again.ToJsonString());

                var stored = await PinCaseAsync(app, modal.CaseId!);
                var spec = stored["spec"]!;
                Check(spec["provider"]?.GetValue<string>() == "faux" && spec["model"]?.GetValue<string>() == "echo" && spec["systemPrompt"]?.GetValue<string>() == "Be brief."
                    && spec["maxTokens"]?.GetValue<int>() == 64 && spec["messages"]?[0]?["text"]?.GetValue<string>() == question, "the full request: " + spec.ToJsonString());
                Check(spec["account"] is null && spec["source"] is null, "no account and no source in the request");
                Check(stored["origin"]?["source"]?.GetValue<string>() == "playground" && stored["origin"]?["runId"]?.GetValue<string>() == first, "where it came from: " + stored["origin"]?.ToJsonString());
                Check(stored["reference"]?["text"]?.GetValue<string>() == "Echo: " + question && stored["reference"]?["output"] is JsonObject output2 && output2["pisharp"] is null,
                    "the reference output, without the Spend Guard's field: " + stored["reference"]?.ToJsonString());
                Check(stored["name"]?.GetValue<string>() == "SemVer question" && stored["tags"]?.ToJsonString() == "[\"smoke\",\"semver\"]" && stored["checks"] is JsonArray { Count: 2 },
                    "its name, tags and checks: " + stored.ToJsonString());
                var export = await app.Get(EvalRoutes.ApiSuites + "/" + suiteId + "/export");
                Check(export.Body?["suite"]?["targets"]?[0]?["provider"]?.GetValue<string>() == "faux" && export.Body["suite"]?["targets"]?[0]?["model"]?.GetValue<string>() == "echo",
                    "the new suite targets the request's model: " + export.Text);

                // History: a call that asks for a tool; its row offers Pin as test, and the modal starts in the newest suite with a tool check.
                var second = await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["source"] = "playground",
                    ["tools"] = new JsonArray(new JsonObject { ["name"] = "get_weather", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } } } }),
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "tool: get_weather {\"city\":\"Oslo\"}" }),
                });
                var history = (await app.Get("/history")).Text;
                Check(history.Contains($"data-pin-run=\"{second}\" data-pin-source=\"history\"", StringComparison.Ordinal) && history.Contains("id=\"modal-test-case\"", StringComparison.Ordinal),
                    "History's row menu offers Pin as test");
                var fromHistory = await PinModalAsync(app, $"source=history&run={second}");
                Equal("ready", fromHistory.State, "ready: " + Cut(fromHistory.Html));
                Check(fromHistory.Html.Contains($"<option value=\"{suiteId}\" selected=\"selected\">", StringComparison.Ordinal) && !fromHistory.Html.Contains("<option value=\"new\" selected", StringComparison.Ordinal),
                    "the newest suite is chosen");
                Check(fromHistory.Text.Contains("It has no suite-run cap.", StringComparison.Ordinal) && fromHistory.Text.Contains("1 tool", StringComparison.Ordinal), "its pane and the tool");
                Check(fromHistory.Checks.Any(check => check["kind"]?.GetValue<string>() == "tool" && check["tool"]?.GetValue<string>() == "get_weather"), "a tool check for the tool the reference called");
                var fromHistorySaved = await PinSaveAsync(app, PinBodyOf(fromHistory, suiteId: suiteId));
                Check(fromHistorySaved["created"]?.GetValue<bool>() == true && fromHistorySaved["suiteCreated"]?.GetValue<bool>() == false
                    && fromHistorySaved["toast"]?["body"]?.GetValue<string>() == "Added to Pins. It runs when you run the suite.", "added: " + fromHistorySaved.ToJsonString());
                Check((await PinCaseAsync(app, fromHistory.CaseId!))["spec"]?["tools"]?[0]?["name"]?.GetValue<string>() == "get_weather", "the request keeps its tools");

                // The inspector's More menu.
                var inspector = (await app.Get("/runs/" + first)).Text;
                Check(inspector.Contains($"data-pin-run=\"{first}\" data-pin-source=\"inspector\"", StringComparison.Ordinal) && inspector.Contains("id=\"modal-test-case\"", StringComparison.Ordinal),
                    "the inspector offers Pin as test");
                var fromInspector = await PinModalAsync(app, $"source=inspector&run={first}");
                var inspectorSaved = await PinSaveAsync(app, PinBodyOf(fromInspector, suiteId: suiteId, name: "SemVer, from the inspector"));
                Check((await PinCaseAsync(app, fromInspector.CaseId!))["origin"]?["source"]?.GetValue<string>() == "inspector" && inspectorSaved["created"]?.GetValue<bool>() == true,
                    "pinned from the inspector");

                // The suite page's Add case and Edit case.
                var add = await PinModalAsync(app, $"suite={suiteId}");
                Check(add.State == "ready" && add.Html.Contains("data-pin-mode=\"add\"", StringComparison.Ordinal) && add.Html.Contains("data-pin-request=\"add\"", StringComparison.Ordinal)
                    && add.Html.Contains("data-pin-no-reference", StringComparison.Ordinal) && add.Text.Contains("faux/echo", StringComparison.Ordinal), "Add case: " + Cut(add.Html));
                var addBody = new JsonObject
                {
                    ["target"] = add.Target!.DeepClone(), ["caseId"] = add.CaseId, ["name"] = "Say hi", ["prompt"] = " ",
                    ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "Echo" }),
                };
                var noPrompt = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, addBody.DeepClone(), origin: app.Origin);
                Check(noPrompt.Status == 400 && noPrompt.Text.Contains("Write the user message the case asks.", StringComparison.Ordinal), "a case needs its prompt: " + noPrompt.Text);
                addBody["prompt"] = "Say hi";
                addBody["systemPrompt"] = "Be brief.";
                addBody["maxTokens"] = "32";
                Check((await PinSaveAsync(app, addBody))["created"]?.GetValue<bool>() == true, "added");
                var added = await PinCaseAsync(app, add.CaseId!);
                Check(added["spec"]?["maxTokens"]?.GetValue<int>() == 32 && added["spec"]?["messages"]?[0]?["text"]?.GetValue<string>() == "Say hi" && added["reference"] is null
                    && added["origin"]?["source"]?.GetValue<string>() == "suite", "a hand-written case: " + added.ToJsonString());

                var edit = await PinModalAsync(app, $"case={modal.CaseId}");
                Check(edit.State == "ready" && edit.Html.Contains("data-pin-mode=\"edit\"", StringComparison.Ordinal) && edit.Text.Contains("Edit test case", StringComparison.Ordinal)
                    && edit.CaseId == modal.CaseId && edit.Text.Contains("value=\"SemVer question\"", StringComparison.Ordinal), "Edit case: " + Cut(edit.Html));
                var editBody = new JsonObject
                {
                    ["target"] = edit.Target!.DeepClone(), ["caseId"] = edit.CaseId, ["name"] = "SemVer question, edited", ["tags"] = "semver",
                    ["checks"] = new JsonArray(contains["check"]!.DeepClone()),
                };
                var wrongCase = (JsonObject)editBody.DeepClone();
                wrongCase["caseId"] = EvalIds.New(EvalIds.Case);
                Equal(400, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, wrongCase, origin: app.Origin)).Status, "an edit names the case it edits");
                Check((await PinSaveAsync(app, editBody))["created"]?.GetValue<bool>() == false, "edited");
                var edited = await PinCaseAsync(app, modal.CaseId!);
                Check(edited["name"]?.GetValue<string>() == "SemVer question, edited" && edited["tags"]?.ToJsonString() == "[\"semver\"]" && edited["checks"] is JsonArray { Count: 1 }
                    && edited["spec"]?["systemPrompt"]?.GetValue<string>() == "Be brief." && edited["reference"]?["text"]?.GetValue<string>() == "Echo: " + question,
                    "its name, tags and checks changed; its request and reference did not: " + edited.ToJsonString());

                // Every case runs in the suite, and every check passes.
                PinAllPassed(await PinRunSuiteAsync(app, suiteId), 4, "the suite run");

                // What can't be pinned.
                var slow = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo",
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "slow: " + string.Join(" ", Enumerable.Range(1, 400)) }),
                }, origin: app.Origin);
                var slowId = slow.Body!["id"]!.GetValue<string>();
                await RunsSseAsync(app, $"/api/runs/{slowId}/events", take: 3);
                var running = await PinModalAsync(app, $"source=playground&run={slowId}");
                Check(running.State == "refused" && running.Text.Contains("still going", StringComparison.Ordinal), "a run still going: " + Cut(running.Html));
                Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/runs/{slowId}/cancel", new JsonObject(), origin: app.Origin)).Status, "Stop");
                await RunsSseAsync(app, $"/api/runs/{slowId}/events");
                var stopped = await PinModalAsync(app, $"source=playground&run={slowId}");
                Check(stopped.State == "refused" && stopped.Text.Contains("(it was stopped)", StringComparison.Ordinal), "a stopped run: " + Cut(stopped.Html));
                Check(!(await app.Get($"/playground/runs/{slowId}/output")).Text.Contains("data-pin-run=", StringComparison.Ordinal), "no Pin as test on a stopped run's output");
                var refusedSave = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, new JsonObject
                {
                    ["target"] = new JsonObject { ["source"] = "playground", ["run"] = slowId }, ["caseId"] = EvalIds.New(EvalIds.Case),
                    ["newSuite"] = new JsonObject { ["id"] = EvalIds.New(EvalIds.Suite), ["name"] = "Never" },
                }, origin: app.Origin);
                Check(refusedSave.Status == 409 && refusedSave.Body?["error"]?["message"]?.GetValue<string>().Contains("was stopped", StringComparison.Ordinal) == true, "a save is refused too: " + refusedSave.Text);
                Equal("refused", (await PinModalAsync(app, "source=history&run=r-none", 404)).State, "an unknown run");
                Equal("refused", (await PinModalAsync(app, $"run={first}&chat=c&entry=1", 400)).State, "two things at once");
                Equal(1, (await app.Get(EvalRoutes.ApiSuites)).Body?["items"]?.AsArray().Count, "one suite in all");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("pins.over-cap.the-pane-warns-and-saving-pauses-the-schedule", async () =>
        {
            var root = Temp("pin-cap");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var run = await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["source"] = "playground", ["maxTokens"] = 256,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Write a SemVer 2.0.0 regex." }),
                });
                async Task<string> MakeSuite(string name, long? cap)
                {
                    var id = EvalIds.New(EvalIds.Suite);
                    var body = new JsonObject { ["id"] = id, ["name"] = name, ["targets"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }) };
                    if (cap is not null) body["capMicros"] = cap;
                    Equal(200, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiSuites, body, origin: app.Origin)).Status, "suite " + name);
                    var caseBody = new JsonObject
                    {
                        ["id"] = EvalIds.New(EvalIds.Case), ["suiteId"] = id, ["name"] = "Existing case",
                        ["spec"] = new JsonObject
                        {
                            ["provider"] = "faux", ["model"] = "echo", ["maxTokens"] = 256,
                            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Write a SemVer 2.0.0 regex, variant 1." }),
                        },
                        ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "Echo" }),
                    };
                    Equal(200, (await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiCases, caseBody, origin: app.Origin)).Status, "a case of " + name);
                    return id;
                }
                var roomy = await MakeSuite("Roomy", 50_000_000);
                var capped = await MakeSuite("Capped", null);
                var at = DateTime.UtcNow.AddHours(12).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                async Task<JsonNode> Schedule(long cap)
                {
                    var answer = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiSuites + "/" + capped + "/schedule",
                        new JsonObject { ["enabled"] = true, ["repeat"] = "daily", ["at"] = at, ["timeZone"] = "UTC", ["capMicros"] = cap }, origin: app.Origin);
                    Equal(200, answer.Status, "schedule: " + answer.Text);
                    return answer.Body!;
                }
                var worst = (await Schedule(1))["estimate"]!["worstMicros"]!.GetValue<long>();
                Check((await Schedule(worst))["saved"]?.GetValue<bool>() == true, "a daily schedule whose cap is exactly its worst case");

                var modal = await PinModalAsync(app, $"source=playground&run={run}");
                var cappedPane = Regex.Match(modal.Html, $"<div data-pin-pane=\"{capped}\" data-pin-over-cap=\"(true|false)\" data-pin-pauses=\"(true|false)\"[^>]*>\\s*<div[^>]*>([^<]*)</div>");
                Check(cappedPane.Success && cappedPane.Groups[1].Value == "true" && cappedPane.Groups[2].Value == "true", "the capped suite's pane is over its cap and says it pauses: " + Cut(modal.Html));
                var cappedText = WebUtility.HtmlDecode(cappedPane.Groups[3].Value);
                Check(cappedText.StartsWith($"Runs on 1 target with k {Suite.DefaultK}, daily at {at}. Adds {Suite.DefaultK} answers per run", StringComparison.Ordinal) && cappedText.Contains(", above the ", StringComparison.Ordinal)
                    && cappedText.EndsWith("The schedule pauses until you raise the cap.", StringComparison.Ordinal), "the over-cap pane: " + cappedText);
                // Colour only for status: the pane's text stays plain form-text, and a warn chip says it is over its cap.
                const string overCapChip = "<span class=\"ps-chip mt-1\" data-tone=\"warn\" data-pin-over-cap-chip>over its cap</span>";
                Check(PinPane(modal.Html, capped).Contains(overCapChip, StringComparison.Ordinal), "the over-cap pane carries a warn chip: " + PinPane(modal.Html, capped));
                Check(Regex.IsMatch(modal.Html, $"<div data-pin-pane=\"{capped}\"[^>]*>\\s*<div class=\"form-text\">") && !modal.Html.Contains("text-warning", StringComparison.Ordinal),
                    "its text is plain grey form-text, never warning-coloured");
                var roomyPane = Regex.Match(modal.Html, $"<div data-pin-pane=\"{roomy}\" data-pin-over-cap=\"false\" data-pin-pauses=\"false\"[^>]*>\\s*<div[^>]*>([^<]*)</div>");
                Check(roomyPane.Success && WebUtility.HtmlDecode(roomyPane.Groups[1].Value).Contains(", within the $50", StringComparison.Ordinal), "a suite with room: " + roomyPane.Value);
                Check(!PinPane(modal.Html, roomy).Contains("data-pin-over-cap-chip", StringComparison.Ordinal), "and no chip");

                var saved = await PinSaveAsync(app, PinBodyOf(modal, suiteId: capped));
                Check(saved["created"]?.GetValue<bool>() == true && saved["suite"]?["paused"]?.GetValue<bool>() == true
                    && saved["toast"]?["body"]?.GetValue<string>().StartsWith("Added to Capped. Its schedule is paused: ", StringComparison.Ordinal) == true, "saved, and the schedule paused: " + saved.ToJsonString());
                var export = await app.Get(EvalRoutes.ApiSuites + "/" + capped + "/export");
                Check(export.Body?["suite"]?["schedule"]?["enabled"]?.GetValue<bool>() == true && export.Body["suite"]?["schedule"]?["paused"] is JsonObject && export.Body["cases"] is JsonArray { Count: 2 },
                    "the suite keeps the case, its schedule paused: " + export.Text);
                var after = await PinModalAsync(app, $"source=playground&run={run}");
                Check(after.Html.Contains($"data-pin-pane=\"{capped}\" data-pin-over-cap=\"true\" data-pin-pauses=\"false\"", StringComparison.Ordinal)
                    && WebUtility.HtmlDecode(after.Html).Contains($"daily at {at} (paused).", StringComparison.Ordinal), "once paused, a further case is refused a run, not paused again");
                Check(PinPane(after.Html, capped).Contains(overCapChip, StringComparison.Ordinal), "still over its cap");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("pins.compare-column-and-arena-reveal-after-the-vote", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("pin-compare");
            var posted = await app.SendAsync(HttpMethod.Post, "/api/compare/run", new JsonObject
            {
                ["systemPrompt"] = "Be brief.", ["prompt"] = "hello compare",
                ["columns"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" }),
            }, origin: app.Origin);
            Check(posted.Status == 200 && posted.Body?["posted"]?.GetValue<bool>() == true, "Run all: " + posted.Text);
            var group = posted.Body!["groupId"]!.GetValue<string>();
            var ids = posted.Body["ids"]!.AsArray().Select(id => id!.GetValue<string>()).ToList();
            foreach (var id in ids) await RunsSseAsync(app, $"/api/runs/{id}/events");
            var column = await app.Get($"/compare/groups/{group}/runs/{ids[1]}?key=c2");
            Check(column.Text.Contains($"data-pin-run=\"{ids[1]}\" data-pin-source=\"compare\"", StringComparison.Ordinal), "a finished column offers Pin as test: " + Cut(column.Text));
            Check((await app.Get("/compare?group=" + group)).Text.Contains("id=\"modal-test-case\"", StringComparison.Ordinal), "Compare hosts the modal");
            var modal = await PinModalAsync(app, $"source=compare&run={ids[1]}");
            Equal("ready", modal.State, "ready: " + Cut(modal.Html));
            var compareSaved = await PinSaveAsync(app, PinBodyOf(modal, newSuite: "Compare picks"));
            var stored = await PinCaseAsync(app, modal.CaseId!);
            Check(stored["spec"]?["model"]?.GetValue<string>() == "echo-small" && stored["spec"]?["systemPrompt"]?.GetValue<string>() == "Be brief."
                && stored["spec"]?["messages"]?[0]?["text"]?.GetValue<string>() == "hello compare" && stored["spec"]?["groupId"] is null, "the column's request, without its group: " + stored["spec"]?.ToJsonString());
            Check(stored["origin"]?["source"]?.GetValue<string>() == "compare" && stored["origin"]?["runId"]?.GetValue<string>() == ids[1] && stored["reference"]?["text"]?.GetValue<string>() == "Echo: hello compare",
                "from the column, with its answer: " + stored.ToJsonString());
            // The one source whose request comes from a group run with shared options: its case runs in its suite too, every check passing.
            Check(compareSaved["created"]?.GetValue<bool>() == true && compareSaved["suiteCreated"]?.GetValue<bool>() == true, "saved in a new suite: " + compareSaved.ToJsonString());
            PinAllPassed(await PinRunSuiteAsync(app, compareSaved["suite"]!["id"]!.GetValue<string>()), 1, "the Compare case's suite run");

            // The Arena: refused while blind, pinned after the vote with the battle and its tag.
            var created = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiArena, ArenaDraftJson("What is two plus two?", ArenaThree), origin: app.Origin);
            Equal(200, created.Status, "a battle: " + created.Text);
            var battleId = created.Body!["battleId"]!.GetValue<string>();
            var row = ReadDb(Path.Join(app.Root, "data", "webagent.db"), $"SELECT group_id, data FROM eval_battles WHERE id = '{battleId}'", reader => (reader.GetString(0), reader.GetString(1))).Single();
            var battle = EvalJson.Deserialize<ArenaBattle>(row.Item2);
            var secrets = ArenaFauxNames.Concat([row.Item1]).Concat(battle.Contestants.Select(item => item.RunId!)).ToList();
            await ArenaSseAsync(app, $"{EvalRoutes.ApiArena}/{battleId}/events");
            var picked = battle.At("B")!;
            var blind = await PinModalAsync(app, $"source=arena&run={picked.RunId}");
            Check(blind.State == "refused" && blind.Text.Contains("Vote first, then pin it.", StringComparison.Ordinal), "refused before the vote: " + Cut(blind.Html));
            ArenaBlind(blind.Html, secrets, "the refused modal");
            var early = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, new JsonObject
            {
                ["target"] = new JsonObject { ["source"] = "arena", ["run"] = picked.RunId }, ["caseId"] = EvalIds.New(EvalIds.Case),
                ["newSuite"] = new JsonObject { ["id"] = EvalIds.New(EvalIds.Suite), ["name"] = "Too early" },
            }, origin: app.Origin);
            Equal(409, early.Status, "a save before the vote: " + early.Text);
            ArenaBlind(early.Text, [.. secrets.Where(secret => secret != picked.RunId)], "the refused save");
            var page = await app.Get("/compare/arena/" + battleId);
            Check(page.Text.Contains("id=\"modal-test-case\"", StringComparison.Ordinal), "the battle page hosts the modal");
            ArenaBlind(page.Text, secrets, "the battle page with the modal's shell");

            var vote = await app.SendAsync(HttpMethod.Post, $"{EvalRoutes.ApiArena}/{battleId}/vote", new JsonObject { ["kind"] = "pick", ["pick"] = "B", ["tag"] = "coding" }, origin: app.Origin);
            Equal(200, vote.Status, "voted: " + vote.Text);
            var revealed = (await app.Get("/compare/arena/" + battleId)).Text;
            Check(revealed.Contains($"data-arena-pin data-pin-run=\"{picked.RunId}\" data-pin-source=\"arena\"", StringComparison.Ordinal), "the reveal offers the picked answer");
            var arena = await PinModalAsync(app, $"source=arena&run={picked.RunId}");
            Check(arena.State == "ready" && arena.Text.Contains("value=\"coding\"", StringComparison.Ordinal), "ready after the vote, tagged with the battle's tag: " + Cut(arena.Html));
            var arenaSaved = await PinSaveAsync(app, PinBodyOf(arena, newSuite: "Arena picks"));
            var arenaCase = await PinCaseAsync(app, arena.CaseId!);
            Check(arenaSaved["created"]?.GetValue<bool>() == true && arenaCase["origin"]?["source"]?.GetValue<string>() == "arena" && arenaCase["origin"]?["battleId"]?.GetValue<string>() == battleId
                && arenaCase["origin"]?["runId"]?.GetValue<string>() == picked.RunId && arenaCase["tags"]?.ToJsonString() == "[\"coding\"]"
                && arenaCase["spec"]?["model"]?.GetValue<string>() == picked.Identity.Model, "the picked answer, with its battle: " + arenaCase.ToJsonString());
            PinAllPassed(await PinRunSuiteAsync(app, arenaSaved["suite"]!["id"]!.GetValue<string>()), 1, "the Arena case's suite run");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("pins.chat-message-and-its-history-row", async () =>
        {
            var root = Temp("pin-chat");
            Directory.CreateDirectory(Path.Join(root, "data"));
            Directory.CreateDirectory(Path.Join(root, "work"));
            try
            {
                var db = new WebAgentDb(Path.Join(root, "data", "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
                db.Migrate();
                await new SettingsStore(db).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                SqliteClearPools();
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Arguments = ["--faux-metered"] });
                var chat = new ChatApp { Root = root, App = app };
                var chatId = await chat.CreateAsync("hello pin", "pin-1");
                await chat.WaitForAsync(chatId, "Echo: hello pin");
                Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages", new JsonObject { ["requestId"] = "pin-2", ["text"] = "and again" }, origin: app.Origin)).Status, "sent");
                await chat.WaitForAsync(chatId, "Echo: and again");

                var page = (await app.Get("/chat/" + chatId)).Text;
                Check(page.Contains("id=\"modal-test-case\"", StringComparison.Ordinal) && page.Contains("/js/pin.js", StringComparison.Ordinal), "the chat hosts the modal");
                var buttons = Regex.Matches(page, $"data-pin-source=\"chat\" data-pin-chat=\"{chatId}\" data-pin-agent=\"(\\d+)\" data-pin-entry=\"(\\d+)\"");
                Check(buttons.Count >= 1, "an answer's footer offers Pin as test: " + Cut(page));
                var agent = buttons[^1].Groups[1].Value;
                var entry = buttons[^1].Groups[2].Value;
                var modal = await PinModalAsync(app, $"source=chat&chat={chatId}&agent={agent}&entry={entry}");
                Equal("ready", modal.State, "ready: " + Cut(modal.Html));
                Check(modal.Text.Contains("2 user messages and 1 answer", StringComparison.Ordinal) && modal.Text.Contains("Echo: and again", StringComparison.Ordinal)
                    && modal.Text.Contains("value=\"and again\"", StringComparison.Ordinal), "the chat up to the answer, and the answer: " + Cut(PinBody(modal.Text)));
                var saved = await PinSaveAsync(app, PinBodyOf(modal, newSuite: "Chat pins"));
                var stored = await PinCaseAsync(app, modal.CaseId!);
                var spec = stored["spec"]!;
                Check(spec["messages"] is JsonArray { Count: 3 } messages && string.Join(",", messages.Select(message => message?["role"]?.GetValue<string>() ?? "user")) == "user,assistant,user"
                    && messages[0]?["text"]?.GetValue<string>() == "hello pin" && messages[1]?["text"]?.GetValue<string>() == "Echo: hello pin" && messages[2]?["text"]?.GetValue<string>() == "and again",
                    "the transcript before the answer: " + spec.ToJsonString());
                // A new chat has no instructions, so its system messages carry no sections (the provider got no system prompt), only its tools.
                Check(spec["systemPrompt"] is null && spec["tools"] is JsonArray chatTools && chatTools.Any(tool => tool?["name"]?.GetValue<string>() == "read")
                    && spec["provider"]?.GetValue<string>() == "faux" && spec["source"] is null && spec["account"] is null,
                    "the chat's tools, on the chat's model, without its source or account: " + Cut(spec.ToJsonString()));
                Check(stored["origin"]?["source"]?.GetValue<string>() == "chat" && stored["origin"]?["chatId"]?.GetValue<string>() == chatId && stored["origin"]?["entryId"]?.GetValue<string>() == entry,
                    "from the chat message: " + stored["origin"]?.ToJsonString());
                Check(stored["reference"]?["text"]?.GetValue<string>() == "Echo: and again", "its answer is the reference");
                var notAnswer = await PinModalAsync(app, $"source=chat&chat={chatId}&agent={agent}&entry={long.Parse(entry, System.Globalization.CultureInfo.InvariantCulture) - 1}");
                Check(notAnswer.State == "refused" && notAnswer.Text.Contains("Only an answer", StringComparison.Ordinal), "only an answer can be pinned: " + Cut(notAnswer.Html));
                await PinModalAsync(app, "source=chat&chat=no-such-chat&entry=1", 404);

                PinAllPassed(await PinRunSuiteAsync(app, saved["suite"]!["id"]!.GetValue<string>()), 1, "the chat case's suite run");

                // The chat's calls are History rows; pinning one pins the same message.
                var calls = ReadDb(Path.Join(root, "data", RunsDb.FileName), $"SELECT id FROM runs WHERE source = 'chat' AND session = '{chatId}' AND status = 'completed' ORDER BY rowid",
                    reader => reader.GetString(0));
                Check(calls.Count == 2, "the chat's two calls are in History: " + string.Join(",", calls));
                var history = (await app.Get("/history")).Text;
                Check(history.Contains($"data-pin-run=\"{calls[^1]}\" data-pin-source=\"history\"", StringComparison.Ordinal), "their rows offer Pin as test");
                var fromHistory = await PinModalAsync(app, $"source=history&run={calls[^1]}");
                Equal("ready", fromHistory.State, "ready: " + Cut(fromHistory.Html));
                await PinSaveAsync(app, PinBodyOf(fromHistory, newSuite: "Chat rows"));
                var row = await PinCaseAsync(app, fromHistory.CaseId!);
                Check(row["spec"]?["messages"]?.ToJsonString() == spec["messages"]?.ToJsonString() && row["spec"]?["systemPrompt"]?.GetValue<string>() == spec["systemPrompt"]?.GetValue<string>(),
                    "the same request as the message's");
                Check(row["origin"]?["source"]?.GetValue<string>() == "history" && row["origin"]?["runId"]?.GetValue<string>() == calls[^1] && row["origin"]?["chatId"]?.GetValue<string>() == chatId
                    && row["origin"]?["entryId"]?.GetValue<string>() == entry && row["reference"]?["text"]?.GetValue<string>() == "Echo: and again", "the row's run, chat and message: " + row["origin"]?.ToJsonString());
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("pins.no-secret-in-the-modal-the-save-the-case-the-export-or-the-db", async () =>
        {
            // An environment key and a stored key with no well-known prefix (only the vault's match finds them), a stored key with one, a
            // key-shaped value that is stored nowhere (only its prefix gives it away), and a key under a credential's name inside an option.
            var envKey = Planted("env-");
            var storedKey = Planted("sk-ant-api03-");
            var plainKey = Planted("plain-");
            var shapedKey = Planted("sk-or-v1-");
            var nestedKey = Planted("nested-");
            string[] planted = [envKey, storedKey, plainKey, shapedKey, nestedKey];
            var root = Temp("pin-secret");
            try
            {
                // History rows whose recorded options carry one of them (written before the app starts, as an earlier build could have).
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                var seeds = new (string Id, string Option, JsonObject Value)[]
                {
                    ("r-nested", "providerOptions", new JsonObject { ["extra_body"] = new JsonObject { ["api_key"] = nestedKey } }),
                    ("r-shaped", "headers", new JsonObject { ["X-Trace"] = shapedKey }),
                    ("r-stored", "headers", new JsonObject { ["X-Note"] = "id " + plainKey }),
                    ("r-stored-option", "providerOptions", new JsonObject { ["metadata"] = new JsonObject { ["notes"] = new JsonArray("ok", plainKey) } }),
                    ("r-env", "headers", new JsonObject { ["X-Env"] = envKey }),
                };
                var seeded = new RunStore(new RunsDb(Path.Join(data, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                foreach (var (id, option, value) in seeds)
                {
                    var row = PinRun(id) with { Created = now, Started = now, Finished = now, Updated = now };
                    row.Options![option] = value;
                    seeded.Insert(row);
                }
                SqliteClearPools();

                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = data,
                    Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey },
                });
                Equal(200, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts",
                    new JsonObject { ["provider"] = "anthropic", ["apiKey"] = storedKey, ["label"] = "Work key" }, origin: app.Origin)).Status, "a stored key");
                Equal(200, (await app.SendAsync(HttpMethod.Post, "/api/credentials/accounts",
                    new JsonObject { ["provider"] = "google", ["apiKey"] = plainKey, ["label"] = "Plain key" }, origin: app.Origin)).Status, "a stored key with no known prefix");
                var run = await HistoryRunAsync(app, new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["source"] = "playground", ["systemPrompt"] = "Be brief.",
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "hello secret" }),
                });
                var seen = new StringBuilder();
                var modal = await PinModalAsync(app, $"source=playground&run={run}");
                seen.AppendLine(modal.Html);
                var saved = await PinSaveAsync(app, PinBodyOf(modal, newSuite: "Secrets"));
                seen.AppendLine(saved.ToJsonString());
                var suiteId = saved["suite"]!["id"]!.GetValue<string>();
                var stored = await PinCaseAsync(app, modal.CaseId!);
                seen.AppendLine(stored.ToJsonString());
                Check(stored["spec"]?["account"] is null, "the case names no account");

                // Each seeded row: the modal says why it can't be a case, and a save is refused, neither naming the value.
                foreach (var (id, _, _) in seeds)
                {
                    var refused = await PinModalAsync(app, $"source=history&run={id}");
                    Check(refused.State == "refused" && refused.Text.Contains("This request can't be a test case: ", StringComparison.Ordinal), $"{id}: the modal refuses it: " + Cut(refused.Html));
                    seen.AppendLine(refused.Html);
                    var save = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, new JsonObject
                    {
                        ["target"] = new JsonObject { ["source"] = "history", ["run"] = id }, ["caseId"] = EvalIds.New(EvalIds.Case),
                        ["newSuite"] = new JsonObject { ["id"] = EvalIds.New(EvalIds.Suite), ["name"] = "Never" },
                    }, origin: app.Origin);
                    Check(save.Status == 409 && save.Body?["error"]?["message"]?.GetValue<string>().StartsWith("This request can't be a test case: ", StringComparison.Ordinal) == true,
                        $"{id}: the save is refused: " + save.Text);
                    seen.AppendLine(save.Text);
                }
                // The cases API (the suite page's own saves) refuses the same requests.
                foreach (var (what, extra) in new (string, JsonObject)[]
                {
                    ("a key under a credential's name inside an option", new JsonObject { ["providerOptions"] = new JsonObject { ["extra_body"] = new JsonObject { ["api_key"] = nestedKey } } }),
                    ("a key-shaped value under a neutral header name", new JsonObject { ["headers"] = new JsonObject { ["X-Trace"] = shapedKey } }),
                    ("a stored key in a header", new JsonObject { ["headers"] = new JsonObject { ["X-Note"] = plainKey } }),
                    ("a stored key deep in the options", new JsonObject { ["providerOptions"] = new JsonObject { ["metadata"] = new JsonObject { ["notes"] = new JsonArray(plainKey) } } }),
                    ("the environment key as the session id", new JsonObject { ["sessionId"] = envKey }),
                })
                {
                    extra["provider"] = "faux";
                    extra["model"] = "echo";
                    extra["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "hello secret" });
                    var answer = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiCases, new JsonObject { ["suiteId"] = suiteId, ["name"] = "Leaky", ["spec"] = extra }, origin: app.Origin);
                    Check(answer.Status == 400, what + " is refused by the cases API: " + answer.Text);
                    seen.AppendLine(answer.Text);
                }

                var export = await app.Get(EvalRoutes.ApiSuites + "/" + suiteId + "/export");
                Check(export.Body?["cases"] is JsonArray { Count: 1 }, "the export holds the one clean case: " + Cut(export.Text));
                seen.AppendLine(export.Text);
                seen.AppendLine((await app.Get(EvalRoutes.ApiSuites + "/" + suiteId)).Text);
                Equal(1, (await app.Get(EvalRoutes.ApiSuites)).Body?["items"]?.AsArray().Count, "no suite was made for a refused pin");
                var checks = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins + "/checks",
                    new JsonObject { ["target"] = modal.Target!.DeepClone(), ["add"] = new JsonObject { ["kind"] = "contains", ["value"] = "hello" } }, origin: app.Origin);
                seen.AppendLine(checks.Text);
                var database = Path.Join(app.Root, "data", "webagent.db");
                Equal(1L, ReadDb(database, "SELECT COUNT(*) FROM eval_cases", reader => reader.GetInt64(0)).Single(), "eval_cases holds the one clean case");
                foreach (var table in new[] { "eval_cases", "eval_suites" })
                    foreach (var row in ReadDb(database, $"SELECT data FROM {table}", reader => reader.GetString(0))) seen.AppendLine(row);
                Check(seen.ToString().Contains("hello secret", StringComparison.Ordinal), "what was read holds the pinned request");
                NoSecret(seen.ToString(), "what Pin as test case shows and stores", [.. planted, "Work key", "Plain key"]);
                NoSecret(app.Log, "the log", planted);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];

    // ----- helpers -----

    /// <summary>What GET /api/evals/pins/modal answered: the fragment, its state and what pin.js reads from it.</summary>
    private sealed record PinModalAnswer(string Html, string State, string? CaseId, string? NewSuiteId, JsonObject? Target, IReadOnlyList<JsonObject> Checks)
    {
        /// <summary>The fragment as the browser reads it (character references decoded).</summary>
        public string Text => WebUtility.HtmlDecode(Html);
    }

    /// <summary>The modal of <paramref name="query"/>, checked: the status, never cached, one root for pin.js, no inline script.</summary>
    private static async Task<PinModalAnswer> PinModalAsync(WebAgentApp app, string query, int status = 200)
    {
        var answer = await app.Get(EvalRoutes.ApiPins + "/modal?" + query);
        Equal(status, answer.Status, $"the modal of {query}: {Cut(answer.Text)}");
        Check(answer.Response.Headers.CacheControl?.NoStore == true, "the modal is never cached: " + query);
        var html = answer.Text;
        Check(html.TrimStart().StartsWith("<div class=\"modal-content\" data-pin-content", StringComparison.Ordinal), "one root for pin.js to swap in: " + Cut(html));
        HistoryNoInline("the modal of " + query, html);
        string? Attribute(string name) => Regex.Match(html, " " + name + "=\"([^\"]*)\"") is { Success: true } match ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        var target = Attribute("data-pin-target") is { } json ? JsonNode.Parse(json) as JsonObject : null;
        var checks = Regex.Matches(html, " data-pin-check=\"([^\"]*)\"").Select(match => JsonNode.Parse(WebUtility.HtmlDecode(match.Groups[1].Value))!.AsObject()).ToList();
        return new PinModalAnswer(html, Attribute("data-pin-state") ?? "", Attribute("data-pin-case"), Attribute("data-pin-new-suite"), target, checks);
    }

    /// <summary>A save as pin.js posts it from <paramref name="modal"/>: to <paramref name="suiteId"/> or a new suite named
    /// <paramref name="newSuite"/>, with the modal's checks unless <paramref name="checks"/> are given.</summary>
    private static JsonObject PinBodyOf(PinModalAnswer modal, string? suiteId = null, string? newSuite = null, string? name = null, string? tags = null,
        IReadOnlyList<JsonObject>? checks = null)
    {
        var body = new JsonObject
        {
            ["target"] = modal.Target!.DeepClone(),
            ["caseId"] = modal.CaseId,
            ["checks"] = new JsonArray([.. (checks ?? modal.Checks).Select(check => (JsonNode)check.DeepClone())]),
        };
        if (name is not null) body["name"] = name;
        if (tags is not null) body["tags"] = tags;
        if (suiteId is not null) body["suiteId"] = suiteId;
        if (newSuite is not null) body["newSuite"] = new JsonObject { ["id"] = modal.NewSuiteId, ["name"] = newSuite };
        return body;
    }

    /// <summary>The markup of the suite pane <paramref name="id"/> in a modal, up to the next pane (empty when there is none).</summary>
    private static string PinPane(string html, string id)
    {
        var start = html.IndexOf($"data-pin-pane=\"{id}\"", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = html.IndexOf("data-pin-pane=", start + 1, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    /// <summary>The modal body's text without the markup, for a failure message.</summary>
    private static string PinBody(string html) => Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), "\\s+", " ");

    /// <summary>Saves a pin (a same-origin JSON POST) and returns the answer.</summary>
    private static async Task<JsonNode> PinSaveAsync(WebAgentApp app, JsonObject body, int status = 200)
    {
        var answer = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiPins, body, origin: app.Origin);
        Equal(status, answer.Status, "save: " + answer.Text);
        Check(answer.Response.Headers.CacheControl?.NoStore == true, "a save is never cached");
        return answer.Body ?? new JsonObject();
    }

    /// <summary>A stored case, from GET /api/evals/cases/{id}.</summary>
    private static async Task<JsonObject> PinCaseAsync(WebAgentApp app, string caseId)
    {
        var answer = await app.Get(EvalRoutes.ApiCases + "/" + caseId);
        Equal(200, answer.Status, "the case: " + answer.Text);
        return answer.Body!["case"]!.AsObject();
    }

    /// <summary>Runs a suite now and waits for it to end; returns GET /api/evals/runs/{id}.</summary>
    private static async Task<JsonNode> PinRunSuiteAsync(WebAgentApp app, string suiteId)
    {
        var runId = EvalIds.New(EvalIds.Run);
        var started = await app.SendAsync(HttpMethod.Post, EvalRoutes.ApiSuites + "/" + suiteId + "/run", new JsonObject { ["runId"] = runId }, origin: app.Origin);
        Check(started.Status == 200 && started.Body?["posted"]?.GetValue<bool>() == true, "the suite run starts: " + started.Text);
        JsonNode? view = null;
        await WaitUntilAsync(async () =>
        {
            view = (await app.Get(EvalRoutes.ApiRuns + "/" + runId)).Body;
            return view?["run"]?["status"]?.GetValue<string>() is "complete" or "failed" or "cancelled";
        }, "the suite run ends", TimeSpan.FromSeconds(60));
        return view!;
    }

    /// <summary>Checks that a suite run completed with an answer from each of <paramref name="cases"/> cases k times, each one passing.</summary>
    private static void PinAllPassed(JsonNode run, int cases, string what)
    {
        var k = run["run"]?["k"]?.GetValue<int>() ?? 0;
        Check(run["run"]?["status"]?.GetValue<string>() == "complete" && k >= 1 && run["results"] is JsonArray results && results.Count == cases * k
            && results.All(result => result?["status"]?.GetValue<string>() == "done" && result["verdict"]?.GetValue<string>() == "passed"), what + ": " + Cut(run.ToJsonString()));
    }

    /// <summary>A runs.db call of faux/echo (system prompt "Be brief.", "hello pin", at most 64 tokens) as the run engine records one: its
    /// options and messages, and when it completed, the answer with the Spend Guard's field on it.</summary>
    private static RunRecord PinRun(string id, string source = "playground", string status = RunStatus.Completed)
    {
        var spec = new RunSpec { Provider = "faux", Model = "echo", SystemPrompt = "Be brief.", Messages = [new RunMessage { Role = "user", Text = "hello pin" }], MaxTokens = 64 };
        var done = status == RunStatus.Completed;
        return new RunRecord
        {
            Id = id,
            Op = "op-" + id,
            Kind = RunKind.Call,
            Source = source,
            Status = status,
            Provider = "faux",
            Model = "echo",
            Title = "hello pin",
            Options = spec.OptionsJson(),
            Messages = spec.MessagesJson(),
            Output = done ? JsonNode.Parse("""{"role":"assistant","content":[{"type":"text","text":"Echo: hello pin"}],"stopReason":"stop","pisharp":{"op":"op"}}""") : null,
            OutputText = done ? "Echo: hello pin" : null,
            StopReason = done ? "stop" : null,
            CostMicros = done ? 12 : null,
            DurationMs = done ? 900 : null,
            Created = SuStart,
            Started = SuStart,
            Finished = status == RunStatus.Running ? null : SuStart,
            Updated = SuStart,
        };
    }

    /// <summary>The exception of type <typeparamref name="T"/> that <paramref name="run"/> throws.</summary>
    private static async Task<T> PinThrowsAsync<T>(Func<Task> run, string what) where T : Exception
    {
        try
        {
            await run();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name);
    }
}
