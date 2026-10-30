using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Evals;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Runs;
using EvalCheck = PiSharp.WebAgent.Evals.Check;
using static System.FormattableString;

// Decision 0011 (run W5, E0): the evals contracts every W5 unit codes against. Ids are prefixed hex; the only randomness is SplitMix64 from a
// stored seed (the same numbers as a reference implementation, and a uniform shuffle by chi-square); tags and model identities normalize
// (the identity never holds the account); a battle refuses duplicates and shows no names before the vote, and a vote splits into left-first
// pairs; a test case keeps the request without account, endpoint or secret; checks keep only their kind's fields and refuse what could
// never run; suites, targets and k; schedule slots (labels, collapse, EnabledAt, a custom DST zone's gap and repeat); results, cells and
// target totals; the plan's drift table and confirmation; price changes; wire names and JSON round trips; the evals settings section; and
// webagent.db migrations 2000-2004 with their constraints and cascades, applied by the module and by the app at start.
internal static partial class Program
{
    private static readonly DateTimeOffset EvalNow = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    internal static IEnumerable<(string, Func<Task>)> EvalContractCases() =>
    [
        Case("evals.contract.ids-are-prefixed-hex", () =>
        {
            foreach (var prefix in EvalIds.Prefixes)
            {
                var id = EvalIds.New(prefix);
                Equal(34, id.Length, "prefix and 32 hex digits");
                Check(EvalIds.IsValid(prefix, id), "a new id is valid: " + id);
                Equal(id, EvalIds.Require(prefix, "  " + id + " ", "x"), "Require trims");
                Check(!EvalIds.IsValid(prefix, id.ToUpperInvariant()), "upper-case hex is not an id");
            }
            Check(!EvalIds.IsValid(EvalIds.Suite, EvalIds.New(EvalIds.Case)), "another kind's id is refused");
            Check(!EvalIds.IsValid("zz", "zz" + new string('0', 32)), "an unknown prefix is refused");
            Throws<ArgumentException>(() => EvalIds.New("zz"), "no ids with an unknown prefix");
            Equal("\"nope\" is not a suite id.", Throws<ArgumentException>(() => EvalIds.Require(EvalIds.Suite, "nope", "suite"), "a bad id").Message, "the message");
            Check(EvalIds.New(EvalIds.Battle) != EvalIds.New(EvalIds.Battle), "ids differ");
        }),

        Case("evals.contract.random-is-splitmix64-and-reproducible", () =>
        {
            // Reference values: SplitMix64 (Steele, Lea and Flood), computed independently.
            var zero = new EvalRandom(0);
            Equal(0xE220A8397B1DCDAFUL, zero.NextUInt64(), "seed 0, first");
            Equal(0x6E789E6AA1B965F4UL, zero.NextUInt64(), "seed 0, second");
            Equal(0x06C45D188009454FUL, zero.NextUInt64(), "seed 0, third");
            var answer = new EvalRandom(42);
            Equal(0xBDD732262FEB6E95UL, answer.NextUInt64(), "seed 42, first");
            Equal(0x28EFE333B266F103UL, answer.NextUInt64(), "seed 42, second");
            Equal(0.7415648787718233, new EvalRandom(42).NextDouble(), "a double from the top 53 bits");
            Equal(0x24B94FACEFB6559FUL, new EvalRandom(EvalRandom.MaxSeed).NextUInt64(), "the largest seed");
            var draws = new EvalRandom(12345);
            Equal("4,7,5,0,3,6,6,8", string.Join(",", Enumerable.Range(0, 8).Select(_ => draws.Next(10))), "Next(10) from seed 12345");
            Equal("2,1,3,0", string.Join(",", new EvalRandom(12345).Permutation(4)), "a Fisher–Yates permutation from seed 12345");
            Equal(string.Join(",", new EvalRandom(777).Permutation(16)), string.Join(",", new EvalRandom(777).Permutation(16)), "the same seed, the same shuffle");
            Equal(777L, new EvalRandom(777).Seed, "the seed is kept");
            for (var index = 0; index < 200; index++)
            {
                var seed = EvalRandom.NewSeed();
                Check(seed is >= 0 and <= EvalRandom.MaxSeed, "a new seed is 53-bit: " + seed);
                Equal(seed, (long)(double)seed, "it survives a JSON number exactly");
            }
            var bounded = new EvalRandom(9);
            for (var index = 0; index < 1000; index++) Check(bounded.Next(3) is >= 0 and < 3, "Next stays in range");
            Throws<ArgumentOutOfRangeException>(() => new EvalRandom(1).Next(0), "Next(0)");
            Equal(0, new EvalRandom(1).Permutation(0).Length, "an empty permutation");
        }),

        Case("evals.contract.shuffle-is-uniform", () =>
        {
            // One seed per battle, as the Arena stores them: 24,000 battles of 4 contestants over the 24 orders. Fixed seeds, so the statistic
            // is the same on every run; 49.73 is chi-square's 0.999 quantile for 23 degrees of freedom.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var columnOf = new int[4, 4];
            const int Battles = 24_000;
            for (var seed = 0; seed < Battles; seed++)
            {
                var order = new EvalRandom(seed * 7919L + 1).Permutation(4);
                var key = string.Join("", order);
                counts[key] = counts.GetValueOrDefault(key) + 1;
                for (var column = 0; column < 4; column++) columnOf[order[column], column]++;
            }
            Equal(24, counts.Count, "every order happens");
            var expected = Battles / 24.0;
            var chiSquare = counts.Values.Sum(count => (count - expected) * (count - expected) / expected);
            Check(chiSquare < 49.73, $"orders are uniform: chi-square {chiSquare:F2} with 23 degrees of freedom");
            for (var contestant = 0; contestant < 4; contestant++)
            {
                var perColumn = Battles / 4.0;
                var statistic = Enumerable.Range(0, 4).Sum(column => (columnOf[contestant, column] - perColumn) * (columnOf[contestant, column] - perColumn) / perColumn);
                Check(statistic < 16.27, $"contestant {contestant} lands in each column equally: chi-square {statistic:F2} with 3 degrees of freedom");
            }
        }),

        Case("evals.contract.tags-normalize", () =>
        {
            Equal("coding", EvalTags.Normalize("  Coding "), "trimmed, lower case");
            Equal("c#", EvalTags.Normalize("C#"), "c#");
            Equal("c++", EvalTags.Normalize("C++"), "c++");
            Equal("node.js", EvalTags.Normalize("Node.js"), "node.js");
            Equal("long form writing", EvalTags.Normalize("Long   form\twriting"), "runs of spaces fold to one");
            Equal("café", EvalTags.Normalize("Café"), "NFC");
            Throws<ArgumentException>(() => EvalTags.Normalize(" "), "an empty tag");
            Throws<ArgumentException>(() => EvalTags.Normalize(new string('a', EvalTags.MaxLength + 1)), "too long");
            Throws<ArgumentException>(() => EvalTags.Normalize("-x"), "a leading dash");
            Throws<ArgumentException>(() => EvalTags.Normalize("a/b"), "a slash");
            Throws<ArgumentException>(() => EvalTags.Normalize("<b>"), "markup");
            Check(EvalTags.IsValid(EvalTags.Agent) && !EvalTags.IsValid("a/b"), "IsValid");
            Equal("a b", EvalTags.Normalize("a\nb"), "a line break is white space");
            Equal("coding,regex", string.Join(",", EvalTags.ParseList("coding, Regex, ,coding")), "the modal's list, deduplicated");
            Throws<ArgumentException>(() => EvalTags.NormalizeList(Enumerable.Range(0, EvalTags.MaxPerCase + 1).Select(index => "t" + index)), "too many tags");
        }),

        Case("evals.contract.identity-is-provider-model-settings-not-account", () =>
        {
            var plain = new ModelIdentity(" openai ", " gpt-5.6-terra ").Normalize();
            Equal("openai/gpt-5.6-terra", plain.Key, "provider/model");
            Equal("default settings", plain.SettingsLabel, "the leaderboard's label");
            var tuned = new ModelIdentity("openai-codex", "gpt-6.1-sol", "medium", 0.7049, "priority").Normalize();
            Equal("openai-codex/gpt-6.1-sol;thinking=medium;temperature=0.7;tier=priority", tuned.Key, "settings in a fixed order, temperature to 2 decimals");
            Equal("thinking medium · temperature 0.7 · service tier priority", tuned.SettingsLabel, "the label");
            Equal(new ModelIdentity("p", "m").Normalize().Key, new ModelIdentity("p", "m", "off").Normalize().Key, "thinking off is no thinking");
            Check(new ModelIdentity("p", "m", Temperature: 0.701).SameAs(new ModelIdentity("p", "m", Temperature: 0.7)), "0.701 rounds to 0.7");
            Check(!new ModelIdentity("p", "m", "high").SameAs(new ModelIdentity("p", "m", "low")), "thinking levels differ");
            var spec = new RunSpec { Provider = "faux", Model = "echo", Account = "acct-1", Thinking = "high", Messages = [new RunMessage { Text = "hi" }] };
            Equal(ModelIdentity.Of(spec with { Account = "acct-2" }).Key, ModelIdentity.Of(spec).Key, "the account is never part of it");
            Equal("faux/echo;thinking=high", ModelIdentity.Of(spec).Key, "Of(RunSpec)");
            Throws<ArgumentException>(() => new ModelIdentity("p", "m", "turbo").Normalize(), "an unknown thinking level");
            Throws<ArgumentException>(() => new ModelIdentity("p", "m", Temperature: 2.5).Normalize(), "temperature above 2");
            Throws<ArgumentException>(() => new ModelIdentity("p", "m", ServiceTier: "gold").Normalize(), "an unknown tier");
            Throws<ArgumentException>(() => new ModelIdentity("a/b", "m").Normalize(), "a slash in the provider");
            Throws<ArgumentException>(() => new ModelIdentity("", "m").Normalize(), "no provider");
            var json = EvalJson.Serialize(tuned);
            Check(!json.Contains("key", StringComparison.Ordinal) && !json.Contains("settingsLabel", StringComparison.Ordinal), "computed names stay out of JSON: " + json);
        }),

        Case("evals.contract.battle-normalizes-and-hides-names-before-the-vote", () =>
        {
            var battle = Battle(3).Normalize();
            Equal("A,B,C", string.Join(",", battle.Letters), "columns A to C");
            Equal("faux/echo-b", battle.At("b")!.Identity.Name, "At takes a letter");
            Check(battle.At("D") is null, "no column D");
            Equal(battle.Contestants.Select(contestant => contestant.Position).ToList().Count, 3, "three contestants");
            var shuffled = Battle(3) with { Contestants = [.. Battle(3).Contestants.Reverse()] };
            Equal("0,1,2", string.Join(",", shuffled.Normalize().Contestants.Select(contestant => contestant.Position)), "sorted by column");
            Throws<ArgumentException>(() => (Battle(3) with { Contestants = [Contestant(0, "x"), Contestant(1, "x"), Contestant(2, "y")] }).Normalize(), "the same model twice");
            (Battle(3) with { Contestants = [Contestant(0, "x"), Contestant(1, "x", "high"), Contestant(2, "y")] }).Normalize();
            Throws<ArgumentException>(() => (Battle(3) with { Contestants = [Contestant(0, "x"), Contestant(2, "y"), Contestant(3, "z")] }).Normalize(), "a gap in the columns");
            Throws<ArgumentException>(() => (Battle(3) with { Contestants = [Contestant(0, "x")] }).Normalize(), "one contestant");
            Throws<ArgumentException>(() => Battle(5).Normalize(), "five contestants");
            Throws<ArgumentException>(() => (Battle(2) with { Seed = EvalRandom.MaxSeed + 1 }).Normalize(), "a seed above 53 bits");
            Throws<ArgumentException>(() => (Battle(2) with { Id = "ab123" }).Normalize(), "a bad id");
            Throws<ArgumentException>(() => (Battle(2) with { Contestants = [Contestant(0, "x") with { RunId = "r1" }, Contestant(1, "y") with { RunId = "r1" }] }).Normalize(), "two contestants share a run");
            Equal("coding", (Battle(2) with { Tag = " Coding " }).Normalize().Tag, "the tag is normalized");
            var blind = EvalJson.Serialize((battle with { Tag = "coding" }).ToBlind());
            foreach (var contestant in battle.Contestants)
                Check(!blind.Contains(contestant.Identity.Model, StringComparison.Ordinal), "no model name before the vote: " + blind);
            Check(!blind.Contains("faux", StringComparison.Ordinal) && !blind.Contains("seed", StringComparison.Ordinal), "nor the provider or the seed: " + blind);
            Check(blind.Contains("\"letters\":[\"A\",\"B\",\"C\"]", StringComparison.Ordinal), "the letters: " + blind);
        }),

        Case("evals.contract.votes-split-into-left-first-pairs", () =>
        {
            var battle = Battle(3).Normalize();
            string Pairs(ArenaVote vote) => string.Join(" ", battle.Comparisons(vote).Select(pair =>
                Invariant($"{ArenaLetters.Of(pair.LeftPosition)}{ArenaLetters.Of(pair.RightPosition)}={pair.LeftScore:0.#}")));
            Equal("AB=0 BC=1", Pairs(Vote(battle, VoteKind.Pick, pick: "b")), "a pick beats each other column; the others are not compared");
            Equal("AB=0.5 AC=0.5 BC=0.5", Pairs(Vote(battle, VoteKind.Tie)), "a tie");
            Equal("AB=0.5 AC=0.5 BC=0.5", Pairs(Vote(battle, VoteKind.AllBad)), "all bad is a tie");
            Equal("AB=1 AC=0 BC=0", Pairs(Vote(battle, VoteKind.Rank, ranking: ["C", "a", "B"])), "a ranking: C, then A, then B");
            var pair = battle.Comparisons(Vote(battle, VoteKind.Pick, pick: "A"))[0];
            Equal("faux/echo-a", pair.Left.Name, "the left identity is column A's");
            Equal("coding", pair.Tag, "with the vote's tag");
            Equal(3, pair.Contestants, "and the battle's size");
            Equal("B", Vote(battle, VoteKind.Pick, pick: " b ").Normalize(3).Pick, "letters are upper case");
            Equal("C", Vote(battle, VoteKind.Rank, ranking: ["c", "a", "b"]).Normalize(3).Best, "Best is the ranking's first");
            Check(Vote(battle, VoteKind.Tie, pick: "A").Normalize(3).Pick is null, "a tie clears the pick");
            Throws<ArgumentException>(() => Vote(battle, VoteKind.Pick, pick: "D").Normalize(3), "column D of three");
            Throws<ArgumentException>(() => Vote(battle, VoteKind.Pick).Normalize(3), "a pick without a letter");
            Throws<ArgumentException>(() => Vote(battle, VoteKind.Rank, ranking: ["A", "B"]).Normalize(2), "ranking two answers");
            Throws<ArgumentException>(() => Vote(battle, VoteKind.Rank, ranking: ["A", "B"]).Normalize(3), "a ranking that leaves one out");
            Throws<ArgumentException>(() => Vote(battle, VoteKind.Rank, ranking: ["A", "A", "B"]).Normalize(3), "a ranking that repeats one");
            Throws<ArgumentException>(() => (Vote(battle, VoteKind.Tie) with { Tag = "" }).Normalize(3), "a vote needs a tag");
            Throws<ArgumentException>(() => battle.Comparisons(Vote(battle, VoteKind.Tie) with { BattleId = EvalIds.New(EvalIds.Battle) }), "another battle's vote");
            Throws<ArgumentException>(() => battle.Comparisons(Vote(battle, VoteKind.Tie) with { Mode = ArenaMode.Agent }), "another mode's vote");
        }),

        Case("evals.contract.test-case-keeps-the-request-without-account-or-endpoint", () =>
        {
            var spec = new RunSpec
            {
                Provider = "faux",
                Model = "echo",
                Account = "acct-1",
                Source = "chat",
                GroupId = "g1",
                ParentId = "p1",
                Title = "a title",
                BaseUrl = "https://example.invalid/v1",
                AzureResourceName = "res",
                SystemPrompt = "Be brief.",
                Temperature = 0.2,
                Messages = [new RunMessage { Text = "Write a SemVer regex." }],
            };
            var testCase = new TestCase
            {
                Id = EvalIds.New(EvalIds.Case),
                SuiteId = EvalIds.New(EvalIds.Suite),
                Name = "  SemVer  ",
                Tags = ["Coding", "regex", "coding"],
                Spec = spec,
                Checks = [RegexCheck("^a"), RegexCheck("^b") with { Id = "k1" }, RegexCheck("^c")],
                Origin = new TestCaseOrigin(PinSource.Chat, ChatId: " c1 ", EntryId: "e1"),
                Reference = new TestCaseReference { Text = "^\\d+$", Provider = " faux ", Model = "echo" },
            }.Normalize();
            Equal("SemVer", testCase.Name, "the name is trimmed");
            Equal("coding,regex", string.Join(",", testCase.Tags), "tags normalized");
            Check(testCase.Spec.Account is null && testCase.Spec.Source is null && testCase.Spec.GroupId is null && testCase.Spec.ParentId is null &&
                testCase.Spec.Title is null && testCase.Spec.BaseUrl is null && testCase.Spec.AzureResourceName is null, "no account, source, grouping or endpoint");
            Equal("Be brief.|0.2|Write a SemVer regex.", Invariant($"{testCase.Spec.SystemPrompt}|{testCase.Spec.Temperature}|{testCase.Spec.Messages[0].Text}"), "the request itself stays");
            Equal("k2,k1,k3", string.Join(",", testCase.Checks.Select(check => check.Id)), "missing check ids are filled in around the given ones");
            Equal("c1", testCase.Origin.ChatId, "the origin is trimmed");
            Equal("faux", testCase.Reference!.Provider, "the reference is trimmed");
            var json = EvalJson.Serialize(testCase);
            Check(!json.Contains("acct-1", StringComparison.Ordinal) && !json.Contains("example.invalid", StringComparison.Ordinal), "nothing of the account in the stored case: " + json);
            Throws<ArgumentException>(() => (testCase with { Spec = spec with { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer x" } } }).Normalize(), "a credential header");
            Throws<ArgumentException>(() => (testCase with { Checks = [RegexCheck("^a") with { Id = "k1" }, RegexCheck("^b") with { Id = "k1" }] }).Normalize(), "two checks with one id");
            Throws<ArgumentException>(() => (testCase with { Checks = [.. Enumerable.Range(0, TestCase.MaxChecks + 1).Select(_ => RegexCheck("x"))] }).Normalize(), "too many checks");
            Throws<ArgumentException>(() => (testCase with { Name = new string('n', TestCase.MaxName + 1) }).Normalize(), "a long name");
            Throws<ArgumentException>(() => (testCase with { SuiteId = "su1" }).Normalize(), "a bad suite id");
            Throws<ArgumentException>(() => (testCase with { Origin = new TestCaseOrigin(PinSource.Compare, BattleId: "ab1") }).Normalize(), "a bad battle id");

            var output = new JsonObject
            {
                ["role"] = "assistant",
                ["provider"] = "faux",
                ["model"] = "echo",
                ["responseModel"] = "echo-2026-10-14",
                ["stopReason"] = "toolUse",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "thinking", ["thinking"] = "hmm" },
                    new JsonObject { ["type"] = "text", ["text"] = "Calling " },
                    new JsonObject { ["type"] = "text", ["text"] = "the tool." },
                    new JsonObject { ["type"] = "toolCall", ["id"] = "call-1", ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = "a.txt" } }),
            };
            var run = new RunRecord
            {
                Id = "r1", Op = "op1", Source = "drift", Status = RunStatus.Completed, Provider = "faux", Model = "echo", Output = output,
                OutputText = "ignored", CostMicros = 1234, Billing = "subscription", DurationMs = 900, TtftMs = 120, Usage = new RunUsage(10, 20, 0, 0, 30),
                Created = EvalNow,
            };
            var answer = EvalAnswer.FromRun(run);
            Equal("Calling the tool.", answer.Text, "text blocks joined, thinking left out");
            Equal("read:a.txt", string.Join(",", answer.ToolCalls.Select(call => call.Name + ":" + call.Arguments?["path"])), "the tool call");
            Equal("echo-2026-10-14", answer.ResponseModel, "responseModel before model");
            Equal("toolUse|1234|True|900|20", Invariant($"{answer.StopReason}|{answer.CostMicros}|{answer.ListPrice}|{answer.DurationMs}|{answer.OutputTokens}"), "the numbers");
            Check(answer.Error is null, "a completed run has no error");
            Equal("echo", EvalAnswer.FromOutput(new JsonObject { ["model"] = "echo" }).ResponseModel, "model when there is no responseModel");
            Equal("boom", EvalAnswer.FromRun(run with { Status = RunStatus.Error, Error = "boom" }).Error, "a failed run's error");
            Equal(RunStatus.Interrupted, EvalAnswer.FromRun(run with { Status = RunStatus.Interrupted }).Error, "an interrupted run");
            Equal("fallback", EvalAnswer.FromOutput(null, "fallback").Text, "the text when there is no message");
            var reference = TestCaseReference.FromRun(run).Normalize();
            Equal("Calling the tool.|echo-2026-10-14|r1|True", $"{reference.Text}|{reference.ResponseModel}|{reference.RunId}|{reference.ListPrice}", "a reference from a run");
            Equal("read", reference.ToAnswer().ToolCalls.Single().Name, "and back to an answer");
            Throws<ArgumentException>(() => (reference with { Output = new JsonArray() }).Normalize(), "a reference message is an object");
        }),

        Case("evals.contract.checks-keep-their-kind-and-refuse-what-cannot-run", () =>
        {
            var contains = new EvalCheck { Kind = CheckKind.Contains, Text = "major", Pattern = "dropped", Tool = "dropped" }.Normalize();
            Equal("major|True|False|Answer", $"{contains.Text}|{contains.MatchCase}|{contains.Negate}|{contains.Scope}", "contains defaults");
            Check(contains.Pattern is null && contains.Tool is null, "other kinds' fields are dropped");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Contains }.Normalize(), "contains without text");
            var regex = new EvalCheck { Kind = CheckKind.Regex, Pattern = "^\\d+$", Scope = CheckScope.FirstCodeBlock, Negate = true }.Normalize();
            Equal("FirstCodeBlock|True", $"{regex.Scope}|{regex.Negate}", "regex keeps scope and negate");
            Equal(CheckLimits.RegexTimeout, EvalCheck.Compile("a+", "x").MatchTimeout, "regexes run with the 100 ms timeout");
            Equal(100, (int)CheckLimits.RegexTimeout.TotalMilliseconds, "100 ms");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Regex, Pattern = "(" }.Normalize(), "a regex that does not compile");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Regex, Pattern = new string('a', CheckLimits.MaxPattern + 1) }.Normalize(), "a long pattern");
            var schema = JsonNode.Parse("""{"type":"object","properties":{"version":{"type":"string","maxLength":40}},"required":["version"]}""")!.AsObject();
            Equal("version", new EvalCheck { Kind = CheckKind.JsonSchema, Schema = schema }.Normalize().Schema!["required"]![0]!.GetValue<string>(), "a schema in the subset");
            var outside = Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.JsonSchema, Schema = JsonNode.Parse("""{"type":"object","properties":{"a":{"type":"string","maxLength":10,"oneOf":[]}}}""")!.AsObject() }.Normalize(), "oneOf");
            Check(outside.Message.Contains("supported subset", StringComparison.Ordinal), "says why: " + outside.Message);
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.JsonSchema, Schema = JsonNode.Parse("""{"type":"array"}""")!.AsObject() }.Normalize(), "a root that is not an object");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.JsonSchema }.Normalize(), "no schema");
            var unbounded = Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.JsonSchema, Schema = JsonNode.Parse("""{"type":"object","properties":{"s":{"type":"string"}}}""")!.AsObject() }.Normalize(), "a string without maxLength");
            Check(unbounded.Message.Contains("maxLength", StringComparison.Ordinal), "the subset wants a maxLength on strings: " + unbounded.Message);
            var tool = new Check
            {
                Kind = CheckKind.Tool,
                Tool = " read ",
                Arguments = [new ToolArgumentMatch(" path ", ArgumentRule.Regex, "\\.txt$"), new ToolArgumentMatch("mode", ArgumentRule.Present, "dropped")],
            }.Normalize();
            Equal("read|AtLeastOnce|path:Regex,mode:Present:", $"{tool.Tool}|{tool.Calls}|{string.Join(",", tool.Arguments!.Select(argument => $"{argument.Name}:{argument.Rule}{(argument.Rule == ArgumentRule.Present ? ":" + argument.Value : "")}"))}", "tool defaults");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Tool, Tool = "read", Calls = ToolCallCount.Never, Arguments = [new ToolArgumentMatch("a", ArgumentRule.Present)] }.Normalize(), "never called, yet with arguments");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Tool, Tool = "read", Arguments = [new ToolArgumentMatch("a", ArgumentRule.Regex, "(")] }.Normalize(), "an argument regex that does not compile");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Tool, Tool = "read", Arguments = [new ToolArgumentMatch("a", ArgumentRule.EqualsValue)] }.Normalize(), "equals without a value");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Tool, Tool = "read me" }.Normalize(), "a tool name with a space");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Limits }.Normalize(), "limits without a limit");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Limits, MaxLatencyMs = 0 }.Normalize(), "a zero latency");
            Equal(5000L, new EvalCheck { Kind = CheckKind.Limits, MaxCostMicros = 5000 }.Normalize().MaxCostMicros, "a max cost alone");
            var judge = new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec(" anthropic ", "claude-haiku", " Is it correct? ") }.Normalize();
            Equal("anthropic|Is it correct?|4", $"{judge.Judge!.Provider}|{judge.Judge.Rubric}|{judge.Judge.PassAt}", "a judge");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("p", "m", " ") }.Normalize(), "a judge without a rubric");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("p", "m", new string('r', PiSharp.Durable.Hosting.JudgmentPolicy.MaxInstructions + 1)) }.Normalize(), "a long rubric");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("p", "m", "r", PassAt: 6) }.Normalize(), "a pass mark above 5");
            Throws<ArgumentException>(() => new EvalCheck { Kind = CheckKind.Judge }.Normalize(), "a judge check without its judge");
            Throws<ArgumentException>(() => (RegexCheck("a") with { Id = "bad id" }).Normalize(), "a check id with a space");
            Throws<ArgumentException>(() => new EvalCheck { Kind = (CheckKind)99 }.Normalize(), "an unknown kind");
            CheckResult Result(CheckVerdict verdict) => new("k", CheckKind.Regex, verdict);
            Equal(CheckVerdict.Failed, CheckResult.Combine([Result(CheckVerdict.Review), Result(CheckVerdict.Error), Result(CheckVerdict.Failed)]), "failed first");
            Equal(CheckVerdict.Error, CheckResult.Combine([Result(CheckVerdict.Passed), Result(CheckVerdict.Error), Result(CheckVerdict.Review)]), "then error");
            Equal(CheckVerdict.Review, CheckResult.Combine([Result(CheckVerdict.Passed), Result(CheckVerdict.Review)]), "then review");
            Equal(CheckVerdict.Passed, CheckResult.Combine([]), "no checks pass");
            Equal("Matches regex|JSON schema", $"{EvalCheck.EditorLabel(CheckKind.Regex)}|{EvalCheck.KindLabel(CheckKind.JsonSchema)}", "labels");
        }),

        Case("evals.contract.suites-targets-and-runs", () =>
        {
            var target = new SuiteTarget(" openai ", "gpt-5.6-terra", " acct-1 ", "off", 0.7, "priority");
            Equal(13, target.Id.Length, "t and 12 hex digits");
            Equal(target.Id, new SuiteTarget("openai", "gpt-5.6-terra", "acct-1", null, 0.70001, "priority").Id, "the same target, the same id");
            Check(target.Id != (target with { Account = "acct-2" }).Id, "another account is another target");
            Equal(target.Identity.Key, (target with { Account = "acct-2" }).Identity.Key, "but the same identity");
            var normalized = target.Normalize();
            Equal("openai|acct-1|", $"{normalized.Provider}|{normalized.Account}|{normalized.Thinking}", "trimmed, thinking off as none");
            var applied = normalized.Apply(new RunSpec { Provider = "faux", Model = "echo", Thinking = "high", Temperature = 0.1, Source = "playground", Messages = [new RunMessage { Text = "q" }] });
            Equal("openai|gpt-5.6-terra|acct-1||0.7|priority|drift|q", Invariant($"{applied.Provider}|{applied.Model}|{applied.Account}|{applied.Thinking}|{applied.Temperature}|{applied.ServiceTier}|{applied.Source}|{applied.Messages[0].Text}"),
                "the target's settings replace the case's, source drift");

            var suite = NewSuite([new SuiteTarget("faux", "echo"), new SuiteTarget("faux", "echo-small"), new SuiteTarget("faux", "echo", Thinking: "high")]).Normalize();
            Equal(Suite.DefaultK, suite.K, "k defaults to 3");
            Equal(108, suite.AnswersPerRun(12), "12 cases × 3 targets × k 3");
            Equal("Add a test case first.", suite.RunProblem(0), "no cases");
            Equal("Add a target first.", (suite with { Targets = [] }).RunProblem(3), "no targets");
            Check(suite.RunProblem(1) is null && suite.Target(suite.Targets[1].Id) == suite.Targets[1], "runnable; Target finds by id");
            NewSuite([new SuiteTarget("faux", "echo")]).Normalize();
            Throws<ArgumentException>(() => NewSuite([.. Enumerable.Range(0, Suite.MaxTargets + 1).Select(index => new SuiteTarget("faux", "m" + index))]).Normalize(), "seven targets");
            Throws<ArgumentException>(() => NewSuite([new SuiteTarget("faux", "echo"), new SuiteTarget(" faux", "echo ")]).Normalize(), "the same target twice");
            Throws<ArgumentException>(() => (suite with { K = 0 }).Normalize(), "k 0");
            Throws<ArgumentException>(() => (suite with { K = Suite.MaxK + 1 }).Normalize(), "k 11");
            Throws<ArgumentException>(() => (suite with { Name = " " }).Normalize(), "no name");
            Throws<ArgumentException>(() => (suite with { CapMicros = 0 }).Normalize(), "a zero cap");
            var daily = new SuiteSchedule { Repeat = ScheduleRepeat.Daily, At = "07:00", Enabled = true };
            Equal("A schedule needs a suite-run cap: scheduled runs can't ask, so the cap blocks.",
                Throws<ArgumentException>(() => (suite with { Schedule = daily }).Normalize(), "an enabled schedule without a cap").Message, "the message");
            Equal("Daily at 07:00", (suite with { Schedule = daily, CapMicros = 2_000_000 }).Normalize().Schedule!.Label, "with a cap it is kept");
            (suite with { Schedule = daily with { Enabled = false } }).Normalize();

            var estimate = new SuiteEstimate(540_000, 160_000, 170_000,
                [new SuiteEstimateLine("openai", "gpt", 36, 15_000, 540_000, BillingKind.Metered), new SuiteEstimateLine("codex", "sol", 36, 0, 0, BillingKind.Subscription)]);
            Check(!estimate.Fits(500_000) && estimate.Fits(540_000), "the worst case against the cap");
            Check(!estimate.HasUnknownPrice && (estimate with { Lines = [.. estimate.Lines, new SuiteEstimateLine("x", "y", 1, 0, 0, BillingKind.Unknown)] }).HasUnknownPrice, "unknown prices");

            var cases = new[] { EvalIds.New(EvalIds.Case), EvalIds.New(EvalIds.Case) };
            var run = SuiteRun.Start(suite, cases, 21, SuiteRunTrigger.Schedule, EvalNow, EvalNow);
            Equal("21|Queued|18|3", $"{run.Number}|{run.Status}|{run.Answers}|{run.Targets.Count}", "a run copies the suite's targets and k");
            Check(EvalIds.IsValid(EvalIds.Run, run.Id) && run.Active && run.Target(suite.Targets[0].Id) is not null, "a new run");
            Throws<ArgumentException>(() => SuiteRun.Start(suite, cases, 1, SuiteRunTrigger.Manual, EvalNow, EvalNow), "a manual run with a slot");
            Throws<ArgumentException>(() => SuiteRun.Start(suite, cases, 1, SuiteRunTrigger.Schedule, null, EvalNow), "a scheduled run without one");
            Throws<ArgumentException>(() => SuiteRun.Start(suite, [], 1, SuiteRunTrigger.Manual, null, EvalNow), "no cases");
            Throws<ArgumentException>(() => SuiteRun.Start(suite, [cases[0], cases[0]], 1, SuiteRunTrigger.Manual, null, EvalNow), "a case twice");
            Throws<ArgumentException>(() => SuiteRun.Start(suite, cases, 0, SuiteRunTrigger.Manual, null, EvalNow), "run number 0");
        }),

        Case("evals.contract.schedule-slots", () =>
        {
            var utc = TimeZoneInfo.Utc;
            var daily = new SuiteSchedule { Repeat = ScheduleRepeat.Daily, At = "7:00", EveryHours = 6, Day = ScheduleDay.Monday, Enabled = true }.Normalize();
            Equal("07:00||", $"{daily.At}|{daily.EveryHours}|{daily.Day}", "HH:mm, other repeats' fields dropped");
            Equal("Daily at 07:00", daily.Label, "daily");
            var hours = new SuiteSchedule { Repeat = ScheduleRepeat.Hours, EveryHours = 6, At = "07:00" }.Normalize();
            Equal("Every 6 hours from 07:00", hours.Label, "hours");
            Equal("01:00,07:00,13:00,19:00", string.Join(",", hours.TimesOfDay().Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture))), "every 6 hours from 07:00 wraps the day");
            Equal(4, hours.MaxRunsPerDay, "4 runs a day");
            Equal("03:00,07:00,12:00,17:00,22:00", string.Join(",", (hours with { EveryHours = 5 }).TimesOfDay().Select(time => time.ToString("HH:mm", CultureInfo.InvariantCulture))), "every 5 hours");
            Equal(1, (hours with { EveryHours = 24 }).MaxRunsPerDay, "every 24 hours is daily");
            Equal("Every hour from 07:00", (hours with { EveryHours = 1 }).Label, "every hour");
            Equal(24, (hours with { EveryHours = 1 }).MaxRunsPerDay, "24 a day");
            var weekly = new SuiteSchedule { Repeat = ScheduleRepeat.Weekly, Day = ScheduleDay.Monday, At = "07:00" }.Normalize();
            Equal("Weekly, Monday 07:00", weekly.Label, "weekly");
            Equal(DayOfWeek.Sunday, SuiteSchedule.ToDayOfWeek(ScheduleDay.Sunday), "Sunday");
            Equal(DayOfWeek.Monday, SuiteSchedule.ToDayOfWeek(ScheduleDay.Monday), "Monday");
            Throws<ArgumentException>(() => new SuiteSchedule { At = "25:00" }.Normalize(), "a time that is not one");
            Throws<ArgumentException>(() => new SuiteSchedule { Repeat = ScheduleRepeat.Hours, EveryHours = 0 }.Normalize(), "every 0 hours");
            Throws<ArgumentException>(() => new SuiteSchedule { Repeat = ScheduleRepeat.Hours }.Normalize(), "every ? hours");
            Throws<ArgumentException>(() => new SuiteSchedule { Repeat = ScheduleRepeat.Weekly }.Normalize(), "weekly without a day");
            Throws<ArgumentException>(() => new SuiteSchedule { TimeZone = "Nowhere/Never" }.Normalize(), "an unknown zone");
            Throws<ArgumentException>(() => new SuiteSchedule { Paused = new SchedulePause(SchedulePause.OverCap, " ", EvalNow) }.Normalize(), "a pause without a reason");
            Equal("UTC", new SuiteSchedule { TimeZone = " UTC " }.Normalize().TimeZone, "a known zone is kept");
            Check(new SuiteSchedule { Enabled = true }.Active && !new SuiteSchedule { Enabled = true, Paused = new SchedulePause(SchedulePause.OverCap, "over", EvalNow) }.Active, "paused is not active");

            // EvalNow is Friday 2026-10-30 10:00 UTC.
            Equal(At(2026, 10, 30, 7), daily.Latest(EvalNow, utc), "today's 07:00");
            Equal(At(2026, 10, 29, 7), daily.Latest(At(2026, 10, 30, 6, 59), utc), "before 07:00, yesterday's");
            Equal(At(2026, 10, 30, 7), daily.Latest(At(2026, 10, 30, 7), utc), "a slot is due at its time");
            Equal(At(2026, 10, 31, 7), daily.Next(EvalNow, utc), "next: tomorrow");
            Equal(At(2026, 10, 31, 7), daily.Next(At(2026, 10, 30, 7), utc), "Next is after, not at");
            var enabled = daily with { EnabledAt = At(2026, 10, 29, 12) };
            Check(enabled.Latest(At(2026, 10, 30, 6, 59), utc) is null, "a slot before it was enabled never runs");
            Equal(At(2026, 10, 30, 7), enabled.Next(At(2026, 10, 1, 0), utc), "the first slot after enabling");
            Check((daily with { EnabledAt = At(2026, 10, 30, 7) }).Latest(At(2026, 10, 30, 7), utc) is null, "a slot at the moment it was enabled does not run");
            // Missed slots collapse: two days off, and only the latest slot is due.
            var sixHourly = hours with { Enabled = true, EnabledAt = At(2026, 10, 28, 0) };
            Equal(At(2026, 10, 30, 7), sixHourly.Latest(EvalNow, utc), "after two days away, one slot: the latest");
            Equal(At(2026, 10, 30, 13), sixHourly.Next(EvalNow, utc), "then the next");
            Equal(At(2026, 10, 30, 1), sixHourly.Latest(At(2026, 10, 30, 6, 59), utc), "the 01:00 slot");
            Equal(At(2026, 10, 26, 7), weekly.Latest(EvalNow, utc), "last Monday");
            Equal(At(2026, 11, 2, 7), weekly.Next(EvalNow, utc), "next Monday");
            Equal(At(2026, 11, 2, 7), weekly.Next(At(2026, 10, 26, 7), utc), "a week after a Monday slot");
            var berlin = DstZone();
            Equal(new DateTimeOffset(2026, 10, 30, 7, 0, 0, TimeSpan.FromHours(1)), daily.Latest(EvalNow, berlin), "07:00 in the zone's own time");
        }),

        Case("evals.contract.schedule-slots-across-dst", () =>
        {
            // A zone at UTC+1 with summer time from the last Sunday of March 02:00 to the last Sunday of October 03:00 (2026: March 29, October 25).
            var zone = DstZone();
            var gap = new SuiteSchedule { At = "02:30" };
            Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), gap.Next(At(2026, 3, 28, 12), zone), "02:30 does not exist that day: the first minute after the gap");
            Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), gap.Latest(At(2026, 3, 29, 12), zone), "Latest agrees");
            Equal(new DateTimeOffset(2026, 3, 30, 2, 30, 0, TimeSpan.FromHours(2)), gap.Next(At(2026, 3, 29, 12), zone), "and 02:30 the day after");
            Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), gap.Next(At(2026, 10, 24, 12), zone), "02:30 happens twice: the first");
            Equal(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)), gap.Next(At(2026, 10, 25, 0, 30), zone), "never the second");
            var hourly = new SuiteSchedule { Repeat = ScheduleRepeat.Hours, EveryHours = 1, At = "00:00" }.Normalize();
            Equal(23, SlotsOn(hourly, zone, new DateTime(2026, 3, 29)), "a spring-forward day has 23 hourly runs");
            Equal(24, SlotsOn(hourly, zone, new DateTime(2026, 10, 25)), "a fall-back day has 24, none twice");
            Equal(24, SlotsOn(hourly, zone, new DateTime(2026, 10, 30)), "an ordinary day 24");
            Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.FromHours(1)), SuiteSchedule.Instant(new DateTime(2026, 3, 29, 1, 0, 0), zone), "Instant before the gap");
        }),

        Case("evals.contract.results-cells-and-target-totals", () =>
        {
            var runId = EvalIds.New(EvalIds.Run);
            var caseId = EvalIds.New(EvalIds.Case);
            SuiteResult Result(string target, int repetition, ResultStatus status, params CheckVerdict[] verdicts) =>
                new SuiteResult { RunId = runId, CaseId = caseId, TargetId = target, Repetition = repetition, Status = status }
                    .WithChecks([.. verdicts.Select((verdict, index) => new CheckResult("k" + (index + 1), CheckKind.Regex, verdict))]);
            var mixed = Result("t1", 0, ResultStatus.Done, CheckVerdict.Passed, CheckVerdict.Failed);
            Equal("Failed|0.5|False", Invariant($"{mixed.Verdict}|{mixed.Score}|{mixed.Passed}"), "one of two checks");
            var passed = Result("t1", 1, ResultStatus.Done, CheckVerdict.Passed, CheckVerdict.Passed);
            Equal("Passed|1|True", Invariant($"{passed.Verdict}|{passed.Score}|{passed.Passed}"), "every check");
            var none = Result("t1", 2, ResultStatus.Done);
            Equal("Passed||True", Invariant($"{none.Verdict}|{none.Score}|{none.Passed}"), "no checks: answered is passed, no score");
            var error = Result("t1", 3, ResultStatus.Error, CheckVerdict.Passed);
            Equal("Failed|0|False", Invariant($"{error.Verdict}|{error.Score}|{error.Passed}"), "an error fails");
            var review = Result("t1", 4, ResultStatus.Done, CheckVerdict.Passed, CheckVerdict.Review);
            Equal("Review|0.5|False", Invariant($"{review.Verdict}|{review.Score}|{review.Passed}"), "review does not pass");
            Check(passed.IsFinished && error.IsFinished && !Result("t1", 5, ResultStatus.Running).IsFinished, "IsFinished");

            var results = new List<SuiteResult>
            {
                passed with { DurationMs = 100, TokensPerSecond = 50, CostMicros = 10, ResponseModel = "m-new" },
                mixed with { DurationMs = 300, TokensPerSecond = 30, CostMicros = 20, ResponseModel = "m-old" },
                none with { DurationMs = 200, TokensPerSecond = 40, CostMicros = 30, ResponseModel = "m-new" },
                error with { CostMicros = 5 },
                Result("t1", 4, ResultStatus.Queued),
                Result("t2", 0, ResultStatus.Done, CheckVerdict.Passed) with { CostMicros = 70, ListPrice = true, DurationMs = 999 },
            };
            var totals = TargetSummary.Of("t1", 6, results);
            Equal("6|4|2|0.5|200|40|65|0", Invariant($"{totals.Answers}|{totals.Done}|{totals.Passed}|{totals.PassRate}|{totals.MedianLatencyMs}|{totals.MedianTokensPerSecond}|{totals.CostMicros}|{totals.ListMicros}"),
                "answers, finished, passed, pass rate, medians of answered calls, metered cost");
            Equal("m-new,m-old", string.Join(",", totals.ResponseModels), "response models, most answers first");
            Equal("0|70", $"{TargetSummary.Of("t2", 1, results).CostMicros}|{TargetSummary.Of("t2", 1, results).ListMicros}", "subscription answers are list price");
            Check(TargetSummary.Of("t3", 3, results).PassRate is null, "no pass rate before any finished");
            Equal(2.5, TargetSummary.Median([4, 1, 3, 2]), "an even median");
            Check(TargetSummary.Median([]) is null, "no median of nothing");
            var cell = SuiteCell.Of(caseId, "t1", results);
            Equal("5|2|0.5|200|65|False", Invariant($"{cell.Repeats.Count}|{cell.Passed}|{cell.Score}|{cell.MedianLatencyMs}|{cell.CostMicros}|{cell.ListPrice}"), "a cell: repeats, passes, mean score");
            Equal("0,1,2,3,4", string.Join(",", cell.Repeats.Select(result => result.Repetition)), "repeats in order");

            var text = new string('a', SuiteResult.MaxTextLength - 1) + "\U0001F600" + "tail";
            var cut = SuiteResult.Cut(text)!;
            Equal(SuiteResult.MaxTextLength - 1, cut.Length, "an answer is cut before a split surrogate pair");
            Equal("short", SuiteResult.Cut("short"), "a short answer is kept");
        }),

        Case("evals.contract.drift-rules-follow-the-plan", () =>
        {
            Equal("response-model:drift,broken-case:suspect,pass-rate:suspect,judge-score:suspect,fingerprint:suspect,cost:suspect,latency:info,tokens-per-second:info",
                string.Join(",", DriftRules.All.Select(rule => $"{EvalJson.Wire(rule)}:{EvalJson.Wire(DriftRules.InitialLevel(rule))}")), "the plan's table");
            Check(DriftRules.NeedsConfirmation(DriftRule.PassRate) && !DriftRules.NeedsConfirmation(DriftRule.ResponseModel) && !DriftRules.NeedsConfirmation(DriftRule.Latency),
                "only suspect rules wait for the next run");
            Equal("0.05|0.5|0.25", Invariant($"{DriftRules.SignificanceLevel}|{DriftRules.SpeedChange}|{DriftRules.CostChange}"), "the thresholds");
            var suite = EvalIds.New(EvalIds.Suite);
            var baseline = EvalIds.New(EvalIds.Run);
            var run20 = EvalIds.New(EvalIds.Run);
            var run21 = EvalIds.New(EvalIds.Run);
            var first = DriftSignal.Raise(suite, "t1", run20, baseline, DriftRule.PassRate, " Pass rate fell from 0.92 to 0.71. ", EvalNow, pValue: 0.031, effect: -0.23);
            Equal("Suspect|Pass rate fell from 0.92 to 0.71.", $"{first.Level}|{first.Message}", "a sign-test signal starts suspect");
            Check(first.AwaitingConfirmation && first.FirstRunId == run20, "and waits for the next run");
            var again = DriftSignal.Raise(suite, "t1", run21, baseline, DriftRule.PassRate, "Pass rate fell from 0.92 to 0.70.", EvalNow.AddDays(1));
            var confirmed = first.Confirm(again);
            Equal($"Drift|{run21}|{run20}|Pass rate fell from 0.92 to 0.70.", $"{confirmed.Level}|{confirmed.RunId}|{confirmed.FirstRunId}|{confirmed.Message}", "confirmed: drift, seen first by run 20");
            Throws<InvalidOperationException>(() => first.Confirm(DriftSignal.Raise(suite, "t1", run21, baseline, DriftRule.Cost, "cost", EvalNow)), "another rule does not confirm");
            Throws<InvalidOperationException>(() => first.Confirm(DriftSignal.Raise(suite, "t2", run21, baseline, DriftRule.PassRate, "x", EvalNow)), "nor another target");
            Throws<InvalidOperationException>(() => first.Confirm(first), "nor the same run");
            var latency = DriftSignal.Raise(suite, "t1", run20, baseline, DriftRule.Latency, "Latency +62%.", EvalNow);
            Equal(DriftLevel.Info, latency.Level, "latency is info");
            Throws<InvalidOperationException>(() => latency.Confirm(latency with { RunId = run21 }), "info never becomes drift");
            Equal(DriftLevel.Drift, DriftSignal.Raise(suite, "t1", run20, baseline, DriftRule.ResponseModel, "Now answers as m-2026.", EvalNow).Level, "a response model change is drift at once");
            Equal(DriftLevel.Info, DriftRules.Worst([latency, first with { Cleared = EvalNow }]), "cleared signals do not count");
            Equal(DriftLevel.Drift, DriftRules.Worst([latency, first, confirmed]), "the worst open level");
            Equal(DriftLevel.Ok, DriftRules.Worst([]), "ok when none");
            Throws<ArgumentException>(() => (latency with { Level = DriftLevel.Drift }).Normalize(), "latency cannot raise drift");
            Throws<ArgumentException>(() => (first with { PValue = 1.5 }).Normalize(), "a p-value above 1");
            Throws<ArgumentException>(() => (first with { Message = " " }).Normalize(), "no message");
            Throws<ArgumentException>(() => (first with { RunId = "sr1" }).Normalize(), "a bad run id");
            (first with { Level = DriftLevel.Ok }).Normalize();
            Equal(DriftSignal.MaxMessage, (first with { Message = new string('m', DriftSignal.MaxMessage + 10) }).Normalize().Message.Length, "long messages are cut");
            var accepted = new Baseline { SuiteId = suite, TargetId = "t1", RunId = baseline, Identity = new ModelIdentity("openai", "gpt"), Accepted = EvalNow };
            Check(accepted.Current && !(accepted with { Replaced = EvalNow }).Current, "a baseline is current until replaced");
        }),

        Case("evals.contract.price-changes-between-snapshots", () =>
        {
            var before = new PriceSnapshot("openai", "aaa", EvalNow.AddDays(-14),
                [new ModelPrice("gpt-5.6-terra", new CatalogCost(1.25, 12, 0.2)), new ModelPrice("gone", new CatalogCost(1, 2)), new ModelPrice("new-price", null)]);
            var after = new PriceSnapshot("openai", "bbb", EvalNow,
                [new ModelPrice("gpt-5.6-terra", new CatalogCost(1.25, 14, 0.1)), new ModelPrice("added", new CatalogCost(3, 4)), new ModelPrice("new-price", new CatalogCost(0.5, 1))]);
            var changes = after.ChangesSince(before);
            Equal("gpt-5.6-terra output $12.00 → $14.00|gpt-5.6-terra cache read $0.20 → $0.10|new-price input none → $0.50|new-price output none → $1.00",
                string.Join("|", changes.Select(change => change.Model + " " + change.Label)), "changed rates of listed models; added and removed models are not changes");
            Check(changes[0].Rise && !changes[1].Rise && changes[2].Rise, "rises");
            Equal("aaa→bbb", $"{changes[0].BeforeSha256}→{changes[0].AfterSha256}", "which catalog files");
            Equal(EvalNow, changes[0].Detected, "when");
            Equal(0, after.ChangesSince(null).Count, "the first snapshot is not a change");
            Equal(0, after.ChangesSince(after).Count, "nor the same prices");
            Throws<ArgumentException>(() => after.ChangesSince(before with { Provider = "anthropic" }), "snapshots of two providers");
            Equal("cache write", PriceChange.FieldLabel(PriceField.CacheWrite), "labels");
        }),

        Case("evals.contract.json-wire-names-and-round-trips", () =>
        {
            Equal("request,agent", Wires<ArenaMode>(), "arena modes");
            Equal("held,ready,voted,void", Wires<ArenaStatus>(), "battle statuses");
            Equal("pick,tie,all-bad,rank", Wires<VoteKind>(), "vote kinds");
            Equal("chat,playground,compare,arena,bench,history,inspector,suite", Wires<PinSource>(), "pin sources");
            Equal("contains,regex,json-schema,tool,limits,judge", Wires<CheckKind>(), "check kinds");
            Equal("answer,first-code-block", Wires<CheckScope>(), "scopes");
            Equal("at-least-once,exactly-once,never", Wires<ToolCallCount>(), "call counts");
            Equal("equals,regex,present", Wires<ArgumentRule>(), "argument rules");
            Equal("passed,failed,review,error", Wires<CheckVerdict>(), "verdicts");
            Equal("hours,daily,weekly", Wires<ScheduleRepeat>(), "repeats");
            Equal("monday,tuesday,wednesday,thursday,friday,saturday,sunday", Wires<ScheduleDay>(), "days");
            Equal("manual,schedule", Wires<SuiteRunTrigger>(), "triggers");
            Equal("queued,running,complete,cancelled,failed,interrupted", Wires<SuiteRunStatus>(), "run statuses");
            Equal("queued,running,done,error,cancelled", Wires<ResultStatus>(), "result statuses");
            Equal("started,skipped-running,skipped-over-cap,skipped-unknown-price,skipped-refused", Wires<SlotOutcome>(), "slot outcomes");
            Equal("ok,info,suspect,drift", Wires<DriftLevel>(), "drift levels");
            Equal("broken-case,pass-rate,judge-score,latency,tokens-per-second,cost,response-model,fingerprint", Wires<DriftRule>(), "drift rules");
            Equal("input,output,cache-read,cache-write", Wires<PriceField>(), "price fields");
            Equal(VoteKind.AllBad, EvalJson.FromWire<VoteKind>("all-bad"), "FromWire");
            Check(EvalJson.FromWire<VoteKind>("AllBad") is null && EvalJson.FromWire<VoteKind>(null) is null, "only wire names");

            var battle = Battle(3).Normalize() with { Tag = "coding", Created = EvalNow, Status = ArenaStatus.Voted, Voted = EvalNow };
            RoundTrip(battle);
            RoundTrip(Vote(battle, VoteKind.Rank, ranking: ["B", "C", "A"]).Normalize(3));
            var schema = JsonNode.Parse("""{"type":"object","properties":{"v":{"type":"string","maxLength":10}}}""")!.AsObject();
            var testCase = new TestCase
            {
                Id = EvalIds.New(EvalIds.Case), SuiteId = EvalIds.New(EvalIds.Suite), Name = "c", Spec = new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "q" }] },
                Checks = [RegexCheck("^a"), new EvalCheck { Kind = CheckKind.JsonSchema, Schema = schema },
                    new EvalCheck { Kind = CheckKind.Tool, Tool = "read", Arguments = [new ToolArgumentMatch("p", ArgumentRule.EqualsValue, "x")] },
                    new EvalCheck { Kind = CheckKind.Judge, Judge = new JudgeSpec("p", "m", "rubric") }],
                Origin = new TestCaseOrigin(PinSource.History, RunId: "r1"), Reference = new TestCaseReference { Text = "a", Output = new JsonObject { ["role"] = "assistant" } },
                Created = EvalNow, Updated = EvalNow,
            }.Normalize();
            var caseJson = RoundTrip(testCase);
            Check(caseJson.Contains("\"kind\":\"json-schema\"", StringComparison.Ordinal) && caseJson.Contains("\"source\":\"history\"", StringComparison.Ordinal), "wire names in the case: " + caseJson);
            Check(!caseJson.Contains("\"account\"", StringComparison.Ordinal), "no account key at all");
            var regexJson = EvalJson.Serialize(RegexCheck("^a").Normalize());
            Check(regexJson.Contains("\"kind\":\"regex\"", StringComparison.Ordinal) && !regexJson.Contains("\"text\"", StringComparison.Ordinal) && !regexJson.Contains("\"label\"", StringComparison.Ordinal),
                "a check sends only its kind's fields: " + regexJson);
            var parsed = EvalJson.Deserialize<EvalCheck>("""{"kind":"regex","pattern":"x+","scope":"first-code-block"}""").Normalize();
            Equal("Regex|FirstCodeBlock|x+", $"{parsed.Kind}|{parsed.Scope}|{parsed.Pattern}", "a page's plain JSON check");
            var suite = NewSuite([new SuiteTarget("faux", "echo", "acct", "high", 0.5, "flex")]) with
            {
                CapMicros = 500_000,
                Schedule = new SuiteSchedule { Repeat = ScheduleRepeat.Weekly, Day = ScheduleDay.Friday, At = "07:00", Enabled = true, EnabledAt = EvalNow,
                    Paused = new SchedulePause(SchedulePause.OverCap, "over", EvalNow) },
                Created = EvalNow, Updated = EvalNow,
            };
            var suiteJson = RoundTrip(suite.Normalize());
            Check(suiteJson.Contains("\"repeat\":\"weekly\"", StringComparison.Ordinal) && suiteJson.Contains("\"day\":\"friday\"", StringComparison.Ordinal) &&
                suiteJson.Contains($"\"id\":\"{suite.Targets[0].Id}\"", StringComparison.Ordinal), "schedule wire names and the target id: " + suiteJson);
            Equal(suite.Targets[0].Id, EvalJson.Deserialize<Suite>(suiteJson).Targets[0].Id, "a target's id survives (it is derived)");
            var run = SuiteRun.Start(suite.Normalize(), [EvalIds.New(EvalIds.Case)], 1, SuiteRunTrigger.Manual, null, EvalNow) with { Status = SuiteRunStatus.Interrupted };
            RoundTrip(run);
            RoundTrip(new SuiteResult
            {
                RunId = run.Id, CaseId = run.CaseIds[0], TargetId = run.Targets[0].Id, Status = ResultStatus.Done, Fingerprint = -42, ResponseModel = "m",
                Checks = [new CheckResult("k1", CheckKind.Judge, CheckVerdict.Review, "unsure", 3, 0.4, "r9")], Verdict = CheckVerdict.Review, Score = 0, Finished = EvalNow,
            });
            RoundTrip(DriftSignal.Raise(suite.Id, "t1", run.Id, run.Id, DriftRule.Fingerprint, "moved", EvalNow, detail: new JsonObject { ["distance"] = 12 }));
            RoundTrip(new Baseline { SuiteId = suite.Id, TargetId = "t1", RunId = run.Id, Identity = new ModelIdentity("p", "m", "low"), ResponseModels = ["m-1"], Accepted = EvalNow });
            RoundTrip(new PriceSnapshot("openai", "abc", EvalNow, [new ModelPrice("gpt", new CatalogCost(1, 2, 0.5, 1.5)), new ModelPrice("free", null)]) { Id = 7 });
            RoundTrip(new ScheduleSlot(suite.Id, EvalNow, SlotOutcome.SkippedOverCap, null, "over the cap", EvalNow));
        }),

        Case("evals.contract.settings-section", async () =>
        {
            Equal("evals", EvalsSettings.SectionName, "the section");
            var defaults = EvalsSettings.CreateDefault().Normalize();
            Equal("False|10|3", $"{defaults.StreamAnyway}|{defaults.ProvisionalBelow}|{defaults.DefaultK}", "held answers, provisional below 10, k 3");
            Throws<ArgumentException>(() => new EvalsSettings(ProvisionalBelow: 0).Normalize(), "provisional below 0");
            Throws<ArgumentException>(() => new EvalsSettings(DefaultK: Suite.MaxK + 1).Normalize(), "k 11");
            var root = Temp("evals-settings");
            try
            {
                var store = new SettingsStore(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal(10, store.Get<EvalsSettings>().ProvisionalBelow, "defaults when nothing is stored");
                await store.SaveAsync(new EvalsSettings(StreamAnyway: true, ProvisionalBelow: 5));
                var fresh = new SettingsStore(new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal("True|5|3", $"{fresh.Get<EvalsSettings>().StreamAnyway}|{fresh.Get<EvalsSettings>().ProvisionalBelow}|{fresh.Get<EvalsSettings>().DefaultK}", "stored and read back");
            }
            finally { DeleteTemp(root); }
        }),

        Case("evals.contract.migrations-create-the-tables-and-rules", () =>
        {
            var root = Temp("evals-db");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, .. EvalMigrations.All]);
                Equal("1,2000,2001,2002,2003,2004", string.Join(",", db.Migrate()), "the contracts' migrations");
                Equal("", string.Join(",", db.Migrate()), "applied once");
                Equal("eval_baselines,eval_battles,eval_cases,eval_drift_signals,eval_price_changes,eval_price_snapshots,eval_results,eval_runs,eval_schedule_slots,eval_suites,eval_votes",
                    string.Join(",", db.Query("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'eval_%' ORDER BY name;", reader => reader.GetString(0))), "the tables");

                var suite = EvalIds.New(EvalIds.Suite);
                db.Execute("INSERT INTO eval_suites (id, name, data, created_at, updated_at) VALUES ($id, 'S', '{}', 0, 0);", ("$id", suite));
                var caseId = EvalIds.New(EvalIds.Case);
                db.Execute("INSERT INTO eval_cases (id, suite_id, name, origin, data, created_at, updated_at) VALUES ($id, $suite, 'C', 'chat', '{}', 0, 0);", ("$id", caseId), ("$suite", suite));
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_cases (id, suite_id, name, origin, data, created_at, updated_at) VALUES ('tc-x', 'su-missing', 'C', 'chat', '{}', 0, 0);"), "a case of no suite");
                string InsertRun(int number, long? slot)
                {
                    var id = EvalIds.New(EvalIds.Run);
                    db.Execute("INSERT INTO eval_runs (id, suite_id, number, run_trigger, slot, status, data, created_at) VALUES ($id, $suite, $number, $trigger, $slot, 'queued', '{}', 0);",
                        ("$id", id), ("$suite", suite), ("$number", number), ("$trigger", slot is null ? "manual" : "schedule"), ("$slot", slot));
                    return id;
                }
                var run1 = InsertRun(1, null);
                var run2 = InsertRun(2, 1000);
                Throws<SqliteException>(() => InsertRun(2, null), "run numbers are unique per suite");
                Throws<SqliteException>(() => InsertRun(3, 1000), "one run per suite and slot");
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_runs (id, suite_id, number, run_trigger, slot, status, data, created_at) VALUES ('sr-y', $suite, 9, 'manual', 5, 'queued', '{}', 0);", ("$suite", suite)), "a manual run has no slot");
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_runs (id, suite_id, number, run_trigger, slot, status, data, created_at) VALUES ('sr-z', $suite, 9, 'schedule', NULL, 'queued', '{}', 0);", ("$suite", suite)), "a scheduled run has one");
                db.Execute("INSERT INTO eval_results (run_id, case_id, target_id, repetition, status, data) VALUES ($run, $case, 't1', 0, 'done', '{}');", ("$run", run1), ("$case", caseId));
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_results (run_id, case_id, target_id, repetition, status, data) VALUES ($run, $case, 't1', 0, 'done', '{}');", ("$run", run1), ("$case", caseId)), "one result per case, target and repetition");
                db.Execute("INSERT INTO eval_schedule_slots (suite_id, slot, outcome, run_id, decided_at) VALUES ($suite, 1000, 'started', $run, 0);", ("$suite", suite), ("$run", run2));
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_schedule_slots (suite_id, slot, outcome, run_id, decided_at) VALUES ($suite, 1000, 'skipped-running', NULL, 0);", ("$suite", suite)), "one decision per slot");
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_schedule_slots (suite_id, slot, outcome, run_id, decided_at) VALUES ($suite, 2000, 'started', NULL, 0);", ("$suite", suite)), "a started slot names its run");
                db.Execute("INSERT INTO eval_schedule_slots (suite_id, slot, outcome, run_id, detail, decided_at) VALUES ($suite, 2000, 'skipped-over-cap', NULL, 'over', 0);", ("$suite", suite));

                void AddBaseline(string run) => db.Execute("INSERT INTO eval_baselines (suite_id, target_id, run_id, identity_key, data, accepted_at) VALUES ($suite, 't1', $run, 'p/m', '{}', 0);", ("$suite", suite), ("$run", run));
                AddBaseline(run1);
                Throws<SqliteException>(() => AddBaseline(run2), "one current baseline per suite and target");
                db.Execute("UPDATE eval_baselines SET replaced_at = 1 WHERE run_id = $run;", ("$run", run1));
                AddBaseline(run2);
                Equal(1L, db.Scalar("SELECT COUNT(*) FROM eval_baselines WHERE replaced_at IS NULL;"), "the new one is current");
                db.Execute("INSERT INTO eval_drift_signals (suite_id, target_id, run_id, baseline_run_id, rule, level, data, created_at) VALUES ($suite, 't1', $run, $base, 'response-model', 'drift', '{}', 0);",
                    ("$suite", suite), ("$run", run2), ("$base", run1));

                var battle = EvalIds.New(EvalIds.Battle);
                db.Execute("INSERT INTO eval_battles (id, mode, status, seed, data, created_at) VALUES ($id, 'request', 'voted', 42, '{}', 0);", ("$id", battle));
                db.Execute("INSERT INTO eval_votes (battle_id, mode, tag, kind, data, created_at) VALUES ($id, 'request', 'coding', 'pick', '{}', 0);", ("$id", battle));
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_votes (battle_id, mode, tag, kind, data, created_at) VALUES ($id, 'request', 'coding', 'tie', '{}', 0);", ("$id", battle)), "one vote per battle");
                Throws<SqliteException>(() => db.Execute("INSERT INTO eval_votes (battle_id, mode, tag, kind, data, created_at) VALUES ('ab-missing', 'request', 'coding', 'tie', '{}', 0);"), "a vote of no battle");
                db.Execute("DELETE FROM eval_battles WHERE id = $id;", ("$id", battle));
                Equal(0L, db.Scalar("SELECT COUNT(*) FROM eval_votes;"), "deleting a battle deletes its vote");

                db.Execute("INSERT INTO eval_price_snapshots (provider, catalog_sha256, data, taken_at) VALUES ('openai', 'abc', '{}', 0);");
                db.Execute("INSERT INTO eval_price_changes (snapshot_id, provider, model, field, before_rate, after_rate, detected_at) VALUES (1, 'openai', 'gpt', 'cache-read', 0.2, 0.1, 0);");
                db.Execute("DELETE FROM eval_price_snapshots;");
                Equal(0L, db.Scalar("SELECT COUNT(*) FROM eval_price_changes;"), "deleting a snapshot deletes its changes");

                db.Execute("DELETE FROM eval_suites WHERE id = $id;", ("$id", suite));
                foreach (var table in new[] { "eval_cases", "eval_runs", "eval_results", "eval_schedule_slots", "eval_baselines", "eval_drift_signals" })
                    Equal(0L, db.Scalar($"SELECT COUNT(*) FROM {table};"), "deleting a suite empties " + table);
            }
            finally { DeleteTemp(root); }
        }),

        Case("evals.contract.every-wire-name-fits-its-column", () =>
        {
            // The CHECK lists in the migrations and the enums' wire names are written separately; every value must fit its column and an
            // unknown one must not.
            var root = Temp("evals-wires");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [.. EvalMigrations.All]);
                db.Migrate();
                var suite = EvalIds.New(EvalIds.Suite);
                db.Execute("INSERT INTO eval_suites (id, name, data, created_at, updated_at) VALUES ($id, 'S', '{}', 0, 0);", ("$id", suite));
                var number = 0;
                var runs = new List<string>();
                void Fits<T>(string what, Action<string> insert) where T : struct, Enum
                {
                    foreach (var value in Enum.GetValues<T>()) insert(EvalJson.Wire(value));
                    Throws<SqliteException>(() => insert("unknown"), "an unknown " + what);
                }
                Fits<ArenaMode>("mode", wire => db.Execute("INSERT INTO eval_battles (id, mode, status, seed, data, created_at) VALUES ($id, $wire, 'held', 1, '{}', 0);", ("$id", EvalIds.New(EvalIds.Battle)), ("$wire", wire)));
                Fits<ArenaStatus>("status", wire => db.Execute("INSERT INTO eval_battles (id, mode, status, seed, data, created_at) VALUES ($id, 'request', $wire, 1, '{}', 0);", ("$id", EvalIds.New(EvalIds.Battle)), ("$wire", wire)));
                Fits<VoteKind>("kind", wire =>
                {
                    var battle = EvalIds.New(EvalIds.Battle);
                    db.Execute("INSERT INTO eval_battles (id, mode, status, seed, data, created_at) VALUES ($id, 'request', 'voted', 1, '{}', 0);", ("$id", battle));
                    db.Execute("INSERT INTO eval_votes (battle_id, mode, tag, kind, data, created_at) VALUES ($id, 'request', 'coding', $wire, '{}', 0);", ("$id", battle), ("$wire", wire));
                });
                Fits<SuiteRunTrigger>("trigger", wire =>
                {
                    var id = EvalIds.New(EvalIds.Run);
                    db.Execute("INSERT INTO eval_runs (id, suite_id, number, run_trigger, slot, status, data, created_at) VALUES ($id, $suite, $number, $wire, $slot, 'queued', '{}', 0);",
                        ("$id", id), ("$suite", suite), ("$number", ++number), ("$wire", wire), ("$slot", wire == "schedule" ? number : null));
                    runs.Add(id);
                });
                Fits<SuiteRunStatus>("run status", wire => db.Execute("INSERT INTO eval_runs (id, suite_id, number, run_trigger, status, data, created_at) VALUES ($id, $suite, $number, 'manual', $wire, '{}', 0);",
                    ("$id", EvalIds.New(EvalIds.Run)), ("$suite", suite), ("$number", ++number), ("$wire", wire)));
                var repetition = 0;
                Fits<ResultStatus>("result status", wire => db.Execute("INSERT INTO eval_results (run_id, case_id, target_id, repetition, status, data) VALUES ($run, 'tc', 't', $rep, $wire, '{}');",
                    ("$run", runs[0]), ("$rep", ++repetition), ("$wire", wire)));
                Fits<CheckVerdict>("verdict", wire => db.Execute("INSERT INTO eval_results (run_id, case_id, target_id, repetition, status, verdict, data) VALUES ($run, 'tc', 't', $rep, 'done', $wire, '{}');",
                    ("$run", runs[0]), ("$rep", ++repetition), ("$wire", wire)));
                var slot = 0;
                Fits<SlotOutcome>("slot outcome", wire => db.Execute("INSERT INTO eval_schedule_slots (suite_id, slot, outcome, run_id, decided_at) VALUES ($suite, $slot, $wire, $run, 0);",
                    ("$suite", suite), ("$slot", ++slot), ("$wire", wire), ("$run", wire == "started" ? runs[0] : null)));
                Fits<DriftRule>("drift rule", wire => db.Execute("INSERT INTO eval_drift_signals (suite_id, target_id, run_id, baseline_run_id, rule, level, data, created_at) VALUES ($suite, 't', $run, $run, $wire, 'ok', '{}', 0);",
                    ("$suite", suite), ("$run", runs[0]), ("$wire", wire)));
                Fits<DriftLevel>("drift level", wire => db.Execute("INSERT INTO eval_drift_signals (suite_id, target_id, run_id, baseline_run_id, rule, level, data, created_at) VALUES ($suite, 't', $run, $run, 'cost', $wire, '{}', 0);",
                    ("$suite", suite), ("$run", runs[0]), ("$wire", wire)));
                db.Execute("INSERT INTO eval_price_snapshots (provider, catalog_sha256, data, taken_at) VALUES ('openai', 'abc', '{}', 0);");
                Fits<PriceField>("price field", wire => db.Execute("INSERT INTO eval_price_changes (snapshot_id, provider, model, field, detected_at) VALUES (1, 'openai', 'gpt', $wire, 0);", ("$wire", wire)));
            }
            finally { DeleteTemp(root); }
        }),

        Case("evals.contract.module-registers-migrations-and-the-app-applies-them", async () =>
        {
            var services = new ServiceCollection();
            EvalsModule.AddEvalsArea(services, new WebAgentOptions { DataDirectory = Temp("evals-module-unused") });
            // W5 E5: the units' own migrations (each in its block, EvalMigrations.Ranges) come after the contracts'.
            var registered = services.Where(service => service.ServiceType == typeof(IWebAgentMigration)).Select(service => ((IWebAgentMigration)service.ImplementationInstance!).Id).Order().ToList();
            Equal("2000,2001,2002,2003,2004", string.Join(",", registered.Where(id => id < 2100)), "the module registers the contracts' migrations");
            Check(registered.All(id => EvalMigrations.RangeOf(id) is not null), "every unit's migration is in its block of the range: " + string.Join(",", registered));
            foreach (var migration in EvalMigrations.All)
            {
                Equal(MigrationArea.Evals, MigrationArea.Of(migration.Id), "in the evals area: " + migration.Id);
                Equal("evals: contracts", EvalMigrations.RangeOf(migration.Id)!.Name, "in the contracts' block: " + migration.Id);
            }
            var blocks = EvalMigrations.Ranges;
            for (var index = 0; index < blocks.Count; index++)
            {
                Check(blocks[index].First >= MigrationArea.Evals.First && blocks[index].Last <= MigrationArea.Evals.Last, "a block inside the evals range: " + blocks[index]);
                if (index > 0) Check(blocks[index].First > blocks[index - 1].Last, "blocks do not overlap: " + blocks[index]);
            }
            Equal("/evals/suites/su1|/evals/runs/sr%201|/evals/leaderboard?tag=c%23", $"{EvalRoutes.Suite("su1")}|{EvalRoutes.Run("sr 1")}|{EvalRoutes.LeaderboardOf("c#")}", "routes");
            Equal("/evals/leaderboard?mode=agent&tag=c%23", EvalRoutes.LeaderboardOf(ArenaMode.Agent, "c#"), "a leaderboard names its mode");

            var root = Temp("evals-app");
            try
            {
                var data = Path.Join(root, "data");
                await using (var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data }))
                {
                    Equal(200, (await app.Get("/api/health")).Status, "the app starts with the Evals area");
                    await app.StopAsync();
                }
                var path = new WebAgentOptions { DataDirectory = data }.DatabasePath;
                // W5 E5: the contracts' block (the units add their own in theirs: the arena's 2500 makes eval_arena_rounds).
                Equal("2000,2001,2002,2003,2004", string.Join(",", ReadDb(path, "SELECT id FROM schema_migrations WHERE id BETWEEN 2000 AND 2099 ORDER BY id;", reader => reader.GetInt32(0))),
                    "the app applies the evals migrations at start");
                Equal(11L, ReadDb(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'eval_%' AND name <> 'eval_arena_rounds';", reader => reader.GetInt64(0)).Single(), "the evals tables");
            }
            finally { DeleteTemp(root); }
        }),
    ];

    private static ArenaContestant Contestant(int position, string model, string? thinking = null) =>
        new(position, new ModelIdentity("faux", model, thinking));

    private static ArenaBattle Battle(int contestants) => new()
    {
        Id = EvalIds.New(EvalIds.Battle),
        Seed = 12345,
        Contestants = [.. Enumerable.Range(0, contestants).Select(position => Contestant(position, "echo-" + (char)('a' + position)))],
    };

    private static ArenaVote Vote(ArenaBattle battle, VoteKind kind, string? pick = null, IReadOnlyList<string>? ranking = null) =>
        new() { BattleId = battle.Id, Mode = battle.Mode, Tag = "Coding", Kind = kind, Pick = pick, Ranking = ranking, Created = EvalNow };

    private static EvalCheck RegexCheck(string pattern) => new() { Kind = CheckKind.Regex, Pattern = pattern };

    private static Suite NewSuite(IReadOnlyList<SuiteTarget> targets) => new() { Id = EvalIds.New(EvalIds.Suite), Name = "Regex edge cases", Targets = targets };

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string Wires<T>() where T : struct, Enum => string.Join(",", Enum.GetValues<T>().Select(value => EvalJson.Wire(value)));

    /// <summary>Writes <paramref name="value"/>, reads it back and writes it again: the two JSON texts must match. Returns the JSON.</summary>
    private static string RoundTrip<T>(T value)
    {
        var json = EvalJson.Serialize(value);
        var again = EvalJson.Serialize(EvalJson.Deserialize<T>(json));
        Equal(json, again, typeof(T).Name + " round trip");
        return json;
    }

    /// <summary>A zone at UTC+1 with summer time (+1 hour) from the last Sunday of March 02:00 to the last Sunday of October 03:00.</summary>
    private static TimeZoneInfo DstZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test/Summer", TimeSpan.FromHours(1), "Test", "Test", "Test summer", [rule]);
    }

    /// <summary>How many slots of <paramref name="schedule"/> fall on the local <paramref name="day"/> in <paramref name="zone"/>.</summary>
    private static int SlotsOn(SuiteSchedule schedule, TimeZoneInfo zone, DateTime day)
    {
        var at = SuiteSchedule.Instant(day, zone).AddTicks(-1);
        var count = 0;
        var seen = new HashSet<DateTimeOffset>();
        while (true)
        {
            at = schedule.Next(at, zone);
            if (TimeZoneInfo.ConvertTime(at, zone).Date != day.Date) return count;
            Check(seen.Add(at), "no slot twice: " + at);
            count++;
        }
    }
}
