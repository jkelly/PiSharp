using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

// No provider calls or process ownership changes. Exercise the same deferred
// outer SerializeAsync used by the selected provider qualification report.
internal static class ReportLifetimeControls
{
    internal static async Task RunAsync()
    {
        foreach (var failure in new string?[] { null, "authored retained assertion failure" })
        {
            JsonElement owned; string expected;
            using (var fixture = JsonDocument.Parse("""{"headers":{"x-inert":["one","two"]},"nested":{"items":[null,true,7,"text"]}}"""))
            {
                var harness = new HeldOwnershipHarnessR2();
                // CompareRequest records exactly this borrowed header value.
                harness.Record("request-header-expectation-layers", new
                {
                    sourceHeaders = fixture.RootElement.GetProperty("headers"),
                    nativeHeaders = new Dictionary<string, string[]> { ["x-inert"] = ["one", "two"] },
                    expectedUtf8ByteLength = "7"
                });
                harness.Record("nested-borrowed-value", new { values = new[] { fixture.RootElement.GetProperty("nested") } });
                harness.Record("owned-native-value", JsonData.FromElement(fixture.RootElement));
                var observations = new { failure, actuals = harness.Observations(),
                    sourceQualification = "OPEN; AUTHORED EXPECTATIONS ARE NOT A GENUINE SOURCE CAPTURE" };
                expected = JsonSerializer.Serialize(new { observations });
                owned = IdentitySuccessorAdmission.OwnReportObservations(observations);
            }
            // The original fixture is now disposed; nested arrays, nulls, owned
            // native values and failure text must survive with identical shape.
            await using var report = new MemoryStream();
            await JsonSerializer.SerializeAsync(report, new { observations = owned });
            var actual = Encoding.UTF8.GetString(report.ToArray());
            if (actual != expected)
                throw new InvalidOperationException("Provider report lifetime control changed serialized observations after fixture disposal.");
            using var parsed = JsonDocument.Parse(actual);
            if (parsed.RootElement.GetProperty("observations").GetProperty("failure").GetString() != failure)
                throw new InvalidOperationException("Provider report lifetime control lost retained failure.");
        }
    }
}
