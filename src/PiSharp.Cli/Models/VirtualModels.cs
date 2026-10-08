// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/virtual-models.ts and
// packages/coding-agent/src/core/model-runtime.ts (registerVirtualModel, resolveModel, getPhysicalModel) and
// packages/ai/src/models.ts (getSupportedThinkingLevels, clampThinkingLevel).
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>Why a request is routed (ModelRouteReason).</summary>
internal enum ModelRouteReason { User, Continuation, Retry, Direct }

/// <summary>One routing request. Messages are the request's conversation as JSON message objects.</summary>
internal sealed record ModelRouteRequest(RegistryModel Model, string ThinkingLevel, ModelRouteReason Reason,
    (RegistryModel Model, string? ThinkingLevel)? Previous, (RegistryModel Model, string? ThinkingLevel, JsonObject Message)? Failed,
    JsonNode? State, IReadOnlyList<JsonObject> Messages, CancellationToken CancellationToken);

/// <summary>The physical model and thinking level of one request, and the router's new state.</summary>
internal sealed record ModelRoute(RegistryModel Model, string ThinkingLevel, JsonNode? State = null);

/// <summary>Source VirtualModelDefinition.</summary>
internal sealed record VirtualModelDefinition(string Provider, string Id, string Name,
    Func<ModelRouteRequest, ValueTask<ModelRoute>> Route, ImmutableArray<string>? ThinkingLevels = null,
    double? ContextWindow = null, double? MaxTokens = null, ImmutableArray<string>? Input = null);

/// <summary>A session-branch entry as virtual-model selection reads it.</summary>
internal abstract record BranchEntry;
internal sealed record ModelChangeEntry(string Provider, string ModelId) : BranchEntry;
internal sealed record AssistantEntry(string Provider, string Model, string Api, string StopReason, string? ThinkingLevel = null) : BranchEntry;
internal sealed record CustomEntry(string CustomType, JsonNode? Data) : BranchEntry;
internal sealed record OtherEntry : BranchEntry;

internal static class VirtualModels
{
    /// <summary>API id of virtual catalog entries. Requests for it fail unless routed first.</summary>
    internal const string Api = "pi-virtual";
    /// <summary>Custom entry type that stores router state on the session branch.</summary>
    internal const string StateEntry = "pi.virtual-model-state";
    internal static readonly ImmutableArray<string> Levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    internal static bool IsVirtual(RegistryModel model) => model.Api == Api;

    /// <summary>Source createVirtualModel.</summary>
    internal static RegistryModel Create(VirtualModelDefinition definition)
    {
        var levels = definition.ThinkingLevels ?? ["off"];
        var map = new JsonObject();
        foreach (var level in Levels) map[level] = levels.Contains(level, StringComparer.Ordinal) ? level : null;
        var input = new JsonArray([.. (definition.Input ?? ["text", "image"]).Select(kind => (JsonNode?)kind)]);
        return RegistryModel.FromJson(new JsonObject
        {
            ["id"] = definition.Id, ["name"] = definition.Name, ["api"] = Api, ["provider"] = definition.Provider, ["baseUrl"] = "",
            ["reasoning"] = levels.Any(level => level != "off"), ["thinkingLevelMap"] = map, ["input"] = input,
            ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 },
            ["contextWindow"] = definition.ContextWindow ?? 0, ["maxTokens"] = definition.MaxTokens ?? 0
        });
    }

    /// <summary>Source withVirtualModels for listing: a virtual model hides a physical chat model with the same id.</summary>
    internal static List<RegistryModel> WithVirtualModels(IReadOnlyList<RegistryModel> physical, IReadOnlyList<RegistryModel> virtualModels)
    {
        var ids = virtualModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        return [.. physical.Where(model => !IsVirtual(model) && !(model.Type == CatalogModelType.Chat && ids.Contains(model.Id))), .. virtualModels];
    }

    /// <summary>Source findLatestResponse: the latest assistant entry that neither failed nor was aborted.</summary>
    internal static AssistantEntry? FindLatestResponse(IReadOnlyList<BranchEntry> branch)
    {
        for (var index = branch.Count - 1; index >= 0; index--)
            if (branch[index] is AssistantEntry { StopReason: not ("error" or "aborted") } response) return response;
        return null;
    }

    /// <summary>Source getBranchSelection: a virtual model_change holds until the next model_change; otherwise the latest physical
    /// response wins.</summary>
    internal static (string Provider, string ModelId)? GetBranchSelection(IReadOnlyList<BranchEntry> branch, Func<string, string, RegistryModel?> getModel)
    {
        for (var index = branch.Count - 1; index >= 0; index--)
        {
            if (branch[index] is ModelChangeEntry change) return (change.Provider, change.ModelId);
            if (branch[index] is AssistantEntry assistant && assistant.Api != Api)
            {
                ModelChangeEntry? last = null;
                for (var before = index - 1; before >= 0 && last is null; before--) last = branch[before] as ModelChangeEntry;
                var model = last is null ? null : getModel(last.Provider, last.ModelId);
                return last is not null && model is not null && IsVirtual(model) ? (last.Provider, last.ModelId) : (assistant.Provider, assistant.Model);
            }
        }
        return null;
    }

    /// <summary>Source getVirtualModelState.</summary>
    internal static JsonNode? GetState(IReadOnlyList<BranchEntry> branch, string provider, string modelId)
    {
        for (var index = branch.Count - 1; index >= 0; index--)
            if (branch[index] is CustomEntry { CustomType: StateEntry, Data: JsonObject data } &&
                JsonTree.String(data, "provider") == provider && JsonTree.String(data, "modelId") == modelId)
                return data["state"]?.DeepClone();
        return null;
    }

    /// <summary>Source getSupportedThinkingLevels.</summary>
    internal static ImmutableArray<string> SupportedThinkingLevels(RegistryModel model)
    {
        if (!model.Reasoning) return ["off"];
        var map = model.CloneJson()["thinkingLevelMap"] as JsonObject;
        return [.. Levels.Where(level =>
        {
            JsonNode? value = null;
            var present = map is not null && map.TryGetPropertyValue(level, out value);
            if (present && value is null) return false;
            return level is not ("xhigh" or "max") || present;
        })];
    }

    /// <summary>Source clampThinkingLevel.</summary>
    internal static string ClampThinkingLevel(RegistryModel model, string level)
    {
        var available = SupportedThinkingLevels(model);
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        var requested = Levels.IndexOf(level);
        if (requested < 0) return available.FirstOrDefault() ?? "off";
        for (var index = requested; index < Levels.Length; index++) if (available.Contains(Levels[index], StringComparer.Ordinal)) return Levels[index];
        for (var index = requested - 1; index >= 0; index--) if (available.Contains(Levels[index], StringComparer.Ordinal)) return Levels[index];
        return available.FirstOrDefault() ?? "off";
    }
}
