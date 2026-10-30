// PiSharp-only (no upstream Pi source): agent sample implementation and authored offline verification.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.MeetingFollowThrough;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("meeting-follow-through", "Meeting follow through",
        "Read meeting actions, then prepare a follow-up draft. Cite the meeting ID and do not claim a message was delivered.",
        [
            new("extract_actions", "Return structured owner, task and due date from meeting notes.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Actions(context, a))),
            new("prepare_followup", "Prepare an attendee follow-up with action ownership.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Followup(context, a)))
        ],
        [new("extract_actions", SampleJson.Object("{\"id\":\"mtg-301\"}")), new("prepare_followup", SampleJson.Object("{\"id\":\"mtg-301\"}"))]);
    static JsonObject Meeting(SampleContext c, JsonObject a) => c.Input["meetings"]?.AsArray()
        .FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))?.AsObject()
        ?? throw new ArgumentException("Unknown meeting");
    static JsonObject Actions(SampleContext c, JsonObject a)
    {
        var m = Meeting(c, a);
        return new JsonObject { ["meeting_id"] = m["id"]?.DeepClone(), ["actions"] = m["actions"]?.DeepClone(),
            ["source"] = $"meeting:{m.Text("id")}" };
    }
    static JsonObject Followup(SampleContext c, JsonObject a)
    {
        var m = Meeting(c, a);
        var lines = m["actions"]?.AsArray().Select(x => $"- {x?["owner"]}: {x?["task"]} (due {x?["due"]})") ?? [];
        return new JsonObject { ["meeting_id"] = m["id"]?.DeepClone(), ["recipients"] = m["attendees"]?.DeepClone(),
            ["subject"] = $"Follow-up: {m.Text("title")}", ["body"] = string.Join("\n", lines), ["status"] = "draft" };
    }
}
