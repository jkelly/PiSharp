using System.Reflection;

// Source-only authored consumer: invocation is gated by a later explicit runtime handoff and sealed identities.
if (args.Length == 0) { Console.Error.WriteLine("A fresh report path is required."); return 1; }
var cases = SelectListCases.Cases().Concat(SelectListRendererCases.Cases()).Concat(SdkAdmissionFaultCases.Cases(args).Select(row => new SelectListCase(row.Id, "Exact SDK-selection rejection from actual admitted receipt", row.Run))).ToArray();
var plan = cases.Select(row => new ConsumerCasePlan("select-list", row.Id))
    .Append(new ConsumerCasePlan("evidence-controls", "durable-failures-and-collisions")).ToArray();
return await ConsumerExecution.RunAsync(args[0], plan, new
{
    reviewRoot = args.ElementAtOrDefault(1), sourceManifestPath = args.ElementAtOrDefault(2),
    sourceManifestSha256 = args.ElementAtOrDefault(3), buildReceiptPath = args.ElementAtOrDefault(4),
    buildReceiptSha256 = args.ElementAtOrDefault(5), independentlyVerified = false
}, async evidence =>
{
    evidence.Stage("admission");
    foreach (var assembly in new[] { typeof(PiSharp.Tui.Components.SelectList.TerminalSelectList).Assembly, Assembly.GetExecutingAssembly() })
        evidence.Observe("unverified-executing-assembly", new { path = assembly.Location,
            sha256 = EvidenceAdmission.Hash(assembly.Location), independentlyVerified = false });
    var admission = new EvidenceAdmission(args); evidence.Admit(admission);
    evidence.Observe("verified-sdk-selection", admission.Sdk);
    foreach (var row in cases)
    {
        evidence.Begin("select-list", row.Id, new { authoredExpectationsOnly = true, row.Description });
        SelectListCases.ActiveEvidence = evidence;
        try { await row.Run(evidence); }
        finally { SelectListCases.ActiveEvidence = null; }
        evidence.Complete(new { passed = true, actualComponentConsumer = true, genuineSourceDifferential = false });
    }
    evidence.Begin("evidence-controls", "durable-failures-and-collisions");
    evidence.Complete(await FailureReportCases.RunAsync(args[0] + ".failure-controls", evidence));
}, Console.Error);
