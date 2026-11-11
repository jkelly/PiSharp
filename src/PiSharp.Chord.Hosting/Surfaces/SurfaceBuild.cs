// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/surfaces/surface-build.ts.
using System.Collections.ObjectModel;
using System.Text;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Surfaces;

namespace PiSharp.Chord.Hosting.Surfaces;

public sealed class SurfaceBuildOptions
{
    public required ISurfaceBuilder Builder { get; init; }
    public required SurfaceBuildRequest Base { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<SurfacePluginBuild>>>? Plugins { get; init; }
    public Func<ScriptSet, CancellationToken, Task>? Publish { get; init; }
    public Func<BaseInputs, IDisposable>? WatchBase { get; init; }
}

/// <summary>A cached build outcome. Only this surface's dependent routes fail when its base fails.</summary>
public sealed record SurfaceBuilt(string? Base = null, BaseInputs? Inputs = null, Exception? Error = null)
{
    public bool Success => Error is null && Base is not null;
}

/// <summary>One base build per lifetime, shared by startup and requests; cancelling one waiter never cancels the build.</summary>
public sealed class SurfaceBuild : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim reload = new(1, 1);
    private readonly SurfaceBuildOptions options;
    private readonly CancellationTokenSource lifetime;
    private readonly string prefix;
    private Task<SurfaceBuilt>? building;
    private SurfaceBuilt? built;
    private PublishedScripts? published;
    private IReadOnlyDictionary<string, SurfaceFile> baseFiles = new ReadOnlyDictionary<string, SurfaceFile>(new Dictionary<string, SurfaceFile>());
    private IDisposable? watch;
    private bool disposed;
    public SurfaceBuild(SurfaceBuildOptions options, CancellationToken cancellationToken = default)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Base.Plugin is not null) throw new ArgumentException("base request cannot name a plugin", nameof(options));
        prefix = SurfacePaths.Prefix(options.Base.Prefix);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }
    public ScriptSet Set { get { lock (gate) return published?.Set ?? new("", "", []); } }
    public SurfaceBuilt? Built { get { lock (gate) return built; } }
    public string? BaseScript { get; private set; }
    public string? BaseStyle { get; private set; }
    public string Prefix => prefix;
    public HttpContribution Contribution => new()
    {
        ScriptPaths = [prefix],
        Routes = new Dictionary<string, HttpRoute>
        {
            ["GET " + prefix + "*"] = new(async (request, context) =>
                await GetFileAsync(request.Path, context.AbortSignal).ConfigureAwait(false), Public: true),
        },
    };
    public Task<SurfaceBuilt> BuildAsync(CancellationToken cancellationToken = default)
    {
        Task<SurfaceBuilt> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            task = building ??= RunAsync();
        }
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }
    public async Task<HttpResult> GetFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = await BuildAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success) return new HttpResult.Empty { Status = 503 };
        SurfaceFile? file;
        lock (gate) file = baseFiles.GetValueOrDefault(path) ?? published!.File(path);
        return file?.Response() ?? new HttpResult.Empty { Status = 404 };
    }
    /// <summary>Refreshes the complete set from the configured plugin compiler/provider.</summary>
    public async Task<SurfacePublication> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var result = await BuildAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("surface base build failed", result.Error);
        var plugins = options.Plugins is { } provider ? await provider(cancellationToken).ConfigureAwait(false) : [];
        return await ReloadAsync(plugins, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Publishes already compiled complete results; failures keep their last good script, CSS and assets.</summary>
    public async Task<SurfacePublication> ReloadAsync(IReadOnlyList<SurfacePluginBuild> plugins, CancellationToken cancellationToken = default)
    {
        var result = await BuildAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("surface base build failed", result.Error);
        await reload.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var publication = published!.Publish(plugins);
            if (options.Publish is { } publish) await publish(publication.Set, cancellationToken).ConfigureAwait(false);
            return publication;
        }
        finally { reload.Release(); }
    }
    private async Task<SurfaceBuilt> RunAsync()
    {
        // Yield ensures the cached task is installed before a synchronous builder can call back into this object.
        await Task.Yield();
        SurfaceBuilt outcome;
        try
        {
            var artifact = await options.Builder.BuildAsync(options.Base with { Prefix = prefix }, lifetime.Token).ConfigureAwait(false);
            var files = new Dictionary<string, SurfaceFile>(StringComparer.Ordinal);
            var hash = SurfacePaths.ArtifactHash(artifact);
            var script = $"{prefix}base.{SurfacePaths.Hash(artifact.JavaScript)}.js";
            PublishedScripts.AddFile(files, script, new(Encoding.UTF8.GetBytes(artifact.JavaScript), SurfacePaths.Mime("base.js")));
            string? css = null;
            if (artifact.Css is { } style)
            {
                css = $"{prefix}base.{SurfacePaths.Hash(style)}.css";
                PublishedScripts.AddFile(files, css, new(Encoding.UTF8.GetBytes(style), SurfacePaths.Mime("base.css")));
            }
            foreach (var asset in artifact.Assets ?? [])
                PublishedScripts.AddFile(files, SurfacePaths.Asset(prefix, asset.Name, asset.Bytes.Span), new(asset.Bytes.Span, asset.Mime ?? SurfacePaths.Mime(asset.Name)));
            var scripts = new PublishedScripts(prefix, hash);
            var plugins = options.Plugins is { } getPlugins ? await getPlugins(lifetime.Token).ConfigureAwait(false) : [];
            var first = scripts.Publish(plugins);
            if (options.Publish is { } publish) await publish(first.Set, lifetime.Token).ConfigureAwait(false);
            var inputWatch = options.WatchBase?.Invoke(artifact.Inputs);
            lock (gate)
            {
                if (disposed) { inputWatch?.Dispose(); throw new OperationCanceledException(lifetime.Token); }
                BaseScript = script; BaseStyle = css;
                baseFiles = new ReadOnlyDictionary<string, SurfaceFile>(files);
                published = scripts; watch = inputWatch;
            }
            outcome = new(hash, artifact.Inputs);
        }
        catch (Exception error) { outcome = new(Error: error); }
        lock (gate) built = outcome;
        return outcome;
    }
    public async ValueTask DisposeAsync()
    {
        Task<SurfaceBuilt>? task;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; task = building;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (task is not null) await task.ConfigureAwait(false);
        // Let any already admitted publication finish before releasing its resources.
        await reload.WaitAsync().ConfigureAwait(false);
        try { watch?.Dispose(); lifetime.Dispose(); }
        finally { reload.Release(); }
    }
}
