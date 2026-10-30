// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.CrmUpdateAssistant;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("13-CrmUpdateAssistant", "CRM Update Assistant", "Use the tools to derive facts from the supplied business records. Report missing records and limitations honestly.",
        new ToolSpec[] {
            new("find_contact", "Inspect source records.", SampleJson.Schema("{\"contactId\":{\"type\":\"string\"}}", "contactId"), (a,t) => FirstAsync(c,a,t)),
            new("preview_update", "Compute the business result.", SampleJson.Schema("{\"contactId\":{\"type\":\"string\"},\"field\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}", "contactId", "field", "value"), (a,t) => Task.FromResult<JsonNode?>(Second(c,a))),
            new ToolSpec("save_update", "Save an approved CRM record copy in the sample workspace.", SampleJson.Schema("{\"contactId\":{\"type\":\"string\"},\"field\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}", "contactId", "field", "value"), async (a,t) => {
    var preview = J.Obj(Second(c,a));
    if (preview["valid"]?.GetValue<bool>() != true) return J.Json(new { saved = false, reason = J.Str(preview,"reason") });
    var contact = Contact(c,J.Str(a,"contactId"))!.DeepClone().AsObject();
    contact[J.Str(a,"field")] = J.Str(a,"value");
    await c.WriteJsonAsync("crm-updates/" + J.Str(a,"contactId") + ".json", contact,t);
    return J.Json(new { saved = true, path = "crm-updates/" + J.Str(a,"contactId") + ".json" });
}, RequiresApproval: true),
        },
        new PlannedCall[] { new("find_contact", SampleJson.Object("""{"contactId":"C-17"}""")), new("preview_update", SampleJson.Object("""{"contactId":"C-17","field":"stage","value":"qualified"}""")) });

    static Task<JsonNode?> FirstAsync(SampleContext c, JsonObject a, CancellationToken t) => Task.FromResult<JsonNode?>(First(c,a));
static JsonObject? Contact(SampleContext c, string id)
{
var original = J.Items(c.Input["contacts"]).FirstOrDefault(x => J.Str(x,"id") == id)?.AsObject();
if (original is null) return null;
var path = c.ResolvePath("crm-updates/" + id + ".json");
if (!File.Exists(path)) return original;
var saved = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Invalid saved contact");
if (J.Str(saved,"id") != id) throw new InvalidDataException("Saved contact ID does not match");
return saved;
}
static JsonNode? First(SampleContext c, JsonObject a)
{
var contact = Contact(c,J.Str(a,"contactId"));
return contact is null ? J.Json(new { found = false }) : J.Json(new { found = true, contact = contact.DeepClone() });
}
    static JsonNode? Second(SampleContext c, JsonObject a)
    {
var contact = Contact(c,J.Str(a,"contactId"));
if (contact is null) return J.Json(new { valid = false, reason = "Contact not found" });
var field = J.Str(a,"field"); var value = J.Str(a,"value");
var allowed = new [] { "email", "stage", "owner" }.Contains(field);
var valid = allowed && !string.IsNullOrWhiteSpace(value) && (field != "email" || value.Contains('@')) && (field != "stage" || new [] { "lead", "qualified", "customer" }.Contains(value));
return J.Json(new { valid, contactId = J.Str(contact,"id"), field, before = allowed ? J.Str(contact,field) : "", after = value, reason = valid ? "" : "Field or value not allowed" });
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
        var c = new SampleContext(Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-13"), JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject());
        var w = Workflow.Create(c);
        var first = await w.Tools[0].Execute(w.DemoCalls[0].Arguments, CancellationToken.None);
        var second = await w.Tools[1].Execute(w.DemoCalls[1].Arguments, CancellationToken.None);
        if (first is null || second is null) throw new Exception("Tool returned null.");
        if (second?["valid"]?.GetValue<bool>() != true || J.Str(second,"before") != "lead") throw new Exception("CRM preview failed.");
        var workspace = Path.Combine(Path.GetTempPath(), "pisharp-sample-selftest-13-" + Guid.NewGuid().ToString("N"));
        try
        {
            var savedContext = new SampleContext(workspace, c.Input.DeepClone().AsObject(), approved: true);
            var savedWorkflow = Workflow.Create(savedContext);
            var stage = SampleJson.Object("""{"contactId":"C-17","field":"stage","value":"qualified"}""");
            var owner = SampleJson.Object("""{"contactId":"C-17","field":"owner","value":"New Owner"}""");
            await savedWorkflow.Tools[2].Execute(stage, CancellationToken.None);
            await savedWorkflow.Tools[2].Execute(owner, CancellationToken.None);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(savedContext.ResolvePath("crm-updates/C-17.json")));
            var found = await savedWorkflow.Tools[0].Execute(savedWorkflow.DemoCalls[0].Arguments, CancellationToken.None);
            var preview = await savedWorkflow.Tools[1].Execute(stage, CancellationToken.None);
            if (J.Str(saved,"stage") != "qualified" || J.Str(saved,"owner") != "New Owner" || J.Str(found?["contact"],"stage") != "qualified" || J.Str(found?["contact"],"owner") != "New Owner" || J.Str(preview,"before") != "qualified")
                throw new Exception("Saved updates were lost or subsequent reads ignored them.");
            var reopened = Workflow.Create(new SampleContext(workspace, c.Input.DeepClone().AsObject(), approved: true));
            var persisted = await reopened.Tools[0].Execute(reopened.DemoCalls[0].Arguments, CancellationToken.None);
            if (J.Str(persisted?["contact"],"stage") != "qualified" || J.Str(persisted?["contact"],"owner") != "New Owner") throw new Exception("Saved contact did not survive reopening.");
        }
        finally { Directory.Delete(workspace, recursive: true); }
        Console.WriteLine("PASS 13-CrmUpdateAssistant");
        return 0;
    }
}
