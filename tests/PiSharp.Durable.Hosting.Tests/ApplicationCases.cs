using System.Reflection;
using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

// Decision 0010, M2: storage foundation, budgets, judgment policies, judgments and artifacts.
internal static partial class Program
{
    private static JsonObjectValue Json(string json) => (JsonObjectValue)JsonValue.Parse(json);

    /// <summary>A classifier policy over <c>{title, confidence}</c>: the faux classifier answers with the item's own confidence.</summary>
    private static JsonObjectValue RelevancePolicy(string extra = "") => Json("""
        {"title":"Relevance","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Is the release relevant?",
         "inputSchema":{"type":"object","additionalProperties":false,"required":["title","confidence"],"properties":{
           "title":{"type":"string","maxLength":200},"confidence":{"type":"number","minimum":0,"maximum":1}}},
         "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}],
         "scores":[{"id":"impact","instructions":"How large is the change?","levels":["none","minor","major"],"weight":2}]
        """ + extra + "}");

    /// <summary>Faux models whose classifier labels every item <c>relevant</c> with the item's confidence and a major impact.</summary>
    private static FauxDurableModels Classifying(JsonObjectValue? usage = null) => new()
    {
        Classify = (context, _) =>
        {
            var input = (JsonObjectValue)((JsonObjectValue)context["state"])["input"];
            var confidence = input["confidence"];
            return Task.FromResult(JsonValue.Object(
            [
                new("label", JsonValue.Object([new("type", "choice"), new("choice", "relevant"),
                    new("probabilities", JsonValue.Object([new("relevant", confidence), new("ignore", 1 - ((JsonNumberValue)confidence).Value)])),
                    new("confidence", confidence)])),
                new("score_impact", JsonValue.Object([new("type", "score"), new("score", 2), new("confidence", 0.99)])),
            ]));
        },
        ClassifierUsage = usage,
    };

    private static JsonObjectValue JudgeInput(double confidence, string title = "v2.0.0") =>
        JsonValue.Object([new("policyId", "relevance"), new("input", JsonValue.Object([new("title", title), new("confidence", confidence)]))]);

    private static async Task<JsonObjectValue> JudgmentOfAsync(HostedSession session, long taskId)
    {
        var run = (await RunAsync(session, taskId))!;
        var id = ((JsonStringValue)Jobs.Items(run, "judgments").First()).Value;
        return await session.JudgmentAsync(id);
    }

    internal static IEnumerable<(string, Func<Task>)> ApplicationCases()
    {
        yield return Case("application.catalog-upgrades-from-each-version-with-a-backup", async () =>
        {
            foreach (var version in new[] { 1, 2, 3, 4, 5, 6 })
            {
                var directory = Temp("catalog-v" + version);
                var path = Path.Join(directory, "catalog.sqlite");
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
                {
                    connection.Open();
                    foreach (var name in new[] { "Schema", "SchemaV2", "SchemaV3", "SchemaV4", "SchemaV5", "SchemaV6" }.Take(version))
                    {
                        using var step = connection.CreateCommand();
                        step.CommandText = (string)typeof(SessionCatalog)
                            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                        step.ExecuteNonQuery();
                    }
                    using var stamp = connection.CreateCommand();
                    stamp.CommandText = $"INSERT INTO meta (key, value) VALUES ('device_uid', 'old-{version}'); PRAGMA user_version = {version};";
                    stamp.ExecuteNonQuery();
                }
                using (var catalog = SessionCatalog.Open(path))
                {
                    Equal("old-" + version, catalog.DeviceUid(), "the device identity survives the upgrade");
                    Equal(0L, (long)catalog.Value(null, "SELECT COUNT(*) FROM operations;")!, "v5 tables exist");
                    Equal(0L, (long)catalog.Value(null, "SELECT COUNT(*) FROM sources;")!, "v6 tables exist");
                    Equal(0L, (long)catalog.Value(null, "SELECT COUNT(*) FROM feedback_events;")!, "v7 tables exist");
                }
                var backup = Path.Join(directory, $"catalog.v{version}.bak.sqlite");
                Check(File.Exists(backup), $"a backup of v{version} is kept");
                using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
                copy.Open();
                using var read = copy.CreateCommand();
                read.CommandText = "PRAGMA user_version;";
                Equal((long)version, (long)read.ExecuteScalar()!, "the backup keeps the old version");
                using (SessionCatalog.Open(path)) { }
                Check(!File.Exists(Path.Join(directory, $"catalog.v7.bak.sqlite")), "a current catalog is not backed up again");
            }
            var newer = Path.Join(Temp("catalog-newer"), "catalog.sqlite");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = newer, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 99;";
                command.ExecuteNonQuery();
            }
            Throws<InvalidDataException>(() => SessionCatalog.Open(newer), "a newer catalog is refused");
        });

