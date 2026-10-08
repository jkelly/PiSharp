using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Compatibility.Node;
using PiSharp.ExtensionHost.Supervision;

// Authored receipt-validation controls; no worker, process, source loading or runtime evidence.
internal static class LifecycleSourceIdentityTests
{
    private static readonly string Oracle = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "authored-admitted-oracle"));
    private static readonly string Todo = Path.Combine(Oracle, "upstream", NodeTierAAdmission.TodoSource.Replace('/', Path.DirectorySeparatorChar));
    private static JsonObject Receipt() => JsonSerializer.SerializeToNode(new
    {
        sourceCommit = NodeTierAAdmission.SourceCommit,
        factoryAwaited = true,
        sourceFunctionsRemainInNode = true,
        successfulSourceFactoryInvocations = 1,
        sourceFactoryCount = 1,
        sourceReference = new { expectedSha256 = NodeCommandInputWorkerLaunch.SourceReferenceExpectedSha256 },
        admissionProfile = "bounded-tier-a",
        sourcePins = new[] { new { path = NodeTierAAdmission.TodoSource, bytes = 8848, sha256 = "e46824d00217e25242c186d41837cc84ca81b23f978500323448502a9a424ee2" } },
        commands = Array.Empty<object>(), inputHandlers = Array.Empty<object>(),
        beforeAgentStartHandlers = Array.Empty<object>(), tools = Array.Empty<object>(),
        sessionHandlers = new[]
        {
            new { callbackId = "session_start-1-1", sourcePath = Todo, topic = "session_start" },
            new { callbackId = "session_tree-1-1", sourcePath = Todo, topic = "session_tree" }
        }
    })!.AsObject();
    private static void Validate(JsonObject receipt, string? oracle = null)
    {
        using var json = JsonDocument.Parse(receipt.ToJsonString());
        NodeTierAAdmission.ValidateReceipt(json.RootElement, [NodeTierAAdmission.TodoSource], 1, oracle ?? Oracle);
    }
    private static void Reject(Action<JsonObject> change, string? oracle = null)
    {
        var receipt = Receipt(); change(receipt);
        try { Validate(receipt, oracle); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Changed lifecycle provenance was admitted.");
    }
    private static void Main()
    {
        Validate(Receipt());
        Reject(row => row["sessionHandlers"]![0]!["sourcePath"] = NodeTierAAdmission.TodoSource);
        Reject(row => row["sessionHandlers"]![0]!["sourcePath"] = Path.Combine(Oracle + "-other", "upstream", NodeTierAAdmission.TodoSource.Replace('/', Path.DirectorySeparatorChar)));
        Reject(row => row["sessionHandlers"]![0]!["sourcePath"] = Todo + ".other");
        Reject(row => row["sessionHandlers"]![0]!["sourcePath"] = Todo.Replace("todo.ts", "Todo.ts", StringComparison.Ordinal));
        Reject(row => row["sessionHandlers"]![0]!["sourcePath"] = Path.Combine(Oracle, "upstream", NodeTierAAdmission.HelloSource.Replace('/', Path.DirectorySeparatorChar)));
        Reject(row => row["sourcePins"]![0]!["sha256"] = new string('0', 64));
        Reject(row => row["sourcePins"]![0]!["bytes"] = 8847);
        Reject(row => row["sourcePins"]![0]!["path"] = NodeTierAAdmission.HelloSource);
        Reject(row => row["sessionHandlers"]![1]!["callbackId"] = "session_start-1-1");
        Reject(row => row["sessionHandlers"]![1]!["topic"] = "session_start");
        Reject(row => row["sessionHandlers"]!.AsArray().RemoveAt(1));
        Reject(row => row.Remove("sessionHandlers"));
        Reject(_ => { }, "relative-oracle");
        Console.WriteLine("PASS authored exact lifecycle source admission and 13 rejection controls");
    }
}
