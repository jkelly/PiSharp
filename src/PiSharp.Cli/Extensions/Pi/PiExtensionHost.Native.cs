// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/loader.ts (loadExtensions: every path in
// order, a failure recorded and the next path loaded) and packages/coding-agent/src/core/agent-session.ts (reload, _buildRuntime:
// the runner over the reloaded extensions). The native C# extensions of the run are PiSharp's own (owner decision 10).
using System.Collections.Immutable;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Extensions.Pi;

internal sealed partial class PiExtensionHost
{
    private readonly List<PiNativeExtension> _natives = [];
    private readonly List<PiNativeExtension> _retiredNatives = [];
    private ImmutableArray<string> _order = [];
    private int _nextNative;

    /// <summary>The native C# extensions loaded (in load order).</summary>
    internal ImmutableArray<PiNativeExtension> NativeExtensions { get { lock (_extensions) return [.. _natives]; } }

    /// <summary>Starts Node for a host created without it (the first TypeScript or JavaScript extension of the run).</summary>
    internal Task EnsureNodeAsync(CancellationToken token) => _node is null ? StartNodeAsync(token) : Task.CompletedTask;

    /// <summary>Loads native C# extension manifests (fresh load contexts); failures become load errors.</summary>
    private List<PiNativeExtension> LoadNative(IEnumerable<string> manifests, int generation)
    {
        var loaded = new List<PiNativeExtension>();
        foreach (var path in manifests)
        {
            int index;
            lock (_extensions) index = _nextNative++;
            try
            {
                var native = PiNativeExtension.Load(path, index, generation);
                lock (_extensions) _natives.Add(native);
                lock (_ownerPaths) _ownerPaths[native.OwnerId] = native.Path;
                loaded.Add(native);
            }
            catch (InvalidOperationException error) { lock (_extensions) _errors.Add(new(path, error.Message)); }
        }
        return loaded;
    }

    /// <summary>The Node and native extensions in the final extension path order (loadFinalExtensionSet); unlisted ones at the end.</summary>
    private List<(PiLoadedExtension? Node, PiNativeExtension? Native)> ActivationOrder()
    {
        lock (_extensions)
        {
            var order = _order;
            int Rank(string path) { var index = order.IndexOf(Path.GetFullPath(path), PiSharp.Cli.Pi.PiPaths.Comparer); return index < 0 ? int.MaxValue : index; }
            return [.. _extensions.Select((extension, position) => (Item: ((PiLoadedExtension?)extension, (PiNativeExtension?)null), Rank: Rank(extension.ResolvedPath), Position: position))
                .Concat(_natives.Select((native, position) => (Item: ((PiLoadedExtension?)null, (PiNativeExtension?)native), Rank: Rank(native.Path), Position: _extensions.Count + position)))
                .OrderBy(item => item.Rank).ThenBy(item => item.Position).Select(item => item.Item)];
        }
    }

    /// <summary>Activates one native extension in the registry with a fresh instance; a failure is reported as the extension's error.</summary>
    private async Task<RegistrationScope?> ActivateNativeAsync(ExtensionRegistry registry, PiNativeExtension native, CancellationToken token)
    {
        try { return await registry.ActivateAsync(native.OwnerId, native.CreateInstance(), token).ConfigureAwait(false); }
        catch (Exception error) when (error is ExtensionRegistrationException or InvalidOperationException or System.Reflection.TargetInvocationException or MissingMethodException)
        {
            var message = error is ExtensionRegistrationException { InnerException: { } inner } ? inner.Message : error.InnerException?.Message ?? error.Message;
            await ReportAsync(native.Path, "load", message).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Reload: the native extensions load again into fresh load contexts; the previous ones retire.</summary>
    private async Task ReloadNativeAsync(IEnumerable<string> manifests, int generation, CancellationToken token)
    {
        PiNativeExtension[] previous;
        lock (_extensions) { previous = [.. _natives]; _natives.Clear(); }
        foreach (var native in previous)
        {
            // The names the session's catalog holds leave it with the next catalog replacement; the registry drops every registration
            // at once, so the reloaded extension registers the same names.
            native.CatalogToolNames = native.LateActivation ? native.PublishedToolNames
                : native.Scope is { } scope && _activation?.Registry is { } registry ? [.. registry.CaptureOwnedTools(scope).Select(tool => tool.Descriptor.Name)] : [];
            native.Retire();
            // Its classifier and image providers leave with it (the reloaded generation registers its own).
            NativeModelProviders.UnregisterOwner(native.OwnerId);
        }
        lock (_retiredNatives) _retiredNatives.AddRange(previous);
        LoadNative(manifests, generation);
        token.ThrowIfCancellationRequested();
        foreach (var error in Errors.Where(error => PiNativeExtension.IsManifest(error.Path))) await ReportAsync(error.Path, "reload", error.Error).ConfigureAwait(false);
    }

    /// <summary>The retired native extensions' tools leave the catalog, then their owners are disposed and their code unloaded; the
    /// natives activated by a reload publish their tools.</summary>
    private async Task SyncNativeToolsAsync(NativeExtensionActivation activation, PiSharp.CodingAgent.ReplaceableAgentSession session)
    {
        PiNativeExtension[] retired;
        lock (_retiredNatives) { retired = [.. _retiredNatives]; _retiredNatives.Clear(); }
        foreach (var native in retired)
        {
            if (native.Scope is { } scope)
            {
                try
                {
                    if (!native.CatalogToolNames.IsEmpty)
                        await activation.PublishPiToolsAsync(session, scope, [], native.CatalogToolNames, [], CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidOperationException or ExtensionRegistrationException or
                    PiSharp.CodingAgent.SessionRuntimeRegistryException or OperationCanceledException or ObjectDisposedException)
                { await ReportAsync(native.Path, "reload", error.Message).ConfigureAwait(false); }
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var quiescent = await scope.QuiesceAsync().ConfigureAwait(false);
                            await scope.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception) { /* A stale owner's cleanup is not the session's. */ }
                        finally { native.Context.Unload(); }
                    });
            }
            else native.Context.Unload();
        }
        foreach (var native in NativeExtensions.Where(native => native.LateActivation && native.Scope is not null && native.PublishedToolIds.IsEmpty))
        {
            var scope = native.Scope!;
            try
            {
                var owned = activation.Registry.CaptureOwnedTools(scope);
                if (owned.IsEmpty) continue;
                await activation.PublishPiToolsAsync(session, scope, [.. owned.Select(tool => tool.RegistrationId)], [], [.. owned.Select(tool => tool.Descriptor)],
                    CancellationToken.None).ConfigureAwait(false);
                native.PublishedToolIds = [.. owned.Select(tool => tool.RegistrationId)];
                native.PublishedToolNames = [.. owned.Select(tool => tool.Descriptor.Name)];
            }
            catch (Exception error) when (error is InvalidOperationException or ExtensionRegistrationException or
                PiSharp.CodingAgent.SessionRuntimeRegistryException or OperationCanceledException or ObjectDisposedException)
            { await ReportAsync(native.Path, "reload", error.Message).ConfigureAwait(false); }
        }
    }
}
