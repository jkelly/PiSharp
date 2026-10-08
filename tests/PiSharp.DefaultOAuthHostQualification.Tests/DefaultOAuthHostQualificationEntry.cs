using PiSharp.CodingAgent.Tests;
namespace PiSharp.Qualification;
internal static class DefaultOAuthHostQualificationEntry
{
    private static Task<int> Main(string[] args) => QualificationEvidence.RunAsync(args,
        () => McpDefaultOAuthHostControls.Cases().Select(item => (item.Name, item.Test)).ToArray(), 6,
        () => McpDefaultOAuthHostControls.CapturedOriginals.Select(item =>
            new CapturedOriginal(item.Phase, item.Original, item.Aggregate, item.Direct)).ToArray());
}
