using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

public sealed partial class RpcSessionDispatcher
{
    private static readonly string[] ThinkingOrder = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    private async Task<JsonData?> ModelThinkingAsync(RpcCommandEnvelope command,
        AgentSessionAttachment? attachment, CancellationToken token)
    {
        await _transitions.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen(); token.ThrowIfCancellationRequested();
            if (attachment is not null && !ReferenceEquals(_sessionOwner!.Current, attachment))
                throw new RpcCommandException(command.Id, command.Type, "Model/thinking command belongs to a retired session attachment.");
            var current = _session.Snapshot;
            if (!_models.ContainsKey(current.Agent.Model)) throw new RpcDispatchException(RpcDispatchFailure.InvalidModelDefinition);
            if (command.Type == "get_available_models")
                return RpcCommandCodec.Build(writer =>
                {
                    writer.WritePropertyName("models"); writer.WriteStartArray();
                    foreach (var model in _modelOrder) writer.WriteRawValue(_models[model].Value.GetRawText());
                    writer.WriteEndArray();
                }, _options.MaximumOutputBytes);
            var levels = _session.GetSupportedThinkingLevels(current.Agent.Model);
            if (command.Type == "get_available_thinking_levels")
                return Levels(levels);
            // Read-only queries remain available during streaming; mutations retain exclusive idle admission.
            lock (_gate) if (_run is not null)
                throw new RpcCommandException(command.Id, command.Type, "Session is processing or settling; model/thinking selection requires idle admission.");
            if (command.Type is "set_model" or "cycle_model")
            {
                ModelDescriptor selected;
                if (command.Type == "set_model")
                {
                    selected = _modelOrder.FirstOrDefault(value => value.Provider == command.Provider && value.Id == command.ModelId)
                        ?? throw new RpcCommandException(command.Id, command.Type, "Model not found: " + command.Provider + "/" + command.ModelId);
                }
                else
                {
                    if (_modelOrder.Length <= 1) return NullData;
                    var index = _modelOrder.IndexOf(current.Agent.Model);
                    selected = _modelOrder[((index < 0 ? 0 : index) + 1) % _modelOrder.Length];
                }
                var selectedWire = _models[selected];
                var retryWindow = selectedWire.Value.TryGetProperty("contextWindow", out var admittedWindow) &&
                    admittedWindow.ValueKind == System.Text.Json.JsonValueKind.Number && admittedWindow.TryGetDouble(out var window) &&
                    double.IsFinite(window) && window >= 0 ? window : 0;
                var thinking = ClampThinkingLevel(current.Context.ThinkingLevel, _session.GetSupportedThinkingLevels(selected));
                var data = command.Type == "set_model" ? selectedWire : RpcCommandCodec.Build(writer =>
                {
                    RpcCommandCodec.Raw(writer, "model", selectedWire); writer.WriteString("thinkingLevel", thinking);
                    writer.WriteBoolean("isScoped", false);
                }, _options.MaximumOutputBytes);
                _ = RpcCommandCodec.Success(command, data, _options); // Bound the complete response before durable effects.
                await _session.ConfigureAsync(new(Model: selected, ThinkingLevel: thinking), token).ConfigureAwait(false);
                if (_session.AutomaticRetryConfigured) _session.SetAutomaticRetryContextWindow(retryWindow);
                return data;
            }
            if (command.Type == "cycle_thinking_level" && levels.All(value => value == "off")) return NullData;
            if (command.Type == "cycle_thinking_level" && levels.IsEmpty) return NullData;
            var level = command.Type == "set_thinking_level"
                ? ClampThinkingLevel(command.ThinkingLevel!, levels)
                : levels[(levels.IndexOf(current.Context.ThinkingLevel) + 1) % levels.Length];
            var response = command.Type == "set_thinking_level" ? null : RpcCommandCodec.Build(
                writer => writer.WriteString("level", level), _options.MaximumOutputBytes);
            _ = RpcCommandCodec.Success(command, response, _options);
            // Operational capabilities were admitted by the actual session binding; durable configuration remains authoritative.
            await _session.ConfigureAsync(new(ThinkingLevel: level), token).ConfigureAwait(false);
            return response;
        }
        catch (SessionRuntimeRegistryException error)
        { throw new RpcCommandException(command.Id, command.Type, error.Message); }
        finally { _transitions.Release(); }
    }

    private static JsonData NullData { get; } = JsonData.Parse("null");
    private JsonData Levels(ImmutableArray<string> levels) => RpcCommandCodec.Build(writer =>
    {
        writer.WritePropertyName("levels"); writer.WriteStartArray(); foreach (var level in levels) writer.WriteStringValue(level);
        writer.WriteEndArray();
    }, _options.MaximumOutputBytes);

    private static string ClampThinkingLevel(string requested, ImmutableArray<string> available)
    {
        if (available.Contains(requested)) return requested;
        var index = Array.IndexOf(ThinkingOrder, requested);
        if (index < 0) return available.FirstOrDefault() ?? "off";
        for (var candidate = index; candidate < ThinkingOrder.Length; candidate++)
            if (available.Contains(ThinkingOrder[candidate])) return ThinkingOrder[candidate];
        for (var candidate = index - 1; candidate >= 0; candidate--)
            if (available.Contains(ThinkingOrder[candidate])) return ThinkingOrder[candidate];
        return available.FirstOrDefault() ?? "off";
    }
}
