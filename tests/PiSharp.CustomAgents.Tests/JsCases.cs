using PiSharp.Codemode;
using PiSharp.CustomAgents.JavaScript;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Tools;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

// Decision 0012, P10: JavaScript tools, the Script action, agent-written tools, worker isolation.
internal static partial class Program
{
    private static JsonObjectValue Schema(string json) => (JsonObjectValue)JsonValue.Parse(json);

    /// <summary>A team whose lead calls the tool its input names (<c>call &lt;tool&gt; &lt;json&gt;</c>), answers with the result, and otherwise
    /// lists its tools.</summary>
    private static (AgentTeam Team, FauxAgentModels Models) CallingTeam(AgentDefinition lead, FauxAgentModels? models = null)
    {
        models ??= new FauxAgentModels();
        return CallingTeamOn(Team(models, "lead"), models, lead);
    }

    internal static IEnumerable<(string, Func<Task>)> JsCases()
    {
        yield return Case("agents.js.pure-returns-value", async () =>
        {
            var direct = await JsRuntime.Shared.RunAsync("console.log('hi ' + args.who); return { sum: args.a + args.b };",
                [KeyValuePair.Create("args", JsonValue.Parse("""{"who":"js","a":2,"b":3}"""))]);
            Check(direct.Ok, "the script returned: " + direct.Error);
            Equal("""{"sum":5}""", direct.Value!.ToJson(), "the value");
            Equal("hi js", direct.Output, "the printed output");
            Equal(null, JsRuntime.CheckSyntax("return 1;"), "a body parses");
            Check(JsRuntime.CheckSyntax("return (") is not null, "a broken body does not");
            Check(JsRuntime.CheckSyntax("}); evil(); (async () => {") is not null, "a body that closes the function early is refused");
            Throws<ArgumentException>(() => JsTool.Create(new JsToolDefinition("js_bad", "Broken", "return (")), "a tool that does not parse");
            var sum = JsTool.Create(new JsToolDefinition("js_sum", "Adds a and b", "return args.a + args.b;")
            {
                Parameters = Schema("""{"type":"object","required":["a","b"],"properties":{"a":{"type":"number"},"b":{"type":"number"}}}"""),
            });
            Equal(ToolReplay.Safe, sum.Replay, "pure tools are replay-safe");
            var typed = JsTool.Create(new JsToolDefinition("js_pair", "Returns a pair", "return { a: 1, b: 'two' };")
            {
                OutputSchema = Schema("""{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"string"}}}"""),
            });
            Check(typed.StructuredOutputSchema is not null, "an output schema makes the value structured");
            var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Calls tools", Tools = [sum, typed] });
            await using var session = await team.OpenAsync(new MemoryStorage());
            Equal("5", (await session.AskAsync("call js_sum {\"a\":2,\"b\":3}")).Text, "the model sees the value");
            Equal("""{"a":1,"b":"two"}""", (await session.AskAsync("call js_pair")).Text, "an object value as JSON");
        });

        yield return Case("agents.js.limits", async () =>
        {
            await using var runtime = new JsRuntime(new JsRuntimeOptions { TimeoutMs = 500, MemoryLimitBytes = 16L * 1024 * 1024, Isolation = JsIsolation.InProcess });
            var spin = await runtime.RunAsync("while (true) {}");
            Check(!spin.Ok && spin.Error!.Contains("timed out", StringComparison.Ordinal), "a runaway script times out: " + spin.Error);
            var deep = await runtime.RunAsync("function f(n) { return f(n + 1); } return f(0);");
            Check(!deep.Ok, "unbounded recursion fails");
            var hungry = await runtime.RunAsync("const keep = []; while (true) keep.push('x'.repeat(1 << 20));");
            Check(!hungry.Ok, "a script over the memory limit fails");
            var thrown = await runtime.RunAsync("throw new TypeError('boom');");
            Check(!thrown.Ok && thrown.Error!.Contains("boom", StringComparison.Ordinal), "a thrown error comes back: " + thrown.Error);
            var host = await runtime.RunAsync("return [typeof require, typeof process, typeof fetch, typeof XMLHttpRequest, typeof System, typeof importScripts].join(',');");
            Equal("undefined,undefined,undefined,undefined,undefined,undefined", host.ValueText, "no host objects");
            Equal("3", (await runtime.RunAsync("return 1 + 2;")).ValueText, "the runtime still works after failures");
        });

        yield return Case("agents.js.durable-calls-allowlisted-only", async () =>
        {
            var secretCalls = 0;
            var lookup = AgentTool.Create<EchoLookup>("lookup", "Looks an order up", (args, _) => Task.FromResult<AgentToolResult>("order " + args.Id + " is paid"));
            var secret = AgentTool.Create("secret", "Not for scripts", _ =>
            {
                Interlocked.Increment(ref secretCalls);
                return Task.FromResult<AgentToolResult>("the secret");
            });
            var report = JsTool.Create(new JsToolDefinition("js_report", "Reports on an order",
                """
                const found = await tools.lookup({ id: args.id });
                let call = 'ran';
                try { await tools.secret({}); } catch (error) { call = 'threw'; }
                return found + '; secret: ' + ('secret' in tools ? 'visible' : 'hidden') + ', call ' + call;
                """)
            {
                Mode = JsToolMode.Durable,
                AllowedTools = ["lookup"],
                Parameters = Schema("""{"type":"object","required":["id"],"properties":{"id":{"type":"string"}}}"""),
            });
            var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Calls tools", Tools = [lookup, secret, report] });
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = (await session.AskAsync("call js_report {\"id\":\"42\"}")).Text;
            Check(reply.Contains("order 42 is paid; secret: hidden, call threw", StringComparison.Ordinal),
                "the script called lookup and could neither see nor call secret: " + reply);
            Equal(0, Volatile.Read(ref secretCalls), "the secret tool never ran");
        });

        yield return Case("agents.js.durable-replays-after-restart", async () =>
        {
            var path = TestHost.SqlitePath("js-replay");
            var steps = 0;
            var gateRuns = 0;
            TaskCompletionSource gateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = true;
            AgentTeam Build(FauxAgentModels models)
            {
                var step = AgentTool.Create("step_one", "Counts", _ =>
                {
                    Interlocked.Increment(ref steps);
                    return Task.FromResult<AgentToolResult>("one");
                });
                var gate = AgentTool.Create("gate", "Waits for the test", async tool =>
                {
                    Interlocked.Increment(ref gateRuns);
                    gateStarted.TrySetResult();
                    if (Volatile.Read(ref hold)) await Task.Delay(Timeout.Infinite, tool.CancellationToken);
                    return "open";
                }, new AgentToolOptions { Replay = ToolReplay.Safe });
                var flow = JsTool.Create(new JsToolDefinition("js_flow", "Steps then waits",
                    "const a = await tools.step_one({}); const b = await tools.gate({}); return a + '+' + b;")
                { Mode = JsToolMode.Durable, AllowedTools = ["step_one", "gate"] });
                return CallingTeam(new AgentDefinition { Name = "lead", Description = "Calls tools", Tools = [step, gate, flow] }, models).Team;
            }
            await using (var first = await Build(new FauxAgentModels()).OpenAsync(await TestHost.SqliteAsync(path)))
            {
                await first.Orchestrator!.SubmitAsync(SubmissionDraft.Input("call js_flow", requestId: "turn-1"), TestHost.Context);
                await gateStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            Volatile.Write(ref hold, false);
            await using var second = await Build(new FauxAgentModels()).OpenAsync(await TestHost.SqliteAsync(path));
            var reply = (await second.AskAsync("call js_flow", requestId: "turn-1")).Text;
            Check(reply.Contains("one+open", StringComparison.Ordinal), "the script finished after the restart: " + reply);
            Equal(1, Volatile.Read(ref steps), "the call the script made before the restart did not run again");
            Equal(2, Volatile.Read(ref gateRuns), "the interrupted replay-safe call ran again");
        });

        yield return Case("agents.js.action-merge-patch-and-next", async () =>
        {
            Equal("""{"a":{"c":2,"e":3},"f":[1]}""",
                JsAction<OrderData>.MergePatch(JsonValue.Parse("""{"a":{"b":1,"c":2},"d":1}"""), JsonValue.Parse("""{"a":{"b":null,"e":3},"d":null,"f":[1]}""")).ToJson(),
                "RFC 7386 merge patch");
            Throws<WorkflowDefinitionException>(() => Workflow.For<OrderData>("js-bad").Script("bad", "return ("), "a script that does not parse");
            var workflow = Workflow.For<OrderData>("js-orders")
                .Script("score", """
                    console.log('scoring ' + data.orderId);
                    return {
                      updates: { count: data.trail.length + 10, note: null, trail: [...data.trail, ctx.action + '#' + ctx.attempt] },
                      next: data.vip ? 'vip' : 'standard',
                      output: 'scored',
                    };
                    """)
                .Then(new AppendAction("vip"), new ActionOptions { Next = Workflow.End })
                .Then(new AppendAction("standard"))
                .Build();
            var failing = Workflow.For<OrderData>("js-fail").Script("check", "return data.count > 1 ? { fail: 'too many' } : { stop: true };").Build();
            var broken = Workflow.For<OrderData>("js-broken").Script("odd", "return 42;").Build();
            var unknown = Workflow.For<OrderData>("js-unknown").Script("odd", "return { nxt: 'x' };").Build();
            var mistyped = Workflow.For<OrderData>("js-mistyped").Script("odd", "return { updates: { count: 'many' } };").Build();
            await using var host = await TestHost.OpenAsync(catalog =>
            {
                catalog.Add(workflow);
                catalog.Add(failing);
                catalog.Add(broken);
                catalog.Add(unknown);
                catalog.Add(mistyped);
            });
            var result = await host.Runner.RunAsync(workflow, new OrderData { OrderId = "1", Trail = ["x"], Note = "drop me" });
            var data = result.EnsureCompleted();
            Equal("x,score#1,standard", string.Join(",", data.Trail), "the patched trail, then the chosen branch");
            Equal(11, data.Count, "the patched count");
            Equal(null, data.Note, "a null in the patch removed the note");
            var run = host.Runner.Attach(workflow, result.TaskId, (await host.Harness.GetTaskAsync(result.TaskId, TestHost.Context))!.ConversationId);
            Check((await run.StatusAsync()).Log.Contains("scoring 1\nscored"), "printed output and output went to the log");
            Equal(WorkflowRunStatus.Completed, (await host.Runner.RunAsync(failing, new OrderData { Count = 1 })).Status, "stop: true completes");
            var failed = await host.Runner.RunAsync(failing, new OrderData { Count = 2 });
            Check(failed.Status == WorkflowRunStatus.Failed && failed.Error!.Contains("too many", StringComparison.Ordinal), "fail ends the run: " + failed.Error);
            var odd = await host.Runner.RunAsync(broken, new OrderData());
            Check(odd.Status == WorkflowRunStatus.Failed && odd.Error!.Contains("returned 42", StringComparison.Ordinal), "a value that is not a decision: " + odd.Error);
            var typo = await host.Runner.RunAsync(unknown, new OrderData());
            Check(typo.Status == WorkflowRunStatus.Failed && typo.Error!.Contains("'nxt'", StringComparison.Ordinal), "an unknown field: " + typo.Error);
            var wrong = await host.Runner.RunAsync(mistyped, new OrderData());
            Check(wrong.Status == WorkflowRunStatus.Failed && wrong.Error!.Contains("data the action returned", StringComparison.Ordinal),
                "patched data that does not fit the type: " + wrong.Error);
        });

        yield return Case("agents.js.create-tool-disabled-by-default", async () =>
        {
            var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Writes tools", CanCreateTools = true });
            await using (var session = await team.OpenAsync(new MemoryStorage()))
                Equal("tools: ", (await session.AskAsync("list")).Text, "without Enable, CanCreateTools gives nothing");
            if (CodemodeWorker.Default is null)
                Throws<InvalidOperationException>(() => JsAuthoring.Enable(Team(new FauxAgentModels())), "authoring needs worker isolation by default");
            var (enabled, models) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Writes tools", CanCreateTools = true });
            enabled.Catalog.Register(new AgentDefinition { Name = "plain", Description = "Cannot write tools" });
            models.On(request => Is(request, "plain"), request => "tools: " + string.Join(",", Own(request.Tools)));
            JsAuthoring.Enable(enabled, new JsAuthoringOptions { RequireIsolation = false });
            await using var opened = await enabled.OpenAsync(new MemoryStorage());
            Equal("tools: create_tool,remove_tool", (await opened.AskAsync("list")).Text, "with both opt-ins the agent can write tools");
            var plain = await opened.StartAgentAsync("plain");
            Equal("tools: ", (await opened.AskAsync("list", plain)).Text, "an agent without CanCreateTools cannot");
        });

        yield return Case("agents.js.create-tool-persists-reloads", async () =>
        {
            var path = TestHost.SqlitePath("js-authoring");
            (AgentTeam Team, ToolSource Source) Build()
            {
                var models = new FauxAgentModels();
                models.On(request => Is(request, "lead") && !request.AfterTools && request.LastUserText == "make", _ => FauxReply.ToolCall("create_tool", Json(new
                {
                    name = "js_double",
                    description = "Doubles n",
                    code = "return args.n * 2;",
                    parameters = new { type = "object", required = new[] { "n" }, properties = new { n = new { type = "number" } } },
                })));
                models.On(request => Is(request, "lead") && !request.AfterTools && request.LastUserText == "make bad name", _ =>
                    FauxReply.ToolCall("create_tool", Json(new { name = "double", description = "Doubles", code = "return 2;" })));
                models.On(request => Is(request, "lead") && !request.AfterTools && request.LastUserText == "make bad code", _ =>
                    FauxReply.ToolCall("create_tool", Json(new { name = "js_broken", description = "Broken", code = "return (" })));
                models.On(request => Is(request, "lead") && !request.AfterTools && request.LastUserText == "remove", _ =>
                    FauxReply.ToolCall("remove_tool", Json(new { name = "js_double" })));
                var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Writes tools", CanCreateTools = true }, models);
                return (team, JsAuthoring.Enable(team, new JsAuthoringOptions { RequireIsolation = false }));
            }
            var (firstTeam, firstSource) = Build();
            await using (var first = await firstTeam.OpenAsync(await TestHost.SqliteAsync(path)))
            {
                Equal("Created js_double. Call it from your next step.", (await first.AskAsync("make")).Text, "created");
                Equal("42", (await first.AskAsync("call js_double {\"n\":21}")).Text, "the written tool runs");
                Check((await first.AskAsync("list")).Text.Contains("js_double", StringComparison.Ordinal), "and is offered");
                Check((await first.AskAsync("make bad name")).Text.StartsWith("error: 'double' is not a written tool name", StringComparison.Ordinal), "names are js_*");
                Check((await first.AskAsync("make bad code")).Text.StartsWith("error: The code does not parse", StringComparison.Ordinal), "code must parse");
                Equal("js_double", string.Join(",", firstSource.Tools.Select(tool => tool.Name)), "only the valid tool was kept");
            }
            var (secondTeam, secondSource) = Build();
            await using var second = await secondTeam.OpenAsync(await TestHost.SqliteAsync(path));
            Equal("js_double", string.Join(",", secondSource.Tools.Select(tool => tool.Name)), "restored from the session before scheduling resumed");
            Equal("10", (await second.AskAsync("call js_double {\"n\":5}")).Text, "the restored tool runs");
            Equal("Removed js_double.", (await second.AskAsync("remove")).Text, "removed");
            Equal(0, secondSource.Tools.Count, "and gone from the source");
            Equal("tools: create_tool,remove_tool", (await second.AskAsync("list")).Text, "and no longer offered");
        });

        yield return Case("agents.js.worker-isolation", async () =>
        {
            var launcher = CodemodeWorkerLauncher.ForCurrentProcess() ?? throw new InvalidOperationException("no launchable test process");
            await using var runtime = new JsRuntime(new JsRuntimeOptions { Isolation = JsIsolation.Worker, Worker = launcher });
            Check(runtime.IsIsolated, "the runtime uses a worker process");
            var result = await runtime.RunAsync("return args.map(n => n * n);", [KeyValuePair.Create("args", JsonValue.Parse("[1,2,3]"))]);
            Check(result.Ok, "the worker ran the script: " + result.Error);
            Equal("[1,4,9]", result.Value!.ToJson(), "the value crossed the process boundary");
            var deep = await runtime.RunAsync("let a = []; for (let i = 0; i < 200000; i++) a = [a]; return JSON.stringify(a).length;");
            Check(!deep.Ok || deep.Value is not null, "a deeply nested value cannot take the host down");
            Equal("ok", (await runtime.RunAsync("return 'ok';")).ValueText, "the next script gets a fresh worker");
            var (team, _) = CallingTeam(new AgentDefinition { Name = "lead", Description = "Writes tools", CanCreateTools = true });
            JsAuthoring.Enable(team, new JsAuthoringOptions { Runtime = runtime });
        });
    }

    private sealed record EchoLookup([property: System.ComponentModel.Description("The order ID")] string Id);
}
