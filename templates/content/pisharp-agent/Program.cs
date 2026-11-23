using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;

namespace MyPiSharpAgent;

internal static class Program
{
    private static async Task<int> Main()
    {
        // Replace ScriptedDemoTransport with a real provider transport (e.g., OpenAIResponsesTransport
        // or AnthropicMessagesTransport) and your API key to call a live model.
        var model = new ModelDescriptor("demo-model", "openai-responses", "demo");
        var transport = new ScriptedDemoTransport();
        var tools = new ToolDefinition("lookup_status", new StatusToolExecutor());
        var hooks = new PolicyToolHooks();
        var sink = new ConsoleEventSink();

        var systemPrompt = new TranscriptEntry("system", JsonData.Parse("""
            {
              "role": "system",
              "content": "You are a concise operations assistant. Use lookup_status to inspect a service before answering.",
              "toolsAdded": [
                {
                  "name": "lookup_status",
                  "description": "Checks the current health of a named service.",
                  "parameters": {
                    "type": "object",
                    "properties": {
                      "service": { "type": "string", "description": "Service name to inspect." }
                    },
                    "required": ["service"]
                  }
                }
              ],
              "timestamp": 1
            }
            """));

        var userPrompt = new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
        {
            role = "user",
            content = "Check the billing service status and summarize it.",
            timestamp = 2
        })));

        await using var agent = new Agent(
            new AgentConfiguration(model, transport, [tools], hooks),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            sink);

        var result = await agent.PromptAsync([systemPrompt, userPrompt]);
        var finalMessage = result.Turns[^1].Result.Chat.Message;
        var reply = string.Concat(finalMessage.Content.OfType<TextContent>().Select(t => t.Text));

        Console.WriteLine($"Agent finished in {result.Turns.Length} turn(s): {reply}");
        return finalMessage.StopReason == StopReason.Stop ? 0 : 1;
    }

    private sealed class StatusToolExecutor : IToolExecutor
    {
        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var service = invocation.Call.Arguments.Value.GetProperty("service").GetString() ?? "unknown";
            var payload = $"{service}: healthy (p99=42ms)";
            return ValueTask.FromResult(new ToolResult([new TextContent(payload)], JsonData.EmptyObject));
        }
    }

    private sealed class PolicyToolHooks : IToolHooks
    {
        public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Console.WriteLine($"[policy] allowing tool '{invocation.Call.Name}'");
            return ValueTask.FromResult(ToolPreflightDecision.Allow);
        }

        public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result);
    }

    private sealed class ConsoleEventSink : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class ScriptedDemoTransport : IChatTransport
    {
        private int _turn;

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var m = request.Model;
            yield return new StreamStarted(new AssistantMessage(m.Api, m.Provider, m.Id, request.Timestamp, [], TokenUsage.Zero, StopReason.Pending));

            if (Interlocked.Increment(ref _turn) == 1)
            {
                var call = new ToolCallContent("call-1", "lookup_status", JsonData.Parse("""{"service":"billing"}"""));
                yield return new ToolCallStarted(0, new ToolCallContent("call-1", "lookup_status", JsonData.EmptyObject));
                yield return new ToolCallDelta(0, """{"service":"billing"}""");
                yield return new ToolCallEnded(0, call);
                yield return new StreamDone(StopReason.ToolUse,
                    new AssistantMessage(m.Api, m.Provider, m.Id, request.Timestamp, [call], TokenUsage.Zero, StopReason.ToolUse));
                yield break;
            }

            const string answer = "The billing service is healthy with a p99 latency of 42ms.";
            yield return new TextStarted(0, new TextContent(""));
            yield return new TextDelta(0, answer);
            yield return new TextEnded(0, answer);
            yield return new StreamDone(StopReason.Stop,
                new AssistantMessage(m.Api, m.Provider, m.Id, request.Timestamp, [new TextContent(answer)], TokenUsage.Zero, StopReason.Stop));
        }
    }
}
