using System.Text.RegularExpressions;
using PiSharp.CustomAgents.Teams;
using PiSharp.CustomAgents.Testing;
using PiSharp.CustomAgents.Workflows;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

// Decision 0012, P7-P8: messages between agents, the host and workflows; questions and the wait-for rules.
internal static partial class Program
{
    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static bool Heard(FauxRequest request, string kind, string? from = null) =>
        request.LastUserText is { } text && text.Contains($"kind=\"{kind}\"", StringComparison.Ordinal) &&
        (from is null || text.Contains($"from=\"{from}\"", StringComparison.Ordinal));

    private static async Task<IReadOnlyList<AgentMessage>> MailboxAsync(AgentSession session, string address) =>
        Mailboxes.Read(await session.Harness.SnapshotAsync(Mailboxes.Doc, address, TestHost.Context));

    /// <summary>The request document of <paramref name="address"/>'s latest task.</summary>
    private static async Task<JsonObjectValue?> LatestRequestAsync(AgentSession session, string address)
    {
        var directory = await session.Harness.SnapshotAsync(AgentDirectory.Doc, TestHost.Context);
        var id = directory?["agents"] is JsonObjectValue agents && agents.TryGetValue(address, out var entry) && entry is JsonObjectValue value
            ? Mailboxes.Str(value, "lastRequestId")
            : null;
        return id is null ? null : await session.Harness.SnapshotAsync(Requests.Doc, id, TestHost.Context);
    }

