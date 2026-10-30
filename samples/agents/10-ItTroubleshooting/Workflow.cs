// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.ItTroubleshooting;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("it-troubleshooting", "IT troubleshooting",
        "Inspect device observations, match diagnostic rules and give safe, ordered checks. Do not claim commands were run on a device.",
        [
            new("inspect_device", "Read device metadata and recorded observations.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Device(context, a).DeepClone())),
            new("diagnose_device", "Match observations to diagnostic rules and return checks.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Diagnose(context, a)))
        ],
        [new("inspect_device", SampleJson.Object("{\"id\":\"dev-1001\"}")), new("diagnose_device", SampleJson.Object("{\"id\":\"dev-1001\"}"))]);
    static JsonObject Device(SampleContext c, JsonObject a) => c.Input["devices"]?.AsArray()
        .FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))?.AsObject()
        ?? throw new ArgumentException("Unknown device");
    static JsonObject Diagnose(SampleContext c, JsonObject a)
    {
        var device = Device(c, a);
        var observations = device["observations"]?.AsObject() ?? throw new InvalidOperationException("observations missing");
        var findings = new JsonArray();
        foreach (var node in c.Input["rules"]?.AsArray() ?? throw new InvalidOperationException("rules missing"))
        {
            var rule = node!.AsObject();
            var key = rule.Text("when");
            if (observations[key]?.GetValue<bool>() != rule["expected"]?.GetValue<bool>()) continue;
            findings.Add(new JsonObject { ["rule_id"] = rule["id"]?.DeepClone(), ["finding"] = rule["finding"]?.DeepClone(),
                ["observed"] = observations[key]?.DeepClone(), ["checks"] = rule["steps"]?.DeepClone() });
        }
        return new JsonObject { ["device_id"] = device["id"]?.DeepClone(), ["findings"] = findings, ["source"] = $"device:{device.Text("id")}:observations" };
    }
}
