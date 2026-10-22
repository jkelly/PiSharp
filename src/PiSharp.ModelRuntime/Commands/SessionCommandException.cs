// Web Agent W2 (WU2, plan "Architecture" item 2): split out of src/PiSharp.Cli/Commands/SessionCommands.cs so the live provider
// selection in PiSharp.ModelRuntime keeps throwing the CLI's own session command failure (namespace and members unchanged).
namespace PiSharp.Cli.Commands;

/// <summary>Why a session command, or the provider selection it parses, failed.</summary>
public enum SessionCommandFailure { InvalidArguments, InvalidPath, WorkspaceMissing, WorkspaceMismatch, ReservedTarget, InvalidScript, ResourceLimit, CommandFailed, OfflineProviderMismatch, InvalidBashConfiguration, UnsupportedBashPlatform }
/// <summary>A session command failure; <see cref="Failure"/> classifies it.</summary>
public sealed class SessionCommandException : Exception
{
    /// <summary>The failure class.</summary>
    public SessionCommandFailure Failure { get; }
    internal SessionCommandException(SessionCommandFailure failure) : base(failure switch
    {
        SessionCommandFailure.InvalidArguments => "Session command arguments are invalid.",
        SessionCommandFailure.InvalidPath => "Session commands require bounded absolute local paths.",
        SessionCommandFailure.WorkspaceMissing => "The explicit workspace directory must already exist.",
        SessionCommandFailure.WorkspaceMismatch => "Session working directory does not match the explicit workspace.",
        SessionCommandFailure.ReservedTarget => "File authorization must name workspace files other than the session or script.",
        SessionCommandFailure.InvalidScript => "The bounded authored offline provider script is invalid or does not match the request history.",
        SessionCommandFailure.ResourceLimit => "Session command exceeds configured bounds.",
        SessionCommandFailure.OfflineProviderMismatch => "Selected durable model has no binding in the requested authored offline API profile.",
        SessionCommandFailure.InvalidBashConfiguration => "Offline Bash requires explicit valid executable, workspace spill root and exact command authorization.",
        SessionCommandFailure.UnsupportedBashPlatform => "The configured offline Bash backend requires Windows, Linux or macOS.",
        _ => "Session command failed; inspect durable state before retrying."
    }) => Failure = failure;
    /// <summary>A failure whose message mirrors upstream CLI diagnostic text exactly.</summary>
    internal SessionCommandException(SessionCommandFailure failure, string message) : base(message) => Failure = failure;
}
