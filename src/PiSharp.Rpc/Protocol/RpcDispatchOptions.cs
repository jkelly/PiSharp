using PiSharp.Contracts;

namespace PiSharp.Rpc.Protocol;

public enum RpcSessionOwnership { Borrowed, Owned }
/// <summary>Explicit configured full model metadata; executable runtime selection remains in the coordinator.</summary>
public sealed record RpcModelDefinition(ModelDescriptor Model, JsonData WireBody);
public sealed record RpcDispatchOptions(int MaximumConcurrentCommands = 8, int MaximumCommandBytes = 1_048_576,
    int MaximumOutputBytes = 1_048_576, int MaximumJsonDepth = 32, int MaximumIdCharacters = 256,
    int MaximumCommandTypeCharacters = 128, int MaximumPromptCharacters = 65_536, int MaximumImages = 16,
    int MaximumModels = 128, int MaximumModelDefinitionBytes = 1_048_576, int MaximumReturnedMessages = 1024,
    int MaximumReturnedEntries = 4096, int MaximumContinuationRuns = 16, int MaximumPendingToolMessages = 128)
{
    internal void Validate(RpcSessionOwnership ownership)
    {
        if (MaximumConcurrentCommands <= 0 || MaximumCommandBytes is < 1 or > int.MaxValue - 1 ||
            MaximumOutputBytes is < 256 or > int.MaxValue - 1 || MaximumJsonDepth is < 1 or > 64 ||
            MaximumIdCharacters <= 0 || MaximumCommandTypeCharacters <= 0 || MaximumPromptCharacters <= 0 ||
            MaximumImages < 0 || MaximumModels <= 0 || MaximumModelDefinitionBytes is < 1 or > int.MaxValue - 1 ||
            MaximumReturnedMessages <= 0 || MaximumReturnedEntries <= 0 || MaximumContinuationRuns <= 0 ||
            MaximumPendingToolMessages <= 0 || !Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(RpcDispatchOptions), "Invalid RPC dispatch limits or ownership.");
    }
}
public enum RpcDispatchFailure { ResourceLimit, InvalidModelDefinition, SessionRunFailed, OutputFailed, CleanupFailed, Disposed }
public sealed class RpcDispatchException : IOException
{
    public RpcDispatchFailure Failure { get; }
    internal RpcDispatchException(RpcDispatchFailure failure, Exception? inner = null) : base(failure switch
    {
        RpcDispatchFailure.ResourceLimit => "RPC operation exceeds configured limits.",
        RpcDispatchFailure.InvalidModelDefinition => "RPC requires an explicit full model definition matching the runtime model.",
        RpcDispatchFailure.SessionRunFailed => "RPC session run failed after acceptance; durable state requires explicit inspection.",
        RpcDispatchFailure.OutputFailed => "RPC protocol output failed.",
        RpcDispatchFailure.Disposed => "RPC dispatcher is closing or disposed.",
        _ => "RPC dispatcher cleanup failed."
    }, inner) => Failure = failure;
}
