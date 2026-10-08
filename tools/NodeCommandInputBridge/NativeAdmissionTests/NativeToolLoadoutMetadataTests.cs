// Authored metadata adapter contracts. No Node/worker is launched by this class.
using System.Collections.Immutable;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;

internal static class NativeToolLoadoutMetadataTests
{
    public static string[] Run()
    {
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Authored loadout metadata assertion failed."); }
        static ToolLoadoutTool Tool(string name, ToolExposure exposure, ToolNamespace? group = null) =>
            new(JsonData.Parse("{\"name\":\"" + name + "\",\"description\":\"original " + name + "\",\"parameters\":{\"type\":\"object\"}}"), exposure) { Namespace = group };
        var direct = Tool("direct", ToolExposure.Direct);
        var outer = Tool("outer", ToolExposure.ModelOnly);
        var code = Tool("code", ToolExposure.Codemode, new("g"));
        var hidden = Tool("hidden", ToolExposure.Hidden, new("", ""));
        var loadout = new ToolLoadout([outer, direct], [direct, code], [direct, outer, code, hidden]);
        var wire = NodeToolLoadoutMetadata.Write(loadout, "loadout-1").Value;
        Check(wire.GetProperty("declared").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["outer", "direct"]));
        Check(wire.GetProperty("callable").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["direct", "code"]));
        var rows = wire.GetProperty("registered");
        Check(!rows[0].TryGetProperty("namespace", out _) && rows[2].GetProperty("namespace").GetProperty("name").GetString() == "g");
        Check(!rows[2].GetProperty("namespace").TryGetProperty("description", out _) && rows[3].GetProperty("namespace").GetProperty("description").GetString() == "");
        Check(rows[3].GetProperty("exposure").GetString() == "hidden" && !rows[0].GetProperty("declaration").TryGetProperty("execute", out _));
        // Later registrations/metadata cannot rewrite the already owned serialized snapshot.
        var replacement = new ToolLoadout([outer, direct], [direct, code], [direct, outer, code with { Namespace = new("later") }, hidden]);
        var later = NodeToolLoadoutMetadata.Write(replacement, "loadout-2");
        Check(later.Value.GetProperty("registered")[2].GetProperty("namespace").GetProperty("name").GetString() == "later" &&
            wire.GetProperty("registered")[2].GetProperty("namespace").GetProperty("name").GetString() == "g" && loadout.GetNamespace("code")?.Name == "g");
        var changes = NodeToolLoadoutMetadata.ReadChanges(JsonData.Parse("{\"descriptions\":{\"outer\":\"g\"},\"hiddenDeclarations\":[\"outer\"]}"))!;
        Check(changes.Descriptions["outer"] == "g" && changes.HiddenDeclarations.SequenceEqual(["outer"]) &&
            NodeToolLoadoutMetadata.ReadChanges(null) is null && NodeToolLoadoutMetadata.ReadChanges(JsonData.Null) is null &&
            loadout.Declared[0].Description == "original outer" && loadout.GetExposure("hidden") == ToolExposure.Hidden);
        foreach (var json in new[] { "{\"executeTool\":true}", "{\"descriptions\":{\"outer\":false}}", "{\"hiddenDeclarations\":[null]}" })
        {
            var rejected = false;
            try { NodeToolLoadoutMetadata.ReadChanges(JsonData.Parse(json)); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected);
        }
        var malformed = new ToolLoadout([Tool("missing", ToolExposure.Direct)], [], [direct]);
        var malformedRejected = false;
        try { NodeToolLoadoutMetadata.Write(malformed, "loadout-3"); } catch (InvalidOperationException) { malformedRejected = true; }
        Check(malformedRejected);
        var oversized = new ToolLoadout([], [], [direct with { Namespace = new(new string('x', 65537)) }]);
        var oversizedRejected = false;
        try { NodeToolLoadoutMetadata.Write(oversized, "loadout-4"); } catch (InvalidOperationException) { oversizedRejected = true; }
        Check(oversizedRejected);
        Console.WriteLine("PASS authored Node loadout metadata adapter contracts; no worker execution");
        return ["ordered-namespace-presence", "immutable-earlier-snapshot", "presentation-only-changes",
            "reject-invalid-changes-and-membership", "reject-oversized-namespace"];
    }
}
