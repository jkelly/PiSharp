using System.Collections.Concurrent;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

// Decision 0010, M4: supervised research — bounded fan-out into task-owned children, partial publication, restart, cancellation, deadlines,
// budgets and planning.
internal static partial class Program
{
    private static readonly FauxModel Thinker = new("thinker", "Thinker", 128_000, Reasoning: true);

    /// <summary>Scripted research answers: holds, failures, the planner's plans and how many calls ran at once.</summary>
    private sealed class Lab
    {
        private int running;
        private int plans;
        public int MaxRunning;
        public ConcurrentQueue<FauxChat> Seen { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> Holds { get; } = new(StringComparer.Ordinal);
        /// <summary>How many more times a key's call throws.</summary>
        public ConcurrentDictionary<string, int> Failures { get; } = new(StringComparer.Ordinal);
        /// <summary>What an angle answers instead of "Answer for &lt;key&gt;.", by key.</summary>
        public ConcurrentDictionary<string, string> Replies { get; } = new(StringComparer.Ordinal);
        /// <summary>The planner's answer to its n-th call (1-based).</summary>
        public Func<int, string> Plan { get; set; } = _ => """Here: [{"title":"Cost","prompt":"What does it cost?"},{"title":"Risk"}]""";

        public FauxDurableModels Models(Func<JsonObjectValue, CancellationToken, Task<JsonObjectValue>>? classify = null) =>
            new([FauxDurableModels.Echo, FauxDurableModels.EchoSmall, Thinker]) { RespondAsync = RespondAsync, Classify = classify };

        public TaskCompletionSource Hold(string key) => Holds[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Asked(string key) => Seen.Count(chat => KeyOf(chat.Text) == key);

        public static string KeyOf(string text)
        {
            if (text.StartsWith("Plan research", StringComparison.Ordinal)) return "plan";
            if (text.StartsWith("Question:", StringComparison.Ordinal)) return "synthesis";
            var at = text.IndexOf("Your angle: ", StringComparison.Ordinal);
            if (at < 0) return "other";
            var rest = text[(at + 12)..];
            return rest[..(rest.IndexOf('\n') is var end and >= 0 ? end : rest.Length)];
        }

        private async Task<string?> RespondAsync(FauxChat chat, CancellationToken cancellationToken)
        {
            Seen.Enqueue(chat);
            var key = KeyOf(chat.Text);
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref MaxRunning)) < now && Interlocked.CompareExchange(ref MaxRunning, now, seen) != seen) { }
            try
            {
                if (Holds.TryGetValue(key, out var hold)) await hold.Task.WaitAsync(cancellationToken);
                if (Failures.TryGetValue(key, out var left) && left > 0)
                {
                    Failures[key] = left - 1;
                    throw new InvalidOperationException($"{key} is unavailable");
                }
                return key switch
                {
                    "plan" => Plan(Interlocked.Increment(ref plans)),
                    "synthesis" => "Combined: " + chat.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters of answers. " +
                        (chat.Text.Contains("Not answered:", StringComparison.Ordinal) ? "Some angles were not answered." : "Every angle answered."),
                    _ => Replies.TryGetValue(key, out var scripted) ? scripted : $"Answer for {key}.",
                };
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }
    }

    private static JsonObjectValue Angles(params string[] titles) => JsonValue.Object([new("question", "Should we upgrade the framework?"),
        new("angles", JsonValue.Array(titles.Select(title => (JsonValue)JsonValue.Object([new("title", title)]))))]);

    private static async Task<long> StartResearchAsync(HostedSession session, JsonObjectValue input, JsonObjectValue? budget = null) =>
        (await session.StartExecutionAsync(1, "pisharp-research", input, budget, null)).TaskId.Value;

    private static List<JsonObjectValue> Branches(JsonObjectValue run, string phase) =>
        [.. Jobs.Items(run, "branches").OfType<JsonObjectValue>().Where(row => Jobs.String(row, "phase") == phase)];

