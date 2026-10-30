using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.InvoiceReconciliation;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("17-InvoiceReconciliation", "Invoice Reconciliation", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("match_invoice", "Inspect source records.", SampleJson.Schema("{\"invoiceId\":{\"type\":\"string\"}}", "invoiceId"), (a,t) => FirstAsync(c,a,t)),
            new("calculate_payable", "Compute the business result.", SampleJson.Schema("{\"invoiceId\":{\"type\":\"string\"}}", "invoiceId"), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),

        },
        new PlannedCall[] { new("match_invoice", SampleJson.Object("""{"invoiceId":"INV-9"}""")), new("calculate_payable", SampleJson.Object("""{"invoiceId":"INV-9"}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonNode? First(SampleContext c, JsonObject a)
{
var invoice = J.Items(c.Input["invoices"]).FirstOrDefault(x => J.Str(x,"id") == J.Str(a,"invoiceId"));
if (invoice is null) return J.Json(new { found = false });
var po = J.Items(c.Input["purchase_orders"]).FirstOrDefault(x => J.Str(x,"id") == J.Str(invoice,"po_id"));
if (po is null) return J.Json(new { found = true, matched = false, reason = "Purchase order missing" });
var rows = J.Items(invoice["lines"]).Select(line => { var sku = J.Str(line,"sku"); var ordered = J.Items(po["lines"]).FirstOrDefault(x => J.Str(x,"sku") == sku); var received = J.Items(c.Input["receipts"]).Where(x => J.Str(x,"po_id") == J.Str(po,"id") && J.Str(x,"sku") == sku).Sum(x => J.Int(x,"quantity")); return new { sku, invoiced = J.Int(line,"quantity"), ordered = J.Int(ordered,"quantity"), received, invoiceUnitPrice = J.Num(line,"unit_price"), orderUnitPrice = J.Num(ordered,"unit_price") }; }).ToArray();
var matched = J.Str(invoice,"vendor") == J.Str(po,"vendor") && rows.All(x => x.invoiced <= x.ordered && x.invoiceUnitPrice == x.orderUnitPrice);
var result = J.Json(new { found = true, matched, vendorMatches = J.Str(invoice,"vendor") == J.Str(po,"vendor"), lines = rows });
c.State["invoice_match"] = result?.DeepClone();
return result;
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var match = c.State["invoice_match"] ?? First(c,a);
if (match?["found"]?.GetValue<bool>() != true || match?["matched"]?.GetValue<bool>() != true) return J.Json(new { ready = false, reason = "Invoice or purchase order mismatch" });
var lines = J.Items(match["lines"]);
var invoiced = lines.Sum(x => J.Int(x,"invoiced") * J.Num(x,"invoiceUnitPrice"));
var payable = lines.Sum(x => Math.Min(J.Int(x,"invoiced"),J.Int(x,"received")) * J.Num(x,"invoiceUnitPrice"));
return J.Json(new { ready = payable == invoiced, invoiced, payable, held = invoiced-payable, shortageSkus = lines.Where(x => J.Int(x,"received") < J.Int(x,"invoiced")).Select(x => J.Str(x,"sku")).ToArray() });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-17"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (J.Num(second,"payable") != 255m || J.Num(second,"held") != 15m || second?["ready"]?.GetValue<bool>() != false) throw new Exception("Three-way match failed.");
        Console.WriteLine("PASS 17-InvoiceReconciliation");
        return 0;
    }
}
