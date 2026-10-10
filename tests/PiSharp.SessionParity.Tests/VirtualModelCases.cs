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
        Case("virtual.summary-routes-direct-with-session-messages-and-level", VirtualSummaryRouting),
    ];

    private sealed class SummarySession : IVirtualModelSession
    {
        internal readonly List<string> Appended = [];
        public IReadOnlyList<SessionEntry> Branch => [];
        public Task AppendStateAsync(JsonData data, CancellationToken token) { Appended.Add(data.ToString()); return Task.CompletedTask; }
        public IReadOnlyList<TranscriptEntry> Messages { get; } =
            [new("user", JsonData.Parse("""{"role":"user","content":"session question","timestamp":1}"""))];
        public string ThinkingLevel => "medium";
    }

    // agent-session.ts _getSummarizationRequestAuth + model-runtime.ts resolveModel: a summary of a virtual selection routes once with
    // reason "direct", the session's messages and thinking level and no router state; it goes to the routed physical model at the
    // routed level (compaction) or with none (branch summaries), and the router's state is not recorded.
    private static async Task VirtualSummaryRouting()
    {
        var registry = ModelRegistry.Create(new() { Environment = name => name == "GROQ_API_KEY" ? "k" : null });
        var requests = new List<ModelRouteRequest>();
        registry.RegisterVirtualModel(new("router", "auto", "Auto", request =>
        {
            requests.Add(request);
            return ValueTask.FromResult(new ModelRoute(registry.Find("groq", "openai/gpt-oss-120b")!, "high", new JsonObject { ["count"] = 9 }));
        }, ["off", "medium", "high"]));
        var session = new SummarySession(); var physical = new PhysicalTransport(); var summaries = new List<(string Model, int Maximum, string? Level)>();
        var transport = new VirtualModelRoutingTransport(registry, registry.Find("router", "auto")!, _ => throw new InvalidOperationException("Main route used."),
            () => session, (target, maximum, level) => { summaries.Add((target.Reference, maximum, level)); return physical; });
        var summary = new ChatRequest(new("auto", "pi-virtual", "router"), [new("user", JsonData.Parse("""{"role":"user","content":"PROMPT","timestamp":2}"""))], 2)
        { ThinkingLevel = "medium", SessionId = "019a0000-0000-7000-8000-000000000001" };
        foreach (var carriesLevel in new[] { true, false })
            await foreach (var _ in transport.Summary(13107, carriesLevel).StreamAsync(summary)) { }
        Check(requests.All(request => request.Reason == ModelRouteReason.Direct && request.ThinkingLevel == "medium" && request.State is null),
            "summary routing request: " + string.Join(",", requests.Select(request => request.Reason + "/" + request.ThinkingLevel + "/" + request.State?.ToJsonString())));
        Check(requests.All(request => request.Messages.Count == 1 && request.Messages[0]["content"]!.GetValue<string>() == "session question"),
            "summary routing did not see the session's messages");
        Check(summaries.SequenceEqual([("groq/openai/gpt-oss-120b", 13107, (string?)"high"), ("groq/openai/gpt-oss-120b", 13107, null)]),
            "summary targets: " + string.Join(",", summaries));
        Check(physical.Requests.All(request => request.Model.Provider == "groq" && request.Model.Id == "openai/gpt-oss-120b" &&
            request.SessionId == "019a0000-0000-7000-8000-000000000001"), "summary request not sent to the physical model");
        Equal(0, session.Appended.Count, "router state entries recorded by a summary");
    }

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
