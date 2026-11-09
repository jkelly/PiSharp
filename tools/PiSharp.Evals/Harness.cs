namespace PiSharp.Evals;

public interface IEvalRuntime
{
    string Evidence { get; }
    Task<IReadOnlyList<EvalCase>> DiscoverAsync(DocumentationArm arm, CancellationToken cancellationToken);
    Task<EvalResult> RunAsync(EvalTask task, CancellationToken cancellationToken);
}
public static class EvalHarness
{
    public static async Task<EvalReport> RunAsync(IEvalRuntime runtime, string model, int repetitions, CancellationToken cancellationToken = default)
    {
        var control = await runtime.DiscoverAsync(DocumentationArm.without_docs, cancellationToken);
        var treatment = await runtime.DiscoverAsync(DocumentationArm.with_docs, cancellationToken);
        EvalPlan.RequireEqualCohorts(control, treatment); // complete both discoveries before executing any arm
        var plan = EvalPlan.Create(control, model, repetitions);
        var results = new List<EvalResult>();
        foreach (var task in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { results.Add(await runtime.RunAsync(task, cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { results.Add(new(task, EvalStatus.errored, Error: error.Message)); }
        }
        return EvalReport.Create(plan, results, runtime.Evidence);
    }
}
public sealed class FixtureRuntime(IReadOnlyList<EvalCase>? cases = null) : IEvalRuntime
{
    public string Evidence => "synthetic fixture; no provider execution";
    public Task<IReadOnlyList<EvalCase>> DiscoverAsync(DocumentationArm arm, CancellationToken cancellationToken) => Task.FromResult(cases ?? EvalPlan.BuiltInCases);
    public Task<EvalResult> RunAsync(EvalTask task, CancellationToken cancellationToken) => Task.FromResult(new EvalResult(task, EvalStatus.scored,
        task.Arm == DocumentationArm.with_docs || task.Case.Category == "smoke" ? 1 : 0));
}
