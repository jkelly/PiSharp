using System.ComponentModel;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Tools;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

internal sealed record LookupArgs([property: Description("The order to look up")] string OrderId, int? Limit = null);

internal sealed record LookupResult(string OrderId, string Status, int Items);

internal sealed class TreeNode
{
    public string Name { get; set; } = "";
    public List<TreeNode> Children { get; set; } = [];
}

// Decision 0012, P5-P6: typed tools, tool sources and the agent team.
internal static partial class Program
{
    private static AgentTeam Team(FauxAgentModels models, string? orchestrator = null) => new(new AgentTeamOptions
    {
        Models = models,
        DefaultModel = models.Model(),
        Orchestrator = orchestrator,
    });

    private static readonly HashSet<string> TeamToolNames = ["list_agents", "send_message", "ask_agent", "reply", "delegate", "spawn", "wait_agent", "stop_agent"];

    /// <summary>The tools of a request other than the team's messaging and orchestration tools.</summary>
    private static IEnumerable<string> Own(IEnumerable<string> tools) => tools.Where(tool => !TeamToolNames.Contains(tool));

    private static bool Is(FauxRequest request, string agent) => request.Sections.TryGetValue("agent", out var role) && role.Contains($"'{agent}'");

    internal static IEnumerable<(string, Func<Task>)> TeamCases()
    {
        yield return Case("agents.tools.typed-schema-from-record", () =>
        {
            var tool = AgentTool.Create<LookupArgs>("lookup_order", "Looks up an order", (args, _) => Task.FromResult<AgentToolResult>("ok"));
            var properties = (JsonObjectValue)tool.Parameters["properties"];
            var orderId = (JsonObjectValue)properties["orderId"];
            Equal("object", ((JsonStringValue)tool.Parameters["type"]).Value, "object root");
            Check(orderId["type"].ToJson().Contains("string"), "string property: " + orderId.ToJson());
            Equal("The order to look up", ((JsonStringValue)orderId["description"]).Value, "description");
            Check(properties.ContainsKey("limit"), "optional property");
            Equal("[\"orderId\"]", tool.Parameters["required"].ToJson(), "required constructor parameters");
            Equal(PiSharp.Contracts.ToolSchemaOrigin.JsonSchema, tool.ParametersOrigin, "origin");
            Equal(ToolReplay.Unsafe, tool.Replay, "unsafe by default");
            Throws<ArgumentException>(() => AgentTool.Create<TreeNode>("tree", "x", (_, _) => Task.FromResult<AgentToolResult>("")), "recursive type");
            Throws<ArgumentException>(() => AgentTool.Create<int>("number", "x", (_, _) => Task.FromResult<AgentToolResult>("")), "non-object arguments");
            Throws<ArgumentException>(() => AgentTool.Create<LookupArgs>("bad name", "x", (_, _) => Task.FromResult<AgentToolResult>("")), "tool name");
            var typed = AgentTool.Create<LookupArgs, LookupResult>("lookup_typed", "Typed", (args, _) => Task.FromResult(new LookupResult(args.OrderId, "open", 2)));
            Check(typed.StructuredOutputSchema is not null, "structured output schema");
        });

        yield return Case("agents.tools.typed-invalid-args-rejected", async () =>
        {
            var models = new FauxAgentModels();
            var calls = new List<string>();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition
            {
                Name = "lead",
                Description = "Looks things up",
                Tools =
                [
                    AgentTool.Create<LookupArgs, LookupResult>("lookup_order", "Looks up an order", (args, context) =>
                    {
                        lock (calls) calls.Add(args.OrderId);
                        return Task.FromResult(new LookupResult(args.OrderId, "shipped", args.Limit ?? 1));
                    }),
                ],
            });
            models.On(request => request.LastUserText == "bad" && !request.AfterTools, _ => FauxReply.ToolCall("lookup_order", """{"limit": 2}"""));
            models.On(request => request.LastUserText == "good" && !request.AfterTools, _ => FauxReply.ToolCall("lookup_order", """{"orderId": "A1", "limit": 3}"""));
            models.On(request => request.AfterTools, request => "result: " + request.ToolResults[0].Text + (request.ToolResults[0].IsError ? " (error)" : ""));
            await using var session = await team.OpenAsync(new MemoryStorage());
            var bad = await session.AskAsync("bad");
            Check(bad.Text.Contains("(error)"), "invalid arguments come back as an error: " + bad.Text);
            Equal(0, calls.Count, "the handler never ran");
            var good = await session.AskAsync("good");
            Check(good.Text.Contains("\"status\":\"shipped\"") && good.Text.Contains("\"items\":3"), "typed result as JSON: " + good.Text);
            Equal("A1", string.Join(",", calls), "handler ran once");
        });

