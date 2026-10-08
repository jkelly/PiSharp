// Authored registry/worker integration controls; execution is owned by the native test coordinator.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.ExtensionHost.Supervision;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Dispatch;

var metadataControls = NativeToolLoadoutMetadataTests.Run();
if (args.Length > 0 && args[0] == "--metadata-only")
{
    if (args.Length != 1 && (args.Length != 3 || args[1] != "--report" || !Path.IsPathFullyQualified(args[2])))
        throw new ArgumentException("Expected --metadata-only with optional --report and fresh absolute path.");
    if (args.Length == 3)
    {
        var report = JsonSerializer.Serialize(new { sourceCommit = NodeTierAAdmission.SourceCommit,
            scope = "Authored native metadata codec contracts only; no worker or original extension prepareLoadout coverage",
            tests = metadataControls.Length, passed = metadataControls.Length, failed = 0,
            results = metadataControls.Select(id => new { id, status = "passed" }) });
        await using var file = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write);
        await using var writer = new StreamWriter(file);
        await writer.WriteLineAsync(report);
    }
    return;
}
if (args.Length == 1 && args[0] == "--sink-only") { await NativeToolProgressTests.RunAsync(); return; }
var paths = new Dictionary<string, string>(StringComparer.Ordinal);
string[] required = ["--node", "--repo", "--run-root", "--oracle", "--jiti", "--reference", "--command-input-reference"];
for (var i = 0; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || !required.Contains(args[i]) || !Path.IsPathFullyQualified(args[i + 1]) || !paths.TryAdd(args[i], args[i + 1]))
        throw new ArgumentException("Unique explicit absolute paths required.");
}
if (paths.Count != required.Length) throw new ArgumentException("All native admission test paths required.");
await NativeToolProgressTests.RunAsync();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var launch = NodeCommandInputWorkerLaunch.ForCommandsAndInput(paths["--node"], paths["--repo"], paths["--run-root"], paths["--oracle"],
    paths["--jiti"], paths["--reference"], paths["--command-input-reference"], 1, 1);
await using var registry = new ExtensionRegistry(uiProvider: new UnavailableExtensionUiProvider(ExtensionUiMode.Print));
var extension = new NodeCommandInputExtension(launch, paths["--repo"], sourcePaths: [NodeTierAAdmission.PirateSource, NodeTierAAdmission.HelloSource]);
var scope = await registry.ActivateAsync("tier-a", extension, deadline.Token);
var snapshot = registry.CaptureSnapshot();
Check(snapshot.Registrations.Length == 3, "pirate command/hook and hello tool publish together");
Check(snapshot.Registrations.All(row => row.OwnerId == "tier-a" && row.OwnerGeneration == scope.OwnerGeneration), "all rows owned by same actual generation");
var dispatcher = new RegisteredExtensionEventDispatcher(registry, _ => {});
var input = new ExtensionBeforeAgentStartEvent("request", "base-system");
var initial = await dispatcher.DispatchBeforeAgentStartAsync(input, deadline.Token);
Check(initial.ForcedSystemPrompt is null && initial.Diagnostics.IsEmpty, "pirate starts disabled");
await registry.InvokeCommandAsync(snapshot, "pirate", JsonData.Parse("\"\""), deadline.Token);
var enabled = await dispatcher.DispatchBeforeAgentStartAsync(input, deadline.Token);
Check(enabled.ForcedSystemPrompt is { } forced && forced.StartsWith("base-system", StringComparison.Ordinal) && forced.Contains("PIRATE MODE", StringComparison.Ordinal), "whole original closure changes the prompt");
Check(enabled.Diagnostics.IsEmpty && enabled.Messages.IsEmpty && input.SystemPrompt == "base-system", "only outgoing system prompt changed");
await registry.InvokeCommandAsync(snapshot, "pirate", JsonData.Parse("\"\""), deadline.Token);
var disabled = await dispatcher.DispatchBeforeAgentStartAsync(input, deadline.Token);
Check(disabled.ForcedSystemPrompt is null && disabled.Diagnostics.IsEmpty, "second command restores disabled state");
Check(snapshot.Tools.Single().HasInitialArgumentPreparation, "generic tool advertises initial preparation");
var raw = JsonData.Parse("{\"name\":\"Joe\"}");
var prepared = await registry.PrepareToolArgumentsAsync(snapshot, "hello", raw, deadline.Token);
Check(prepared.Value.GetProperty("name").GetString() == "Joe" && raw.ToString() == "{\"name\":\"Joe\"}", "original preparation uses retained live schema and immutable caller input");
var preparationRejected = false;
try { await registry.PrepareToolArgumentsAsync(snapshot, "hello", JsonData.Parse("{}"), deadline.Token); }
catch (InvalidOperationException) { preparationRejected = true; }
Check(preparationRejected, "original schema rejects missing required name");
var greeting = await registry.InvokeToolAsync(snapshot, "hello", prepared, "host-call-1",
    (_, _) => throw new InvalidOperationException("hello must not publish updates"), deadline.Token);
Check(greeting.Value.GetProperty("content")[0].GetProperty("text").GetString() == "Hello, Joe!", "original non-UI tool executed with actual host identity");
var toolReceipt = extension.SourceOperations.Single(row => row.Value.GetProperty("kind").GetString() == "tool").Value.GetProperty("observation");
var toolContext = toolReceipt.GetProperty("context");
Check(toolContext.GetProperty("owner").GetString() == "genuine ExtensionRunner.createToolContext" &&
    toolContext.GetProperty("actualNativeToolCallId").GetString() == "host-call-1" && toolContext.GetProperty("toolContextShape").GetProperty("toolsGetter").GetBoolean() &&
    toolContext.GetProperty("toolContextShape").GetProperty("executeToolMethod").GetBoolean(), "actual source tool context has parent identity and guarded tool members");
Check(toolReceipt.GetProperty("updateDeliveryJoined").GetBoolean(), "tool settlement joins progress publication");
Check(extension.ActiveContexts == 0 && extension.SourceOperations.All(row => row.Value.GetProperty("settled").GetBoolean()), "all source operations settled and native contexts retired");
await scope.DisposeAsync();
Check(registry.CaptureSnapshot().Registrations.IsEmpty, "owner disposal removes every translated registration");
Check(extension.SourceFinalizationReport?.Value.GetProperty("immutableInputsVerified").GetBoolean() == true, "source inventory verified after shutdown");
var rejected = false;
try { await registry.InvokeCommandAsync(snapshot, "pirate", JsonData.Parse("\"\""), deadline.Token); }
catch (ExtensionRegistrationException) { rejected = true; }
Check(rejected, "captured stale generation cannot dispatch after owner disposal");
Console.WriteLine(JsonSerializer.Serialize(new { authoredScenario = "original-pirate-toggle-hook-and-hello-registry", sourceCommit = NodeTierAAdmission.SourceCommit,
    passed = true, scope = "native registry and optional worker; no provider, CLI package admission, or full-phase credit" }));
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
