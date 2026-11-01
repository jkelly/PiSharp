// Decision 0011 (run W4, integration fixes 2): the chat-owner seams agent compare needed, each with the case that would have caught it.
// The device models hook: DeviceHostService streams through the app's IDurableModels under every IDeviceModelsDecorator, in registration
// order, while the IDurableModels registration stays the plain one; agent compare adds its gate through the hook (one DeviceHostService
// registration, no RemoveAll). Read-only views: AgentViewHub renders a session SessionViews knows, chat or not, without actions, approval
// cards or interactive panels, in a feed of its own; ChatStreamMux serves them under session/{session}/{agent}; and an agent compare
// model's session streams its transcript that way before it is opened as a chat (the column's Transcript), while a chat key for it is
// refused until then. Faux models only; no case calls a provider.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.AgentCompare.Runner;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using DurableJson = PiSharp.Durable.Chord.JsonValue;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> W4IntegrationFix2Cases() =>
    [
        Case("w4.fix2.device-models-hook-wraps-only-the-device", async () =>
        {
            var inner = new ConcurrentQueue<string>();
            await using var chat = await ChatHarness.StartAsync("w4-fix2-device-models", configure: services =>
            {
                services.AddDeviceModelsDecorator((_, models) => new TaggedModels(models, "inner", inner));
                services.AddDeviceModelsDecorator((_, models) => new TaggedModels(models, "outer", inner));
            });
            var outer = chat.Device.Models as TaggedModels ?? throw new InvalidOperationException("the device streams through the last decorator");
            Equal("outer", outer.Tag, "the last registered is the outermost");
            var under = outer.Inner as TaggedModels ?? throw new InvalidOperationException("over the first decorator");
            Equal("inner", under.Tag, "the first registered is the innermost");
            Check(ReferenceEquals(under.Inner, chat.Services.GetRequiredService<IDurableModels>()), "over the app's models");
            Check(chat.Services.GetRequiredService<IDurableModels>() is not TaggedModels, "the IDurableModels registration is the plain one");
            await chat.CreateAsync("through the hook", "hook-1");
            Equal("outer,inner", string.Join(",", inner.Take(2)), "a chat's request passes both decorators, outer first");

            // Agent compare adds its gate through the hook: one DeviceHostService registration, never replaced.
            var root = Temp("w4-fix2-registrations");
            try
            {
                var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true };
                options.EnsureDirectories();
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton(options);
                services.AddSingleton(new WebAgentDb(options.DatabasePath, [CoreMigrations.AppInfo]));
                services.AddChatArea(options);
                services.AddAgentCompareRunner(options);
                Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(DeviceHostService)), "one DeviceHostService registration");
                Check(services.Any(descriptor => descriptor.ServiceType == typeof(ISessionViewSource)), "agent compare says what its sessions are");
                using var provider = services.BuildServiceProvider();
                var faux = new FauxDurableModels();
                var device = DeviceModels.Apply(faux, provider.GetServices<IDeviceModelsDecorator>());
                var gate = device as AgentCompareModels ?? throw new InvalidOperationException("the device's models are agent compare's gate: " + device.GetType().Name);
                Check(ReferenceEquals(gate.Inner, faux), "over the app's models");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("w4.fix2.read-only-view-of-a-session-without-a-chat", async () =>
        {
            var known = new ConcurrentDictionary<string, SessionView>(StringComparer.Ordinal);
            await using var chat = await ChatHarness.StartAsync("w4-fix2-read-only", configure: services =>
                services.AddSingleton<ISessionViewSource>(new KnownSessions(known)));
            var views = chat.Services.GetRequiredService<SessionViews>();
            var session = await chat.Device.Host.CreateSessionAsync(new SessionCreateRequest
            {
                Cwd = chat.Work, Name = "Not a chat", Model = new ModelRef("faux", "echo"), ThinkingLevel = "off",
            });
            await session.SendAsync(1, new SendRequest("hello unbound", "u1"));
            await chat.IdleAsync(session.Id);
            Check(chat.Bindings.Get(session.Id) is null, "the session has no chat binding");
            Check(!views.Knows(session.Id), "a session no source names cannot be watched");
            known[session.Id] = new SessionView(session.Id, "A: Echo", "faux", "echo", null, "off");
            Equal("faux/echo", views.Find(session.Id) is { } view ? view.Provider + "/" + view.Model : null, "its view, from the source");

            var sink = new FrameSink();
            using (chat.Hub.Subscribe(session.Id, 1, sink.Add, readOnly: true))
            {
                var whole = await sink.WaitAsync(frame => IsView(frame, true), "the read-only view");
                var html = Html(whole);
                Check(html.Contains("Echo: hello unbound", StringComparison.Ordinal) && html.Contains("hello unbound", StringComparison.Ordinal), "the transcript: " + html);
                Check(!html.Contains("data-action=", StringComparison.Ordinal), "no action buttons: " + html);
                Check(whole["state"]?["readOnly"]?.GetValue<bool>() == true, "the state says read-only");
                Equal("notices", string.Join(",", (whole["panels"] as JsonObject ?? []).Select(panel => panel.Key)), "only the notices panel");
                await session.SendAsync(1, new SendRequest("second one", "u2"));
                await sink.WaitAsync(frame => IsView(frame, false) && Html(frame).Contains("Echo: second one", StringComparison.Ordinal), "a live change");
            }
            await WaitUntilAsync(() => Task.FromResult(chat.Hub.FeedCount == 0), "the read-only feed stops with its last subscriber");

            // A chat: its page's feed and a read-only feed are separate; only the page's has actions and the inline approval card.
            var binding = await chat.CreateAsync("a real chat", "c1");
            Check(views.Knows(binding.SessionId), "a chat's binding answers for its read-only view");
            var page = new FrameSink();
            var watcher = new FrameSink();
            using var pageFeed = chat.Hub.Subscribe(binding.SessionId, 1, page.Add);
            using var readOnlyFeed = chat.Hub.Subscribe(binding.SessionId, 1, watcher.Add, readOnly: true);
            await page.WaitAsync(frame => IsView(frame, true), "the page's view");
            await watcher.WaitAsync(frame => IsView(frame, true), "the read-only view");
            Equal(2, chat.Hub.FeedCount, "two feeds of one agent");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", "!echo asked"));
            var pending = await chat.PendingAsync(binding.SessionId);
            await page.WaitAsync(frame => Html(frame).Contains("data-approval=", StringComparison.Ordinal), "the page shows the approval card");
            var running = await watcher.WaitAsync(frame => IsView(frame, false) && Html(frame).Contains("echo asked", StringComparison.Ordinal), "the read-only view shows the call");
            Check(!Html(running).Contains("data-approval=", StringComparison.Ordinal), "without the approval card");
            Check(page.Frames.Any(frame => Html(frame).Contains("data-action=\"fork\"", StringComparison.Ordinal)), "the page's items can fork");
            Check(watcher.Frames.All(frame => !Html(frame).Contains("data-action=", StringComparison.Ordinal)), "the read-only items never can");
            await chat.Chats.DecideApprovalAsync(binding.SessionId, pending.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce));
            await chat.IdleAsync(binding.SessionId);
        }),

        Case("w4.fix2.agent-compare-column-streams-its-transcript-before-open-as-chat", async () =>
        {
            await using var app = await CompareApp.StartAsync("w4-fix2-ac-transcript", ToolApprovalMode.Auto);
            var run = await app.CreateAsync(new JsonObject
            {
                ["requestId"] = "transcript-1",
                ["task"] = "!echo seen > seen.txt",
                ["projectPath"] = app.Project,
                ["models"] = Models("echo"),
                ["isolation"] = "copy",
                ["approval"] = "ask",
            });
            var waiting = await app.WaitAsync(run.Id(), status => status.Models().All(model => model.Str("waiting") == "approval"), "the model asks");
            var sessionId = waiting.Models().Single().Str("sessionId") ?? throw new InvalidOperationException("the model has a session");
            var key = ChatStreamMux.SessionKey(sessionId, 1);
            var column = await app.App.Get($"/compare/agent/runs/{run.Id()}");
            Equal(200, column.Status, "the run's view");
            Check(column.Text.Contains($"data-ac-transcript=\"{key}\"", StringComparison.Ordinal), "the column offers its transcript on the session key");

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.App.Base, "/api/stream"));
            using var response = await app.App.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
            var sse = new SseReader(reader);
            var hello = await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == ChatStreamMux.HelloKey, "the stream's hello");
            var stream = "/api/stream/" + hello["frame"]!["streamId"]!.GetValue<string>();
            Equal(400, (await app.App.SendAsync(HttpMethod.Put, stream, new JsonObject { ["subs"] = new JsonArray(ChatStreamMux.AgentKey(sessionId, 1)) })).Status,
                "the model's session is not a chat yet");
            Equal(400, (await app.App.SendAsync(HttpMethod.Put, stream, new JsonObject { ["subs"] = new JsonArray("session/unknown/1") })).Status,
                "a session no one names");
            var put = await app.App.SendAsync(HttpMethod.Put, stream, new JsonObject { ["subs"] = new JsonArray(key) });
            Equal(200, put.Status, "subscribed read-only: " + put.Text);
            var whole = await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == key && frame["frame"]?["type"]?.GetValue<string>() == "view", "the transcript");
            var html = Html(whole["frame"]!.AsObject());
            Check(html.Contains("echo seen &gt; seen.txt", StringComparison.Ordinal) || html.Contains("echo seen > seen.txt", StringComparison.Ordinal),
                "the waiting call shows: " + html);
            Check(!html.Contains("data-action=", StringComparison.Ordinal) && !html.Contains("data-approval=", StringComparison.Ordinal), "read-only: no actions, no approval card");
            Check(whole["frame"]!["state"]?["readOnly"]?.GetValue<bool>() == true, "the state says read-only");

            var allowed = await app.PostAsync($"/api/compare/agent/{run.Id()}/approve", new JsonObject { ["scope"] = "all", ["items"] = Listed(waiting), ["decision"] = "allow" });
            Equal(200, allowed.Status, "allowed: " + allowed.Text);
            await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == key && frame["frame"]?["reset"]?.GetValue<bool>() == false &&
                Html(frame["frame"]!.AsObject()).Contains("Tool finished", StringComparison.Ordinal), "the answer streams in", TimeSpan.FromSeconds(45));
            await app.WaitAsync(run.Id(), status => status.Str("state") == "finished", "the run finished");

            var opened = await app.PostAsync($"/api/compare/agent/{run.Id()}/open-chat", new JsonObject { ["slot"] = "a" });
            Equal(200, opened.Status, "opened as a chat: " + opened.Text);
            Equal(200, (await app.App.SendAsync(HttpMethod.Put, stream, new JsonObject { ["subs"] = new JsonArray(key, ChatStreamMux.AgentKey(sessionId, 1)) })).Status,
                "then both its read-only and its chat key");
        }),
    ];

    /// <summary>A decorator of the device's models that records its tag on each request and passes everything through.</summary>
    private sealed class TaggedModels(IDurableModels inner, string tag, ConcurrentQueue<string> seen) : IDurableModels
    {
        public IDurableModels Inner { get; } = inner;

        public string Tag { get; } = tag;

        public DurableModel? GetModel(string provider, string modelId) => Inner.GetModel(provider, modelId);

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<DurableJson> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken)
        {
            seen.Enqueue(Tag);
            return Inner.StreamSimple(model, messages, options, cancellationToken);
        }

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<DurableJson> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken) => Inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            Inner.StreamDeferred(model, handle, cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            Inner.FetchDeferredAsync(model, handle, cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            Inner.CancelDeferredAsync(model, handle, cancellationToken);
    }

    /// <summary>A view source that names the sessions in <paramref name="known"/>.</summary>
    private sealed class KnownSessions(ConcurrentDictionary<string, SessionView> known) : ISessionViewSource
    {
        public SessionView? Find(string sessionId) => known.TryGetValue(sessionId, out var view) ? view : null;
    }
}
