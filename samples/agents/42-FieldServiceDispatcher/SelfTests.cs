// PiSharp-only (no upstream Pi source): feasibility, approval and persistent assignment replay checks.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.FieldServiceDispatcher;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(),"pisharp-42-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root,input,approved:true);
            var w = Workflow.Create(c);
            async Task<JsonNode> Call(int i,string id) => (await w.Tools[i].Execute(new() { ["jobId"] = id },default))!;
            JsonObject Assign(string op,string tech = "TECH-ADA") => new() { ["jobId"] = "JOB-71", ["technicianId"] = tech, ["operationId"] = op };
            Check((await Call(0,"JOB-71"))["found"]!.GetValue<bool>(),"inspect");
            var rank = await Call(1,"JOB-71");
            Check(Data.Text(rank,"recommendedTechnicianId") == "TECH-ADA","feasible recommendation");
            var ada = Data.Rows(rank["candidates"]).Single(x => Data.Text(x,"technicianId") == "TECH-ADA");
            Check(ada["totalTravelMinutes"]!.GetValue<double>() == 20,"inbound and outbound travel");
            var ben = Data.Rows(rank["candidates"]).Single(x => Data.Text(x,"technicianId") == "TECH-BEN");
            Check(Data.Strings(ben["reasons"]).Contains("insufficient_inbound_travel_time"),"travel infeasibility");
            Check(Data.Text(await Call(1,"JOB-72"),"recommendedTechnicianId") == "TECH-CAM","second job distinct skills");
            Check(!(await Call(1,"UNKNOWN"))["found"]!.GetValue<bool>(),"missing job");
            Check(JsonNode.DeepEquals(rank,await Call(1,"JOB-71")),"no stale cache");
            foreach (var y in new[] { long.MaxValue, 1L })
            {
                var oversized = (JsonObject)input.DeepClone();
                oversized["technicians"]![1]!["location"]!["x"] = long.MaxValue;
                oversized["technicians"]![1]!["location"]!["y"] = y;
                var oversizedContext = new SampleContext(Path.Combine(root,"overflow-" + y),oversized,approved:true);
                var oversizedWorkflow = Workflow.Create(oversizedContext);
                await ThrowsOverflow(() => oversizedWorkflow.Tools[1].Execute(new() { ["jobId"] = "JOB-71" },default),"unrepresentable travel cannot rank feasible");
                await ThrowsOverflow(() => oversizedWorkflow.Tools[2].Execute(Assign("overflow","TECH-BEN"),default),"unrepresentable travel cannot assign");
                Check(!Directory.Exists(oversizedContext.ResolvePath("assignments")),"overflow creates no assignment");
            }
            var denied = Workflow.Create(new SampleContext(Path.Combine(root,"denied"),input));
            Check(w.Tools[2].RequiresApproval,"approval metadata");
            await Throws(() => denied.Tools[2].Execute(Assign("deny"),default),"direct callback guard");
            Check(!Directory.Exists(Path.Combine(root,"denied","assignments")),"denied writes nothing");
            await Throws(() => w.Tools[2].Execute(Assign("bad-tech","TECH-BEN"),default),"infeasible assignment");
            // A historical visit does not override the technician's supplied current position.
            input["technicians"]![0]!["appointments"]!.AsArray().Add(SampleJson.Object("""{"id":"PAST","startAt":"2026-11-02T07:00:00Z","endAt":"2026-11-02T08:00:00Z","location":{"x":100,"y":100}}"""));
            Check(Data.Text(await Call(1,"JOB-71"),"recommendedTechnicianId") == "TECH-ADA","current position overrides historical visit");
            input["technicians"]![0]!["appointments"]!.AsArray().RemoveAt(1);
            // A later appointment too close to the job prevents returning in time.
            input["technicians"]![0]!["appointments"]![0]!["startAt"] = "2026-11-02T11:05:00Z";
            var tight = await Call(1,"JOB-71");
            Check(tight["recommendedTechnicianId"] is null,"outbound travel excludes the last candidate");
            input["technicians"]![0]!["appointments"]![0]!["startAt"] = "2026-11-02T10:30:00Z";
            Check(Data.Strings(Data.Rows((await Call(1,"JOB-71"))["candidates"]).Single(x => Data.Text(x,"technicianId") == "TECH-ADA")["reasons"]).Contains("appointment_overlap"),"appointment overlap");
            input["technicians"]![0]!["appointments"]![0]!["startAt"] = "2026-11-02T12:00:00Z";
            input["technicians"]![0]!["shiftEnd"] = "2026-11-02T10:30:00Z";
            Check((await Call(1,"JOB-71"))["recommendedTechnicianId"] is null,"shift containment");
            input["technicians"]![0]!["shiftEnd"] = "2026-11-02T17:00:00Z";
            var assigned = (await w.Tools[2].Execute(Assign("once"),default))!;
            Check(Data.Text(assigned["result"]!,"status") == "assigned_locally","local scope");
            var restarted = Workflow.Create(new SampleContext(root,input,approved:true));
            Check((await restarted.Tools[2].Execute(Assign("once"),default))!["replayed"]!.GetValue<bool>(),"persistent idempotency");
            await Throws(() => w.Tools[2].Execute(Assign("once","TECH-CAM"),default),"conflicting idempotency payload");
            await Throws(() => w.Tools[2].Execute(Assign("another"),default),"duplicate job new operation");
            Check((await Call(1,"JOB-71"))["recommendedTechnicianId"] is null,"existing assignment blocks duplicate dispatch");
            // Existing assignments participate in scheduling a different overlapping job.
            input["jobs"]!.AsArray().Add(SampleJson.Object("""{"id":"JOB-73","startAt":"2026-11-02T10:30:00Z","durationMinutes":30,"skills":["electrical"],"location":{"x":2,"y":1}}"""));
            Check((await Call(1,"JOB-73"))["recommendedTechnicianId"] is null,"persisted assignment prevents overlap");
            Console.WriteLine("PASS 42-FieldServiceDispatcher (27 focused checks)");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
    static void Check(bool value,string name) { if (!value) throw new Exception(name); }
    static async Task Throws(Func<Task> action,string name)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection: " + name);
    }
    static async Task ThrowsOverflow(Func<Task> action,string name)
    {
        try { await action(); } catch (OverflowException) { return; }
        throw new Exception("Expected overflow rejection: " + name);
    }
}
