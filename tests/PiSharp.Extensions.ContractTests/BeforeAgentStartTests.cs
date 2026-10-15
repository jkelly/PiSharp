using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Compaction;
using NativeAgent = PiSharp.Agent.Agent;

internal static class BeforeAgentStartTests
{
    internal const string Prefix = "before-agent-start.";
    private static readonly ModelDescriptor Model = new("before-start", "openai-responses", "fixture");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "custom-tailed restored history continues without another prompt or duplicate input", RestoredCustomTail),
        (Prefix + "failed assistant recovery retains custom tail and completes internal continuation", RecoveryCustomTail),
        (Prefix + "actual durable custom entries survive reopen while forced prompts remain request-only", Durable),
        (Prefix + "shared binding keeps prepared prompt state isolated between agents", Isolation),
        (Prefix + "once per prompt with ordered snapshots and continuation projection", Continuation),
        (Prefix + "invalid custom content and callback errors retain prior prompt and continue", Invalid),
        (Prefix + "operation session and owner cancellation join held cleanup before any request", Cancellation),
        (Prefix + "custom-message run fires before_agent_start as messageType custom and keeps its prompt for every request", CustomRunEvent),
        (Prefix + "custom-message run carries pending nextTurn messages and handler messages", CustomRunMessages),
        (Prefix + "custom message sent while before_agent_start runs is queued into the starting run", QueuedIntoStartingRun),
        (Prefix + "custom message without triggerTurn sent while the run starts is recorded before it and in its first request", HeldWhileStarting),
        (Prefix + "abort requested while before_agent_start runs aborts the run as it starts", AbortWhileStarting),
        (Prefix + "user message sent while the run starts requires a delivery mode", UserMessageWhileStarting)
    ];

    // Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/agent-session.ts (_startRun, _prepareRun,
    // sendCustomMessage, abort) and packages/coding-agent/test/suite/agent-session-prompt.test.ts ("runs started by a custom
    // message"), ported to the native session. Authored expectations.
    private sealed class RunStartFixture : IAsyncDisposable
    {
        internal readonly Files Files = new();
        internal readonly ExtensionRegistry Registry = new();
        internal readonly ScriptedSource Source = new();
        internal PersistentAgentSession Session = null!;
        public async ValueTask DisposeAsync()
        {
            try { if (Session is not null) await Session.DisposeAsync(); await Registry.DisposeAsync(); }
            finally { Files.Dispose(); }
        }
    }
    private static async Task<RunStartFixture> RunStartSession(Action<IExtensionRegistry, Func<PersistentAgentSession>> register)
    {
        var fixture = new RunStartFixture();
        await fixture.Registry.ActivateAsync("owner", new Plugin(registrations => register(registrations, () => fixture.Session)));
        var binding = Binding(fixture.Registry);
        var runtime = new SessionRuntimeRegistry([new(Model, fixture.Source, Hooks: binding.Hooks)], [], new Policy());
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "run-start", timestamp = "2026-10-12T00:00:00.000Z", cwd = fixture.Files.Directory }));
        fixture.Session = await PersistentAgentSession.CreateAsync(fixture.Files.Path, header, runtime, Model, () => 123, fixture.Files.Next);
        await fixture.Session.ConfigureAsync(new(SystemMessage: Message("system", "base")));
        return fixture;
    }
    private static Task<SessionCustomMessageReceipt> Send(PersistentAgentSession session, string customType, string text,
        SessionCustomMessageDelivery delivery = SessionCustomMessageDelivery.Steer, bool? triggerTurn = true) =>
        session.SendCustomMessageAsync(session.Snapshot.Log.Header.Id, new(customType, JsonData.Parse(JsonSerializer.Serialize(text)), true), delivery, triggerTurn);
    private static string SystemText(ChatRequest request) => request.Messages[0].WireBody.Value.GetProperty("content").GetString()!;
    private static string[] UserTexts(ChatRequest request) => [.. request.Messages.Where(message => message.Role == "user").Select(message =>
    {
        var content = message.WireBody.Value.GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
    })];
    private static bool Idle(PersistentAgentSession session) => session.Snapshot is var state && !state.Agent.IsRunning && !state.IsProcessingOperation;

    // "keeps the same system prompt for every request of the run": before_agent_start fires for the custom run too, so its
    // prompt holds for the request after the tool call; the events are "user" then "custom".
    private static async Task CustomRunEvent()
    {
        var events = new List<ExtensionBeforeAgentStartEvent>();
        await using var fixture = await RunStartSession((registrations, _) => registrations.RegisterBeforeAgentStartHandler(new("rules", (input, _, _) =>
        { lock (events) events.Add(input); return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(SystemPrompt: "Answer in one word.")); })));
        var (source, session) = (fixture.Source, fixture.Session);
        source.Responses.Enqueue(_ => Answer("hi")); source.Responses.Enqueue(_ => ToolCallAnswer()); source.Responses.Enqueue(_ => Answer("done"));
        await session.PromptAsync(Message("user", "hello")).WaitAsync(TimeSpan.FromSeconds(30));
        var receipt = await Send(session, "task-done", "background task finished").WaitAsync(TimeSpan.FromSeconds(30));
        Check(receipt.Disposition == SessionCustomMessageDisposition.Started && source.Requests.Count == 3);
        Check(source.Requests.All(request => SystemText(request) == "Answer in one word."));
        Check(events.Select(e => e.MessageType).SequenceEqual([ExtensionBeforeAgentStartMessageType.User, ExtensionBeforeAgentStartMessageType.Custom]));
        Check(events[0].Prompt == "hello" && events[0].Message is null && events[1].Prompt == "" && events[1].Images is null);
        var message = events[1].Message!.Value;
        Check(message.GetProperty("role").GetString() == "custom" && message.GetProperty("customType").GetString() == "task-done" &&
            message.GetProperty("content").GetString() == "background task finished" && message.GetProperty("display").GetBoolean() &&
            message.TryGetProperty("timestamp", out _));
        Check(events[1].SystemPrompt == "base");
    }

    // _prepareRun: the starting custom message, then pending nextTurn messages (consumed), then the handlers' messages.
    private static async Task CustomRunMessages()
    {
        await using var fixture = await RunStartSession((registrations, _) => registrations.RegisterBeforeAgentStartHandler(new("context", (input, _, _) =>
            ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(input.MessageType == ExtensionBeforeAgentStartMessageType.Custom
                ? new(new("from-handler", false, JsonData.Parse(JsonSerializer.Serialize("handler context")))) : null))));
        var (source, session) = (fixture.Source, fixture.Session);
        Check((await Send(session, "note", "next turn note", SessionCustomMessageDelivery.NextTurn)).Disposition == SessionCustomMessageDisposition.Queued);
        await Send(session, "task-done", "background task finished").WaitAsync(TimeSpan.FromSeconds(30));
        Check(UserTexts(source.Requests.Single()).SequenceEqual(["background task finished", "next turn note", "handler context"]));
        Check(!session.HasPendingCustomMessages);
        Check(session.Snapshot.Log.Entries.Where(entry => entry.Kind == SessionEntryKind.CustomMessage)
            .Select(entry => entry.WireBody.Value.GetProperty("customType").GetString()).SequenceEqual(["task-done", "note", "from-handler"]));
    }

    // "queues a custom message sent while before_agent_start runs into the starting run": the session is busy while the run starts,
    // so a second triggerTurn message is steered into it and both reach the one request.
    private static async Task QueuedIntoStartingRun()
    {
        var entered = Gate(); var release = Gate();
        await using var fixture = await RunStartSession((registrations, _) => registrations.RegisterBeforeAgentStartHandler(new("wait", async (input, _, _) =>
        {
            if (input.Message?.Value.GetProperty("customType").GetString() != "first") return null;
            entered.TrySetResult(); await release.Task; return null;
        })));
        var (source, session) = (fixture.Source, fixture.Session);
        try
        {
            var first = Send(session, "first", "task one finished");
            await Reach(entered.Task, first);
            Check(!Idle(session));
            var second = await Send(session, "second", "task two finished").WaitAsync(TimeSpan.FromSeconds(30));
            Check(second.Disposition == SessionCustomMessageDisposition.Queued);
            release.TrySetResult();
            Check((await first.WaitAsync(TimeSpan.FromSeconds(30))).Disposition == SessionCustomMessageDisposition.Started);
            Check(source.Requests.Count == 1 && UserTexts(source.Requests[0]).SequenceEqual(["task one finished", "task two finished"]));
            Check(Idle(session));
        }
        finally { release.TrySetResult(); }
    }

    // "includes a custom message sent without triggerTurn while the run starts in its first request": held while the run starts,
    // it is recorded before the starting messages, as it would have been while idle.
    private static async Task HeldWhileStarting()
    {
        SessionCustomMessageReceipt? held = null;
        await using var fixture = await RunStartSession((registrations, session) => registrations.RegisterBeforeAgentStartHandler(new("note", async (input, _, _) =>
        {
            if (input.MessageType == ExtensionBeforeAgentStartMessageType.User)
                held = await Send(session(), "note", "context from handler", triggerTurn: false);
            return null;
        })));
        var (source, session) = (fixture.Source, fixture.Session);
        await session.PromptAsync(Message("user", "hello")).WaitAsync(TimeSpan.FromSeconds(30));
        Check(held?.Disposition == SessionCustomMessageDisposition.Queued);
        Check(source.Requests.Count == 1 && UserTexts(source.Requests[0]).SequenceEqual(["context from handler", "hello"]));
        var entries = session.Snapshot.Log.Entries;
        var note = entries.IndexOf(entries.Single(entry => entry.Kind == SessionEntryKind.CustomMessage));
        var user = entries.IndexOf(entries.Single(entry => entry.Kind == SessionEntryKind.Message &&
            entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user"));
        Check(note + 1 == user);
    }

    // "aborts a run when abort is requested while before_agent_start runs": the handler is not cancelled; the run starts, records
    // its starting message and ends with an aborted assistant message, and the session is idle again.
    private static async Task AbortWhileStarting()
    {
        var entered = Gate(); var release = Gate(); var cancelled = false;
        await using var fixture = await RunStartSession((registrations, _) => registrations.RegisterBeforeAgentStartHandler(new("wait", async (_, _, token) =>
        {
            entered.TrySetResult(); await release.Task; cancelled = token.IsCancellationRequested; return null;
        })));
        var (source, session) = (fixture.Source, fixture.Session);
        try
        {
            source.Responses.Enqueue(_ => Answer("should not complete"));
            var run = Send(session, "task-done", "background task finished");
            await Reach(entered.Task, run);
            Check(session.Abort());
            release.TrySetResult();
            var receipt = await run.WaitAsync(TimeSpan.FromSeconds(30));
            Check(!cancelled && receipt.Disposition == SessionCustomMessageDisposition.Started);
            var messages = session.Snapshot.Agent.Messages;
            Check(messages[^2].Role == "custom" && messages[^1].Role == "assistant" &&
                messages[^1].WireBody.Value.GetProperty("stopReason").GetString() == "aborted");
            Check(!messages.Any(message => message.Role == "assistant" && message.WireBody.ToString().Contains("should not complete", StringComparison.Ordinal)));
            await session.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(Idle(session));
        }
        finally { release.TrySetResult(); }
    }

    // sendUserMessage while the run starts: the session is busy, so a message without deliverAs fails with prompt()'s error and a
    // steered one goes into the starting run.
    private static async Task UserMessageWhileStarting()
    {
        string? error = null; SubmittedInputResult? steered = null;
        await using var fixture = await RunStartSession((registrations, session) => registrations.RegisterBeforeAgentStartHandler(new("send", async (input, _, _) =>
        {
            if (input.Prompt != "hello") return null;
            try { await session().SubmitInputAsync(new("no mode", PromptInputSource.Extension)); }
            catch (InvalidOperationException failure) { error = failure.Message; }
            steered = await session().SubmitInputAsync(new("steered", PromptInputSource.Extension, StreamingBehavior: PromptInputStreamingBehavior.Steer));
            return null;
        })));
        var (source, session) = (fixture.Source, fixture.Session);
        var result = await session.SubmitInputAsync(new("hello")).WaitAsync(TimeSpan.FromSeconds(30));
        Check(result.Disposition == SubmittedInputDisposition.Started);
        Check(error == "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.");
        Check(steered?.Disposition == SubmittedInputDisposition.Queued);
        Check(source.Requests.Count == 1 && UserTexts(source.Requests[0]).SequenceEqual(["hello", "steered"]));
    }

    private static AssistantMessage Answer(string text) =>
        new(Model.Api, Model.Provider, Model.Id, 123, [new TextContent(text)], TokenUsage.Zero, StopReason.Stop);
    private static AssistantMessage ToolCallAnswer() =>
        new(Model.Api, Model.Provider, Model.Id, 123, [new ToolCallContent("call-1", "noop", JsonData.EmptyObject)], TokenUsage.Zero, StopReason.ToolUse);
    /// <summary>Records requests and answers from a script (default: "done"), text or one tool call.</summary>
    private sealed class ScriptedSource : IChatTransport
    {
        internal readonly List<ChatRequest> Requests = [];
        internal readonly Queue<Func<ChatRequest, AssistantMessage>> Responses = new();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask; token.ThrowIfCancellationRequested();
            AssistantMessage final;
            lock (Requests) { Requests.Add(request); final = Responses.Count == 0 ? Answer("done") : Responses.Dequeue()(request); }
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (final.Content[0] is ToolCallContent call) { yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { var text = ((TextContent)final.Content[0]).Text; yield return new TextStarted(0, new("")); yield return new TextEnded(0, text); }
            yield return new StreamDone(final.StopReason, final);
        }
    }

    private static async Task RestoredCustomTail()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry(); var source = new Source(); var beforeCalls = 0;
        await registry.ActivateAsync("owner", new Plugin(registrations =>
            registrations.RegisterBeforeAgentStartHandler(new("must-not-run", (_, _, _) =>
            { beforeCalls++; return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(null); }))));
        var codec = new SessionEntryCodec();
        var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "custom-tail", timestamp = "1970-01-01T00:00:00.000Z", cwd = files.Directory }));
        var custom = codec.Parse(JsonSerializer.Serialize(new { type = "custom_message", id = "custom", parentId = (string?)null,
            timestamp = "1970-01-01T00:00:00.010Z", customType = "restored", content = "retained-custom-tail", display = false, details = new { restored = true } }));
        await using (var store = await SessionLogStore.CreateNewAsync(files.Path, header)) await store.AppendAsync([custom]);
        var prefix = await File.ReadAllBytesAsync(files.Path);
        var binding = Binding(registry); var runtime = new SessionRuntimeRegistry([new(Model, source, Hooks: binding.Hooks)], [], new Policy());
        await using var session = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, runtime, () => 123, files.Next, fallbackModel: Model);
        Check(session.Snapshot.Agent.Messages.Single().Role == "custom");
        var result = await session.ContinueAsync();
        Check(result.Reason == AgentLoopStopReason.Completed && beforeCalls == 0);
        Check(source.Requests.Count == 1 && source.Requests[0].Messages.Single().Role == "user");
        Check(source.Requests[0].Messages[0].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == "retained-custom-tail");
        Check(session.Snapshot.Agent.Messages.Select(message => message.Role).SequenceEqual(["custom", "assistant"]));
        Check(session.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.CustomMessage) == 1);
        Check(session.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.Message) == 1);
        Check((await SessionLogFixtureReads.ReadAllBytesAsync(files.Path)).AsSpan(0, prefix.Length).SequenceEqual(prefix));
    }

    private static async Task RecoveryCustomTail()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry(); var source = new RecoverySource(); var beforeCalls = 0;
        await registry.ActivateAsync("owner", new Plugin(registrations =>
            registrations.RegisterBeforeAgentStartHandler(new("custom", (_, _, _) =>
            { beforeCalls++; return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(new("recovery-tail", false, JsonData.Parse("\"recovery-custom-tail\"")), "retained-force")); }))));
        var codec = new SessionEntryCodec();
        var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "custom-recovery", timestamp = "1970-01-01T00:00:00.000Z", cwd = files.Directory }));
        var oldAssistant = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 10, [new TextContent("old answer")], TokenUsage.Zero, StopReason.Stop);
        var oldMessages = new[] { Message("user", new string('a', 8_000)), new TranscriptEntry("assistant", PiWireJson.WriteMessage(oldAssistant)),
            Message("user", new string('b', 8_000)), new TranscriptEntry("assistant", PiWireJson.WriteMessage(oldAssistant)) };
        await using (var store = await SessionLogStore.CreateNewAsync(files.Path, header))
        {
            var entries = oldMessages.Select((message, index) => codec.Parse(JsonSerializer.Serialize(new
            { type = "message", id = "seed-" + index, parentId = index == 0 ? null : "seed-" + (index - 1), timestamp = "1970-01-01T00:00:00.010Z", message = message.WireBody.Value }))).ToImmutableArray();
            await store.AppendAsync(entries);
        }
        var binding = Binding(registry); var runtime = new SessionRuntimeRegistry([new(Model, source, Hooks: binding.Hooks)], [], new Policy());
        await using var session = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, runtime, () => 123, files.Next, fallbackModel: Model);
        var summary = new Summary(() => Check(session.Snapshot.Agent.Messages[^1].Role == "custom"));
        session.ConfigureAutomaticCompaction(summary, new(true, 128, 80), 100_000, recoveryDesiredMaxOutput: 1000);
        var result = await session.PromptAsync(Message("user", "new prompt"));
        Check(result.Reason == AgentLoopStopReason.Completed && source.Requests.Count == 2 && summary.Calls == 1 && beforeCalls == 1);
        Check(source.Requests[1].Messages[^1].WireBody.ToString().Contains("recovery-custom-tail", StringComparison.Ordinal));
        Check(source.Requests[1].Messages[0].WireBody.Value.GetProperty("content").GetString() == "retained-force");
        Check(!source.Requests[1].Messages.Any(message => message.Role == "assistant" && PiWireJson.ReadMessage(message.WireBody.Value).StopReason == StopReason.Error));
        Check(session.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.ContextEdit) == 1);
        Check(session.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.Compaction) == 1);
        Check(session.Snapshot.Log.Entries.Count(entry => entry.Kind == SessionEntryKind.CustomMessage) == 1);
        Check(session.Snapshot.Agent.Messages.Any(message => message.Role == "custom"));
        Check(session.Snapshot.Log.Entries.Any(entry => entry.Kind == SessionEntryKind.Message &&
            entry.WireBody.Value.GetProperty("message").TryGetProperty("stopReason", out var reason) && reason.GetString() == "error"));
    }

    private sealed class Summary(Action validate) : ISessionSummaryGenerator
    {
        internal int Calls;
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); validate(); Calls++; return ValueTask.FromResult(new SessionGeneratedSummary("older conversation summary", TokenUsage.Zero)); }
    }
    private sealed class RecoverySource : IChatTransport
    {
        internal readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask; token.ThrowIfCancellationRequested(); Requests.Add(request);
            var failed = Requests.Count == 1;
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [new TextContent("answer")], TokenUsage.Zero,
                failed ? StopReason.Error : StopReason.Stop);
            if (failed) final = final with { ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"input exceeds the context window\"")) };
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "answer");
            if (failed) yield return new StreamError(StopReason.Error, final); else yield return new StreamDone(StopReason.Stop, final);
        }
    }

    private static async Task Durable()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry(); var source = new Source(); var beforeCount = 0;
        await registry.ActivateAsync("owner", new Plugin(registrations =>
        {
            registrations.RegisterBeforeAgentStartHandler(new("first", (input, _, _) =>
            {
                beforeCount++; Check(input.SystemPrompt == "base");
                return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(input.Prompt == "one"
                    ? new(new("fixture", false, JsonData.Parse("\"custom-one\""), JsonData.Parse("{\"key\":7}")), "first-force") : null);
            }));
            registrations.RegisterBeforeAgentStartHandler(new("second", (input, _, _) =>
            {
                if (input.Prompt != "one") return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(null);
                Check(input.SystemPrompt == "first-force");
                return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(new("fixture-two", true), ""));
            }));
            registrations.RegisterContextWithSystemHandler(new("context", (input, _, _) =>
            {
                Check(input.Messages.Count(message => message.Role == "custom") == 2);
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new(input.Messages.SetItem(0, Message("system", "context-only"))));
            }));
        }));
        var binding = Binding(registry); var runtime = new SessionRuntimeRegistry([new(Model, source, Hooks: binding.Hooks)], [], new Policy());
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "before-start", timestamp = "2026-10-03T00:00:00.000Z", cwd = files.Directory }));
        await using (var session = await PersistentAgentSession.CreateAsync(files.Path, header, runtime, Model, () => 123, files.Next))
        {
            await session.ConfigureAsync(new(SystemMessage: Message("system", "base")));
            var prefix = await SessionLogFixtureReads.ReadAllBytesAsync(files.Path);
            await session.PromptAsync(Message("user", "one"));
            Check(source.Requests.Single().Messages[0].WireBody.Value.GetProperty("content").GetString() == "");
            Check(source.Requests[0].Messages.All(message => message.Role != "custom"));
            Check(source.Requests[0].Messages[2].WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString() == "custom-one");
            Check(session.Snapshot.Agent.Messages.Count(message => message.Role == "custom") == 2);
            var bytes = await SessionLogFixtureReads.ReadAllBytesAsync(files.Path);
            Check(bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix));
            var log = System.Text.Encoding.UTF8.GetString(bytes);
            Check(log.Contains("custom_message", StringComparison.Ordinal) && !log.Contains("first-force", StringComparison.Ordinal) && !log.Contains("context-only", StringComparison.Ordinal));
        }
        await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, runtime, () => 123, files.Next, fallbackModel: Model);
        Check(reopened.Snapshot.Agent.Messages.Count(message => message.Role == "custom") == 2);
        Check(reopened.Snapshot.Agent.Messages.Single(message => message.Role == "custom" && message.WireBody.Value.GetProperty("customType").GetString() == "fixture").WireBody.Value.GetProperty("details").GetProperty("key").GetInt32() == 7);
        await reopened.PromptAsync(Message("user", "two"));
        Check(source.Requests[1].Messages[0].WireBody.Value.GetProperty("content").GetString() == "context-only");
        Check(beforeCount == 2);
    }

    private static async Task Continuation()
    {
        await using var registry = new ExtensionRegistry(); var source = new Source(); var trace = new List<string>();
        IExtensionRegistration? removed = null; var added = false;
        await registry.ActivateAsync("owner", new Plugin(registrations =>
        {
            registrations.RegisterBeforeAgentStartHandler(new("first", (input, _, _) =>
            {
                trace.Add("first:" + input.Prompt); removed!.Dispose();
                if (!added)
                {
                    added = true; registrations.RegisterBeforeAgentStartHandler(new("late", (next, _, _) =>
                    { trace.Add("late:" + next.Prompt); return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(null); }));
                }
                Check(input.Images?.Value.GetArrayLength() == 1 || input.Prompt == "two");
                return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(SystemPrompt: "forced-" + input.Prompt));
            }));
            removed = registrations.RegisterBeforeAgentStartHandler(new("second", (input, _, _) =>
            { trace.Add("second:" + input.SystemPrompt); return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(null); }));
        }));
        await using var agent = Agent(source, Binding(registry).Hooks);
        var system = new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"base\",\"toolsAdded\":[{\"name\":\"read\"}],\"timestamp\":10}"));
        var user = new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"one\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}],\"timestamp\":123}"));
        await agent.PromptAsync([system, user]);
        agent.FollowUp(Message("user", "queued")); await agent.ContinueAsync();
        Check(trace.SequenceEqual(["first:one", "second:forced-one"]));
        Check(source.Requests.Take(2).All(request => request.Messages[0].WireBody.Value.GetProperty("content").GetString() == "forced-one"));
        Check(source.Requests[0].Messages[0].WireBody.Value.GetProperty("toolsAdded")[0].GetProperty("name").GetString() == "read");
        await agent.PromptAsync(Message("user", "two"));
        Check(trace.SequenceEqual(["first:one", "second:forced-one", "first:two", "late:two"]));
        Check(source.Requests[2].Messages[0].WireBody.Value.GetProperty("content").GetString() == "forced-two");
    }

    private static async Task Isolation()
    {
        await using var registry = new ExtensionRegistry();
        await registry.ActivateAsync("owner", new Plugin(registrations =>
            registrations.RegisterBeforeAgentStartHandler(new("force", (input, _, _) =>
                ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(SystemPrompt: "forced-" + input.Prompt))))));
        var binding = Binding(registry); var firstSource = new Source(); var secondSource = new Source();
        await using var first = Agent(firstSource, binding.Hooks); await using var second = Agent(secondSource, binding.Hooks);
        await first.PromptAsync(Message("user", "a")); await second.PromptAsync(Message("user", "b"));
        first.FollowUp(Message("user", "queued")); await first.ContinueAsync();
        Check(firstSource.Requests.All(request => request.Messages[0].WireBody.Value.GetProperty("content").GetString() == "forced-a"));
        Check(secondSource.Requests.Single().Messages[0].WireBody.Value.GetProperty("content").GetString() == "forced-b");
    }

    private static async Task Invalid()
    {
        await using var registry = new ExtensionRegistry(); var source = new Source(); var diagnostics = new List<ExtensionEventDiagnostic>();
        await registry.ActivateAsync("owner", new Plugin(registrations =>
        {
            registrations.RegisterBeforeAgentStartHandler(new("first", (_, _, _) => ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(SystemPrompt: "valid"))));
            registrations.RegisterBeforeAgentStartHandler(new("throws", (_, _, _) => throw new IOException("private")));
            registrations.RegisterBeforeAgentStartHandler(new("invalid", (_, _, _) => ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(new("bad", true, JsonData.Parse("42")), "invalid"))));
            registrations.RegisterBeforeAgentStartHandler(new("last", (input, _, _) =>
            { Check(input.SystemPrompt == "valid"); return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(new("good", false, JsonData.Parse("[]")))); }));
        }));
        await using var agent = Agent(source, Binding(registry, diagnostics).Hooks);
        await agent.PromptAsync(Message("user", "one"));
        Check(source.Requests[0].Messages[0].WireBody.Value.GetProperty("content").GetString() == "valid");
        Check(agent.Snapshot.Messages.Count(message => message.Role == "custom") == 1);
        Check(diagnostics.Select(diagnostic => diagnostic.Failure).SequenceEqual([ExtensionEventFailure.HandlerFailed, ExtensionEventFailure.InvalidResult]));
    }

    private static async Task Cancellation()
    {
        foreach (var kind in new[] { "operation", "session", "owner" })
        {
            await using var registry = new ExtensionRegistry(); using var operation = new CancellationTokenSource(); using var session = new CancellationTokenSource();
            var entered = Gate(); var cleanup = Gate(); var release = Gate(); var source = new Source();
            var scope = await registry.ActivateAsync("owner", new Plugin(registrations =>
                registrations.RegisterBeforeAgentStartHandler(new("wait", async (_, _, token) =>
                {
                    entered.TrySetResult();
                    try { await Gate().Task.WaitAsync(token); return null; }
                    finally { cleanup.TrySetResult(); await release.Task; }
                }))));
            await using var agent = Agent(source, Binding(registry, session: session.Token).Hooks);
            var run = agent.PromptAsync(Message("user", "original"), operation.Token); Task? retiring = null;
            try
            {
                await Reach(entered.Task, run);
                if (kind == "operation") operation.Cancel(); else if (kind == "session") session.Cancel(); else retiring = scope.DisposeAsync().AsTask();
                await Reach(cleanup.Task, run); Check(!run.IsCompleted && (retiring is null || !retiring.IsCompleted));
                Check(source.Requests.Count == 0 && agent.Snapshot.Messages.IsEmpty);
            }
            finally
            {
                release.TrySetResult(); try { await run; } catch (OperationCanceledException) { }
                if (retiring is not null) await retiring;
            }
            Check(source.Requests.Count == 0 && agent.Snapshot.Messages.IsEmpty);
        }
    }

    private static ExtensionAgentBinding Binding(ExtensionRegistry registry, List<ExtensionEventDiagnostic>? diagnostics = null, CancellationToken session = default) =>
        new(registry, new Policy(), (_, _, _) => ValueTask.FromResult(true), options: new()
        { RestoreSystemMessage = (messages, token) => new SessionSystemReplay().Replay(messages, token).CurrentMessage,
          ReadSystemPrompt = (messages, token) => new SessionSystemReplay().Replay(messages, token).Prompt,
          ReportEventDiagnostic = (diagnostic, _) => { diagnostics?.Add(diagnostic); return ValueTask.CompletedTask; } }, sessionCancellationToken: session);
    private static NativeAgent Agent(Source source, AgentHooks hooks) => new(new(Model, source, [], Hooks: hooks), () => 123,
        new Sink(), new(CancellationBehavior: AgentCancellationBehavior.Propagate));
    private sealed class Plugin(Action<IExtensionRegistry> initialize) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { initialize(registry); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Policy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(false)); }
    private sealed class Sink : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => ValueTask.CompletedTask; }
    private sealed class Source : IChatTransport
    {
        internal readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask; token.ThrowIfCancellationRequested(); Requests.Add(request);
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); yield return new StreamDone(StopReason.Stop, final);
        }
    }
    private sealed class Files : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-context-hook-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "session.jsonl"); private int id;
        internal Files() => System.IO.Directory.CreateDirectory(Directory);
        internal string Next() => "entry-" + Interlocked.Increment(ref id);
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Directory);
            if (System.IO.Path.GetDirectoryName(target) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) ||
                !System.IO.Path.GetFileName(target).StartsWith("PiSharp-context-hook-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup path");
            System.IO.Directory.Delete(target, true);
        }
    }
    private static TranscriptEntry Message(string role, string text) => new(role, JsonData.Parse(JsonSerializer.Serialize(new { role, content = text, timestamp = 123 })));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reach(Task boundary, Task run) { if (await Task.WhenAny(boundary, run) == run && !boundary.IsCompleted) { await run; throw new InvalidOperationException("Missing boundary"); } await boundary; }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Before-start assertion failed"); }
}
