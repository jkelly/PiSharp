// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (executeBash) and
// modes/rpc/rpc-mode.ts ("bash": emitUserBash before executeBash).
using System.Collections.Immutable;
using PiSharp.CodingAgent.Execution;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Commands;

/// <summary>The session's user Bash capability (RPC <c>bash</c>, interactive <c>!</c>/<c>!!</c>) over the source executor.
/// Admitted only when the host configured an explicit shell and spill root; the model tool's command grant does not apply
/// because these commands come from the user, not from model output.</summary>
internal sealed class UserBashHost(ShellCommandExecutor executor, string? commandPrefix = null) : IUserBashExecutor
{
    /// <summary>Source user_bash handlers, consulted in order before local execution. Native extension registration of
    /// these handlers belongs to the extension event dispatch; hosts install them here.</summary>
    public ImmutableArray<UserBashHandler> Handlers { get; set; } = [];

    public async Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(progress);
        // Source: the handlers see the command as typed; a returned result is recorded as is, returned operations run
        // the prefixed command instead of the local shell. A handler error does not fall back to local execution.
        var interception = Handlers.IsDefaultOrEmpty ? null : await UserBashInterceptors.EmitAsync(Handlers,
            new(request.Command, request.ExcludeFromContext ?? false, request.WorkingDirectory), cancellationToken).ConfigureAwait(false);
        if (interception?.Result is { } handled)
            return new(handled.Output, handled.Cancelled ? null : handled.ExitCode, handled.Cancelled, handled.Truncated, handled.FullOutputPath);
        var selected = interception?.Operations is { } operations ? executor.WithOperations(operations) : executor;
        // Source executeBash: the shellCommandPrefix setting applies to user commands too.
        var command = string.IsNullOrEmpty(commandPrefix) ? request.Command : commandPrefix + "\n" + request.Command;
        var result = await selected.ExecuteAsync(command, request.WorkingDirectory, async chunk =>
        {
            foreach (var delta in Deltas(chunk)) await progress(delta).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return new(result.Output, result.ExitCode, result.Cancelled, result.Truncated, result.FullOutputPath);
    }

    /// <summary>Session progress deltas are bounded; a larger sanitized chunk is split without separating a surrogate pair.</summary>
    internal static IEnumerable<string> Deltas(string chunk)
    {
        const int maximum = 65_536;
        for (var start = 0; start < chunk.Length;)
        {
            var length = Math.Min(maximum, chunk.Length - start);
            if (start + length < chunk.Length && char.IsHighSurrogate(chunk[start + length - 1])) length--;
            yield return chunk.Substring(start, length); start += length;
        }
    }
}
