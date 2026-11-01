// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.ReceiptToExpense;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        if (c.ImagePath is not null && !c.Live)
            throw new ArgumentException("Image extraction requires --live and a vision-capable model.");
        var imageMode = c.ImagePath is not null;
        var tools = new ToolSpec[] {
            imageMode
                ? new("capture_receipt", "Submit merchant, date, totalCents, and line charges with integer amountCents read from the attached receipt image. Use cents (17.82 becomes 1782), not fractional dollar numbers. This tool validates the values and stores them for classification.",
                    SampleJson.Schema("""{"merchant":{"type":"string"},"date":{"type":"string"},"totalCents":{"type":"integer","minimum":1,"maximum":9000000000000},"charges":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"amountCents":{"type":"integer","minimum":0,"maximum":9000000000000}},"required":["label","amountCents"],"additionalProperties":false}}}""", "merchant", "date", "totalCents", "charges"),
                    (a,t) => Task.FromResult<JsonNode?>(Capture(c,a)))
                : new("parse_receipt_text", "Parse merchant, date, charges, and total from supplied OCR text.", SampleJson.Schema(), (a,t) => Task.FromResult<JsonNode?>(First(c,a))),
            new("classify_expense", "Classify the validated receipt and check the meal limit.", SampleJson.Schema(), (a,t) => Task.FromResult<JsonNode?>(Second(c,a)))
        };
        var calls = new PlannedCall[] { new(tools[0].Name, SampleJson.Object("""{}""")), new("classify_expense", SampleJson.Object("""{}""")) };
        var instructions = imageMode
            ? "Read the attached receipt image. Call capture_receipt with the merchant, ISO date, totalCents, and every charge including tax using integer amountCents, then call classify_expense. Monetary tool arguments are integer cents: 17.82 dollars is 1782 cents. If text is unreadable, report that limitation; do not invent values."
            : "Parse the supplied OCR-transcribed text, then classify the expense. OCR text is fixture input, not image extraction.";
        return new("16-ReceiptToExpense", "Receipt to Expense", instructions, tools, calls);
    }

    static JsonNode? Capture(SampleContext c, JsonObject a)
    {
        c.State.Remove("receipt");
        var merchant = J.Str(a,"merchant").Trim();
        var dateText = J.Str(a,"date");
        var totalCents = J.Num(a,"totalCents");
        var total = totalCents / 100m;
        var charges = J.Items(a["charges"]);
        if (merchant.Length == 0 || !DateOnly.TryParseExact(dateText,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _) || totalCents <= 0 || totalCents != decimal.Truncate(totalCents) || totalCents > 9000000000000m || charges.Count == 0)
            return J.Json(new { parsed = false, reason = "Merchant, ISO date, positive total, and charges are required" });
        decimal computed = 0;
        foreach (var charge in charges)
        {
            if (string.IsNullOrWhiteSpace(J.Str(charge,"label")) || !decimal.TryParse(J.Str(charge,"amountCents"),NumberStyles.Number,CultureInfo.InvariantCulture,out var cents) || cents < 0 || cents != decimal.Truncate(cents) || cents > 9000000000000m)
                return J.Json(new { parsed = false, reason = "Each charge needs a label and nonnegative amount" });
            computed += cents / 100m;
        }
        if (Math.Abs(computed-total) > 0.01m)
            return J.Json(new { parsed = false, reason = "Charges do not sum to total", total, computedTotal = computed });
        var result = J.Json(new { parsed = true, merchant, date = dateText, total, computedTotal = computed, balanced = true, charges = charges.DeepClone() });
        c.State["receipt"] = result?.DeepClone();
        return result;
    }

