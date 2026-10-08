using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

// Authored expectations from pinned Pi transform-messages.ts; no Node/live capture claim.
if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report path.");
var model = new ModelDescriptor("orphan-model", "openai-responses", "openai");
var outcomes = new List<object>();
var telemetry = new List<object>();
var expectedFaultTasks = new List<object>();
var cases = new (string Name, Func<Task> Run)[] {
    ("eof-two-missing-in-call-order", Eof), ("user-boundary-before-new-turn", User),
    ("assistant-boundary-before-next-assistant", NextAssistant), ("held-system-after-real-and-synthetic-results", HeldSystem),
    ("foreign-id-normalization-and-paired-real-result", Foreign), ("strict-default-and-unmatched-results-retained", Admission),
    ("synthetic-output-budgets-and-pre-cancel", Limits), ("held-http-callback-original-task-settlement", () => HeldHttp()),
    ("native-provider-normal-off-routes-held-original-tasks", NativeRoutes), ("native-admission-and-cancel-before-http", NativeAdmission)
};
for (var ordinal = 0; ordinal < cases.Length; ordinal++) {
    var (name, run) = cases[ordinal]; Task? original = null; Exception? awaitedError = null;
    try { original = run(); await original; }
    catch (Exception error) { awaitedError = error; }
    outcomes.Add(new { ordinal, name, passed = awaitedError is null, originalInvoked = true,
        originalCaptured = original is not null, originalTaskJoined = original?.IsCompleted == true,
        originalStatus = original?.Status.ToString(), actuallyCanceled = original?.IsCanceled == true,
        faulted = original?.IsFaulted == true, synchronousFailure = original is null,
        faults = CaptureFaults(original?.Exception, awaitedError) });
}
var failed = outcomes.Count(item => JsonSerializer.SerializeToElement(item).GetProperty("passed").GetBoolean() == false);
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { profile = "responses-orphan-10-originals", sourceCommit = "d86654abb8862e201933517d6f1fce9f88dd117f", expectationKind = "authored-source-derived", expected = cases.Length, executed = outcomes.Count, passed = outcomes.Count - failed, failed, directOriginalAwait = true, tests = outcomes, heldTelemetry = telemetry, expectedFaultTasks, networkCalls = 0, noNode = true }, new JsonSerializerOptions { WriteIndented = true }));
Environment.ExitCode = failed == 0 ? 0 : 1;
object CaptureFaults(Exception? originalAggregate, Exception? caught) {
    var identities = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
    var nodes = new List<object>(); var edges = 0; var truncated = false;
    int? Visit(Exception? error) {
        if (error is null) return null;
        if (identities.TryGetValue(error, out var retained)) return retained;
        if (nodes.Count >= 1024) { truncated = true; return null; }
        var index = nodes.Count; identities.Add(error, index); nodes.Add(new { pending = true });
        IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions : error.InnerException is { } inner ? new[] { inner } : [];
        var links = new List<int?>();
        foreach (var child in children) { if (edges >= 4096) { truncated = true; break; } edges++; links.Add(Visit(child)); }
        nodes[index] = new { index, type = error.GetType().AssemblyQualifiedName, error.Message, error.HResult, error.StackTrace,
            aggregate = error is AggregateException, inner = links, cancellationException = error is OperationCanceledException,
            cancellationRequested = error is OperationCanceledException canceled && canceled.CancellationToken.IsCancellationRequested };
        return index;
    }
    var originalAggregateRoot = Visit(originalAggregate); var awaitOrSynchronousRoot = Visit(caught);
    return new { originalAggregateRoot, awaitOrSynchronousRoot, nodes, edges, truncated };
}
async Task SharedFaultControl() {
    var shared = new InvalidOperationException("expected-shared-leaf");
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var original = completion.Task; completion.SetException(new Exception[] { shared, shared }); Exception? caught = null;
    try { await original; } catch (Exception error) { caught = error; }
    var faults = CaptureFaults(original.Exception, caught); var graph = JsonSerializer.SerializeToElement(faults);
    Check(original.IsFaulted && original.IsCompleted && ReferenceEquals(shared, caught) && graph.GetProperty("nodes").GetArrayLength() == 2 &&
        graph.GetProperty("originalAggregateRoot").GetInt32() == 0 && graph.GetProperty("awaitOrSynchronousRoot").GetInt32() == 1 &&
        graph.GetProperty("nodes")[0].GetProperty("inner")[0].GetInt32() == 1 && graph.GetProperty("nodes")[0].GetProperty("inner")[1].GetInt32() == 1 && !graph.GetProperty("truncated").GetBoolean(), "Shared original/await exception identity was lost.");
    expectedFaultTasks.Add(new { originalInvoked = true, originalCaptured = true, originalTaskJoined = true, directOriginalAwait = true,
        originalStatus = original.Status.ToString(), faulted = original.IsFaulted, expectedFailure = true, faults });
}
TranscriptEntry Assistant(string[] ids, string provider = "openai", string text = "") => new("assistant", PiWireJson.WriteMessage(new AssistantMessage(
    model.Api, provider, model.Id, 12, ids.Select(id => (AssistantContent)new ToolCallContent(id, "lookup", JsonData.Parse("{}"))).Concat(
        text.Length == 0 ? [] : new AssistantContent[] { new TextContent(text) }).ToImmutableArray(), TokenUsage.Zero, ids.Length == 0 ? StopReason.Stop : StopReason.ToolUse)));
