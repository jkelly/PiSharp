using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using NativeAgent = PiSharp.Agent.Agent;

internal static class RegisteredToolImageTests
{
    private static readonly ModelDescriptor Model = new("agent-image-model", "openai-completions", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly List<object> Evidence = [];
    public static object DifferentialEvidence => new { fixtureSha256 = "d54a73d00c0518d79038b06a19a94189cea6cd231782bb058fb8bc572dd93cb5", actualRegisteredBindingAndPublicAgent = true, observations = Evidence };
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (var profile in new[] { "progress-identity", "progress-nullish", "progress-image-patch", "progress-empty-patch" })
        { var id = profile; yield return ("registered-tool-image." + id + ".actual-progress-hooks-canonical-sink-source-request", () => RoundTrip(id)); }
        yield return ("registered-tool-image.canceled-progress-joins-callback-cleanup-before-idle-and-no-continuation", Cancellation);
    }
    private static async Task RoundTrip(string id)
    {
        using var fixture = Fixture(); var sourceCase = fixture.RootElement.GetProperty("observations").EnumerateArray().Single(value => value.GetProperty("profile").GetProperty("id").GetString() == id);
        var raw = JsonData.FromElement(sourceCase.GetProperty("rawProducedResult"));
        var patch = sourceCase.GetProperty("profile").GetProperty("patch");
        var schema = JsonData.FromElement(sourceCase.GetProperty("initialMessages")[1].GetProperty("toolsAdded")[0].GetProperty("parameters"));
        var progressEntered = Gate(); var releaseProgress = Gate(); var resultEntered = Gate(); var releaseResult = Gate();
        var executions = 0; var afterCalls = 0; var validations = 0; var policy = new Policy();
        await using var registry = new ExtensionRegistry();
        await registry.ActivateAsync("image-owner", new Extension((entries, _) =>
        {
            entries.RegisterTool(new("image-registration", "inspect", "An authored inert adapter.", schema, async (arguments, context, token) =>
            {
                Check(ReferenceEquals(arguments, policy.Action!.Arguments), "Image callback did not receive policy's identical final arguments.");
                executions++; var invocation = context as IExtensionToolInvocationContext ?? throw new InvalidOperationException("Invocation-aware progress capability was lost.");
                Equal("call-inspect", invocation.ToolCallId); await invocation.ReportUpdateAsync(raw, token); return raw;
            }));
            entries.RegisterToolResultHandler(new("image-after", (input, _, _) =>
            {
                afterCalls++;
                Check(!input.OutcomeIsError && JsonElement.DeepEquals(raw.Value.GetProperty("content"), input.Result.Value.GetProperty("content")), "Registered after-hook lost the original full image array.");
                return ValueTask.FromResult<ExtensionToolResultPatch?>(patch.ValueKind == JsonValueKind.Null ? null : new(JsonData.FromElement(patch)));
            }));
            return ValueTask.CompletedTask;
        }));
        var binding = new ExtensionAgentBinding(registry, policy, (_, arguments, _) =>
        {
            validations++; var value = arguments.Value;
            return ValueTask.FromResult(value.EnumerateObject().Count() == 2 && value.GetProperty("value").ValueKind == JsonValueKind.Number && value.GetProperty("keep").ValueKind == JsonValueKind.Null);
        });
        var transport = new Source(); ToolOutcome? outcome = null; JsonData? canonical = null;
        await using var agent = new NativeAgent(new(Model, transport, binding.Tools), () => 123, new Sink(async (observation, token) =>
        {
            if (observation is ToolExecutionUpdated update)
            { Equal(raw.Value.GetProperty("content").GetRawText(), update.PartialResult.ContentValue.ToString()); progressEntered.TrySetResult(); await releaseProgress.Task.WaitAsync(token); }
            if (observation is ToolExecutionEnded end) outcome = end.Outcome;
            if (observation is ToolResultMessageEnded ended)
            { canonical = ToolResultMessageMaterializer.ToTranscript(ended.Message, 123).WireBody; resultEntered.TrySetResult(); await releaseResult.Task.WaitAsync(token); }
        }));
        var initial = sourceCase.GetProperty("initialMessages").EnumerateArray().Select(value => new TranscriptEntry(value.GetProperty("role").GetString()!, JsonData.FromElement(value))).ToImmutableArray();
        var running = agent.PromptAsync(initial);
        var bodies = new List<JsonElement>();
        try
        {
            await progressEntered.Task.WaitAsync(Deadline); Equal(1, transport.Requests.Count); Equal(1, executions); Equal(1, policy.Calls); Equal(0, afterCalls);
            Check(outcome is null && !running.IsCompleted, "Registered image finalization crossed held progress delivery.");
            releaseProgress.TrySetResult(); await resultEntered.Task.WaitAsync(Deadline); Equal(1, afterCalls); Equal(1, transport.Requests.Count);
            Check(!agent.WaitForIdleAsync().IsCompleted, "Registered image canonical sink lost continuation ownership.");
            releaseResult.TrySetResult(); var result = await running.WaitAsync(Deadline); Equal(AgentLoopStopReason.Completed, result.Reason);
            Equal(2, transport.Requests.Count); Check(validations >= 2 && !outcome!.IsError, "Image execution bypassed validation or failed.");
            Check(JsonElement.DeepEquals(sourceCase.GetProperty("finalResult"), outcome!.Result.ToJson().Value), "Registered final image result differs from actual source after-hook outcome.");
            Check(JsonElement.DeepEquals(sourceCase.GetProperty("toolMessages")[0], canonical!.Value), "Registered canonical image message differs from actual source.");
            Equal(canonical!.ToString(), agent.Snapshot.Messages.Single(message => message.Role == "toolResult").WireBody.ToString());
            Equal(canonical.ToString(), transport.Requests[1].Messages.Single(message => message.Role == "toolResult").WireBody.ToString());
            var factory = new CompletionsKeyAuthRequestFactory(new Uri("https://agent-image.invalid/v1/chat/completions"), Model, new() { ModelSupportsImages = true });
            for (var index = 0; index < 2; index++)
            {
                using var request = factory.Create(transport.Requests[index], "authored-inert-key"); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                bodies.Add(body.RootElement.Clone()); Check(JsonElement.DeepEquals(sourceCase.GetProperty("requests")[index].GetProperty("bodyJson"), body.RootElement), "Registered complete source provider body differs at " + index);
            }
            Evidence.Add(new { id, actualBodies = bodies.ToArray(), sourceBodies = sourceCase.GetProperty("requests").EnumerateArray().Select(request => request.GetProperty("bodyJson").Clone()).ToArray(),
                nativeFinalResult = outcome.Result.ToJson().Value.Clone(), sourceFinalResult = sourceCase.GetProperty("finalResult").Clone(), nativeCanonical = canonical.Value.Clone(),
                sourceCanonical = sourceCase.GetProperty("toolMessages")[0].Clone(), executions, afterCalls, validations, policyCalls = policy.Calls, progressAndResultBarriersPassed = true });
        }
        finally { releaseProgress.TrySetResult(); releaseResult.TrySetResult(); agent.Abort(); await Observe(running); }
    }
    private static async Task Cancellation()
    {
        using var fixture = Fixture(); var raw = JsonData.FromElement(fixture.RootElement.GetProperty("observations")[0].GetProperty("rawProducedResult"));
        var entered = Gate(); var releaseProgress = Gate(); var cleanup = Gate(); var releaseCleanup = Gate(); var afterCalls = 0; var transport = new Source();
        await using var registry = new ExtensionRegistry();
        await registry.ActivateAsync("image-owner", new Extension((entries, _) =>
        {
            entries.RegisterTool(new("image-registration", "inspect", "An authored inert adapter.", JsonData.Parse("{\"type\":\"object\"}"), async (_, context, token) =>
            { try { await ((IExtensionToolInvocationContext)context).ReportUpdateAsync(raw, token); return raw; } finally { cleanup.TrySetResult(); await releaseCleanup.Task; } }));
            entries.RegisterToolResultHandler(new("image-after", (_, _, _) => { afterCalls++; return ValueTask.FromResult<ExtensionToolResultPatch?>(null); }));
            return ValueTask.CompletedTask;
        }));
        var binding = new ExtensionAgentBinding(registry, new Policy(), (_, _, _) => ValueTask.FromResult(true));
        await using var agent = new NativeAgent(new(Model, transport, binding.Tools), () => 123, new Sink(async (observation, token) =>
        { if (observation is ToolExecutionUpdated) { entered.TrySetResult(); await releaseProgress.Task; } }));
        var running = agent.PromptAsync(new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"run\",\"timestamp\":123}")));
        try
        {
            await entered.Task.WaitAsync(Deadline); Check(agent.Abort(), "Image cancellation was not admitted.");
            Check(!running.IsCompleted && !agent.WaitForIdleAsync().IsCompleted, "Cancellation escaped the admitted primary progress sink.");
            // Source-compatible primary delivery remains owned during cancellation; release it explicitly.
            releaseProgress.TrySetResult(); await cleanup.Task.WaitAsync(Deadline);
            Check(!running.IsCompleted && !agent.WaitForIdleAsync().IsCompleted, "Registered callback cleanup was abandoned.");
            Equal(0, afterCalls); Equal(1, transport.Requests.Count); releaseCleanup.TrySetResult();
            try { var result = await running.WaitAsync(Deadline); Check(result.Reason != AgentLoopStopReason.Completed, "Canceled image callback completed successfully."); }
            catch (OperationCanceledException) { }
            Equal(1, transport.Requests.Count);
        }
        finally { releaseProgress.TrySetResult(); releaseCleanup.TrySetResult(); agent.Abort(); await Observe(running); }
    }
    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        var bytes = File.ReadAllBytes(Path.Combine(directory!.FullName, "fixtures/native/completions-image-tool-lifecycle-source.json"));
        Equal("d54a73d00c0518d79038b06a19a94189cea6cd231782bb058fb8bc572dd93cb5", Convert.ToHexStringLower(SHA256.HashData(bytes))); return JsonDocument.Parse(bytes);
    }
    private sealed class Extension(Func<IExtensionRegistry, CancellationToken, ValueTask> initialize) : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry, token); public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Policy : IToolActionPolicy
    {
        public int Calls; public PreparedToolAction? Action;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { Calls++; Action = action; return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Source : IChatTransport
    {
        public List<ChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            var final = new AssistantMessage(Model.Api, Model.Provider, Model.Id, 123, Requests.Count == 0 ?
                [new ToolCallContent("call-inspect", "inspect", JsonData.Parse("{\"value\":1.00,\"keep\":null}"))] : [new TextContent("Observed image")], TokenUsage.Zero,
                Requests.Count == 0 ? StopReason.ToolUse : StopReason.Stop);
            Requests.Add(request); token.ThrowIfCancellationRequested(); yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            if (final.Content[0] is ToolCallContent call) { yield return new ToolCallStarted(0, call with { Arguments = JsonData.EmptyObject }); yield return new ToolCallEnded(0, call); }
            else { var text = (TextContent)final.Content[0]; yield return new TextStarted(0, new("")); yield return new TextEnded(0, text.Text); }
            yield return new StreamDone(final.StopReason, final); await Task.CompletedTask;
        }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task task) { try { await task.WaitAsync(Deadline); } catch { } }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
