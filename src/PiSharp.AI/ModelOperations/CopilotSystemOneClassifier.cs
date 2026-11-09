// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/ai/src/providers/github-copilot.ts copilotSystemOne.
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>Copilot chat endpoints are unversioned, but System One lives beneath /v1. Append the prefix after
/// account auth has replaced the catalog base URL; keep the shared System One protocol unchanged.</summary>
internal sealed class CopilotSystemOneClassifier : IClassifierApi
{
    internal static CopilotSystemOneClassifier Instance { get; } = new();
    public string Api => TypeSafeSystemOneClassifier.ApiId;

    public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null,
        CancellationToken cancellationToken = default) => TypeSafeSystemOneClassifier.Instance.ClassifyAsync(
            model with { BaseUrl = model.BaseUrl.TrimEnd('/') + "/v1" }, context, options, cancellationToken);
}
