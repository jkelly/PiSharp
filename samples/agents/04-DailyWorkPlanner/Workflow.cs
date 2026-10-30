using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.DailyWorkPlanner;
public static class Workflow
{
    public static WorkflowSpec Create(SampleContext context) => new("daily-work-planner", "Daily work planner",
        "Read commitments and build a plan within capacity. Explain anything deferred; do not claim calendar changes.",
        [
            new("list_commitments", "List the dated work items and daily capacity.", SampleJson.Schema(),
                (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["date"] = context.Input["date"]?.DeepClone(),
                    ["capacity_minutes"] = context.Input["capacity_minutes"]?.DeepClone(), ["commitments"] = context.Input["commitments"]?.DeepClone() })),
            new("build_plan", "Order commitments by priority and due date within available minutes.", SampleJson.Schema(),
                (_, _) => Task.FromResult<JsonNode?>(Plan(context)))
        ],
        [new("list_commitments", new JsonObject()), new("build_plan", new JsonObject())]);
    static JsonObject Plan(SampleContext c)
    {
        var capacity = c.Input["capacity_minutes"]?.GetValue<int>() ?? throw new InvalidOperationException("capacity_minutes missing");
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var work = c.Input["commitments"]?.AsArray() ?? throw new InvalidOperationException("commitments missing");
        var ordered = work.OrderByDescending(x => x?["priority"]?.GetValue<int>() ?? 0)
            .ThenBy(x => x?["due"]?.GetValue<string>(), StringComparer.Ordinal).ToArray();
        var scheduled = new JsonArray(); var deferred = new JsonArray(); var used = 0;
        foreach (var item in ordered)
        {
            var minutes = item?["minutes"]?.GetValue<int>() ?? 0;
            if (minutes < 0) throw new ArgumentException("Negative work estimate");
            if (minutes <= capacity - used) { scheduled.Add(item?.DeepClone()); used += minutes; }
            else deferred.Add(new JsonObject { ["id"] = item?["id"]?.DeepClone(), ["reason"] = "insufficient remaining capacity" });
        }
        return new JsonObject { ["date"] = c.Input["date"]?.DeepClone(), ["scheduled"] = scheduled,
            ["deferred"] = deferred, ["used_minutes"] = used, ["remaining_minutes"] = capacity - used };
    }
}
