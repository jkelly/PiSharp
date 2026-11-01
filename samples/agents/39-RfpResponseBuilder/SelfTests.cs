// PiSharp-only (no upstream Pi source): grounding, missing evidence and repeated draft checks.
using System.Text.Json.Nodes;
using PiSharp.Samples;
namespace PiSharp.Samples.RfpResponseBuilder;

public static class SelfTests
{
    public static async Task<int> RunAsync(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(),"pisharp-39-" + Guid.NewGuid().ToString("N"));
        try
        {
            var input = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
            var c = new SampleContext(root,input);
            var w = Workflow.Create(c);
            async Task<JsonNode> Call(int i,string id) => (await w.Tools[i].Execute(new() { ["rfpId"] = id },default))!;
            Check((await Call(0,"RFP-ORBIT"))["found"]!.GetValue<bool>(),"inspect");
            var match = await Call(1,"RFP-ORBIT");
            Check(Data.Number(match,"supportedCount") == 2 && Data.Number(match,"missingMandatoryCount") == 1,"requirement coverage");
            Check(match["unsupportedClaims"]!.AsArray().Count == 1 && !match["readyToSubmit"]!.GetValue<bool>(),"unsupported expired claim");
            var row = match["requirements"]![0]!;
            Check(row["sourceIds"]![0]!.GetValue<string>() == "SRC-SSO" && Data.Text(row,"response").Contains("[SRC-SSO]",StringComparison.Ordinal),"source attribution");
            Check(match["requirements"]![1]!["rejectedSourceIds"]![0]!.GetValue<string>() == "SRC-OLD-EU","expired evidence excluded");
            var tiny = await Call(1,"RFP-TINY");
            Check(tiny["readyToSubmit"]!.GetValue<bool>() && Data.Number(tiny,"supportedCount") == 1,"second RFP independent");
            Check(!(await Call(1,"UNKNOWN"))["found"]!.GetValue<bool>(),"missing RFP");
            Check(JsonNode.DeepEquals(match,await Call(1,"RFP-ORBIT")),"no stale cache");
            var saved = await Call(2,"RFP-ORBIT");
            var first = await File.ReadAllTextAsync(c.ResolvePath("rfp/RFP-ORBIT.json"));
            await Call(2,"RFP-ORBIT");
            Check(first == await File.ReadAllTextAsync(c.ResolvePath("rfp/RFP-ORBIT.json")) && !saved["submitted"]!.GetValue<bool>(),"repeat local draft only");
            input["rfps"]![1]!["draftClaims"]!.AsArray().Add(SampleJson.Object("""{"requirementId":"REQ-EXPORT","sourceId":"SRC-EXPORT","text":"Unlimited exports are always instant."}"""));
            Check((await Call(1,"RFP-TINY"))["unsupportedClaims"]!.AsArray().Count == 1,"embellished source claim rejected");
            input["evidence"]![2]!["approved"] = false;
            Check(Data.Number(await Call(1,"RFP-TINY"),"missingMandatoryCount") == 1,"unapproved evidence excluded");
            input["evidence"]!.AsArray().Add(input["evidence"]![0]!.DeepClone());
            await Throws(() => Call(1,"RFP-ORBIT"),"ambiguous source");
            Console.WriteLine("PASS 39-RfpResponseBuilder (12 focused checks)");
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
}
