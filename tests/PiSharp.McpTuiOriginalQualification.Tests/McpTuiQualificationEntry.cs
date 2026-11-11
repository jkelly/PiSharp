namespace PiSharp.Qualification;
internal static class McpTuiQualificationEntry
{
    private static async Task<int> Main(string[] args)
    {
        var code = await QualificationEvidence.RunAsync(args,
        () => McpDynamicRootsHttpBindingTests.Cases().Concat(McpAdmittedHttpAuthenticationTests.Cases())
            .Concat(McpOAuthDiscoveryRefreshTests.Cases()).Concat(TerminalComponentTransportFailureTests.Cases()).Concat(NativeComponentOpenLifetimeTests.Cases()).ToArray(),
        21, () => McpDynamicRootsHttpBindingTests.CapturedOriginals.Select(item =>
            new CapturedOriginal("roots:" + item.Phase, item.Original, item.Aggregate, item.Direct))
            .Concat(McpAdmittedHttpAuthenticationTests.CapturedOriginals.Select(item =>
            new CapturedOriginal("authentication:" + item.Phase, item.Original, item.Aggregate, item.Direct))).Concat(McpOAuthDiscoveryRefreshTests.CapturedOriginals.Select((item, index) =>
            new CapturedOriginal("oauth:" + index, item.Original, item.Aggregate, item.Direct))).ToArray());
        if (code == 0 && args is ["--report", var path])
        {
            using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var results = report.RootElement.GetProperty("groups").EnumerateArray().Select(row => new
            {
                Id = row.GetProperty("name").GetString(),
                status = row.GetProperty("succeeded").GetBoolean() ? "PASS_AUTHORED_NATIVE_ONLY" : "FAIL"
            }).ToArray();
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { results, sourceReport = path,
                fullMcpOAuthParity = false, fullProviderParity = false }));
        }
        return code;
    }
}
