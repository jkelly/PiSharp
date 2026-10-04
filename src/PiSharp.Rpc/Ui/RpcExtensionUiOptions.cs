namespace PiSharp.Rpc.Ui;

public sealed record RpcExtensionUiOptions(int MaximumOutstandingRequests = 32, int MaximumRequestBytes = 65_536,
    int MaximumResponseBytes = 65_536, int MaximumRetainedBytes = 1_048_576, int MaximumTextCharacters = 16_384,
    int MaximumChoices = 256, int MaximumJsonDepth = 32, int MaximumIdCharacters = 128,
    int MaximumDeferredOrdinaryFrames = 8, int MaximumRetainedOrdinaryBytes = 8_388_608)
{
    internal void Validate()
    {
        if (MaximumOutstandingRequests is < 1 or > 4096 || MaximumRequestBytes is < 256 or > int.MaxValue - 1 ||
            MaximumResponseBytes is < 256 or > int.MaxValue - 1 || MaximumRetainedBytes < MaximumRequestBytes ||
            MaximumTextCharacters <= 0 || MaximumChoices is < 0 or > 4096 || MaximumJsonDepth is < 1 or > 64 ||
            MaximumIdCharacters < 49 || MaximumDeferredOrdinaryFrames is < 1 or > 4096 || MaximumRetainedOrdinaryBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(RpcExtensionUiOptions));
    }
}