        yield return Case("application.typed-inputs-validate-and-numeric-jobs-stay-unchanged", async () =>
        {
            var typed = new JobDefinition
            {
                Id = "typed", Title = "Typed", Summary = "A typed job",
                InputSchema = Json("""
                    {"type":"object","additionalProperties":false,"required":["repo"],"properties":{
                      "repo":{"type":"string","maxLength":100,"pattern":"^[a-z0-9-]+/[a-z0-9-]+$"},
                      "limit":{"type":"integer","minimum":1,"maximum":50,"default":10},
                      "token":{"type":"string","maxLength":120,"format":"secret-ref"}}}
                    """),
                InputSchemaVersion = 3,
                Phases = [new JobPhase("work", "Work", "Echo") { Run = context => Task.FromResult(JobPhaseResult.Next("ok", context.Input)) }],
            };
            await using var device = await TestDevice.OpenAsync("typed-inputs", new DeviceSetup { Jobs = [.. SampleJobs.All, typed] });
            var session = await device.CreateAsync();
            var invalid = await ThrowsAsync<HostValidationException>(() => session.RunJobAsync(1, "typed",
                Json("""{"repo":"Not Valid","limit":99,"extra":1,"token":"hunter2"}""")), "invalid typed input");
            var paths = invalid.Fields.Select(field => field.Path).ToHashSet();
            foreach (var path in new[] { "/repo", "/limit", "/extra", "/token" }) Check(paths.Contains(path), "field error at " + path + ": " + invalid.Message);
            var task = await session.RunJobAsync(1, "typed", Json("""{"repo":"pisharp/pisharp","token":"secret:github"}"""));
            var run = await WaitForRunAsync(session, task.Value, "completed");
            Equal(10L, Jobs.Long((JsonObjectValue)run["input"], "limit"), "default filled in");
            Equal(3L, Jobs.Long(run, "inputSchemaVersion"), "schema version recorded");
            Equal(CanonicalJson.Hash((JsonObjectValue)run["input"]), Jobs.String(run, "inputHash"), "input hash recorded");
            var big = JsonValue.Object([new("repo", "a/b"), new("token", "secret:" + new string('x', 90))]);
            await ThrowsAsync<HostTooLargeException>(() => session.RunJobAsync(1, "typed",
                big.SetItem("pad", new string('p', 70_000))), "an input over MaxInputBytes");
            // A numeric job keeps clamping and its old JSON shape.
            var numeric = await session.RunJobAsync(1, SampleJobs.All[0].Id, Json("""{"steps":1000}"""));
            var numericRun = await RunAsync(session, numeric.Value);
            Equal(JsonValue.Null, numericRun!["inputSchemaVersion"], "numeric jobs have no schema version");
            Check(typed.ToJson()["inputSchema"] is JsonObjectValue, "definitions publish their schema");
            Equal(JsonValue.Null, SampleJobs.All[0].ToJson()["inputSchema"], "numeric definitions have none");
            // A typed schedule keeps its normalized object input.
            var schedule = await device.Host.CreateScheduleAsync(session.Id, 1, "typed", 5, Json("""{"repo":"a/b"}"""));
            Equal(10L, Jobs.Long(schedule.Input, "limit"), "schedules normalize typed input");
            await ThrowsAsync<HostValidationException>(() => device.Host.CreateScheduleAsync(session.Id, 1, "typed", 5, Json("""{"repo":1}""")), "schedules validate");
            Throws<ArgumentException>(() => DurableDeviceHost.OpenAsync(device.Options(new DeviceSetup
            {
                Jobs = [typed with { Id = "broken", InputSchema = Json("""{"type":"object","properties":{"x":{"type":"string"}}}""") }],
            })).GetAwaiter().GetResult(), "a string without maxLength is refused at open");
            Throws<ArgumentException>(() => DurableDeviceHost.OpenAsync(device.Options(new DeviceSetup { Jobs = [typed with { Id = "pisharp-mine" }] }))
                .GetAwaiter().GetResult(), "application IDs are reserved");
        });

