using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Extensions.Runtime;
using System.Collections.Immutable;
using System.Text.Json;

namespace PiSharp.Extensions.Agent;

/// <summary>Explicit application construction connects the admitted extension catalog to the existing
/// IChatTransport/Agent pipeline. It borrows the provider host and creates no producer/queue/task owner.</summary>
public sealed class ExtensionProviderTransport(ExtensionProviderRegistrationHost admittedProviders) : IChatTransport, IThinkingLevelTransport
{
    private readonly ExtensionProviderRegistrationHost providers = admittedProviders ?? throw new ArgumentNullException(nameof(admittedProviders));
    public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var selected = providers.CaptureModels().SingleOrDefault(x => x.Model == model) ?? throw new ArgumentException("Unknown admitted model.");
        var metadata = selected.Metadata.Value;
        if (!metadata.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind == JsonValueKind.False) return ["off"];
        if (reasoning.ValueKind != JsonValueKind.True) throw new ArgumentException("Invalid admitted reasoning metadata.");
        var map = metadata.TryGetProperty("thinkingLevelMap", out var supplied) ? supplied : default;
        if (map.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) throw new ArgumentException("Invalid thinking map.");
        var levels = ThinkingLevels.Ordered.Where(level =>
        {
            var exists = map.ValueKind == JsonValueKind.Object && map.TryGetProperty(level, out _);
            return (!exists || map.GetProperty(level).ValueKind != JsonValueKind.Null) && (level is not ("xhigh" or "max") || exists);
        }).ToImmutableArray();
        if (levels.IsEmpty) throw new ArgumentException("No admitted native thinking levels.");
        return levels;
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return providers.StreamAsync(new(request.Model, request.Messages, request.Timestamp, request.ThinkingLevel), cancellationToken);
    }
}
