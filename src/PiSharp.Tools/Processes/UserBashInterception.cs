// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts (UserBashEvent,
// UserBashEventResult), core/extensions/runner.ts (emitUserBash, isUserBashEventResult), modes/rpc/rpc-mode.ts ("bash")
// and modes/interactive/interactive-mode.ts (handleBashCommand).
namespace PiSharp.Tools.Processes;

/// <summary>Source UserBashEvent: a user <c>!</c>/<c>!!</c> command (or RPC <c>bash</c>) before it runs.</summary>
public sealed record UserBashEvent(string Command, bool ExcludeFromContext, string WorkingDirectory);

/// <summary>Source UserBashEventResult: exactly one of custom <see cref="Operations"/> or a finished <see cref="Result"/>.</summary>
public sealed record UserBashInterception
{
    /// <summary>Execute the command through these operations instead of the local shell.</summary>
    public IShellOperations? Operations { get; init; }
    /// <summary>The command already ran elsewhere; this result is recorded as is.</summary>
    public ShellCommandResult? Result { get; init; }
}

/// <summary>Source user_bash handler. Null continues with the next handler, then with local execution.</summary>
public delegate ValueTask<UserBashInterception?> UserBashHandler(UserBashEvent userBashEvent, CancellationToken cancellationToken);

/// <summary>Source emitUserBash.</summary>
public static class UserBashInterceptors
{
    public const string InvalidResultMessage =
        "Invalid user_bash handler result: return undefined for local execution or exactly one valid { operations } or { result } object";

    /// <summary>
    /// Runs handlers in order. The first non-null interception wins; one without exactly one of operations or result is
    /// an error. A handler error propagates and, as in the source, the command does not fall back to local execution.
    /// </summary>
    public static async ValueTask<UserBashInterception?> EmitAsync(IEnumerable<UserBashHandler> handlers, UserBashEvent userBashEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handlers); ArgumentNullException.ThrowIfNull(userBashEvent);
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await handler(userBashEvent, cancellationToken).ConfigureAwait(false);
            if (result is null) continue;
            if ((result.Operations is null) == (result.Result is null)) throw new InvalidOperationException(InvalidResultMessage);
            return result;
        }
        return null;
    }
}
