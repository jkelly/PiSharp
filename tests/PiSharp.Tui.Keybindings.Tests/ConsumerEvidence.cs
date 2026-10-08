using System.Text;
using System.Text.Json;

internal sealed record ConsumerCasePlan(string Category, string Id);
internal sealed record ConsumerException(string Type, string Message, int HResult, string? StackTrace, string Detail)
{
    internal static ConsumerException From(Exception error) => new(error.GetType().FullName ?? error.GetType().Name,
        error.Message, error.HResult, error.StackTrace, error.ToString());
}

/// <summary>Durable append-only observations, then one exclusive final JSON report, including on failure.</summary>
internal sealed class ConsumerEvidence
{
    private sealed class CaseState(ConsumerCasePlan plan)
    {
        internal ConsumerCasePlan Plan { get; } = plan;
        internal string Status { get; set; } = "unexecuted";
        internal JsonElement? Scenario { get; set; }
        internal List<JsonElement> Observations { get; } = [];
        internal JsonElement? Result { get; set; }
        internal bool? Passed { get; set; }
        internal ConsumerException? Exception { get; set; }
    }

    private readonly string reportPath;
    private readonly FileStream journal;
    private readonly StreamWriter writer;
    private readonly CaseState[] cases;
    private readonly object requestedInputs;
    private readonly bool admissionRequired;
    private readonly List<JsonElement> observations = [];
    private readonly List<ConsumerException> ioFailures = [];
    private CaseState? active;
    private ConsumerException? exception;
    private JsonElement? admittedIdentity;
    private string stage = "initialization";
    private bool bodyCompleted, journalHealthy = true;
    private long sequence;

