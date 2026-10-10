// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/runner.ts (emitUserBash) and
// modes/rpc/rpc-mode.ts / modes/interactive/interactive-mode.ts (user_bash before executeBash).
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Extensions;

internal sealed partial class NativeExtensionActivation
{
    /// <summary>ui_prompt_start/ui_prompt_end around blocking dialogs, when the host supplied a UI.</summary>
    private NativeUiPromptEvents? UiPrompts { get; init; }

    /// <summary>The selected model's catalog object for model_select payloads; the host sets it when it knows the catalog.</summary>
    internal Func<ModelDescriptor, JsonData?>? ModelWire { get; set; }

    /// <summary>The provider request hooks of this generation around a live route's HTTP handler, or null without handlers.
    /// A null inner handler gets a new owned socket handler, as the provider factories would create.</summary>
    internal Func<HttpMessageHandler?, HttpMessageHandler?>? ProviderHttpHooks(ModelDescriptor model)
    {
        var snapshot = _registry.CaptureSnapshot(); // The loaded revision; usable before the agent binding exists.
        return !ExtensionProviderHttpHandler.Applies(_registry, snapshot) ? null :
            inner => new ExtensionProviderHttpHandler(inner ?? new SocketsHttpHandler(), _registry, snapshot, model, _reportInputDiagnostic);
    }

    /// <summary>Source emitCacheWarmingDecision: handlers see the decision; the last returned <c>action</c> wins; failures are
    /// reported and fall back to the current action.</summary>
    internal async Task<string> DecideCacheWarmingAsync(PiSharp.CodingAgent.Usage.CacheWarmingDecisionEvent decision)
    {
        var snapshot = Binding.Snapshot;
        if (!_registry.HasEventHandlers(snapshot, "cache_warming_decision")) return decision.Action;
        var reduced = await _registry.ReduceEventAsync(snapshot, "cache_warming_decision", decision.ToJson(), (current, result) =>
        {
            if (result.Value.ValueKind != System.Text.Json.JsonValueKind.Object || !result.Value.TryGetProperty("action", out var action) ||
                action.ValueKind != System.Text.Json.JsonValueKind.String || action.GetString() is not ("warm" or "stop")) return null;
            var node = PiSharp.Contracts.JsonUtf16.MutableNode(current.Value.GetRawText())!.AsObject(); node["action"] = action.GetString();
            return JsonData.Parse(node.ToJsonString());
        }, _reportInputDiagnostic, CancellationToken.None, _closing.Token).ConfigureAwait(false);
        return reduced.Value.GetProperty("action").GetString()!;
    }

    /// <summary>True when this generation registered native user_bash handlers.</summary>
    internal bool HasUserBashHandlers => _registry.HasUserBashHandlers(Binding.Snapshot);

    /// <summary>The source user_bash handler chain of this generation as one <see cref="UserBashHandler"/> for
    /// <c>UserBashHost.Handlers</c>.</summary>
    internal UserBashHandler UserBashHandler => CreateUserBashHandler(_registry, Binding.Snapshot, _reportInputDiagnostic, _closing.Token);

    /// <summary>Source emitUserBash over one captured registration revision: the first handler patch wins, a returned result
    /// is recorded as is, returned operations replace the local shell, and a failure is reported and does not fall back.</summary>
    internal static UserBashHandler CreateUserBashHandler(ExtensionRegistry registry, ExtensionRegistrySnapshot snapshot,
        Func<ExtensionEventDiagnostic, CancellationToken, ValueTask>? report, CancellationToken lifetime) => async (userBashEvent, token) =>
    {
        var patch = await registry.DispatchUserBashAsync(snapshot,
            new(userBashEvent.Command, userBashEvent.ExcludeFromContext, userBashEvent.WorkingDirectory), report, token, lifetime).ConfigureAwait(false);
        if (patch is null) return null;
        if (patch.Result is { } result)
            return new() { Result = new(result.Output, result.Cancelled ? null : result.ExitCode, result.Cancelled, result.Truncated, result.FullOutputPath) };
        return new() { Operations = new ShellOperations(patch.Operations!) };
    };

    private sealed class ShellOperations(IExtensionShellOperations operations) : IShellOperations
    {
        public ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ProcessRawOutputCallback onData, CancellationToken cancellationToken)
            => operations.ExecuteAsync(command, workingDirectory, chunk => onData(chunk), cancellationToken);
    }
}
