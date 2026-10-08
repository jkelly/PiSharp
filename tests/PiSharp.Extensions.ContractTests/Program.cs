using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? report = null, filter = null;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (++index >= args.Length || option is not ("--report" or "--filter") ||
                option == "--report" && report is not null || option == "--filter" && filter is not null)
            { Console.Error.WriteLine("Usage: PiSharp.Extensions.ContractTests [--report <path>] [--filter <name-substring>]"); return 2; }
            if (option == "--report") report = Path.GetFullPath(args[index]); else filter = args[index];
        }
        var cases = RegistrationTests.Cases().Concat(ExtensionAgentBindingTests.Cases()).Concat(ExtensionToolInvocationTests.Cases()).Concat(ManifestAdmissionTests.Cases()).Concat(PublishedLoaderTests.Cases()).Concat(PublishedPersistentAgentTests.Cases()).Concat(EventReducerTests.Cases()).Concat(RegisteredEventDispatchTests.Cases()).Concat(RegisteredExtensionToolTests.Cases()).Concat(RegisteredToolImageTests.Cases()).Concat(RegisteredExtensionInputTests.Cases()).Concat(RuntimeExtensionHookCompositionTests.Cases()).Concat(PublishedSystemImportTests.Cases()).ToArray();
        cases = cases.Concat(LoadoutDiagnosticDeliveryTests.Cases()).Concat(SessionShutdownRegistryTests.Cases()).Concat(BeforeAgentStartTests.Cases()).Concat(ContextWithSystemTests.Cases()).Concat(NestedToolBrokerTests.Cases()).Concat(ToolActivationTests.Cases()).Concat(QuiescenceTests.Cases()).Concat(ExtensionSessionSnapshotTests.Cases()).Concat(SessionCreationRegistryTests.Cases()).Concat(SessionCreationPreflightRegistryTests.Cases()).Concat(SessionCatalogStateRegistryTests.Cases()).Concat(CommandCatalogRegistryTests.Cases().Select(test => (Name: "command-catalog." + test.Name, Run: test.Run)))
            .Concat(McpConfigurationTests.Cases()).Concat(McpServerRuntimeTests.Cases())
            .Concat(McpTransportTests.Cases()).Concat(ResourceReloadWorkflowTests.Cases()).Concat(ExtensionToolCatalogPublicationTests.Cases())
            .Concat(McpHttpOriginalOwnershipTests.Cases())
            .Concat(HostReloadPlannerTests.Cases()).Concat(McpResourceTests.Cases())
            .Concat(McpResourceRuntimeBridgeTests.Cases())
            .Concat(McpAdmittedOAuthStateProviderTests.Cases())
            .Concat(McpChannelExtensionIntegrationTests.Cases())
            .Concat(McpDynamicRootsTests.Cases())
            .Concat(McpIncomingRequestRegistryTests.Cases())
            .Concat(McpRegistrationFacadeTests.Cases())
            .Concat(RegisteredCustomComponentTests.Cases())
            .Where(test => filter is null || test.Name.Contains(filter, StringComparison.Ordinal)).ToArray();
        if (cases.Length == 0) { Console.Error.WriteLine("No matching extension cases."); return 2; }
        var evidence = new List<object>();
        var failures = 0;
        foreach (var test in cases)
        {
            try
            {
                if (test.Name.StartsWith("custom-registry.", StringComparison.Ordinal) || test.Name.StartsWith("tool-catalog.", StringComparison.Ordinal) || test.Name.StartsWith(McpConfigurationTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpServerRuntimeTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpTransportTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpHttpOriginalOwnershipTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(ResourceReloadWorkflowTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(HostReloadPlannerTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpResourceTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpResourceRuntimeBridgeTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpAdmittedOAuthStateProviderTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpChannelExtensionIntegrationTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpDynamicRootsTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpIncomingRequestRegistryTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(McpRegistrationFacadeTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(LoadoutDiagnosticDeliveryTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(SessionShutdownRegistryTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith("command-catalog.", StringComparison.Ordinal) ||
                    test.Name.StartsWith(NestedToolBrokerTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(ToolActivationTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(ContextWithSystemTests.Prefix, StringComparison.Ordinal) ||
                    test.Name.StartsWith(BeforeAgentStartTests.Prefix, StringComparison.Ordinal)) await test.Run();
                else await test.Run().WaitAsync(TimeSpan.FromSeconds(20));
                Console.WriteLine($"PASS {test.Name}");
                evidence.Add(new { testId = $"extension-registration.{test.Run.Method.Name}", name = test.Name, status = "passed" });
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {error}");
                evidence.Add(new { testId = $"extension-registration.{test.Run.Method.Name}", name = test.Name, status = "failed", error = error.Message, diagnostics = TestFailureDiagnostics.Capture(error) });
            }
        }
        Console.WriteLine($"{cases.Length - failures}/{cases.Length} experimental registration groups passed");
        if (report is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
                scope = "experimental-native-extension-registry-reducers-manifest-published-loading-and-Agent-binding",
                contractProfile = PiSharp.Extensions.ExperimentalExtensionContract.Profile,
                linkFixtureGap = ManifestAdmissionTests.LinkFixtureGap,
                toolImageDifferential = RegisteredToolImageTests.DifferentialEvidence,
                tests = evidence,
                passed = cases.Length - failures,
                failed = failures
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures == 0 ? 0 : 1;
    }
}
