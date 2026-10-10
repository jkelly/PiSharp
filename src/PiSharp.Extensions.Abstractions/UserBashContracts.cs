// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/extensions/types.ts (UserBashEvent,
// UserBashEventResult, BashOperations) and core/extensions/runner.ts (emitUserBash).
using PiSharp.Extensions.Events;

namespace PiSharp.Extensions;

/// <summary>Source UserBashEvent: a user <c>!</c>/<c>!!</c> command (or RPC <c>bash</c>) before it runs.</summary>
public sealed record ExtensionUserBashEvent(string Command, bool ExcludeFromContext, string WorkingDirectory);

/// <summary>Source BashResult: a command that already ran elsewhere. A cancelled result has no exit code.</summary>
public sealed record ExtensionUserBashResult(string Output, int? ExitCode, bool Cancelled = false, bool Truncated = false,
    string? FullOutputPath = null);

/// <summary>Raw output chunk from extension-provided shell operations, awaited before the next chunk.</summary>
public delegate ValueTask ExtensionShellOutputCallback(ReadOnlyMemory<byte> chunk);

/// <summary>Source BashOperations.exec: run a command and stream its raw output; null exit code means killed or cancelled.</summary>
public interface IExtensionShellOperations
{
    ValueTask<int?> ExecuteAsync(string command, string workingDirectory, ExtensionShellOutputCallback onData,
        CancellationToken cancellationToken);
}

/// <summary>Source UserBashEventResult: exactly one of <see cref="Operations"/> (run the command through them instead of the
/// local shell) or <see cref="Result"/> (record this result as is). A null patch continues with the next handler.</summary>
public sealed record ExtensionUserBashPatch
{
    public IExtensionShellOperations? Operations { get; init; }
    public ExtensionUserBashResult? Result { get; init; }
}

public sealed record ExtensionUserBashHandlerDescriptor(string RegistrationId,
    ExtensionReducerCallback<ExtensionUserBashEvent, ExtensionUserBashPatch> HandleAsync);

/// <summary>Native registration of source <c>pi.on("user_bash", handler)</c>.</summary>
public interface IExtensionUserBashRegistry : IExtensionRegistry
{
    IExtensionRegistration RegisterUserBashHandler(ExtensionUserBashHandlerDescriptor descriptor);
}
