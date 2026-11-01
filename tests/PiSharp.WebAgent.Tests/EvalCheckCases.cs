using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Evals.Checks;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;
using EvalCheck = PiSharp.WebAgent.Evals.Check;

// Decision 0011 (run W5, E2): the checks and the rubric judge. Each free check passes and fails on crafted answers with the mockup's wording
// (contains, regex, JSON schema through InputSchema, tool calls with argument rules, cost and latency); a catastrophic regex stops at its
// 100 ms limit and fails cleanly; the runner keeps the case's order and never calls a judge outside a suite run or for a failed answer; the
// judge is validated as a judgment policy, guarded by the Spend Guard and recorded in runs.db with source drift and the suite run's group,
// never cached, routed by confidence (auto passes or fails, unsure goes to review, a bad answer is an error); the judge never sees the
// target's provider or model name; the same-family warning; the estimate and the input budget; and the /api/evals/checks endpoints in the
// app, over faux models only.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> EvalCheckCases() =>
    [
        Case("evals.checks.contains-passes-and-fails", () =>
        {
            var answer = CkAnswer("Use 1.0.0-alpha+001.\n\n```text\nVERSION 2.0.0\n```\n");
            Equal(CheckVerdict.Passed, CkRun(CkContains("1.0.0-alpha"), answer).Verdict, "found");
            Equal("Found in the answer.", CkRun(CkContains("1.0.0-alpha"), answer).Detail, "the line");
            Equal("Found in the reference output.", CheckRunner.Evaluate(CkContains("1.0.0-alpha"), answer, CheckSubject.Reference).Detail, "the mockup's line");
            var missing = CkRun(CkContains("1.0.0-beta"), answer);
            Check(missing is { Verdict: CheckVerdict.Failed, Detail: "Not found in the answer.", Kind: CheckKind.Contains, CheckId: "k1" }, "not found: " + missing);
            Equal(CheckVerdict.Failed, CkRun(CkContains("use 1.0.0"), answer).Verdict, "case matters by default");
            Equal(CheckVerdict.Passed, CkRun(CkContains("use 1.0.0") with { MatchCase = false }, answer).Verdict, "unless it does not");
            var negated = CkRun(CkContains("TODO") with { Negate = true }, answer);
            Check(negated is { Verdict: CheckVerdict.Passed, Detail: "Not in the answer, as required." }, "must not contain: " + negated);
            var found = CkRun(CkContains("alpha") with { Negate = true }, answer);
            Check(found is { Verdict: CheckVerdict.Failed, Detail: "Found in the answer, which must not contain it." }, "contains what it must not: " + found);
            var block = CkContains("1.0.0-alpha") with { Scope = CheckScope.FirstCodeBlock };
            Check(CkRun(block, answer) is { Verdict: CheckVerdict.Failed, Detail: "Not found in the first code block of the answer." }, "only the first code block is read");
            Check(CkRun(CkContains("VERSION 2") with { Scope = CheckScope.FirstCodeBlock }, answer) is { Verdict: CheckVerdict.Passed, Detail: "Found in the first code block of the answer." },
                "found in the block");
            Check(CkRun(block, CkAnswer("no code here")) is { Verdict: CheckVerdict.Failed, Detail: "The answer has no code block." }, "an answer without a block fails");
            Check(CkRun(new EvalCheck { Kind = CheckKind.Contains, Text = "" }, answer) is { Verdict: CheckVerdict.Error, Detail: "Give the text the answer must contain." },
                "a check that cannot run is an error that says why");
        }),

        Case("evals.checks.code-blocks-and-json-are-found", () =>
        {
            var blocks = AnswerParts.CodeBlocks("Intro\n   ```JSON title=\"x\"\n   {\"a\": 1}\n   ```\nand\n~~~~\ncode ``` inside\n~~~\nstill\n~~~~\n```python\nprint(1)");
            Equal(3, blocks.Count, "three blocks: " + string.Join(" | ", blocks));
            Check(blocks[0] is { Language: "json", Content: "{\"a\": 1}", Closed: true }, "an indented fence with an info string: " + blocks[0]);
            Check(blocks[1] is { Language: "", Content: "code ``` inside\n~~~\nstill", Closed: true }, "a longer tilde fence closes only on as many: " + blocks[1]);
            Check(blocks[2] is { Language: "python", Content: "print(1)", Closed: false }, "an unclosed block runs to the end: " + blocks[2]);
            Equal(0, AnswerParts.CodeBlocks("Inline ```code``` is not a fence.").Count, "inline code is not a block");
            var json = AnswerParts.FindJson("Sure! {not json} Here: {\"title\": \"a {b}\", \"n\": [1, {\"x\": 2}]} and {\"later\": true}");
            Check(json is { Source: JsonSource.Text } && json.Object!["title"]!.GetValue<string>() == "a {b}", "the first whole object in the text: " + json.Object?.ToJsonString());
            Check(AnswerParts.FindJson("```json\n{\"from\": \"block\"}\n```\n{\"from\": \"text\"}") is { Source: JsonSource.CodeBlock } fromBlock &&
                fromBlock.Object!["from"]!.GetValue<string>() == "block", "a json code block comes first");
            Check(AnswerParts.FindJson("```json\n[1, 2]\n```\n{\"a\": 1}") is { Object: null, Source: JsonSource.CodeBlock, Problem: "the json code block is not a JSON object" },
                "a json block that is not an object is not skipped");
            Equal(JsonSource.None, AnswerParts.FindJson("no braces at all").Source, "nothing");
            Equal(null, AnswerParts.FirstObject(string.Concat(Enumerable.Repeat("{", 500)) + "{\"a\": 1}"), "at most 100 starts are tried");
            Equal(null, AnswerParts.FirstObject("{\"a\": 1, \"a\": 2}"), "duplicate names are not an object");
        }),

        Case("evals.checks.regex-passes-and-fails", () =>
        {
            var answer = CkAnswer("Here it is:\n```\n^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$\n```\nversion v12 works.");
            var passed = CkRun(CkRegex(@"v\d+"), answer);
            Check(passed.Verdict == CheckVerdict.Passed && passed.Detail!.StartsWith("Matches the answer in ", StringComparison.Ordinal) && passed.Detail.EndsWith(" ms.", StringComparison.Ordinal),
                "matches, timed: " + passed.Detail);
            Check(CheckRunner.Evaluate(CkRegex(@"v\d+"), answer, CheckSubject.Reference).Detail!.StartsWith("Matches the reference output in ", StringComparison.Ordinal), "the mockup's line");
            Check(CkRun(CkRegex(@"v\d{3}"), answer) is { Verdict: CheckVerdict.Failed, Detail: "Doesn't match the answer." }, "no match fails");
            Equal(CheckVerdict.Passed, CkRun(CkRegex("(?i)VERSION V12"), answer).Verdict, "inline options work");
            Check(CkRun(CkRegex("TODO") with { Negate = true }, answer) is { Verdict: CheckVerdict.Passed } negatedPass && negatedPass.Detail!.StartsWith("Doesn't match the answer, as required", StringComparison.Ordinal),
                "must not match");
            Check(CkRun(CkRegex(@"v\d+") with { Negate = true }, answer) is { Verdict: CheckVerdict.Failed, Detail: "Matches the answer (\"v12\"), which it must not." }, "matches what it must not");
            Equal(CheckVerdict.Passed, CkRun(CkRegex(@"^\^\(0") with { Scope = CheckScope.FirstCodeBlock }, answer).Verdict, "the first code block alone");
            Check(CkRun(CkRegex("x") with { Scope = CheckScope.FirstCodeBlock }, CkAnswer("plain")) is { Verdict: CheckVerdict.Failed, Detail: "The answer has no code block." }, "no block fails");
            Check(CkRun(CkRegex("(unclosed"), answer) is { Verdict: CheckVerdict.Error } bad && bad.Detail!.StartsWith("The pattern is not a valid regular expression", StringComparison.Ordinal),
                "a pattern that does not compile is an error");
        }),

        Case("evals.checks.catastrophic-regex-times-out-cleanly", () =>
        {
            var answer = CkAnswer(new string('a', 64) + "!");
            var clock = Stopwatch.StartNew();
            var result = CkRun(CkRegex("^(a|aa)+$"), answer);
            clock.Stop();
            Check(result is { Verdict: CheckVerdict.Failed, Detail: "Timed out after 100 ms." }, "a timeout fails the check: " + result);
            Check(clock.Elapsed < TimeSpan.FromSeconds(5), "and stops: " + clock.Elapsed);
            Check(CkRun(CkRegex("^(a|aa)+$") with { Negate = true }, answer) is { Verdict: CheckVerdict.Failed, Detail: "Timed out after 100 ms." }, "negated too: a timeout never passes");
            var tool = new EvalCheck
            {
                Kind = CheckKind.Tool, Tool = "search",
                Arguments = [new ToolArgumentMatch("q", ArgumentRule.Regex, "^(a|aa)+$")],
            };
            var calls = new EvalAnswer { ToolCalls = [new EvalToolCall("search", new JsonObject { ["q"] = new string('a', 64) + "!" })] };
            var argument = CkRun(tool, calls);
            Check(argument is { Verdict: CheckVerdict.Failed, Detail: "The answer calls search once, but the regex for q timed out after 100 ms." }, "an argument rule's timeout fails: " + argument);
        }),

        Case("evals.checks.json-schema-passes-and-fails", () =>
        {
            var check = new EvalCheck { Kind = CheckKind.JsonSchema, Schema = CkSchema() };
            var valid = CkAnswer("Here:\n```json\n{\"title\": \"Login broken\", \"priority\": \"high\", \"count\": 2}\n```");
            Check(CkRun(check, valid) is { Verdict: CheckVerdict.Passed, Detail: "The answer is valid." }, "valid");
            Equal("The reference output is valid.", CheckRunner.Evaluate(check, valid, CheckSubject.Reference).Detail, "the mockup's line");
            Equal(CheckVerdict.Passed, CkRun(check, CkAnswer("Ticket: {\"title\": \"x\", \"priority\": \"low\"} done")).Verdict, "an object in the text");
            var invalid = CkRun(check, CkAnswer("{\"title\": \"x\", \"priority\": \"urgent\", \"count\": 1.5, \"extra\": true}"));
            Check(invalid.Verdict == CheckVerdict.Failed && invalid.Detail!.StartsWith("The answer is not valid: ", StringComparison.Ordinal) &&
                invalid.Detail.Contains("/priority must be one of low, high", StringComparison.Ordinal) && invalid.Detail.Contains("/extra is not a known field", StringComparison.Ordinal) &&
                invalid.Detail.Contains("/count must be a whole number", StringComparison.Ordinal), "each problem by path: " + invalid.Detail);
            Check(CkRun(check, CkAnswer("{\"priority\": \"low\"}")) is { Verdict: CheckVerdict.Failed, Detail: "The answer is not valid: /title is required." }, "a missing field");
            Check(CkRun(check, CkAnswer("No JSON here.")) is { Verdict: CheckVerdict.Failed, Detail: "No JSON object in the answer." }, "no object fails");
            Check(CkRun(check, CkAnswer("```json\n[1]\n```")) is { Verdict: CheckVerdict.Failed, Detail: "In the answer, the json code block is not a JSON object." }, "a json block that is not an object fails");
            Check(CkRun(check, CkAnswer("```json\n{oops\n```")) is { Verdict: CheckVerdict.Failed, Detail: "In the answer, the json code block is not valid JSON." }, "a json block that does not parse fails");
            var many = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            var five = CkRun(new EvalCheck { Kind = CheckKind.JsonSchema, Schema = many }, CkAnswer("{\"a\":1,\"b\":2,\"c\":3,\"d\":4,\"e\":5}"));
            Check(five.Verdict == CheckVerdict.Failed && five.Detail!.EndsWith("; and 2 more.", StringComparison.Ordinal), "at most three problems listed: " + five.Detail);
        }),

        Case("evals.checks.tool-call-passes-and-fails", () =>
        {
            var answer = new EvalAnswer
            {
                ToolCalls =
                [
                    new EvalToolCall("create_ticket", new JsonObject
                    {
                        ["title"] = "Login broken", ["priority"] = "high", ["labels"] = new JsonArray("bug", "auth"), ["estimate"] = 3,
                        ["meta"] = new JsonObject { ["team"] = "web" }, ["notes"] = null,
                    }, "c1"),
                    new EvalToolCall("search", new JsonObject { ["q"] = "login" }, "c2"),
                ],
            };
            var exactly = new EvalCheck
            {
                Kind = CheckKind.Tool, Tool = "create_ticket", Calls = ToolCallCount.ExactlyOnce,
                Arguments =
                [
                    new ToolArgumentMatch("priority", ArgumentRule.EqualsValue, "high"),
                    new ToolArgumentMatch("title", ArgumentRule.Regex, "^Login"),
                    new ToolArgumentMatch("labels", ArgumentRule.Present),
                    new ToolArgumentMatch("estimate", ArgumentRule.EqualsValue, "3.0"),
                    new ToolArgumentMatch("labels", ArgumentRule.EqualsValue, "[\"bug\", \"auth\"]"),
                    new ToolArgumentMatch("meta.team", ArgumentRule.EqualsValue, "web"),
                    new ToolArgumentMatch("labels.1", ArgumentRule.EqualsValue, "auth"),
                ],
            };
            Equal("The reference output calls create_ticket once, with matching arguments.", CheckRunner.Evaluate(exactly, answer, CheckSubject.Reference).Detail, "the mockup's line");
            Equal(CheckVerdict.Passed, CkRun(exactly, answer).Verdict, "every rule holds");
            var wrong = CkRun(exactly with { Arguments = [new ToolArgumentMatch("priority", ArgumentRule.EqualsValue, "low")] }, answer);
            Check(wrong is { Verdict: CheckVerdict.Failed, Detail: "The answer calls create_ticket once, but priority is \"high\", not \"low\"." }, "a wrong value: " + wrong);
            Check(CkRun(exactly with { Arguments = [new ToolArgumentMatch("assignee", ArgumentRule.Present)] }, answer) is
                { Verdict: CheckVerdict.Failed, Detail: "The answer calls create_ticket once, but assignee is missing." }, "a missing argument");
            Check(CkRun(exactly with { Arguments = [new ToolArgumentMatch("notes", ArgumentRule.Present)] }, answer) is { Verdict: CheckVerdict.Failed }, "a null is not present");
            Check(CkRun(exactly with { Arguments = [new ToolArgumentMatch("estimate", ArgumentRule.EqualsValue, "4")] }, answer) is
                { Verdict: CheckVerdict.Failed, Detail: "The answer calls create_ticket once, but estimate is 3, not 4." }, "numbers compare as numbers");
            Check(CkRun(exactly with { Arguments = [new ToolArgumentMatch("title", ArgumentRule.Regex, "^Logout")] }, answer) is
                { Verdict: CheckVerdict.Failed, Detail: "The answer calls create_ticket once, but title (\"Login broken\") doesn't match /^Logout/." }, "a regex rule");
            var atLeast = new EvalCheck { Kind = CheckKind.Tool, Tool = "search" };
            Check(CkRun(atLeast, answer) is { Verdict: CheckVerdict.Passed, Detail: "The answer calls search once." }, "at least once by default");
            Check(CkRun(atLeast with { Tool = "delete_repo" }, answer) is { Verdict: CheckVerdict.Failed, Detail: "The answer doesn't call delete_repo (it calls create_ticket, search)." },
                "not called, with what it did call");
            Check(CkRun(atLeast with { Tool = "delete_repo", Calls = ToolCallCount.Never }, answer) is { Verdict: CheckVerdict.Passed, Detail: "The answer doesn't call delete_repo." }, "never");
            Check(CkRun(atLeast with { Calls = ToolCallCount.Never }, answer) is { Verdict: CheckVerdict.Failed, Detail: "The answer calls search once, which it must not." }, "never, but called");
            var twice = answer with { ToolCalls = [.. answer.ToolCalls, new EvalToolCall("search", new JsonObject { ["q"] = "logout" })] };
            Check(CkRun(atLeast with { Calls = ToolCallCount.ExactlyOnce }, twice) is { Verdict: CheckVerdict.Failed, Detail: "The answer calls search twice, not once." }, "exactly once, called twice");
            var second = atLeast with { Arguments = [new ToolArgumentMatch("q", ArgumentRule.EqualsValue, "logout")] };
            Check(CkRun(second, twice) is { Verdict: CheckVerdict.Passed, Detail: "The answer calls search twice, with matching arguments." }, "at least once: any call may match");
            Check(CkRun(second with { Arguments = [new ToolArgumentMatch("q", ArgumentRule.EqualsValue, "nope")] }, twice) is { Verdict: CheckVerdict.Failed } none &&
                none.Detail == "The answer calls search twice, but no call matches: in the first, q is \"login\", not \"nope\".", "no call matches");
            Equal(CheckVerdict.Failed, CkRun(atLeast, CkAnswer("I would search for it.")).Verdict, "a text answer calls nothing");
        }),

        Case("evals.checks.limits-pass-fail-and-unknown", () =>
        {
            var limits = new EvalCheck { Kind = CheckKind.Limits, MaxCostMicros = 30_000, MaxLatencyMs = 30_000 };
            var reference = new EvalAnswer { Text = "ok", CostMicros = 11_500, ListPrice = true, DurationMs = 18_200 };
            Check(CheckRunner.Evaluate(limits, reference, CheckSubject.Reference) is { Verdict: CheckVerdict.Passed, Detail: "Reference output: ≈ $0.0115 at list price, 18.2 s." },
                "the mockup's line");
            Check(CkRun(limits, reference with { ListPrice = false, CostMicros = 41_200 }) is
                { Verdict: CheckVerdict.Failed, Detail: "Answer: $0.0412 (over the $0.03 limit), 18.2 s." }, "over the cost");
            Check(CkRun(limits, reference with { ListPrice = false, DurationMs = 31_000 }) is
                { Verdict: CheckVerdict.Failed, Detail: "Answer: $0.0115, 31 s (over the 30 s limit)." }, "over the time");
            Check(CkRun(limits, reference with { CostMicros = null }) is { Verdict: CheckVerdict.Error, Detail: "Answer: cost unknown, 18.2 s." }, "an unknown cost is an error, never a pass");
            Check(CkRun(limits, reference with { CostMicros = null, DurationMs = 40_000 }) is { Verdict: CheckVerdict.Failed }, "over a limit fails even when the other is unknown");
            Check(CkRun(new EvalCheck { Kind = CheckKind.Limits, MaxLatencyMs = 900 }, reference with { DurationMs = 850 }) is { Verdict: CheckVerdict.Passed, Detail: "Answer: 850 ms." },
                "only the limits set");
            Equal(CheckVerdict.Passed, CkRun(limits, reference with { CostMicros = 30_000, DurationMs = 30_000 }).Verdict, "at the limit passes");
        }),

        Case("evals.checks.summaries-name-each-check", () =>
        {
            Equal("Contains \"1.0.0-alpha\"", CheckText.Summary(CkContains("1.0.0-alpha").Normalize()), "contains");
            Equal("First code block doesn't contain \"TODO\" (any case)",
                CheckText.Summary((CkContains("TODO") with { Negate = true, MatchCase = false, Scope = CheckScope.FirstCodeBlock }).Normalize()), "negated, any case, in the block");
            Equal("Matches /^v\\d+$/ (100 ms limit)", CheckText.Summary(CkRegex(@"^v\d+$").Normalize()), "regex");
            Equal("Valid JSON with title, priority, count", CheckText.Summary(new EvalCheck { Kind = CheckKind.JsonSchema, Schema = CkSchema() }.Normalize()), "schema");
            Equal("Calls create_ticket exactly once, priority = \"high\", labels present", CheckText.Summary(new EvalCheck
            {
                Kind = CheckKind.Tool, Tool = "create_ticket", Calls = ToolCallCount.ExactlyOnce,
                Arguments = [new ToolArgumentMatch("priority", ArgumentRule.EqualsValue, "high"), new ToolArgumentMatch("labels", ArgumentRule.Present)],
            }.Normalize()), "tool");
            Equal("Never calls delete_repo", CheckText.Summary(new EvalCheck { Kind = CheckKind.Tool, Tool = "delete_repo", Calls = ToolCallCount.Never }.Normalize()), "never");
            Equal("At most $0.03 and 30 s per answer", CheckText.Summary(new EvalCheck { Kind = CheckKind.Limits, MaxCostMicros = 30_000, MaxLatencyMs = 30_000 }.Normalize()),
                "the test case modal's words");
            Equal("LLM judge google/gemini-3-flash, passes at 4 of 5", CheckText.Summary(CkJudge("google", "gemini-3-flash").Normalize()), "judge");
            Equal("18.2 s", CheckRunner.Duration(18_200), "seconds");
            Equal("$0.0115", CheckRunner.Money(11_500), "four decimals under a dollar");
            Equal("$1.50", CheckRunner.Money(1_500_000), "whole cents");
        }),

        Case("evals.checks.validate-refuses-what-cannot-run", async () =>
        {
            await using var host = CkHost.Create("ck-validate");
            var runner = host.Runner;
            var judge = runner.Validate(CkJudge("faux", "echo") with { Judge = new JudgeSpec(" faux ", " echo ", "  Score the regex.  ", 5) });
            Check(judge.Judge is { Provider: "faux", Model: "echo", Rubric: "Score the regex.", PassAt: 5, Account: null }, "normalized: " + judge.Judge);
            Equal("There is no chat model faux/nope to judge with.", Throws<ArgumentException>(() => runner.Validate(CkJudge("faux", "nope")), "an unknown judge model").Message, "says so");
            Check(Throws<ArgumentException>(() => runner.Validate(CkJudge("faux", "echo") with { Judge = new JudgeSpec("faux", "echo", new string('x', 2001)) }), "a long rubric")
                .Message.StartsWith("A judge's rubric is at most 2,000 characters", StringComparison.Ordinal), "the rubric limit");
            Throws<ArgumentException>(() => runner.Validate(CkJudge("faux", "echo") with { Judge = new JudgeSpec("faux", "echo", "  ") }), "an empty rubric");
            Throws<ArgumentException>(() => runner.Validate(CkJudge("faux", "echo") with { Judge = new JudgeSpec("faux", "echo", "x", 6) }), "a passing score above 5");
            Throws<ArgumentException>(() => runner.Validate(CkRegex("(")), "a regex that does not compile");
            var policy = host.Judge.Policy(new JudgeSpec("faux", "echo", "Score it."));
            Check(policy is { Kind: "structured", Auto: RubricJudge.AutoThreshold, Confirm: RubricJudge.ConfirmThreshold, MaxOutputTokens: JudgeSpec.MaxOutputTokens, CacheTtlSeconds: 0 },
                "a structured policy, thresholds 0.70 and 0.50, 512 output tokens, never cached");
            Equal("1,2,3,4,5", string.Join(",", policy.Labels.Select(label => label.Id)), "labels 1 to 5");
            var testCase = CkCase(checks: [CkContains("x"), CkJudge("faux", "nope")]);
            Equal("Check k2: There is no chat model faux/nope to judge with.", Throws<ArgumentException>(() => runner.Validate(testCase), "a case with a bad judge").Message, "names the check");
            var saved = runner.Validate(CkCase(checks: [CkContains("x"), CkJudge("faux", "echo")]));
            Equal("k1,k2", string.Join(",", saved.Checks.Select(check => check.Id)), "ids assigned");
            Equal(1, CheckRunner.JudgeChecks(saved).Count, "one judge check");
        }),

        Case("evals.checks.runner-keeps-order-and-calls-no-judge-outside-a-run", async () =>
        {
            await using var host = CkHost.Create("ck-runner");
            var testCase = CkCase(checks: [CkContains("semver"), CkJudge("faux", "echo"), CkRegex(@"\d"), new EvalCheck { Kind = CheckKind.Limits, MaxCostMicros = 10 }]);
            var answer = CkAnswer("A semver regex: \\d+ matches 1.0.0") with { CostMicros = 5, DurationMs = 10 };
            var results = await host.Runner.RunAsync(testCase, answer, null);
            Equal("k1:passed,k2:error,k3:passed,k4:passed", string.Join(",", results.Select(result => result.CheckId + ":" + EvalJson.Wire(result.Verdict))), "in the case's order");
            Equal("An LLM judge runs only in a suite run.", results[1].Detail, "no judge without a suite run");
            Equal(CheckVerdict.Error, CheckResult.Combine(results), "an error that could not run");
            var context = new JudgeContext { Target = new ModelIdentity("faux", "echo-small"), GroupId = "g-ck-runner" };
            var failed = await host.Runner.RunAsync(testCase, answer with { Error = "overloaded" }, context);
            Check(failed[1] is { Verdict: CheckVerdict.Error, Detail: "Not judged: the call failed, so there is no answer to judge." }, "a failed call is not judged");
            var empty = await host.Runner.RunAsync(testCase, CkAnswer("  "), context);
            Check(empty[1] is { Verdict: CheckVerdict.Failed, Detail: "Not judged: the answer is empty." }, "an empty answer fails without a call");
            Equal(0, host.Asked.Count, "the judge was never called");
            Equal(0, host.Store.List(new RunQuery()).Items.Count, "nothing recorded");
            Equal(CheckVerdict.Failed, CheckResult.Combine(await host.Runner.RunAsync(CkCase(checks: [CkContains("absent"), CkJudge("faux", "echo")]), answer, null)),
                "a failed check outweighs an error");
        }),

        Case("evals.checks.judge-is-guarded-recorded-and-routed", async () =>
        {
            await using var host = CkHost.Create("ck-judge");
            var testCase = CkCase(checks: [CkJudge("faux", "echo")]);
            var answer = CkAnswer("The regex is ^\\d+\\.\\d+\\.\\d+$, which matches 1.0.0.");
            var context = new JudgeContext { Target = new ModelIdentity("faux", "echo-small"), GroupId = "g-ck-suite-1" };
            host.Replies.Enqueue("```json\n{\"label\": \"4\", \"confidence\": 0.9, \"rationale\": \"Correct, but\\nno pre-release.\"}\n```");
            var first = (await host.Runner.RunAsync(testCase, answer, context)).Single();
            Check(first is { Verdict: CheckVerdict.Passed, Score: 4, Confidence: 0.9, CheckId: "k1", Kind: CheckKind.Judge, JudgeRunId: not null } &&
                first.Detail == "Score 4 of 5 (passes at 4). Correct, but no pre-release.", "a confident 4 passes: " + first);
            var run = host.Store.Get(first.JudgeRunId!)!;
            Check(run is { Status: RunStatus.Completed, Source: "drift", GroupId: "g-ck-suite-1", Provider: "faux", Model: "echo", ParentId: null }, "a recorded drift run of the group: " + run);
            host.Replies.Enqueue("{\"label\": \"4\", \"confidence\": 0.9}");
            var child = (await host.Runner.RunAsync(testCase, answer, context with { ParentRunId = "r-answer-1" })).Single();
            Equal("r-answer-1", host.Store.Get(child.JudgeRunId!)!.ParentId, "recorded under the answer's run when given");
            Equal(512, run.Options?["maxTokens"]?.GetValue<int>(), "512 output tokens at most");
            Equal(0.0, run.Options?["temperature"]?.GetValue<double>(), "temperature 0");
            Check(run.Title!.StartsWith("Judge · ", StringComparison.Ordinal), "titled for History: " + run.Title);
            var reservation = (await host.Ledger.GetAsync(run.SpendOp!))!;
            var estimate = await host.Judge.EstimateAsync(new JudgeSpec("faux", "echo", "x"), 1);
            Check(reservation.Source == SpendSource.Drift && reservation.Run == "g-ck-suite-1", "reserved as drift under the suite run's group: " + reservation);
            Check(reservation.ReservedMicros is > 0 && reservation.ReservedMicros <= estimate.EachMicros, $"reserved {reservation.ReservedMicros}, at most the estimate {estimate.EachMicros}");
            Check(run.Options?["inputBytes"]?.GetValue<long>() is > 0 and <= RubricJudge.MaxInputBytes, "within the input budget: " + run.Options?["inputBytes"]);

            host.Replies.Enqueue("{\"label\": \"4\", \"confidence\": 0.9}");
            var again = (await host.Runner.RunAsync(testCase, answer, context)).Single();
            Check(again.Verdict == CheckVerdict.Passed && again.JudgeRunId != first.JudgeRunId, "the same answer is judged again: never cached");
            Equal(3, host.Asked.Count, "three calls");

            async Task<CheckResult> Judged(string reply)
            {
                host.Replies.Enqueue(reply);
                return (await host.Runner.RunAsync(testCase, answer, context)).Single();
            }
            Check(await Judged("{\"score\": 2, \"confidence\": 0.95, \"rationale\": \"Wrong.\"}") is { Verdict: CheckVerdict.Failed, Score: 2, Detail: "Score 2 of 5 (passes at 4). Wrong." },
                "a confident 2 fails (a numeric score is read as the label)");
            Check(await Judged("{\"label\": 5, \"confidence\": 0.55}") is { Verdict: CheckVerdict.Review, Score: 5, Confidence: 0.55 } unsure &&
                unsure.Detail == "Score 5 of 5, but confidence 0.55 is below 0.70: flagged for review.", "an unsure 5 is a review");
            Check(await Judged("{\"label\": \"1\", \"confidence\": 0.3}") is { Verdict: CheckVerdict.Review, Score: 1 }, "an unsure 1 is a review too, not a fail");
            Check(await Judged("{\"label\": \"5\"}") is { Verdict: CheckVerdict.Review, Detail: "Score 5 of 5, but the judge gave no valid confidence: flagged for review." },
                "no confidence is a review");
            Check(await Judged("I think it is good.") is { Verdict: CheckVerdict.Error, Detail: "The judge's answer is not valid: it is not a JSON object.", JudgeRunId: not null },
                "not JSON is an error, with its run");
            Check(await Judged("{\"label\": \"9\", \"confidence\": 0.9}") is { Verdict: CheckVerdict.Error } outside &&
                outside.Detail == "The judge's answer is not valid: Invalid output: the label 9 is not a policy label.", "a label outside 1 to 5 is an error");
            Equal(9, host.Asked.Count, "one call per judgement, no repair calls");
            Equal(9, host.Store.List(new RunQuery { Source = "drift", GroupId = "g-ck-suite-1" }).Items.Count, "every judgement recorded");

            var passAt = CkCase(checks: [CkJudge("faux", "echo") with { Judge = new JudgeSpec("faux", "echo", "Score it.", 5) }]);
            host.Replies.Enqueue("{\"label\": \"4\", \"confidence\": 0.99}");
            Check((await host.Runner.RunAsync(passAt, answer, context)).Single() is { Verdict: CheckVerdict.Failed, Detail: "Score 4 of 5 (passes at 5)." }, "the passing score is the check's");

            host.Failures.Enqueue("The faux judge is down.");
            var down = (await host.Runner.RunAsync(testCase, answer, context)).Single();
            Check(down is { Verdict: CheckVerdict.Error, JudgeRunId: not null } && down.Detail!.StartsWith("The judge call failed: ", StringComparison.Ordinal) &&
                down.Detail.Contains("The faux judge is down.", StringComparison.Ordinal), "a failed judge call is an error, with its run: " + down.Detail);
            Equal(RunStatus.Error, host.Store.Get(down.JudgeRunId!)!.Status, "recorded as failed");
        }),

        Case("evals.checks.judge-never-sees-the-target-name", async () =>
        {
            await using var host = CkHost.Create("ck-blind");
            var testCase = new TestCase
            {
                Id = EvalIds.New(EvalIds.Case), SuiteId = EvalIds.New(EvalIds.Suite), Name = "Ask GPT-5.4 mini its name",
                Spec = new RunSpec
                {
                    Provider = "openai", Model = "gpt-5.4-mini", SystemPrompt = "You are gpt-5.4-mini, served by OpenAI.",
                    Messages =
                    [
                        new RunMessage { Role = "user", Text = "Which model are you? Are you ChatGPT?" },
                        new RunMessage { Role = "assistant", Text = "I'm GPT 5.4 mini.", ToolCalls = [new RunToolCall("t1", "lookup", new JsonObject { ["q"] = "openai gpt-5.4-mini" })] },
                        new RunMessage { Role = "toolResult", ToolCallId = "t1", ToolName = "lookup", Text = "OpenAI's GPT-5.4-mini, a GPT model." },
                        new RunMessage { Role = "user", Text = "So, are you an OpenAI model?" },
                    ],
                },
                Checks = [new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("faux", "echo", "Score how honestly GPT-5.4 mini names itself; OpenAI wants a 5.") }],
                Origin = new TestCaseOrigin(PinSource.Suite),
            }.Normalize();
            var answer = new EvalAnswer
            {
                Text = "Yes: I am GPT-5.4 mini (gpt-5.4-mini-2026-03-17), made by OpenAI. Unlike other GPTs, gpt4 and ChatGPT, I run on openai/gpt-5.4-mini. Made with care.",
                ToolCalls = [new EvalToolCall("report", new JsonObject { ["model"] = "GPT_5_4_MINI", ["vendor"] = "openai" })],
                ResponseModel = "gpt-5.4-mini-2026-03-17",
            };
            var context = new JudgeContext { Target = new ModelIdentity("openai", "gpt-5.4-mini"), GroupId = "g-ck-blind" };
            var result = (await host.Runner.RunAsync(testCase, answer, context)).Single();
            Check(result is { Verdict: CheckVerdict.Passed, JudgeRunId: not null }, "judged: " + result);
            var asked = host.Asked.Single();
            var run = host.Store.Get(result.JudgeRunId!)!;
            var recorded = run.Messages!.ToJsonString();
            foreach (var (what, text) in new[] { ("the judge's request", asked), ("the recorded messages", recorded), ("the run's title", run.Title ?? "") })
                foreach (var name in new[] { "gpt", "openai" })
                    Check(!text.Contains(name, StringComparison.OrdinalIgnoreCase), $"{what} never says {name}: {text}");
            Check(asked.Contains("[model]", StringComparison.Ordinal) && asked.Contains("[provider]", StringComparison.Ordinal), "names become [model] and [provider]: " + asked);
            Check(asked.Contains("Made with care.", StringComparison.Ordinal) && asked.Contains("Which model are you?", StringComparison.Ordinal) &&
                recorded.Contains("names itself", StringComparison.Ordinal), "the rest is kept");
            Check(recorded.Contains("hidden as [model] and [provider]", StringComparison.Ordinal), "the judge is told names are hidden");

            var claude = JudgeRedaction.For(new ModelIdentity("anthropic", "claude-sonnet-5-5"));
            var hidden = claude.Apply("Claude Sonnet 5.5 (by Anthropic) and claude-sonnet-5-5; Claude3 and Claude's notes; anthropic.com; meta tags and Meta stay; Claudette stays.");
            Check(!hidden.Contains("claude ", StringComparison.OrdinalIgnoreCase) && !hidden.Contains("anthropic", StringComparison.OrdinalIgnoreCase) &&
                !hidden.Contains("claude3", StringComparison.OrdinalIgnoreCase) && !hidden.Contains("claude-", StringComparison.OrdinalIgnoreCase), "Claude's names hidden: " + hidden);
            Check(hidden.Contains("meta tags and Meta stay", StringComparison.Ordinal) && hidden.Contains("Claudette stays", StringComparison.Ordinal), "other words stay: " + hidden);
            var bedrock = JudgeRedaction.For(new ModelIdentity("amazon-bedrock", "us.anthropic.claude-opus-4-1-v1:0"));
            var bedrockText = bedrock.Apply("Served as us.anthropic.claude-opus-4-1-v1:0 on Amazon Bedrock by Anthropic.");
            Check(!bedrockText.Contains("anthropic", StringComparison.OrdinalIgnoreCase) && !bedrockText.Contains("claude", StringComparison.OrdinalIgnoreCase) &&
                !bedrockText.Contains("bedrock", StringComparison.OrdinalIgnoreCase) && !bedrockText.Contains("amazon", StringComparison.OrdinalIgnoreCase), "a hosted model's names: " + bedrockText);
        }),

        Case("evals.checks.same-family-warning", () =>
        {
            var claude = ModelFamilies.Compare("anthropic", "claude-opus-5", [new ModelIdentity("anthropic", "claude-sonnet-5-5"), new ModelIdentity("openai", "gpt-5.6-terra")]);
            Check(claude is { Same: true, Chip: "same family", Family: "Claude" } && claude.Message ==
                "The target anthropic/claude-sonnet-5-5 is also a Claude model. Judges tend to favour their own family, so pick a judge from another provider.", "the mockup's warning: " + claude);
            var gpt = ModelFamilies.Compare("openai", "gpt-5.5",
                [new ModelIdentity("openai", "gpt-5.6-terra"), new ModelIdentity("openai-codex", "gpt-6.1-sol"), new ModelIdentity("google", "gemini-3-pro")]);
            Check(gpt is { Same: true, Family: "GPT" } && gpt.Message ==
                "The targets openai/gpt-5.6-terra and openai-codex/gpt-6.1-sol are also GPT models. Judges tend to favour their own family, so pick a judge from another provider.",
                "the plural warning: " + gpt.Message);
            Equal("openai/gpt-5.6-terra|openai-codex/gpt-6.1-sol", string.Join("|", gpt.Targets), "the targets named");
            var gemini = ModelFamilies.Compare("google", "gemini-3-flash", [new ModelIdentity("anthropic", "claude-sonnet-5-5"), new ModelIdentity("openai", "gpt-5.6-terra")]);
            Check(gemini is { Same: false, Chip: "different family", Family: "Gemini", Message: "No target in this suite is a Gemini model." }, "different family: " + gemini);
            Equal("claude", ModelFamilies.Of("amazon-bedrock", "us.anthropic.claude-opus-4-1-v1:0")?.Id, "a hosted Claude");
            Equal("claude", ModelFamilies.Of("openrouter", "anthropic/claude-sonnet-5")?.Id, "an aggregator's Claude");
            Equal("gpt", ModelFamilies.Of("openai", "o3-mini")?.Id, "a reasoning model with no brand in its id");
            Equal("gpt", ModelFamilies.Of("azure-openai-responses", "my-deployment")?.Id, "by provider when the id says nothing");
            Equal("llama", ModelFamilies.Of("groq", "llama-4-scout")?.Id, "Llama on another host");
            Equal(null, ModelFamilies.Of("ollama", "my-finetune")?.Id, "unknown");
            Check(ModelFamilies.Compare("amazon-bedrock", "us.anthropic.claude-opus-4-1-v1:0", [new ModelIdentity("anthropic", "claude-sonnet-5-5")]).Same, "same family across providers");
            var self = ModelFamilies.Compare("ollama", "my-finetune", [new ModelIdentity("ollama", "my-finetune")]);
            Check(self is { Same: true } && self.Message.StartsWith("The judge is also a target: ollama/my-finetune", StringComparison.Ordinal), "a judge that is a target: " + self.Message);
            var unknown = ModelFamilies.Compare("ollama", "my-finetune", [new ModelIdentity("anthropic", "claude-sonnet-5-5")]);
            Check(unknown is { Same: false, Family: null } && unknown.Message.Contains("can't tell", StringComparison.Ordinal), "an unknown family: " + unknown.Message);
            var judge = RubricJudge.Family(new JudgeSpec("faux", "echo", "x"), [new ModelIdentity("faux", "echo-small")]);
            Check(judge is { Same: true, Family: "Faux" } && judge.Message.StartsWith("The target faux/echo-small is also a Faux model.", StringComparison.Ordinal), "faux: " + judge.Message);
        }),

        Case("evals.checks.judge-estimate-budget-and-refusal", async () =>
        {
            await using var host = CkHost.Create("ck-budget");
            var spec = new JudgeSpec("faux", "echo", "Score how well the regex handles pre-release and build metadata.");
            var estimate = await host.Judge.EstimateAsync(spec, 9);
            Check(estimate is { Billing: BillingKind.Metered, InputTokens: JudgeSpec.MaxInputTokens, OutputTokens: JudgeSpec.MaxOutputTokens, InputRate: 3.75, OutputRate: 15, EachMicros: 17_055, RunMicros: 153_495 },
                "2,500 input tokens at the worst input rate and 512 output tokens: " + estimate);
            Equal("Worst per judgement: 2,500 input × $3.75 + 512 output × $15.00 per million tokens = $0.0171. This case makes 9 judgements per run: $0.1535.", estimate.Text,
                "the mockup's cost line");
            Check(estimate.Line is { Judge: true, Calls: 9, EachMicros: 17_055, RunMicros: 153_495, Provider: "faux", Model: "echo" }, "the suite estimate's judge line");
            Equal("Worst per judgement: 2,500 input × $3.75 + 512 output × $15.00 per million tokens = $0.0171. This case makes 1 judgement per run: $0.0171.",
                (await host.Judge.EstimateAsync(spec, 1)).Text, "one judgement");

            // A long request and a long answer are cut to the budget: the request keeps its end, the answer its start.
            var testCase = CkCase(system: "Context: " + string.Concat(Enumerable.Repeat("background facts \"quoted\"\n", 600)), prompt: "THE QUESTION: is the regex right?",
                checks: [CkJudge("faux", "echo")]);
            var answer = CkAnswer("ANSWER START " + string.Concat(Enumerable.Repeat("détail ünïcode ✓ ", 2000)) + " ANSWER END");
            var request = host.Judge.Prepare(spec, testCase, answer, new ModelIdentity("faux", "echo-small"));
            Check(request is { PromptCut: true, AnswerCut: true } && request.InputBytes <= RubricJudge.MaxInputBytes && request.InputBytes > RubricJudge.MaxInputBytes - 400,
                $"cut to fit ({request.InputBytes} bytes)");
            var user = request.Spec.Messages.Single().Text!;
            Check(user.Contains("THE QUESTION", StringComparison.Ordinal) && user.Contains("ANSWER START", StringComparison.Ordinal) && !user.Contains("ANSWER END", StringComparison.Ordinal) &&
                user.Contains(RubricJudge.CutMarker, StringComparison.Ordinal), "the question and the answer's start are kept");
            Equal(JudgeSpec.MaxInputTokens, (int)CostEstimator.InputTokens(RubricJudge.MaxInputBytes), "the byte budget is the token budget");
            var context = new JudgeContext { Target = new ModelIdentity("faux", "echo-small"), GroupId = "g-ck-budget" };
            var big = (await host.Runner.RunAsync(testCase, answer, context)).Single();
            var reservation = (await host.Ledger.GetAsync(host.Store.Get(big.JudgeRunId!)!.SpendOp!))!;
            Check(reservation.ReservedMicros is { } reserved && reserved <= estimate.EachMicros && reserved > estimate.EachMicros!.Value * 9 / 10,
                $"the largest judgement reserves at most its estimate ({reservation.ReservedMicros} of {estimate.EachMicros})");

            // A Block cap: the judgement is refused, nothing is sent or recorded, and the check is an error.
            var before = host.Store.List(new RunQuery()).Items.Count;
            var asked = host.Asked.Count;
            await host.Ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 1, CapAction.Block));
            var refused = (await host.Runner.RunAsync(testCase, CkAnswer("short"), context)).Single();
            Check(refused is { Verdict: CheckVerdict.Error, JudgeRunId: null } && refused.Detail!.StartsWith("The Spend Guard refused the judge call: ", StringComparison.Ordinal),
                "refused: " + refused.Detail);
            Equal(before, host.Store.List(new RunQuery()).Items.Count, "nothing recorded");
            Equal(asked, host.Asked.Count, "nothing sent");
            Check(Throws<ArgumentException>(() => host.Judge.Prepare(spec with { Rubric = string.Concat(Enumerable.Repeat("✓✓✓✓ ", 400)) }, testCase, answer,
                new ModelIdentity("faux", "echo-small")), "a rubric that leaves no room").Message.Contains("does not fit", StringComparison.Ordinal), "says so");
        }),

        Case("evals.checks.api-validates-tries-and-previews", async () =>
        {
            var root = Temp("ck-api");
            try
            {
                var data = Path.Join(root, "data");
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data });
                var valid = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/validate",
                    new JsonObject { ["check"] = new JsonObject { ["kind"] = "regex", ["pattern"] = "^v\\d+", ["text"] = "dropped" } }, origin: app.Origin);
                Equal(200, valid.Status, "validated: " + valid.Text);
                Check(valid.Body?["check"]?["kind"]?.GetValue<string>() == "regex" && valid.Body?["check"]?["text"] is null && valid.Body?["check"]?["scope"]?.GetValue<string>() == "answer",
                    "normalized: " + valid.Text);
                Check(valid.Body?["label"]?.GetValue<string>() == "Regex" && valid.Body?["editorLabel"]?.GetValue<string>() == "Matches regex" &&
                    valid.Body?["summary"]?.GetValue<string>() == "Matches /^v\\d+/ (100 ms limit)", "labels and summary: " + valid.Text);
                Check(valid.Response.Headers.CacheControl?.NoStore == true, "never cached");
                var invalid = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/validate",
                    new JsonObject { ["check"] = new JsonObject { ["kind"] = "judge", ["judge"] = new JsonObject { ["provider"] = "faux", ["model"] = "nope", ["rubric"] = "x" } } }, origin: app.Origin);
                Check(invalid.Status == 400 && invalid.Body?["error"]?["code"]?.GetValue<string>() == SpendErrors.Invalid &&
                    invalid.Body?["error"]?["message"]?.GetValue<string>() == "There is no chat model faux/nope to judge with.", "refused: " + invalid.Text);
                Equal(403, (await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/validate", new JsonObject { ["check"] = new JsonObject { ["kind"] = "contains", ["text"] = "x" } },
                    origin: "http://evil.example")).Status, "no cross-origin writes");

                var reference = new JsonObject { ["text"] = "Use 1.0.0-alpha.", ["costMicros"] = 11_500, ["listPrice"] = true, ["durationMs"] = 18_200 };
                var tried = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/try",
                    new JsonObject { ["check"] = new JsonObject { ["kind"] = "contains", ["text"] = "1.0.0-alpha" }, ["reference"] = reference }, origin: app.Origin);
                Check(tried.Status == 200 && tried.Body?["subject"]?.GetValue<string>() == "reference" && tried.Body?["result"]?["verdict"]?.GetValue<string>() == "passed" &&
                    tried.Body?["result"]?["detail"]?.GetValue<string>() == "Found in the reference output.", "tried on a reference: " + tried.Text);
                var limits = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/try",
                    new JsonObject { ["check"] = new JsonObject { ["kind"] = "limits", ["maxCostMicros"] = 30_000, ["maxLatencyMs"] = 30_000 }, ["reference"] = reference.DeepClone() },
                    origin: app.Origin);
                Equal("Reference output: ≈ $0.0115 at list price, 18.2 s.", limits.Body?["result"]?["detail"]?.GetValue<string>(), "the limits line: " + limits.Text);
                var judged = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/try", new JsonObject
                {
                    ["check"] = new JsonObject { ["kind"] = "judge", ["judge"] = new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["rubric"] = "Score it." } },
                    ["reference"] = reference.DeepClone(),
                }, origin: app.Origin);
                Check(judged.Status == 200 && judged.Body?["result"] is null && judged.Body?["note"]?.GetValue<string>()?.Contains("only in suite runs", StringComparison.Ordinal) == true,
                    "a judge is never tried: " + judged.Text);
                Equal(404, (await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/try",
                    new JsonObject { ["check"] = new JsonObject { ["kind"] = "contains", ["text"] = "x" }, ["runId"] = "nope" }, origin: app.Origin)).Status, "an unknown run");

                // A suite and a case written as the contracts store them: the editor tries a check on the case's reference output.
                var suite = new Suite { Id = EvalIds.New(EvalIds.Suite), Name = "SemVer", Targets = [new SuiteTarget("faux", "echo-small"), new SuiteTarget("openai", "gpt-5.4")], K = 3 };
                var testCase = CkCase(checks: [CkContains("x")]) with
                {
                    SuiteId = suite.Id,
                    Reference = new TestCaseReference { Text = "Version v12 is out.", CostMicros = 100, DurationMs = 900 },
                };
                CkInsert(Path.Join(data, "webagent.db"), suite, testCase);
                var onCase = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/try",
                    new JsonObject { ["check"] = new JsonObject { ["kind"] = "regex", ["pattern"] = "v\\d" }, ["caseId"] = testCase.Id }, origin: app.Origin);
                Check(onCase.Status == 200 && onCase.Body?["result"]?["verdict"]?.GetValue<string>() == "passed" &&
                    onCase.Body?["result"]?["detail"]?.GetValue<string>()?.StartsWith("Matches the reference output in ", StringComparison.Ordinal) == true, "tried on a stored case: " + onCase.Text);

                var preview = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/judge-preview",
                    new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["caseId"] = testCase.Id }, origin: app.Origin);
                Check(preview.Status == 200 && preview.Body?["family"]?["same"]?.GetValue<bool>() == true && preview.Body?["family"]?["chip"]?.GetValue<string>() == "same family" &&
                    preview.Body?["family"]?["message"]?.GetValue<string>()?.StartsWith("The target faux/echo-small is also a Faux model.", StringComparison.Ordinal) == true,
                    "the same-family warning against the suite's targets: " + preview.Text);
                Check(preview.Body?["estimate"]?["judgements"]?.GetValue<int>() == 6 && preview.Body?["estimate"]?["billing"]?.GetValue<string>() == "free" &&
                    preview.Body?["estimate"]?["text"]?.GetValue<string>() == "faux/echo is free: judgements cost nothing.", "two targets × k 3, free offline: " + preview.Text);
                var other = await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/judge-preview", new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo",
                    ["targets"] = new JsonArray(new JsonObject { ["provider"] = "openai", ["model"] = "gpt-5.4" }), ["judgements"] = 4,
                }, origin: app.Origin);
                Check(other.Status == 200 && other.Body?["family"]?["chip"]?.GetValue<string>() == "different family" && other.Body?["estimate"]?["judgements"]?.GetValue<int>() == 4,
                    "different family, judgements as given: " + other.Text);
                Equal(404, (await app.SendAsync(HttpMethod.Post, CheckEndpoints.Prefix + "/judge-preview", new JsonObject { ["provider"] = "faux", ["model"] = "nope" },
                    origin: app.Origin)).Status, "an unknown judge model");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),
    ];

    private static EvalAnswer CkAnswer(string text) => new() { Text = text };

    private static CheckResult CkRun(EvalCheck check, EvalAnswer answer) => CheckRunner.Evaluate(check with { Id = check.Id ?? "k1" }, answer);

    private static EvalCheck CkContains(string text) => new() { Kind = CheckKind.Contains, Text = text };

    private static EvalCheck CkRegex(string pattern) => new() { Kind = CheckKind.Regex, Pattern = pattern };

    private static EvalCheck CkJudge(string provider, string model) =>
        new() { Kind = CheckKind.Judge, Judge = new JudgeSpec(provider, model, "Score how well the answer explains its regex.") };

    private static JsonObject CkSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["title"] = new JsonObject { ["type"] = "string", ["maxLength"] = 100 },
            ["priority"] = new JsonObject { ["type"] = "string", ["maxLength"] = 10, ["enum"] = new JsonArray("low", "high") },
            ["count"] = new JsonObject { ["type"] = "integer" },
        },
        ["required"] = new JsonArray("title", "priority"),
    };

    private static TestCase CkCase(string? system = null, string prompt = "Write a SemVer 2.0.0 regex.", IReadOnlyList<EvalCheck>? checks = null) => new TestCase
    {
        Id = EvalIds.New(EvalIds.Case),
        SuiteId = EvalIds.New(EvalIds.Suite),
        Name = "SemVer with pre-release",
        Spec = new RunSpec { Provider = "faux", Model = "echo", SystemPrompt = system, Messages = [new RunMessage { Role = "user", Text = prompt }] },
        Checks = checks ?? [],
        Origin = new TestCaseOrigin(PinSource.Suite),
    }.Normalize();

    /// <summary>Writes <paramref name="suite"/> and <paramref name="testCase"/> to webagent.db as the contracts store them (data JSON beside
    /// the columns).</summary>
    private static void CkInsert(string database, Suite suite, TestCase testCase)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 5000;
            INSERT INTO eval_suites (id, name, schedule_enabled, cap_micros, data, created_at, updated_at) VALUES ($suite, $name, 0, NULL, $suiteData, 0, 0);
            INSERT INTO eval_cases (id, suite_id, name, position, origin, data, created_at, updated_at) VALUES ($case, $suite, $caseName, 0, 'suite', $caseData, 0, 0);
            """;
        command.Parameters.AddWithValue("$suite", suite.Id);
        command.Parameters.AddWithValue("$name", suite.Name);
        command.Parameters.AddWithValue("$suiteData", EvalJson.Serialize(suite));
        command.Parameters.AddWithValue("$case", testCase.Id);
        command.Parameters.AddWithValue("$caseName", testCase.Name);
        command.Parameters.AddWithValue("$caseData", EvalJson.Serialize(testCase));
        command.ExecuteNonQuery();
    }

    /// <summary>The Checks unit over the priced faux models (--faux --faux-metered) in a new temporary folder; its judge answers with
    /// <see cref="Replies"/> in order (else a confident 4) and records what it was asked.</summary>
    internal sealed class CkHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string root;

        private CkHost(ServiceProvider provider, string root, WebAgentOptions options, ConcurrentQueue<string> asked, ConcurrentQueue<string> replies,
            ConcurrentQueue<string> failures)
        {
            this.provider = provider;
            this.root = root;
            Options = options;
            Asked = asked;
            Replies = replies;
            Failures = failures;
        }

        public WebAgentOptions Options { get; }

        /// <summary>What the judge model was asked (each request's newest user text), in order.</summary>
        public ConcurrentQueue<string> Asked { get; }

        /// <summary>What the judge model answers next, in order.</summary>
        public ConcurrentQueue<string> Replies { get; }

        /// <summary>Errors the judge model fails with next, in order (before it answers anything).</summary>
        public ConcurrentQueue<string> Failures { get; }

        public IServiceProvider Services => provider;

        public CheckRunner Runner => provider.GetRequiredService<CheckRunner>();

        public RubricJudge Judge => provider.GetRequiredService<RubricJudge>();

        public RunStore Store => provider.GetRequiredService<RunStore>();

        public ISpendLedger Ledger => provider.GetRequiredService<ISpendLedger>();

        public static CkHost Create(string name)
        {
            var root = Temp(name);
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true, FauxMetered = true };
            options.EnsureDirectories();
            var asked = new ConcurrentQueue<string>();
            var replies = new ConcurrentQueue<string>();
            var failures = new ConcurrentQueue<string>();
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(new FauxDurableModels([FauxDurableModels.Echo, FauxDurableModels.EchoSmall, FauxModelCatalog.EchoThinking])
            {
                RespondAsync = (chat, _) =>
                {
                    if (failures.TryDequeue(out var failure)) throw new InvalidOperationException(failure);
                    asked.Enqueue(chat.Text);
                    return Task.FromResult<string?>(replies.TryDequeue(out var reply) ? reply : "{\"label\": \"4\", \"confidence\": 0.9, \"rationale\": \"Fine.\"}");
                },
            });
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            EvalsModule.AddEvalsArea(services, options);
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new CkHost(provider, root, options, asked, replies, failures);
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(root);
        }
    }
}
