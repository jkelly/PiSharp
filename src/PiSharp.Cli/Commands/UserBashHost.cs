// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (executeBash).
using PiSharp.CodingAgent.Execution;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Commands;

/// <summary>The session's user Bash capability (RPC <c>bash</c>, interactive <c>!</c>/<c>!!</c>) over the source executor.
/// Admitted only when the host configured an explicit shell and spill root; the model tool's command grant does not apply
/// because these commands come from the user, not from model output.</summary>
internal sealed class UserBashHost(ShellCommandExecutor executor) : IUserBashExecutor
{
    public async Task<UserBashResult> ExecuteAsync(UserBashExecutionRequest request, UserBashProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(progress);
        var result = await executor.ExecuteAsync(request.Command, request.WorkingDirectory, async chunk =>
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
