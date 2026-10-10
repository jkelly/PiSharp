using PiSharp.Contracts;
using PiSharp.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly Dictionary<ExtensionSessionSnapshot, ProfileRuntimeView> shutdownViews = new(ReferenceEqualityComparer.Instance);
    public JsonData CommandCatalog
    {
        get { var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter(); return WithBuiltinCommands(view.CommandCatalog); }
    }

    /// <summary>The session's catalog without the built-in extensions' commands (the extensions, prompt templates and skills).</summary>
    private System.Text.Json.JsonElement ExtensionCommandCatalog()
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter(); return view.CommandCatalog.Value;
    }

    /// <summary>rpc-mode.ts get_commands (and pi.getCommands()): extensionRunner.getRegisteredCommands() includes the commands of the
    /// loaded built-in extensions (<c>/llama</c>, <c>/mcp</c>), which load after the file extensions, with their <c>builtin:</c> sourceInfo.
    /// They follow the extension commands, before the prompt templates and skills. Only the Pi entry has built-in extensions.</summary>
    private JsonData WithBuiltinCommands(JsonData catalog)
    {
        if (BuiltinExtensions is not { } builtins || catalog.Value.ValueKind != System.Text.Json.JsonValueKind.Array) return catalog;
        var rows = System.Text.Json.Nodes.JsonNode.Parse(catalog.Value.GetRawText(), documentOptions: JsonData.DocumentOptions) as System.Text.Json.Nodes.JsonArray;
        if (rows is null) return catalog;
        var names = rows.OfType<System.Text.Json.Nodes.JsonObject>().Select(row => row["name"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var added = PiSharp.Cli.Extensions.Pi.PiBuiltinExtensions.Commands.Where(command => builtins.IsEnabled(command.Builtin) && !names.Contains(command.Name)).ToList();
        if (added.Count == 0) return catalog;
        var index = 0;
        while (index < rows.Count && rows[index]?["source"]?.GetValue<string>() == "extension") index++;
        foreach (var (builtin, name, description) in added)
            rows.Insert(index++, new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = name, ["description"] = description, ["source"] = "extension",
                ["sourceInfo"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["path"] = PiSharp.Cli.Extensions.Pi.PiBuiltinExtensions.PathPrefix + builtin, ["source"] = "builtin",
                    ["scope"] = builtins.ScopeOf(builtin), ["origin"] = "top-level"
                }
            });
        return JsonData.Parse(rows.ToJsonString());
    }
    public async ValueTask<JsonData> CompleteCommandAsync(string name, string prefix, CancellationToken token)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        if (view.Extension is null) throw new InvalidOperationException("No active extension command revision.");
        return await view.Extension.CompleteCommandAsync(name, prefix, token).ConfigureAwait(false);
    }
    /// <summary>IMPL-I: the renderers extensions registered for <paramref name="toolName"/> (ExtensionRunner.resolveToolRenderers), for
    /// the interactive mode's tool rows; null without extensions or renderers.</summary>
    internal ExtensionToolRenderers? ResolveExtensionToolRenderers(string toolName)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        return view.Extension?.ResolveToolRenderers(toolName);
    }
    internal async ValueTask StartLifecycleAsync(string reason, CancellationToken token)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        if (view.Extension is not null) await view.Extension.DispatchSessionStartAsync(reason, token).ConfigureAwait(false);
    }
    internal ExtensionSessionSnapshot? CaptureShutdownSessionSnapshot()
    {
        var attachment = Sessions?.Current;
        var view = CaptureRuntimeView(attachment);
        var extension = view.Extension;
        // Startup rejection may already have retired the acquired runtime hold.
        // An empty view has no native shutdown callback or snapshot to borrow.
        if (extension is null) return null;
        using var use = view.Lifetime.Enter();
        var snapshot = extension.CaptureShutdownSessionSnapshot(attachment);
        if (snapshot is not null) lock (viewGate) shutdownViews[snapshot] = view;
        return snapshot;
    }
    internal async ValueTask<bool> DispatchSessionShutdownAsync(ExtensionSessionSnapshot? retained)
    {
        ProfileRuntimeView view;
        if (retained is not null)
        {
            lock (viewGate)
                view = shutdownViews.TryGetValue(retained, out var captured) ? captured :
                    throw new InvalidOperationException("Shutdown snapshot has no captured profile view.");
        }
        else view = CaptureRuntimeView();
        var extension = view.Extension;
        if (extension is null) return false;
        using var use = view.Lifetime.Enter();
        return await extension.DispatchSessionShutdownAsync(retained).ConfigureAwait(false);
    }
    internal async ValueTask DrainLoadoutDiagnosticsAsync(CancellationToken token)
    {
        var view = CaptureRuntimeView(); using var use = view.Lifetime.Enter();
        if (view.Extension is not null) await view.Extension.DrainLoadoutDiagnosticsAsync(token).ConfigureAwait(false);
    }
}