        yield return Case("agents.tools.dynamic-add-visible-next-request", async () =>
        {
            var models = new FauxAgentModels();
            var source = new DynamicToolSource("live");
            source.Add(AgentTool.Create("first", "Adds the second tool", context =>
            {
                source.Add(AgentTool.Create("second", "Added at run time", _ => Task.FromResult<AgentToolResult>("second ran")));
                return Task.FromResult<AgentToolResult>("added");
            }));
            var team = Team(models, "lead");
            team.Catalog.AddSource(source);
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Uses live tools", ToolSources = ["live"] });
            var offered = new List<string>();
            models.On(request => !request.AfterTools, request =>
            {
                lock (offered) offered.Add(string.Join(",", Own(request.Tools)));
                return FauxReply.ToolCall("first");
            });
            models.On(request => request.AfterTools && request.LastToolName == "first", request =>
            {
                lock (offered) offered.Add(string.Join(",", Own(request.Tools)));
                return FauxReply.ToolCall("second");
            });
            models.On(request => request.LastToolName == "second", request => "done: " + request.LastToolResult);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("go");
            Equal("done: second ran", reply.Text, "the added tool ran");
            Equal("first", offered[0], "first request");
            Equal("first,second", offered[1], "the next request offers the added tool");
        });

        yield return Case("agents.tools.dynamic-remove-mid-round-unavailable", async () =>
        {
            var models = new FauxAgentModels();
            var source = new DynamicToolSource("live");
            var sequential = new AgentToolOptions { Sequential = true };
            source.Add(AgentTool.Create("remover", "Removes the victim", _ =>
            {
                source.Remove("victim");
                return Task.FromResult<AgentToolResult>("removed");
            }, sequential));
            source.Add(AgentTool.Create("victim", "Gets removed", _ => Task.FromResult<AgentToolResult>("victim ran"), sequential));
            var team = Team(models, "lead");
            team.Catalog.AddSource(source);
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "x", ToolSources = ["live"] });
            models.On(request => !request.AfterTools, _ => FauxReply.ToolCalls(("remover", "{}"), ("victim", "{}")));
            models.On(request => request.AfterTools, request => string.Join("|", request.ToolResults.Select(result => result.Name + "=" + result.Text)) +
                ";offered=" + string.Join(",", Own(request.Tools)));
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("go");
            Check(reply.Text.StartsWith("remover=removed|victim=", StringComparison.Ordinal), "both calls answered: " + reply.Text);
            Check(reply.Text.Contains("not available"), "the unresolved call is unavailable: " + reply.Text);
            Check(reply.Text.EndsWith("offered=remover", StringComparison.Ordinal), "the next request no longer offers it: " + reply.Text);
        });

        yield return Case("agents.tools.remove-while-executing-completes", async () =>
        {
            var models = new FauxAgentModels();
            var source = new DynamicToolSource("live");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Add(AgentTool.Create("slow", "Takes a while", async context =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                return "slow finished";
            }));
            var team = Team(models, "lead");
            team.Catalog.AddSource(source);
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "x", ToolSources = ["live"] });
            models.On(request => !request.AfterTools, _ => FauxReply.ToolCall("slow"));
            models.On(request => request.AfterTools, request => request.LastToolResult!);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var asking = session.AskAsync("go");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(source.Remove("slow"), "removed while executing");
            release.TrySetResult();
            Equal("slow finished", (await asking.WaitAsync(TimeSpan.FromSeconds(20))).Text, "the executing call finishes on its old code");
        });

        yield return Case("agents.tools.name-conflict-detected", () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models);
            var source = new DynamicToolSource("shared", [AgentTool.Create("lookup", "x", _ => Task.FromResult<AgentToolResult>(""))]);
            team.Catalog.AddSource(source);
            Throws<ToolConflictException>(() => team.Catalog.Register(new AgentDefinition
            {
                Name = "dup",
                Description = "x",
                Tools = [AgentTool.Create("lookup", "mine", _ => Task.FromResult<AgentToolResult>(""))],
                ToolSources = ["shared"],
            }), "inline tool vs source tool");
            team.Catalog.Register(new AgentDefinition
            {
                Name = "ok",
                Description = "x",
                Tools = [AgentTool.Create("mine", "mine", _ => Task.FromResult<AgentToolResult>(""))],
                ToolSources = ["shared"],
            });
            Throws<ToolConflictException>(() => source.Add(AgentTool.Create("mine", "clash", _ => Task.FromResult<AgentToolResult>(""))), "source change");
            Equal("lookup", string.Join(",", source.Tools.Select(tool => tool.Name)), "a refused change leaves the source unchanged");
            Throws<ArgumentException>(() => source.Add(AgentTool.Create("lookup", "again", _ => Task.FromResult<AgentToolResult>(""))), "within a source");
        });

        yield return Case("agents.team.child-does-not-inherit-parent-tools", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition
            {
                Name = "lead",
                Description = "Leads",
                Instructions = "LEAD INSTRUCTIONS",
                Tools = [AgentTool.Create("lead_tool", "x", _ => Task.FromResult<AgentToolResult>(""))],
            });
            team.Catalog.Register(new AgentDefinition
            {
                Name = "helper",
                Description = "Helps",
                Instructions = "HELPER INSTRUCTIONS",
                Tools = [AgentTool.Create("helper_tool", "x", _ => Task.FromResult<AgentToolResult>(""))],
            });
            FauxRequest? seen = null;
            models.On(request => Is(request, "helper"), request =>
            {
                seen = request;
                return "note for " + request.LastUserText;
            });
            var workflow = Workflow.For<OrderData>("with-agent")
                .Agent("note", "helper", data => data.OrderId, (data, answer) => data.Note = answer.Text)
                .Build();
            team.Workflows.Add(workflow);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var result = await session.Workflows.RunAsync(workflow, new OrderData { OrderId = "42" },
                new WorkflowStartOptions { Conversation = session.Orchestrator!.Id });
            Equal("note for 42", result.EnsureCompleted().Note, "agent step answer");
            Equal("helper_tool", string.Join(",", Own(seen!.Tools)), "only the helper's tools");
            Check(seen.SystemText.Contains("HELPER INSTRUCTIONS") && !seen.SystemText.Contains("LEAD"), "only the helper's prompt: " + seen.SystemText);
            var missing = Workflow.For<OrderData>("missing-agent").Agent("note", "nobody", data => "x", (_, _) => { }).Build();
            team.Workflows.Add(missing);
            var failed = await session.Workflows.RunAsync(missing, new OrderData());
            Equal(WorkflowRunStatus.Failed, failed.Status, "unknown agent fails the step");
            Check(failed.Error!.Contains("nobody"), "names the agent: " + failed.Error);
        });

        yield return Case("agents.team.definition-update-propagates", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models);
            var v1 = new AgentDefinition { Name = "peer", Description = "Peer", Instructions = "VERSION ONE" };
            team.Catalog.Register(v1);
            models.On(_ => true, request => (request.SystemText.Contains("VERSION TWO") ? "v2" : "v1") + ":" + string.Join(",", Own(request.Tools)) + ":" + request.ModelId);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var peer = await session.StartAgentAsync("peer");
            Equal("peer#1", (await session.DirectoryAsync()).Single().Address, "directory address");
            Equal("v1::faux-1", (await session.AskAsync("hi", peer)).Text, "first version");
            team.Catalog.Update(v1 with
            {
                Instructions = "VERSION TWO",
                Tools = [AgentTool.Create("added", "x", _ => Task.FromResult<AgentToolResult>(""))],
            });
            await WaitUntilAsync(async () => (await peer.AgentAsync(TestHost.Context)).Instructions == "VERSION TWO", TimeSpan.FromSeconds(10));
            Equal("v2:added:faux-1", (await session.AskAsync("again", peer)).Text, "the update reaches the live agent");
        });

        yield return Case("agents.team.default-selection-empty", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models);
            team.Catalog.Register(new AgentDefinition
            {
                Name = "peer",
                Description = "x",
                Tools = [AgentTool.Create("peer_tool", "x", _ => Task.FromResult<AgentToolResult>(""))],
            });
            await using var session = await team.OpenAsync(new MemoryStorage());
            var raw = await session.Harness.CreateConversationAsync(new ConversationCreateOptions(ConversationOwnership.Ownerless), TestHost.Context);
            var agent = await raw.AgentAsync(TestHost.Context);
            Equal(0, agent.Extensions.Length, "a conversation the team did not configure selects nothing");
            Equal(0, agent.Tools.Length, "no tools");
            var peer = await session.StartAgentAsync("peer", "named-peer");
            Equal("agents.messaging,agent.peer", string.Join(",", (await peer.AgentAsync(TestHost.Context)).Extensions.Select(extension => extension.Name)), "the team messaging and its own");
            await ThrowsAsync<InvalidOperationException>(() => session.StartAgentAsync("peer", "named-peer"), "address taken");
        });

        yield return Case("agents.team.register-before-resume", async () =>
        {
            var path = TestHost.SqlitePath("team-resume");
            var hang = true;
            var runs = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AgentTeam Build(FauxAgentModels models)
            {
                var team = Team(models, "lead");
                team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "x", ToolSources = ["late"] });
                // The source arrives through a tracked load (as plugins and scripts do); OpenAsync waits for it before resuming.
                team.Catalog.Track(Task.Run(async () =>
                {
                    await Task.Delay(200);
                    team.Catalog.AddSource(new DynamicToolSource("late", [AgentTool.Create("work", "Safe work", async context =>
                    {
                        Interlocked.Increment(ref runs);
                        if (hang)
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.Infinite, context.CancellationToken);
                        }
                        return "worked";
                    }, new AgentToolOptions { Replay = ToolReplay.Safe })]));
                }));
                models.On(request => !request.AfterTools, _ => FauxReply.ToolCall("work"));
                models.On(request => request.AfterTools, request => "answer: " + request.LastToolResult);
                return team;
            }
            SubmissionId submitted;
            await using (var first = await Build(new FauxAgentModels()).OpenAsync(await TestHost.SqliteAsync(path)))
            {
                var submission = await first.Orchestrator!.SubmitAsync(SubmissionDraft.Input("go", requestId: "go-1"), TestHost.Context);
                submitted = submission.Id;
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            hang = false;
            await using (var second = await Build(new FauxAgentModels()).OpenAsync(await TestHost.SqliteAsync(path)))
            {
                var submission = await second.Harness.SubmissionAsync(submitted, TestHost.Context);
                var settled = await submission!.WaitAsync(TestHost.Context).WaitAsync(TimeSpan.FromSeconds(20));
                Equal(SubmissionStatus.Done, settled.Status, "the interrupted run finished after the reopen");
                var again = await second.AskAsync("go", requestId: "go-1");
                Equal("answer: worked", again.Text, "the replay-safe tool reran with its source loaded");
            }
            Equal(2, runs, "the tool ran once per session");
        });
    }
}
