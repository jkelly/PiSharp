using System.Reflection;

namespace PiSharp.Qualification;
internal static class GrammarQualificationEntry
{
    private static Task<int> Main(string[] args)
    {
        // Bind exact compiled original methods, not copies of their implementation.
        (string Name, string Method)[] roster = [
            ("default-grammar-fallback", "DefaultFallback"),
            ("unsupported-strict-fallback", "UnsupportedStrictFallback"),
            ("supported-json-schema-strict-preserved", "SupportedStrictPreserved"),
            ("json-schema-require-refusal-preserved", "JsonSchemaRefusalPreserved"),
            ("input-budget-and-cancellation-preserved", "BoundAndCancelPreserved"),
            ("actual-public-factory-wire", "PublicRoute")
        ];
        return QualificationEvidence.RunAsync(args, () => roster.Select(item =>
        {
            var method = typeof(global::Program).GetMethod(item.Method, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("Exact original grammar control absent: " + item.Method);
            if (method.ReturnType != typeof(Task) || method.GetParameters().Length != 0)
                throw new InvalidOperationException("Original grammar control signature differs.");
            return (item.Name, method.CreateDelegate<Func<Task>>());
        }).ToArray(), 6, () => []);
    }
}