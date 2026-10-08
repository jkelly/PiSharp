using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI;
using PiSharp.AI.Protocols.PiMessages;
using PiSharp.Agent;
using PiSharp.Contracts;

// Versioned, explicitly selected native evidence profiles. Full-runner identity migration
// separately retains frozen historical criteria. Results alone cannot grant source/reviewer acceptance.
internal static class IdentitySuccessorAdmission
{
    private const string SourceSha = "d86654abb8862e201933517d6f1fce9f88dd117f";
    private const string OriginalSha = "1b8a6fbb80726e6f1a6cb1848d2df9b7da41c9015d91592d9f077f2aa8c04891";
    private const string SuccessorSha = "ee6505f14fcd43d1ba7d488c2dc0e03951512a2d6a897b0cc1ac742b3f21ccfa";
    private const string OutputSha = "93c80e37db4fa20ef386594beeb6a609f5c9996738ed7de45373543dbfaf2a52";
    private const string CasePath = "tests/PiSharp.PiMessages.Tests/identity-successor-case-r1.json";
    private const string OutputPath = "tests/PiSharp.PiMessages.Tests/identity-successor-output-r1.json";
    private const string OriginalPath = "tests/PiSharp.PiMessages.Tests/authored-cases-r2.json";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    // Harness records can contain borrowed JsonElements (for example source headers).
    // Materialize the entire report value while their fixture documents are alive.
    internal static JsonElement OwnReportObservations(object value) => JsonSerializer.SerializeToElement(value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Resolve(string root, string relative)
    {
        Require(!Path.IsPathRooted(relative) && !relative.Contains(':') && !relative.Split('/').Any(part => part is "" or "." or ".."), "Admission requires a repository-relative pin.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Require(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Pin escaped repository root."); return path;
    }
    internal static async Task<int> RunAsync(string[] args)
    {
        var controlsOnly = args[0] == "--identity-controls-r1";
        Require(args.Length == (controlsOnly ? 3 : 4), "Use selected mode, [successor fixture], admission fixture, and fresh report path.");
        var admissionPath = Path.GetFullPath(args[controlsOnly ? 1 : 2]);
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(admissionPath)!, "..", ".."));
        using var admission = JsonDocument.Parse(await File.ReadAllBytesAsync(admissionPath));
        var plan = admission.RootElement;
        Require(plan.GetProperty("schemaVersion").GetInt32() == 1 && plan.GetProperty("profileVersion").GetString() == "identity-successor-r1" && plan.GetProperty("sourceSha").GetString() == SourceSha, "Version/source pin mismatch.");
        var pinChecks = new List<object>();
        foreach (var pin in plan.GetProperty("filePins").EnumerateArray())
        {
            var relative = pin.GetProperty("path").GetString()!; var path = Resolve(root, relative); var actual = Hash(path);
            Require(actual == pin.GetProperty("sha256").GetString() && new FileInfo(path).Length == pin.GetProperty("bytes").GetInt64(), "Admission source/fixture/output pin differs: " + relative);
            pinChecks.Add(new { path = relative, sha256 = actual });
        }
        Require(Hash(Resolve(root, OriginalPath)) == OriginalSha && Hash(Resolve(root, CasePath)) == SuccessorSha && Hash(Resolve(root, OutputPath)) == OutputSha, "Compiled predecessor/successor/output identity mismatch.");
        if (!controlsOnly) Require(string.Equals(Path.GetFullPath(args[1]), Resolve(root, CasePath), StringComparison.OrdinalIgnoreCase), "Selected successor fixture path differs.");
        var runtimeAssemblies = new[] { typeof(IdentitySuccessorAdmission).Assembly, typeof(ChatClient).Assembly, typeof(ToolInvoker).Assembly, typeof(PiWireJson).Assembly }
            .Select(assembly => new { name = assembly.GetName().Name, mvid = assembly.ManifestModule.ModuleVersionId, sha256 = Hash(assembly.Location) }).ToArray();
        // Build/source and produced-output receipt linkage is mandatory in the outer
        // coordinator admission. This local profile reports observations, never acceptance.
        object observations; var failures = 0;
        if (controlsOnly)
        {
            var controls = await PiMessagesIdentityReplacementCases.RunAsync(); failures = controls.Count(control => control.Status == "FAIL");
            Require(controls.Count == 7, "Selected safety control count differs.");
            var continuation = await IdentitySuccessorContinuation.RunAsync(root);
            if (continuation.Status == "FAIL") failures++;
            observations = new { controls, continuation };
        }
        else
        {
            await ReportLifetimeControls.RunAsync();
            using var specDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Resolve(root, CasePath)));
            using var originalDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Resolve(root, OriginalPath)));
            using var outputDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(Resolve(root, OutputPath)));
            var spec = specDocument.RootElement;
            var original = originalDocument.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "PM-TOOL-IDENTITY-REPLACEMENT");
            Require(original.GetProperty("expected").GetProperty("domainGap").GetBoolean(), "Historical gap must remain retained.");
            var scenario = JsonNode.Parse(original.GetRawText())!.AsObject();
            scenario["assertions"] = JsonNode.Parse(spec.GetProperty("assertions").GetRawText());
            scenario["expected"] = JsonNode.Parse(outputDocument.RootElement.GetProperty("expected").GetRawText());
            using var effective = JsonDocument.Parse(scenario.ToJsonString());
            var ledger = new AssertionLedger(effective.RootElement, spec.GetProperty("dependencies")); var harness = new HeldOwnershipHarnessR2();
            string? failure = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var invocation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            try
            {
                await Program.Run(effective.RootElement, typeof(PiMessagesOptions), typeof(PiMessagesHttpSseTransport), typeof(PiMessagesLifecycleHooks), typeof(PiMessagesKeyAuthRequestFactory),
                    harness, ledger, invocation, deadline.Token, [], identitySuccessor: true);
                Require(!harness.Unjoined && ledger.VariantDependencyStatus() == "COMPLETED", "Successor native constituents incomplete.");
            }
            catch (Exception error) { failures++; failure = error.ToString(); }
            observations = OwnReportObservations(new { successorCaseId = spec.GetProperty("id").GetString(), historicalCase = original.Clone(),
                effectiveSuccessor = effective.RootElement.Clone(), failure, assertions = ledger.Snapshot([ledger], 1), actuals = harness.Observations(),
                sourceQualification = "OPEN; AUTHORED EXPECTATIONS ARE NOT A GENUINE SOURCE CAPTURE" });
        }
        await using var report = new FileStream(args[^1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(report, new { profile = args[0], status = "NATIVE OBSERVATIONS; INDEPENDENT ADMISSION/REVIEW AND SOURCE QUALIFICATION OPEN",
            admissionSha256 = Hash(admissionPath), pinChecks, runtimeAssemblies, failures, observations,
            continuationCriterion3 = "OPEN_UNTIL_MATCHING_RUNTIME_RECEIPTS_AND_REVIEWER_ACCEPTANCE", genuineSourceCasesCaptured = 0, allEightPhaseGates = "OPEN" }, new JsonSerializerOptions { WriteIndented = true });
        return failures == 0 ? 0 : 1;
    }
}
