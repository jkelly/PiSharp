// PiSharp-only (no upstream Pi source): offline feasibility and approval regression tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.TravelDisruptionAssistant;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-60-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var ranked = Workflow.Rank(c, "trip-1");
            Check(ranked["feasible"]!.AsArray().Count == 2 && ranked["feasible"]![0]!["alternativeId"]!.GetValue<string>() == "connection", "cheapest feasible ranked first");
            Check(ranked["rejected"]![0]!["reasons"]!.AsArray().Any(x => x!.GetValue<string>() == "invalid-connection"), "minimum connection enforced");
            Check(ranked["rejected"]![1]!["reasons"]!.AsArray().Count == 2, "refundability and cent budget enforced");
            Check(Workflow.Rank(c, "trip-2").Text("status") == "no-feasible-alternative", "second ID has honest empty result");
            Check(JsonNode.DeepEquals(ranked, Workflow.Rank(c, "trip-1")), "repeat deterministic");
            await Throws(() => Task.FromResult(Workflow.Rank(c, "missing")), "unknown ID");
            var incompleteTrip = (JsonObject)input.DeepClone(); incompleteTrip["itineraries"]![0]!.AsObject().Remove("disruptedServiceId");
            await Throws(() => Task.FromResult(Workflow.Rank(new SampleContext(Path.Combine(root, "missing-service"), incompleteTrip), "trip-1")), "missing disrupted service cannot produce feasible proposal");
            var chosen = ranked["feasible"]![0]!.AsObject();
            var args = new JsonObject { ["itineraryId"] = "trip-1", ["alternativeId"] = chosen.Text("alternativeId"), ["expectedHash"] = chosen.Text("expectedHash") };
            Check(Workflow.Create(c).Tools.Single(t => t.Name == "rebook_local").RequiresApproval, "host approval metadata");
            await Throws(() => Workflow.Rebook(c, args, default), "direct callback approval guard");
            Check(!File.Exists(c.ResolvePath("local-rebooking-trip-1.json")), "denied callback no mutation");
            var approved = new SampleContext(Path.Combine(root, "approved"), (JsonObject)input.DeepClone(), approved: true);
            var receipt = await Workflow.Rebook(approved, args, default);
            Check(JsonNode.DeepEquals(receipt, await Workflow.Rebook(approved, args, default)), "approved repeat idempotent");
            var alternative = ranked["feasible"]![1]!.AsObject();
            await Throws(() => Workflow.Rebook(approved, new JsonObject { ["itineraryId"] = "trip-1", ["alternativeId"] = alternative.Text("alternativeId"), ["expectedHash"] = alternative.Text("expectedHash") }, default), "different booking conflicts");
            approved.Input["itineraries"]![0]!["maxBudgetCents"] = 46000;
            await Throws(() => Workflow.Rebook(approved, args, default), "stale proposal hash rejected");
            input["itineraries"]![0]!["alternatives"]![0]!["legs"]![0]!["origin"] = "LAX";
            Check(Workflow.Rank(c, "trip-1")["feasible"]!.AsArray().Count == 1, "route continuity enforced without stale cache");
            input["itineraries"]![0]!["alternatives"]![1]!["costCents"] = 32000.5;
            await Throws(() => Task.FromResult(Workflow.Rank(c, "trip-1")), "fractional cents rejected");
            var native = new SampleContext(Path.Combine(root, "native"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
            Check((await SampleHost.ExecuteAsync(Workflow.Create(native), native)).Success, "native proposal workflow");
            Check(File.Exists(native.ResolvePath("travel-trip-1.json")), "proposal persisted");
            var nativeApproved = new SampleContext(Path.Combine(root, "native-approved"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), approved: true);
            Check(Workflow.Create(nativeApproved).DemoCalls.Count == 4 && (await SampleHost.ExecuteAsync(Workflow.Create(nativeApproved), nativeApproved)).Success, "approved four-stage native workflow");
            Check(File.Exists(nativeApproved.ResolvePath("local-rebooking-trip-1.json")), "approved native simulation persisted");
            Console.WriteLine("PASS 60: routes, connections, policies, IDs, integer cents, approval, stale hashes and idempotency."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL 60: " + e.Message); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> action, string message) { try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException) { return; } throw new InvalidOperationException(message); }
}