    internal static IEnumerable<(string, Func<Task>)> ResearchCases()
    {
        yield return Case("research.fans-out-within-concurrency-with-distinct-models-and-publishes-partials", async () =>
        {
            var lab = new Lab();
            var holds = new[] { "Cost", "Risk", "Time" }.ToDictionary(key => key, lab.Hold);
            await using var device = await TestDevice.OpenAsync("research-fan", new DeviceSetup { Models = lab.Models() });
            var session = await device.CreateAsync();
            var input = Angles("Cost", "Risk", "Time").SetItem("concurrency", 2)
                .SetItem("research", JsonValue.Object([new("model", "faux/thinker"), new("thinking", "high")]))
                .SetItem("synthesis", JsonValue.Object([new("model", "faux/echo-small")]));
            var bad = input.SetItem("research", JsonValue.Object([new("model", "faux/echo"), new("thinking", "high")]));
            var refused = await ThrowsAsync<HostValidationException>(() => session.StartExecutionAsync(1, "pisharp-research", bad, null, null), "thinking needs a reasoning model");
            Check(refused.Fields.Any(field => field.Path == "/research/thinking"), "the field is named");
            var taskId = await StartResearchAsync(session, input);
            await WaitUntilAsync(() => Task.FromResult(lab.Seen.Count >= 2), "two angles running");
            await Task.Delay(300);
            Equal(2, lab.MaxRunning, "at most two at once");
            Equal(2, lab.Seen.Count, "the third waits for a slot");
            // Out of order: the second angle answers first, then the queued third, then the first.
            holds["Risk"].SetResult();
            await WaitUntilAsync(async () => (await session.ExecutionArtifactsAsync(taskId)).Items.Length == 1, "the first answer is published at once");
            Equal("Risk", Jobs.String((JsonObjectValue)(await session.ExecutionArtifactsAsync(taskId)).Items[0], "title"), "the answer that arrived");
            Equal("running", Jobs.String((await RunAsync(session, taskId))!, "status"), "while the run continues");
            holds["Time"].SetResult();
            await WaitUntilAsync(async () => (await session.ExecutionArtifactsAsync(taskId)).Items.Length == 2, "the queued angle took the free slot");
            holds["Cost"].SetResult();
            var run = await WaitForRunAsync(session, taskId, "completed");
            var combined = lab.Seen.Single(chat => Lab.KeyOf(chat.Text) == "synthesis").Text;
            Check(combined.IndexOf("## Angle a1: Cost", StringComparison.Ordinal) is var first and >= 0 && first < combined.IndexOf("## Angle a2: Risk", StringComparison.Ordinal)
                && combined.IndexOf("## Angle a2: Risk", StringComparison.Ordinal) < combined.IndexOf("## Angle a3: Time", StringComparison.Ordinal),
                "the synthesis reads the answers in plan order, not arrival order");
            Equal("passed", Jobs.String((JsonObjectValue)run["audit"], "status"), "the completion audit passed");
            var research = Branches(run, "research");
            Equal(3, research.Count, "one branch per angle");
            Equal(3, research.Select(row => Jobs.Long(row, "conversationId")).Distinct().Count(), "each in its own child");
            Check(research.All(row => Jobs.String(row, "model") == "faux/thinker" && Jobs.String(row, "thinking") == "high" && Jobs.String(row, "status") == "completed"),
                "the research settings");
            Check(research.All(row => Jobs.String(row, "artifactId") is not null), "each branch links its finding");
            Equal("faux/echo-small", Jobs.String(Branches(run, "synthesize")[0], "model"), "the synthesis model");
            Check(lab.Seen.Where(chat => Lab.KeyOf(chat.Text) is "Cost" or "Risk" or "Time").All(chat => chat.ModelId == "thinker" && chat.Reasoning == "high"),
                "the children asked the research model with thinking");
            Check(lab.Seen.Single(chat => Lab.KeyOf(chat.Text) == "synthesis") is { ModelId: "echo-small", Reasoning: null }, "the synthesis child");
            var artifacts = (await session.ExecutionArtifactsAsync(taskId)).Items.OfType<JsonObjectValue>().ToList();
            Equal(4, artifacts.Count, "three angle findings and the synthesis");
            Equal("Cost,Risk,Time", string.Join(",", artifacts.Where(item => ((JsonObjectValue)item["provenance"]).ContainsKey("angleId"))
                .Select(item => Jobs.String(item, "title")!).Order()), "a finding per angle");
            var result = (JsonObjectValue)run["result"];
            Check(Jobs.String(result, "synthesisHash")!.StartsWith("sha256:", StringComparison.Ordinal), "the synthesis input hash");
            Equal(0, Jobs.Items(result, "missing").Count(), "nothing missing");
            var reservations = device.Host.Budgets.ForRun(session.Id + "/" + taskId);
            Equal(4, reservations.Count, "an attempt reservation per child");
            Check(reservations.All(item => item.Status == "settled" && item.Actual!["requests"] == 1), "settled at what each child recorded");
        });

        yield return Case("research.plans-angles-repairs-an-invalid-plan-and-fails-a-second", async () =>
        {
            var lab = new Lab { Plan = call => call == 1 ? "I would look at cost and risk." : """[{"title":"Cost"},{"title":"Risk","prompt":"What could break?"}]""" };
            await using var device = await TestDevice.OpenAsync("research-plan", new DeviceSetup { Models = lab.Models() });
            var session = await device.CreateAsync();
            var taskId = await StartResearchAsync(session, JsonValue.Object([new("question", "Should we upgrade?")]));
            var run = await WaitForRunAsync(session, taskId, "completed");
            Equal("plan,plan-repair", string.Join(",", Branches(run, "plan").Select(row => Jobs.String(row, "id"))), "a repair after the invalid plan");
            Equal("a1,a2", string.Join(",", Branches(run, "research").Select(row => Jobs.String(row, "id"))), "the planned angles");
            Check(((JsonObjectValue)run["research"]).TryGetValue("planned", out var planned) && planned is JsonBooleanValue { Value: true }, "the plan is recorded as planned");
            Equal(1, lab.Asked("Risk"), "each planned angle asked once");

            lab.Plan = _ => """[{"title":""}]""";
            var failing = await StartResearchAsync(session, JsonValue.Object([new("question", "Again?")]));
            var failed = await WaitForRunAsync(session, failing, "failed");
            Check(Jobs.String(failed, "error")!.Contains("planner's angles were invalid", StringComparison.Ordinal), "the run says why: " + Jobs.String(failed, "error"));
            Equal(0, Branches(failed, "research").Count, "no angle was started");
        });

        yield return Case("research.retries-failed-angles-labels-missing-and-honours-require-all", async () =>
        {
            var lab = new Lab();
            lab.Failures["Risk"] = int.MaxValue;
            lab.Failures["Time"] = 1;
            await using var device = await TestDevice.OpenAsync("research-fail", new DeviceSetup { Models = lab.Models() });
            var session = await device.CreateAsync();
            var taskId = await StartResearchAsync(session, Angles("Cost", "Risk", "Time"));
            var run = await WaitForRunAsync(session, taskId, "completed");
            var rows = Branches(run, "research").ToDictionary(row => Jobs.String(row, "title")!);
            Equal("failed", Jobs.String(rows["Risk"], "status"), "a failing angle fails");
            Equal(2, Jobs.Items(rows["Risk"], "attempts").Count(), "after its attempts");
            Equal("completed", Jobs.String(rows["Time"], "status"), "a retry recovers");
            Equal(2, Jobs.Items(rows["Time"], "attempts").Count(), "on the second attempt");
            Check(Jobs.Items(rows["Time"], "attempts").OfType<JsonObjectValue>().Select(attempt => Jobs.String(attempt, "requestId")).Distinct().Count() == 2,
                "each attempt has its own identity");
            Equal("a2", string.Join(",", Jobs.Items((JsonObjectValue)run["result"], "missing").OfType<JsonStringValue>().Select(id => id.Value)), "the missing angle is labelled");
            Check(lab.Seen.Single(chat => Lab.KeyOf(chat.Text) == "synthesis").Text.Contains("Not answered: Risk", StringComparison.Ordinal), "the synthesis is told");
            Equal(3, (await session.ExecutionArtifactsAsync(taskId)).Items.Length, "two angle findings and the synthesis");

            var strict = await StartResearchAsync(session, Angles("Cost", "Risk").SetItem("requireAll", true).SetItem("maxAttempts", 1));
            var failed = await WaitForRunAsync(session, strict, "failed");
            Check(Jobs.String(failed, "error")!.Contains("Every angle had to be answered", StringComparison.Ordinal), "require-all fails the run");
            Equal(false, ((JsonObjectValue)((JsonObjectValue)failed["checks"])["research:answers"])["passed"] is JsonBooleanValue { Value: true }, "the answers check failed");

            lab.Failures["Cost"] = int.MaxValue;
            var none = await WaitForRunAsync(session, await StartResearchAsync(session, Angles("Cost", "Risk").SetItem("maxAttempts", 1)), "failed");
            Check(Jobs.String(none, "error")!.StartsWith("No research angle was answered", StringComparison.Ordinal), "no answers fails the run");
        });

        yield return Case("research.restart-rejoins-children-without-new-angles-or-lost-answers", async () =>
        {
            var lab = new Lab();
            var risk = lab.Hold("Risk");
            var setup = new DeviceSetup { Models = lab.Models() };
            await using var device = await TestDevice.OpenAsync("research-restart", setup);
            var session = await device.CreateAsync();
            var taskId = await StartResearchAsync(session, Angles("Cost", "Risk", "Time").SetItem("concurrency", 3));
            await WaitUntilAsync(async () => (await session.ExecutionArtifactsAsync(taskId)).Items.Length == 2, "two angles published");
            await WaitUntilAsync(() => Task.FromResult(lab.Asked("Risk") == 1), "the held angle is asking");
            var before = Branches((await RunAsync(session, taskId))!, "research");
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            var synthesis = lab.Hold("synthesis");
            risk.SetResult();
            await WaitUntilAsync(async () => Jobs.String((await RunAsync(session, taskId))!, "phase") == "synthesize" && lab.Asked("synthesis") >= 1, "synthesizing");
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            synthesis.SetResult();
            var run = await WaitForRunAsync(session, taskId, "completed");
            var after = Branches(run, "research");
            Equal(3, after.Count, "no new angles");
            Equal(string.Join(",", before.Select(row => Jobs.Long(row, "conversationId"))), string.Join(",", after.Select(row => Jobs.Long(row, "conversationId"))),
                "the same children");
            Equal(1, lab.Asked("Cost"), "a completed angle is not asked again");
            Equal(1, lab.Asked("Time"), "nor the other");
            Equal(4, (await session.ExecutionArtifactsAsync(taskId)).Items.Length, "one finding per angle and the synthesis");
            Check(Jobs.Long(run, "resumed") >= 1, "the run says it resumed");
            Check(Jobs.Items(after.Single(row => Jobs.String(row, "title") == "Risk"), "attempts").Count() == 1, "the interrupted attempt was rejoined, not repeated");
        });

        yield return Case("research.cancel-stops-children-and-marks-angles-cancelled", async () =>
        {
            var lab = new Lab();
            lab.Hold("Cost");
            lab.Hold("Risk");
            await using var device = await TestDevice.OpenAsync("research-cancel", new DeviceSetup { Models = lab.Models() });
            var session = await device.CreateAsync();
            var taskId = await StartResearchAsync(session, Angles("Cost", "Risk", "Time").SetItem("concurrency", 2));
            await WaitUntilAsync(() => Task.FromResult(lab.Seen.Count == 2), "two angles asking");
            await session.AbortTaskAsync(taskId);
            var run = await WaitForRunAsync(session, taskId, "aborted");
            var rows = Branches(run, "research");
            Check(rows.Count == 3 && rows.All(row => Jobs.String(row, "status") == "cancelled"), "every angle is cancelled: " + string.Join(", ", rows.Select(row => Jobs.String(row, "status"))));
            await Task.Delay(200);
            Equal(2, lab.Seen.Count, "the queued angle never started");
            var reservations = device.Host.Budgets.ForRun(session.Id + "/" + taskId);
            Equal(2, reservations.Count, "the two started attempts reserved budget");
            Check(reservations.All(item => item.Status == "uncertain" && item.Detail!.Contains("aborted", StringComparison.Ordinal)),
                "an interrupted attempt is left for someone to resolve, not counted as reserved forever: " + string.Join(", ", reservations.Select(item => item.Status)));
            Check(Branches(run, "research").SelectMany(row => Jobs.Items(row, "attempts").OfType<JsonObjectValue>()).All(attempt => Jobs.String(attempt, "status") == "cancelled"),
                "the attempts are cancelled");
        });

        yield return Case("research.deadlines-expire-attempts-and-budgets-limit-angles", async () =>
        {
            var lab = new Lab();
            lab.Hold("Cost");
            var clock = new JumpClock();
            await using var device = await TestDevice.OpenAsync("research-deadline", new DeviceSetup { Models = lab.Models(), Time = clock });
            var session = await device.CreateAsync();
            var taskId = await StartResearchAsync(session, Angles("Cost", "Risk").SetItem("maxAttempts", 1).SetItem("angleDeadlineSeconds", 30));
            await WaitUntilAsync(async () => Branches((await RunAsync(session, taskId))!, "research").Any(row => Jobs.String(row, "status") == "completed"), "one angle answered");
            clock.Advance(TimeSpan.FromSeconds(31));
            var run = await WaitForRunAsync(session, taskId, "completed");
            var cost = Branches(run, "research").Single(row => Jobs.String(row, "title") == "Cost");
            Equal("failed", Jobs.String(cost, "status"), "the held angle failed");
            Equal("expired", Jobs.String(Jobs.Items(cost, "attempts").OfType<JsonObjectValue>().Single(), "status"), "its attempt expired");

            var limited = await StartResearchAsync(session, Angles("Risk", "Time", "Scope").SetItem("concurrency", 1),
                JsonValue.Object([new("requests", 2)]));
            var failed = await WaitForRunAsync(session, limited, "failed");
            var scope = Branches(failed, "research").Single(row => Jobs.String(row, "title") == "Scope");
            Check(Jobs.String(scope, "error")!.StartsWith("limited:", StringComparison.Ordinal), "the third angle hit the run limit: " + Jobs.String(scope, "error"));
            Equal(0, lab.Asked("Scope"), "and was never asked");
            Check(Jobs.String(failed, "error")!.Contains("synthesis", StringComparison.Ordinal), "the synthesis is refused too: " + Jobs.String(failed, "error"));
            Check(device.Host.Budgets.ForRun(session.Id + "/" + limited).Sum(item => item.Actual?["requests"] ?? 0) <= 2, "the run stayed within its cap");
        });

        yield return Case("research.gate-judges-the-synthesis-and-waits-for-review", async () =>
        {
            var lab = new Lab();
            var judged = new ConcurrentQueue<JsonObjectValue>();
            var models = lab.Models((context, _) =>
            {
                judged.Enqueue((JsonObjectValue)((JsonObjectValue)context["state"])["input"]);
                return Task.FromResult(JsonValue.Object([new("label", JsonValue.Object([new("type", "choice"), new("choice", "publish"),
                    new("probabilities", JsonValue.Object([new("publish", 0.7), new("hold", 0.3)])), new("confidence", 0.7)]))]));
            });
            await using var device = await TestDevice.OpenAsync("research-gate", new DeviceSetup { Models = models });
            device.Host.Policies.Publish("publishable", (JsonObjectValue)JsonValue.Parse("""
                {"title":"Publishable","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Is the research ready to publish?",
                 "inputSchema":{"type":"object","additionalProperties":false,"required":["title"],"properties":{
                   "title":{"type":"string","maxLength":200},"summary":{"type":"string","maxLength":4000}}},
                 "labels":[{"id":"publish","description":"Ready"},{"id":"hold","description":"Not ready"}]}
                """));
            var session = await device.CreateAsync();
            var unknown = await ThrowsAsync<HostValidationException>(() => session.StartExecutionAsync(1, "pisharp-research",
                Angles("Cost").SetItem("policyId", "missing"), null, null), "an unknown policy");
            Check(unknown.Fields.Any(field => field.Path == "/policyId"), "the policy field is named");
            var versionOnly = await ThrowsAsync<HostValidationException>(() => session.StartExecutionAsync(1, "pisharp-research",
                Angles("Cost").SetItem("version", 1), null, null), "a version without a policy");
            Check(versionOnly.Fields.Any(field => field.Path == "/version"), "the version field is named");

            var taskId = await StartResearchAsync(session, Angles("Cost", "Risk").SetItem("policyId", "publishable"));
            var waiting = await WaitForRunAsync(session, taskId, "waiting");
            Equal(1L, Jobs.Long((JsonObjectValue)waiting["frozen"], "policyVersion"), "the policy version is frozen at admission");
            Equal("review", Jobs.String(waiting, "phase"), "waiting for review");
            var judgment = await session.JudgmentAsync(((JsonStringValue)Jobs.Items(waiting, "judgments").First()).Value);
            Equal("confirm", Jobs.String(judgment, "route"), "a middling confidence asks a person");
            var item = judged.Single();
            Equal("summary,title", string.Join(",", item.Keys.Order(StringComparer.Ordinal)), "the item carries only the policy's fields");
            Check(Jobs.String(item, "summary")!.StartsWith("Combined:", StringComparison.Ordinal), "the synthesis is what is judged");
            Equal(2, (await session.ExecutionArtifactsAsync(taskId)).Items.Length, "the angle findings are already kept; the synthesis is not");

            await session.DecideJobAsync(1, taskId, true);
            var run = await WaitForRunAsync(session, taskId, "completed");
            Equal("passed", Jobs.String((JsonObjectValue)run["audit"], "status"), "the completion audit passed");
            var synthesis = (await session.ExecutionArtifactsAsync(taskId)).Items.OfType<JsonObjectValue>()
                .Single(artifact => Jobs.String(artifact, "title")!.StartsWith("Research:", StringComparison.Ordinal));
            var provenance = (JsonObjectValue)synthesis["provenance"];
            Equal(Jobs.String(judgment, "id"), Jobs.String(provenance, "judgmentId"), "the synthesis names its judgment");
            Equal(1L, Jobs.Long(provenance, "policyVersion"), "and the policy version");
            Equal(Jobs.String((JsonObjectValue)run["result"], "synthesisHash"), Jobs.String(provenance, "synthesis"), "and the answers it read");

            var cancelled = await StartResearchAsync(session, Angles("Scope").SetItem("policyId", "publishable"));
            await WaitForRunAsync(session, cancelled, "waiting");
            await session.AbortTaskAsync(cancelled);
            var aborted = await WaitForRunAsync(session, cancelled, "aborted");
            Equal("aborted", Jobs.String(Jobs.Items(aborted, "phases").OfType<JsonObjectValue>().Single(phase => Jobs.String(phase, "id") == "review"), "status"),
                "cancelled during review");
            Equal(1, (await session.ExecutionArtifactsAsync(cancelled)).Items.Length, "the angle's finding stays; the synthesis is not published");
        });

        yield return Case("research.restart-during-planning-keeps-one-plan", async () =>
        {
            var lab = new Lab();
            var plan = lab.Hold("plan");
            var setup = new DeviceSetup { Models = lab.Models() };
            await using var device = await TestDevice.OpenAsync("research-plan-restart", setup);
            var session = await device.CreateAsync();
            var taskId = await StartResearchAsync(session, JsonValue.Object([new("question", "Should we upgrade?")]));
            await WaitUntilAsync(() => Task.FromResult(lab.Asked("plan") == 1), "the planner is asking");
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            plan.SetResult();
            var run = await WaitForRunAsync(session, taskId, "completed");
            var planned = Branches(run, "plan");
            Equal(1, planned.Count, "one planner child");
            Equal(1, Jobs.Items(planned[0], "attempts").Count(), "its interrupted attempt was rejoined");
            Equal("a1,a2", string.Join(",", Branches(run, "research").Select(row => Jobs.String(row, "id"))), "the planned angles, once");
            Equal(1, lab.Asked("Cost"), "each angle asked once");
            Equal(3, (await session.ExecutionArtifactsAsync(taskId)).Items.Length, "one finding per angle and the synthesis");
        });
    }
}
