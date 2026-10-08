using System.Reflection;

if (args.Length == 0) { Console.Error.WriteLine("Fresh report and complete immutable admission inputs required."); return 1; }
var cases = TerminalSelectDialogCases.Cases().Concat(StartupBarrierCases.Cases()).Concat(StartupCommandCases.Cases(args)).Concat(TerminalTextAdoptionCases.Cases(args)).Concat(TerminalQueueAdoptionCases.Cases(args)).Concat(TerminalQueueRestorationCases.Cases(args)).Concat(TerminalInterruptCases.Cases()).Concat(TerminalNativeInterruptCases.Cases(args)).Concat(TerminalGracefulShutdownCases.Cases(args)).Concat(TerminalInputDrainCases.Cases(args)).Concat(TerminalTwoPhaseShutdownCases.Cases(args)).Concat(TerminalRpcAdmissionCancellationCases.Cases(args)).Concat(TerminalRpcReadCancellationCases.Cases(args)).Concat(TerminalOwnedChildShutdownCases.Cases(args)).Concat(TerminalSessionNavigationCases.Cases()).Concat(TerminalNavigationSettingsCases.Cases(args)).Concat(TerminalStartupValidationCases.Cases()).Concat(SdkAdmissionFaultCases.Cases(args)).Concat(AdmissionFaultCases.Cases(args)).ToArray();
var plan = cases.Select(row => new ConsumerCasePlan("selector-integration", row.Id))
    .Append(new ConsumerCasePlan("evidence-controls", "durable-failures-and-collisions")).ToArray();
return await ConsumerExecution.RunAsync(args[0], plan, new
{
    reviewRoot = args.ElementAtOrDefault(1), sourceManifestPath = args.ElementAtOrDefault(2),
    sourceManifestSha256 = args.ElementAtOrDefault(3), buildReceiptPath = args.ElementAtOrDefault(4),
    buildReceiptSha256 = args.ElementAtOrDefault(5), independentlyVerified = false
}, async evidence =>
{
    evidence.Stage("admission");
    evidence.Observe("unverified-entry", new { path = Assembly.GetExecutingAssembly().Location,
        sha256 = EvidenceAdmission.Hash(Assembly.GetExecutingAssembly().Location), independentlyVerified = false });
    var admission = new EvidenceAdmission(args); evidence.Admit(admission);
    evidence.Observe("verified-sdk-selection", admission.Sdk);
    foreach (var row in cases)
    {
        evidence.Begin("selector-integration", row.Id, new { authoredExpectationsOnly = true });
        await row.Run(evidence);
        evidence.Complete(new { passed = true, actualExtensionScopeInputViewConsumer = true, genuineSourceDifferential = false });
    }
    evidence.Begin("evidence-controls", "durable-failures-and-collisions");
    evidence.Complete(await FailureReportCases.RunAsync(args[0] + ".failure-controls", evidence));
}, Console.Error);
