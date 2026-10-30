using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.HelpdeskResolution;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("helpdesk-resolution", "Helpdesk resolution",
        "Inspect the ticket, match the most relevant knowledge article and present steps with an escalation trigger. Do not claim the ticket was resolved.",
        [
            new("inspect_ticket", "Read product, symptoms and description.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Ticket(context, a).DeepClone())),
            new("suggest_resolution", "Rank KB articles for a ticket and return supported steps.", SampleJson.Schema("{\"id\":{\"type\":\"string\"}}", "id"),
                (a, _) => Task.FromResult<JsonNode?>(Suggest(context, a)))
        ],
        [new("inspect_ticket", SampleJson.Object("{\"id\":\"hd-901\"}")), new("suggest_resolution", SampleJson.Object("{\"id\":\"hd-901\"}"))]);
    static JsonObject Ticket(SampleContext c, JsonObject a) => c.Input["tickets"]?.AsArray()
        .FirstOrDefault(x => x?["id"]?.GetValue<string>() == a.Text("id"))?.AsObject()
        ?? throw new ArgumentException("Unknown ticket");
    static JsonObject Suggest(SampleContext c, JsonObject a)
    {
        var ticket = Ticket(c, a);
        var description = ticket.Text("description");
        var ranked = (c.Input["articles"]?.AsArray() ?? throw new InvalidOperationException("articles missing"))
            .Select(x => x!.AsObject()).Where(x => x.Text("product") == ticket.Text("product"))
            .Select(x => (article: x, score: x["keywords"]?.AsArray().Count(k => description.Contains(k?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase)) ?? 0))
            .OrderByDescending(x => x.score).ToArray();
        if (ranked.Length == 0 || ranked[0].score == 0)
            return new JsonObject { ["ticket_id"] = ticket["id"]?.DeepClone(), ["status"] = "no-matching-article", ["recommendation"] = "escalate for investigation" };
        var article = ranked[0].article;
        return new JsonObject { ["ticket_id"] = ticket["id"]?.DeepClone(), ["status"] = "suggested",
            ["article_id"] = article["id"]?.DeepClone(), ["match_count"] = ranked[0].score,
            ["steps"] = article["steps"]?.DeepClone(), ["escalate_if"] = article["escalate_if"]?.DeepClone() };
    }
}
