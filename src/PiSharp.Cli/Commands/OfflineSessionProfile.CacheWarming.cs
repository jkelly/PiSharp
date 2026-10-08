// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts (cacheWarmer: start in the agent stream
// function for session requests, cacheContextIsCurrent, decide -> emitCacheWarmingDecision) and core/agent-session.ts
// (onAgentSettled first in _emitAgentSettled, onWarmed -> entry_appended, cancel on dispose).
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Usage;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Cli.Commands;

internal sealed partial class OfflineSessionProfile
{
    private readonly ConditionalWeakTable<PersistentAgentSession, CacheWarmer> _cacheWarmers = new();
    private IChatTransport? _cacheWarmReplay;
    private readonly object _cacheWarmGate = new();

    /// <summary>The cache warmer of the current session, if it has sent a request (backs <c>/session</c> warming status).</summary>
    internal CacheWarmer? CurrentCacheWarmer => Sessions?.Current.Session is { } session && _cacheWarmers.TryGetValue(session, out var warmer) ? warmer : null;

    /// <summary>The live main route with Pi's cache warmer: each session request starts warming its prompt cache entry.</summary>
    private IChatTransport WithCacheWarming(IChatTransport main, LiveSessionConnection live, JsonData modelWire) =>
        new CacheWarmingTransport(this, main, live, modelWire);

    private CacheWarmer WarmerFor(PersistentAgentSession session, LiveSessionConnection live) => _cacheWarmers.GetValue(session, owned =>
    {
        var warmer = new CacheWarmer(
            async (request, overrides, token) =>
            {
                IChatTransport replay;
                lock (_cacheWarmGate) replay = _cacheWarmReplay ??= live.CreateCacheWarmTransport();
                AssistantMessage? final = null;
                await foreach (var observation in replay.StreamAsync((ChatRequest)request.Payload!, token).ConfigureAwait(false))
                    if (observation is StreamTerminalEvent terminal) final = terminal.Message;
                return final ?? throw new InvalidOperationException("Cache warming replay ended without a response.");
            },
            (kind, provider, model, usage, note) => owned.AppendUsageEntryAsync(kind, provider, model, usage, note),
            () => owned.Snapshot.Context.Ancestry,
            () => CacheWarmingModes.Resolve(CurrentSettingsValue("cacheWarming")),
            async decision =>
            {
                if (_extension is not { } extension) return decision.Action;
                return await extension.DecideCacheWarmingAsync(decision).ConfigureAwait(false);
            });
        // Source _emitAgentSettled: the warmer learns the run settled before extensions and listeners do.
        _ = owned.SubscribeOperationEvents(new SettledSink(warmer));
        return warmer;
    });

    /// <summary>A string setting of the current attachment's effective settings (null when unset or unavailable).</summary>
    private string? CurrentSettingsValue(string name)
    {
        try
        {
            var settings = Sessions is { } owner ? CaptureEffectiveSettings(owner.Current) : CaptureStartupEffectiveSettings();
            return settings?.Values is { } values && values.Value.ValueKind == JsonValueKind.Object &&
                values.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    private sealed class SettledSink(CacheWarmer warmer) : ISessionOperationEventSink
    {
        public ValueTask EmitAsync(SessionOperationEvent observation, CancellationToken cancellationToken)
        {
            if (observation is SessionOperationSettled) warmer.OnAgentSettled();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CacheWarmingTransport(OfflineSessionProfile profile, IChatTransport main, LiveSessionConnection live, JsonData modelWire)
        : IChatTransport, IThinkingLevelTransport
    {
        private readonly CacheWarmModel? model = TryModel(modelWire);
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor descriptor) =>
            main is IThinkingLevelTransport levels ? levels.GetSupportedThinkingLevels(descriptor) : ["off"];
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            // Cache warming is best effort: it never affects the request it observes.
            try { StartWarming(request); } catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            return main.StreamAsync(request, cancellationToken);
        }
        private void StartWarming(ChatRequest request)
        {
            if (model is not null && profile.Sessions?.Current.Session is { } session)
            {
                var warmer = profile.WarmerFor(session, live);
                var captured = session.Snapshot.Agent.Messages; var selected = session.Snapshot.Agent.Model;
                warmer.Start(new(model, new(Reasoning: request.ThinkingLevel is null or "off" ? null : request.ThinkingLevel), request), () =>
                {
                    // Source cacheContextIsCurrent: same selected model, and the request's messages still prefix the transcript.
                    var current = session.Snapshot.Agent;
                    if (current.Model != selected || current.Messages.Length < captured.Length) return false;
                    for (var index = 0; index < captured.Length; index++) if (!ReferenceEquals(captured[index], current.Messages[index])) return false;
                    return true;
                });
            }
        }
        private static CacheWarmModel? TryModel(JsonData wire)
        {
            try { return CacheWarmModel.FromJson(wire.Value); } catch (FormatException) { return null; }
        }
    }
}
