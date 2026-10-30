using System.Text.Json.Nodes;
using PiSharp.Samples;

namespace PiSharp.Samples.InboxTriage;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new(
        "inbox-triage", "Inbox triage",
        "Use list_messages then assess_message. Cite message IDs, explain urgency from evidence, and never claim messages were moved or replied to.",
        [
            new("list_messages", "List inbox messages, optionally filtering to unread.", SampleJson.Schema("{\"unread_only\":{\"type\":\"boolean\"}}"),
                (args, _) => Task.FromResult<JsonNode?>(List(context, args))),
            new("assess_message", "Assess one message against sender, due date and request signals.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (args, _) => Task.FromResult<JsonNode?>(Assess(context, args)))
        ],
        [new("list_messages", SampleJson.Object("{\"unread_only\":true}")), new("assess_message", SampleJson.Object("{\"id\":\"m-101\"}"))]);

    static JsonObject List(SampleContext context, JsonObject args)
    {
        var unread = args["unread_only"]?.GetValue<bool>() ?? false;
        var rows = (context.Input["messages"]?.AsArray() ?? throw new InvalidOperationException("messages missing"))
            .Where(x => !unread || x?["unread"]?.GetValue<bool>() == true)
            .Select(x => (JsonNode?)new JsonObject { ["id"] = x?["id"]?.DeepClone(), ["from"] = x?["from"]?.DeepClone(), ["subject"] = x?["subject"]?.DeepClone(), ["due"] = x?["due"]?.DeepClone() }).ToArray();
        return new JsonObject { ["count"] = rows.Length, ["messages"] = new JsonArray(rows) };
    }

    static JsonObject Assess(SampleContext context, JsonObject args)
    {
        var id = args.Text("id");
        var row = context.Input["messages"]?.AsArray().FirstOrDefault(x => x?["id"]?.GetValue<string>() == id)
            ?? throw new ArgumentException($"Unknown message: {id}");
        var body = row["body"]?.GetValue<string>() ?? "";
        var due = row["due"]?.GetValue<string>();
        var vip = row["vip"]?.GetValue<bool>() ?? false;
        var actionable = body.Contains("please", StringComparison.OrdinalIgnoreCase) || body.Contains("need", StringComparison.OrdinalIgnoreCase);
        var reasons = new JsonArray();
        if (vip) reasons.Add("priority sender");
        if (!string.IsNullOrWhiteSpace(due)) reasons.Add($"dated request: {due}");
        if (actionable) reasons.Add("explicit request");
        return new JsonObject { ["id"] = id, ["priority"] = vip && due is not null ? "high" : actionable ? "normal" : "low", ["action_required"] = actionable, ["reasons"] = reasons, ["source"] = $"message:{id}" };
    }
}
