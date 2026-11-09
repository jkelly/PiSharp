using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Evals;

[JsonConverter(typeof(JsonStringEnumConverter<DocumentationArm>))]
public enum DocumentationArm { without_docs, with_docs }
[JsonConverter(typeof(JsonStringEnumConverter<EvalStatus>))]
public enum EvalStatus { scored, unscored, skipped, pending, errored }
public sealed record EvalCase(string Id, string Category, string Prompt, string ExpectedText);
public sealed record EvalTask(EvalCase Case, string Model, int Repetition, DocumentationArm Arm)
{
    public string PairId => JsonSerializer.Serialize(new object[] { Case.Id, Model, Repetition });
    public string Id => JsonSerializer.Serialize(new object[] { Case.Id, Model, Repetition, Arm.ToString() });
    public string ArtifactId => EvalJson.Hash(Id);
}
public static class EvalJson
{
    public static JsonSerializerOptions Options { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
public static class EvalPlan
{
    public static IReadOnlyList<EvalTask> Create(IEnumerable<EvalCase> source, string model, int repetitions)
    {
        var cases = ValidateCases(source);
        if (string.IsNullOrWhiteSpace(model) || !model.Contains('/') || model.StartsWith('/') || model.EndsWith('/'))
            throw new ArgumentException("Model must be provider/model.");
        if (repetitions < 1 || repetitions > 1000) throw new ArgumentOutOfRangeException(nameof(repetitions));
        return cases.SelectMany(c => Enumerable.Range(1, repetitions).SelectMany(r =>
            (r % 2 == 1 ? new[] { DocumentationArm.without_docs, DocumentationArm.with_docs } :
                new[] { DocumentationArm.with_docs, DocumentationArm.without_docs }).Select(a => new EvalTask(c, model, r, a)))).ToArray();
    }
    public static EvalCase[] ValidateCases(IEnumerable<EvalCase> source)
    {
        var cases = source.ToArray();
        if (cases.Length == 0 || cases.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Category) ||
            string.IsNullOrWhiteSpace(c.Prompt) || string.IsNullOrWhiteSpace(c.ExpectedText)))
            throw new ArgumentException("A cohort must contain complete cases.");
        if (cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Length) throw new ArgumentException("Duplicate case IDs.");
        return cases.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }
    public static void RequireEqualCohorts(IEnumerable<EvalCase> control, IEnumerable<EvalCase> treatment)
    {
        if (!ValidateCases(control).SequenceEqual(ValidateCases(treatment))) throw new ArgumentException("Documentation cohorts differ.");
    }
    // Native C# API questions, kept separate from host smoke tests; scores are literal answer checks, not code execution claims.
    public static IReadOnlyList<EvalCase> BuiltInCases { get; } = [
        new("smoke.identity", "smoke", "Reply with exactly PISHARP_EVAL_READY", "PISHARP_EVAL_READY"),
        new("extension.namespace", "extensions", "In PiSharp's native C# extension API, what is the namespace containing extension interfaces? Reply with the namespace only.", "PiSharp.Extensions"),
        new("models.namespace", "models", "In PiSharp, what namespace contains the public FrozenModelCatalog API? Reply with the namespace only.", "PiSharp.AI.Catalogs"),
        new("prompts.arguments", "prompt-templates", "What token in a PiSharp prompt template expands to all supplied arguments? Reply with the literal token only.", "$ARGUMENTS")
    ];
}
