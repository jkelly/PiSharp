using System.Text.Json;

var originals = new List<OriginalTaskRecord>();
var differences = new List<GoldenResult>();
try
{
    var accountingControls = NativePolicyAccountingTests.Run();
    foreach (var item in OfflineCases.All)
    {
        var record = new OriginalTaskRecord(item.Name); originals.Add(record);
        try
        {
            var original = item.Run(); record.Original = original;
            var actual = await original;
            differences.Add(GoldenResult.Compare(item, actual));
        }
        catch (Exception error) { record.Direct = error; }
        finally { record.Capture(); }
    }
    Console.WriteLine(JsonSerializer.Serialize(new {
        sourceDerivedOracle = true, originalExecuted = false, originalPin = "d86654abb8862e201933517d6f1fce9f88dd117f",
        // Preserve the historical baseline; this one intentional backport is attributed separately, never counted as v1.5 parity.
        postBaselineBackports = new[] { new { name = "responses-cache-session-affinity-headers", targetPin = "7d073328e44337d3292a7db9b0f0308f679c5e97",
            change = "Generated Responses session_id becomes session-id; caller deletion addresses the new spelling." } },
        fixtureAuthoringBase = "0faf10e6d75eee5730716f93f8162e834b921eaf", cases = differences.Select(item => new {
            item.Name, item.Source, item.Scope, item.Matched, status = item.Status,
            expected = item.Expected.Value, actual = item.Actual.Value,
            acceptedNativeDifference = item.AcceptedNativeDifference,
            nativePolicy = item.NativePolicy is { } policy ? new { policy.Name, policy.Evidence,
                expected = policy.Expected.Value, matched = item.NativePolicyMatched } : null,
            reason = item.AcceptedNativeDifference ? "Exact native policy satisfied; original source expectation differs." : null }),
        sourceMatchedCount = differences.Count(item => item.Matched),
        acceptedNativeDifferenceCount = differences.Count(item => item.AcceptedNativeDifference),
        contractFailedCount = differences.Count(item => !item.ContractSatisfied),
        nativePolicyAccountingControls = accountingControls,
        groups = originals.Select(QualificationReporter.Project).ToArray(),
        ownedOriginals = OfflineCases.Originals.Select(record => new {
            expectedFault = record.Name.EndsWith("-expected-fault", StringComparison.Ordinal),
            observation = QualificationReporter.Project(record) }).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true }));
    return differences.Count != OfflineCases.All.Length || differences.Any(item => !item.ContractSatisfied) || originals.Any(item => item.Direct is not null) ? 1 : 0;
}
catch (Exception reportFailure)
{ throw new QualificationReportingFailure(reportFailure, originals.ToArray(), OfflineCases.Originals.ToArray()); }

internal sealed record GoldenResult(string Name, string Source, string Scope, PiSharp.Contracts.JsonData Expected,
    PiSharp.Contracts.JsonData Actual, bool Matched, NativePolicyExpectation? NativePolicy)
{
    internal bool NativePolicyMatched => NativePolicy is { } policy && JsonElement.DeepEquals(policy.Expected.Value, Actual.Value);
    // An explicit native policy must match exactly, even if unexpected output happens
    // to match the original source. It is never a wildcard accepting any difference.
    internal bool ContractSatisfied => NativePolicy is null ? Matched : NativePolicyMatched;
    internal bool AcceptedNativeDifference => !Matched && NativePolicyMatched;
    internal string Status => !ContractSatisfied ? "FAIL" : AcceptedNativeDifference ? "PASS_NATIVE_POLICY_DIFFERENCE" : "PASS";
    internal static GoldenResult Compare(GoldenCase item, PiSharp.Contracts.JsonData actual) =>
        new(item.Name, item.Source, item.Scope, item.Expected, actual,
            JsonElement.DeepEquals(item.Expected.Value, actual.Value), item.AcceptedNativePolicy);
}
