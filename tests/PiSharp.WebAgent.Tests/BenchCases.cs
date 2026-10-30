// Decision 0011 (run W4, R7): the Tool Bench. In process, over the priced faux models: a tool-call loop where an auto-respond rule answers
// a call with its mock and the next turn starts by itself, a schema violation pauses the bench until a manual result continues it, a call
// without a mock pauses (or, with that rule off, gets the "no mock" error), a call of a tool the bench did not send pauses and nothing runs;
// every turn is one guarded run recorded in runs.db with source "bench" under the conversation's group, Stop skips the pending calls and Run
// starts again under a new group; a Block cap refuses the turn with the confirm modal's answer; a session running at a restart reads back
// stopped. Tool definitions are validated as JSON Schema (names, types, enums, patterns, required) and built-ins keep PiSharp's own
// declarations. End to end, the built app renders /bench in its empty, awaiting and default states with nothing the CSP would block, and
// /api/bench goes round trip: a session, a run followed over SSE, the view, the call log export, Stop, and the 400/404/409 answers.
// Strict (W4 fix 2): a strict bench tool reaches the OpenAI Responses body as strict: true (and only that tool), is accepted offline by the
// faux model, and is refused by name, before anything is sent, on Anthropic and Gemini; the tool list and editor say what the model does
// with it, and the tool check lists what OpenAI's strict mode would also ask of a schema.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Bench;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    private static readonly string[] BenchScripts = ["bench.js", "bench-tools.js", "bench-setup.js"];

    internal static IEnumerable<(string, Func<Task>)> BenchCases() =>
    [
        Case("bench.loop.auto-respond-and-manual-result", async () =>
        {
            await using var host = BenchHost.Faux("bench-loop");
            var runner = host.Runner;
            var setup = BenchFauxSetup(BenchWeather(new BenchMock { Name = "Oslo", Match = new JsonObject { ["location"] = "Oslo" }, Text = """{"temp":4}""" }));
            var session = runner.Create(setup, ["""tool: get_weather {"location":"Oslo"}"""]);
            Equal(BenchMode.Continue, BenchRunner.Mode(session), "a user message to run");

            // Auto-respond: the mock answers, the next turn runs by itself, and the conversation finishes.
            var started = await runner.RunAsync(session.Id);
            Check(started is { Refusal: null, Session.State: BenchStates.Running, Session.ActiveRun: not null }, "the first turn runs: " + started.Session.State);
            var done = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Equal(BenchStates.Finished, done.State, "finished: " + done.Note);
            Equal(2, done.Turns.Count, "two turns");
            var call = done.Calls.Single();
            Check(call is { Tool: "get_weather", Known: true, Violations.Count: 0, Result.Source: BenchResultSources.Mock, Result.Mock: "Oslo" } && call.Id.StartsWith("faux:", StringComparison.Ordinal),
                "the call got its mock: " + call);
            Equal("""{"temp":4}""", call.Result!.Text, "the mock's text");
            Equal("""Tool finished: get_weather: {"temp":4}""", done.Turns[1].Text, "the model saw the mock as the tool result");
            Equal("toolUse", done.Turns[0].StopReason, "the first turn stopped for the call");
            var rows = host.Runs.List(new RunQuery { GroupId = done.Group }).Items;
            Equal(2, rows.Count, "each turn is a runs.db row of the conversation's group");
            Check(rows.All(row => row is { Source: "bench", Status: RunStatus.Completed, SpendOp: not null }), "recorded as bench runs: " + string.Join(" | ", rows));
            Equal(string.Join(",", done.Turns.Select(turn => turn.RunId).Order()), string.Join(",", rows.Select(row => row.Id).Order()), "the turns are those rows");
            var ledger = host.Services.GetRequiredService<ISpendLedger>();
            foreach (var row in rows)
                Check((await ledger.GetAsync(row.SpendOp!)) is { Source: SpendSource.Bench, State: ReservationState.Settled }, "the Spend Guard reserved and settled " + row.Id);
            var second = host.Runs.Get(done.Turns[1].RunId)!;
            var sent = second.Messages?["messages"]?.AsArray().Select(item => item!["role"]?.GetValue<string>()).ToList() ?? [];
            Equal("user,assistant,toolResult", string.Join(",", sent), "the second turn sent the call and its result");
            Check(second.Messages!["messages"]![2]!["toolCallId"]?.GetValue<string>() == call.Id, "the result answers the call");

            // A schema violation pauses; a manual result continues.
            var paused = await runner.AddMessageAsync(session.Id, """tool: get_weather {"location":"Oslo","unit":"kelvin"}""", run: true);
            Check(paused.Refusal is null, "the turn runs");
            var awaiting = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Equal(BenchStates.Awaiting, awaiting.State, "paused: " + awaiting.Note);
            var pending = awaiting.Pending.Single();
            Check(pending is { Pause: BenchPauses.Schema, Result: null } && pending.Violations.Any(item => item.Path == "/unit"), "on the schema violation: " + string.Join("; ", pending.Violations));
            Check(awaiting.PausedAt is not null, "with the time it paused");
            await ThrowsAsync<BenchConflictException>(() => runner.RunAsync(session.Id), "Run waits for the pending result");
            await ThrowsAsync<BenchConflictException>(() => runner.AddMessageAsync(session.Id, "more", run: false), "and so does a new message");
            await ThrowsAsync<KeyNotFoundException>(() => runner.SubmitResultAsync(session.Id, "faux:nope", "x", false, null, true), "an unknown call");
            var answered = await runner.SubmitResultAsync(session.Id, pending.Id, """{"temp":277,"unit":"K"}""", false, null, @continue: true);
            Check(answered is { Refusal: null, Session.State: BenchStates.Running }, "the result continues the run");
            var after = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Equal(BenchStates.Finished, after.State, "finished again");
            var manual = after.Call(pending.Id)!;
            Check(manual.Result is { Source: BenchResultSources.Manual, IsError: false, Ms: >= 0 }, "a manual result: " + manual.Result);
            Equal("""Tool finished: get_weather: {"temp":277,"unit":"K"}""", after.Turns[^1].Text, "the model saw exactly what was sent");
            Equal(4, host.Runs.List(new RunQuery { GroupId = after.Group }).Items.Count, "still one group");
            await ThrowsAsync<BenchConflictException>(() => runner.SubmitResultAsync(session.Id, pending.Id, "again", false, null, false), "a call is answered once");

            // No mock matches: the bench pauses; Stop skips the call; Run starts again under a new group.
            await runner.AddMessageAsync(session.Id, """tool: get_weather {"location":"Bergen"}""", run: true);
            var noMock = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Check(noMock.State == BenchStates.Awaiting && noMock.Pending.Single().Pause == BenchPauses.NoMock, "paused: no mock matches");
            var stopped = await runner.StopAsync(session.Id);
            Check(stopped is { State: BenchStates.Stopped, Pending.Count: 0 } && stopped.Calls[^1].Skipped, "Stop: the call never gets a result");
            Equal(BenchMode.Restart, BenchRunner.Mode(stopped), "Run starts again");
            Check(BenchRunner.Broken(stopped), "the conversation cannot go on as it is");
            await ThrowsAsync<BenchConflictException>(() => runner.AddMessageAsync(session.Id, "more", run: false), "no message after a stop on calls");
            var group = stopped.Group;

            // With the no-mock pause off, the bench answers the "no mock" error and goes on.
            await runner.SaveSetupAsync(session.Id, setup with { Rules = new BenchRules { PauseOnNoMock = false } });
            var restart = await runner.RunAsync(session.Id);
            Check(restart.Session is { State: BenchStates.Running, Calls.Count: 0 } && restart.Session.Turns.Count == 1, "Run starts the conversation again from the user messages");
            var errored = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Equal(BenchStates.Finished, errored.State, "finished: " + errored.Note);
            Check(errored.Group is not null && errored.Group != group, "under a new group");
            var bergen = errored.Calls.Single();
            Check(bergen.Result is { Source: BenchResultSources.NoMock, IsError: true, Text: BenchRunner.NoMockText }, "the no-mock error: " + bergen.Result);
            Check(errored.Turns[^1].Text?.StartsWith("Tool finished: get_weather: {\"error\":\"no_mock\"", StringComparison.Ordinal) == true, "the model saw it: " + errored.Turns[^1].Text);

            // A call of a tool the bench did not send pauses, and nothing runs: the user answers it.
            var reset = await runner.ResetAsync(session.Id);
            Check(reset is { State: BenchStates.Idle, Group: null, Calls.Count: 0, Turns.Count: 0 } && reset.UserMessages.Count == 3, "Reset keeps only the user messages");
            await runner.RemoveMessageAsync(session.Id, 0);
            Equal(0, runner.Get(session.Id).Entries.Count, "removing the first message removes everything after it");
            await runner.AddMessageAsync(session.Id, "!ls -la", run: true);
            var unknown = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            var bash = unknown.Pending.Single();
            Check(bash is { Tool: "bash", Known: false, Pause: BenchPauses.UnknownTool } && bash.Arguments?["command"]?.GetValue<string>() == "ls -la", "the unknown tool waits: " + bash);
            await runner.SubmitResultAsync(session.Id, bash.Id, "bash is not available here", isError: true, null, @continue: true);
            var answeredBash = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Check(answeredBash.State == BenchStates.Finished && answeredBash.Call(bash.Id)!.Result is { IsError: true, Source: BenchResultSources.Manual }, "answered by hand with isError");

            // Auto-respond off: every call waits, even one a mock matches.
            await runner.SaveSetupAsync(session.Id, setup with { Rules = new BenchRules { AutoRespond = false } });
            await runner.AddMessageAsync(session.Id, """tool: get_weather {"location":"Oslo"}""", run: true);
            var off = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Check(off.State == BenchStates.Awaiting && off.Pending.Single().Pause == BenchPauses.Manual, "auto-respond off pauses on every call");
            await runner.StopAsync(session.Id);
        }),

        Case("bench.loop.guard-stop-and-restart", async () =>
        {
            var root = Temp("bench-guard");
            try
            {
                string id;
                await using (var host = BenchHost.Faux(root, Path.Join(root, "data")))
                {
                    var runner = host.Runner;
                    var session = runner.Create(BenchFauxSetup(BenchWeather()), ["slow: " + string.Join(" ", Enumerable.Range(1, 400))]);
                    id = session.Id;
                    // Setup errors refuse before anything is sent.
                    var empty = runner.Create(BenchFauxSetup(), ["hi"]);
                    Check((await ThrowsAsync<ArgumentException>(() => runner.RunAsync(empty.Id), "no tools")).Message.Contains("Define a tool first", StringComparison.Ordinal), "says why");
                    var none = runner.Create(BenchFauxSetup(BenchWeather() with { Enabled = false }), ["hi"]);
                    await ThrowsAsync<ArgumentException>(() => runner.RunAsync(none.Id), "no tool switched on");
                    var noMessage = runner.Create(BenchFauxSetup(BenchWeather()));
                    await ThrowsAsync<ArgumentException>(() => runner.RunAsync(noMessage.Id), "no user message");
                    Equal(0L, host.Runs.Count(), "nothing recorded");

                    // A Block cap: the turn is refused with the confirm modal's answer, and the session does not change.
                    var ledger = host.Services.GetRequiredService<ISpendLedger>();
                    await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 1, CapAction.Block));
                    var refused = await runner.RunAsync(id);
                    Check(refused is { Refusal.Code: SpendErrors.Blocked } && refused.Session.Turns.Count == 0 && refused.Session.State == BenchStates.Idle, "blocked, nothing changed");
                    var answer = await BenchModule.SpendAsync(refused.Refusal!, host.Services);
                    Check(answer["code"]?.GetValue<string>() == SpendErrors.Blocked && answer["canProceed"]?.GetValue<bool>() == false && answer["estimate"]?["chip"] is not null,
                        "the confirm modal's answer: " + answer.ToJsonString());
                    Equal(0L, host.Runs.Count(), "a refused turn is not recorded");
                    foreach (var cap in await ledger.CapsAsync()) await ledger.RemoveCapAsync(cap.Id);

                    // Stop a running turn: the run is cancelled (aborted in runs.db) and Run continues from the user message.
                    var running = await runner.RunAsync(id);
                    var runId = running.Session.ActiveRun!;
                    await WaitUntilAsync(() => Task.FromResult(host.Services.GetRequiredService<RunLive>().Find(runId)?.Items.Count > 3), "the slow turn streams");
                    var stopping = await runner.StopAsync(id);
                    Equal(BenchStates.Stopped, stopping.State, "stopped");
                    var stopped = await runner.WhenSettledAsync(id, TimeSpan.FromSeconds(30));
                    Check(stopped.ActiveRun is null && stopped.Turns.Single().Status == RunStatus.Aborted, "the turn is aborted: " + stopped.Turns.Single().Status);
                    Equal(RunStatus.Aborted, host.Runs.Get(runId)!.Status, "and so is its run");
                    Equal(BenchMode.Continue, BenchRunner.Mode(stopped), "Run continues from the user message");
                    var spec = runner.Spec(stopped);
                    Check(spec is { Source: "bench", ToolChoice: null } && spec.Messages.Count == 1 && spec.Tools?.Single().Name == "get_weather", "the next turn sends the message and the tool, not the stopped turn");

                    // A session that is running when the app stops reads back stopped.
                    host.Store.Update(id, current => current with
                    {
                        State = BenchStates.Running,
                        ActiveRun = "r-gone",
                        Entries = [.. current.Entries, new BenchEntry { Kind = "assistant", Turn = new BenchTurn { Index = 2, RunId = "r-gone", Status = RunStatus.Running } }],
                    });
                }
                await using (var again = BenchHost.Faux(root, Path.Join(root, "data")))
                {
                    var back = again.Runner.Get(id);
                    Check(back is { State: BenchStates.Stopped, ActiveRun: null, Note: BenchStore.InterruptedNote }, "interrupted: " + back.State + " " + back.Note);
                    Equal(RunStatus.Interrupted, back.Turns[^1].Status, "the running turn reads back interrupted");
                    Equal(BenchMode.Continue, BenchRunner.Mode(back), "and is never run again by itself");
                }
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("bench.schema.validates-tool-definitions", () =>
        {
            var weather = BenchWeather();
            var valid = BenchSchema.CheckTool(weather.Name, weather.Description, weather.Parameters);
            Check(valid is { Valid: true, Properties: 2, Required: 1 }, "a valid definition: " + string.Join("; ", valid.Errors));
            Check(!BenchSchema.CheckTool("get weather", null, weather.Parameters).Valid, "a name with a space");
            Check(!BenchSchema.CheckTool(new string('a', 65), null, weather.Parameters).Valid, "a name over 64");
            void Invalid(string schema, string what, string says)
            {
                var check = BenchSchema.CheckTool("t", null, JsonNode.Parse(schema));
                Check(!check.Valid && check.Errors.Any(error => error.Contains(says, StringComparison.Ordinal)), what + ": " + string.Join("; ", check.Errors));
            }
            Invalid("""{"type":"array"}""", "the parameters are an object", "type \"object\"");
            Invalid("""[1,2]""", "a schema object", "JSON Schema object");
            Invalid("""{"type":"object","properties":{"a":{"type":"strng"}}}""", "an unknown type", "\"type\" is one of");
            Invalid("""{"type":"object","properties":{"a":{"type":"string","enum":[]}}}""", "an empty enum", "\"enum\"");
            Invalid("""{"type":"object","properties":{"a":{"type":"string","pattern":"(["}}}""", "a pattern that does not compile", "pattern");
            Invalid("""{"type":"object","properties":{"a":{"type":"string"}},"required":["b"]}""", "required names a missing property", "\"b\"");
            Invalid("""{"type":"object","properties":{"a":{"type":"array","items":{"type":"number","minimum":"x"}}}}""", "a nested keyword", "is a number");
            Check(BenchSchema.CheckTool("t", null, null).Valid, "no parameters is an empty object");

            // Normalizing a setup: defaults, refusals, and built-ins keep PiSharp's own declaration.
            var read = BenchTemplates.Builtin("read")!;
            var tampered = new BenchTool { Name = "read", Builtin = true, Parameters = new JsonObject { ["type"] = "object" }, Description = "mine" };
            var normalized = BenchFauxSetup(tampered, weather) with { ToolChoice = "auto", Thinking = "off" };
            var checkedSetup = normalized.Normalize();
            Check(JsonNode.DeepEquals(checkedSetup.Tools[0].Parameters, read.Parameters) && checkedSetup.Tools[0].Description == read.Description, "a built-in's declaration is PiSharp's");
            Check(checkedSetup is { ToolChoice: null, Thinking: null }, "auto and off are the defaults");
            Throws<ArgumentException>(() => BenchFauxSetup(new BenchTool { Name = "nope", Builtin = true }).Normalize(), "an unknown built-in");
            Throws<ArgumentException>(() => BenchFauxSetup(weather, weather).Normalize(), "two tools with one name");
            Throws<ArgumentException>(() => (BenchFauxSetup(weather) with { ToolChoice = "missing" }).Normalize(), "a tool choice that is not a tool");
            Throws<ArgumentException>(() => (BenchFauxSetup(weather with { Enabled = false }) with { ToolChoice = "get_weather" }).Normalize(), "nor one that is off");
            Throws<ArgumentException>(() => (BenchFauxSetup(weather) with { Rules = new BenchRules { Latency = "fast" } }).Normalize(), "an unknown latency");
            Throws<ArgumentException>(() => (BenchFauxSetup(weather) with { Thinking = "huge" }).Normalize(), "an unknown thinking level");
            Throws<ArgumentException>(() => BenchFauxSetup(weather with { Parameters = JsonNode.Parse("""{"type":"string"}""")!.AsObject() }).Normalize(), "a schema that is not an object");
            var mocks = BenchFauxSetup(weather with { Mocks = [new BenchMock { Text = "a", Match = new JsonObject() }, new BenchMock { Name = "Named", Text = "b" }] }).Normalize().Tools[0].Mocks;
            Check(mocks[0] is { Name: "Mock 1", Match: null, Fallback: true } && mocks[1].Name == "Named", "mocks get a name; an empty match is the fallback");

            // Arguments against the schema (Pi's check) and mock matching.
            Check(BenchSchema.CheckArguments("get_weather", weather.Parameters, new JsonObject { ["location"] = "Oslo" }, false).Count == 0, "valid arguments");
            var wrongType = BenchSchema.CheckArguments("get_weather", weather.Parameters, new JsonObject { ["location"] = new JsonObject { ["a"] = 1 } }, false);
            Check(wrongType.Any(item => item.Path == "/location"), "a wrong type: " + string.Join("; ", wrongType));
            Check(BenchSchema.CheckArguments("get_weather", weather.Parameters, new JsonObject(), false).Count > 0, "a missing required property");
            Check(BenchSchema.CheckArguments("get_weather", weather.Parameters, JsonValue.Create("x"), false).Single().Path == "/", "arguments that are not an object");
            Check(BenchSchema.CheckArguments("read", read.Parameters, new JsonObject(), true).Count > 0, "a built-in's arguments are checked too");
            Check(BenchSchema.Matches(new JsonObject { ["n"] = 4 }, new JsonObject { ["n"] = 4.0, ["other"] = true }), "a match is a subset; numbers compare by value");
            Check(!BenchSchema.Matches(new JsonObject { ["n"] = 4 }, new JsonObject { ["n"] = "4" }), "a string is not a number");
            Check(BenchSchema.Matches(new JsonObject { ["a"] = new JsonObject { ["b"] = "x" } }, new JsonObject { ["a"] = new JsonObject { ["b"] = "x", ["c"] = 1 } }), "nested subsets");

            // Decisions: mock, fallback, schema pause, no mock.
            var withMocks = BenchFauxSetup(weather with
            {
                Mocks = [new BenchMock { Name = "Oslo", Match = new JsonObject { ["location"] = "Oslo" }, Text = "oslo" }],
            }).Normalize();
            Check(BenchRunner.Decide(withMocks, "get_weather", new JsonObject { ["location"] = "Oslo" }) is { Pause: null, Result.Mock: "Oslo" }, "the matching mock");
            Equal(BenchPauses.NoMock, BenchRunner.Decide(withMocks, "get_weather", new JsonObject { ["location"] = "Rome" }).Pause, "no mock matches");
            Equal(BenchPauses.Schema, BenchRunner.Decide(withMocks, "get_weather", new JsonObject { ["location"] = "Oslo", ["unit"] = "k" }).Pause, "a schema violation pauses first");
            var fallback = (withMocks with { Tools = [withMocks.Tools[0] with { Mocks = [.. withMocks.Tools[0].Mocks, new BenchMock { Name = "Any", Text = "any" }] }] }).Normalize();
            Equal("Any", BenchRunner.Decide(fallback, "get_weather", new JsonObject { ["location"] = "Rome" }).Result?.Mock, "the fallback");
            var lax = withMocks with { Rules = new BenchRules { PauseOnSchema = false, PauseOnNoMock = false } };
            Check(BenchRunner.Decide(lax, "get_weather", new JsonObject { ["location"] = "Oslo", ["unit"] = "k" }) is { Pause: null, Result.Mock: "Oslo", Violations.Count: 1 },
                "with the schema pause off, the mock answers and the violation is kept");
            Check(BenchRunner.Decide(lax, "nope", new JsonObject()) is { Known: false, Pause: null, Result.Source: BenchResultSources.NoMock }, "an unknown tool gets the no-mock error");

            // The templates: each example and each built-in is a valid definition.
            foreach (var name in new[] { "read", "grep", "find", "ls", "bash", "edit", "write" })
                Check(BenchTemplates.Builtin(name) is { Parameters: not null }, "the built-in " + name);
            foreach (var template in BenchTemplates.All)
                Check(BenchSchema.CheckTool(template.Tool.Name, template.Tool.Description, template.Tool.Parameters).Valid, "the template " + template.Id + " is valid");
            Check(BenchTemplates.All.All(template => !template.Json.Contains("mock", StringComparison.OrdinalIgnoreCase)), "no template carries a sample mock");
        }),

        Case("bench.page.renders-each-state", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("bench-page");
            var first = await app.Get("/bench");
            Equal(200, first.Status, "GET /bench");
            var empty = first.Text;
            CheckShell("/bench", empty);
            Check(empty.Contains("<h1 class=\"app-title\">Tool Bench", StringComparison.Ordinal), "the top bar title");
            Check(!empty.Contains("Coming in a later milestone", StringComparison.Ordinal), "no longer the later-milestone page");
            Check(Regex.IsMatch(empty, "data-bench data-session=\"b[0-9a-f]{32}\" data-version=\"\\d+\" data-state=\"empty\""), "the empty state");
            Check(empty.Contains("Give the model something to call.", StringComparison.Ordinal) && empty.Contains("No tools defined", StringComparison.Ordinal)
                && empty.Contains("Create your first tool", StringComparison.Ordinal), "its texts");
            Check(Regex.IsMatch(empty, "title=\"Define a tool first\" disabled=\"disabled\" data-bench-run"), "Run is disabled with its reason");
            Check(empty.Contains("No tool calls yet", StringComparison.Ordinal), "the empty call log");
            Check(Regex.IsMatch(empty, "data-preset-save>") && empty.Contains("data-preset-load", StringComparison.Ordinal) && !empty.Contains("Presets are not available yet", StringComparison.Ordinal),
                "the preset hooks for Presets");
            Check(Regex.IsMatch(empty, "id=\"ruleBuiltin\" disabled"), "built-ins never run");
            foreach (var modal in new[] { "modal-model-picker", "modal-tool-editor", "modal-spend-confirm", "modal-shortcuts", "modal-confirm" })
                Check(empty.Contains($"id=\"{modal}\"", StringComparison.Ordinal), "the page hosts #" + modal);
            foreach (var template in new[] { "bench-mock-section", "bench-mock-row", "bench-builtin-note", "bench-strict-note", "bench-tool-delete" })
                Check(empty.Contains($"<template id=\"{template}\">", StringComparison.Ordinal), "the editor's " + template);
            Check(Regex.IsMatch(empty, "<script type=\"module\" src=\"/js/bench\\.js\\?v=") && Regex.IsMatch(empty, "<link href=\"/css/bench\\.css\\?v="),
                "its module and stylesheet: " + string.Join(" ", Regex.Matches(empty, "<(script|link)[^>]*bench[^>]*>").Select(match => match.Value)));
            foreach (var script in BenchScripts)
            {
                var asset = await app.Get("/js/" + script);
                Equal(200, asset.Status, script);
                Check(!Regex.IsMatch(asset.Text, "\\.innerHTML\\s*=\\s*[^'\";\\n]*\\b(delta|text|message|fill)\\b"), script + " never writes model or user text as HTML");
                Check(!Regex.IsMatch(asset.Text, "\\.insertAdjacentHTML|document\\.write"), script + " writes no other HTML");
            }
            Equal(200, (await app.Get("/css/bench.css")).Status, "bench.css");

            // Awaiting: auto-respond off, the call waits for a result.
            var created = await app.SendAsync(HttpMethod.Post, "/api/bench/sessions", new JsonObject
            {
                ["setup"] = BenchSetupJson(autoRespond: false),
                ["messages"] = new JsonArray("""tool: get_weather {"location":"Oslo","unit":"kelvin"}"""),
            }, origin: app.Origin);
            Equal(200, created.Status, "a session: " + created.Text);
            var id = created.Body!["id"]!.GetValue<string>();
            Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/run", new JsonObject(), origin: app.Origin)).Status, "Run");
            await BenchStateAsync(app, id, BenchStates.Awaiting);
            var awaiting = (await app.Get("/bench?session=" + id)).Text;
            CheckShell("/bench awaiting", awaiting);
            Check(awaiting.Contains("data-state=\"awaiting\"", StringComparison.Ordinal) && Regex.IsMatch(awaiting, "data-awaiting=\"faux:[0-9a-f]{12}\""), "the awaiting state and card");
            Check(awaiting.Contains("Awaiting result", StringComparison.Ordinal) && awaiting.Contains("Auto-respond is off, so every call waits for you.", StringComparison.Ordinal), "why it paused");
            Check(Regex.IsMatch(awaiting, "Paused (·|&#xB7;) 1 result pending") && awaiting.Contains("data-bench-stop", StringComparison.Ordinal), "the paused chip and Stop");
            Check(awaiting.Contains("data-fill=\"{&quot;temp&quot;:4}\"", StringComparison.Ordinal) && awaiting.Contains("data-fill-name=\"Oslo\"", StringComparison.Ordinal), "quick fill from the saved mock");
            Check(Regex.IsMatch(awaiting, "data-result-send=\"continue\"") && awaiting.Contains("Send &amp; continue", StringComparison.Ordinal), "Send & continue");
            Check(awaiting.Contains("schema: unit", StringComparison.Ordinal) && awaiting.Contains("bench-violations", StringComparison.Ordinal), "the schema violation on the call");
            Check(Regex.IsMatch(awaiting, "data-bench-log-row=\"faux:[0-9a-f]{12}\"") && awaiting.Contains(">awaiting</span>", StringComparison.Ordinal), "the call log row");
            Check(Regex.IsMatch(awaiting, "id=\"benchUserMsg\"[^>]*disabled") && awaiting.Contains("Send the pending tool result first", StringComparison.Ordinal), "no new message while paused");

            // Default: a finished conversation, with model and user text encoded or sanitized.
            var xss = await app.SendAsync(HttpMethod.Post, "/api/bench/sessions", new JsonObject
            {
                ["setup"] = BenchSetupJson(autoRespond: true),
                ["messages"] = new JsonArray("**bold** <script>alert(1)</script>"),
            }, origin: app.Origin);
            var xssId = xss.Body!["id"]!.GetValue<string>();
            await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{xssId}/run", new JsonObject(), origin: app.Origin);
            await BenchStateAsync(app, xssId, BenchStates.Finished);
            var finished = (await app.Get("/bench?session=" + xssId)).Text;
            CheckShell("/bench finished", finished);
            Check(finished.Contains("data-state=\"default\"", StringComparison.Ordinal) && Regex.IsMatch(finished, "finished (·|&#xB7;) stop"), "the default state, finished");
            Check(finished.Contains("<strong>bold</strong>", StringComparison.Ordinal) && !finished.Contains("<script>alert", StringComparison.Ordinal)
                && finished.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", StringComparison.Ordinal), "Markdown sanitized; the user's text encoded");
            Check(Regex.IsMatch(finished, "href=\"/runs/[^\"]+\"") && Regex.IsMatch(finished, "1 turn (·|&#xB7;) 0 tool calls"), "Inspect and the summary");
            Check(finished.Contains("data-bench-remove=\"0\"", StringComparison.Ordinal), "a user message can be removed");
            var missing = (await app.Get("/bench?session=nope")).Text;
            Check(missing.Contains("There is no bench session nope", StringComparison.Ordinal), "an unknown session says so");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("bench.http.round-trip", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("bench-http");
            var created = await app.SendAsync(HttpMethod.Post, "/api/bench/sessions", new JsonObject
            {
                ["setup"] = BenchSetupJson(autoRespond: true),
                ["messages"] = new JsonArray("""tool: get_weather {"location":"Oslo"}"""),
            }, origin: app.Origin);
            Equal(200, created.Status, "created: " + created.Text);
            var id = created.Body!["id"]!.GetValue<string>();
            Equal("/bench?session=" + id, created.Body["url"]?.GetValue<string>(), "its page");
            Equal("no-store", created.Response.Headers.CacheControl?.ToString(), "never cached");
            Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/bench/sessions", new JsonObject(), origin: "http://evil.example")).Status, "a cross-origin write is refused");

            var view = await app.Get($"/api/bench/sessions/{id}/view");
            Check(view.Body is { } body && body["state"]?.GetValue<string>() == BenchStates.Idle && body["mode"]?.GetValue<string>() == "continue"
                && body["messages"]?.AsArray().Count == 1 && body["html"]?["timeline"]?.GetValue<string>().Contains("tool: get_weather", StringComparison.Ordinal) == true,
                "the view: " + Cut(view.Text));
            foreach (var part in new[] { "controls", "timelineHead", "timeline", "tools", "log" })
                Check(view.Body!["html"]?[part] is not null, "the view's " + part);

            // Run, followed over the session's event stream until it finishes.
            var events = BenchSessionFramesAsync(app, id, BenchStates.Finished);
            var run = await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/run", new JsonObject(), origin: app.Origin);
            Check(run.Status == 200 && run.Body?["posted"]?.GetValue<bool>() == true && run.Body["runId"] is not null && run.Body["view"]?["state"]?.GetValue<string>() == BenchStates.Running,
                "posted: " + Cut(run.Text));
            var frames = await events;
            Check(frames.Count >= 2 && frames[^1]["state"]?.GetValue<string>() == BenchStates.Finished, "the stream reported each change until finished: " + string.Join(" ", frames.Select(frame => frame["state"])));
            Check(frames.Select(frame => frame["version"]!.GetValue<long>()).Zip(frames.Skip(1).Select(frame => frame["version"]!.GetValue<long>())).All(pair => pair.First < pair.Second), "in version order");
            var done = await app.Get($"/api/bench/sessions/{id}/view");
            var timeline = done.Body!["html"]!["timeline"]!.GetValue<string>();
            Check(Regex.IsMatch(timeline, "auto (·|&#xB7;) mock") && timeline.Contains("Tool finished: get_weather", StringComparison.Ordinal), "the timeline: " + Cut(timeline));
            var runs = await app.Get("/api/runs?source=bench");
            Equal(2, runs.Body?["items"]?.AsArray().Count, "two bench runs in history: " + Cut(runs.Text));

            var log = await app.Get($"/api/bench/sessions/{id}/log.json");
            Equal(200, log.Status, "the log");
            Check(log.Response.Content.Headers.ContentDisposition?.ToString().StartsWith("attachment; filename=\"bench-calls-", StringComparison.Ordinal) == true, "as a file");
            Check(log.Body?["calls"]?[0]?["result"]?["source"]?.GetValue<string>() == "mock" && log.Body["turns"]?.AsArray().Count == 2, "every call and turn: " + Cut(log.Text));

            // Tool checks, setup errors and the 400/404/409 answers.
            var check = await app.SendAsync(HttpMethod.Post, "/api/bench/tools/check",
                new JsonObject { ["tool"] = new JsonObject { ["name"] = "t", ["parameters"] = JsonNode.Parse("""{"type":"object","required":["x"]}""") } }, origin: app.Origin);
            Check(check.Body?["valid"]?.GetValue<bool>() == false && check.Body["errors"]?.AsArray().Count > 0, "an invalid definition: " + check.Text);
            var good = await app.SendAsync(HttpMethod.Post, "/api/bench/tools/check", new JsonObject { ["tool"] = BenchSetupJson(true)["tools"]![0]!.DeepClone() }, origin: app.Origin);
            Check(good.Body?["valid"]?.GetValue<bool>() == true, "a valid one: " + good.Text);
            var badSetup = await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/setup", new JsonObject { ["setup"] = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "bad name" }) } }, origin: app.Origin);
            Check(badSetup.Status == 400 && badSetup.Body?["error"]?["code"]?.GetValue<string>() == "invalid", "a bad setup: " + badSetup.Text);
            Equal(404, (await app.Get("/api/bench/sessions/nope/view")).Status, "an unknown session");
            Equal(404, (await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/calls/nope/result", new JsonObject { ["text"] = "x" }, origin: app.Origin)).Status, "an unknown call");

            var slow = await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/messages",
                new JsonObject { ["text"] = "slow: " + string.Join(" ", Enumerable.Range(1, 400)), ["run"] = true }, origin: app.Origin);
            Check(slow.Body?["posted"]?.GetValue<bool>() == true, "Add & run: " + Cut(slow.Text));
            var slowRun = slow.Body!["runId"]!.GetValue<string>();
            var conflict = await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/run", new JsonObject(), origin: app.Origin);
            Check(conflict.Status == 409 && conflict.Body?["error"]?["code"]?.GetValue<string>() == "conflict", "Run while a turn runs: " + conflict.Text);
            var stop = await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/stop", new JsonObject(), origin: app.Origin);
            Equal(BenchStates.Stopped, stop.Body?["state"]?.GetValue<string>(), "stopped: " + Cut(stop.Text));
            await WaitUntilAsync(async () => (await app.Get($"/api/runs/{slowRun}")).Body?["run"]?["status"]?.GetValue<string>() == RunStatus.Aborted, "the slow run is aborted");
            await WaitUntilAsync(async () => (await app.Get($"/api/bench/sessions/{id}")).Body?["session"]?["activeRun"] is null, "the stopped turn is recorded");
            var reset = await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/reset", new JsonObject(), origin: app.Origin);
            Check(reset.Status == 200 && reset.Body?["state"]?.GetValue<string>() == BenchStates.Idle && reset.Body["messages"]?.AsArray().Count == 2, "reset: " + Cut(reset.Text));
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),

        Case("bench.strict.sent-on-openai-and-refused-on-anthropic-and-google", async () =>
        {
            await using var host = BenchHost.Routing("bench-strict");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", Planted("sk-proj-")));
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", Planted("sk-ant-")));
            var catalog = host.Services.GetRequiredService<IModelCatalog>();
            var runner = host.Runner;
            var gpt = ModelOf(catalog, "openai", "openai-responses", "gpt-4.1");
            var lookup = BenchLookup() with { Strict = true };
            var weather = BenchWeather();
            var setup = new BenchSetup { Provider = "openai", Model = gpt, Tools = [lookup, weather] };

            // The setup keeps the flag (left out of the JSON when off), and the turn's spec carries it for that tool only.
            var json = JsonSerializer.Serialize(setup.Normalize(), SettingsStore.JsonOptions);
            var back = JsonSerializer.Deserialize<BenchSetup>(json, SettingsStore.JsonOptions)!;
            Check(back.Tools[0].Strict && !back.Tools[1].Strict, "the strict flag round-trips: " + json);
            Equal(1, Regex.Matches(json, "\"strict\":").Count, "it is written only when on: " + json);
            var session = runner.Create(setup, ["bench strict run"]);
            Check(session.Setup.Tools[0].Strict, "the session keeps it");
            var spec = runner.Spec(session);
            Check(spec.Tools is [{ Name: "lookup", Strict: true }, { Name: "get_weather", Strict: null }],
                "RunTool.Strict for the strict tool only: " + string.Join(", ", spec.Tools!.Select(tool => tool.Name + "=" + tool.Strict)));
            var openai = host.Pages.View(session);
            Check(openai.StrictTaken && openai.StrictNote is ({ } sentNote, "faint") && sentNote.Contains("lookup: sent as strict: true on the Responses API", StringComparison.Ordinal),
                "the tool list says how it is sent: " + openai.StrictNote);
            Check(openai.StrictEditorNote.Contains("as strict: true on the Responses API", StringComparison.Ordinal), "and so does the editor: " + openai.StrictEditorNote);

            // On the Responses API the strict tool goes out with strict: true, the other one without, and the turn finishes.
            var started = await runner.RunAsync(session.Id);
            Check(started is { Refusal: null, Session.State: BenchStates.Running }, "the turn runs: " + started.Session.Note);
            var done = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Equal(BenchStates.Finished, done.State, "finished: " + done.Note + " " + done.Turns.LastOrDefault()?.Error);
            var body = JsonNode.Parse(host.Provider.Requests.Single(request => request.Body?.Contains("bench strict run", StringComparison.Ordinal) == true).Body!)!;
            var tools = body["tools"]!.AsArray().OfType<JsonObject>().ToDictionary(tool => tool["name"]!.GetValue<string>());
            Equal(true, tools["lookup"]["strict"]?.GetValue<bool>(), "the strict tool is sent strict");
            Check(tools["get_weather"]["strict"] is not JsonValue other || !other.GetValue<bool>(), "and only that one: " + tools["get_weather"].ToJsonString());
            var row = host.Runs.Get(done.Turns.Single().RunId)!;
            Check(row is { Source: "bench" } && row.Options?["applied"]?.AsArray().Any(item => item?.GetValue<string>() == RunModelOptions.StrictTools) == true,
                "recorded as applied: " + row.Options?.ToJsonString());

            // Anthropic and Gemini take no strict tools: Run refuses by name, before anything is sent or recorded, and the page says so first.
            var sent = host.Provider.Requests.Count;
            var recorded = host.Runs.Count();
            async Task RefusedOn(string provider, string model, string api)
            {
                await runner.SaveSetupAsync(session.Id, setup with { Provider = provider, Model = model });
                var error = await ThrowsAsync<ArgumentException>(() => runner.RunAsync(session.Id), "strict on " + provider);
                Check(error.Message.StartsWith($"Strict is on for lookup, and {provider}/{model} cannot take it: ", StringComparison.Ordinal)
                    && error.Message.Contains(api, StringComparison.Ordinal) && error.Message.Contains("Switch Strict off for it in the tool's editor", StringComparison.Ordinal)
                    && error.Message.EndsWith("nothing was sent.", StringComparison.Ordinal), $"a clear refusal on {provider}: {error.Message}");
                var view = host.Pages.View(runner.Get(session.Id));
                Check(!view.StrictTaken && view.StrictNote is ({ } note, "danger") && note.Contains("Run refuses until you switch Strict off", StringComparison.Ordinal)
                    && note.Contains(api, StringComparison.Ordinal), $"the tool list says so before Run on {provider}: {view.StrictNote}");
                Check(view.StrictEditorNote.Contains("cannot take Strict", StringComparison.Ordinal), $"and so does the editor on {provider}: {view.StrictEditorNote}");
                Equal(sent, host.Provider.Requests.Count, "nothing was sent to " + provider);
                Equal(recorded, host.Runs.Count(), "nothing was recorded for " + provider);
            }
            var claude = ModelOf(catalog, "anthropic", "anthropic-messages");
            await RefusedOn("anthropic", claude, "Anthropic Messages API");
            await RefusedOn("google", ModelOf(catalog, "google", "google-generative-ai"), "Gemini API");

            // A strict tool that is switched off is not sent, so it refuses nothing.
            Check(BenchStrict.Refusal(setup with { Provider = "anthropic", Model = claude, Tools = [lookup with { Enabled = false }, weather] },
                host.Engine.Capabilities("anthropic", claude)!) is null, "a strict tool that is off is not sent, so it is not refused");

            // Strict off: the same model runs the turn, and nothing strict is sent.
            await runner.SaveSetupAsync(session.Id, setup with { Provider = "anthropic", Model = claude, Tools = [lookup with { Strict = false }, weather] });
            var plain = await runner.RunAsync(session.Id);
            Check(plain is { Refusal: null, Session.State: BenchStates.Running }, "Strict off runs: " + plain.Session.Note);
            var anthropic = await runner.WhenSettledAsync(session.Id, TimeSpan.FromSeconds(30));
            Equal(BenchStates.Finished, anthropic.State, "finished on Anthropic: " + anthropic.Note + " " + anthropic.Turns.LastOrDefault()?.Error);
            var messages = host.Provider.Requests.Last(request => request.Url.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)).Body!;
            Check(messages.Contains("\"lookup\"", StringComparison.Ordinal) && !Regex.IsMatch(messages, "\"strict\"\\s*:\\s*true"), "no strict tool on Anthropic: " + messages);
        }),

        Case("bench.strict.lists-what-openai-strict-mode-asks-of-a-schema", () =>
        {
            Equal(0, BenchStrict.Problems(BenchLookup().Parameters).Count, "a schema that fits strict mode");
            var weather = BenchStrict.Problems(BenchWeather().Parameters);
            Check(weather is [var extra, var optional] && extra == "parameters: set \"additionalProperties\": false"
                && optional.StartsWith("parameters: list every property in \"required\" (unit is not)", StringComparison.Ordinal), "the root's two rules: " + string.Join(" | ", weather));
            var nested = BenchStrict.Problems(JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["to","tags","any"],"properties":{
                  "to":{"type":"object","properties":{"city":{"type":"string"},"zip":{"type":"string"}},"required":["city"]},
                  "tags":{"type":"array","items":{"type":"object","properties":{"k":{"type":"string"}},"required":["k"],"additionalProperties":false}},
                  "any":{"anyOf":[{"type":"string"},{"type":["object","null"],"properties":{"x":{"type":"number"}},"required":["x"]}]}}}
                """)!.AsObject());
            Check(nested is [var to, var zip, var anyOf] && to == "parameters/properties/to: set \"additionalProperties\": false"
                && zip.StartsWith("parameters/properties/to: list every property in \"required\" (zip is not)", StringComparison.Ordinal)
                && anyOf == "parameters/properties/any/anyOf/1: set \"additionalProperties\": false", "nested objects, array items and unions: " + string.Join(" | ", nested));
            Check(BenchStrict.Problems(null) is [var empty] && empty.Contains("additionalProperties", StringComparison.Ordinal), "the empty schema needs additionalProperties false too");
            Check(BenchStrict.Problems(BenchTemplates.Builtin("read")!.Parameters).Count > 0, "PiSharp's own read declaration does not fit strict mode (optional offset and limit)");
            Check(BenchSchema.NormalizeTool(new BenchTool { Name = "read", Builtin = true, Strict = true }) is { Strict: true, Builtin: true }, "a built-in may still be sent strict");
        }),

        Case("bench.strict.page-says-what-the-model-does-and-faux-accepts-it", async () =>
        {
            await using var app = await WebAgentApp.StartAsync("bench-strict-page");
            var setup = BenchSetupJson(autoRespond: true);
            setup["tools"]![0]!["strict"] = true;
            var created = await app.SendAsync(HttpMethod.Post, "/api/bench/sessions", new JsonObject
            {
                ["setup"] = setup,
                ["messages"] = new JsonArray("""tool: get_weather {"location":"Oslo"}"""),
            }, origin: app.Origin);
            Equal(200, created.Status, "a session: " + created.Text);
            var id = created.Body!["id"]!.GetValue<string>();
            Equal(true, created.Body["session"]?["setup"]?["tools"]?[0]?["strict"]?.GetValue<bool>(), "the session keeps Strict");

            var page = (await app.Get("/bench?session=" + id)).Text;
            CheckShell("/bench strict", page);
            Check(page.Contains("data-bench-strict=\"get_weather\"", StringComparison.Ordinal) && page.Contains("data-strict-taken=\"true\"", StringComparison.Ordinal), "the strict chip");
            Check(page.Contains("data-strict-note=\"The faux model accepts Strict offline and does not apply it", StringComparison.Ordinal), "the editor's line for this model");
            Check(page.Contains("data-bench-strict-note=\"faint\"", StringComparison.Ordinal)
                && page.Contains("Strict is on for get_weather: the faux model accepts it offline and does not apply it.", StringComparison.Ordinal), "the tool list's line");
            Check(page.Contains("&quot;strict&quot;: true", StringComparison.Ordinal), "the schema preview shows it");
            Check(!page.Contains("are not sent yet", StringComparison.Ordinal), "Strict is no longer 'not sent yet' on the bench");
            Check(page.Contains("<template id=\"bench-strict-note\">", StringComparison.Ordinal) && page.Contains("id=\"tool-strict\"", StringComparison.Ordinal),
                "the editor's Strict switch and the bench's line for it");

            // The faux model accepts Strict offline: the loop runs, and the turn records Strict as applied.
            Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/bench/sessions/{id}/run", new JsonObject(), origin: app.Origin)).Status, "Run");
            await BenchStateAsync(app, id, BenchStates.Finished);
            var session = (await app.Get($"/api/bench/sessions/{id}")).Body!["session"]!;
            var first = session["entries"]!.AsArray().First(entry => entry?["turn"] is not null)!["turn"]!["runId"]!.GetValue<string>();
            var run = (await app.Get($"/api/runs/{first}")).Body?["run"];
            Check(run?["options"]?["applied"]?.AsArray().Any(item => item?.GetValue<string>() == RunModelOptions.StrictTools) == true, "Strict applied: " + run?["options"]?.ToJsonString());

            // The tool check: with Strict on, what OpenAI's strict mode would also ask of a valid schema.
            async Task<JsonNode> CheckTool(JsonNode parameters, bool strict) => (await app.SendAsync(HttpMethod.Post, "/api/bench/tools/check",
                new JsonObject { ["tool"] = new JsonObject { ["name"] = "t", ["parameters"] = parameters.DeepClone(), ["strict"] = strict } }, origin: app.Origin)).Body!;
            var loose = await CheckTool(BenchWeather().Parameters!, strict: true);
            Check(loose["valid"]?.GetValue<bool>() == true && loose["strictProblems"]?.AsArray().Count == 2, "two strict-mode problems: " + loose.ToJsonString());
            Equal(0, (await CheckTool(BenchWeather().Parameters!, strict: false))["strictProblems"]?.AsArray().Count, "none asked without Strict");
            Equal(0, (await CheckTool(BenchLookup().Parameters!, strict: true))["strictProblems"]?.AsArray().Count, "none for a schema that fits");

            // The editor's script drives the shared #tool-strict: it sends the flag, shows the problems and puts the bench's line under it.
            var script = (await app.Get("/js/bench-tools.js")).Text;
            Check(script.Contains("#tool-strict", StringComparison.Ordinal) && script.Contains("strict: strictOn()", StringComparison.Ordinal)
                && script.Contains("strictProblems", StringComparison.Ordinal) && script.Contains("bench-strict-note", StringComparison.Ordinal), "bench-tools.js wires Strict");
            Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
        }),
    ];

    /// <summary>A lookup tool whose schema fits OpenAI's strict mode (every property required, additionalProperties false).</summary>
    private static BenchTool BenchLookup() => new()
    {
        Name = "lookup",
        Description = "Look a word up.",
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"word":{"type":"string"}},"required":["word"],"additionalProperties":false}""")!.AsObject(),
    };

    /// <summary>A get_weather tool: location (required) and unit (c or f).</summary>
    private static BenchTool BenchWeather(params BenchMock[] mocks) => new()
    {
        Name = "get_weather",
        Description = "Current weather for a place.",
        Parameters = JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"},"unit":{"type":"string","enum":["c","f"]}},"required":["location"]}""")!.AsObject(),
        Mocks = mocks,
    };

    private static BenchSetup BenchFauxSetup(params BenchTool[] tools) => new() { Provider = "faux", Model = "echo", Tools = tools };

    /// <summary>A faux setup as the page sends it: get_weather with an Oslo mock.</summary>
    private static JsonObject BenchSetupJson(bool autoRespond) => new()
    {
        ["provider"] = "faux",
        ["model"] = "echo",
        ["tools"] = new JsonArray(new JsonObject
        {
            ["name"] = "get_weather",
            ["description"] = "Current weather for a place.",
            ["parameters"] = JsonNode.Parse("""{"type":"object","properties":{"location":{"type":"string"},"unit":{"type":"string","enum":["c","f"]}},"required":["location"]}"""),
            ["mocks"] = new JsonArray(new JsonObject { ["name"] = "Oslo", ["match"] = new JsonObject { ["location"] = "Oslo" }, ["text"] = """{"temp":4}""" }),
        }),
        ["rules"] = new JsonObject { ["autoRespond"] = autoRespond },
    };

    private static async Task BenchStateAsync(WebAgentApp app, string id, string state) =>
        await WaitUntilAsync(async () => (await app.Get($"/api/bench/sessions/{id}")).Body?["session"]?["state"]?.GetValue<string>() == state,
            $"bench session {id} is {state}", TimeSpan.FromSeconds(30));

    /// <summary>The "session" frames of a bench session's event stream until one says <paramref name="until"/>.</summary>
    private static async Task<List<JsonNode>> BenchSessionFramesAsync(WebAgentApp app, string id, string until)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await app.Http.GetAsync(new Uri(app.Base, $"/api/bench/sessions/{id}/events"), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType, "an event stream");
        var frames = new List<JsonNode>();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? kind = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal)) kind = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && kind == "session" && JsonNode.Parse(line[6..]) is { } frame)
            {
                frames.Add(frame);
                if (frame["state"]?.GetValue<string>() == until) break;
            }
            else if (line.Length == 0) kind = null;
        }
        return frames;
    }

    /// <summary>The run engine over the priced faux models, with the Tool Bench, in a temporary data folder.</summary>
    internal sealed class BenchHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string? root;

        private BenchHost(ServiceProvider provider, string? root, ProviderFake? fake = null)
        {
            this.provider = provider;
            this.root = root;
            Provider = fake ?? new ProviderFake();
        }

        public IServiceProvider Services => provider;

        public BenchRunner Runner => provider.GetRequiredService<BenchRunner>();

        public BenchPages Pages => provider.GetRequiredService<BenchPages>();

        public BenchStore Store => provider.GetRequiredService<BenchStore>();

        public RunStore Runs => provider.GetRequiredService<RunStore>();

        public RunEngine Engine => provider.GetRequiredService<RunEngine>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        /// <summary>The fake provider every request goes to (only used by <see cref="Routing"/>).</summary>
        public ProviderFake Provider { get; }

        /// <summary>Over real routing (not --faux) with <see cref="Provider"/> as every provider request's handler, in a new temporary folder.</summary>
        public static BenchHost Routing(string name)
        {
            var root = Temp(name);
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data") };
            options.EnsureDirectories();
            var fake = new ProviderFake();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), root));
            services.AddSingleton(new RoutingOptions { ProviderHandler = fake, CodexTransport = "sse" });
            services.AddWebAgentCore(options);
            services.AddCredentialsArea(options);
            services.AddRoutingArea(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            services.AddBenchArea(options);
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new BenchHost(provider, root, fake);
        }

        public static BenchHost Faux(string name)
        {
            var root = Temp(name);
            return Build(Path.Join(root, "data"), root);
        }

        /// <summary>Over <paramref name="data"/>, which the caller removes.</summary>
        public static BenchHost Faux(string root, string data) => Build(data, null);

        private static BenchHost Build(string data, string? owned)
        {
            var options = new WebAgentOptions { DataDirectory = data, Faux = true, FauxMetered = true };
            options.EnsureDirectories();
            var services = new ServiceCollection().AddLogging();
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            services.AddBenchArea(options);
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new BenchHost(provider, owned);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            if (root is not null) DeleteTemp(root);
        }
    }
}
