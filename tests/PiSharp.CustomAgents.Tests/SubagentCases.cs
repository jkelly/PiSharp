using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.Durable;
using PiSharp.Durable.Storage;

// Decision 0012, P8: subagents: delegate, spawn, limits, abort propagation and restart.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SubagentCases()
    {
        yield return Case("agents.sub.delegate-strict-and-interactive-answer", async () =>
        {
            foreach (var mode in new[] { DelegationMode.Strict, DelegationMode.Interactive })
            {
                var models = new FauxAgentModels();
                var team = Team(models, "lead");
                team.Catalog.Register(new AgentDefinition
                {
                    Name = "lead", Description = "Leads", Instructions = "LEAD SECRET", Subagents = ["worker"], Delegation = mode,
                });
                team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Computes" });
                FauxRequest? worker = null;
                models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "compute 6*7" })));
                models.On(request => Is(request, "lead") && request.LastToolName == "delegate", request => "lead: " + request.LastToolResult);
                models.On(request => Is(request, "worker"), request =>
                {
                    worker = request;
                    return "42";
                });
                await using var session = await team.OpenAsync(new MemoryStorage());
                var reply = await session.AskAsync("what is 6*7?");
                Equal("lead: worker#1 answered:\n42", reply.Text, mode + ": the delegate result");
                Check(worker!.LastUserText!.Contains("compute 6*7", StringComparison.Ordinal) && worker.LastUserText.Contains("kind=\"task\"", StringComparison.Ordinal),
                    mode + ": the worker got the task: " + worker.LastUserText);
                Check(!worker.SystemText.Contains("LEAD SECRET", StringComparison.Ordinal), mode + ": the worker does not see the lead's prompt");
                var entry = (await session.FindAgentAsync("worker#1"))!;
                Equal(AgentKind.Child, entry.Kind, mode + ": a child");
                Equal("lead", entry.Parent, mode + ": of the lead");
                Equal(1, entry.Depth, mode + ": one level down");
                Equal(Requests.Tool, Requests.State(await LatestRequestAsync(session, "worker#1")), mode + ": claimed by the call");
            }
        });

        yield return Case("agents.sub.spawn-background-reports-once", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
            team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Researches" });
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = 0;
            string? report = null;
            models.On(request => Is(request, "lead") && Heard(request, "result", "worker#1"), request =>
            {
                Interlocked.Increment(ref reports);
                report = request.LastUserText;
                return "noted";
            });
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("spawn", Json(new { agent = "worker", task = "research X" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "spawn", request => "spawned: " + request.LastToolResult);
            models.On(request => Is(request, "worker"), _ => FauxReply.Answer("found X").HeldUntil(release.Task));
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("research in the background");
            Check(reply.Text.StartsWith("spawned: Started worker#1", StringComparison.Ordinal), "spawn returns at once: " + reply.Text);
            release.TrySetResult();
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reports) >= 1), TimeSpan.FromSeconds(20));
            await session.Orchestrator!.WaitForIdleAsync(TestHost.Context);
            await Task.Delay(200);
            Equal(1, Volatile.Read(ref reports), "reported once");
            Check(report!.Contains("found X", StringComparison.Ordinal), "the report carries the answer: " + report);
            Equal(Requests.Reporter, Requests.State(await LatestRequestAsync(session, "worker#1")), "claimed by the reporter");
            Equal(1, (await MailboxAsync(session, "lead")).Count(message => message.Kind == AgentMessageKind.Result), "one result record");
        });

        yield return Case("agents.sub.depth-limit-withholds-tools", async () =>
        {
            var models = new FauxAgentModels();
            var team = new AgentTeam(new AgentTeamOptions { Models = models, DefaultModel = models.Model(), Orchestrator = "lead", MaxDepth = 1 });
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
            team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Could nest", Subagents = ["worker"] });
            FauxRequest? lead = null, worker = null;
            models.On(request => Is(request, "lead") && !request.AfterTools, request =>
            {
                lead = request;
                return FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "go deeper" }));
            });
            models.On(request => Is(request, "lead") && request.LastToolName == "delegate", request => "lead: " + request.LastToolResult);
            models.On(request => Is(request, "worker"), request =>
            {
                worker = request;
                return "leaf";
            });
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("nest");
            Equal("lead: worker#1 answered:\nleaf", reply.Text, "the delegate result");
            Check(lead!.Tools.Contains("delegate") && lead.Tools.Contains("spawn"), "the root may delegate: " + string.Join(",", lead.Tools));
            Check(lead.Sections.TryGetValue("subagents", out var roster) && roster.Contains("- worker: Could nest", StringComparison.Ordinal), "the roster lists the subagents");
            Check(!worker!.Tools.Contains("delegate") && !worker.Tools.Contains("spawn"), "at MaxDepth no orchestration tools: " + string.Join(",", worker.Tools));
            Check(worker.Tools.Contains("send_message"), "messaging stays");
        });

        yield return Case("agents.sub.abort-parent-aborts-interactive-child", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
            team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Takes forever" });
            var never = new TaskCompletionSource();
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "long job" })));
            models.On(request => Is(request, "lead") && request.AfterTools, request => "lead: " + request.LastToolResult);
            models.On(request => Is(request, "worker"), _ => FauxReply.Answer("never").HeldUntil(never.Task));
            await using var session = await team.OpenAsync(new MemoryStorage());
            var submission = await session.Orchestrator!.SubmitAsync(SubmissionDraft.Input("start the long job"), TestHost.Context);
            await WaitUntilAsync(() => Task.FromResult(models.Requests.Any(request => Is(request, "worker"))), TimeSpan.FromSeconds(20));
            await session.Orchestrator.AbortAsync(TestHost.Context);
            var settled = await submission.WaitAsync(TestHost.Context);
            Check(settled.Status != SubmissionStatus.Done, "the parent's turn was aborted: " + settled.Status);
            await WaitUntilAsync(async () => Requests.State(await LatestRequestAsync(session, "worker#1")) == Requests.Aborted, TimeSpan.FromSeconds(20));
            var child = await session.AgentAsync("worker#1");
            await child.WaitForIdleAsync(TestHost.Context).WaitAsync(TimeSpan.FromSeconds(20));
        });

        yield return Case("agents.sub.supervisor-survives-restart", async () =>
        {
            var path = TestHost.SqlitePath("sub-restart");
            AgentTeam Build(FauxAgentModels models)
            {
                var team = Team(models, "lead");
                team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
                team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Works" });
                models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "survive" })));
                models.On(request => Is(request, "lead") && request.LastToolName == "delegate", request => "lead: " + request.LastToolResult);
                return team;
            }
            var before = new FauxAgentModels();
            before.On(request => Is(request, "worker"), _ => FauxReply.Answer("too late").HeldUntil(new TaskCompletionSource().Task));
            await using (var first = await Build(before).OpenAsync(await TestHost.SqliteAsync(path)))
            {
                await first.Orchestrator!.SubmitAsync(SubmissionDraft.Input("delegate and survive", requestId: "turn-1"), TestHost.Context);
                await WaitUntilAsync(() => Task.FromResult(before.Requests.Any(request => Is(request, "worker"))), TimeSpan.FromSeconds(20));
            }
            var after = new FauxAgentModels();
            var answers = 0;
            after.On(request => Is(request, "worker"), _ =>
            {
                Interlocked.Increment(ref answers);
                return "done after restart";
            });
            await using var second = await Build(after).OpenAsync(await TestHost.SqliteAsync(path));
            var reply = await second.AskAsync("delegate and survive", requestId: "turn-1");
            Equal("lead: worker#1 answered:\ndone after restart", reply.Text, "the delegate resumed and claimed the answer");
            Equal(1, Volatile.Read(ref answers), "the worker answered once after the restart");
            Equal(1, (await second.DirectoryAsync()).Count(entry => entry.Kind == AgentKind.Child), "the same child, not a second one");
        });

        yield return Case("agents.sub.spawn-restart-reports-once", async () =>
        {
            var path = TestHost.SqlitePath("spawn-restart");
            var reports = 0;
            AgentTeam Build(FauxAgentModels models)
            {
                var team = Team(models, "lead");
                team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
                team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Works" });
                models.On(request => Is(request, "lead") && Heard(request, "result", "worker#1"), _ =>
                {
                    Interlocked.Increment(ref reports);
                    return "noted";
                });
                models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("spawn", Json(new { agent = "worker", task = "outlive me" })));
                models.On(request => Is(request, "lead") && request.LastToolName == "spawn", _ => "spawned");
                return team;
            }
            var before = new FauxAgentModels();
            before.On(request => Is(request, "worker"), _ => FauxReply.Answer("too late").HeldUntil(new TaskCompletionSource().Task));
            await using (var first = await Build(before).OpenAsync(await TestHost.SqliteAsync(path)))
            {
                Equal("spawned", (await first.AskAsync("spawn and close")).Text, "spawn returned");
                await WaitUntilAsync(() => Task.FromResult(before.Requests.Any(request => Is(request, "worker"))), TimeSpan.FromSeconds(20));
            }
            var after = new FauxAgentModels();
            after.On(request => Is(request, "worker"), _ => "finished after restart");
            await using var second = await Build(after).OpenAsync(await TestHost.SqliteAsync(path));
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reports) >= 1), TimeSpan.FromSeconds(20));
            await second.Orchestrator!.WaitForIdleAsync(TestHost.Context);
            await Task.Delay(200);
            Equal(1, Volatile.Read(ref reports), "the resumed watcher reported once");
            var result = (await MailboxAsync(second, "lead")).Single(message => message.Kind == AgentMessageKind.Result);
            Equal("finished after restart", result.Text, "the answer from after the restart");
        });
    }
}
