using System.Text.Json;
using PiSharp.Contracts;

internal static class NativePolicyAccountingTests
{
    internal sealed record Control(string Id, string Status);
    internal static Control[] Run()
    {
        var configured = OfflineCases.All.Where(item => item.AcceptedNativePolicy is not null).ToArray();
        Require(configured.Length == 1 && configured[0].Name == "mistral-response-callback-original-error",
            "The accepted native difference must remain restricted to its named Mistral case.");
        var item = configured[0]; var policy = item.AcceptedNativePolicy!;
        Require(policy.Name == "mistral-foreign-callback-fixed-sourcefailed-diagnostic" &&
            policy.Evidence == "src/PiSharp.AI/Protocols/MistralConversations/HANDOFF.md:37", "Native policy evidence changed.");
        var sourceExpected = JsonData.Parse("""{"stop":"Error","starts":0,"responseHooks":1,"error":"offline callback failure"}""");
        Require(JsonElement.DeepEquals(sourceExpected.Value, item.Expected.Value), "The original source expectation was changed.");
        var nativeExpected = JsonData.Parse("""{"stop":"Error","starts":0,"responseHooks":1,"error":"Mistral source operation failed."}""");
        Require(JsonElement.DeepEquals(nativeExpected.Value, policy.Expected.Value), "The exact native policy expectation changed.");
        var native = GoldenResult.Compare(item, nativeExpected);
        Require(!native.Matched && native.AcceptedNativeDifference && native.ContractSatisfied &&
            native.Status == "PASS_NATIVE_POLICY_DIFFERENCE", "An exact native policy was misreported as source parity.");
        var source = GoldenResult.Compare(item, item.Expected);
        Require(source.Matched && !source.AcceptedNativeDifference && !source.ContractSatisfied && source.Status == "FAIL",
            "Unexpected source-shaped output bypassed the explicit native contract.");
        var unexpected = GoldenResult.Compare(item, JsonData.Parse("""{"stop":"Error","starts":0,"responseHooks":1,"error":"unexpected callback detail"}"""));
        Require(!unexpected.Matched && !unexpected.AcceptedNativeDifference && !unexpected.ContractSatisfied && unexpected.Status == "FAIL",
            "Unexpected native diagnostic was accepted.");
        var wrongHooks = GoldenResult.Compare(item, JsonData.Parse("""{"stop":"Error","starts":0,"responseHooks":2,"error":"Mistral source operation failed."}"""));
        Require(!wrongHooks.ContractSatisfied && !wrongHooks.AcceptedNativeDifference, "Only the diagnostic text was compared.");
        var ordinary = GoldenResult.Compare(item with { Name = "unrelated-control", AcceptedNativePolicy = null }, policy.Expected);
        Require(!ordinary.Matched && !ordinary.ContractSatisfied && !ordinary.AcceptedNativeDifference,
            "A native-policy exception leaked into an unconfigured case.");
        return [new("native-policy-single-case-evidence-and-source-expectation", "PASS"),
            new("native-policy-exact-output-remains-source-mismatch", "PASS"),
            new("native-policy-rejects-unexpected-source-shaped-output", "PASS"),
            new("native-policy-rejects-unexpected-native-message", "PASS"),
            new("native-policy-compares-complete-observation", "PASS"),
            new("native-policy-does-not-accept-unconfigured-differences", "PASS")];
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
