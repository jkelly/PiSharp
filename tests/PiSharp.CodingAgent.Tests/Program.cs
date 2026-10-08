using System.Text.Json;

if (args.Length > 0 && args[0] == OwnedChildShutdownFixture.Entry.ChildSwitch)
    return await OwnedChildShutdownFixture.Entry.RunChildAsync(args[1..]);

var terminalWorkerResult = await WindowsConPtyTerminalSessionFixture.TryRunWorkerAsync(args);
if (terminalWorkerResult is not null) return terminalWorkerResult.Value;

string? report = null, dotnetHost = null, cliDll = null, filter = null, exactName = null;
bool commandTerminalPackage = false, safeCaseProgress = false;
var packageOptions = new Dictionary<string, string>(StringComparer.Ordinal);
string[] packageKeys = ["--published", "--node", "--repo", "--oracle", "--jiti", "--reference", "--command-input-reference", "--run-parent"];
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--command-terminal-package" && !commandTerminalPackage) commandTerminalPackage = true;
    else if (args[index] == "--safe-case-progress" && !safeCaseProgress) safeCaseProgress = true;
    else if (packageKeys.Contains(args[index], StringComparer.Ordinal) && index + 1 < args.Length &&
        Path.IsPathFullyQualified(args[index + 1]) && packageOptions.TryAdd(args[index], Path.GetFullPath(args[index + 1]))) index++;
    else if (args[index] == "--report" && report is null && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
    else if (args[index] == "--dotnet-host" && dotnetHost is null && index + 1 < args.Length) dotnetHost = Path.GetFullPath(args[++index]);
    else if (args[index] == "--cli" && cliDll is null && index + 1 < args.Length) cliDll = Path.GetFullPath(args[++index]);
    else if (args[index] == "--filter" && filter is null && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])) filter = args[++index];
    else if (args[index] == "--exact-name" && exactName is null && index + 1 < args.Length && !string.IsNullOrWhiteSpace(args[index + 1])) exactName = args[++index];
    else throw new ArgumentException("Usage: PiSharp.CodingAgent.Tests --dotnet-host <path> --cli <path> [--report <path>] [--safe-case-progress] [--filter <ordinal-ignore-case name substring> | --exact-name <ordinal full name>]");
}
if (filter is not null && exactName is not null) throw new ArgumentException("--filter and --exact-name are mutually exclusive.");
if (dotnetHost is null || cliDll is null) throw new ArgumentException("Explicit .NET host and built CLI paths are required.");
if (commandTerminalPackage ? packageOptions.Count != packageKeys.Length : packageOptions.Count != 0)
    throw new ArgumentException("All absolute Node package paths require the explicit --command-terminal-package option.");
if (commandTerminalPackage && (!OperatingSystem.IsWindows() || report is null || File.Exists(report)))
    throw new ArgumentException("Command terminal qualification requires Windows and a fresh explicit report path.");
