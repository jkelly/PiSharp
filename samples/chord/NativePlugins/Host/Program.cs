using NativePluginSample;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Registries;

if (args.Length != 2) throw new ArgumentException("Usage: Host <plugin-root> <dotnet-executable>");
var context = ChordContext.Background;
await using var host = Hosts.Create(new() { OnError = error => Console.Error.WriteLine(error.Kind) });
await using var manager = new PluginManager([args[0]], new PluginBuilder(new()
{
    DotnetPath = args[1], Restore = true, ArtifactDirectory = Path.Combine(Path.GetTempPath(), "chord-sample", Guid.NewGuid().ToString("N")),
}));
await using var runtime = new PluginRuntime(host, new() { SharedAssemblies = [typeof(SharedContracts).Assembly] });
var core = Facet.Define((facet, _) => facet.Serve(SharedContracts.Greetings, new RegistryProvider<string, string[]> { Compose = (entries, _) => entries.Select(x => x.Value).ToArray() }));
var report = await runtime.LoadAsync(await manager.BuildAsync(), [new("sample.core", core)], context);
foreach (var failure in report.Failed) Console.Error.WriteLine($"{failure.Plugin}: {failure.Error.Message}");
using var counter = await host.GetAsync(SharedContracts.Counter, new(), context);
Console.WriteLine(await counter.CallAsync("greet", null, context));
Console.WriteLine("Edit the plugin (or break its build), then press Enter to build and reload. Empty input again closes.");
Console.ReadLine();
var reloaded = await runtime.ReloadAsync(await manager.BuildAsync(), context);
foreach (var failure in reloaded.Failed) Console.Error.WriteLine($"{failure.Plugin}: {failure.Error.Message}");
await runtime.DrainRetiredAsync(context);
Console.WriteLine(await counter.CallAsync("greet", null, context));
counter.Release(); await host.CloseAsync(context);
