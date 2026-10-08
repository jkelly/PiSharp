using System.Text.Json;

// Authored reporting checks. No source qualification or tool execution is granted.
internal static class OutcomeReportingControls
{
    internal sealed record Outcome(string Status, string? Error);
    internal static Outcome Run()
    {
        try
        {
            using var scenario = JsonDocument.Parse("""{"assertions":[{"id":"case/1","name":"behavior"},{"id":"case/2","name":"orchestration"}]}""");
            using var dependencies = JsonDocument.Parse("""[{"id":"case/1","name":"behavior","priorGroup":"behavior","requiredGroups":["behavior"],"scope":"CASE"},{"id":"case/2","name":"orchestration","priorGroup":"integration-open","requiredGroups":["integration-open"],"scope":"CASE"}]""");
            AssertionLedger Ledger() => new(scenario.RootElement, dependencies.RootElement);
            void Check(bool condition) { if (!condition) throw new InvalidOperationException("Reporting control differs from required FAIL/OPEN classification."); }

            var open = Ledger(); open.Check("behavior", () => { }); open.Open("integration-open", "Agent/ToolInvoker orchestration remains unqualified.");
            var openOutcome = PiMessagesOutcomeReporting.Classify(open.VariantDependencyStatus(), false, null);
            Check(openOutcome.Category == "OPEN_CRITERION" && openOutcome.Behavior == "NO_FAILED_ASSERTIONS" && openOutcome.Failure is null && openOutcome.Nonpassing);
            var snapshot = JsonSerializer.SerializeToElement(open.Snapshot([open], 1));
            Check(snapshot.GetProperty("groups").EnumerateArray().All(group => group.GetProperty("failed").GetInt32() == 0));
            Check(snapshot.GetProperty("namedCriteria")[0].GetProperty("status").GetString() == "COMPLETED");
            Check(snapshot.GetProperty("namedCriteria")[1].GetProperty("status").GetString() == "OPEN");

            var completed = Ledger(); completed.Check("behavior", () => { }); completed.Check("integration-open", () => { });
            var domainGap = PiMessagesOutcomeReporting.Classify(completed.VariantDependencyStatus(), true, null);
            Check(domainGap.Category == "OPEN_DOMAIN_GAP" && domainGap.Failure is null && domainGap.Nonpassing);
            Check(!PiMessagesOutcomeReporting.Classify(completed.VariantDependencyStatus(), false, null).Nonpassing);

            var failed = Ledger();
            try { failed.Check("behavior", () => throw new InvalidOperationException("authored assertion fault")); } catch (InvalidOperationException) { }
            failed.Open("integration-open", "Open qualification cannot conceal a failed check.");
            Check(PiMessagesOutcomeReporting.Classify(failed.VariantDependencyStatus(), true, null).Category == "BEHAVIORAL_FAIL");
            try { completed.Check("additional-owned-check", () => throw new InvalidOperationException("authored additional group fault")); } catch (InvalidOperationException) { }
            Check(PiMessagesOutcomeReporting.Classify(completed.VariantDependencyStatus(), false, null).Category == "BEHAVIORAL_FAIL");
            var runtimeFault = PiMessagesOutcomeReporting.Classify("OPEN", true, new InvalidOperationException("authored runtime fault"));
            Check(runtimeFault.Category == "BEHAVIORAL_FAIL" && runtimeFault.Failure!.Contains("authored runtime fault", StringComparison.Ordinal));
            var incomplete = Ledger(); incomplete.Check("behavior", () => { });
            Check(PiMessagesOutcomeReporting.Classify(incomplete.VariantDependencyStatus(), false, null).Behavior == "INCOMPLETE");
            Check(PiMessagesOutcomeReporting.Classify("UNEXECUTED", false, null, unexecuted: true).Nonpassing);
            return new("PASS_AUTHORED_REPORTING_ONLY", null);
        }
        catch (Exception error) { return new("FAIL", error.ToString()); }
    }
}
