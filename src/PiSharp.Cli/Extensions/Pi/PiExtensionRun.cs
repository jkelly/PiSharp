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
        var sources = PiExtensionDiscovery.Resolve(_cwd, _agentDir, _home, settings, projectTrusted, _parsed.Extensions ?? [], _parsed.NoExtensions,
            PackageExtensions?.Invoke(settings, projectTrusted));
        await _loading.LoadAsync(sources, () => new PiExtensionHostOptions(_cwd, _agentDir, _mode, _hasUI)
        {
            GetEnvironment = _host.GetEnvironment,
            StandardError = _stderr is null ? null : line => { lock (_stderr) { _stderr.Write(line + "\n"); _stderr.Flush(); } },
            Theme = settings.Theme,
            ProjectTrusted = _ => projectTrusted
        }, token).ConfigureAwait(false);
        Host?.Reorder(sources.Select(source => source.Path));
    }

    /// <summary>Extensions installed by packages (IMPL-E package manager): settings <c>packages</c> resolved for the trust state.</summary>
    internal static Func<PiSettings, bool, IReadOnlyList<PiExtensionSource>>? PackageExtensions { get; set; }

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