OfflineDemoTests.Configure(dotnetHost, cliDll);
var tests = PersistentAgentSessionTests.Cases().Concat(OfflineDemoTests.Cases())
    .Concat(StartupSettingsTests.Cases())
    .Concat(MistralLiveSelectionTests.Cases())
    .Concat(TerminalCustomComponentPresentationTests.Cases()).Concat(TerminalToolComponentLifecycleTests.Cases())
    .Concat(StartupToolSelectionTests.Cases())
    .Concat(SkillResourceTests.Cases())
    .Concat(SkillDiscoveryTests.Cases()).Concat(SkillCliHostTests.Cases())
    .Concat(McpProcessLeaseTests.Cases()).Concat(McpPreparedPublicationTests.Cases()).Concat(OwnedResourceRetirementTests.Cases())
    .Concat(McpPreOpenCaptureTests.Cases()).Concat(McpAdmittedActivationTests.Cases())
    .Concat(ShutdownPhysicalStopTests.Cases()).Concat(ShutdownNotificationPhaseTests.Cases())
    .Concat(McpSessionRuntimeFactoryTests.Cases()).Concat(McpHostShutdownTests.Cases())
    .Concat(McpProfileIntegrationTests.Cases()).Concat(McpProfileReviewTests.Cases()).Concat(McpRegisteredProfileDiscoveryTests.Cases())
    .Concat(CleanUserBashSessionTests.Cases()).Concat(AutomaticBashBoundaryTests.Cases()).Concat(CleanBashHookCompositionTests.Cases())
    .Concat(CleanRetryPolicyTests.Cases()).Concat(CleanRetryCoordinatorTests.Cases()).Concat(CleanRetryIntegrationTests.Cases()).Concat(CleanRetryOriginalOwnershipTests.Cases())
    .Concat(AdmittedHttpClientRequestFactoryTests.Cases()).Concat(McpAdmittedHttpLoopbackTests.Cases()).Concat(RetryProfileHostTests.Cases())
    .Concat(SettingsModelStartupTests.Cases())
    .Concat(PromptTemplateTests.Cases())
    .Concat(PromptTemplateCatalogTests.Cases())
    .Concat(PromptTemplateInputAdmissionTests.Cases())
    .Concat(PromptTemplateDiscoveryTests.Cases())
    .Concat(PromptTemplateResourceSetTests.Cases())
    .Concat(PromptTemplateCliAdapterTests.Cases())
    .Concat(PromptTemplateYamlTests.Cases())
    .Concat(PromptTemplateFrontendTests.Cases())
    .Concat(PromptTemplateWorkflowTests.Cases())
    .Concat(SessionRecoveryIntegrationTests.Cases())
    .Concat(SessionRecoveryOriginalCriteriaTests.Cases())
    .Concat(SessionNativeDiagnosticIntegrationTests.Cases())
    .Concat(SessionReplacementTests.Cases())
    .Concat(SessionTreeNavigationTests.Cases())
    .Concat(TwoPhaseShutdownTests.Cases())
    .Concat(NativeShutdownPlacementTests.Cases())
    .Concat(ReplacementSessionShutdownTests.Cases())
    .Concat(OwnedChildShutdownFixtureTests.Cases(dotnetHost))
    .Concat(NestedToolHostTests.Cases())
    .Concat(SessionContextEditIntegrationTests.Cases())
    .Concat(SessionCompactionIntegrationTests.Cases())
    .Concat(SessionCompactObservationTests.Cases())
    .Concat(SessionInfoChangedTests.Cases())
    .Concat(ReplacementSessionStartTests.Cases())
    .Concat(NativeBeforeSwitchPayloadTests.Cases())
    .Concat(SessionCompactionIntegrationTests.ReferenceCases())
    .Concat(SessionCompactionCommandTests.Cases(dotnetHost, cliDll))
    .Concat(TerminalEditorIntegrationTests.Cases(dotnetHost, cliDll))
    .Concat(TerminalAcknowledgedCallerTests.Cases(dotnetHost, cliDll))
    .Concat(TerminalSourceDefaultConsumerTests.Cases())
    .Concat(TerminalDefaultSourceReplayTests.Cases())
    .Concat(TerminalInputAdapterTests.Cases())
    .Concat(SessionContextEditIntegrationTests.ReferenceCases())
    .Concat(NativeSdkContextEditTests.Cases())
    .Concat(SessionContextEditCommandTests.Cases(dotnetHost, cliDll))
    .Concat(SessionCreationTests.Cases())
    .Concat(NativeSdkCreationPreflightTests.Cases())
    .Concat(SessionCatalogResumeTests.Cases())
    .Concat(SessionLifecycleReadOnlyTests.Cases())
    .Concat(SessionLifecycleBackendTests.Cases())
    .Concat(RuntimeAttachmentGenerationTests.Cases())
    .Concat(SessionLifecycleFrontendTests.Cases(dotnetHost, cliDll))
    .Concat(SessionLifecycleReferenceTests.Cases(cliDll))
    .Concat(NativeSdkSessionResumeTests.Cases())
    .Concat(SessionCatalogFrontendTests.Cases(dotnetHost, cliDll))
    .Concat(SessionRuntimeConfigurationTests.Cases()).Concat(SessionCommandTests.Cases(dotnetHost, cliDll))
    .Concat(PersistentSessionCompileConsumerTests.Cases()).Concat(PendingInputQueueSnapshotTests.Cases()).Concat(AtomicQueueTakeTests.Cases())
    .Concat(RpcSessionCommandTests.Cases(dotnetHost, cliDll))
    .Concat(StandardInputCancellationTests.Cases())
    .Concat(RuntimeToolProgressTests.Cases())
    .Concat(OfflineBashCommandTests.Cases(dotnetHost, cliDll))
    .Concat(SessionCopyCommandTests.Cases(dotnetHost, cliDll))
    .Concat(SessionPrintCommandTests.Cases(dotnetHost, cliDll))
    .Concat(SessionJsonEventCommandTests.Cases(dotnetHost, cliDll))
    .Concat(NativeExtensionSessionCommandTests.Cases(dotnetHost, cliDll))
    .Concat(NativeCliExtensionUiTests.Cases(dotnetHost, cliDll))
    .Concat(InteractiveSessionCommandTests.Cases(dotnetHost, cliDll))
    .Concat(InteractiveSessionRetirementTests.Cases(cliDll))
    .Concat(NativeHookOnlyActivationTests.Cases(dotnetHost, cliDll))
    .Concat(NativeToolNamespaceTests.Cases())
    .Concat(NativeLsInstallationTests.Cases())
    .Concat(NativeGrepInstallationTests.Cases())
    .Concat(NativeLoadoutDiagnosticTests.Cases())
    .Concat(SessionLoadoutDiagnosticDrainTests.Cases())
    .Concat(NativeRpcHtmlExportTests.Cases())
    .Concat(SessionHtmlRendererTests.Cases())
    .Concat(BuiltinToolInstallationTests.Cases())
    .Concat(NativeStringSchemaTests.Cases())
    .Concat(NativeTodoSchemaTests.Cases())
    .Concat(NativeExtensionSessionCommandTests.TodoCases(dotnetHost, cliDll))
    .Concat(NativeExtensionSessionCommandTests.CheckpointCases(dotnetHost, cliDll))
    .Concat(NativeExtensionSessionCommandTests.CreationCases(dotnetHost, cliDll))
    .Concat(InitialToolPreparationTests.Cases())
    .Concat(NativeExtensionSessionCommandTests.HelloSchemaCases(dotnetHost, cliDll))
    .Concat(TerminalSessionCommandTests.Cases(dotnetHost, cliDll))
    .Concat(LiveInteractiveAcceptanceTests.Cases(LiveInteractiveAcceptanceAdapter.RunAsync))
    .Concat(ImageCapabilityFrontendTests.Cases(dotnetHost, cliDll))
    .Concat(McpOwnedResourceDispatchTests.Cases())
    .Concat(NativeHostReloadStopOrderTests.Cases())
    .Concat(McpResourceFactoryCallerTests.Cases())
    .Concat(McpFactoryDisposalOriginalTests.Cases())
    .Concat(McpProfileAdmissionRollbackTests.Cases())
    .Concat(CliReloadRoutingTests.Cases())
    .Concat(ReloadCandidateStopOrderTests.Cases())
    .Concat(ProfileRuntimeViewTests.Cases())
    .Concat(ProfileRuntimeViewNativeTests.Cases())
    .Concat(ProfileRuntimeViewMcpBindingTests.Cases())
    .Concat(RuntimeLeaseWrappingTests.Cases())
    .Concat(ProfileCloseAndEmptyReloadTests.Cases())
    .Concat(McpProfileResourceAdmissionTests.Cases())
    .Concat(NativeHostReloadTests.Cases())
    .Concat(InteractiveCommandCompletionTests.Cases()).ToArray();
