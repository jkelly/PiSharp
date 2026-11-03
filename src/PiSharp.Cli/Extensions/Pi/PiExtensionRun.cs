// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/resource-loader.ts (reload with resolveProjectTrust:
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
    private readonly HashSet<string> _installWarnings = new(StringComparer.Ordinal);

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
        ImmutableArray<PiSharp.Cli.Packages.PiPackageInstallFailure> installFailures;
        try { (sources, installFailures) = await ResolveSourcesAsync(settings, token).ConfigureAwait(false); }
        catch (PiSharp.Cli.Packages.PiPackageException error) { _loading.Add([new("error", error.Message)]); return; }
        // A configured package that failed to install is skipped with a warning; the next start or reload tries it again.
        _loading.Add([.. installFailures.Select(FormatPackageInstallWarning).Where(_installWarnings.Add).Select(warning => new PiDiagnostic("warning", warning))]);
        await _loading.LoadAsync(sources, () => new PiExtensionHostOptions(_cwd, _agentDir, _mode, _hasUI)
        {
            GetEnvironment = _host.GetEnvironment,
            StandardError = _stderr is null ? null : line => { lock (_stderr) { _stderr.Write(line + "\n"); _stderr.Flush(); } },
            Theme = settings.Theme,
            ProjectTrusted = _ => projectTrusted
        }, token).ConfigureAwait(false);
        Host?.Reorder(sources.Select(source => source.Path));
        if (Host is { } loaded) loaded.PackageWarnings = [.. loaded.PackageWarnings.Concat(InstallWarnings(installFailures)).Distinct()];
        // agent-session.ts reload(): the resource loader resolves the extension paths again (settings, packages, auto-discovery).
        if (Host is { } host)
            host.ResolveReloadPaths ??= async reloadToken =>
            {
                var (reloaded, reloadFailures) = await ResolveSourcesAsync(PiSettings.Load(_cwd, _agentDir, host.ProjectTrusted ?? projectTrusted), reloadToken).ConfigureAwait(false);
                host.PackageWarnings = InstallWarnings(reloadFailures);
                foreach (var source in reloaded) host.SetSourceInfo(source.Path, source.SourceInfo());
                return [.. reloaded.Where(source => File.Exists(source.Path)).Select(source => source.Path)];
            };
    }

    /// <summary>resource-loader.ts reload: <c>packageManager.resolveExtensionSources(-e paths, temporary)</c> first, then (unless
    /// <c>--no-extensions</c>) the enabled extensions of <c>packageManager.resolve()</c> (settings entries, auto-discovered folders and
    /// packages in upstream's precedence; project ones only for a trusted project), each path once. Built-in extensions are PiSharp's own.</summary>
    private async Task<(ImmutableArray<PiExtensionSource> Sources, ImmutableArray<PiSharp.Cli.Packages.PiPackageInstallFailure> InstallFailures)> ResolveSourcesAsync(
        PiSettings settings, CancellationToken token)
    {
        var output = _stderr ?? TextWriter.Null;
        var manager = new PiSharp.Cli.Packages.PiPackageManager(_cwd, _agentDir, _home, settings, _host.GetEnvironment,
            _host.PackageProcesses?.Invoke(output, output) ?? new() { Output = output, ErrorOutput = output });
        var cli = _parsed.Extensions is { Count: > 0 } paths
            ? await manager.ResolveExtensionSourcesAsync(paths, temporary: true, cancellationToken: token).ConfigureAwait(false)
            : PiSharp.Cli.Packages.PiResolvedPaths.Empty;
        var resolved = _parsed.NoExtensions ? PiSharp.Cli.Packages.PiResolvedPaths.Empty : await manager.ResolveAsync(cancellationToken: token).ConfigureAwait(false);
        // Source mergePaths: each path once by getResourcePathKey.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = ImmutableArray.CreateBuilder<PiExtensionSource>();
        foreach (var resource in cli.Extensions.Concat(resolved.Extensions))
        {
            if (!resource.Enabled || resource.Path.StartsWith("builtin:", StringComparison.Ordinal)) continue;
            if (seen.Add(PiSourceInfo.ResourcePathKey(resource.Path)))
                list.Add(new(resource.Path, resource.Metadata.Scope, resource.Metadata.Source, resource.Metadata.Origin, resource.Metadata.BaseDir, resource.Metadata.ChangelogPath));
        }
        // A local -e path that does not exist is still reported (Extension path does not exist).
        foreach (var path in _parsed.Extensions ?? [])
            if (PiPaths.IsLocalPath(path) && PiPaths.ResolvePath(path, _cwd, _home, trim: true) is var local && !Path.Exists(local) && seen.Add(PiSourceInfo.ResourcePathKey(local)))
                list.Add(new(local, "temporary", "cli"));
        return (list.ToImmutable(), [.. resolved.InstallFailures, .. cli.InstallFailures]);
    }

    /// <summary>Source describePackageInstallFailure: why a package was skipped and how to drop it from settings.json.</summary>
    internal static string DescribePackageInstallFailure(PiSharp.Cli.Packages.PiPackageInstallFailure failure)
    {
        var hint = failure.Scope == "temporary" ? "Check the source and try again."
            : $"Run \"{PiConfig.DisplayName} remove {failure.Source}{(failure.Scope == "project" ? " --local" : "")}\" to remove it from settings.json.";
        return $"failed to install ({failure.Message}). {hint}";
    }

    /// <summary>Source formatPackageInstallWarning: the one-line startup warning of a package that could not be installed.</summary>
    internal static string FormatPackageInstallWarning(PiSharp.Cli.Packages.PiPackageInstallFailure failure) =>
        $"Extension package \"{failure.Source}\": {DescribePackageInstallFailure(failure)}";

    private static ImmutableArray<(string Path, string Warning)> InstallWarnings(ImmutableArray<PiSharp.Cli.Packages.PiPackageInstallFailure> failures) =>
        [.. failures.Select(failure => (failure.Source, DescribePackageInstallFailure(failure)))];

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
