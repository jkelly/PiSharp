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
    /// <summary>The session's messages (agent-session.ts convertToLlm(this.messages)), routed for a summary.</summary>
    IReadOnlyList<TranscriptEntry> Messages => [];
    /// <summary>The session's thinking level (agent-session.ts this.thinkingLevel), routed for a summary.</summary>
    string ThinkingLevel => "off";
}

/// <summary>The transport of a virtual selection: each request asks the router for its physical model and thinking level, records
/// a changed router state on the branch, and streams through the physical model's own live route. The selection stays virtual;
/// responses name the physical model that produced them.</summary>
internal sealed class VirtualModelRoutingTransport(ModelRegistry registry, RegistryModel model, Func<RegistryModel, IChatTransport> physical,
    Func<IVirtualModelSession?> session, Func<RegistryModel, int, string?, IChatTransport>? physicalSummary = null) : IChatTransport, IThinkingLevelTransport
{
    private readonly ConcurrentDictionary<string, IChatTransport> _routes = new(StringComparer.Ordinal);

    /// <summary>
    /// agent-session.ts _getSummarizationRequestAuth for a virtual selection: one routing per summary with reason "direct", the
    /// session's messages and thinking level and no router state (model-runtime.ts resolveModel); the summary then goes to the routed
    /// physical model's summary route at <paramref name="maximum"/> (clamped there to its own cap) with the routed thinking level when
    /// the summary carries one (compaction and bug reports, not branch summaries). The router's new state is not recorded.
    /// </summary>
    internal IChatTransport Summary(int maximum, bool carriesLevel) => new SummaryRoute(this, maximum, carriesLevel);

    private sealed class SummaryRoute(VirtualModelRoutingTransport owner, int maximum, bool carriesLevel) : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            owner.SummaryStreamAsync(request, maximum, carriesLevel, cancellationToken);
    }

    private async IAsyncEnumerable<StreamEvent> SummaryStreamAsync(ChatRequest request, int maximum, bool carriesLevel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = physicalSummary ?? throw new InvalidOperationException("This virtual route has no summary route.");
        var current = session();
        var branch = (current?.Branch ?? []).Select(Entry).ToList();
        var messages = (current?.Messages ?? []).Select(message => message.WireBody.Value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(message.WireBody.Value.GetRawText())!.AsObject() : new JsonObject()).ToList();
        var route = await registry.ResolveVirtualAsync(model, branch, messages, ModelRouteReason.Direct, current?.ThinkingLevel ?? "off",
            null, null, cancellationToken).ConfigureAwait(false);
        var routed = request with { Model = new(route.Model.Id, route.Model.Api, route.Model.Provider) };
        await foreach (var observation in target(route.Model, maximum, carriesLevel ? route.ThinkingLevel : null)
            .StreamAsync(routed, cancellationToken).ConfigureAwait(false)) yield return observation;
    }

    /// <summary>IMPL-E seam for the live route of a virtual selection. Upstream registers virtual models only through the extension
    /// API (pi.registerVirtualModel), so no live session selects one yet; a host that admits that registration builds the session
    /// transport here. Each physical target connects through its own live route (<see cref="Commands.LiveSessionSelection.FromEntry"/>),
    /// and every connection opened is added to <paramref name="connections"/> for the host to dispose with the session.</summary>
    internal static VirtualModelRoutingTransport ForLive(ModelRegistry registry, RegistryModel model, Commands.LiveSessionRuntime? runtime,
        string? maximumTokens, Func<IVirtualModelSession?> session, ICollection<IDisposable> connections)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(connections);
        if (!VirtualModels.IsVirtual(model)) throw new ArgumentException("A virtual route needs a virtual model.", nameof(model));
        // One live connection per physical model serves both its requests and its summaries.
        var live = new ConcurrentDictionary<string, Lazy<Commands.LiveSessionConnection>>(StringComparer.Ordinal);
        Commands.LiveSessionConnection Connect(RegistryModel physical) => live.GetOrAdd(physical.Reference, _ => new(() =>
        {
            var connection = Commands.LiveSessionSelection.FromEntry(physical, registry, maximumTokens, useModelMaximum: maximumTokens is null).Connect(runtime);
            lock (connections) connections.Add(connection);
            return connection;
        })).Value;
        return new(registry, model, physical => Connect(physical).CreateTransport(), session, (physical, maximum, level) =>
        {
            var connection = Connect(physical);
            return connection.CreateSummaryTransport(Math.Min(maximum, connection.MaximumOutputTokens), level);
        });
    }

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
