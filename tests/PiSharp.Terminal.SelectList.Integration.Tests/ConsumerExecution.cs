internal static class ConsumerExecution
{
    internal static async Task<int> RunAsync(string reportPath, ConsumerCasePlan[] plan, object requested,
        Func<ConsumerEvidence, Task> body, TextWriter errors, bool admissionRequired = true)
    {
        ConsumerEvidence evidence;
        try { evidence = ConsumerEvidence.Open(reportPath, plan, requested, admissionRequired); }
        catch (Exception error) { try { errors.WriteLine(error.ToString()); } catch { } return 1; }
        try { evidence.Started(); await body(evidence); evidence.BodyCompleted(); }
        catch (Exception error) { evidence.Abort(error); evidence.Error(errors, error.ToString()); }
        return evidence.Finish(errors);
    }
}
