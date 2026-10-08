// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_installAgentRequestProjection:
// a virtual selection routes each request; the router's new state is appended as a pi.virtual-model-state custom entry and
// emitted as entry_appended) and core/virtual-models.ts (getVirtualModelState, findLatestResponse).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Cli.Models;

/// <summary>The session a virtual selection routes for: its current raw branch, and the run-time append of router state.</summary>
internal interface IVirtualModelSession
{
    IReadOnlyList<SessionEntry> Branch { get; }
    Task AppendStateAsync(JsonData data, CancellationToken cancellationToken);
}

/// <summary>The transport of a virtual selection: each request asks the router for its physical model and thinking level, records
/// a changed router state on the branch, and streams through the physical model's own live route. The selection stays virtual;
/// responses name the physical model that produced them.</summary>
internal sealed class VirtualModelRoutingTransport(ModelRegistry registry, RegistryModel model, Func<RegistryModel, IChatTransport> physical,
    Func<IVirtualModelSession?> session) : IChatTransport, IThinkingLevelTransport
{
    private readonly ConcurrentDictionary<string, IChatTransport> _routes = new(StringComparer.Ordinal);

    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor descriptor) => VirtualModels.SupportedThinkingLevels(model);

    public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var owner = session();
        var raw = owner?.Branch ?? [];
        var branch = raw.Select(Entry).ToList();
        var messages = request.Messages.Select(message => message.WireBody.Value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(message.WireBody.Value.GetRawText())!.AsObject() : new JsonObject()).ToList();
        var failed = Failed(branch, messages);
        var lastResponse = messages.FindLastIndex(message => Role(message) == "assistant");
        // Only messages the user wrote start a turn; extension messages can follow them.
        var userTurn = messages.Skip(lastResponse + 1).Any(message => Role(message) == "user");
        var state = VirtualModels.GetState(branch, model.Provider, model.Id);
        var route = await registry.ResolveVirtualAsync(model, branch, messages, failed is not null ? ModelRouteReason.Retry : userTurn ? ModelRouteReason.User :
            ModelRouteReason.Continuation, request.ThinkingLevel ?? "off", failed, state, cancellationToken).ConfigureAwait(false);
        if (route.State is not null && !JsonNode.DeepEquals(route.State, state) && owner is not null)
            await owner.AppendStateAsync(JsonData.Parse(new JsonObject
            { ["provider"] = model.Provider, ["modelId"] = model.Id, ["state"] = route.State.DeepClone() }.ToJsonString()), cancellationToken).ConfigureAwait(false);
        var transport = _routes.GetOrAdd(route.Model.Reference, _ => physical(route.Model));
        var routed = request with { Model = new(route.Model.Id, route.Model.Api, route.Model.Provider), ThinkingLevel = route.ThinkingLevel };
        await foreach (var observation in transport.StreamAsync(routed, cancellationToken).ConfigureAwait(false)) yield return observation;
    }

    /// <summary>The failed response the next request repeats (auto-retry, overflow recovery): the branch's latest assistant entry
    /// when it failed and the request no longer carries it.</summary>
    private static AssistantEntry? Failed(List<BranchEntry> branch, List<JsonObject> messages)
    {
        var latest = branch.OfType<AssistantEntry>().LastOrDefault();
        if (latest is not { StopReason: "error" or "length" }) return null;
        var lastSent = messages.LastOrDefault(message => Role(message) == "assistant");
        return lastSent is not null && JsonTree.String(lastSent, "stopReason") == latest.StopReason &&
            JsonTree.String(lastSent, "model") == latest.Model ? null : latest;
    }
    private static string? Role(JsonObject message) => JsonTree.String(message, "role");

    internal static BranchEntry Entry(SessionEntry entry)
    {
        var body = entry.WireBody.Value;
        string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        switch (entry.Type)
        {
            case "model_change": return new ModelChangeEntry(Text(body, "provider") ?? "", Text(body, "modelId") ?? "");
            case "custom": return new CustomEntry(Text(body, "customType") ?? "", body.TryGetProperty("data", out var data) ? JsonNode.Parse(data.GetRawText()) : null);
            case "message" when body.TryGetProperty("message", out var message) && Text(message, "role") == "assistant":
                return new AssistantEntry(Text(message, "provider") ?? "", Text(message, "model") ?? "", Text(message, "api") ?? "",
                    Text(message, "stopReason") ?? "", Text(message, "thinkingLevel"));
            default: return new OtherEntry();
        }
    }
}
