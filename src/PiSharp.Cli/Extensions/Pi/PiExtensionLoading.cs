// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/resource-loader.ts (loadProjectTrustExtensions: the
// pre-trust pass loads user and CLI extensions with project settings forced untrusted; loadFinalExtensionSet: the remaining (project)
// extensions load after trust into the same runtime, preloaded ones are kept; reload: "Extension path does not exist" for a missing
// local -e path) and packages/coding-agent/src/main.ts (Failed to load extension "<path>": <error>; EXTENSION_LOAD_FAILURE_HINT).
using System.Collections.Immutable;
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Extensions.Pi;

/// <summary>The extension loading of one Pi-style run.</summary>
internal sealed class PiExtensionLoading
{
    internal const string LoadFailureHint = "Hint: Start without extensions using \"" + PiConfig.DisplayName + " -ne\".";

    private readonly HashSet<string> _requested = new(PiPaths.Comparer);
    private readonly List<PiDiagnostic> _diagnostics = [];
    internal PiExtensionHost? Host { get; private set; }
    internal IReadOnlyList<PiDiagnostic> Diagnostics => _diagnostics;
    internal bool HasLoadErrors => _diagnostics.Any(diagnostic => diagnostic.Type == "error" && diagnostic.Message.StartsWith("Failed to load extension", StringComparison.Ordinal));

    /// <summary>Loads the sources not loaded yet (in order), starting the Node host on first need. Load failures become diagnostics.</summary>
    internal async Task LoadAsync(IReadOnlyList<PiExtensionSource> sources, Func<PiExtensionHostOptions> options, CancellationToken token)
    {
        var fresh = sources.Where(source => _requested.Add(Path.GetFullPath(source.Path))).ToList();
        var missing = fresh.Where(source => !File.Exists(source.Path)).ToList();
        foreach (var source in missing) _diagnostics.Add(new("error", $"Failed to load extension \"{source.Path}\": Extension path does not exist: {source.Path}"));
        var paths = fresh.Except(missing).Select(source => source.Path).ToList();
        if (paths.Count == 0) return;
        // Native C# extensions need no Node: a run with only those starts Node once a TypeScript or JavaScript extension loads.
        var nodePaths = paths.Where(path => !PiNativeExtension.IsManifest(path)).ToList();
        Host ??= nodePaths.Count == 0 ? PiExtensionHost.CreateWithoutNode(options()) : null;
        if (Host is null || nodePaths.Count > 0 && !Host.IsRunning)
        {
            try
            {
                if (Host is null) Host = await PiExtensionHost.StartAsync(options(), token).ConfigureAwait(false);
                else await Host.EnsureNodeAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is PiExtensionHostUnavailableException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                foreach (var path in nodePaths) _diagnostics.Add(new("error", $"Failed to load extension \"{path}\": {error.Message}"));
                paths = [.. paths.Except(nodePaths)];
                if (Host is null) { if (paths.Count == 0) return; Host = PiExtensionHost.CreateWithoutNode(options()); }
            }
            if (Host.RuntimeFallback is { } fallback) _diagnostics.Add(new("warning", "Extensions are not running against the Pi " + PiNodeRuntime.PiVersion + " packages: " + fallback));
        }
        foreach (var source in fresh) Host.SetSourceInfo(source.Path, source.SourceInfo());
        var before = Host.Errors.Length;
        await Host.LoadAsync(paths, token).ConfigureAwait(false);
        foreach (var error in Host.Errors.Skip(before)) _diagnostics.Add(new("error", $"Failed to load extension \"{error.Path}\": {error.Error}"));
        _diagnostics.RemoveAll(diagnostic => diagnostic.Message.Contains("\" conflicts with ", StringComparison.Ordinal));
        foreach (var conflict in Host.Conflicts()) _diagnostics.Add(new("error", $"Failed to load extension \"{conflict.Path}\": {conflict.Error}"));
    }

    internal void Add(IEnumerable<PiDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}
