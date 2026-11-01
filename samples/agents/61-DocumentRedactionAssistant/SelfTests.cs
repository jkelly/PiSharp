// PiSharp-only (no upstream Pi source): offline redaction and approval regression tests.
using System.Text.Json.Nodes;

namespace PiSharp.Samples.DocumentRedactionAssistant;
public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-61-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(); var c = new SampleContext(root, input);
            var result = Workflow.Redact(c, "letter-1");
            Check(result.Text.Contains("😀", StringComparison.Ordinal), "non-sensitive surrogate pair preserved");
            foreach (var value in new[] { "Zoë", "zoë@example.test", "312-555-0199", "123-45-6789", "Case Orion" })
                Check(!result.Text.Contains(value, StringComparison.OrdinalIgnoreCase), "detected value removed");
            Check(result.Matches.Length > result.Merged.Length, "overlapping literal and email matches merged");
            Check(!Workflow.Detect(c, "letter-1").ToJsonString().Contains("example.test", StringComparison.Ordinal), "detection output does not reveal values");
            Check(Workflow.Redact(c, "letter-2").Matches.Length == 0, "second document not stale");
            Check(Workflow.Redact(c, "letter-1").Text == result.Text, "repeated result deterministic");
            await Throws(() => Task.FromResult(Workflow.Redact(c, "unknown")), "missing ID rejected");
            var args = new JsonObject { ["documentId"] = "letter-1", ["expectedHash"] = result.Hash };
            Check(Workflow.Create(c).Tools.Single(t => t.Name == "export_redacted").RequiresApproval, "export host approval metadata");
            await Throws(() => Workflow.Export(c, args, default), "direct export callback checks approval");
            Check(!File.Exists(c.ResolvePath("redacted-letter-1.txt")), "denied export creates no file");
            var missingInput = (JsonObject)input.DeepClone();
            var missing = new SampleContext(Path.Combine(root, "missing-source"), missingInput, approved: true);
            var missingSpec = Workflow.Create(missing);
            missingInput["documents"]![0]!.AsObject().Remove("text");
            await Throws(() => Workflow.Export(missing, args, default), "missing source blocks direct export");
            Check(!(await SampleHost.ExecuteAsync(missingSpec, missing)).Success, "missing source cannot produce successful native workflow");
            Check(!File.Exists(missing.ResolvePath("redacted-letter-1.txt")) && !File.Exists(missing.ResolvePath("redaction-review-letter-1.json")), "missing source creates neither export nor review artifact");
            foreach (var invalidText in new JsonNode?[] { null, JsonValue.Create(123), new JsonObject() })
            {
                missingInput["documents"]![0]!["text"] = invalidText;
                await Throws(() => Workflow.Export(missing, args, default), "null and nonstring source rejected");
                Check(!File.Exists(missing.ResolvePath("redacted-letter-1.txt")), "invalid source never exports");
            }
            var emptyInput = (JsonObject)input.DeepClone(); emptyInput["documents"]![0]!["text"] = "";
            var empty = new SampleContext(Path.Combine(root, "explicit-empty"), emptyInput, approved: true);
            var emptyRedaction = Workflow.Redact(empty, "letter-1");
            Check(emptyRedaction.Text.Length == 0 && emptyRedaction.Matches.Length == 0, "explicit empty source remains a valid empty document");
            await Workflow.Export(empty, new JsonObject { ["documentId"] = "letter-1", ["expectedHash"] = emptyRedaction.Hash }, default);
            Check(new FileInfo(empty.ResolvePath("redacted-letter-1.txt")).Length == 0, "only explicit empty input permits empty export");
            var markerInput = (JsonObject)input.DeepClone();
            markerInput["documents"]![0]!["text"] = "Ted"; markerInput["documents"]![0]!["sensitiveLiterals"] = new JsonArray("Ted");
            var markerContext = new SampleContext(Path.Combine(root, "marker-collision"), markerInput, approved: true);
            var markerResult = Workflow.Redact(markerContext, "letter-1");
            Check(markerResult.Text == "█" && !markerResult.Text.Contains("Ted", StringComparison.OrdinalIgnoreCase), "safe marker avoids matching its own replacement text");
            await Workflow.Export(markerContext, new JsonObject { ["documentId"] = "letter-1", ["expectedHash"] = markerResult.Hash }, default);
            Check(await File.ReadAllTextAsync(markerContext.ResolvePath("redacted-letter-1.txt")) == "█", "marker collision exports without original data");
            var boundaryInput = (JsonObject)input.DeepClone();
            boundaryInput["documents"]![0]!["text"] = "][]["; boundaryInput["documents"]![0]!["sensitiveLiterals"] = new JsonArray("][");
            var boundaryContext = new SampleContext(Path.Combine(root, "marker-boundary"), boundaryInput, approved: true);
            var boundaryResult = Workflow.Redact(boundaryContext, "letter-1");
            Check(boundaryResult.Text == "██" && boundaryResult.Matches.Length == 2, "adjacent replacements cannot reconstruct a sensitive literal");
            await Workflow.Export(boundaryContext, new JsonObject { ["documentId"] = "letter-1", ["expectedHash"] = boundaryResult.Hash }, default);
            Check(await File.ReadAllTextAsync(boundaryContext.ResolvePath("redacted-letter-1.txt")) == "██", "boundary collision exports without reconstructed sensitive data");
            var approved = new SampleContext(Path.Combine(root, "approved"), (JsonObject)input.DeepClone(), approved: true);
            var receipt = await Workflow.Export(approved, args, default);
            Check(JsonNode.DeepEquals(receipt, await Workflow.Export(approved, args, default)), "repeated approved export idempotent");
            Check(await File.ReadAllTextAsync(approved.ResolvePath("redacted-letter-1.txt")) == result.Text, "export read back verified");
            approved.Input["documents"]![0]!["text"] = "New text zoë@example.test";
            await Throws(() => Workflow.Export(approved, args, default), "changed source hash rejected");
            input["documents"]![1]!["text"] = "😀 ababa fin"; input["documents"]![1]!["sensitiveLiterals"] = new JsonArray("aba", "bab");
            Check(Workflow.Redact(c, "letter-2").Text == "😀 [REDACTED] fin", "overlapping repeated literals retain unicode offsets");
            input["documents"]![1]!["format"] = "application/pdf";
            await Throws(() => Task.FromResult(Workflow.Redact(c, "letter-2")), "binary document rejected");
            var native = new SampleContext(Path.Combine(root, "native"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
            Check((await SampleHost.ExecuteAsync(Workflow.Create(native), native)).Success, "native review workflow");
            Check(File.Exists(native.ResolvePath("redaction-review-letter-1.json")), "sanitized review persisted");
            var nativeApproved = new SampleContext(Path.Combine(root, "native-approved"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject(), approved: true);
            Check(Workflow.Create(nativeApproved).DemoCalls.Count == 4 && (await SampleHost.ExecuteAsync(Workflow.Create(nativeApproved), nativeApproved)).Success, "approved native four-stage workflow");
            Check(File.Exists(nativeApproved.ResolvePath("redacted-letter-1.txt")), "approved native export persisted");
            Console.WriteLine("PASS 61: unicode, overlaps, values absent, IDs, repeats, approval, export verification and stale hash."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL 61: " + e.Message); return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Throws(Func<Task> action, string message) { try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; } throw new InvalidOperationException(message); }
}
