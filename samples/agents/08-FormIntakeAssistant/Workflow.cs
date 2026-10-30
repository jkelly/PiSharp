// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.FormIntakeAssistant;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("form-intake-assistant", "Form intake assistant",
        "Validate required fields before preparing a routing packet. Describe the packet as proposed; no external submission occurs.",
        [
            new("validate_form", "Check required fields for a local form.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Validate(context, a))),
            new("prepare_routing", "Build a routing packet for a valid form.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Route(context, a)))
        ],
        [new("validate_form", SampleJson.Object("{\"id\":\"form-801\"}")), new("prepare_routing", SampleJson.Object("{\"id\":\"form-801\"}"))]);
    static JsonObject Form(SampleContext c, JsonObject a) => c.Input["forms"]?.AsArray()
        .FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))?.AsObject()
        ?? throw new ArgumentException("Unknown form");
    static JsonObject Validate(SampleContext c, JsonObject a)
    {
        var form = Form(c, a);
        var missing = new JsonArray();
        foreach (var field in new[] { "requester", "system", "reason", "manager" })
            if (string.IsNullOrWhiteSpace(form.Text(field))) missing.Add(field);
        return new JsonObject { ["id"] = form["id"]?.DeepClone(), ["valid"] = missing.Count == 0, ["missing"] = missing };
    }
    static JsonObject Route(SampleContext c, JsonObject a)
    {
        var form = Form(c, a); var validation = Validate(c, a);
        if (validation["valid"]?.GetValue<bool>() != true) return new JsonObject { ["id"] = form["id"]?.DeepClone(), ["status"] = "needs-information", ["missing"] = validation["missing"]?.DeepClone() };
        var team = c.Input["routing"]?[form.Text("type")]?.GetValue<string>()
            ?? throw new InvalidOperationException("No route configured for form type");
        return new JsonObject { ["id"] = form["id"]?.DeepClone(), ["status"] = "prepared", ["destination"] = team,
            ["requester"] = form["requester"]?.DeepClone(), ["system"] = form["system"]?.DeepClone(),
            ["reason"] = form["reason"]?.DeepClone(), ["manager"] = form["manager"]?.DeepClone() };
    }
}
