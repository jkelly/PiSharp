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
        if (Host is null)
        {
            try { Host = await PiExtensionHost.StartAsync(options(), token).ConfigureAwait(false); }
            catch (Exception error) when (error is PiExtensionHostUnavailableException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                foreach (var path in paths) _diagnostics.Add(new("error", $"Failed to load extension \"{path}\": {error.Message}"));
                return;
            }
        }
        var before = Host.Errors.Length;
        await Host.LoadAsync(paths, token).ConfigureAwait(false);
        foreach (var error in Host.Errors.Skip(before)) _diagnostics.Add(new("error", $"Failed to load extension \"{error.Path}\": {error.Error}"));
    }

    internal void Add(IEnumerable<PiDiagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}