        yield return Case("application.operation-receipts-and-execution-starts-are-idempotent", async () =>
        {
            await using var device = await TestDevice.OpenAsync("operations", new DeviceSetup { Jobs = [CheckedJob()] });
            var catalog = device.Host.Catalog;
            var applied = 0;
            var first = catalog.RunOperation("key-1", "test", "sha256:a", 1, _ => { applied++; return "result"; });
            var again = catalog.RunOperation("key-1", "test", "sha256:a", 2, _ => { applied++; return "other"; });
            Check(!first.Replayed && again.Replayed && again.Result == "result" && applied == 1, "same key and request replays");
            Throws<HostConflictException>(() => catalog.RunOperation("key-1", "test", "sha256:b", 3, _ => "x"), "same key, different request");
            var session = await device.CreateAsync();
            var starts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                session.StartExecutionAsync(1, "checked", null, Json("""{"requests":5}"""), "start-1")));
            Equal(1, starts.Select(start => start.TaskId).Distinct().Count(), "concurrent duplicates start one run");
            Equal(4, starts.Count(start => start.Replayed), "the others replay");
            await ThrowsAsync<HostConflictException>(() => session.StartExecutionAsync(1, "checked", null, Json("""{"requests":6}"""), "start-1"),
                "a different body under the key");
            var run = await WaitForRunAsync(session, starts[0].TaskId.Value, "completed");
            Equal(5L, Jobs.Long((JsonObjectValue)run["budget"], "requests"), "the run's limits are frozen in its record");
            await ThrowsAsync<HostValidationException>(() => session.StartExecutionAsync(1, "checked", null, Json("""{"bogus":1}"""), null), "unknown meter");
        });

        yield return Case("application.blob-store-dedupes-keeps-references-and-collects-orphans", async () =>
        {
            await using var device = await TestDevice.OpenAsync("blobs");
            var blobs = device.Host.Blobs;
            var bytes = System.Text.Encoding.UTF8.GetBytes("<rss>body</rss>");
            var one = blobs.Put(bytes, "application/rss+xml", "owner-1");
            var two = blobs.Put(bytes, "application/rss+xml", "owner-2");
            Equal(one.Hash, two.Hash, "content addressed");
            Equal(2, blobs.References(one.Hash), "two owners");
            Equal("<rss>body</rss>", System.Text.Encoding.UTF8.GetString(blobs.Read(one.Hash)!), "read back");
            blobs.RemoveReferences("owner-1");
            Equal(0, blobs.Collect(TimeSpan.Zero).Blobs, "a referenced blob is never collected");
            blobs.RemoveReferences("owner-2");
            var dry = blobs.Collect(TimeSpan.FromHours(1), dryRun: true);
            Equal(0, dry.Blobs, "the grace period protects a new blob");
            var orphan = Path.Join(blobs.Root, "sha256", "ab", "ab" + new string('0', 62));
            Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
            await File.WriteAllTextAsync(orphan, "orphan");
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
            Directory.CreateDirectory(Path.Join(blobs.Root, "tmp"));
            var temporary = Path.Join(blobs.Root, "tmp", "stale.tmp");
            await File.WriteAllTextAsync(temporary, "partial");
            File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddDays(-2));
            var collected = blobs.Collect(TimeSpan.Zero);
            Equal(1, collected.Blobs, "the unreferenced blob");
            Equal(1, collected.OrphanFiles, "the orphan file");
            Equal(1, collected.TemporaryFiles, "the stale temporary file");
            Equal<byte[]?>(null, blobs.Read(one.Hash), "gone");
            await ThrowsAsync<HostTooLargeException>(() => Task.Run(() => blobs.Put(new byte[BlobStore.MaxBlobBytes + 1], "x/y", "o")), "size limit");
        });

        yield return Case("application.budgets-cap-parallel-reservations-and-keep-uncertain-costs", async () =>
        {
            await using var device = await TestDevice.OpenAsync("budgets");
            var budgets = device.Host.Budgets;
            var limits = budgets.SetLimits(new Dictionary<string, long?> { ["tokens"] = 100, ["money:USD"] = 1_000_000 }, 0);
            Equal(1L, limits.Revision, "revisioned");
            Throws<HostConflictException>(() => budgets.SetLimits(new Dictionary<string, long?> { ["tokens"] = 5 }, 0), "stale revision");
            var amount = new Dictionary<string, long> { ["requests"] = 1, ["tokens"] = 30, ["money:USD"] = 1000 };
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
            {
                try { budgets.Reserve($"op-{index}", "s/1", "faux", amount); return true; }
                catch (HostLimitException) { return false; }
            })));
            Equal(3, results.Count(ok => ok), "only three 30-token reservations fit 100 tokens");
            var reserved = Enumerable.Range(0, 10).Select(index => budgets.Get($"op-{index}")).Where(item => item is not null).Select(item => item!.OperationId).ToList();
            budgets.Settle(reserved[0], new Dictionary<string, long> { ["requests"] = 1, ["tokens"] = 5, ["money:USD"] = 10 });
            Equal(reserved[0], budgets.Settle(reserved[0], new Dictionary<string, long> { ["requests"] = 1, ["tokens"] = 5, ["money:USD"] = 10 }).OperationId,
                "settling twice the same way is a no-op");
            Throws<HostConflictException>(() => budgets.Settle(reserved[0], new Dictionary<string, long> { ["tokens"] = 6 }), "a different settlement");
            budgets.Reserve("op-after-settle", "s/1", "faux", amount);
            budgets.MarkUncertain(reserved[1], "restart during call");
            Throws<HostConflictException>(() => budgets.Release(reserved[1]), "uncertain needs reconciliation");
            Throws<HostLimitException>(() => budgets.Reserve("op-more", "s/2", "faux", amount), "the uncertain reservation still counts");
            budgets.Resolve(reserved[1], "released", null, "provider says no charge");
            budgets.Reserve("op-more", "s/2", "faux", amount);
            Equal(reserved[1], budgets.Reserve(reserved[1], "s/1", "faux", amount).OperationId, "a repeated reservation returns the existing one");
            Throws<HostConflictException>(() => budgets.Reserve(reserved[1], "s/1", "faux", new Dictionary<string, long> { ["tokens"] = 1 }), "different amounts");
            Throws<HostLimitException>(() => budgets.Reserve("op-unpriced", "s/3", "faux", new Dictionary<string, long> { ["tokens"] = 1 }),
                "an unknown price under a money limit");
            Throws<HostLimitException>(() => budgets.Reserve("op-run", "s/4", "faux", new Dictionary<string, long> { ["requests"] = 2, ["money:USD"] = 1 },
                new Dictionary<string, long> { ["requests"] = 1 }), "the run's own limit");
            budgets.Settle("op-more", new Dictionary<string, long> { ["requests"] = 1, ["tokens"] = 30 }, costKnown: false);
            var usage = budgets.Usage("s/2");
            Equal(false, ((JsonBooleanValue)usage["costKnown"]).Value, "unknown cost stays visible");
            Equal(30L, Jobs.Long((JsonObjectValue)usage["totals"], "tokens"), "settled totals");
            Throws<HostValidationException>(() => BudgetLedger.Amounts(Json("""{"money:usd":1}""")), "currencies are upper case");
        });

        yield return Case("application.policies-validate-publish-and-activate", async () =>
        {
            await using var device = await TestDevice.OpenAsync("policies", new DeviceSetup { Models = Classifying() });
            var policies = device.Host.Policies;
            var invalid = Throws<HostValidationException>(() => policies.Publish("relevance", Json("""
                {"title":"","kind":"classifier","model":{"provider":"faux","id":"missing"},"instructions":"x",
                 "labels":[{"id":"a"},{"id":"a"},{"id":"Bad Label"}],"thresholds":{"auto":0.5,"confirm":0.8},
                 "scores":[{"id":"s","instructions":"x","levels":["only"],"weight":-1}],"surprise":true}
                """)), "invalid policy");
            var paths = invalid.Fields.Select(field => field.Path).ToHashSet();
            foreach (var path in new[] { "/title", "/model", "/labels/1/id", "/labels/2/id", "/thresholds/confirm", "/scores/0/levels", "/scores/0/weight", "/surprise" })
                Check(paths.Contains(path), $"error at {path}: {invalid.Message}");
            Equal(0, policies.List().Count, "nothing published");
            var (v1, _) = policies.Publish("relevance", RelevancePolicy());
            Equal(1, v1.Version, "first version");
            Equal(1.0, ((JsonNumberValue)((JsonObjectValue)((JsonArrayValue)v1.Spec["scores"])[0])["weight"]).Value, "weights normalized");
            Equal(1, policies.Get("relevance")!.ActiveVersion, "the first version is active");
            Equal(1, policies.Publish("relevance", RelevancePolicy()).Version.Version, "an identical spec is the same version");
            var (v2, _) = policies.Publish("relevance", RelevancePolicy(""","thresholds":{"auto":0.8,"confirm":0.4}"""));
            Equal(2, v2.Version, "a change is a new version");
            Equal(1, policies.Get("relevance")!.ActiveVersion, "publishing does not activate");
            Throws<HostConflictException>(() => policies.Activate("relevance", 2, 0), "stale pointer revision");
            Equal(2, policies.Activate("relevance", 2, 1).ActiveVersion, "activated");
            Equal(2L, policies.Get("relevance")!.Revision, "pointer revision advanced");
            var (keyed, replayed) = policies.Publish("other", RelevancePolicy(), "publish-key");
            Check(!replayed && policies.Publish("other", RelevancePolicy(), "publish-key").Replayed, "a keyed publish replays");
            Throws<HostConflictException>(() => policies.Publish("other", RelevancePolicy(""","boolConfidence":"margin" """), "publish-key"), "key reuse");
            Equal(keyed.Hash, policies.Version("other", 1)!.Hash, "versions are immutable");
            Throws<HostInvalidException>(() => policies.Publish("Bad Id", RelevancePolicy()), "policy IDs");
        });

        yield return Case("application.judge-auto-route-caches-records-finding-and-usage", async () =>
        {
            var models = Classifying(Json("""{"input":40,"output":2,"totalTokens":42,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0.000012}}"""));
            await using var device = await TestDevice.OpenAsync("judge-auto", new DeviceSetup { Models = models });
            device.Host.Policies.Publish("relevance", RelevancePolicy());
            var session = await device.CreateAsync();
            var start = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.95), null, null);
            var run = await WaitForRunAsync(session, start.TaskId.Value, "completed");
            Equal("passed", Jobs.String((JsonObjectValue)run["audit"], "status"), "audit passed");
            Equal(1L, Jobs.Long((JsonObjectValue)run["frozen"], "policyVersion"), "the policy version is frozen at admission");
            var judgment = await JudgmentOfAsync(session, start.TaskId.Value);
            Equal("judged", Jobs.String(judgment, "status"), "judged");
            Equal("auto", Jobs.String(judgment, "route"), "auto route");
            Equal("approved", Jobs.String(judgment, "decision"), "the policy approved it");
            Equal(1.0, ((JsonNumberValue)((JsonObjectValue)judgment["result"])["score"]).Value, "weighted score of a major impact");
            Equal("bypassed", Jobs.String(Jobs.Items(run, "phases").OfType<JsonObjectValue>().Single(phase => Jobs.String(phase, "id") == "review"), "status"),
                "no review needed");
            var artifacts = await session.ExecutionArtifactsAsync(start.TaskId.Value);
            Equal(1, artifacts.Count, "one finding");
            var finding = (JsonObjectValue)artifacts[0];
            Equal("finding", Jobs.String(finding, "kind"), "finding kind");
            Equal(Jobs.String(judgment, "id"), Jobs.String((JsonObjectValue)finding["provenance"], "judgmentId"), "provenance");
            var usage = await session.ExecutionUsageAsync(start.TaskId.Value);
            Equal(true, ((JsonBooleanValue)usage["costKnown"]).Value, "reported usage is known");
            Equal(42L, Jobs.Long((JsonObjectValue)usage["totals"], "tokens"), "settled tokens");
            Equal(12L, Jobs.Long((JsonObjectValue)usage["totals"], "money:USD"), "settled micro-dollars");
            Equal(1, models.ClassifyCalls, "one classification");
            var cached = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.95), null, null);
            await WaitForRunAsync(session, cached.TaskId.Value, "completed");
            Equal("hit", Jobs.String(await JudgmentOfAsync(session, cached.TaskId.Value), "cache"), "the same item hits the cache");
            Equal(1, models.ClassifyCalls, "no second call");
            device.Host.Policies.Publish("relevance", RelevancePolicy(""","thresholds":{"auto":0.9,"confirm":0.3}"""));
            device.Host.Policies.Activate("relevance", 2, 1);
            var changed = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.95), null, null);
            await WaitForRunAsync(session, changed.TaskId.Value, "completed");
            Equal("miss", Jobs.String(await JudgmentOfAsync(session, changed.TaskId.Value), "cache"), "a new policy version misses the cache");
            Equal(2, models.ClassifyCalls, "evaluated again");
            await ThrowsAsync<HostValidationException>(() => session.StartExecutionAsync(1, "pisharp-judge",
                Json("""{"policyId":"relevance","input":{"title":"x"}}"""), null, null), "the item is checked against the policy's schema at admission");
        });

        yield return Case("application.judge-human-route-survives-restart-and-records-decisions", async () =>
        {
            var setup = new DeviceSetup { Models = Classifying() };
            await using var device = await TestDevice.OpenAsync("judge-human", setup);
            device.Host.Policies.Publish("relevance", RelevancePolicy());
            var session = await device.CreateAsync();
            var start = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.3), null, null);
            await WaitForRunAsync(session, start.TaskId.Value, "waiting");
            var judgment = await JudgmentOfAsync(session, start.TaskId.Value);
            Equal("human", Jobs.String(judgment, "route"), "low confidence routes to a person");
            Equal("pending", Jobs.String(judgment, "decision"), "waiting for a person");
            await device.ReopenAsync(setup);
            session = device.Host.ResolveSession(session.Id);
            await WaitForRunAsync(session, start.TaskId.Value, "waiting");
            var id = Jobs.String(judgment, "id")!;
            await ThrowsAsync<HostInvalidException>(() => session.DecideJudgmentAsync(id, "maybe", "device-admin"), "choices");
            var decided = await session.DecideJudgmentAsync(id, "approve", "device-admin", "Checked the changelog");
            Equal("approved", Jobs.String(decided, "decision"), "approved");
            await WaitForRunAsync(session, start.TaskId.Value, "completed");
            judgment = await session.JudgmentAsync(id);
            var decisions = Jobs.Items(judgment, "decisions").OfType<JsonObjectValue>().ToList();
            Equal(1, decisions.Count, "one decision appended");
            Equal("device-admin", Jobs.String(decisions[0], "actor"), "actor recorded");
            Equal("Checked the changelog", Jobs.String(decisions[0], "reason"), "reason recorded");
            Equal("relevant", Jobs.String((JsonObjectValue)judgment["result"], "label"), "the model's result is unchanged");
            Equal("approved", Jobs.String(await session.DecideJudgmentAsync(id, "approve", "device-admin"), "decision"), "repeating is a no-op");
            await ThrowsAsync<HostConflictException>(() => session.DecideJudgmentAsync(id, "reject", "device-admin"), "the opposite choice conflicts");
        });

        yield return Case("application.judge-confirm-rejects-and-review-expires", async () =>
        {
            var clock = new JumpClock();
            await using var device = await TestDevice.OpenAsync("judge-confirm", new DeviceSetup { Models = Classifying(), Time = clock });
            device.Host.Policies.Publish("relevance", RelevancePolicy());
            device.Host.Policies.Publish("timed", RelevancePolicy(""","reviewTimeoutHours":2"""));
            var session = await device.CreateAsync();
            var start = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.6), null, null);
            await WaitForRunAsync(session, start.TaskId.Value, "waiting");
            var judgment = await JudgmentOfAsync(session, start.TaskId.Value);
            Equal("confirm", Jobs.String(judgment, "route"), "medium confidence asks for confirmation");
            await session.DecideJudgmentAsync(Jobs.String(judgment, "id")!, "reject", "device-admin", "Not ours");
            var run = await WaitForRunAsync(session, start.TaskId.Value, "declined");
            Equal("rejected", Jobs.String(await session.JudgmentAsync(Jobs.String(judgment, "id")!), "decision"), "rejected");
            Equal(0, (await session.ExecutionArtifactsAsync(start.TaskId.Value)).Count, "no finding for a rejected item");

            var timed = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.6).SetItem("policyId", "timed"), null, null);
            await WaitForRunAsync(session, timed.TaskId.Value, "waiting");
            clock.Advance(TimeSpan.FromHours(3));
            run = await WaitForRunAsync(session, timed.TaskId.Value, "declined");
            Equal("expired", Jobs.String((JsonObjectValue)run["approval"], "status"), "the review expired");
            var expired = await JudgmentOfAsync(session, timed.TaskId.Value);
            Equal("expired", Jobs.String(expired, "decision"), "never approved by a timeout");
            await ThrowsAsync<HostConflictException>(() => session.DecideJudgmentAsync(Jobs.String(expired, "id")!, "approve", "device-admin"), "a late decision");
        });

        yield return Case("application.judge-budget-limits-and-uncertain-calls-stay-visible", async () =>
        {
            var gate = new TaskCompletionSource();
            var calls = 0;
            var blocking = new FauxDurableModels
            {
                Classify = async (context, token) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) await gate.Task.WaitAsync(token);
                    return await Classifying().Classify!(context, token);
                },
            };
            await using var device = await TestDevice.OpenAsync("judge-budget", new DeviceSetup { Models = blocking });
            device.Host.Policies.Publish("relevance", RelevancePolicy());
            var session = await device.CreateAsync();
            var limited = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.95), Json("""{"tokens":10}"""), null);
            var failed = await WaitForRunAsync(session, limited.TaskId.Value, "failed");
            Check(Jobs.String(failed, "error")!.Contains("limit", StringComparison.Ordinal), "the run failed on its limit: " + Jobs.String(failed, "error"));
            Equal("limited", Jobs.String(await JudgmentOfAsync(session, limited.TaskId.Value), "status"), "limited judgment");
            Equal(0, calls, "no provider call");

            var start = await session.StartExecutionAsync(1, "pisharp-judge", JudgeInput(0.95), null, null);
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) == 1), "the first call is in flight");
            await device.Host.DisposeAsync();
            gate.SetResult();
            await device.ReopenAsync(new DeviceSetup { Models = Classifying() });
            session = device.Host.ResolveSession(session.Id);
            await WaitForRunAsync(session, start.TaskId.Value, "completed");
            var judgment = await JudgmentOfAsync(session, start.TaskId.Value);
            var attempts = Jobs.Items(judgment, "attempts").OfType<JsonObjectValue>().ToList();
            Equal(2, attempts.Count, "a new attempt after the restart");
            Equal("uncertain", Jobs.String(attempts[0], "status"), "the interrupted attempt is uncertain");
            var usage = await session.ExecutionUsageAsync(start.TaskId.Value);
            Equal(1L, Jobs.Long(usage, "uncertain"), "the uncertain reservation stays in the ledger");
            Equal(false, ((JsonBooleanValue)usage["costKnown"]).Value, "so the run's cost is not known");
        });

        yield return Case("application.artifacts-validate-kinds-evidence-actions-and-export", async () =>
        {
            JsonObjectValue? saved = null;
            HostValidationException? comparisonError = null, evidenceError = null, actionError = null, kindError = null;
            var cards = new JobDefinition
            {
                Id = "cards", Title = "Cards", Summary = "Saves artifacts", RequiredChecks = ["artifact:comparison"],
                Phases = [new JobPhase("save", "Save", "Saves") { Run = async context =>
                {
                    var evidence = await context.SaveEvidenceAsync(new EvidenceDraft("Release notes", "https://example.com/notes", new string('n', 5000)));
                    var again = await context.SaveEvidenceAsync(new EvidenceDraft("Release notes", "https://example.com/notes", new string('n', 5000)));
                    Check(Jobs.String(evidence, "id") == Jobs.String(again, "id"), "the same evidence is one record");
                    var id = Jobs.String(evidence, "id")!;
                    saved = await context.SaveArtifactAsync("comparison", "<script>alert(1)</script>", Json("""
                        {"columns":["Old","New"],"rows":[{"label":"API","values":["v1","v2 secret:token"]}],"recommendation":"Upgrade"}
                        """), [id]);
                    comparisonError = await ThrowsAsync<HostValidationException>(() => context.SaveArtifactAsync("comparison", "Bad",
                        Json("""{"columns":["A","B"],"rows":[{"label":"x","values":["only one"]}]}""")), "row width");
                    evidenceError = await ThrowsAsync<HostValidationException>(() => context.SaveArtifactAsync("evidence-list", "Foreign",
                        Json("""{"items":[{"evidenceId":"ev_foreign"}]}"""), ["ev_foreign"]), "foreign evidence");
                    actionError = await ThrowsAsync<HostValidationException>(() => context.SaveArtifactAsync("action-request", "Run",
                        Json("""{"summary":"Delete everything","action":"rm-rf"}""")), "unregistered action");
                    kindError = await ThrowsAsync<HostValidationException>(() => context.SaveArtifactAsync("html", "Raw", Json("""{"html":"<b>x</b>"}""")), "unknown kind");
                    await context.SaveArtifactAsync("timeline", "Releases", Json("""{"events":[{"at":1700000000000,"title":"v2","evidenceId":"__ID__"}]}"""
                        .Replace("__ID__", id, StringComparison.Ordinal)), [id]);
                    return JobPhaseResult.Next("saved");
                }}],
            };
            await using var device = await TestDevice.OpenAsync("artifacts", new DeviceSetup { Jobs = [cards] });
            var session = await device.CreateAsync();
            var task = await session.RunJobAsync(1, "cards");
            var run = await WaitForRunAsync(session, task.Value, "completed");
            Equal("passed", Jobs.String((JsonObjectValue)run["audit"], "status"), "artifact check passed");
            Check(comparisonError!.Fields.Any(field => field.Path == "/props/rows/0/values"), "row width path");
            Check(evidenceError!.Fields.Any(field => field.Path == "/evidenceIds/0"), "evidence ownership path");
            Check(actionError!.Fields.Any(field => field.Path == "/props/action"), "action path");
            Check(kindError!.Fields.Any(field => field.Path == "/kind"), "kind path");
            var errors = Jobs.Items(Jobs.Items(run, "phases").OfType<JsonObjectValue>().Single(), "steps").OfType<JsonObjectValue>()
                .Count(step => Jobs.String(step, "kind") == "artifact" && Jobs.String(step, "status") == "error");
            Equal(4, errors, "invalid output stays in the run's history");
            var artifact = await session.ArtifactAsync(Jobs.String(saved!, "id")!);
            Equal("<script>alert(1)</script>", Jobs.String(artifact, "title"), "stored as text, never as markup");
            Equal(2, (await session.ExecutionArtifactsAsync(task.Value)).Count, "two valid artifacts");
            var evidence = await session.EvidenceAsync(((JsonStringValue)((JsonArrayValue)artifact["evidenceIds"])[0]).Value);
            Check(Jobs.String(evidence, "blobHash") is not null, "a long body goes to the blob store");
            Equal(5000, (await session.EvidenceTextAsync(Jobs.String(evidence, "id")!)).Length, "the full text reads back");
            var export = Artifacts.Export(artifact, [evidence]);
            Check(!export.ToJson().Contains("secret:token", StringComparison.Ordinal), "export redacts secret references");
            Check(Artifacts.ToText(artifact).Contains("| API | v1 |", StringComparison.Ordinal), "text fallback renders the table");
            var records = await session.RecordsAsync("artifacts", 1);
            Equal(1, ((JsonArrayValue)records["items"]).Count, "paged");
            Check(records["next"] is JsonStringValue, "more pages");
            await ThrowsAsync<HostNotFoundException>(() => session.ArtifactAsync("../etc"), "record IDs are checked");
        });

        yield return Case("application.structured-judgment-repairs-invalid-output-once", async () =>
        {
            var repaired = 0;
            var models = new FauxDurableModels
            {
                // The first answer is prose; the repair prompt (which quotes it) gets JSON, unless the item asks to stay broken.
                Respond = asked =>
                {
                    if (!asked.StartsWith("Item to judge", StringComparison.Ordinal)) return null;
                    if (!asked.Contains("A previous answer", StringComparison.Ordinal) || asked.Contains("stubborn", StringComparison.Ordinal)) return "I think it is relevant.";
                    Interlocked.Increment(ref repaired);
                    return """{"label":"relevant","confidence":0.97,"rationale":"Breaking change."}""";
                },
            };
            await using var device = await TestDevice.OpenAsync("judge-repair", new DeviceSetup { Models = models });
            device.Host.Policies.Publish("structured", Json("""
                {"title":"Structured","kind":"structured","model":{"provider":"faux","id":"echo"},"instructions":"Is it relevant?",
                 "inputSchema":{"type":"object","additionalProperties":false,"required":["title"],"properties":{"title":{"type":"string","maxLength":200}}},
                 "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}],"cacheTtlSeconds":0}
                """));
            var session = await device.CreateAsync();
            JsonObjectValue Input(string title) => JsonValue.Object([new("policyId", "structured"), new("input", JsonValue.Object([new("title", title)]))]);

            var start = await session.StartExecutionAsync(1, "pisharp-judge", Input("v2.0"), null, null);
            await WaitForRunAsync(session, start.TaskId.Value, "completed");
            var judgment = await JudgmentOfAsync(session, start.TaskId.Value);
            Equal("judged", Jobs.String(judgment, "status"), "the repair produced a result");
            Equal(1, repaired, "one repair call");
            var attempts = Jobs.Items(judgment, "attempts").OfType<JsonObjectValue>().ToList();
            Equal(2, attempts.Count, "the invalid answer and its repair");
            Equal("invalid", Jobs.String(attempts[0], "status"), "the first answer was invalid");
            Check(Jobs.String((JsonObjectValue)attempts[0]["repair"], "answer") == "I think it is relevant.", "the invalid answer is kept for the repair");
            Equal(1L, Jobs.Long(attempts[1], "repairs"), "the second attempt repairs the first");
            Equal(2, ((JsonArrayValue)(await session.ExecutionUsageAsync(start.TaskId.Value))["reservations"]).Items.Length, "both calls are in the ledger");

            var stubborn = await session.StartExecutionAsync(1, "pisharp-judge", Input("stubborn"), null, null);
            await WaitForRunAsync(session, stubborn.TaskId.Value, "failed");
            var failed = await JudgmentOfAsync(session, stubborn.TaskId.Value);
            Equal("failed", Jobs.String(failed, "status"), "a second invalid answer fails");
            Equal(2, Jobs.Items(failed, "attempts").Count(), "only one repair");
            Check(Jobs.String(failed, "error")!.StartsWith("Invalid output", StringComparison.Ordinal), "the error says why");
            Check(Jobs.String(failed, "decision") is null, "invalid output is never approved");
        });
    }
}