    internal static IEnumerable<(string, Func<Task>)> MessagingCases()
    {
        yield return Case("agents.msg.send-idle-starts-turn", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads" });
            team.Catalog.Register(new AgentDefinition { Name = "helper", Description = "Helps" });
            var heard = new TaskCompletionSource<FauxRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("send_message", Json(new { to = "helper", text = "build <the> thing" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "send_message", request => "lead: " + request.LastToolResult);
            models.On(request => Is(request, "helper"), request =>
            {
                heard.TrySetResult(request);
                return "on it";
            });
            await using var session = await team.OpenAsync(new MemoryStorage());
            await session.StartAgentAsync("helper", "helper");
            var reply = await session.AskAsync("tell helper");
            Check(reply.Text.StartsWith("lead: Sent to helper", StringComparison.Ordinal), "the tool reports the send: " + reply.Text);
            var request = await heard.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(request.LastUserText!.Contains("<agent-message from=\"lead\"", StringComparison.Ordinal), "a header names the sender: " + request.LastUserText);
            Check(request.LastUserText.Contains("build &lt;the&gt; thing", StringComparison.Ordinal), "the body is escaped: " + request.LastUserText);
            var mailbox = await MailboxAsync(session, "helper");
            Equal(1, mailbox.Count, "one mailbox record");
            Equal("lead", mailbox[0].From, "the authoritative sender");
        });

        yield return Case("agents.msg.idempotent-id", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads" });
            var turns = 0;
            models.On(request => Is(request, "lead"), _ =>
            {
                Interlocked.Increment(ref turns);
                return "ok";
            });
            await using var session = await team.OpenAsync(new MemoryStorage());
            var first = await session.Host.SendAsync("lead", "ping", new AgentSendOptions { Id = "fixed-1" });
            var second = await session.Host.SendAsync("lead", "ping", new AgentSendOptions { Id = "fixed-1" });
            Equal(first.Id, second.Id, "the same message");
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref turns) >= 1), TimeSpan.FromSeconds(20));
            await session.Orchestrator!.WaitForIdleAsync(TestHost.Context);
            Equal(1, Volatile.Read(ref turns), "delivered once");
            Equal(1, (await MailboxAsync(session, "lead")).Count, "recorded once");
        });

        yield return Case("agents.msg.scope-refuses", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads" });
            team.Catalog.Register(new AgentDefinition { Name = "quiet", Description = "Keeps to itself", Messaging = MessagingScope.None });
            FauxRequest? quiet = null;
            models.On(request => Is(request, "quiet"), request =>
            {
                quiet = request;
                return "alone";
            });
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("send_message", Json(new { to = "nobody", text = "hi" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "send_message", request =>
                (request.ToolResults[0].IsError ? "refused: " : "sent: ") + request.LastToolResult);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("message nobody");
            Check(reply.Text.StartsWith("refused: There is no agent 'nobody'; use list_agents.", StringComparison.Ordinal), "an unknown agent is refused: " + reply.Text);
            await session.AskAsync("hello", await session.StartAgentAsync("quiet"));
            Check(!quiet!.Tools.Contains("send_message"), "messaging None withholds the tools: " + string.Join(",", quiet.Tools));
            // Family scope: parent, children, siblings, and the roots among themselves; not another root's children.
            var definition = new AgentDefinition { Name = "worker", Description = "Works" };
            var child = Sender.Of(new AgentEntry("worker#1", new ConversationId(10), "worker", AgentKind.Child, "lead", 1, false), definition);
            AgentEntry Entry(string address, string? parent) => new(address, new ConversationId(address.Length + 20), "worker", parent is null ? AgentKind.Peer : AgentKind.Child,
                parent, parent is null ? 0 : 1, false);
            Check(MessagingCore.CanReach(team, child, Entry("lead", null)), "its parent");
            Check(MessagingCore.CanReach(team, child, Entry("worker#2", "lead")), "a sibling");
            Check(!MessagingCore.CanReach(team, child, Entry("peer", null)), "not another root");
            Check(!MessagingCore.CanReach(team, child, Entry("worker#3", "peer")), "not another root's child");
            var wide = Sender.Of(child.Entry!, definition with { Messaging = MessagingScope.Team });
            Check(MessagingCore.CanReach(team, wide, Entry("worker#3", "peer")), "Team scope reaches everyone");
        });

        yield return Case("agents.msg.host-send-and-watch", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", CanAskHost = true });
            team.Catalog.Register(new AgentDefinition { Name = "mute", Description = "May not talk to the host" });
            models.On(request => Is(request, "lead") && !request.AfterTools && Heard(request, "message", "host"),
                _ => FauxReply.ToolCall("send_message", Json(new { to = "host", text = "status: green" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "send_message", _ => "reported");
            models.On(request => Is(request, "mute") && !request.AfterTools, _ => FauxReply.ToolCall("send_message", Json(new { to = "host", text = "psst" })));
            models.On(request => Is(request, "mute") && request.LastToolName == "send_message", request =>
                (request.ToolResults[0].IsError ? "refused: " : "sent: ") + request.LastToolResult);
            await using var session = await team.OpenAsync(new MemoryStorage());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var watching = Task.Run(async () =>
            {
                await foreach (var message in session.Host.WatchMailboxAsync(timeout.Token)) return message;
                return null;
            });
            await session.Host.SendAsync("lead", "report your status");
            var received = await watching;
            Equal("lead", received!.From, "the host hears the agent");
            Equal("status: green", received.Text, "the text");
            var reply = await session.AskAsync("say something", await session.StartAgentAsync("mute"));
            Check(reply.Text.StartsWith("refused: You may not message the host", StringComparison.Ordinal), "CanAskHost is required: " + reply.Text);
            Equal(1, (await session.Host.ReadMailboxAsync()).Count, "only the allowed message reached the host");
        });

        yield return Case("agents.msg.task-adapter-idempotent", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads" });
            var turns = 0;
            models.On(request => Is(request, "lead"), _ =>
            {
                Interlocked.Increment(ref turns);
                return "ok";
            });
            var attempts = 0;
            var workflow = Workflow.For<OrderData>("notify")
                .Then("send", async (data, context, token) =>
                {
                    var sent = await context.Messages.SendAsync("lead", "order " + data.OrderId + " is in", cancellationToken: token);
                    data.Note = sent.From;
                    if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("flaky after sending");
                }, new ActionOptions { Retry = RetryPolicy.Fixed(2) })
                .Build();
            team.Workflows.Add(workflow);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var run = await session.Workflows.StartAsync(workflow, new OrderData { OrderId = "7" });
            var result = await run.WaitAsync();
            Equal(2, attempts, "the action ran twice");
            Equal(AgentMessaging.WorkflowAddress(run.TaskId), result.EnsureCompleted().Note, "sent as the run");
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref turns) >= 1), TimeSpan.FromSeconds(20));
            await session.Orchestrator!.WaitForIdleAsync(TestHost.Context);
            Equal(1, Volatile.Read(ref turns), "the retried attempt did not send again");
            Equal(1, (await MailboxAsync(session, "lead")).Count, "one record");
        });

        yield return Case("agents.msg.child-to-blocked-parent-routed", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
            team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Works" });
            var leadNotes = 0;
            models.On(request => Is(request, "lead") && Heard(request, "message"), _ =>
            {
                Interlocked.Increment(ref leadNotes);
                return "a note arrived as input";
            });
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "count to 3" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "delegate", request => "lead: " + request.LastToolResult);
            models.On(request => Is(request, "worker") && !request.AfterTools, _ => FauxReply.ToolCall("send_message", Json(new { to = "parent", text = "halfway there" })));
            models.On(request => Is(request, "worker") && request.LastToolName == "send_message", _ => "1, 2, 3");
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("delegate the count");
            Check(reply.Text.StartsWith("lead: worker#1 answered:\n1, 2, 3", StringComparison.Ordinal), "the answer: " + reply.Text);
            Check(reply.Text.Contains("Notes worker#1 sent meanwhile:\n- halfway there", StringComparison.Ordinal), "the note came with the answer: " + reply.Text);
            await session.Orchestrator!.WaitForIdleAsync(TestHost.Context);
            Equal(0, Volatile.Read(ref leadNotes), "the note was not also submitted as input");
        });

        yield return Case("agents.ask.cycle-refused-no-submission", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"], Delegation = DelegationMode.Strict });
            team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Works" });
            var questions = 0;
            models.On(request => Is(request, "lead") && Heard(request, "question"), _ =>
            {
                Interlocked.Increment(ref questions);
                return "a question arrived";
            });
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "pick a color" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "delegate", request => "lead: " + request.LastToolResult);
            models.On(request => Is(request, "worker") && !request.AfterTools, _ => FauxReply.ToolCall("ask_agent", Json(new { to = "parent", question = "which color?" })));
            models.On(request => Is(request, "worker") && request.LastToolName == "ask_agent", request =>
                (request.ToolResults[0].IsError ? "refused: " : "asked: ") + request.LastToolResult);
            await using var session = await team.OpenAsync(new MemoryStorage());
            var reply = await session.AskAsync("delegate strictly");
            Check(reply.Text.Contains("refused: lead is waiting on you", StringComparison.Ordinal), "the cycle is refused with guidance: " + reply.Text);
            Equal(0, Volatile.Read(ref questions), "no question reached the parent");
            Check(!(await MailboxAsync(session, "lead")).Any(message => message.Kind == AgentMessageKind.Question), "nothing in its mailbox");
        });

        yield return Case("agents.ask.child-asks-delegating-parent-escalates", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads", Subagents = ["worker"] });
            team.Catalog.Register(new AgentDefinition { Name = "worker", Description = "Works" });
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            models.On(request => Is(request, "lead") && !request.AfterTools, _ => FauxReply.ToolCall("delegate", Json(new { agent = "worker", task = "paint the shed" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "delegate", request =>
            {
                var id = Regex.Match(request.LastToolResult!, @"message id (\S+)\)").Groups[1].Value;
                return FauxReply.ToolCall("reply", Json(new { id, text = "blue" }));
            });
            models.On(request => Is(request, "lead") && request.LastToolName == "reply", _ => FauxReply.ToolCall("wait_agent", Json(new { agent = "worker#1" })));
            models.On(request => Is(request, "lead") && request.LastToolName == "wait_agent", request => "final: " + request.LastToolResult);
            models.On(request => Is(request, "worker") && !request.AfterTools, _ => FauxReply.ToolCall("ask_agent", Json(new { to = "parent", question = "which color?" })));
            models.On(request => Is(request, "worker") && request.LastToolName == "ask_agent", request => FauxReply.Answer("painted " + request.LastToolResult).HeldUntil(release.Task));
            await using var session = await team.OpenAsync(new MemoryStorage());
            // The worker answers only once wait_agent has re-attached, so the answer goes to that call (not a report).
            var reattached = Task.Run(async () =>
            {
                await WaitUntilAsync(async () => await LatestRequestAsync(session, "worker#1") is { } request &&
                    Requests.State(request) == Requests.Waiting && Mailboxes.Str(request, "onCallerEnd") == "detach", TimeSpan.FromSeconds(20));
                release.TrySetResult();
            });
            var reply = await session.AskAsync("get the shed painted");
            await reattached;
            Equal("final: worker#1 answered:\npainted blue", reply.Text, "the question reached the waiting parent, and the answer the next wait");
            Equal(Requests.Tool, Requests.State(await LatestRequestAsync(session, "worker#1")), "claimed by wait_agent");
            Check(!models.Requests.Any(request => Is(request, "lead") && Heard(request, "question")), "the question was not submitted as input");
        });

        yield return Case("agents.ask.timeout-detaches-and-reports-once", async () =>
        {
            var models = new FauxAgentModels();
            var team = Team(models, "lead");
            team.Catalog.Register(new AgentDefinition { Name = "lead", Description = "Leads" });
            team.Catalog.Register(new AgentDefinition { Name = "helper", Description = "Slow" });
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = 0;
            models.On(request => Is(request, "lead") && Heard(request, "result", "helper"), request =>
            {
                Interlocked.Increment(ref reports);
                return "thanks";
            });
            models.On(request => Is(request, "lead") && !request.AfterTools, _ =>
                FauxReply.ToolCall("ask_agent", Json(new { to = "helper", question = "what is 2+2?", timeoutSeconds = 1 })));
            models.On(request => Is(request, "lead") && request.LastToolName == "ask_agent", request => "lead: " + request.LastToolResult);
            models.On(request => Is(request, "helper"), _ => FauxReply.Answer("4").HeldUntil(release.Task));
            await using var session = await team.OpenAsync(new MemoryStorage());
            await session.StartAgentAsync("helper", "helper");
            var reply = await session.AskAsync("ask the helper");
            Check(reply.Text.Contains("No answer from helper within 1 s. Its answer will arrive as a message from helper.", StringComparison.Ordinal),
                "the ask detached: " + reply.Text);
            release.TrySetResult();
            await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref reports) >= 1), TimeSpan.FromSeconds(20));
            await session.Orchestrator!.WaitForIdleAsync(TestHost.Context);
            await Task.Delay(200);
            Equal(1, Volatile.Read(ref reports), "the late answer was reported once");
            var results = (await MailboxAsync(session, "lead")).Where(message => message.Kind == AgentMessageKind.Result).ToList();
            Equal(1, results.Count, "one result record");
            Equal("4", results[0].Text, "the late answer");
        });
    }
}