if (commandTerminalPackage)
    tests = NativeCommandTerminalSessionTests.Cases(dotnetHost, typeof(InteractiveCommandCompletionTests).Assembly.Location, cliDll,
        packageOptions["--published"], packageOptions["--node"], packageOptions["--repo"], packageOptions["--oracle"],
        packageOptions["--jiti"], packageOptions["--reference"], packageOptions["--command-input-reference"], packageOptions["--run-parent"]).ToArray();
var availableTests = tests.Length;
// IDs belong to this exact source catalog and retain their ordinals after filtering.
var progressOrdinals = safeCaseProgress ? tests.Select((test, index) => (test.Name, Ordinal: index + 1))
    .ToDictionary(test => test.Name, test => test.Ordinal, StringComparer.Ordinal) : null;
if (exactName is not null)
{
    tests = tests.Where(test => string.Equals(test.Name, exactName, StringComparison.Ordinal)).ToArray();
    if (tests.Length != 1) throw new ArgumentException("The exact test name must select exactly one case.");
}
if (filter is not null)
{
    tests = tests.Where(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (tests.Length == 0) throw new ArgumentException("The explicit test filter selected no cases.");
}
var evidence = new List<object>();
var failed = 0;
var progressCaptureFailures = 0;
void EmitProgress(string phase, int ordinal, string testId, string caseName, string? status, object? diagnostics = null)
{
    try
    {
        // One stream and a synchronous flush preserve START/END ordering in physical capture.
        // Only catalog identities and structural diagnostics are emitted; no exception message or payload.
        Console.WriteLine("CASE " + phase + " " + JsonSerializer.Serialize(new
        {
            caseId = $"codingagent.case{ordinal:D4}", testId, caseName, status, diagnostics
        }));
        Console.Out.Flush();
    }
    catch
    {
        // A diagnostic write failure cannot replace the original case failure or interrupt its cleanup.
        // It prevents acceptance at runner exit and is reported separately.
        progressCaptureFailures++;
    }
}
foreach (var test in tests)
{
    var testId = $"codingagent.{test.Run.Method.DeclaringType?.Name}.{test.Run.Method.Name}";
    var ordinal = progressOrdinals is null ? 0 : progressOrdinals[test.Name];
    if (safeCaseProgress) EmitProgress("START", ordinal, testId, test.Name, null);
    try
    {
        // These fixtures own child/read/write work or nested session cleanup and join all admitted work.
        // An outer WaitAsync would detach that physical cleanup if a deadline expires.
        // Diagnostic singletons rely on the admitted original owner's finite process ceiling.
        // Await the actual case task so a local diagnostic wait cannot detach its cleanup.
        if (test.Run.Method.DeclaringType == typeof(McpRegisteredProfileDiscoveryTests) || test.Run.Method.DeclaringType == typeof(TerminalToolComponentLifecycleTests) || safeCaseProgress || test.Name.StartsWith("custom-native.", StringComparison.Ordinal) || test.Name.StartsWith("shutdown-stop.", StringComparison.Ordinal) ||
            test.Name.StartsWith("shutdown-phase.", StringComparison.Ordinal) ||
            test.Name == "session-replacement shutdown cancels a postcommit lifecycle callback before joining transition" ||
            test.Name == "session-creation postcommit abort and disposal join held notification cleanup" ||
            test.Name.StartsWith(NativeHostReloadStopOrderTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-resource-factory.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-factory-disposal.", StringComparison.Ordinal) ||
            test.Name.StartsWith(McpProfileAdmissionRollbackTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("CLI ", StringComparison.Ordinal) ||
            test.Name.StartsWith("reload candidate ", StringComparison.Ordinal) ||
            test.Name.StartsWith("reload successful ", StringComparison.Ordinal) ||
            test.Name.StartsWith("profile view ", StringComparison.Ordinal) ||
            test.Name.StartsWith("runtime lease ", StringComparison.Ordinal) ||
            test.Name.StartsWith("MCP profile ", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-profile-resource-admission.", StringComparison.Ordinal) ||
            test.Name.StartsWith(McpOwnedResourceDispatchTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeHostReloadTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("owned-resource.", StringComparison.Ordinal) ||
            test.Name.StartsWith("MCP pre-open ", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-activation.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-session-factory.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-host-shutdown.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-profile.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-profile-review.", StringComparison.Ordinal) ||
            test.Name.StartsWith(CleanBashHookCompositionTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("clean user Bash ", StringComparison.Ordinal) ||
            test.Name.StartsWith(AutomaticBashBoundaryTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("clean retry ", StringComparison.Ordinal) ||
            test.Name.StartsWith("retry-profile.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-http-admission.", StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-http-loopback.", StringComparison.Ordinal) ||
            test.Name.StartsWith(RuntimeAttachmentGenerationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(McpPreparedPublicationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("mcp-process.", StringComparison.Ordinal) ||
            test.Name.StartsWith(SkillCliHostTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SkillDiscoveryTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SkillResourceTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(StartupSettingsTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(StartupToolSelectionTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SettingsModelStartupTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeBeforeSwitchPayloadTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(ReplacementSessionStartTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SessionCompactObservationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SessionInfoChangedTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(AtomicQueueTakeTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SessionTreeNavigationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(TwoPhaseShutdownTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeShutdownPlacementTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(ReplacementSessionShutdownTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("prompt template ", StringComparison.Ordinal) ||
            test.Name.StartsWith("Native thinking ", StringComparison.Ordinal) ||
            test.Name.StartsWith(OwnedChildShutdownFixtureTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("native before-start ", StringComparison.Ordinal) ||
            test.Name.StartsWith("native request-context ", StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeToolNamespaceTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeLsInstallationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeGrepInstallationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeLoadoutDiagnosticTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(SessionLoadoutDiagnosticDrainTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(NativeRpcHtmlExportTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith(BuiltinToolInstallationTests.Prefix, StringComparison.Ordinal) ||
            test.Name.StartsWith("nested-host ", StringComparison.Ordinal) ||
            test.Name.StartsWith("image-capability frontend RPC ", StringComparison.Ordinal) ||
            test.Name.StartsWith("terminal-session.", StringComparison.Ordinal) ||
            test.Name.StartsWith("terminal editor integration ", StringComparison.Ordinal) ||
            test.Name.StartsWith("live-interactive.", StringComparison.Ordinal) ||
            test.Name.StartsWith("node-command-terminal.", StringComparison.Ordinal) ||
            test.Name.StartsWith("native stateful Todo ", StringComparison.Ordinal) ||
            test.Name.StartsWith("native session checkpoint ", StringComparison.Ordinal) ||
            test.Name is "RPC direct host write/flush faults poison gated response authority and await durable close" or
                "RPC shared output failures retain RpcHostFailed with zero new cleanup failures for every API" or
                "RPC retained output fault preserves original phase and genuine late cleanup cause order" or
                "RPC Anthropic HTTP gate preserves queue/abort/EOF, output-fault authority and caller cancellation barriers" or
                "RPC Completions preserves held HTTP queues, abort/EOF, output faults and strict startup admission" or
                "RPC actual caller cancellation returns Canceled only after aborted durable work and lease closure") await test.Run();
        else await test.Run().WaitAsync(TimeSpan.FromSeconds(30));
        if (safeCaseProgress) EmitProgress("END", ordinal, testId, test.Name, "passed");
        else Console.WriteLine($"PASS {test.Name}");
        evidence.Add(new { testId, name = test.Name, status = "passed" });
    }
    catch (Exception error)
    {
        failed++;
        var diagnostics = TestFailureDiagnostics.Capture(error);
        if (safeCaseProgress)
        {
            EmitProgress("END", ordinal, testId, test.Name, "failed", diagnostics);
            evidence.Add(new { testId, name = test.Name, status = "failed", diagnostics });
        }
        else
        {
            Console.Error.WriteLine($"FAIL {test.Name}: {error}");
            evidence.Add(new { testId, name = test.Name, status = "failed", error = error.Message, diagnostics });
        }
    }
}
Console.WriteLine($"CodingAgent tests: {tests.Length - failed} passed, {failed} failed.");
if (report is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        schemaVersion = 1, sourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f",
        scope = commandTerminalPackage ? "explicit-opt-in-original-command-input-real-conpty-durable-session-processes" :
            "authored-native-durable-session-agent-and-offline-cli-composition", tests = evidence,
        commandTerminalPackage, nodeWorkerExecutionRequested = commandTerminalPackage,
        filter, exactName, availableTests, completeSuite = filter is null && exactName is null,
        safeCaseProgress, progressCaptureFailures,
        terminalDefaultSourceEvidence = TerminalDefaultSourceReplayTests.Evidence,
        offlineCliEvidence = OfflineDemoTests.Evidence,
        checkpointSessionEvidence = NativeExtensionSessionCommandTests.CheckpointProcessEvidence,
        terminalSessionEvidence = new { platform = WindowsConPtyTerminalSessionFixture.PlatformEvidence,
            workerReceipts = WindowsConPtyTerminalSessionFixture.WorkerReceipts,
            parentWitnesses = WindowsConPtyTerminalSessionFixture.ParentWitnesses },
        passed = tests.Length - failed, failed, upstreamDifferential = false, phaseAcceptanceClaimed = false
    }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
return failed == 0 && progressCaptureFailures == 0 ? 0 : 1;
