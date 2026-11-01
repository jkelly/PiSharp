// PiSharp-only (no upstream Pi source): skill, shift, appointment and travel constrained local dispatch.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.FieldServiceDispatcher;

public static class Workflow
{
    // Serialize local assignment admission, including direct callbacks in one process.
    static readonly SemaphoreSlim MutationGate = new(1,1);
    public static WorkflowSpec Create(SampleContext c) => new("42-FieldServiceDispatcher", "Field Service Dispatcher",
        "Inspect the requested job and technicians, rank candidates with all required skills, shifts, existing appointments and travel time, then assign the best feasible candidate using the supplied operationId. Assignments exist only in this workspace; never contact a technician or update a live dispatch system.",
        [new("inspect_dispatch", "Read job details and technician calendars for an exact job ID.",Schema(),(a,t) => Task.FromResult<JsonNode?>(Inspect(c,a))),
         new("rank_candidates", "Check skills, shifts, overlap and travel before and after the fixed job window.",Schema(),(a,t) => Task.FromResult<JsonNode?>(Rank(c,a))),
         new("assign_technician", "Persist the top feasible technician as an idempotent local assignment.",
            SampleJson.Schema("""{"jobId":{"type":"string"},"technicianId":{"type":"string"},"operationId":{"type":"string"}}""","jobId","technicianId","operationId"),AssignAsync(c),RequiresApproval: true)],
        [new("inspect_dispatch",Args("JOB-71")),new("rank_candidates",Args("JOB-71")),new("assign_technician",new() { ["jobId"] = "JOB-71", ["technicianId"] = "TECH-ADA", ["operationId"] = "assign-job71-v1" })]);
    static JsonObject Schema() => SampleJson.Schema("""{"jobId":{"type":"string"}}""","jobId");
    static JsonObject Args(string id) => new() { ["jobId"] = id };
    static JsonObject? Job(SampleContext c, JsonObject a) => Data.Find(c.Input["jobs"],Data.Id(a,"jobId"));
    static JsonObject[] Receipts(SampleContext c)
    {
        var dir = Path.GetDirectoryName(c.ResolvePath("assignments/probe.json"))!;
        return !Directory.Exists(dir) ? [] : Directory.GetFiles(dir,"*.json").Select(p =>
            JsonNode.Parse(File.ReadAllText(c.ResolvePath("assignments/" + Path.GetFileName(p))))!.AsObject()).ToArray();
    }
    static double Travel(SampleContext c, JsonNode origin, JsonNode destination)
    {
        var distance = checked(Math.Abs(Data.Number(origin,"x") - Data.Number(destination,"x")) + Math.Abs(Data.Number(origin,"y") - Data.Number(destination,"y")));
        return checked(distance * Data.Number(c.Input,"travelMinutesPerUnit",1));
    }
    static JsonNode Inspect(SampleContext c, JsonObject a)
    {
        var job = Job(c,a);
        return job is null ? Data.Missing(Data.Id(a,"jobId")) : Data.Json(new { found = true, job = job.DeepClone(), technicians = c.Input["technicians"]!.DeepClone() });
    }
    static JsonNode Rank(SampleContext c, JsonObject a)
    {
        var job = Job(c,a);
        if (job is null) return Data.Missing(Data.Id(a,"jobId"));
        var start = Data.Time(job,"startAt");
        var end = start.AddMinutes(Data.Number(job,"durationMinutes",1));
        var asOf = Data.Time(c.Input,"asOf");
        var needed = Data.Strings(job["skills"]);
        if (needed.Length == 0) throw new InvalidOperationException("Job must specify required skills.");
        var receipts = Receipts(c);
        var alreadyAssigned = receipts.Any(r => Data.Text(r["result"]!,"jobId") == Data.Text(job,"id"));
        var techs = Data.Rows(c.Input["technicians"]).ToArray();
        if (techs.GroupBy(t => Data.Text(t,"id")).Any(g => g.Count() > 1)) throw new InvalidOperationException("Ambiguous technician ID.");
        var candidates = techs.Select(tech =>
        {
            var reasons = new List<string>();
            var techId = Data.Text(tech,"id");
            if (alreadyAssigned) reasons.Add("job_already_assigned");
            if (start < asOf) reasons.Add("job_in_past");
            if (needed.Except(Data.Strings(tech["skills"]),StringComparer.Ordinal).Any()) reasons.Add("missing_skills");
            var shiftStart = Data.Time(tech,"shiftStart");
            var shiftEnd = Data.Time(tech,"shiftEnd");
            if (shiftEnd <= shiftStart) throw new InvalidOperationException("Invalid technician shift.");
            if (start < shiftStart || end > shiftEnd) reasons.Add("outside_shift");
            var appts = Data.Rows(tech["appointments"]).Select(x => x.DeepClone().AsObject()).ToList();
            appts.AddRange(receipts.Where(r => Data.Text(r["result"]!,"technicianId") == techId).Select(r => r["result"]!.DeepClone().AsObject()));
            if (appts.Any(x => Data.Time(x,"endAt") <= Data.Time(x,"startAt"))) throw new InvalidOperationException("Invalid appointment interval.");
            if (appts.Any(x => Data.Time(x,"startAt") < end && Data.Time(x,"endAt") > start)) reasons.Add("appointment_overlap");
            // The supplied technician position is authoritative at asOf; older visits cannot move it backwards.
            var before = appts.Where(x => Data.Time(x,"endAt") > asOf && Data.Time(x,"endAt") <= start).OrderByDescending(x => Data.Time(x,"endAt")).FirstOrDefault();
            var after = appts.Where(x => Data.Time(x,"startAt") >= end).OrderBy(x => Data.Time(x,"startAt")).FirstOrDefault();
            var origin = before?["location"] ?? tech["location"]!;
            var availableAt = before is null ? asOf : Data.Time(before,"endAt");
            if (availableAt < shiftStart) availableAt = shiftStart;
            var travelBefore = Travel(c,origin,job["location"]!);
            var travelAfter = after is null ? 0 : Travel(c,job["location"]!,after["location"]!);
            if (availableAt.AddMinutes(travelBefore) > start) reasons.Add("insufficient_inbound_travel_time");
            if (after is not null && end.AddMinutes(travelAfter) > Data.Time(after,"startAt")) reasons.Add("insufficient_outbound_travel_time");
            return new { technicianId = techId, feasible = reasons.Count == 0, reasons, travelBeforeMinutes = travelBefore,
                travelAfterMinutes = travelAfter, totalTravelMinutes = travelBefore + travelAfter, arrivalAt = availableAt.AddMinutes(travelBefore) };
        }).OrderBy(x => !x.feasible).ThenBy(x => x.totalTravelMinutes).ThenBy(x => x.technicianId,StringComparer.Ordinal).ToArray();
        return Data.Json(new { found = true, jobId = Data.Text(job,"id"), startAt = start, endAt = end,
            candidates, recommendedTechnicianId = candidates.FirstOrDefault(x => x.feasible)?.technicianId });
    }
    static Func<JsonObject,CancellationToken,Task<JsonNode?>> AssignAsync(SampleContext c) => async (a,token) =>
    {
        Data.Approved(c);
        await MutationGate.WaitAsync(token);
        try
        {
            var jobId = Data.Id(a,"jobId");
            var techId = Data.Id(a,"technicianId");
            var operation = Data.Id(a,"operationId");
            var job = Job(c,a) ?? throw new InvalidOperationException("Job not found.");
            var tech = Data.Find(c.Input["technicians"],techId) ?? throw new InvalidOperationException("Technician not found.");
            var payload = new JsonObject { ["jobId"] = jobId, ["technicianId"] = techId, ["job"] = job.DeepClone(), ["technician"] = tech.DeepClone() };
            var path = "assignments/" + operation + ".json";
            var existing = await Data.ExistingAsync(c,path,payload,token);
            if (existing is not null) return existing;
            var rank = Rank(c,a);
            if (rank["recommendedTechnicianId"]?.GetValue<string>() != techId) throw new InvalidOperationException("Technician is not the current best feasible candidate.");
            var result = new JsonObject { ["jobId"] = jobId, ["technicianId"] = techId,
                ["startAt"] = rank["startAt"]!.DeepClone(), ["endAt"] = rank["endAt"]!.DeepClone(),
                ["location"] = job["location"]!.DeepClone(), ["status"] = "assigned_locally", ["path"] = path };
            return await Data.CreateReceiptAsync(c,path,payload,result,token);
        }
        finally { MutationGate.Release(); }
    };
}
