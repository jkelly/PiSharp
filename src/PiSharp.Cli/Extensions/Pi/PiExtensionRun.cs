// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/resource-loader.ts (reload with resolveProjectTrust:
// loadProjectTrustExtensions, then loadFinalExtensionSet ordered as the final extension paths) and
// packages/coding-agent/src/core/agent-session-services.ts (applyExtensionFlagValues).
using System.Collections.Immutable;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>The extensions of one Pi-style run, from discovery to the session: user and CLI extensions before trust, project
/// extensions after a trusted decision, then the command-line flag values.</summary>
internal sealed class PiExtensionRun : IAsyncDisposable
{
    private readonly PiExtensionLoading _loading = new();
    private readonly List<PiLoadedExtensions> _registries = [];
    private readonly PiHost _host;
    private readonly PiArgs _parsed;
    private readonly string _cwd, _agentDir, _home, _mode;
    private readonly bool _hasUI;
    private readonly TextWriter? _stderr;

    private PiExtensionRun(PiHost host, PiArgs parsed, string cwd, string agentDir, string home, string mode, bool hasUI, TextWriter? stderr)
    { _host = host; _parsed = parsed; _cwd = cwd; _agentDir = agentDir; _home = home; _mode = mode; _hasUI = hasUI; _stderr = stderr; }

    internal PiExtensionHost? Host => _loading.Host;
    internal IReadOnlyList<PiDiagnostic> Diagnostics => _loading.Diagnostics;
    internal bool HasLoadErrors => _loading.HasLoadErrors;

    internal static async Task<PiExtensionRun> LoadAsync(PiHost host, PiArgs parsed, string cwd, string agentDir, string home, bool projectTrusted,
        string mode, bool hasUI, TextWriter? stderr, CancellationToken token)
    {
        var run = new PiExtensionRun(host, parsed, cwd, agentDir, home, mode, hasUI, stderr);
        await run.LoadSourcesAsync(PiSettings.Load(cwd, agentDir, projectTrusted), projectTrusted, token).ConfigureAwait(false);
        return run;
    }

    /// <summary>loadFinalExtensionSet: the trusted project's extensions load into the same runtime; the order follows the final paths.</summary>
    internal Task LoadProjectAsync(PiSettings settings, CancellationToken token) => LoadSourcesAsync(settings, true, token);

    private async Task LoadSourcesAsync(PiSettings settings, bool projectTrusted, CancellationToken token)
    {
        ImmutableArray<PiExtensionSource> sources;
        try { sources = await ResolveSourcesAsync(settings, token).ConfigureAwait(false); }
        catch (PiSharp.Cli.Packages.PiPackageException error) { _loading.Add([new("error", error.Message)]); return; }
        await _loading.LoadAsync(sources, () => new PiExtensionHostOptions(_cwd, _agentDir, _mode, _hasUI)
        {
            GetEnvironment = _host.GetEnvironment,
            StandardError = _stderr is null ? null : line => { lock (_stderr) { _stderr.Write(line + "\n"); _stderr.Flush(); } },
            Theme = settings.Theme,
            ProjectTrusted = _ => projectTrusted
        }, token).ConfigureAwait(false);
        Host?.Reorder(sources.Select(source => source.Path));
    }

    /// <summary>resource-loader.ts reload: <c>packageManager.resolveExtensionSources(-e paths, temporary)</c> first, then (unless
    /// <c>--no-extensions</c>) the enabled extensions of <c>packageManager.resolve()</c> (settings entries, auto-discovered folders and
    /// packages in upstream's precedence; project ones only for a trusted project), each path once. Built-in extensions are PiSharp's own.</summary>
    private async Task<ImmutableArray<PiExtensionSource>> ResolveSourcesAsync(PiSettings settings, CancellationToken token)
    {
        var output = _stderr ?? TextWriter.Null;
        var manager = new PiSharp.Cli.Packages.PiPackageManager(_cwd, _agentDir, _home, settings, _host.GetEnvironment,
            _host.PackageProcesses?.Invoke(output, output) ?? new() { Output = output, ErrorOutput = output });
        var cli = _parsed.Extensions is { Count: > 0 } paths
            ? await manager.ResolveExtensionSourcesAsync(paths, temporary: true, cancellationToken: token).ConfigureAwait(false)
            : PiSharp.Cli.Packages.PiResolvedPaths.Empty;
        var resolved = _parsed.NoExtensions ? PiSharp.Cli.Packages.PiResolvedPaths.Empty : await manager.ResolveAsync(cancellationToken: token).ConfigureAwait(false);
        var seen = new HashSet<string>(PiPaths.Comparer);
        var list = ImmutableArray.CreateBuilder<PiExtensionSource>();
        foreach (var resource in cli.Extensions.Concat(resolved.Extensions))
        {
            if (!resource.Enabled || resource.Path.StartsWith("builtin:", StringComparison.Ordinal)) continue;
            if (seen.Add(Path.GetFullPath(resource.Path))) list.Add(new(resource.Path, resource.Metadata.Scope, resource.Metadata.Source));
        }
        // A local -e path that does not exist is still reported (Extension path does not exist).
        foreach (var path in _parsed.Extensions ?? [])
            if (PiPaths.IsLocalPath(path) && PiPaths.ResolvePath(path, _cwd, _home, trim: true) is var local && !Path.Exists(local) && seen.Add(Path.GetFullPath(local)))
                list.Add(new(local, "temporary", "cli"));
        return list.ToImmutable();
    }

    internal async Task ApplyFlagValuesAsync(IReadOnlyDictionary<string, string?> values, CancellationToken token)
    {
        if (Host is null)
        {
            if (values.Count > 0) _loading.Add([new("error", $"Unknown option{(values.Count == 1 ? "" : "s")}: {string.Join(", ", values.Keys.Select(name => "--" + name))}")]);
            return;
        }
        _loading.Add(await Host.ApplyFlagValuesAsync(values, token).ConfigureAwait(false));
    }

    internal async Task<PiLoadedExtensions> PreSessionAsync(CancellationToken token)
    {
        var loaded = await Host!.PreSessionAsync(token).ConfigureAwait(false);
        _registries.Add(loaded);
        return loaded;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries) await registry.Registry.DisposeAsync().ConfigureAwait(false);
        if (Host is not null) await Host.DisposeAsync().ConfigureAwait(false);
    }
}
