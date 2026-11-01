// PiSharp-only (no upstream Pi source): renewal economics and a reviewed local action plan.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.SubscriptionRenewalAssistant;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c) => new("38-SubscriptionRenewalAssistant", "Subscription Renewal Assistant",
        "Inspect each requested subscription, compare renewal economics using fixture asOf and integer cents, then save its plan. Saving records host review in a local artifact only; never cancel or contact a vendor.",
        [new("inspect_subscription", "Read subscription terms, usage and the renewal/cancellation window.", IdSchema(), (a,t) => Task.FromResult<JsonNode?>(Inspect(c,a))),
         new("compare_renewal", "Compute seat utilization, annual costs and an actionable renewal recommendation.", IdSchema(), (a,t) => Task.FromResult<JsonNode?>(Compare(c,a))),
         new("save_renewal_plan", "Save a host-reviewed workspace plan; no external cancellation or renewal.", IdSchema(), (a,t) => SaveAsync(c,a,t), RequiresApproval: true)],
        [new("inspect_subscription", Args("SUB-CRM")), new("compare_renewal", Args("SUB-CRM")), new("save_renewal_plan", Args("SUB-CRM"))]);

    static JsonObject IdSchema() => SampleJson.Schema("""{"subscriptionId":{"type":"string"}}""", "subscriptionId");
    static JsonObject Args(string id) => new() { ["subscriptionId"] = id };
    static JsonObject? Subscription(SampleContext c, JsonObject a) => Data.Find(c.Input["subscriptions"], Data.Id(a,"subscriptionId"));
    static JsonNode Inspect(SampleContext c, JsonObject a)
    {
        var id = Data.Id(a,"subscriptionId");
        var s = Subscription(c,a);
        if (s is null) return Data.Missing(id);
        var now = Data.Time(c.Input,"asOf");
        var renewal = Data.Time(s,"renewalAt");
        var deadline = renewal.AddDays(-Data.Number(s,"noticeDays"));
        return Data.Json(new { found = true, subscriptionId = id, subscription = s.DeepClone(),
            daysUntilRenewal = (renewal - now).TotalDays, cancellationDeadline = deadline,
            noticeWindowOpen = now <= deadline, upcoming = renewal >= now && renewal <= now.AddDays(60) });
    }
    static JsonNode Compare(SampleContext c, JsonObject a)
    {
        var s = Subscription(c,a);
        if (s is null) return Data.Missing(Data.Id(a,"subscriptionId"));
        var now = Data.Time(c.Input,"asOf");
        var renewal = Data.Time(s,"renewalAt");
        var seats = Data.Number(s,"licensedSeats",1);
        var active = Data.Number(s,"activeSeats");
        if (active > seats) throw new InvalidOperationException("Active seats exceed licensed seats; reconcile usage first.");
        var minSeats = Data.Number(s,"minimumSeats",1);
        if (minSeats > seats) throw new InvalidOperationException("Minimum seats exceed licensed seats.");
        var unit = Data.Number(s,"renewalAnnualSeatCents",1);
        var current = Data.Number(s,"currentAnnualCents",1);
        var rightSize = Math.Max(active,minSeats);
        var full = checked(seats * unit);
        var proposed = checked(rightSize * unit);
        var canChange = now <= renewal.AddDays(-Data.Number(s,"noticeDays"));
        var upcoming = renewal >= now && renewal <= now.AddDays(60);
        var recommendation = !upcoming ? "review_outside_upcoming_window" : !canChange ? "negotiate_late_change_with_vendor" : rightSize < seats ? "request_seat_reduction" : "review_price_and_renew";
        return Data.Json(new { found = true, subscriptionId = Data.Text(s,"id"), asOf = now, renewalAt = renewal,
            licensedSeats = seats, activeSeats = active, utilizationPercent = 100m * active / seats,
            proposedSeats = rightSize, currentAnnualCents = current, quotedAnnualCents = full,
            proposedAnnualCents = proposed, quotedIncreaseCents = full-current, savingsAgainstQuoteCents = full-proposed,
            canChangeWithinNotice = canChange, upcoming, recommendation,
            evidenceIds = new[] { Data.Text(s,"contractSourceId"), Data.Text(s,"usageSourceId") },
            reviewNote = "Verify contract terms and usage with the owner before any vendor action." });
    }
    static async Task<JsonNode?> SaveAsync(SampleContext c, JsonObject a, CancellationToken token)
    {
        Data.Approved(c);
        var plan = Compare(c,a);
        if (plan["found"]!.GetValue<bool>() != true) return plan;
        plan["hostReviewed"] = true;
        plan["externalActionTaken"] = false;
        var path = "renewals/" + Data.Id(a,"subscriptionId") + ".json";
        await c.WriteJsonAsync(path,plan,token);
        return Data.Json(new { saved = true, path, plan });
    }
}
