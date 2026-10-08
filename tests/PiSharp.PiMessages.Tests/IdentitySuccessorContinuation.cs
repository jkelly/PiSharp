using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Agent;
using PiSharp.Contracts;
using NativeAgent = PiSharp.Agent.Agent;

internal static class IdentitySuccessorContinuation
{
    internal sealed record Outcome(string Id, string Status, string? Failure, object? Actuals);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Versioned two-turn final-policy continuation expectation differs."); }
    internal static async Task<Outcome> RunAsync(string root)
    {
        var requests = new List<JsonData>(); var progress = new List<JsonData>(); var adapter = new Adapter(); var policy = new Policy();
        try
        {
            using var original = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "tests/PiSharp.PiMessages.Tests/authored-cases-r2.json")));
            using var outputs = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "tests/PiSharp.PiMessages.Tests/identity-successor-output-r1.json")));
            var sourceCase = original.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "PM-TOOL-IDENTITY-REPLACEMENT");
            var input = sourceCase.GetProperty("input"); var metadata = input.GetProperty("model");
            var expected = outputs.RootElement.GetProperty("agentContinuation"); var finalId = expected.GetProperty("finalCallId").GetString()!;
            var model = new ModelDescriptor(metadata.GetProperty("id").GetString()!, "pi-messages", metadata.GetProperty("provider").GetString()!);
            var firstWire = string.Concat(input.GetProperty("events").EnumerateArray().Select(dto => "data: " + JsonSerializer.Serialize(dto) + "\n\n"));
            var zero = PiWireJson.WriteMessage(new(model.Api, model.Provider, model.Id, 123, [], TokenUsage.Zero, StopReason.Pending)).Value.GetProperty("usage");
            var secondWire = string.Concat(new object[] { new { type = "start" }, new { type = "text_start", contentIndex = 0 },
                new { type = "text_delta", contentIndex = 0, delta = "continued" }, new { type = "text_end", contentIndex = 0, content = "continued" },
                new { type = "done", reason = "stop", usage = zero } }.Select(dto => "data: " + JsonSerializer.Serialize(dto) + "\n\n"));
            using var handler = new Handler(async (request, token) =>
            {
                requests.Add(JsonData.Parse(await request.Content!.ReadAsStringAsync(token)));
                Check(requests.Count <= 2);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(requests.Count == 1 ? firstWire : secondWire)) };
            });
            using var client = new HttpClient(handler); var firstTerminal = false;
            adapter.BeforePrepare = () => Check(firstTerminal && requests.Count == 1);
            var invoker = new ToolInvoker([adapter], policy,
                [(_, action, _) => ValueTask.FromResult(action with { Target = "/allowed/transformed/7" })],
                options: new ToolInvokerOptions { AllowedRootTools = ImmutableHashSet.Create(StringComparer.Ordinal, "other") });
            var options = new PiMessagesOptions(JsonData.FromElement(metadata), input.GetProperty("options").GetProperty("apiKey").GetString()) { EnvironmentLookup = _ => null };
            await using var agent = new NativeAgent(new(model, new PiMessagesHttpSseTransport(client, model, options), [new("other", invoker)]), () => 123,
                new Sink((observation, _) =>
                {
                    if (observation is TurnStreamObserved stream)
                    {
                        progress.Add(PiWireJson.WriteEvent(stream.Event));
                        if (requests.Count == 1)
                        {
                            Check(adapter.Executions.Count == 0 && policy.Actions.Count == 0 && adapter.Invocations.Count == 0);
                            if (stream.Event is StreamDone { Reason: StopReason.ToolUse }) firstTerminal = true;
                        }
                    }
                    return ValueTask.CompletedTask;
                }), new(StreamCapacity: 1));
            var inputs = expected.GetProperty("nativeInputs").EnumerateArray()
                .Select(message => new TranscriptEntry(message.GetProperty("role").GetString()!, JsonData.FromElement(message))).ToImmutableArray();
            Task<AgentLoopResult>? pending = null;
            try
            {
                pending = agent.PromptAsync(inputs); var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Check(result.Turns.Length == 2 && requests.Count == expected.GetProperty("expectedPhysicalRequests").GetInt32());
                Check(result.Turns.All(turn => turn.Result.Chat.Failure is null) && result.Turns[1].Result.Chat.Message.Content.OfType<TextContent>().Single().Text == expected.GetProperty("finalAssistantText").GetString());
                var prepared = adapter.Invocations.Single(); Check(prepared.Call.Id == finalId && prepared.Call.Name == "other" && prepared.AssistantMessage.StopReason == StopReason.ToolUse);
                Check(JsonElement.DeepEquals(prepared.Call.Arguments.Value, expected.GetProperty("finalArguments")) && adapter.Validations == 2 && adapter.Executions.Count == 1);
                Check(policy.Invocations.Single().Call.Id == finalId && policy.Actions.Single().Target == expected.GetProperty("transformedTarget").GetString() && ReferenceEquals(policy.Actions.Single(), adapter.Executions.Single()));
                var next = requests[1].Value.GetProperty("context").GetProperty("messages");
                Check(next.GetArrayLength() == inputs.Length + 2);
                var assistant = next[inputs.Length]; var toolResult = next[inputs.Length + 1];
                Check(assistant.GetProperty("role").GetString() == "assistant" && assistant.GetProperty("content")[0].GetProperty("id").GetString() == finalId && assistant.GetProperty("content")[0].GetProperty("name").GetString() == "other");
                Check(JsonElement.DeepEquals(assistant.GetProperty("content")[0].GetProperty("arguments"), expected.GetProperty("finalArguments")));
                Check(toolResult.GetProperty("role").GetString() == "toolResult" && toolResult.GetProperty("toolCallId").GetString() == finalId && toolResult.GetProperty("toolName").GetString() == "other");
                return new("pi-identity.agent-two-turn-final-policy-continuation-r1", "PASS_AUTHORED_NATIVE_ONLY_REVIEW_PENDING", null,
                    new { requests, progress, finalizedInvocations = adapter.Invocations, policyActions = policy.Actions, validations = adapter.Validations, effects = adapter.Executions.Count, transcript = result.Transcript.Select(entry => entry.WireBody).ToArray() });
            }
            finally { agent.Abort(); if (pending is not null) { try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch when (pending.IsCompleted) { } } }
        }
        catch (Exception error) { return new("pi-identity.agent-two-turn-final-policy-continuation-r1", "FAIL", error.ToString(), new { requests, progress, finalizedInvocations = adapter.Invocations, policyActions = policy.Actions, validations = adapter.Validations, effects = adapter.Executions.Count }); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class Sink(Func<AgentEvent, CancellationToken, ValueTask> emit) : IAgentEventSink
    { public ValueTask EmitAsync(AgentEvent observation, CancellationToken token) => emit(observation, token); }
    private sealed class Adapter : IPreparedToolAdapter
    {
        public string Name => "other";
        internal Action? BeforePrepare;
        internal readonly List<ToolInvocation> Invocations = [];
        internal readonly List<PreparedToolAction> Executions = [];
        internal int Validations;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); BeforePrepare?.Invoke(); Invocations.Add(invocation); return ValueTask.FromResult(new PreparedToolAction(Name, "inspect", PreparedToolActionKind.Path, "/allowed/7", invocation.Call.Arguments, [], null, ImmutableDictionary<string, string>.Empty)); }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Validations++; return ValueTask.FromResult(action.Arguments.Value.GetProperty("value").GetInt32() == 7 && action.Target.StartsWith("/allowed/", StringComparison.Ordinal)); }
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Executions.Add(action); return ValueTask.FromResult(ToolResult.Success("continued-tool-result")); }
    }
    private sealed class Policy : IToolActionPolicy
    {
        internal readonly List<ToolInvocation> Invocations = [];
        internal readonly List<PreparedToolAction> Actions = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Invocations.Add(invocation); Actions.Add(action); return ValueTask.FromResult(new ToolActionAuthorization(true)); }
    }
}
