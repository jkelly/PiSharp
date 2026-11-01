// PiSharp-only (no upstream Pi source): deterministic calendar and guarded-ledger tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.CalendarSchedulingCoordinator;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-calendar-" + Guid.NewGuid().ToString("N"));
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject(Action action, string message) { var failed = false; try { action(); } catch (ArgumentException) { failed = true; } catch (InvalidOperationException) { failed = true; } catch (FormatException) { failed = true; } Check(failed, message); }
        async Task RejectAsync(Func<Task> action, string message) { var failed = false; try { await action(); } catch (ArgumentException) { failed = true; } catch (InvalidOperationException) { failed = true; } Check(failed, message); }
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root, input); var plan = Workflow.Plan(c, "meeting-a");
            Check(plan["slots"]![0]!["startUtc"]!.GetValue<string>() == "2026-11-02T15:30:00Z", "Timezone conversion and overlapping busy ranges determine first free slot.");
            Check(plan["slots"]![0]!["localStarts"]!["sam"]!.GetValue<string>().StartsWith("2026-11-02T10:30:00", StringComparison.Ordinal), "Participant local display.");
            var run = await SampleHost.ExecuteAsync(Workflow.Create(c), c);
            Check(run.Success && run.Report["outputs"]!.AsArray().Count == 3 && File.Exists(c.ResolvePath("proposal-meeting-a.json")), "Native fixture executes three tools and persists proposal.");
            Reject(() => Workflow.Plan(c, "missing"), "Missing request rejected.");
            var duplicate = (JsonObject)input.DeepClone(); duplicate["people"]!.AsArray().Add(duplicate["people"]![0]!.DeepClone());
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "duplicate"), duplicate), "meeting-a"), "Ambiguous calendar rejected.");
            var dst = (JsonObject)input.DeepClone(); dst["people"]![0]!["available"]![0]!["start"] = "2026-11-01T01:30:00";
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "dst"), dst), "meeting-a"), "DST ambiguity fails honestly.");
            var offsetless = (JsonObject)input.DeepClone(); offsetless["requests"]![0]!["windowStartUtc"] = "2026-11-02T15:00:00";
            Reject(() => Workflow.Plan(new SampleContext(Path.Combine(root, "offsetless"), offsetless), "meeting-a"), "UTC windows require an explicit Z offset.");
            foreach (var collection in new[] { "requests", "people", "available", "busy" })
            foreach (var scalar in new[] { false, true })
            {
                var malformed = (JsonObject)input.DeepClone();
                var records = collection is "requests" or "people" ? malformed[collection]!.AsArray() : malformed["people"]![0]![collection]!.AsArray();
                records.Add(scalar ? JsonValue.Create("not-a-record") : null);
                var badContext = new SampleContext(Path.Combine(root, $"malformed-{collection}-{scalar}"), malformed, approved: true);
                Reject(() => Workflow.Plan(badContext, "meeting-a"), $"Malformed {collection} record cannot produce a free slot.");
                await RejectAsync(async () => { await Workflow.Book(badContext, "meeting-a"); }, $"Malformed {collection} record cannot be booked.");
                Check(!File.Exists(badContext.ResolvePath("booking-meeting-a.json")), "Invalid calendar data creates no booking file.");
            }
            var b = Workflow.Plan(c, "meeting-b"); Check(b["requestId"]!.GetValue<string>() == "meeting-b" && b["durationMinutes"]!.GetValue<int>() == 60, "Second request does not reuse first plan.");
            await RejectAsync(async () => { await Workflow.Create(c).Tools.Single(t => t.Name == "book_local_meeting").Execute(Workflow.Args("meeting-a"), default); }, "Direct callback requires approval.");
            Check(!File.Exists(c.ResolvePath("booking-meeting-a.json")), "Denied callback created no booking.");
            var approved = new SampleContext(root, input, approved: true);
            var booked = await Workflow.Book(approved, "meeting-a");
            var replay = await Workflow.Book(new SampleContext(root, (JsonObject)input.DeepClone(), approved: true), "meeting-a");
            Check(JsonNode.DeepEquals(booked, replay) && Directory.GetFiles(root, "booking-*.json").Length == 1, "Repeated context reuses booking exactly once.");
            var collision = (JsonObject)input.DeepClone(); var request = collision["requests"]![1]!.AsObject(); request["durationMinutes"] = 30; request["windowStartUtc"] = "2026-11-02T15:30:00Z"; request["windowEndUtc"] = "2026-11-02T16:00:00Z";
            Check(Workflow.Plan(new SampleContext(root, collision), "meeting-b")["slots"]!.AsArray().Count == 0, "Existing local booking blocks participant overlap.");
            var changed = (JsonObject)input.DeepClone(); changed["requests"]![0]!["durationMinutes"] = 45;
            await RejectAsync(async () => { await Workflow.Book(new SampleContext(root, changed, approved: true), "meeting-a"); }, "Same booking ID rejects changed payload.");
            var approvedRunContext = new SampleContext(Path.Combine(root, "native-approved"), input, approved: true);
            var approvedRun = await SampleHost.ExecuteAsync(Workflow.Create(approvedRunContext), approvedRunContext);
            Check(approvedRun.Success && approvedRun.Report["outputs"]!.AsArray().Count == 4, "Native approval path runs booking stage.");
            Console.WriteLine($"PASS CalendarSchedulingCoordinator: {checks} focused checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root) && Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(root, true); }
    }
}