TranscriptEntry Entry(string role, string raw) => new(role, JsonData.Parse(raw));
TranscriptEntry UserEntry() => Entry("user", "{\"role\":\"user\",\"content\":\"next\"}");
TranscriptEntry SystemEntry() => Entry("system", "{\"role\":\"system\",\"content\":\"update\"}");
TranscriptEntry Result(string id) => Entry("toolResult", JsonSerializer.Serialize(new { role = "toolResult", toolCallId = id, toolName = "lookup", content = new[] { new { type = "text", text = "actual" } }, isError = false, timestamp = 10 }));
ChatRequest Request(params TranscriptEntry[] entries) => new(model, entries.ToImmutableArray(), 123);
JsonElement[] Project(ChatRequest request, ResponsesTranscriptProjectionOptions? options = null) => new ResponsesTranscriptProjector(options ?? new(false, SupportsMidConversationSystemMessages: true, SynthesizeMissingToolResults: true)).Project(request).Value.EnumerateArray().Select(item => item.Clone()).ToArray();
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
void Output(JsonElement value, string id, string text) { Check(value.GetProperty("type").GetString() == "function_call_output" && value.GetProperty("call_id").GetString() == id && value.GetProperty("output").GetString() == text, "Wrong result identity/text/order."); }
Task Eof() { var items = Project(Request(Assistant(["a|fc_a", "b|fc_b"]))); Check(items.Length == 4, "Missing synthesized EOF outputs."); Output(items[2], "a", "No result provided"); Output(items[3], "b", "No result provided"); return Task.CompletedTask; }
Task User() { var items = Project(Request(Assistant(["a|fc_a"]), UserEntry())); Output(items[1], "a", "No result provided"); Check(items[2].GetProperty("role").GetString() == "user", "User emitted before synthetic result."); return Task.CompletedTask; }
Task NextAssistant() { var items = Project(Request(Assistant(["a|fc_a"]), Assistant([], text: "retry"))); Output(items[1], "a", "No result provided"); Check(items[2].GetProperty("role").GetString() == "assistant", "Next assistant ordering changed."); return Task.CompletedTask; }
Task HeldSystem() { var items = Project(Request(Assistant(["a|fc_a", "b|fc_b"]), SystemEntry(), Result("b|fc_b"), UserEntry())); Check(items.Length == 6, "Held system output count wrong."); Output(items[2], "b", "actual"); Output(items[3], "a", "No result provided"); Check(items[4].GetProperty("content").GetString() == "update", "System was not held through tool flow."); return Task.CompletedTask; }
Task Foreign() { var request = Request(Assistant(["a.bad|foreign!", "b.bad|other!"], "other-provider"), Result("b.bad|other!")); var before = request.Messages.Select(item => item.WireBody.ToString()).ToArray(); var items = Project(request); Output(items[2], "b_bad", "actual"); Output(items[3], "a_bad", "No result provided"); Check(request.Messages.Select(item => item.WireBody.ToString()).SequenceEqual(before), "Canonical history mutated."); return Task.CompletedTask; }
void Fails(ChatRequest request, ResponsesProjectionFailure expected, ResponsesTranscriptProjectionOptions? options = null) { try { _ = Project(request, options); } catch (ResponsesProjectionException error) when (error.Failure == expected) { return; } throw new InvalidOperationException("Expected bounded projection rejection: " + expected); }
async Task Admission() { Fails(Request(Assistant(["a|fc_a"])), ResponsesProjectionFailure.UnmatchedToolResult, new(false)); Fails(Request(Result("orphan")), ResponsesProjectionFailure.UnmatchedToolResult); Fails(Request(Assistant(["a|fc_a"]), Result("a|fc_a"), Result("a|fc_a")), ResponsesProjectionFailure.UnmatchedToolResult); foreach (var reason in new[] { "error", "aborted" }) { var skipped = Assistant(["a|fc_a"]); var body = skipped.WireBody.ToString().Replace("\"stopReason\":\"toolUse\"", "\"stopReason\":\"" + reason + "\""); Check(Project(Request(new TranscriptEntry("assistant", JsonData.Parse(body)))).Length == 0, "Error/aborted assistant generated an orphan output."); } await SharedFaultControl(); }
Task Limits() { Fails(Request(Assistant(["a|fc_a"])), ResponsesProjectionFailure.ResourceLimit, new(false, MaximumOutputItems: 1, SynthesizeMissingToolResults: true)); using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { _ = new ResponsesTranscriptProjector(new(false, SynthesizeMissingToolResults: true)).Project(Request(Assistant(["a|fc_a"])), cancel.Token); } catch (OperationCanceledException) { return Task.CompletedTask; } throw new InvalidOperationException("Pre-cancellation ignored."); }
async Task NativeRoutes() { await HeldHttp(true); await HeldHttp(true, "off"); }
async Task HeldHttp(bool native = false, string? thinking = null) {
    var request = Request(Assistant(["a|fc_a"]), SystemEntry(), UserEntry()) with { ThinkingLevel = thinking };
    var before = request.Messages.Select(item => item.WireBody.ToString()).ToArray();
    using var handler = new HeldHandler(); using var http = new HttpClient(handler);
    var factory = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), model, new(false, SupportsMidConversationSystemMessages: true, SynthesizeMissingToolResults: true));
    using var provider = native ? NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "inert-synthetic-key", new(false, SupportsMidConversationSystemMessages: true, SynthesizeMissingToolResults: true), handler: handler) : null;
    var client = new ChatClient(provider?.Transport ?? new ResponsesHttpSseTransport(http, factory, "inert-synthetic-key"));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var original = client.CompleteAsync(request, deadline.Token);
    try {
        await handler.Entered.Task.WaitAsync(deadline.Token);
        Check(!original.IsCompleted && handler.CallbackTask is { IsCompleted: false }, "Original/callback settled while callback held.");
        using var body = JsonDocument.Parse(handler.Body!); var items = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Output(items[1], "a", "No result provided"); Check(items[2].GetProperty("content").GetString() == "update", "Actual factory held-system payload mismatch.");
        var heldOriginalStatus = original.Status.ToString(); var heldCallbackStatus = handler.CallbackTask!.Status.ToString();
        handler.Release.TrySetResult();
        var final = await original.WaitAsync(deadline.Token);
        await handler.CallbackTask!.WaitAsync(deadline.Token);
        Check(original.IsCompletedSuccessfully && handler.CallbackTask!.IsCompletedSuccessfully && final.Failure is null && final.Message.StopReason == StopReason.Stop && handler.Attempts == 1, "Original callback/run did not settle successfully.");
        Check(request.Messages.Select(item => item.WireBody.ToString()).SequenceEqual(before), "HTTP path mutated history.");
        telemetry.Add(new { name = "held-http-original-task-telemetry", passed = true, native, thinking, heldOriginalStatus, heldCallbackStatus, finalOriginalStatus = original.Status.ToString(), finalCallbackStatus = handler.CallbackTask!.Status.ToString(), originalDirectlyAwaited = true, handler.Attempts, originalFaults = CaptureFaults(original.Exception, null), callbackFaults = CaptureFaults(handler.CallbackTask!.Exception, null) });
    } finally { handler.Release.TrySetResult(); try { await original; } catch (OperationCanceledException) { } }
}

