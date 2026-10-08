using PiSharp.Cli.Prompts;
using PiSharp.CodingAgent.Resources;
using static PromptTemplateDiscoveryTests;

internal static class PromptTemplateYamlTests
{
    public const string Prefix = "prompt template YAML ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "standard quoted and flow mapping metadata", Mapping),
        (Prefix + "literal folded and chomping metadata retain strings", Multiline),
        (Prefix + "untyped booleans numbers null and collections are not coerced", Types),
        (Prefix + "ordinary yes on and date scalars remain strings", CoreStrings),
        (Prefix + "anchors and aliases are resolved by standard library", Aliases),
        (Prefix + "alias count includes anchor and is independent per anchor", AliasBoundary),
        (Prefix + "nested aliases apply cached conversion weights", AliasWeights),
        (Prefix + "nested distinct anchors have proportional accounting visits", AliasAccountingWork),
        (Prefix + "cyclic and unresolved aliases reject before conversion", AliasCycles),
        (Prefix + "empty collections receive a bounded minimum alias weight", EmptyAliases),
        (Prefix + "empty comments and nonmapping roots produce empty metadata", Roots),
        (Prefix + "malformed duplicate and multiple documents reject", Invalid),
        (Prefix + "actual parser extraction invokes standard decoder and fallback", Extraction)
    ];
    private static PromptTemplateMetadata Decode(string yaml) => PromptTemplateFrontendDecoder.Decode(yaml);
    private static Task Mapping()
    {
        Equal(new PromptTemplateMetadata("A desc", "[focus]"), Decode("description: 'A desc'\nargument-hint: \"[focus]\"\nunknown: value"));
        Equal(new PromptTemplateMetadata("Flow", "file"), Decode("{description: Flow, argument-hint: file}"));
        Equal(new PromptTemplateMetadata(), Decode("Description: ignored\nARGUMENT-HINT: ignored")); return Task.CompletedTask;
    }
    private static Task Multiline()
    {
        Equal("Line one\nLine two\n", Decode("description: |\n  Line one\n  Line two\n").Description);
        Equal("Line one Line two\n", Decode("description: >\n  Line one\n  Line two\n").Description);
        Equal("true", Decode("description: |-\n  true\n").Description);
        Equal("42", Decode("description: |-\n  42\n").Description);
        Equal("null", Decode("description: |-\n  null\n").Description); return Task.CompletedTask;
    }
    private static Task Types()
    {
        foreach (var value in new[] { "true", "FALSE", "42", "1.5", "null", "~", "[a, b]", "{a: b}" })
            Equal(new PromptTemplateMetadata(), Decode("description: " + value + "\nargument-hint: " + value));
        Equal("42", Decode("description: '42'").Description); Equal("true", Decode("description: \"true\"").Description);
        Equal("null", Decode("description: !!str null").Description); Equal("42", Decode("description: !!str 42").Description);
        return Task.CompletedTask;
    }
    private static Task CoreStrings()
    {
        foreach (var value in new[] { "yes", "on", "no", "off", "2026-01-01" }) Equal(value, Decode("description: " + value).Description);
        return Task.CompletedTask;
    }
    private static Task Aliases()
    {
        Equal(new PromptTemplateMetadata("shared", "shared"), Decode("description: &text shared\nargument-hint: *text"));
        Equal("true", Decode("description: &text |-\n  true\nargument-hint: *text\n").ArgumentHint); return Task.CompletedTask;
    }
    private static Task Roots()
    {
        foreach (var yaml in new[] { "", "# comment only", "null", "42", "plain", "[a, b]" }) Equal(new PromptTemplateMetadata(), Decode(yaml));
        return Task.CompletedTask;
    }
    private static string References(string anchor, int count) => "[" + string.Join(", ", Enumerable.Repeat("*" + anchor, count)) + "]";
    private static void AliasRejected(string yaml)
    {
        var rejected = false;
        try { Decode(yaml); }
        catch (Exception error) when (error.GetType().Namespace?.StartsWith("YamlDotNet", StringComparison.Ordinal) == true)
        { rejected = true; }
        Equal(true, rejected);
    }
    private static Task AliasBoundary()
    {
        Equal("normal", Decode("description: &a normal\nunused: " + References("a", 99)).Description);
        AliasRejected("description: &a normal\nunused: " + References("a", 100));
        // There is no unrelated global cap of100 aliases: each anchor has its own accounting.
        Equal("normal", Decode("description: &a normal\nargument-hint: &b hint\none: " + References("a", 99) +
            "\ntwo: " + References("b", 99)).Description);
        return Task.CompletedTask;
    }
    private static Task AliasWeights()
    {
        var header = "description: &a normal\nb: &b " + References("a", 9) + "\nc: ";
        Equal("normal", Decode(header + References("b", 9)).Description);
        AliasRejected(header + References("b", 10));
        // Weight is cached on the first reference, not recalculated after later aliases.
        Equal("normal", Decode("description: &a normal\nb: &b [*a]\nc: *b\nd: " + References("a", 90) +
            "\ne: " + References("b", 48)).Description);
        return Task.CompletedTask;
    }
    private static Task AliasCycles()
    {
        foreach (var yaml in new[] { "description: &a [*a]", "description: &a {child: [*a]}",
            "description: *a\nother: &a later", "description: *missing" }) AliasRejected(yaml);
        Equal(new PromptTemplateMetadata("normal", "normal"), Decode("description: &a normal\nargument-hint: *a"));
        return Task.CompletedTask;
    }
    private static Task AliasAccountingWork()
    {
        // This accepted shape made the previous first-reference subtree scans quadratic.
        // Inspect the actual preconversion guard, avoiding a deserializer depth-limit claim.
        foreach (var depth in new[] { 32, 128, 512 })
        {
            var yaml = "description: normal\ntree: " +
                string.Concat(Enumerable.Range(0, depth).Select(index => "&a" + index + " [")) +
                "normal" + new string(']', depth) + "\nunused: [" +
                string.Join(", ", Enumerable.Range(0, depth).Select(index => "*a" + index)) + "]";
            var observed = PromptTemplateFrontendDecoder.InspectAliasAccounting(yaml);
            Equal(2 * depth + 7, observed.SyntaxNodes);
            Equal((long)depth, observed.ReferenceVisits);
            Equal(true, observed.WeightVisits <= 8L * observed.SyntaxNodes);
        }
        // A hot scalar anchor exercises reference refreshes and monotone propagation.
        var hot = PromptTemplateFrontendDecoder.InspectAliasAccounting("description: &a normal\nunused: " + References("a", 99));
        Equal(true, hot.ReferenceVisits <= 100L * hot.SyntaxNodes);
        Equal(true, hot.WeightVisits <= 256L * hot.SyntaxNodes);
        // A collection must see alias counts at its first use, rather than parse-close time.
        var header = "description: &a normal\ntree: &b [*a]\nincrement: *a\nunused: ";
        Equal("normal", Decode(header + References("b", 32)).Description);
        AliasRejected(header + References("b", 33));
        return Task.CompletedTask;
    }
    private static Task EmptyAliases()
    {
        Equal("normal", Decode("description: normal\na: &a []\nb: " + References("a", 99)).Description);
        AliasRejected("description: normal\na: &a []\nb: " + References("a", 100));
        return Task.CompletedTask;
    }
    private static Task Invalid()
    {
        foreach (var yaml in new[] { "description: [bad", "description: first\ndescription: second", "description: first\n---\ndescription: second" })
        {
            var rejected = false; try { Decode(yaml); } catch (Exception error) when (error.GetType().Namespace?.StartsWith("YamlDotNet", StringComparison.Ordinal) == true) { rejected = true; }
            Equal(true, rejected);
        }
        return Task.CompletedTask;
    }
    private static Task Extraction()
    {
        var parsed = PromptTemplateParser.Parse("review.md", "\uFEFF---\r\ndescription: ''\r\nargument-hint: '[focus]'\r\n---\r\n\r\nReview $1\r\n", Decode);
        Equal("Review $1", parsed.Content); Equal("Review $1", parsed.Description); Equal("[focus]", parsed.ArgumentHint);
        Equal("Review change", PromptTemplateExpander.ExpandPromptTemplate("/review change", [parsed])); return Task.CompletedTask;
    }
}
