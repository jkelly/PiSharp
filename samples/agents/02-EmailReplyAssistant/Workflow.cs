using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.EmailReplyAssistant;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("email-reply", "Email reply assistant",
        "Read the thread and verified facts before drafting. Include only facts in the tool result. Present a draft; do not claim to send mail.",
        [
            new("read_thread", "Read a thread and its fact record.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Read(context, a))),
            new("draft_reply", "Draft a reply from a thread fact record.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Draft(context, a)))
        ],
        [new("read_thread", SampleJson.Object("{\"id\":\"t-201\"}")), new("draft_reply", SampleJson.Object("{\"id\":\"t-201\"}"))]);
    static JsonObject Thread(SampleContext c, JsonObject a) => c.Input["threads"]?.AsArray()
        .FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))?.AsObject()
        ?? throw new ArgumentException("Unknown thread");
    static JsonObject Read(SampleContext c, JsonObject a)
    {
        var t = Thread(c, a);
        return new JsonObject { ["id"] = t["id"]?.DeepClone(), ["subject"] = t["subject"]?.DeepClone(),
            ["customer"] = t["customer"]?.DeepClone(), ["messages"] = t["messages"]?.DeepClone(), ["facts"] = t["facts"]?.DeepClone() };
    }
    static JsonObject Draft(SampleContext c, JsonObject a)
    {
        var t = Thread(c, a);
        var facts = t["facts"]?.AsObject() ?? throw new InvalidOperationException("Thread facts missing");
        var invoice = facts.Text("invoice");
        var included = facts["training_included"]?.GetValue<bool>() ?? false;
        var body = $"Hi {t.Text("customer")},\n\n{invoice} {(included ? "includes" : "does not include")} the training session. The recorded total is {facts.Text("total")}.\n\nPlease let us know if you need a line-item breakdown.";
        return new JsonObject { ["thread_id"] = t["id"]?.DeepClone(), ["subject"] = $"Re: {t.Text("subject")}",
            ["body"] = body, ["status"] = "draft", ["evidence"] = $"thread:{t.Text("id")}:facts" };
    }
}
