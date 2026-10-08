using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;

internal static class NestedToolHostTests
{
    private static readonly ModelDescriptor Model = new("nested-host", "openai-responses", "fixture");
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("nested-host native SDK persists usage and binds actual replacement generations", Replacement),
        ("nested-host failed staging leaves originating generation usable", FailedStaging),
        ("nested-host disposal cancels and joins ignored native child cleanup", Disposal)
    ];

    private static async Task Replacement()
    {
        await using var fixture = await Fixture.Create();
        var initial = await fixture.Session("A");
        await using var owner = new ReplaceableAgentSession(initial, (request, token) => fixture.Open(request.Path, token));
        await initial.PromptAsync(Input());
        Check(fixture.Generations.SequenceEqual([1L]));
        var firstContext = fixture.Contexts.Single();
        var root = initial.Snapshot.Context.LlmMessages.Single(message => message.Role == "toolResult").WireBody.Value;
        Check(root.GetProperty("usage").GetProperty("input").GetInt32() == 3);
        Check(root.GetProperty("nestedCalls").GetProperty("calls").GetArrayLength() == 1);
        Check(root.GetProperty("toolCallId").GetString() == "root");
        await using (var seed = await fixture.Session("B")) { }
        var next = await owner.SwitchAsync(owner.Current, new(fixture.PathFor("B")));
        Check(next is not null && next.Current.Generation == 2 && next.Previous.LifetimeToken.IsCancellationRequested);
        Check(firstContext.SessionCancellationToken.IsCancellationRequested);
        Check((await firstContext.ExecuteToolAsync("inner", JsonData.EmptyObject)).IsError);
        await owner.Current.Session.PromptAsync(Input());
        Check(fixture.Generations.SequenceEqual([1L, 2L]));
        await owner.SwitchAsync(owner.Current, new(fixture.PathFor("A")));
        await owner.Current.Session.PromptAsync(Input());
        Check(fixture.Generations.SequenceEqual([1L, 2L, 3L]));
        Check(owner.Current.Session.Snapshot.Context.LlmMessages.Count(message => message.Role == "toolResult") == 2);
    }

    private static async Task FailedStaging()
    {
        await using var fixture = await Fixture.Create();
        var initial = await fixture.Session("A");
        await using var owner = new ReplaceableAgentSession(initial, (request, token) => fixture.Open(request.Path, token));
        await using (var seed = await fixture.Session("B")) { }
        var original = owner.Current;
        var replacement = await owner.SwitchAsync(original, new(fixture.PathFor("B")),
            beforeSwitch: (_, _, _) => ValueTask.FromResult(false));
        Check(replacement is null && ReferenceEquals(original, owner.Current));
        Check(!original.LifetimeToken.IsCancellationRequested);
        await initial.PromptAsync(Input());
        Check(fixture.Generations.SequenceEqual([1L]));
    }

    private static async Task Disposal()
    {
        var entered = Gate(); var cleanup = Gate(); var release = Gate();
        await using var fixture = await Fixture.Create(async token =>
        {
            entered.TrySetResult();
            try { await Gate().Task.WaitAsync(token); }
            finally { cleanup.TrySetResult(); await release.Task; }
        }, ignoreChild: true);
        var initial = await fixture.Session("A");
        await using var owner = new ReplaceableAgentSession(initial, (request, token) => fixture.Open(request.Path, token));
        var running = initial.PromptAsync(Input()); Task? disposal = null;
        try
        {
            await Reach(entered.Task, running);
            disposal = owner.DisposeAsync().AsTask();
            await Reach(cleanup.Task, running);
            Check(!running.IsCompleted && !disposal.IsCompleted);
            Check(fixture.Contexts.Single().SessionCancellationToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
            try { await running; } finally { if (disposal is not null) await disposal; }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PiSharp-nested-host-" + Guid.NewGuid().ToString("N"));
        private readonly ExtensionRegistry extensions = new();
        private SessionRuntimeRegistry registry = null!;
        private TranscriptEntry declarations = null!;
        private int next;
        internal readonly List<long> Generations = [];
        internal readonly List<ToolInvocationContext> Contexts = [];
        internal static async Task<Fixture> Create(Func<CancellationToken, Task>? execute = null, bool ignoreChild = false)
        {
            var fixture = new Fixture();
            System.IO.Directory.CreateDirectory(fixture.directory);
            try
            {
                await fixture.extensions.ActivateAsync("nested-host-extension", new Extension(async (arguments, context, token) =>
                {
                    var work = context.ExecuteToolAsync("inner", JsonData.EmptyObject);
                    if (ignoreChild) _ = work.AsTask(); else await work;
                    return (ToolResult.Success("outer") with { Usage = Usage(1) }).ToJson();
                }));
                var policy = new Policy();
                var binding = new ExtensionAgentBinding(fixture.extensions, policy, (_, _, _) => ValueTask.FromResult(true));
                var outerDeclaration = JsonData.FromElement(binding.CreateDeclarationMessage("tools", 1).WireBody.Value.GetProperty("toolsAdded")[0]);
                var innerDeclaration = JsonData.Parse("{\"name\":\"inner\",\"description\":\"native child\",\"parameters\":{\"type\":\"object\"}}");
                var child = new Adapter(async (invocation, token) =>
                {
                    fixture.Generations.Add(invocation.Context!.SessionGeneration);
                    fixture.Contexts.Add(invocation.Context);
                    Check(invocation.ParentToolCallId == "root");
                    if (execute is not null) await execute(token);
                    return ToolResult.Success("inner") with { Usage = Usage(2) };
                });
                fixture.registry = new([new(Model, new Script())],
                    [new(outerDeclaration, binding.Adapters[0]), new(innerDeclaration, child)], policy,
                    new() { PreparedToolHooks = binding.PreparedHooks, BindNestedCallsToSessionOwner = true });
                fixture.declarations = new("system", JsonData.Parse(JsonSerializer.Serialize(new
                { role = "system", content = "tools", timestamp = 1, toolsAdded = new[] { outerDeclaration.Value, innerDeclaration.Value } })));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        internal string PathFor(string name) => System.IO.Path.Combine(directory, name + ".jsonl");
        internal async Task<PersistentAgentSession> Session(string name)
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
            { type = "session", version = 3, id = name, timestamp = "2026-10-03T00:00:00.000Z", cwd = directory }));
            var session = await PersistentAgentSession.CreateAsync(PathFor(name), header, registry, Model, () => 123, Next);
            try { await session.ConfigureAsync(new(SystemMessage: declarations)); return session; }
            catch { await session.DisposeAsync(); throw; }
        }
        internal Task<PersistentAgentSession> Open(string path, CancellationToken token) =>
            PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 123, Next, fallbackModel: Model, cancellationToken: token);
        private string Next() => "entry-" + Interlocked.Increment(ref next);
        public async ValueTask DisposeAsync()
        {
            await extensions.DisposeAsync();
            var target = System.IO.Path.GetFullPath(directory);
            if (System.IO.Path.GetDirectoryName(target) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) ||
                !System.IO.Path.GetFileName(target).StartsWith("PiSharp-nested-host-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
        }
    }
    private sealed class Extension(ExtensionToolCallback callback) : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
        {
            registry.RegisterTool(new("outer", "outer", "native SDK parent", JsonData.Parse("{\"type\":\"object\"}"), callback));
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Adapter(Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> execute) : IInvocationPreparedToolAdapter
    {
        public string Name => "inner";
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token) =>
            ValueTask.FromResult(new PreparedToolAction(Name, "invoke", PreparedToolActionKind.Path, "fixture", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty));
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(true);
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token) => throw new InvalidOperationException("Missing invocation");
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, PreparedToolAction action, ToolProgressCallback progress, CancellationToken token) => execute(invocation, token);
    }
    private sealed class Policy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true));
    }
    private sealed class Script : IChatTransport
    {
        private int turns;
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask; token.ThrowIfCancellationRequested();
            var tool = Interlocked.Increment(ref turns) % 2 == 1;
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123,
                tool ? [new ToolCallContent("root", "outer", JsonData.EmptyObject)] : [new TextContent("done")],
                TokenUsage.Zero, tool ? StopReason.ToolUse : StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (tool) { var call = (ToolCallContent)final.Content[0]; yield return new ToolCallStarted(0, call); yield return new ToolCallEnded(0, call); }
            else { yield return new TextStarted(0, new("")); yield return new TextEnded(0, "done"); }
            yield return new StreamDone(final.StopReason, final);
        }
    }
    private static JsonData Usage(int value) => JsonData.Parse(JsonSerializer.Serialize(new
    { input = value, output = 0, cacheRead = 0, cacheWrite = 0, totalTokens = value, cost = new { input = value, output = 0, cacheRead = 0, cacheWrite = 0, total = value } }));
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"run\",\"timestamp\":123}"));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Reach(Task boundary, Task work) { if (await Task.WhenAny(boundary, work) == work && !boundary.IsCompleted) { await work; throw new InvalidOperationException("Missing boundary"); } await boundary; }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Nested host integration assertion failed"); }
}
