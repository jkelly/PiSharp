// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (reload: session_shutdown with
// reason "reload", the old runner invalidated, resources and extensions reloaded with the previous flag values, the runtime rebuilt,
// session_start with reason "reload") and packages/coding-agent/src/core/extensions/loader.ts (fresh modules on each load).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Extensions.Pi;

internal sealed partial class PiExtensionHost
{
    /// <summary>Registry owners kept free for extensions a reload adds (owners are bound to the session once).</summary>
    internal const int SpareOwners = 4;
    private ImmutableArray<PiLoadedExtension> _spares = [];
    private readonly Dictionary<int, long> _ownerGenerations = [];

    /// <summary>The extension entry points a reload loads (packageManager.resolve() again); the loaded paths when unset.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<string>>>? ResolveReloadPaths { get; set; }

    /// <summary>The mode's reload (ctx.reload(), /reload): set by the session host.</summary>
    internal Func<CancellationToken, Task>? Reload { get; set; }

    /// <summary>The extension an owner slot holds now (a reload replaces the entries); a spare slot's placeholder when empty.</summary>
    internal PiLoadedExtension? Slot(int index)
    {
        lock (_extensions)
            return _extensions.FirstOrDefault(extension => extension.Index == index) ?? _spares.FirstOrDefault(extension => extension.Index == index);
    }

    private ImmutableArray<PiLoadedExtension> CreateSpares()
    {
        lock (_extensions)
        {
            var next = _extensions.Count == 0 ? 0 : _extensions.Max(extension => extension.Index) + 1;
            _spares = [.. Enumerable.Range(next, SpareOwners).Select(index => new PiLoadedExtension(index, "", "") { Descriptor = new JsonObject() })];
            return _spares;
        }
    }

    /// <summary>Loads the extensions again in a fresh Node runtime (the old pi and ctx objects become stale), then brings the session's
    /// registrations in line: commands, tools and handlers of every owner. Returns the load errors.</summary>
    internal async Task<ImmutableArray<PiExtensionLoadError>> ReloadExtensionsAsync(CancellationToken token)
    {
        var paths = ResolveReloadPaths is { } resolve ? await resolve(token).ConfigureAwait(false) : [.. Extensions.Select(extension => extension.Path)];
        lock (_providers) _providers.Clear();
        lock (_virtualModels) _virtualModels.Clear();
        lock (_mcpServers) _mcpServers.Clear();
        var result = await Node.RequestAsync("reload", new JsonObject { ["paths"] = new JsonArray([.. paths.Select(path => (JsonNode)path)]) }, token)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The Node extension host returned no reload result.");
        var errors = ImmutableArray.CreateBuilder<PiExtensionLoadError>();
        int owners;
        lock (_extensions)
        {
            foreach (var extension in _extensions.Concat(_spares)) if (extension.OwnerGeneration > 0) _ownerGenerations[extension.Index] = extension.OwnerGeneration;
            _extensions.Clear(); _errors.Clear();
            foreach (var item in result.GetProperty("results").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString()!;
                if (item.TryGetProperty("error", out var error)) { var failure = new PiExtensionLoadError(path, error.GetString() ?? "Failed to load extension"); _errors.Add(failure); errors.Add(failure); continue; }
                var descriptor = JsonNode.Parse(item.GetProperty("extension").GetRawText())!.AsObject();
                var index = descriptor["index"]!.GetValue<int>();
                _extensions.Add(new(index, path, descriptor["resolvedPath"]!.GetValue<string>())
                    { Descriptor = descriptor, OwnerGeneration = _ownerGenerations.GetValueOrDefault(index) });
            }
            foreach (var flag in result.GetProperty("flagValues").EnumerateObject()) _flagValues[flag.Name] = JsonNode.Parse(flag.Value.GetRawText());
            owners = _owners.Length;
        }
        foreach (var error in errors) await ReportAsync(error.Path, "reload", error.Error).ConfigureAwait(false);
        foreach (var extension in Extensions.Where(extension => extension.Index >= owners))
            await ReportAsync(extension.Path, "reload", "Loaded by reload, but no session slot is left for it; restart to use it").ConfigureAwait(false);
        // Commands now; the tools reach the session catalog once the reload's own input or command settles.
        _ = SyncRegistrationsAsync(-1, force: true);
        RegistrationsChanged?.Invoke();
        return errors.ToImmutable();
    }
}
