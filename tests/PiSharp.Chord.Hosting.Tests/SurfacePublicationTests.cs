// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/surfaces/surface-build.ts, packages/chord/src/node/surfaces/published.ts.
// Authored native regression; not an upstream execution capture.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Surfaces;
using PiSharp.Chord.Surfaces;

internal static class SurfacePublicationTests
{
    private static readonly BaseInputs NoInputs = BaseInputs.FromHashes([]);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static SurfaceBuildArtifact Artifact(string source, string? css = null, params SurfaceAsset[] assets) => new(source, NoInputs, css, assets);

    public static Task Retention()
    {
        var published = new PublishedScripts("/_chord/web/", "base-hash");
        var assetBytes = new byte[] { 1, 2, 3 };
        var assetPath = SurfacePaths.Asset("/_chord/web/", "logo.png", assetBytes);
        var first = published.Publish([
            new("z.plugin", Artifact("function(){return 'z';}", "body{color:red}", new SurfaceAsset("logo.png", assetBytes))),
            new("a.plugin", Artifact("function(){return 'a';}\n//# sourceMappingURL=data:ignore")),
        ]);
        Check(first.Set.Plugins.Select(x => x.Name).SequenceEqual(["a.plugin", "z.plugin"]), "plugins must publish in ordinal order");
        var entry = first.Set.Plugins[1];
        var script = published.File(entry.Url)!;
        Check(entry.Url.Contains(SurfacePaths.Hash(script.ToArray()), StringComparison.Ordinal), "script URL must hash served bytes");
        Check(!Encoding.UTF8.GetString(published.File(first.Set.Combined)!.ToArray()).Contains("sourceMappingURL", StringComparison.Ordinal), "combined script must omit inline maps");
        assetBytes[0] = 9;
        Check(published.File(assetPath)!.ToArray()[0] == 1, "cache must own asset bytes");
        var second = published.Publish([new("z.plugin", Error: new IOException("build failed"))]);
        Check(second.Failed.Count == 1 && second.Set.Plugins.Single() == entry, "failed plugin must keep JS and CSS");
        Check(published.File(assetPath)!.ToArray()[0] == 1, "failed plugin must retain assets");
        var changed = published.Publish([new("z.plugin", Artifact("function(){return 'z';}", "body{color:blue}"))]);
        Check(changed.Set.Plugins.Single().Digest == entry.Digest && changed.Set.Plugins.Single().Css != entry.Css, "CSS-only change must preserve JS digest and change CSS URL");
        published.Publish([]);
        Check(published.File(first.Set.Combined) is not null && published.RetainedSets == 4, "fourth publication must retain oldest set");
        published.Publish([new("fresh", Artifact("function(){return 'new';}"))]);
        Check(published.File(first.Set.Combined) is null && published.RetainedSets == 4, "fifth publication must evict oldest set");
        Check(published.File(entry.Url) is not null, "files referenced by another retained set remain available");
        return Task.CompletedTask;
    }

