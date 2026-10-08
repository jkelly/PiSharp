using System.Text.Json;
using System.Text.Json.Nodes;

// Future negative controls mutate copies of an ACTUAL admitted receipt. They never
// supply a passing preparation proof and never replace the producer's original artifact.
internal static class AdmissionFaultCases
{
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] admittedArgs) =>
    [
        ("complete-admission-rejects-unscoped-legacy-dll-receipt", evidence => Reject(admittedArgs, evidence, "unscoped", receipt =>
        { receipt.Remove("selectorIntegration"); receipt.Remove("selector"); })),
        ("complete-admission-rejects-scoped-product-hash-mismatch", evidence => Reject(admittedArgs, evidence, "scope-hash", receipt =>
        { receipt["selectorIntegration"]!["assemblies"]![0]!["sha256"] = new string('0', 64); }))
    ];

    private static Task Reject(string[] args, ConsumerEvidence evidence, string id, Action<JsonObject> mutate)
    {
        var copy = JsonNode.Parse(File.ReadAllText(args[4]))!.AsObject(); mutate(copy);
        var directory = args[0] + ".admission-controls";
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, id + ".negative-receipt.json");
        using (var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write))
        { using var writer = new StreamWriter(stream); writer.Write(copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        var invocation = (string[])args.Clone(); invocation[0] = Path.Combine(directory, id + ".unused-report.json");
        invocation[4] = receipt; invocation[5] = EvidenceAdmission.Hash(receipt);
        Exception? rejected = null;
        try { _ = new EvidenceAdmission(invocation); } catch (Exception error) { rejected = error; }
        evidence.Observe("actual-mutated-negative-admission", new { id, actualProducerReceiptSha256 = args[5],
            negativeReceiptSha256 = invocation[5], rejected = rejected is null ? null : ConsumerException.From(rejected),
            passingReceipt = false, copiedFromActualAdmittedReceipt = true });
        TerminalSelectDialogCases.True(rejected is InvalidDataException or KeyNotFoundException);
        return Task.CompletedTask;
    }
}
