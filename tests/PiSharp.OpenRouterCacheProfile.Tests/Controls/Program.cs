using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report.");
var outcomes = new List<object>(); var passed = 0; var failed = 0;
var model = new ModelDescriptor("anthropic/cache-controls", "openai-completions", "openrouter");
var request = new ChatRequest(model, [new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"Instruction 😀 é\",\"toolsAdded\":[{\"name\":\"inspect\",\"description\":\"Inspect.\",\"parameters\":{}}],\"timestamp\":123}")), new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"Authored question.\",\"timestamp\":123}"))], 123);
const string key = "authored-inert-key";
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
CompletionsKeyAuthRequestFactory Factory(CompletionsKeyAuthRequestOptions? options = null, CompletionsTranscriptProjectionOptions? projection = null) => new(new("https://cache-controls.invalid/v1/chat/completions"), model, projection, options);
async Task Run(string name, Func<Task> action)
{
    try { await action(); passed++; outcomes.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; outcomes.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
async Task RejectBeforeSend(CompletionsKeyAuthRequestFactory factory, ChatRequest supplied)
{
    using var handler = new CounterHandler(); using var client = new HttpClient(handler); var factoryCalls = 0; var payloadCalls = 0;
    var hooks = new CompletionsLifecycleHooks { OnPayload = (_, _, _) => { payloadCalls++; return ValueTask.FromResult<JsonData?>(null); } };
    var transport = CompletionsHttpSseTransport.FromAsyncRequestFactory(client, (input, token) => { factoryCalls++; return factory.CreateAsync(input, key, hooks, token); }, new() { Hooks = hooks });
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var result = await new ChatClient(transport).CompleteAsync(supplied, deadline.Token);
    Check(result.Failure is not null && handler.Attempts == 0 && factoryCalls == 1 && payloadCalls == 0, "Rejected cache expansion reached a payload callback or an actual SendAsync entry.");
    // Preserve the independently typed admission category, rather than relying on generic mapped failure text.
    try { using var unused = factory.Create(supplied, key); } catch (CompletionsRequestException error)
    { Check(error.Failure == CompletionsRequestFailure.ResourceLimit, "Wrong cache admission category."); return; }
    throw new InvalidOperationException("Cache expansion escaped its configured bound.");
}
using var baselineRequest = Factory(new(CacheRetention: CompletionsCacheRetention.None)).Create(request, key);
var baselineText = await baselineRequest.Content!.ReadAsStringAsync(); using var baseline = JsonDocument.Parse(baselineText);
var originalBodies = request.Messages.Select(entry => entry.WireBody.ToString()).ToArray();
await Run("cache-expansion-total-payload-byte-limit-zero-actual-send-entry", () => RejectBeforeSend(Factory(new(MaximumPayloadBytes: Encoding.UTF8.GetByteCount(baselineText))), request));
await Run("cache-expansion-message-character-limit-zero-actual-send-entry", () => RejectBeforeSend(Factory(projection: new(MaximumOutputCharacters: baseline.RootElement.GetProperty("messages").GetRawText().Length)), request));
await Run("cache-expansion-message-utf8-byte-limit-zero-actual-send-entry", () => RejectBeforeSend(Factory(projection: new(MaximumOutputBytes: Encoding.UTF8.GetByteCount(baseline.RootElement.GetProperty("messages").GetRawText()))), request));
await Run("cache-expansion-declaration-character-limit-zero-actual-send-entry", () => RejectBeforeSend(Factory(projection: new(ToolDeclarations: new(MaximumOutputCharacters: baseline.RootElement.GetProperty("tools").GetRawText().Length))), request));
await Run("cache-expansion-declaration-byte-limit-zero-actual-send-entry", () => RejectBeforeSend(Factory(projection: new(ToolDeclarations: new(MaximumOutputBytes: Encoding.UTF8.GetByteCount(baseline.RootElement.GetProperty("tools").GetRawText())))), request));
await Run("cache-expansion-depth-limit-zero-actual-send-entry", async () =>
{
    var shallow = new ChatRequest(model, [new("system", JsonData.Parse("{\"role\":\"system\",\"content\":\"Instruction.\",\"timestamp\":123}")), request.Messages[1]], 123);
    using var admitted = Factory(new(CacheRetention: CompletionsCacheRetention.None, MaximumPayloadDepth: 3)).Create(shallow, key);
    await RejectBeforeSend(Factory(new(MaximumPayloadDepth: 3)), shallow);
});
await Run("cache-retention-none-affinity-suppression-in-both-existing-formats", () =>
{
    foreach (var format in new[] { "openai", "openrouter" })
    {
        using var owned = Factory(new(CacheRetention: CompletionsCacheRetention.None, SessionId: "authored-session", SendSessionAffinityHeaders: true, SessionAffinityFormat: format)).Create(request, key);
        Check(new[] { "x-session-id", "session_id", "x-client-request-id", "x-session-affinity" }.All(name => !owned.Headers.Contains(name)), "None retention generated session affinity.");
    }
    return Task.CompletedTask;
});
await Run("cache-pre-canceled-run-zero-request-construction-and-send-entry", async () =>
{
    using var handler = new CounterHandler(); using var client = new HttpClient(handler); using var canceled = new CancellationTokenSource(); canceled.Cancel(); var factoryCalls = 0;
    var factory = Factory(); var transport = new CompletionsHttpSseTransport(client, (input, token) => { factoryCalls++; return factory.Create(input, key, token); });
    var result = await new ChatClient(transport).CompleteAsync(request, canceled.Token);
    Check(result.Message.StopReason == StopReason.Aborted && handler.Attempts == 0 && factoryCalls == 0, "Pre-cancel constructed or attempted a cache-profile request.");
});
await Run("cache-factory-reuse-concurrent-owned-projections-and-canonical-isolation", async () =>
{
    var factory = Factory(new(CacheRetention: CompletionsCacheRetention.Long, SessionId: "owned-session", SendSessionAffinityHeaders: true, SessionAffinityFormat: "openrouter"));
    var alternate = request with { Messages = [request.Messages[0], new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"Alternate question.\",\"timestamp\":123}"))] };
    var ready = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var bodies = await Task.WhenAll(new[] { request, alternate, request, alternate }.Select(item => Task.Run(async () =>
    { if (Interlocked.Increment(ref ready) == 4) release.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); using var owned = factory.Create(item, key); Check(owned.Headers.GetValues("x-session-id").Single() == "owned-session", "Reusable factory changed affinity."); return await owned.Content!.ReadAsStringAsync(); })));
    Check(bodies[0] == bodies[2] && bodies[1] == bodies[3] && bodies[0] != bodies[1], "Factory reused a mutable cache projection across requests.");
    Check(request.Messages.Select(entry => entry.WireBody.ToString()).SequenceEqual(originalBodies), "Repeated or rejected cache projection changed canonical input.");
});
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { schemaVersion = 1, passed, failed, observations = outcomes,
    executedAi = Identity(typeof(ChatClient).Assembly), executedTests = Identity(Assembly.GetExecutingAssembly()), fullGate = false, phaseAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Cache profile controls: {passed} passed, {failed} failed."); return failed == 0 ? 0 : 1;
static object Identity(Assembly assembly) { var data = File.ReadAllBytes(assembly.Location); return new { path = assembly.Location, bytes = data.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion }; }
sealed class CounterHandler : HttpMessageHandler
{
    internal int Attempts;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n") }); }
}
