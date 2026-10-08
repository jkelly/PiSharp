using System.Text.Json;

var originals = new List<OriginalTaskRecord>();
var differences = new List<GoldenResult>();
try
{
    foreach (var item in OfflineCases.All)
    {
        var record = new OriginalTaskRecord(item.Name); originals.Add(record);
        try
        {
            var original = item.Run(); record.Original = original;
            var actual = await original;
            differences.Add(new(item.Name, item.Source, item.Scope, item.Expected, actual,
                JsonElement.DeepEquals(item.Expected.Value, actual.Value)));
        }
        catch (Exception error) { record.Direct = error; }
        finally { record.Capture(); }
    }
    Console.WriteLine(JsonSerializer.Serialize(new {
        sourceDerivedOracle = true, originalExecuted = false, originalPin = "d86654abb8862e201933517d6f1fce9f88dd117f",
        fixtureAuthoringBase = "0faf10e6d75eee5730716f93f8162e834b921eaf", cases = differences.Select(item => new {
            item.Name, item.Source, item.Scope, item.Matched, expected = item.Expected.Value, actual = item.Actual.Value }),
        groups = originals.Select(QualificationReporter.Project).ToArray(),
        ownedOriginals = OfflineCases.Originals.Select(record => new {
            expectedFault = record.Name.EndsWith("-expected-fault", StringComparison.Ordinal),
            observation = QualificationReporter.Project(record) }).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true }));
    return differences.Count != OfflineCases.All.Length || differences.Any(item => !item.Matched) || originals.Any(item => item.Direct is not null) ? 1 : 0;
}
catch (Exception reportFailure)
{ throw new QualificationReportingFailure(reportFailure, originals.ToArray(), OfflineCases.Originals.ToArray()); }

internal sealed record GoldenResult(string Name, string Source, string Scope, PiSharp.Contracts.JsonData Expected, PiSharp.Contracts.JsonData Actual, bool Matched);