async Task NativeAdmission() {
    foreach (var options in new[] { new ResponsesTranscriptProjectionOptions(false), new(false, MaximumOutputItems: 1, SynthesizeMissingToolResults: true) }) {
        using var handler = new HeldHandler();
        using var provider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "inert-synthetic-key", options, handler: handler);
        var original = new ChatClient(provider.Transport).CompleteAsync(Request(Assistant(["a|fc_a"])));
        var final = await original.WaitAsync(TimeSpan.FromSeconds(5));
        Check(original.IsCompletedSuccessfully && final.Failure is not null && handler.Attempts == 0 && handler.CallbackTask is null, "Projection admission acquired HTTP.");
    }
    using var cancelledHandler = new HeldHandler();
    using var cancelledProvider = NativeProviderFactory.CreateResponses(model, new("https://api.openai.com/v1/responses"), "inert-synthetic-key", new(false, SynthesizeMissingToolResults: true), handler: cancelledHandler);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var cancelledOriginal = new ChatClient(cancelledProvider.Transport).CompleteAsync(Request(Assistant(["a|fc_a"])), cancelled.Token);
    try { _ = await cancelledOriginal.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
    Check(cancelledOriginal.IsCompleted && cancelledHandler.Attempts == 0 && cancelledHandler.CallbackTask is null, "Pre-cancel acquired HTTP.");
}
sealed class HeldHandler : HttpMessageHandler {
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task? CallbackTask { get; private set; }
    internal string? Body { get; private set; }
    internal int Attempts { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        CallbackTask = Callback(request, cancellationToken); return (Task<HttpResponseMessage>)CallbackTask;
    }
    private async Task<HttpResponseMessage> Callback(HttpRequestMessage request, CancellationToken token) {
        Attempts++; Body = await request.Content!.ReadAsStringAsync(token); Entered.TrySetResult();
        await Release.Task.WaitAsync(token);
        return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
    }
}