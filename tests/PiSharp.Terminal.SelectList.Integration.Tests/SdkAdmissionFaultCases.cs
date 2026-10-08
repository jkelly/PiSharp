using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// These are future controls derived exclusively from the actual receipt admitted
// by this consumer. Copies and owned negative logs never become passing proofs.
internal static class SdkAdmissionFaultCases
{
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("sdk-admission-rejects-missing-selection-step", e => Reject(args, e, "missing-step", "sdk-admission:missing-step")),
        ("sdk-admission-rejects-missing-log-declaration", e => Reject(args, e, "missing-log-declaration", "sdk-admission:missing-log")),
        ("sdk-admission-rejects-missing-physical-log", e => Reject(args, e, "missing-log-file", "sdk-admission:missing-log")),
        ("sdk-admission-rejects-missing-recorded-version", e => Reject(args, e, "missing-version", "sdk-admission:missing-version")),
        ("sdk-admission-rejects-version-different-from-hashed-log", e => Reject(args, e, "version-mismatch", "sdk-admission:version-mismatch")),
        ("sdk-admission-rejects-matching-log-version-outside-pinned-policy", e => Reject(args, e, "policy-mismatch", "sdk-admission:policy-mismatch"))
    ];
    private static Task Reject(string[] args, ConsumerEvidence evidence, string id, string expectedReason)
    {
        var originalHash = EvidenceAdmission.Hash(args[4]);
        if (originalHash != args[5]) throw new InvalidDataException("Actual admitted producer receipt changed before SDK control.");
        var original = JsonNode.Parse(File.ReadAllText(args[4]))!.AsObject();
        var copy = original.DeepClone().AsObject(); var history = copy["actualPreparationSteps"]!.AsArray();
        var sdk = history.Select(node => node!.AsObject()).Single(row => row["id"]!.GetValue<string>() == "dotnet-sdk-version");
        var logs = Path.Combine(Path.GetFullPath(args[1]), "artifacts", "selector-sdk-negative-" + Guid.NewGuid().ToString("N"));
        object? negativeLog = null;
        switch (id)
        {
            case "missing-step": history.Remove(sdk); break;
            case "missing-log-declaration": sdk.Remove("log"); break;
            case "missing-log-file":
                Directory.CreateDirectory(logs);
                sdk["log"]!["path"] = Path.Combine(logs, "deliberately-absent.log"); break;
            case "missing-version": copy.Remove("sdkVersion"); break;
            case "version-mismatch": copy["sdkVersion"] = "10.0.999"; break;
            case "policy-mismatch":
                Directory.CreateDirectory(logs); var log = Path.Combine(logs, "owned-negative-sdk.log");
                // Match the declared version and actual hashed log, while keeping the sealed global.json unchanged.
                using (var stream = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { var bytes = Encoding.UTF8.GetBytes("9.0.100\n"); stream.Write(bytes); stream.Flush(flushToDisk: true); }
                var hash = EvidenceAdmission.Hash(log); var count = new FileInfo(log).Length;
                sdk["log"] = new JsonObject { ["path"] = log, ["bytes"] = count, ["sha256"] = hash };
                copy["sdkVersion"] = "9.0.100"; negativeLog = new { path = log, bytes = count, sha256 = hash, passingProof = false }; break;
            default: throw new InvalidOperationException("Unknown SDK fault control.");
        }
        // Preserve every original scope/product/source/restore assertion; mutate only the allocated SDK fields.
        var retainedOriginal = original.DeepClone().AsObject(); var retainedCopy = copy.DeepClone().AsObject();
        retainedOriginal.Remove("sdkVersion"); retainedCopy.Remove("sdkVersion");
        static void RemoveSdk(JsonObject value)
        {
            var rows = value["actualPreparationSteps"]!.AsArray();
            foreach (var row in rows.Where(row => row!["id"]!.GetValue<string>() == "dotnet-sdk-version").ToArray()) rows.Remove(row);
        }
        RemoveSdk(retainedOriginal); RemoveSdk(retainedCopy);
        if (!JsonNode.DeepEquals(retainedOriginal, retainedCopy)) throw new InvalidOperationException("SDK control changed unrelated original preparation proof.");
        var directory = args[0] + ".sdk-admission-controls"; Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, id + ".negative-receipt.json");
        using (var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        var invocation = (string[])args.Clone(); invocation[0] = Path.Combine(directory, id + ".unused-report.json");
        invocation[4] = receipt; invocation[5] = EvidenceAdmission.Hash(receipt);
        Exception? rejected = null;
        try { _ = new EvidenceAdmission(invocation); } catch (Exception error) { rejected = error; }
        evidence.Observe("actual-sdk-negative-admission-before-assertion", new { id, expectedReason, originalHash,
            negativeReceiptSha256 = invocation[5], negativeLog, rejected = rejected is null ? null : ConsumerException.From(rejected),
            originalProofPreserved = EvidenceAdmission.Hash(args[4]) == originalHash, unrelatedProofFieldsPreserved = true, passingPreparationProof = false });
        if (rejected is not InvalidDataException || rejected.Message != expectedReason)
            throw new InvalidOperationException("SDK control failed intended rejection branch: " + expectedReason, rejected);
        if (EvidenceAdmission.Hash(args[4]) != originalHash) throw new InvalidDataException("SDK control mutated original producer receipt.");
        return Task.CompletedTask;
    }
}
