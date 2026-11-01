// PiSharp-only (no upstream Pi source): timezone-aware local calendar coordination.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Samples.CalendarSchedulingCoordinator;

public static class Workflow
{
    public static WorkflowSpec Create(SampleContext c)
    {
        var schema = SampleJson.Schema("{\"requestId\":{\"type\":\"string\"}}", "requestId");
        var id = c.Input.Text("requestId");
        var calls = new List<PlannedCall> { new("inspect_calendars", Args(id)), new("find_shared_slots", Args(id)), new("save_proposal", Args(id)) };
        if (c.Approved) calls.Add(new("book_local_meeting", Args(id)));
        return new("33", "Calendar Scheduling Coordinator",
            "Inspect calendars, find shared slots, then save_proposal for the requested requestId. All times are computed from local fixture calendars. With host approval, book_local_meeting commits the earliest slot to a workspace-only ledger. No external invitations are sent.",
            [new("inspect_calendars", "Inspect the named request and participants, validating timezone intervals.", schema, (a, _) => Task.FromResult<JsonNode?>(Inspect(c, a.Text("requestId")))),
             new("find_shared_slots", "Compute up to five shared free slots in UTC and participant local time.", schema, (a, _) => Task.FromResult<JsonNode?>(Plan(c, a.Text("requestId")))),
             new("save_proposal", "Persist the calculated meeting proposal without booking.", schema, async (a, ct) => { var p = Plan(c, a.Text("requestId")); var path = $"proposal-{Id(a.Text("requestId"))}.json"; await c.WriteJsonAsync(path, p, ct); return new JsonObject { ["artifact"] = path, ["proposal"] = p }; }),
             new("book_local_meeting", "Book the earliest shared slot in the local ledger; approval and matching replay required.", schema, async (a, ct) => await Book(c, a.Text("requestId"), ct), RequiresApproval: true)], calls);
    }

    internal static JsonObject Args(string id) => new() { ["requestId"] = id };
    internal static string Id(string id) => id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') ? id : throw new ArgumentException("Invalid request ID.");
    static IEnumerable<JsonObject> Objects(JsonArray rows) => rows.Select(n => n as JsonObject ?? throw new ArgumentException("Every record must be a JSON object."));
    static JsonObject One(JsonArray rows, string id) => Objects(rows).Where(x => x.Text("id") == id).ToArray() is [var row] ? row : throw new ArgumentException($"Missing or ambiguous ID: {id}");
    static JsonArray Rows(JsonObject data, string key) => data[key] as JsonArray ?? throw new ArgumentException($"Missing array: {key}");
    static JsonObject Request(SampleContext c, string id) => One(Rows(c.Input, "requests"), Id(id));
    static JsonObject Person(SampleContext c, string id) => One(Rows(c.Input, "people"), id);
    static DateTimeOffset Utc(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    static DateTimeOffset Local(JsonObject person, string value)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(person.Text("timeZone"));
        var time = DateTime.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (zone.IsInvalidTime(time) || zone.IsAmbiguousTime(time)) throw new ArgumentException("Ambiguous or invalid local time; supply an unambiguous calendar interval.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(time, zone), TimeSpan.Zero);
    }
    static (DateTimeOffset Start, DateTimeOffset End) Interval(JsonObject p, JsonObject row)
    {
        var start = Local(p, row.Text("start")); var end = Local(p, row.Text("end"));
        if (end <= start) throw new ArgumentException("Calendar interval end must follow start.");
        return (start, end);
    }
    static string[] Participants(JsonObject r)
    {
        var ids = Rows(r, "participants").Select(n => n!.GetValue<string>()).ToArray();
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException("Participants must be nonempty and unique.");
        return ids;
    }
    static string Fingerprint(SampleContext c, JsonObject r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new JsonObject { ["request"] = r.DeepClone(), ["people"] = new JsonArray(Participants(r).Select(id => (JsonNode?)Person(c, id).DeepClone()).ToArray()) }.ToJsonString())));
    internal static JsonObject Inspect(SampleContext c, string id)
    {
        var r = Request(c, id); var people = new JsonArray();
        foreach (var personId in Participants(r))
        {
            var p = Person(c, personId);
            foreach (var range in Objects(Rows(p, "available")).Concat(Objects(Rows(p, "busy")))) _ = Interval(p, range);
            people.Add(p.DeepClone());
        }
        return new() { ["request"] = r.DeepClone(), ["calendars"] = people };
    }
    internal static JsonObject Plan(SampleContext c, string id)
    {
        _ = Inspect(c, id); var r = Request(c, id); var participants = Participants(r);
        var duration = r["durationMinutes"]!.GetValue<int>();
        if (duration is < 15 or > 480) throw new ArgumentException("Duration must be 15–480 minutes.");
        var start = Utc(r.Text("windowStartUtc")); var end = Utc(r.Text("windowEndUtc"));
        if (end <= start || end - start > TimeSpan.FromDays(7)) throw new ArgumentException("Window must be positive and no longer than seven days.");
        var bookings = Directory.EnumerateFiles(c.Workspace, "booking-*.json").Select(path => JsonNode.Parse(File.ReadAllText(c.ResolvePath(Path.GetFileName(path))))!.AsObject()).Where(b => b.Text("requestId") != id).ToArray();
        var slots = new JsonArray();
        for (var t = start; t.AddMinutes(duration) <= end && slots.Count < 5; t = t.AddMinutes(15))
        {
            var stop = t.AddMinutes(duration);
            bool available = participants.All(personId => {
                var p = Person(c, personId);
                return Objects(Rows(p, "available")).Select(x => Interval(p, x)).Any(x => x.Start <= t && x.End >= stop)
                    && !Objects(Rows(p, "busy")).Select(x => Interval(p, x)).Any(x => x.Start < stop && x.End > t);
            });
            if (!available || bookings.Any(b => Rows(b, "participants").Any(n => participants.Contains(n!.GetValue<string>(), StringComparer.Ordinal)) && Utc(b.Text("startUtc")) < stop && Utc(b.Text("endUtc")) > t)) continue;
            var local = new JsonObject();
            foreach (var personId in participants) local[personId] = TimeZoneInfo.ConvertTime(t, TimeZoneInfo.FindSystemTimeZoneById(Person(c, personId).Text("timeZone"))).ToString("O");
            slots.Add(new JsonObject { ["startUtc"] = t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["endUtc"] = stop.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["localStarts"] = local });
        }
        return new() { ["requestId"] = id, ["status"] = slots.Count > 0 ? "proposed" : "no-shared-slot", ["durationMinutes"] = duration, ["slots"] = slots, ["externalInvitationsSent"] = false };
    }
    internal static async Task<JsonObject> Book(SampleContext c, string id, CancellationToken ct = default)
    {
        if (!c.Approved) throw new InvalidOperationException("Approval required for local booking.");
        var r = Request(c, id); var hash = Fingerprint(c, r); var path = $"booking-{Id(id)}.json";
        if (File.Exists(c.ResolvePath(path)))
        {
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(c.ResolvePath(path), ct))!.AsObject();
            if (saved.Text("fingerprint") != hash) throw new InvalidOperationException("Booking ID already exists with different input.");
            return saved;
        }
        var slots = Plan(c, id)["slots"]!.AsArray();
        if (slots.Count == 0) throw new InvalidOperationException("No shared slot is available.");
        var booking = slots[0]!.DeepClone().AsObject(); booking["requestId"] = id; booking["fingerprint"] = hash; booking["participants"] = r["participants"]!.DeepClone(); booking["scope"] = "workspace-only";
        await c.WriteJsonAsync(path, booking, ct); return booking;
    }
}
