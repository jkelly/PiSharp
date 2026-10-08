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
    /// <summary>The selected model's catalog object for model_select payloads; the host sets it when it knows the catalog.</summary>
    internal Func<ModelDescriptor, JsonData?>? ModelWire { get; set; }

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