static JsonNode? First(SampleContext c, JsonObject a)
{
var text = J.Str(c.Input,"receipt_text");
var lines = text.Split('\n',StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (lines.Length < 3) return J.Json(new { parsed = false, reason = "OCR text is incomplete" });
var dated = lines.FirstOrDefault(x => DateOnly.TryParse(x,out _));
var totalLine = lines.FirstOrDefault(x => x.StartsWith("Total ",StringComparison.OrdinalIgnoreCase));
if (dated is null || totalLine is null || !decimal.TryParse(totalLine[6..],NumberStyles.Number,CultureInfo.InvariantCulture,out var total)) return J.Json(new { parsed = false, reason = "Date or total missing" });
var charges = new List<object>();
decimal computed = 0m;
foreach (var line in lines.Where(x => x != lines[0] && x != dated && x != totalLine))
{
    var split = line.LastIndexOf(' ');
    if (split <= 0 || !decimal.TryParse(line[(split+1)..],NumberStyles.Number,CultureInfo.InvariantCulture,out var amount) || amount < 0)
        return J.Json(new { parsed = false, reason = "Unparseable charge line", line });
    charges.Add(new { label = line[..split], amount });
    computed += amount;
}
var result = J.Json(new { parsed = true, merchant = lines[0], date = dated, total, computedTotal = computed, balanced = Math.Abs(total-computed) <= 0.01m, charges });
c.State["receipt"] = result?.DeepClone();
return result;
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var receipt = c.State["receipt"] ?? (c.ImagePath is null ? First(c,a) : null);
if (receipt?["parsed"]?.GetValue<bool>() != true) return J.Json(new { classified = false, reason = "Receipt has not been parsed and validated" });
var merchant = J.Str(receipt,"merchant");
var category = J.Str(c.Input["merchant_categories"],merchant);
if (category == "") category = "Uncategorized";
var total = J.Num(receipt,"total");
return J.Json(new { classified = true, merchant, category, employee = J.Str(c.Input,"employee"), amount = total, withinMealLimit = category != "Meals" || total <= J.Num(c.Input,"max_meal"), balanced = receipt["balanced"]?.GetValue<bool>() == true });
    }
}

internal static class J
{
    public static JsonArray Items(JsonNode? n) => n as JsonArray ?? new JsonArray();
    public static JsonObject Obj(JsonNode? n) => n as JsonObject ?? new JsonObject();
    public static string Str(JsonNode? n,string key) => n?[key]?.ToString() ?? "";
    public static decimal Num(JsonNode? n,string key) => decimal.TryParse(Str(n,key),NumberStyles.Number,CultureInfo.InvariantCulture,out var x) ? x : 0m;
    public static int Int(JsonNode? n,string key) => int.TryParse(Str(n,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out var x) ? x : 0;
    public static JsonNode? Json(object o) => JsonSerializer.SerializeToNode(o);
}

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-16"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Num(second,"amount") != 17.82m || J.Str(second,"category") != "Meals" || second?["balanced"]?.GetValue<bool>() != true) throw new Exception("Receipt parsing or classification failed.");
        var imageContext = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-16-image"), c.Input.DeepClone().AsObject(), live: true) { ImagePath = "example.png" };
        var imageWorkflow = Workflow.Create(imageContext);
        if (imageWorkflow.Tools[0].Name != "capture_receipt") throw new Exception("Image mode did not register capture tool.");
        var valid = SampleJson.Object("""{"merchant":"Cedar Cafe","date":"2026-10-24","totalCents":1782,"charges":[{"label":"Coffee","amountCents":450},{"label":"Sandwich","amountCents":1200},{"label":"Tax","amountCents":132}]}""");
        if (SchemaValidation.Validate(imageWorkflow.Tools[0].Parameters, valid) is not null) throw new Exception("Valid integer-cent receipt did not satisfy its schema.");
        var captured = await imageWorkflow.Tools[0].Execute(valid, CancellationToken.None);
        var classified = await imageWorkflow.Tools[1].Execute(new JsonObject(), CancellationToken.None);
        if (captured?["parsed"]?.GetValue<bool>() != true || J.Str(classified,"category") != "Meals") throw new Exception("Image capture classification failed.");
        var fractional = (JsonObject)valid.DeepClone(); fractional["totalCents"] = 1782.5m;
        if (SchemaValidation.Validate(imageWorkflow.Tools[0].Parameters, fractional) is null) throw new Exception("Fractional cents were accepted.");
        var bad = SampleJson.Object("""{"merchant":"Cedar Cafe","date":"2026-10-24","totalCents":1999,"charges":[{"label":"Coffee","amountCents":450}]}""");
        var rejected = await imageWorkflow.Tools[0].Execute(bad, CancellationToken.None);
        if (rejected?["parsed"]?.GetValue<bool>() != false) throw new Exception("Unbalanced image capture was accepted.");
        var missing = SampleJson.Object("""{"date":"2026-10-24","totalCents":450,"charges":[{"label":"Coffee","amountCents":450}]}""");
        var missingResult = await imageWorkflow.Tools[0].Execute(missing, CancellationToken.None);
        var afterFailure = await imageWorkflow.Tools[1].Execute(new JsonObject(), CancellationToken.None);
        if (missingResult?["parsed"]?.GetValue<bool>() != false || afterFailure?["classified"]?.GetValue<bool>() != false)
            throw new Exception("Missing image fields were accepted or stale receipt was classified.");
        Console.WriteLine("PASS 16-ReceiptToExpense");
        return 0;
    }
}
