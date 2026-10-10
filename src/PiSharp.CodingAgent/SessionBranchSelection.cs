// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/virtual-models.ts (getBranchSelection,
// findLastModelChange, isVirtualModel) as sdk.ts uses it to restore a session's model.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent;

/// <summary>The model selection a session branch records. A virtual <c>model_change</c> holds until the next <c>model_change</c>,
/// because responses name the physical models it routed to. Otherwise the latest physical response wins. A virtual model that is
/// no longer registered does not hold, so the selection falls back to the physical model that answered last.</summary>
public static class SessionBranchSelection
{
    /// <summary>Source VIRTUAL_MODEL_API.</summary>
    public const string VirtualApi = "pi-virtual";

    /// <param name="isVirtual">Whether the catalog registers <c>(provider, modelId)</c> as a virtual model.</param>
    public static SessionContextModel? Select(ImmutableArray<SessionEntry> branch, Func<string, string, bool> isVirtual)
    {
        ArgumentNullException.ThrowIfNull(isVirtual);
        if (branch.IsDefault) return null;
        for (var index = branch.Length - 1; index >= 0; index--)
        {
            var entry = branch[index];
            if (entry.Kind == SessionEntryKind.ModelChange) return Change(entry);
            if (entry.Kind != SessionEntryKind.Message || !entry.WireBody.Value.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("role", out var role) || role.GetString() != "assistant") continue;
            // Failed routing leaves the virtual model on its message; it names no physical response.
            if (message.TryGetProperty("api", out var api) && api.ValueKind == JsonValueKind.String && api.GetString() == VirtualApi) continue;
            var response = new SessionContextModel(message.GetProperty("provider").GetString()!, message.GetProperty("model").GetString()!);
            for (var before = index - 1; before >= 0; before--)
                if (branch[before].Kind == SessionEntryKind.ModelChange)
                {
                    var change = Change(branch[before]);
                    return isVirtual(change.Provider, change.ModelId) ? change : response;
                }
            return response;
        }
        return null;
    }

    /// <summary>The selection checked against a configured model: only that model is known to be virtual.</summary>
    internal static SessionContextModel? Select(SessionContextProjection context, PiSharp.Contracts.ModelDescriptor configured) =>
        Select(context.Ancestry, (provider, id) => configured.Api == VirtualApi && configured.Provider == provider && configured.Id == id);

    private static SessionContextModel Change(SessionEntry entry) =>
        new(entry.WireBody.Value.GetProperty("provider").GetString()!, entry.WireBody.Value.GetProperty("modelId").GetString()!);
}
