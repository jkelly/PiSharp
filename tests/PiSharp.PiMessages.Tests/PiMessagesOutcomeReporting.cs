internal static class PiMessagesOutcomeReporting
{
    internal sealed record Outcome(string Category, string Behavior, string Qualification, string? Failure)
    {
        public bool Nonpassing => Behavior != "COMPLETED" || Qualification != "COMPLETED";
    }
    internal static Outcome Classify(string dependencies, bool domainGap, Exception? failure, bool unexecuted = false)
    {
        if (failure is not null || dependencies == "FAILED")
            return new("BEHAVIORAL_FAIL", "FAIL", "NONPASSING", failure?.ToString() ?? "A behavioral assertion group failed; see complete ledger.");
        if (unexecuted)
            return new("UNEXECUTED", "UNEXECUTED", "OPEN", null);
        if (domainGap)
            return new("OPEN_DOMAIN_GAP", "COMPLETED", "OPEN", null);
        if (dependencies != "COMPLETED")
            return new("OPEN_CRITERION", dependencies == "OPEN" ? "NO_FAILED_ASSERTIONS" : "INCOMPLETE", "OPEN", null);
        return new("AUTHORED_CHECKS_COMPLETED", "COMPLETED", "COMPLETED", null);
    }
}
