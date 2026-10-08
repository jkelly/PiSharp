using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.Contracts;

if (args.Length != 2 || args[0] != "--report") throw new ArgumentException("Supply --report path.");
var outcomes = new List<object>(); var passed = 0; var failed = 0;
var model = new ModelDescriptor("openai/map-fixture", "openai-completions", "openrouter");
var request = new ChatRequest(model, [new TranscriptEntry("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"canonical\",\"timestamp\":123}"))], 123);
CompletionsKeyAuthRequestFactory Factory(CompletionsKeyAuthRequestOptions options) => new(new Uri("https://mode-controls.invalid/v1/chat/completions"), model, new(Reasoning: true), options);
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
void Rejected(Action action, CompletionsRequestFailure? failure = null)
{
    try { action(); } catch (CompletionsRequestException error)
    { Check(failure is null || error.Failure == failure, "Typed admission category changed."); Check(!error.Message.Contains("PRIVATE_", StringComparison.Ordinal), "Rejected map entered diagnostic text."); return; }
    throw new InvalidOperationException("Invalid mode/map was admitted.");
}
async Task Run(string name, Func<Task> action)
{
    try { await action(); passed++; outcomes.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; outcomes.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
foreach (var invalid in new[] { "[]", "null", "{\"low\":42}", "{\"low\":true}", "{\"low\":{}}", "{\"PRIVATE_UNKNOWN\":\"PRIVATE_VALUE\"}", "{\"low\":\"\\ud800\"}" })
    await Run("native-invalid-map-" + outcomes.Count, () => { Rejected(() => Factory(new() { ThinkingLevelMap = JsonData.Parse(invalid) })); return Task.CompletedTask; });
await Run("native-duplicate-map-rejected-by-json-carrier-before-factory", () =>
{
    var factoryCalls = 0;
    try { var map = JsonData.Parse("{\"low\":\"first\",\"low\":\"second\"}"); factoryCalls++; Factory(new() { ThinkingLevelMap = map }); }
    catch (JsonException) { Check(factoryCalls == 0, "Duplicate map reached factory configuration."); return Task.CompletedTask; }
    throw new InvalidOperationException("Duplicate map bypassed JSON carrier admission.");
});
await Run("native-invalid-format-before-effects", () => { Rejected(() => Factory(new() { ReasoningFormat = (CompletionsReasoningFormat)42 }), CompletionsRequestFailure.InvalidConfiguration); return Task.CompletedTask; });
await Run("native-map-byte-quota-before-effects", () =>
{ Rejected(() => Factory(new(MaximumPayloadBytes: 64) { ThinkingLevelMap = JsonData.Parse(JsonSerializer.Serialize(new { low = new string('界', 30) })) }), CompletionsRequestFailure.ResourceLimit); return Task.CompletedTask; });
await Run("native-mapped-payload-quota-preserves-history", () =>
{
    var original = request.Messages[0].WireBody.ToString();
    var factory = Factory(new(ReasoningEffort: "low", MaximumPayloadBytes: 256) { ThinkingLevelMap = JsonData.Parse(JsonSerializer.Serialize(new { low = new string('x', 180) })) });
    Rejected(() => factory.Create(request, "authored-inert-key"), CompletionsRequestFailure.ResourceLimit);
    Check(original == request.Messages[0].WireBody.ToString(), "Rejected mapped output rewrote canonical input."); return Task.CompletedTask;
});
await Run("native-record-clones-retain-independent-bindings", async () =>
{
    var original = new CompletionsKeyAuthRequestOptions(ReasoningEffort: "high") { ThinkingLevelMap = JsonData.Parse("{\"high\":\"low\"}") };
    var changed = original with { ReasoningFormat = CompletionsReasoningFormat.OpenAI, ThinkingLevelMap = JsonData.Parse("{\"high\":\"medium\"}") };
    var first = Factory(original); var second = Factory(changed);
    foreach (var selected in new[] { first, second, first })
    {
        using var owned = selected.Create(request, "authored-inert-key"); using var body = JsonDocument.Parse(await owned.Content!.ReadAsStringAsync());
        if (ReferenceEquals(selected, first)) Check(body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() == "low" && !body.RootElement.TryGetProperty("reasoning_effort", out _), "Original binding changed after a clone.");
        else Check(body.RootElement.GetProperty("reasoning_effort").GetString() == "medium" && !body.RootElement.TryGetProperty("reasoning", out _), "Explicit clone did not retain its own mode/map.");
    }
});
await Run("native-cancelled-request-has-no-created-request", () =>
{
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { Factory(new(ReasoningEffort: "low")).Create(request, "authored-inert-key", cancelled.Token); }
    catch (OperationCanceledException) { return Task.CompletedTask; } throw new InvalidOperationException("Cancelled request was created.");
});

// Invoke the exact existing five request-factory groups; no original test body or fixture is copied or changed.
var compatibility = Assembly.Load("PiSharp.Compatibility.Tests");
var cases = (IEnumerable<(string Name, Func<Task> Run)>)compatibility.GetType("CompletionsRequestFactoryTests", true)!
    .GetMethod("Cases", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
foreach (var test in cases) await Run(test.Name, test.Run);
var report = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
var dll = typeof(CompletionsKeyAuthRequestFactory).Assembly.Location;
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { schemaVersion = 1, passed, failed, observations = outcomes,
    productionAiDll = new { path = dll, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dll))) },
    sameExistingFactoryGroupsExecuted = 5, allEffectsOffline = true, completeCompatibilitySuite = false, fullGate = false, phaseAcceptance = false }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"Reasoning controls: {passed} passed, {failed} failed."); return failed == 0 ? 0 : 1;
