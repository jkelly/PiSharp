namespace PiSharp.Qualification;
internal static class McpTuiQualificationEntry
{
    private static Task<int> Main(string[] args) => QualificationEvidence.RunAsync(args,
        () => McpDynamicRootsHttpBindingTests.Cases().Concat(McpAdmittedHttpAuthenticationTests.Cases())
            .Concat(McpOAuthDiscoveryRefreshTests.Cases()).Concat(TerminalComponentTransportFailureTests.Cases()).Concat(NativeComponentOpenLifetimeTests.Cases()).ToArray(),
        21, () => McpDynamicRootsHttpBindingTests.CapturedOriginals.Select(item =>
            new CapturedOriginal("roots:" + item.Phase, item.Original, item.Aggregate, item.Direct))
            .Concat(McpAdmittedHttpAuthenticationTests.CapturedOriginals.Select(item =>
            new CapturedOriginal("authentication:" + item.Phase, item.Original, item.Aggregate, item.Direct))).Concat(McpOAuthDiscoveryRefreshTests.CapturedOriginals.Select((item, index) =>
            new CapturedOriginal("oauth:" + index, item.Original, item.Aggregate, item.Direct))).ToArray());
}