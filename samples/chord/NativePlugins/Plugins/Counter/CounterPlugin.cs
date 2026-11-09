using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Plugins;

namespace NativePluginSample;

public sealed class CounterPlugin : IChordPlugin
{
    public Facet CreateFacet(PluginContext context) => Facet.Define((host, self) =>
    {
        self.Contribute(SharedContracts.Greetings, PrivateGreeting.Value);
        host.Serve(SharedContracts.Counter, (_, _, _) => new(new Dictionary<string, object?>
        { ["greet"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(PrivateGreeting.Value))) }));
    });
}