    private ConsumerEvidence(string reportPath, ConsumerCasePlan[] plan, object requestedInputs, bool admissionRequired)
    {
        if (File.Exists(reportPath) || Directory.Exists(reportPath)) throw new IOException("Fresh report path required");
        if (plan.Select(value => (value.Category, value.Id)).Distinct().Count() != plan.Length)
            throw new ArgumentException("Duplicate evidence case plan");
        this.reportPath = reportPath;
        this.requestedInputs = requestedInputs;
        this.admissionRequired = admissionRequired;
        cases = plan.Select(value => new CaseState(value)).ToArray();
        JournalPath = reportPath + ".journal.jsonl";
        journal = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        try { writer = new StreamWriter(journal, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\n" }; }
        catch { journal.Dispose(); throw; }
    }

    internal string JournalPath { get; }
    internal static ConsumerEvidence Open(string reportPath, ConsumerCasePlan[] plan, object requestedInputs, bool admissionRequired) =>
        new(reportPath, plan, requestedInputs, admissionRequired);

    internal void Started() => Append("started", new { requestedInputs, plan = cases.Select(value => value.Plan).ToArray() });
    internal void Stage(string value) { stage = value; Append("stage", new { stage }); }
    internal void Observe(string kind, object value)
    {
        var captured = JsonSerializer.SerializeToElement(new { kind, value });
        if (active is null) observations.Add(captured); else active.Observations.Add(captured);
        Append("observation", captured);
    }

    internal void Admit(EvidenceAdmission identity)
    {
        admittedIdentity = JsonSerializer.SerializeToElement(new { identity.Candidate, identity.Tree, identity.ReviewRoot,
            identity.ManifestSha256, identity.ReceiptSha256, identity.SourceFilesVerified, identity.Assemblies, identity.Host });
        Append("admitted", admittedIdentity);
    }

    internal void Begin(string category, string id, object? scenario = null)
    {
        if (active is not null) throw new InvalidOperationException("A prior case is still active");
        stage = category + ":" + id;
        var value = cases.Single(item => item.Plan.Category == category && item.Plan.Id == id);
        if (value.Status != "unexecuted") throw new InvalidOperationException("Evidence case already started");
        active = value; active.Status = "running";
        if (scenario is not null) active.Scenario = JsonSerializer.SerializeToElement(scenario);
        Append("case-start", new { category, id, scenario = active.Scenario });
    }

    internal void Complete(object result)
    {
        var value = active ?? throw new InvalidOperationException("No active evidence case");
        value.Result = JsonSerializer.SerializeToElement(result);
        value.Passed = value.Result.Value.GetProperty("passed").GetBoolean();
        value.Status = "completed";
        Append("case-completed", new { value.Plan.Category, value.Plan.Id, result = value.Result });
        active = null;
    }

    internal void BodyCompleted() => bodyCompleted = true;
    internal void Error(TextWriter output, string detail)
    {
        try { output.WriteLine(detail); }
        catch (Exception error) { ioFailures.Add(ConsumerException.From(error)); }
    }
    internal void Abort(Exception error)
    {
        exception = ConsumerException.From(error);
        if (active is not null) { active.Status = "exception"; active.Exception = exception; }
        // A failed journal receives no new events; final JSON retains the in-memory observations.
        try { Append("exception", new { stage, exception }); }
        catch (Exception journalError) { ioFailures.Add(ConsumerException.From(journalError)); }
    }

    private void Append(string kind, object? value)
    {
        if (!journalHealthy) return;
        try
        {
            writer.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, kind, stage,
                activeCase = active?.Plan, value }));
            writer.Flush(); journal.Flush(flushToDisk: true);
        }
        catch (Exception error)
        {
            journalHealthy = false; ioFailures.Add(ConsumerException.From(error)); throw;
        }
    }

    private object Snapshot()
    {
        var unexecuted = cases.Where(value => value.Status == "unexecuted").Select(value => value.Plan).ToArray();
        var complete = bodyCompleted && exception is null && (!admissionRequired || admittedIdentity is not null) &&
            cases.All(value => value.Status == "completed");
        var assertionFailures = cases.Count(value => value.Passed == false);
        return new
        {
            schemaVersion = 2, kind = "AUTHORED REGISTRY / BASELINE AND CONFIGURED EDITOR EVIDENCE; NO PARITY ACCEPTANCE",
            status = complete ? assertionFailures == 0 && ioFailures.Count == 0 ? "completed" : "completed-with-failures" : "incomplete",
            incomplete = !complete, passed = complete && assertionFailures == 0 && ioFailures.Count == 0,
            stage, requestedInputs, admissionRequired, admissionSucceeded = admittedIdentity is not null, admittedIdentity,
            observations, plannedCases = cases.Length, completedCases = cases.Count(value => value.Status == "completed"),
            assertionFailures, exception, ioFailures,
            completedObservations = cases.Where(value => value.Status == "completed").Select(value => new
            { value.Plan.Category, value.Plan.Id, completeAuthoredScenario = value.Scenario, observations = value.Observations, result = value.Result }).ToArray(),
            incompleteCases = cases.Where(value => value.Status is "running" or "exception").Select(value => new
            { value.Plan.Category, value.Plan.Id, value.Status, completeAuthoredScenario = value.Scenario,
                observations = value.Observations, result = value.Result, exception = value.Exception }).ToArray(),
            unexecutedCases = unexecuted, journalPath = JournalPath, journalHealthy,
            authoredRegistryContractOnly = false, authoredExpectationsOnly = true, nativeKeyProtocolParity = false, sourceCapturesExecuted = 0,
            sharedControllerConfigurationApplied = cases.Any(value => value.Plan.Category == "configured-editor" && value.Status == "completed"),
            baselineControllerConfigurationApplied = false, configuredEditorCasesPresent = cases.Any(value => value.Plan.Category == "configured-editor"),
            configuredEditorBehaviorQualified = false,
            configuredRoutesPresent = cases.Any(value => value.Plan.Category is "legacy-route" or "acknowledged-route"),
            configuredRouteCasesCompleted = cases.Count(value => (value.Plan.Category is "legacy-route" or "acknowledged-route") && value.Status == "completed"),
            startupCaseCompleted = cases.Any(value => value.Plan.Category == "startup" && value.Status == "completed"),
            fullNativeGate = false, physicalTerminalGate = false, originalPackageAcceptance = false,
            all79OriginalScopesPreserved = true, historicalAcceptedTestedProfileOwnScopes = new[] { "P4-03", "P4-04", "P4-05", "P4-06", "P4-08" },
            dependencyCompletePackages = 0, allEightPhaseGatesOpen = true
        };
    }

    internal int Finish(TextWriter errorOutput)
    {
        try { Append("run-end", Snapshot()); }
        catch (Exception error) { Error(errorOutput, error.ToString()); }
        // Close each owned resource before serializing the final report so close failures remain nonpassing diagnostics.
        try { writer.Dispose(); }
        catch (Exception error) { journalHealthy = false; ioFailures.Add(ConsumerException.From(error)); Error(errorOutput, error.ToString()); }
        try { journal.Dispose(); }
        catch (Exception error) { journalHealthy = false; ioFailures.Add(ConsumerException.From(error)); Error(errorOutput, error.ToString()); }
        var passed = bodyCompleted && exception is null && (!admissionRequired || admittedIdentity is not null) && ioFailures.Count == 0 && cases.All(value =>
            value.Status == "completed" && value.Passed == true);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Snapshot(), new JsonSerializerOptions { WriteIndented = true });
            using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.Write(bytes); output.Flush(flushToDisk: true);
        }
        catch (Exception error)
        {
            Error(errorOutput, "Final report write failed; retain append-only journal: " + JournalPath);
            Error(errorOutput, error.ToString()); return 1;
        }
        foreach (var error in ioFailures.ToArray()) Error(errorOutput, error.Detail);
        return passed ? 0 : 1;
    }
}
