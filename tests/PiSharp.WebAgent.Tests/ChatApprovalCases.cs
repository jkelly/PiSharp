using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;

// Decision 0011 (run W2, WU7): the web-approval extension on a real durable device with faux models. A call that changes things waits for
// the user (and is listed again after a reload); the first decision wins; a crash while it waits asks again after the restart; a crash after
// the decision never asks again and the command runs once (a marker file counts the runs); read-only tools never ask; deny tells the model why;
// "allow in this chat" and "allow always" stop the asking. The bash command appends a line to a marker file in the case's own folder.
internal static partial class Program
{
    private const string MarkerCommand = "!echo once >> marker.txt";

    internal static IEnumerable<(string, Func<Task>)> ChatApprovalCases() =>
    [
        Case("chat.approval.asks-survives-reload-first-wins", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-approval-first");
            var binding = await chat.CreateAsync("hello", "c1");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", MarkerCommand));
            var pending = await chat.PendingAsync(binding.SessionId, "bash");
            Equal("echo once >> marker.txt", pending.Request.Command, "the command waits");
            Equal(0, Lines(Path.Join(chat.Work, "marker.txt")).Length, "nothing ran yet");
            // Two reloads of the page each show the same prompt, inline in the call's tool card.
            for (var reload = 0; reload < 2; reload++)
            {
                var page = await chat.Chats.OpenAsync(binding.SessionId);
                var html = string.Concat(await Task.WhenAll(page.Items.Select(item =>
                    chat.Services.GetRequiredService<PiSharp.Web.Common.FragmentRenderer>().RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(item, page.Context)))));
                Check(html.Contains($"data-approval=\"{pending.Request.TaskId}\"", StringComparison.Ordinal), "reload " + reload + " shows the prompt");
                Check(page.Busy, "the chat counts as running while it waits");
            }
            Equal(1, chat.Broker.Pending(binding.SessionId).Count, "still one prompt");
            var first = chat.Chats.DecideApprovalAsync(binding.SessionId, pending.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce));
            var second = await chat.Chats.DecideApprovalAsync(binding.SessionId, pending.Request.TaskId, new ApprovalDecision(ApprovalChoice.Deny, "late"));
            Equal(ApprovalOutcomeStatus.AlreadyDecided, second.Status, "the second decision is too late");
            Equal(ApprovalChoice.AllowOnce, second.Decision!.Choice, "and learns the first");
            Equal(ApprovalOutcomeStatus.Decided, (await first).Status, "the first decides");
            await chat.IdleAsync(binding.SessionId);
            Equal(1, Lines(Path.Join(chat.Work, "marker.txt")).Length, "the command ran once");
            Check((await chat.TextAsync(binding.SessionId)).Contains("Tool finished: bash", StringComparison.Ordinal), "the model got the result");
            Equal(0, chat.Broker.Pending(binding.SessionId).Count, "nothing waits");
        }),
        Case("chat.approval.crash-while-waiting-asks-again", async () =>
        {
            var root = Temp("chat-approval-crash-waiting");
            try
            {
                var (settings, bindings) = await ChatStoresAsync(root);
                var models = new FauxDurableModels();
                var broker = new ApprovalBroker();
                string sessionId;
                var device = await DurableDeviceHost.OpenAsync(DeviceOptions(root, models, settings, bindings, broker));
                try
                {
                    var session = await device.CreateSessionAsync(new SessionCreateRequest { Cwd = Path.Join(root, "work"), Name = "crash", Model = FauxDurableModels.EchoRef, ThinkingLevel = "off" });
                    sessionId = session.Id;
                    await session.SendAsync(1, new SendRequest(MarkerCommand, "r1"));
                    await WaitUntilAsync(() => Task.FromResult(broker.Pending(sessionId).Count == 1), "the call asks", TimeSpan.FromSeconds(30));
                }
                finally
                {
                    await device.DisposeAsync();
                }
                Equal(0, Lines(Path.Join(root, "work", "marker.txt")).Length, "nothing ran before the crash");
                var restarted = new ApprovalBroker();
                await using var reopened = await DurableDeviceHost.OpenAsync(DeviceOptions(root, models, settings, bindings, restarted));
                await WaitUntilAsync(() => Task.FromResult(restarted.Pending(sessionId).Count == 1), "the recovered call asks again", TimeSpan.FromSeconds(30));
                var request = restarted.Pending(sessionId)[0].Request;
                Equal("bash", request.Tool, "the same call");
                Equal(ApprovalOutcomeStatus.Decided, (await restarted.DecideAsync(sessionId, request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce))).Status, "allowed");
                var session2 = reopened.Sessions.First(item => item.Id == sessionId);
                await WaitUntilAsync(async () => !(await session2.AgentsAsync()).Any(agent => agent.Busy) && Lines(Path.Join(root, "work", "marker.txt")).Length > 0,
                    "the command ran", TimeSpan.FromSeconds(30));
                Equal(1, Lines(Path.Join(root, "work", "marker.txt")).Length, "once");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
        Case("chat.approval.crash-after-allow-never-asks-again", async () =>
        {
            var root = Temp("chat-approval-crash-decided");
            try
            {
                var (settings, bindings) = await ChatStoresAsync(root);
                var models = new FauxDurableModels();
                var broker = new ApprovalBroker();
                var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // A second extension, installed after web-approval, holds the call after the decision is stored: the crash comes between
                // the decision and the command.
                var blocker = Define.Extension(new Extension
                {
                    Name = "test-blocker",
                    Hooks =
                    [
                        Define.Hook(HarnessTasks.ToolTask, new ToolHooks
                        {
                            BeforeTool = async (call, api, context) =>
                            {
                                blocked.TrySetResult();
                                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                                return null;
                            },
                        }),
                    ],
                });
                var options = DeviceOptions(root, models, settings, bindings, broker);
                var plain = options.CreateRegistry!;
                string sessionId;
                var device = await DurableDeviceHost.OpenAsync(options with
                {
                    CreateRegistry = context =>
                    {
                        var registry = plain(context);
                        registry.Install(blocker);
                        return registry;
                    },
                });
                try
                {
                    var session = await device.CreateSessionAsync(new SessionCreateRequest { Cwd = Path.Join(root, "work"), Name = "crash", Model = FauxDurableModels.EchoRef, ThinkingLevel = "off" });
                    sessionId = session.Id;
                    await session.SendAsync(1, new SendRequest(MarkerCommand, "r1"));
                    await WaitUntilAsync(() => Task.FromResult(broker.Pending(sessionId).Count == 1), "the call asks", TimeSpan.FromSeconds(30));
                    var request = broker.Pending(sessionId)[0].Request;
                    Equal(ApprovalOutcomeStatus.Decided, (await broker.DecideAsync(sessionId, request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce))).Status,
                        "allowed and stored");
                    await blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    await device.DisposeAsync();
                }
                Equal(0, Lines(Path.Join(root, "work", "marker.txt")).Length, "the crash came before the command");
                var restarted = new ApprovalBroker();
                var asked = 0;
                restarted.Changed += _ => Interlocked.Increment(ref asked);
                await using var reopened = await DurableDeviceHost.OpenAsync(DeviceOptions(root, models, settings, bindings, restarted));
                var session2 = reopened.Sessions.First(item => item.Id == sessionId);
                await WaitUntilAsync(async () => Lines(Path.Join(root, "work", "marker.txt")).Length > 0 && !(await session2.AgentsAsync()).Any(agent => agent.Busy),
                    "the recovered call ran", TimeSpan.FromSeconds(30));
                Equal(0, Volatile.Read(ref asked), "it never asked again");
                Equal(1, Lines(Path.Join(root, "work", "marker.txt")).Length, "the command ran once");
                var text = Transcript.ToMarkdown(Transcript.Build(await PiSharp.Durable.Server.AgentFrames.ActiveEntriesAsync(session2, 1)));
                Check(text.Contains("Tool finished: bash", StringComparison.Ordinal), "the model got the result: " + text);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
        Case("chat.approval.read-only-never-asks", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-approval-read");
            await File.WriteAllTextAsync(Path.Join(chat.Work, "note.txt"), "a note");
            var asked = 0;
            chat.Broker.Changed += _ => Interlocked.Increment(ref asked);
            var binding = await chat.CreateAsync("tool: read {\"path\":\"note.txt\"}", "c1");
            Check((await chat.TextAsync(binding.SessionId)).Contains("Tool finished: read", StringComparison.Ordinal), "the read ran");
            Equal(0, Volatile.Read(ref asked), "it never asked");
        }),
        Case("chat.approval.deny-tells-the-model-why", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-approval-deny");
            var binding = await chat.CreateAsync("hello", "c1");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", MarkerCommand));
            var pending = await chat.PendingAsync(binding.SessionId);
            Equal(ApprovalOutcomeStatus.Decided, (await chat.Chats.DecideApprovalAsync(binding.SessionId, pending.Request.TaskId,
                new ApprovalDecision(ApprovalChoice.Deny, "not today"))).Status, "denied");
            await chat.IdleAsync(binding.SessionId);
            Equal(0, Lines(Path.Join(chat.Work, "marker.txt")).Length, "the command never ran");
            var text = await chat.TextAsync(binding.SessionId);
            Check(text.Contains("The user denied this bash call. Reason: not today", StringComparison.Ordinal), "the reason reached the model: " + text);
        }),
        Case("chat.approval.allow-in-chat-and-always", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-approval-grants", mode: ToolApprovalMode.AllowList);
            var binding = await chat.CreateAsync("hello", "c1");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", MarkerCommand));
            var pending = await chat.PendingAsync(binding.SessionId);
            await chat.Chats.DecideApprovalAsync(binding.SessionId, pending.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowChat));
            await chat.IdleAsync(binding.SessionId);
            Equal("bash", chat.Broker.Grants(binding.SessionId).Single().Tool, "the chat has a bash grant");
            var asked = 0;
            chat.Broker.Changed += _ => Interlocked.Increment(ref asked);
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s2", "!echo twice >> marker.txt"));
            await WaitUntilAsync(() => Task.FromResult(Lines(Path.Join(chat.Work, "marker.txt")).Length == 2), "the granted call ran", TimeSpan.FromSeconds(30));
            await chat.IdleAsync(binding.SessionId);
            Equal(0, Volatile.Read(ref asked), "the grant did not ask");

            var other = await chat.CreateAsync("hello again", "c2");
            await chat.Chats.SendAsync(other.SessionId, new ChatSendRequest("s3", "!echo always >> marker.txt"));
            var second = await chat.PendingAsync(other.SessionId);
            await chat.Chats.DecideApprovalAsync(other.SessionId, second.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowAlways));
            await chat.IdleAsync(other.SessionId);
            Check(chat.Settings.Get<ToolsSettings>().AllowList!.Contains(new ToolAllowRule("bash", "echo always >> marker.txt")), "the allow-list has the command");
            var third = await chat.CreateAsync("third", "c3");
            asked = 0;
            await chat.Chats.SendAsync(third.SessionId, new ChatSendRequest("s4", "!echo always >> marker.txt"));
            await WaitUntilAsync(() => Task.FromResult(Lines(Path.Join(chat.Work, "marker.txt")).Length == 4), "the allow-listed call ran", TimeSpan.FromSeconds(30));
            await chat.IdleAsync(third.SessionId);
            Equal(0, Volatile.Read(ref asked), "the allow-list did not ask in another chat");
        }),
        Case("chat.approval.auto-mode-never-asks", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-approval-auto", mode: ToolApprovalMode.Auto);
            var asked = 0;
            chat.Broker.Changed += _ => Interlocked.Increment(ref asked);
            await chat.CreateAsync(MarkerCommand, "c1");
            Equal(1, Lines(Path.Join(chat.Work, "marker.txt")).Length, "it ran");
            Equal(0, Volatile.Read(ref asked), "without asking");
        }),
    ];
}
