using System.Text.Json;

string? host=null, node=null, repo=null, report=null;
var seen=new HashSet<string>(StringComparer.Ordinal);
for(var index=0;index<args.Length;index++)
{
    var key=args[index];
    if(!seen.Add(key)||index+1>=args.Length) throw new ArgumentException("Explicit unique --dotnet-host, --node, --repo and optional --report required.");
    var value=Path.GetFullPath(args[++index]);
    switch(key){case "--dotnet-host":host=value;break;case "--node":node=value;break;case "--repo":repo=value;break;case "--report":report=value;break;default:throw new ArgumentException("Unknown process test option.");}
}
if(host is null||node is null||repo is null)throw new ArgumentException("Explicit runtime and repository paths required.");
var tests=NodeWorkerSupervisorTests.Cases(host,node,repo).ToArray();
var evidence=new List<object>();var failed=0;
foreach(var test in tests)
{
    try{await test.Run().WaitAsync(TimeSpan.FromSeconds(90));Console.WriteLine("PASS "+test.Name);evidence.Add(new{testId=test.Name,status="passed"});}
    catch(Exception error){failed++;Console.Error.WriteLine($"FAIL {test.Name}: {error}");evidence.Add(new{testId=test.Name,status="failed",error=error.Message});}
}
Console.WriteLine($"Actual optional Node worker: {tests.Length-failed} passed, {failed} failed.");
if(report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{schemaVersion=1,scope="actual-pinned-node-worker-supervision-authored-fixed-peer",passed=tests.Length-failed,failed,tests=evidence,nativeBridgeParity=false,upstreamExtensionExecuted=false,phaseGatesPassed=Array.Empty<string>()},new JsonSerializerOptions{WriteIndented=true})+"\n");
}
return failed==0?0:1;
