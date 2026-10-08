using System.Text.Json;

var values = new Dictionary<string, string>(StringComparer.Ordinal);
var allowed = new HashSet<string>(["--dotnet-host", "--node", "--repo", "--oracle", "--jiti", "--reference-repo", "--run-parent", "--report"], StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !allowed.Contains(args[index]) || !Path.IsPathFullyQualified(args[index + 1]) ||
        !values.TryAdd(args[index], Path.GetFullPath(args[index + 1])))
        throw new ArgumentException("Bridge process tests require unique explicit path options.");
}
foreach (var option in allowed)
    if (!values.ContainsKey(option)) throw new ArgumentException("Missing required bridge test option: " + option);
var cases = ProtectedPathsBridgeTests.Cases(values["--dotnet-host"], values["--node"], values["--repo"],
    values["--oracle"], values["--jiti"], values["--reference-repo"], values["--run-parent"]);
var results = new List<object>(); var passed = 0; var failed = 0;
foreach (var (name, run) in cases)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(90));
        passed++; Console.WriteLine("PASS " + name);
        results.Add(new { testId = name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++; Console.Error.WriteLine("FAIL " + name + ": " + error);
        results.Add(new { testId = name, status = "failed", error = error.Message });
    }
}
Console.WriteLine($"Real Node protected-paths bridge tests: {passed} passed, {failed} failed.");
Directory.CreateDirectory(Path.GetDirectoryName(values["--report"])!);
await File.WriteAllTextAsync(values["--report"], JsonSerializer.Serialize(new
{
    schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
    scope = "actual-optional-node-protected-paths-hook-native-invoker-and-ui-boundary",
    fullPhaseAcceptance = false, tests = results, passed, failed
}, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
