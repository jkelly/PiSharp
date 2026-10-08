using System.Text.Json;

// Source-only authored consumer. Native execution requires a separate lead-owned runtime handoff.
if (args.Length == 0)
{
    Console.Error.WriteLine("A fresh report path is required; no report can be reserved without it."); return 1;
}
return ConsumerExecution.Run(args[0], ConsumerExecution.StandardPlan(), new
{
    reviewRoot = args.ElementAtOrDefault(1), manifestPath = args.ElementAtOrDefault(2),
    requestedManifestSha256 = args.ElementAtOrDefault(3), buildReceiptPath = args.ElementAtOrDefault(4),
    requestedReceiptSha256 = args.ElementAtOrDefault(5), requestedIdentityIndependentlyVerified = false
}, evidence =>
{
    ConsumerExecution.ObserveInputs(evidence, args);
    evidence.Stage("admission");
    var admission = new EvidenceAdmission(args); // Allocated consumer pins all twelve products and its exact CLI friend DLL.
    evidence.Admit(admission);
    evidence.Stage("loading-admitted-fixtures");
    var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "authored-keybinding-cases.json");
    var inventoryPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "source-defaults-inventory.json");
    using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
    using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
    var configurationPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "authored-configuration-loading-cases.json");
    using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));
    evidence.Observe("fixture", new { path = fixturePath, sha256 = EvidenceAdmission.Hash(fixturePath) });
    evidence.Observe("static-source-inventory", new { path = inventoryPath, sha256 = EvidenceAdmission.Hash(inventoryPath) });
    evidence.Observe("authored-configuration-fixture", new { path = configurationPath, sha256 = EvidenceAdmission.Hash(configurationPath) });
    RegistryCases.Run(fixture.RootElement, evidence);
    evidence.Begin("defaults", "source-defaults"); evidence.Complete(RegistryCases.Defaults(inventory.RootElement, evidence));
    evidence.Begin("supplemental", "native-controls"); evidence.Complete(RegistryCases.Supplemental(evidence));
    EditorBoundaryCases.Run(fixture.RootElement, evidence);
    EditorBoundaryCases.RunConfigured(fixture.RootElement, evidence);
    evidence.Begin("configured-controls", "original-events-and-dispatch");
    evidence.Complete(EditorBoundaryCases.ConfiguredControls(evidence));
    ConfigurationLoadingCases.Run(configuration.RootElement, evidence);
    evidence.Begin("configuration-controls", "platform-path-reload-and-bounds");
    evidence.Complete(ConfigurationLoadingCases.Controls(evidence, admission.ReviewRoot));
    TerminalStartupCases.Run(evidence, admission.ReviewRoot);
    ConfigurationLoadingCases.RunLoadDefault(configuration.RootElement, evidence, admission.ReviewRoot);
    TerminalStartupCases.RunDefault(evidence, admission.ReviewRoot);
    evidence.Begin("evidence-controls", "failure-report-controls");
    evidence.Complete(FailureReportCases.Run(fixture.RootElement, args[0] + ".failure-controls", evidence));
}, Console.Error);
