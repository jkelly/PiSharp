// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (reload: session_shutdown with
// reason "reload", the old runner invalidated, resources and extensions reloaded with the previous flag values, the runtime rebuilt
// by _buildRuntime with a new ExtensionRunner over the loaded extensions and _refreshToolRegistry, session_start with reason
// "reload") and packages/coding-agent/src/core/extensions/loader.ts (fresh modules on each load).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions.Pi;

internal sealed partial class PiExtensionHost
{
    /// <summary>The extension runtime generation: 0 for the first load, incremented by every reload (owner ids carry it).</summary>
    private int _runtimeGeneration;
    /// <summary>Owners of a previous runtime whose tools still leave the session's catalog before their registry owner is disposed.</summary>
    private readonly List<PiNodeOwner> _retired = [];
    /// <summary>Every registry owner id this host activated, with its extension path (MCP servers and diagnostics report paths).</summary>
    private readonly Dictionary<string, string> _ownerPaths = new(StringComparer.Ordinal);

    /// <summary>The extension entry points a reload loads (packageManager.resolve() again); the loaded paths when unset.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<string>>>? ResolveReloadPaths { get; set; }

    /// <summary>The mode's reload (ctx.reload(), /reload): set by the session host.</summary>
    internal Func<CancellationToken, Task>? Reload { get; set; }

    /// <summary>Loads the extensions again in a fresh Node runtime (the old pi and ctx objects become stale) and rebuilds the extension
    /// runtime of the session, as _buildRuntime creates a new runner: the previous runtime's owners withdraw their commands, handlers,
    /// renderers and MCP servers at once and leave the registry; one new owner per loaded extension takes their place; the tools are
    /// replaced in the session's catalog (_refreshToolRegistry). Returns the load errors.</summary>
    internal async Task<ImmutableArray<PiExtensionLoadError>> ReloadExtensionsAsync(CancellationToken token)
    {
        var paths = ResolveReloadPaths is { } resolve ? await resolve(token).ConfigureAwait(false)
            : [.. ActivationOrder().Select(item => item.Node?.Path ?? item.Native!.Path)];
        lock (_providers) _providers.Clear();
        lock (_virtualModels) _virtualModels.Clear();
        lock (_mcpServers) _mcpServers.Clear();
        var nodePaths = paths.Where(path => !PiNativeExtension.IsManifest(path)).ToList();
        if (_node is null && nodePaths.Count > 0) await StartNodeAsync(token).ConfigureAwait(false);
        // As for a load: the reloaded factories' registrations (notifications sent before the result) are handled before the new owners activate.
        JsonElement? result = _node is null ? null : await Node.RequestAsync("reload", new JsonObject { ["paths"] = new JsonArray([.. nodePaths.Select(path => (JsonNode)path)]) }, token,
            afterPrecedingFrames: true).ConfigureAwait(false) ?? throw new InvalidOperationException("The Node extension host returned no reload result.");
        var errors = ImmutableArray.CreateBuilder<PiExtensionLoadError>();
        ImmutableArray<PiNodeOwner> previous;
        int generation;
        lock (_extensions)
        {
            generation = ++_runtimeGeneration;
            _extensions.Clear(); _errors.Clear();
            _order = [.. paths.Select(Path.GetFullPath)];
            if (result is { } reloaded)
            {
                foreach (var item in reloaded.GetProperty("results").EnumerateArray())
                {
                    var path = item.GetProperty("path").GetString()!;
                    if (item.TryGetProperty("error", out var error)) { var failure = new PiExtensionLoadError(path, error.GetString() ?? "Failed to load extension"); _errors.Add(failure); errors.Add(failure); continue; }
                    var descriptor = JsonNode.Parse(item.GetProperty("extension").GetRawText())!.AsObject();
                    _extensions.Add(new(descriptor["index"]!.GetValue<int>(), path, descriptor["resolvedPath"]!.GetValue<string>()) { Descriptor = descriptor, Generation = generation });
                }
                foreach (var flag in reloaded.GetProperty("flagValues").EnumerateObject()) _flagValues[flag.Name] = JsonNode.Parse(flag.Value.GetRawText());
            }
            previous = _owners; _owners = [];
        }
        foreach (var error in errors) await ReportAsync(error.Path, "reload", error.Error).ConfigureAwait(false);
        // The previous runtime's registrations leave at once (its tools with the catalog replacement below); native extensions load
        // again into fresh load contexts.
        foreach (var owner in previous) owner.Retire();
        lock (_retired) _retired.AddRange(previous);
        await ReloadNativeAsync(paths.Where(PiNativeExtension.IsManifest), generation, token).ConfigureAwait(false);
        if (_activation?.Registry is { } registry)
        {
            var names = CommandInvocationNames();
            var owners = ImmutableArray.CreateBuilder<PiNodeOwner>();
            foreach (var (extension, native) in ActivationOrder())
            {
                if (native is not null)
                {
                    native.LateActivation = true;
                    native.Scope = await ActivateNativeAsync(registry, native, token).ConfigureAwait(false);
                    continue;
                }
                var owner = new PiNodeOwner(this, extension!, names) { DeferTools = true };
                lock (_ownerPaths) _ownerPaths[extension!.OwnerId] = extension.Path;
                try { await registry.ActivateAsync(extension.OwnerId, owner, token).ConfigureAwait(false); owners.Add(owner); }
                catch (ExtensionRegistrationException error) { await ReportAsync(extension.Path, "reload", error.Message).ConfigureAwait(false); }
            }
            lock (_extensions) _owners = owners.ToImmutable();
        }
        // The tools reach the session catalog once the reload's own input or command settles (the previous runtime's leave first).
        _ = SyncRegistrationsAsync(-1, force: true);
        RegistrationsChanged?.Invoke();
        return errors.ToImmutable();
    }

    /// <summary>The current runtime's owner of an extension path (null before the session bound it).</summary>
    private PiNodeOwner? OwnerOf(string? path)
    {
        if (path is null) return null;
        lock (_extensions) return _owners.FirstOrDefault(owner => PiSharp.Cli.Pi.PiPaths.Comparer.Equals(owner.Path, path));
    }

    /// <summary>Removes the retired owners' tools from the session's catalog, then disposes their registry owners (detached: a reload
    /// started by one of their commands settles first).</summary>
    private async Task RetireOwnersAsync(NativeExtensionActivation activation, PiSharp.CodingAgent.ReplaceableAgentSession session)
    {
        PiNodeOwner[] retired;
        lock (_retired) { retired = [.. _retired]; _retired.Clear(); }
        foreach (var owner in retired)
        {
            try { await owner.RetireToolsAsync(activation, session, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidOperationException or ExtensionRegistrationException or
                PiSharp.CodingAgent.SessionRuntimeRegistryException or OperationCanceledException or ObjectDisposedException)
            { await ReportAsync(owner.Path, "reload", error.Message).ConfigureAwait(false); }
            if (owner.Scope is { } scope)
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // The callbacks still running (the command that reloaded) finish before the owner's lifetime ends.
                            using var quiescent = await scope.QuiesceAsync().ConfigureAwait(false);
                            await scope.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception) { /* A stale owner's cleanup is not the session's. */ }
                    });
        }
    }
}
