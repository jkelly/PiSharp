// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/surfaces/current.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Host;
using PiSharp.Chord.Surfaces;

namespace PiSharp.Chord.Hosting.Surfaces;

public enum SurfaceName { Web, Tui }

/// <summary>One local publication object supplies snapshots to each principal's scripts object.</summary>
public static class SurfaceScripts
{
    private static readonly Contract WebCurrent = Current("chord.web.current");
    private static readonly Contract TuiCurrent = Current("chord.tui.current");
    private static Contract Current(string name) => Contract.Local(name, new()
    {
        Version = 1, Params = [], Members = new()
        {
            ["set"] = Scripts.Schema,
            ["publish"] = Schemas.Method(Scripts.Schema),
        },
    });
    private static Contract Current(SurfaceName surface) => surface switch
    { SurfaceName.Web => WebCurrent, SurfaceName.Tui => TuiCurrent, _ => throw new ArgumentOutOfRangeException(nameof(surface)) };
    private static Contract Public(SurfaceName surface) => surface switch
    { SurfaceName.Web => Scripts.WebScripts, SurfaceName.Tui => Scripts.TuiScripts, _ => throw new ArgumentOutOfRangeException(nameof(surface)) };

    public static Facet CreateFacet(SurfaceName surface, Func<ScriptSet> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var local = Current(surface); var remote = Public(surface);
        return Facet.Define((host, _) =>
        {
            host.Serve(local, (_, _, self) =>
            {
                var value = self.Writable<JsonNode?>(current().ToJson());
                return new(new Dictionary<string, object?>
                {
                    ["set"] = self.Derive([value], values => ((JsonNode?)values[0])?.DeepClone()),
                    ["publish"] = (HostMethod)((arguments, _) =>
                    {
                        var next = ScriptSet.FromJson(arguments as JsonNode);
                        value.Set(next.ToJson());
                        return Task.FromResult<object?>(null);
                    }),
                });
            });
            host.Serve(remote, (_, _, self) =>
            {
                var reference = self.Get(local, new());
                return new(new Dictionary<string, object?>
                { ["set"] = self.Derive([reference.Member("set")], values => ((JsonNode?)values[0])?.DeepClone()) }, "main");
            });
        });
    }
    public static async Task PublishAsync(ChordHost host, SurfaceName surface, ScriptSet set, ChordContext context)
    {
        using var reference = await host.GetAsync(Current(surface), new(), context).ConfigureAwait(false);
        await reference.CallAsync("publish", set.ToJson(), context).ConfigureAwait(false);
    }
}
