// PiSharp-only (no upstream Pi source): offline evidence reconciliation regression tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.InsuranceClaimOrganizer;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-62-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var reconciled = Workflow.Reconcile(c, "claim-1");
            Check(reconciled["receiptTotalCents"]!.GetValue<long>() == 37500 && reconciled["balanced"]!.GetValue<bool>(), "integer cents reconcile");
            Check(reconciled["receipts"]!.AsArray().Count == 2 && reconciled["receipts"]![0]!["duplicateCount"]!.GetValue<int>() == 1, "duplicate not double counted");
            Check(reconciled["receipts"]![0]!["sourceIds"]!.AsArray().Count == 2, "duplicate provenance retained");
            Check(Workflow.Inventory(c, "claim-1")["missingRequirements"]![0]!["kind"]!.GetValue<string>() == "policy", "missing policy reported");
            input["claims"]![0]!["requirements"]![1]!["minCount"] = 3;
            Check(Workflow.Inventory(c, "claim-1")["missingRequirements"]!.AsArray().Count == 2, "duplicate copies cannot satisfy distinct receipt count");
            input["claims"]![0]!["requirements"]![1]!["minCount"] = 1;
            Check(Workflow.Inventory(c, "claim-2")["evidence"]!.AsArray().Count == 0, "second claim independent");
            Check(JsonNode.DeepEquals(reconciled, Workflow.Reconcile(c, "claim-1")), "repeat deterministic");
            Throws(() => Workflow.Reconcile(c, "absent"), "missing claim rejected");
            input["claims"]![0]!["evidence"]![3]!["amountCents"] = 26000;
            var conflict = Workflow.Reconcile(c, "claim-1");
            Check(conflict["receiptTotalCents"]!.GetValue<long>() == 12500 && conflict["issues"]!.AsArray().Count == 1, "conflicting duplicate excluded");
            input["claims"]![0]!["evidence"]![3]!["amountCents"] = 25000;
            input["claims"]![0]!["evidence"]![4]!["currency"] = "EUR";
            Check(Workflow.Reconcile(c, "claim-1")["receiptTotalCents"]!.GetValue<long>() == 25000, "currency mismatch excluded");
            input["claims"]![0]!["evidence"]![0]!["date"] = "";
            Check(Workflow.Inventory(c, "claim-1")["missingRequirements"]!.AsArray().Count == 3, "missing photo metadata and count reported");
            input["claims"]![0]!["evidence"]!.AsArray().Add(input["claims"]![0]!["evidence"]![1]!.DeepClone());
            Throws(() => Workflow.Inventory(c, "claim-1"), "duplicate source identity rejected");
            var native = new SampleContext(Path.Combine(root, "native"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
            Check((await SampleHost.ExecuteAsync(Workflow.Create(native), native)).Success, "native inventory and manifest workflow");
            Check(File.Exists(native.ResolvePath("claim-claim-1.json")), "manifest persisted");
            Console.WriteLine("PASS 62: inventory, missing metadata, duplicates, conflicts, currencies, IDs, repeats and native manifest."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL 62: " + e.Message); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws(Action action, string message) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException(message); }
}
