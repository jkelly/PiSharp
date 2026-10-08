using System.Reflection;
using System.Text.Json;

// A separately admitted partition, never a replacement for the original 140-case selector.
if (args.Length != 6) { Console.Error.WriteLine("Fresh focused report and all six immutable admission inputs required."); return 1; }
var cases = TerminalRpcAdmissionCancellationCases.Cases(args)
    .Concat(TerminalRpcReadCancellationCases.Cases(args))
    .Concat(TerminalOwnedChildShutdownCases.Cases(args)).ToArray();
var plan = cases.Select(row => new ConsumerCasePlan("selector-ownership-focused", row.Id))
    .Append(new ConsumerCasePlan("evidence-controls", "durable-failures-and-collisions")).ToArray();
return await ConsumerExecution.RunAsync(args[0], plan, new
{
    scope = "PARTIAL_SELECTOR_OWNERSHIP_31", originalTarget = "terminal-select-dialog",
    selectedOriginalCases = 31, excludedOriginalCases = 109, originalPlannedCases = 140,
    fullSelectorSuite = false, fullNativeGate = false,
    reviewRoot = args[1], sourceManifestPath = args[2], sourceManifestSha256 = args[3],
    buildReceiptPath = args[4], buildReceiptSha256 = args[5], independentlyVerified = false
}, async evidence =>
{
    evidence.Stage("focused-admission");
    evidence.Observe("unverified-focused-entry", new { path = Assembly.GetExecutingAssembly().Location,
        sha256 = EvidenceAdmission.Hash(Assembly.GetExecutingAssembly().Location), independentlyVerified = false });
    var admission = new EvidenceAdmission(args); evidence.Admit(admission);
    evidence.Observe("verified-sdk-selection", admission.Sdk);
    using var selectionDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "focused-case-plan.json")));
    var selection = selectionDocument.RootElement;
    var selected = selection.GetProperty("selectedCaseIds").EnumerateArray().Select(item => item.GetString()!).ToArray();
    if (cases.Length != 31 || selected.Length != 31 || selected.Distinct(StringComparer.Ordinal).Count() != 31 ||
        !cases.Select(row => row.Id).SequenceEqual(selected, StringComparer.Ordinal))
        throw new InvalidDataException("Focused ownership factory plan differs from immutable selected IDs.");
    evidence.Observe("admitted-partial-selector-partition", selection.Clone());
    foreach (var row in cases)
    {
        evidence.Begin("selector-ownership-focused", row.Id, new { authoredExpectationsOnly = true, partialSelectorScope = true });
        await row.Run(evidence); // Original linked body, assertions, cancellation and physical joins.
        evidence.Complete(new { passed = true, partialOwnershipCaseOnly = true, fullSelectorSuite = false, genuineSourceDifferential = false });
    }
    evidence.Begin("evidence-controls", "durable-failures-and-collisions");
    evidence.Complete(await FailureReportCases.RunAsync(args[0] + ".failure-controls", evidence));
}, Console.Error);