    public static async Task BuildOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new Builder(async (_, token) =>
        { entered.SetResult(); await finish.Task.WaitAsync(token); return Artifact("globalThis.ready=true;", "body{}"); });
        var watch = new Watch(); var watched = 0;
        await using var surface = new SurfaceBuild(new()
        {
            Base = new("web", "base.js", "/web/"), Builder = builder,
            WatchBase = inputs => { Check(ReferenceEquals(inputs, NoInputs), "must watch compiler-read input hashes"); watched++; return watch; },
        });
        using var cancelled = new CancellationTokenSource();
        var abandoned = surface.BuildAsync(cancelled.Token);
        await entered.Task;
        cancelled.Cancel();
        try { await abandoned; throw new InvalidOperationException("waiter did not cancel"); } catch (OperationCanceledException) { }
        var remaining = surface.BuildAsync(); finish.SetResult();
        var outcome = await remaining;
        Check(outcome.Success && builder.Count == 1 && watched == 1, "cancelling a waiter must not cancel or duplicate shared build");
        Check(ReferenceEquals(outcome, await surface.BuildAsync()), "outcome must be cached");
        var response = await surface.GetFileAsync(surface.BaseScript!);
        Check(response.Status == 200 && response.Headers["Cache-Control"].Single() == "private, max-age=31536000, immutable", "artifact cache response");
        Check((await surface.GetFileAsync("/web/missing")).Status == 404, "unknown successful surface file must be 404");
        await surface.DisposeAsync();
        Check(watch.Disposed, "base input registration must be released");
    }

    public static async Task FailureHttpIsolation()
    {
        var failedBuilder = new Builder((_, _) => Task.FromException<SurfaceBuildArtifact>(new IOException("base broken")));
        await using var failed = new SurfaceBuild(new() { Base = new("web", "missing.js", "/web/"), Builder = failedBuilder });
        await using var host = Hosts.Create(new() { OnError = _ => { } });
        await using var http = new ChordHttp(host, new() { OnError = _ => { } });
        var routes = Facet.Define((_, self) =>
        {
            var surfaceRoutes = new Dictionary<string, HttpRoute>(failed.Contribution.Routes)
            {
                ["GET /alive"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("alive")), Public: true),
            };
            self.Contribute(HttpRegistries.Http, new HttpContribution
            {
                Routes = surfaceRoutes,
                ScriptPaths = failed.Contribution.ScriptPaths,
            });
        });
        await host.LoadAsync([new("chord.http", http.Facet), new("test.surface", routes)], ChordContext.Background);
        for (var i = 0; i < 2; i++)
        {
            var request = Request("/web/base.js"); await http.HandleAsync(request);
            Check(request.Response.StatusCode == 503, $"failed base must return 503 only on its surface route (got {request.Response.StatusCode})");
        }
        var alive = Request("/alive"); await http.HandleAsync(alive);
        Check(alive.Response.StatusCode == 200 && failedBuilder.Count == 1, "failed build is cached and server continues");
        Check(failed.Contribution.ScriptPaths.SequenceEqual(["/web/"]), "CSP must only admit surface prefix");
    }

    public static async Task HttpArtifact()
    {
        await using var surface = new SurfaceBuild(new()
        { Base = new("web", "entry.js", "/web/"), Builder = new Builder((_, _) => Task.FromResult(Artifact("globalThis.hello=1;"))) });
        await using var host = Hosts.Create(new() { OnError = _ => { } });
        await using var http = new ChordHttp(host, new() { OnError = _ => { } });
        await host.LoadAsync([new("chord.http", http.Facet), new("test.surface", Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, surface.Contribution)))], ChordContext.Background);
        await surface.BuildAsync();
        var request = Request(surface.BaseScript!); await http.HandleAsync(request);
        Check(request.Response.StatusCode == 200, "anonymous public artifact request must succeed");
        Check(request.Response.ContentType == "text/javascript; charset=utf-8", "JavaScript MIME must be correct");
        Check(request.Response.Headers.CacheControl.ToString() == "private, max-age=31536000, immutable", "immutable private cache must survive HTTP response writer");
        Check(Encoding.UTF8.GetString(((MemoryStream)request.Response.Body).ToArray()) == "globalThis.hello=1;", "HTTP must serve actual JavaScript bytes");
    }

    public static async Task ScriptsRoundTrip()
    {
        await using var host = Hosts.Create(new() { Address = Scripts.Server.Address(), OnError = _ => { }, Principal = new() { ["kind"] = "test" } });
        var initial = new ScriptSet("a", "/plugins.a.js", []);
        await host.LoadAsync([new("test.surface", SurfaceScripts.CreateFacet(SurfaceName.Web, () => initial))], ChordContext.Background);
        using var reference = await host.GetAsync(Scripts.WebScripts, new(), ChordContext.Background);
        Check(ScriptSet.FromJson(reference.Member("set").Value).Base == "a", "new client receives current publication");
        var next = new ScriptSet("b", "/plugins.b.js", [new("test", "digest", "/test.js")]);
        await SurfaceScripts.PublishAsync(host, SurfaceName.Web, next, ChordContext.Background);
        Check(ScriptSet.FromJson(reference.Member("set").Value).Base == "b", "held client receives next publication");
    }

    private static DefaultHttpContext Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.Scheme = "http"; context.Request.Host = new("localhost", 8123);
        context.Connection.LocalIpAddress = IPAddress.Loopback; context.Connection.LocalPort = 8123;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Path = path; context.Response.Body = new MemoryStream();
        return context;
    }
    private sealed class Builder(Func<SurfaceBuildRequest, CancellationToken, Task<SurfaceBuildArtifact>> build) : ISurfaceBuilder
    {
        public int Count;
        public Task<SurfaceBuildArtifact> BuildAsync(SurfaceBuildRequest request, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return build(request, cancellationToken); }
    }
    private sealed class Watch : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
}
