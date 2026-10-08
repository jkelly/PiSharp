using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

// Pi v1.1.0 packages/coding-agent/src/core/agent-session.ts (_installAgentRequestProjection: per-request routing of a virtual
// selection, router state persisted as pi.virtual-model-state) and core/virtual-models.ts, by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> VirtualModelCases() =>
    [
        Case("virtual.per-request-route-state-entry-and-retry-reason", VirtualPerRequestRouting),
    ];

    private sealed class FakeVirtualSession : IVirtualModelSession
    {
        internal readonly List<SessionEntry> Entries = []; internal readonly List<string> Appended = [];
        public IReadOnlyList<SessionEntry> Branch => Entries;
        public Task AppendStateAsync(JsonData data, CancellationToken token)
        {
            Appended.Add(data.ToString());
            Entries.Add(new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "custom", id = "s" + Entries.Count, parentId = (string?)null,
                timestamp = "2026-10-08T00:00:00.000Z", customType = VirtualModels.StateEntry, data = data.Value })));
            return Task.CompletedTask;
        }
    }
    private sealed class PhysicalTransport : IChatTransport
    {
        internal readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request); await Task.CompletedTask;
            var final = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 1, [new TextContent("routed")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(final with { Content = [], StopReason = StopReason.Pending });
            yield return new StreamDone(StopReason.Stop, final);
        }
    }

    private static async Task VirtualPerRequestRouting()
    {
        var registry = ModelRegistry.Create(new() { Environment = name => name == "GROQ_API_KEY" ? "k" : null });
        var reasons = new List<ModelRouteReason>(); var states = new List<string?>();
        registry.RegisterVirtualModel(new("router", "auto", "Auto", request =>
        {
            reasons.Add(request.Reason); states.Add(request.State?.ToJsonString());
            var count = (request.State?["count"]?.GetValue<int>() ?? 0) + (request.Reason == ModelRouteReason.Continuation ? 0 : 1);
            return ValueTask.FromResult(new ModelRoute(registry.Find("groq", "openai/gpt-oss-120b")!, "high", new JsonObject { ["count"] = count }));
        }, ["off", "high"]));
        var session = new FakeVirtualSession(); var physical = new PhysicalTransport(); var created = 0;
        var transport = new VirtualModelRoutingTransport(registry, registry.Find("router", "auto")!, _ => { created++; return physical; }, () => session);
        async Task<StreamEvent> Send(params string[] messages)
        {
            StreamEvent? last = null;
            await foreach (var observation in transport.StreamAsync(new(new("auto", "pi-virtual", "router"),
                [.. messages.Select(message => new TranscriptEntry(JsonNode.Parse(message)!["role"]!.GetValue<string>(), JsonData.Parse(message)))]) { ThinkingLevel = "high" })) last = observation;
            return last!;
        }
        var user = """{"role":"user","content":"go","timestamp":1}""";
        var first = await Send(user);
        Check(first is StreamDone { Message.Model: "openai/gpt-oss-120b", Message.Provider: "groq" }, "The response does not name the physical model.");
        Equal("""{"provider":"router","modelId":"auto","state":{"count":1}}""", session.Appended.Single(), "router state entry");
        Check(physical.Requests[0].Model == new ModelDescriptor("openai/gpt-oss-120b", "openai-completions", "groq") || physical.Requests[0].Model.Provider == "groq", "routed model");
        // A continuation (tool results after the assistant) keeps the state: no new entry.
        var assistant = """{"role":"assistant","content":[],"provider":"groq","model":"openai/gpt-oss-120b","api":"openai-completions","stopReason":"toolUse","timestamp":2}""";
        var toolResult = """{"role":"toolResult","toolCallId":"c","toolName":"t","content":[],"isError":false,"timestamp":3}""";
        await Send(user, assistant, toolResult);
        Check(session.Appended.Count == 1 && states[1] == """{"count":1}""", "An unchanged state was appended again or not read back.");
        // After a failed response that the retry omits, the request is routed with reason retry.
        session.Entries.Add(new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "message", id = "failed", parentId = (string?)null, timestamp = "2026-10-08T00:00:00.000Z",
            message = PiWireJson.WriteMessage(new AssistantMessage("openai-completions", "groq", "openai/gpt-oss-120b", 4, [], TokenUsage.Zero, StopReason.Error,
                JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"503\"")))).Value })));
        await Send(user);
        Check(reasons.SequenceEqual([ModelRouteReason.User, ModelRouteReason.Continuation, ModelRouteReason.Retry]), "route reasons: " + string.Join(",", reasons));
        Equal(1, created, "physical transports per routed model");
        Check(physical.Requests.All(request => request.ThinkingLevel == "high"), "routed thinking level");
    }
}
