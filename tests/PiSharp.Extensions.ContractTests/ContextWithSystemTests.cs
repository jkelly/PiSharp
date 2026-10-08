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
using NativeAgent = PiSharp.Agent.Agent;

internal static class ContextWithSystemTests
{
    internal const string Prefix = "context-with-system.";
    private static readonly ModelDescriptor Model = new("context-hook", "openai-responses", "fixture");
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "conversation identity preserves system positions and changed lists replay tool state", ConversationRestore),
        (Prefix + "conversation phase precedes full phase and snapshots new full handlers", PhaseOrdering),
        (Prefix + "ordered native handlers change model requests but preserve durable history", DurableHistory),
        (Prefix + "removal retains admitted handler then disappears on the next request", Removal),
        (Prefix + "invalid returns and exceptions diagnose and continue; empty replacement is honored", InvalidResults),
        (Prefix + "operation session and owner cancellation join callback cleanup before settlement", Cancellation),
        (Prefix + "malformed user and system content diagnoses before transport and retains prior context", MalformedContent),
        (Prefix + "legacy preparation cannot rewrite canonical history through the new seam", LegacyGuard)
    ];

    private static async Task ConversationRestore()
    {
        await using var registry = new ExtensionRegistry(); var source = new Source(); var prune = false;
        await registry.ActivateAsync("owner", new Plugin(registrations => registrations.RegisterContextHandler(new("context", (input, _, _) =>
        {
            Check(input.Messages.All(message => message.Role != "system"));
            return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new(prune
                ? [input.Messages.Last(message => message.Role == "user")]
                : input.Messages.ToArray().ToImmutableArray()));
        }))));
        var first = new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"first\",\"sections\":{\"rule\":\"old\"},\"toolsAdded\":[{\"name\":\"read\"}],\"timestamp\":10}"));
        var second = new TranscriptEntry("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"second\",\"sections\":{\"rule\":\"new\"},\"toolsRemoved\":[{\"name\":\"read\"}],\"toolsAdded\":[{\"name\":\"write\"}],\"timestamp\":20}"));
        var binding = Binding(registry); await using var agent = Agent(source, binding.Hooks);
        await agent.PromptAsync([first, Message("user", "one"), second, Message("user", "two")]);
        Check(ReferenceEquals(source.Requests[0].Messages[0], first) && ReferenceEquals(source.Requests[0].Messages[2], second));
        prune = true; await agent.PromptAsync(Message("user", "three"));
        var request = source.Requests[1].Messages; Check(request.Length == 2);
        var head = request[0].WireBody.Value;
        Check(head.GetProperty("content").GetString() == "first\n\nsecond");
        Check(head.GetProperty("sections").GetProperty("rule").GetString() == "new");
        Check(head.GetProperty("timestamp").GetInt32() == 10);
        Check(head.GetProperty("toolsAdded").GetArrayLength() == 1 && head.GetProperty("toolsAdded")[0].GetProperty("name").GetString() == "write");
        Check(agent.Snapshot.Messages.Count(message => message.Role == "system") == 2);
    }

    private static async Task PhaseOrdering()
    {
        await using var registry = new ExtensionRegistry(); var trace = new List<string>(); var source = new Source(); var added = false;
        await registry.ActivateAsync("owner", new Plugin(registrations =>
        {
            registrations.RegisterContextWithSystemHandler(new("full-first", (_, _, _) =>
            { trace.Add("full"); return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null); }));
            registrations.RegisterContextHandler(new("conversation", (_, _, _) =>
            {
                trace.Add("conversation");
                if (!added)
                {
                    added = true; registrations.RegisterContextWithSystemHandler(new("new-full", (_, _, _) =>
                    { trace.Add("new-full"); return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null); }));
                }
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null);
            }));
        }));
        await using var agent = Agent(source, Binding(registry).Hooks);
        await agent.PromptAsync(Message("user", "one"));
        Check(trace.SequenceEqual(["conversation", "full", "new-full"]));
    }

    private static async Task DurableHistory()
    {
        using var files = new Files(); await using var registry = new ExtensionRegistry();
        var seen = new List<string>(); var source = new Source();
        await registry.ActivateAsync("first", new Plugin(registrations =>
            registrations.RegisterContextWithSystemHandler(new("first", (input, _, _) =>
            {
                Check(input.Messages[0].Role == "system");
                Check(input.Messages.Any(message => message.WireBody.ToString().Contains("canonical-user", StringComparison.Ordinal)));
                Check(!input.Messages.Any(message => message.WireBody.ToString().Contains("request-only", StringComparison.Ordinal)));
                seen.Add("first");
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new([Message("system", "request-only-prompt"), Message("user", "request-only-user")]));
            }))));
        await registry.ActivateAsync("second", new Plugin(registrations =>
            registrations.RegisterContextWithSystemHandler(new("second", (input, _, _) =>
            {
                Check(input.Messages[1].WireBody.Value.GetProperty("content").GetString() == "request-only-user"); seen.Add("second");
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new(input.Messages.Add(Message("user", "request-only-tail"))));
            }))));
        var binding = Binding(registry);
        var runtime = new SessionRuntimeRegistry([new(Model, source, Hooks: binding.Hooks)], [], new Policy());
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
        { type = "session", version = 3, id = "context", timestamp = "2026-10-03T00:00:00.000Z", cwd = files.Directory }));
        ImmutableArray<TranscriptEntry> final;
        await using (var session = await PersistentAgentSession.CreateAsync(files.Path, header, runtime, Model, () => 123, files.Next))
        {
            await session.ConfigureAsync(new(SystemMessage: Message("system", "canonical-prompt")));
            var prefix = await SessionLogFixtureReads.ReadAllBytesAsync(files.Path);
            await session.PromptAsync(Message("user", "canonical-user-one"));
            await session.PromptAsync(Message("user", "canonical-user-two"));
            final = session.Snapshot.Context.LlmMessages;
            Check(seen.SequenceEqual(["first", "second", "first", "second"]));
            Check(source.Requests.Count == 2 && source.Requests.All(request => request.Messages.Length == 3));
            Check(source.Requests.All(request => request.Messages[0].WireBody.Value.GetProperty("content").GetString() == "request-only-prompt"));
            var bytes = await SessionLogFixtureReads.ReadAllBytesAsync(files.Path);
            Check(bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix));
            Check(!System.Text.Encoding.UTF8.GetString(bytes).Contains("request-only", StringComparison.Ordinal));
            Check(final.Count(message => message.Role == "user") == 2);
        }
        await using var reopened = await PersistentAgentSession.OpenWithRegistryAsync(files.Path, runtime, () => 123, files.Next, fallbackModel: Model);
        Check(reopened.Snapshot.Context.LlmMessages.Select(message => message.WireBody.ToString()).SequenceEqual(final.Select(message => message.WireBody.ToString())));
    }

    private static async Task Removal()
    {
        await using var registry = new ExtensionRegistry(); var source = new Source(); var trace = new List<string>();
        IExtensionRegistration? second = null; var once = false;
        await registry.ActivateAsync("owner", new Plugin(registrations =>
        {
            registrations.RegisterContextWithSystemHandler(new("first", (input, _, _) =>
            {
                trace.Add("first"); second!.Dispose();
                if (!once) { once = true; registrations.RegisterContextWithSystemHandler(new("late", (value, _, _) =>
                    { trace.Add("late"); return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null); })); }
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null);
            }));
            second = registrations.RegisterContextWithSystemHandler(new("second", (_, _, _) =>
            { trace.Add("second"); return ValueTask.FromResult<ExtensionContextMessagesPatch?>(null); }));
        }));
        var binding = Binding(registry);
        await using var agent = Agent(source, binding.Hooks);
        await agent.PromptAsync(Message("user", "one"));
        await agent.PromptAsync(Message("user", "two"));
        Check(trace.SequenceEqual(["first", "second", "first", "late"]));
        Check(source.Requests.Count == 2);
    }

    private static async Task InvalidResults()
    {
        await using var registry = new ExtensionRegistry(); var diagnostics = new List<ExtensionEventDiagnostic>(); var source = new Source();
        await registry.ActivateAsync("owner", new Plugin(registrations =>
        {
            registrations.RegisterContextWithSystemHandler(new("throw", (_, _, _) => throw new IOException("private failure")));
            registrations.RegisterContextWithSystemHandler(new("default-array", (_, _, _) =>
                ValueTask.FromResult<ExtensionContextMessagesPatch?>(new(default(ImmutableArray<TranscriptEntry>)))));
            registrations.RegisterContextWithSystemHandler(new("wrong-role", (_, _, _) =>
                ValueTask.FromResult<ExtensionContextMessagesPatch?>(new([new("assistant", Message("user", "wrong").WireBody)]))));
            registrations.RegisterContextWithSystemHandler(new("last", (input, _, _) =>
            {
                Check(input.Messages.Length == 2 && input.Messages[0].Role == "system");
                return ValueTask.FromResult<ExtensionContextMessagesPatch?>(new([]));
            }));
        }));
        var binding = Binding(registry, diagnostics);
        await using var agent = Agent(source, binding.Hooks);
        await agent.PromptAsync([Message("system", "original"), Message("user", "original")]);
        Check(source.Requests.Single().Messages.IsEmpty);
        Check(diagnostics.Select(value => value.Failure).SequenceEqual([ExtensionEventFailure.HandlerFailed,
            ExtensionEventFailure.InvalidResult, ExtensionEventFailure.InvalidResult, ExtensionEventFailure.LeadingSystemRemoved]));
        Check(agent.Snapshot.Messages.Count(message => message.Role is "system" or "user") == 2);
    }

    private static async Task Cancellation()
    {
        foreach (var conversation in new[] { false, true })
        foreach (var kind in new[] { "operation", "session", "owner" })
        {
            await using var registry = new ExtensionRegistry(); using var operation = new CancellationTokenSource(); using var session = new CancellationTokenSource();
            var entered = Gate(); var cleanup = Gate(); var release = Gate(); var source = new Source();
            async ValueTask<ExtensionContextMessagesPatch?> Wait(CancellationToken token)
                {
                    entered.TrySetResult();
                    try { await Gate().Task.WaitAsync(token); return null; }
                    finally { cleanup.TrySetResult(); await release.Task; }
                }
            var scope = await registry.ActivateAsync("owner", new Plugin(registrations =>
            {
                if (conversation) registrations.RegisterContextHandler(new("wait", (_, _, token) => Wait(token)));
                else registrations.RegisterContextWithSystemHandler(new("wait", (_, _, token) => Wait(token)));
            }));
            var binding = Binding(registry, session: session.Token);
            await using var agent = Agent(source, binding.Hooks);
            var run = agent.PromptAsync(Message("user", "original"), operation.Token); Task? retiring = null;
            try
            {
                await Reach(entered.Task, run);
                if (kind == "operation") operation.Cancel();
                else if (kind == "session") session.Cancel();
                else retiring = scope.DisposeAsync().AsTask();
                await Reach(cleanup.Task, run);
                Check(!run.IsCompleted && (retiring is null || !retiring.IsCompleted));
                Check(source.Requests.Count == 0);
            }
            finally
            {
                release.TrySetResult();
                try { await run; } catch (OperationCanceledException) { }
                if (retiring is not null) await retiring;
            }
            Check(source.Requests.Count == 0);
        }
    }

    private static async Task MalformedContent()
    {
        foreach (var conversation in new[] { false, true })
        foreach (var role in new[] { "user", "system" })
        {
            await using var registry = new ExtensionRegistry(); var source = new Source(); var diagnostics = new List<ExtensionEventDiagnostic>();
            string[] invalid = ["42", "null", "{}", "[null]", "[{}]", "[{\"type\":7}]", "[{\"type\":\"text\"}]",
                "[{\"type\":\"text\",\"text\":42}]", "[{\"type\":\"image\",\"data\":42,\"mimeType\":\"image/png\"}]",
                "[{\"type\":\"image\",\"data\":\"AA==\"}]", "[{\"type\":\"unknown\"}]"];
            await registry.ActivateAsync("owner", new Plugin(registrations =>
            {
                for (var index = 0; index < invalid.Length; index++)
                {
                    var body = JsonData.Parse("{\"role\":\"" + role + "\",\"content\":" + invalid[index] + ",\"timestamp\":123}");
                    var patch = new ExtensionContextMessagesPatch([new(role, body)]);
                    if (conversation) registrations.RegisterContextHandler(new("bad-" + index, (_, _, _) => ValueTask.FromResult<ExtensionContextMessagesPatch?>(patch)));
                    else registrations.RegisterContextWithSystemHandler(new("bad-" + index, (_, _, _) => ValueTask.FromResult<ExtensionContextMessagesPatch?>(patch)));
                }
            }));
            await using var agent = Agent(source, Binding(registry, diagnostics).Hooks);
            var original = Message("user", "original"); await agent.PromptAsync(original);
            Check(source.Requests.Single().Messages.Length == 1 && ReferenceEquals(source.Requests[0].Messages[0], original));
            Check(diagnostics.Count == invalid.Length && diagnostics.All(diagnostic => diagnostic.Failure == ExtensionEventFailure.InvalidResult));
        }
        // Native supported content remains admissible, including empty arrays and user images.
        AgentLoopRunner.ValidateRequestMessages([Message("system", ""), Message("user", ""),
            new("system", JsonData.Parse("{\"role\":\"system\",\"content\":[]}")),
            new("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"},{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\"}]}"))]);
    }

    private static async Task LegacyGuard()
    {
        var source = new Source(); var transformed = false;
        var hooks = new AgentHooks(PrepareRequest: (snapshot, _) => ValueTask.FromResult(new ChatRequest(Model, [])))
        { TransformRequestMessages = (messages, _) => { transformed = true; return ValueTask.FromResult(messages); } };
        await using var agent = Agent(source, hooks);
        try { await agent.PromptAsync(Message("user", "original")); throw new InvalidOperationException("Legacy rewrite was accepted"); }
        catch (ArgumentException) { }
        Check(!transformed && source.Requests.Count == 0);
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
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Context request hook assertion failed"); }
}
