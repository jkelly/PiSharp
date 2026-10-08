using PiSharp.CodingAgent.Resources;

internal static class PromptTemplateCatalogTests
{
    public const string Prefix = "prompt template catalog ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "ordered first-name winner and ordinal collisions", Ordered),
        (Prefix + "read and decoder warnings precede collision diagnostics", Diagnostics),
        (Prefix + "caller path and provenance remain values without reads", Identities),
        (Prefix + "snapshot does not follow caller list changes", Snapshot),
        (Prefix + "empty YAML bypasses decoder and metadata remains injected", Metadata),
        (Prefix + "pre-cancellation prevents enumeration and decoding", PreCancellation),
        (Prefix + "decoder original cancellation and late cancellation are retained", DecoderCancellation),
        (Prefix + "caller enumeration failure is not a file warning", EnumerationFailure)
    ];

    private static Task Ordered()
    {
        var result = Build([Text("first/review.md", "first"), Text("second/review.md", "second"), Text("third/Review.md", "case")]);
        Equal(2, result.Resources.Length); Equal("first", result.Resources[0].Template.Content);
        Equal("Review", result.Resources[1].Template.Name);
        Equal(new PromptTemplateResourceDiagnostic(PromptTemplateResourceDiagnosticType.Collision,
            "name \"/review\" collision", "second/review.md", new("review", "first/review.md", "second/review.md")), result.Diagnostics.Single());
        return Task.CompletedTask;
    }

    private static Task Diagnostics()
    {
        var original = new FormatException("Synthetic malformed YAML");
        var result = PromptTemplateCatalogBuilder.Build([
            Text("a/review.md", "first"), Text("b/review.md", "second"),
            new PromptTemplateReadFailure(Identity("unreadable.md"), "Supplied read failure"),
            Text("invalid.md", "---\nfoo: [bar\n---\nBody"), Text("last.md", "last")], _ => throw original);
        Equal(2, result.Resources.Length); Equal("last", result.Resources[1].Template.Content);
        Equal(3, result.Diagnostics.Length);
        Equal(new PromptTemplateResourceDiagnostic(PromptTemplateResourceDiagnosticType.Warning, "Supplied read failure", "unreadable.md"), result.Diagnostics[0]);
        Equal(new PromptTemplateResourceDiagnostic(PromptTemplateResourceDiagnosticType.Warning, original.Message, "invalid.md"), result.Diagnostics[1]);
        Equal(PromptTemplateResourceDiagnosticType.Collision, result.Diagnostics[2].Type);
        return Task.CompletedTask;
    }

    private static Task Identities()
    {
        // Invalid filesystem syntax is deliberately only caller metadata; there is no path API or read.
        var source = new PromptTemplateSourceInfo("caller://project/source", "supplied-package", PromptTemplateSourceScope.Project,
            PromptTemplateSourceOrigin.Package, "caller://base");
        var identity = new PromptTemplateResourceIdentity("not-a-file\0path", "review.md", source);
        var result = Build([new PromptTemplateReadText(identity, "supplied text")]);
        Equal(identity.FilePath, result.Resources.Single().FilePath);
        Equal(source, result.Resources.Single().SourceInfo); Equal("review", result.Resources.Single().Template.Name);
        return Task.CompletedTask;
    }

    private static Task Snapshot()
    {
        var input = new List<PromptTemplateReadOutcome> { Text("review.md", "old") };
        var result = Build(input); input[0] = Text("review.md", "new"); input.Clear();
        Equal("old", result.Resources.Single().Template.Content); Equal(0, result.Diagnostics.Length);
        return Task.CompletedTask;
    }

    private static Task Metadata()
    {
        var calls = 0;
        var result = PromptTemplateCatalogBuilder.Build([Text("empty.md", "---\n---\nBody"),
            Text("metadata.md", "---\n# synthetic metadata\n---\nBody")], yaml =>
            { calls++; Equal("# synthetic metadata", yaml); return new("description", "[focus]"); });
        Equal(1, calls); Equal("Body", result.Resources[0].Template.Description);
        Equal("description", result.Resources[1].Template.Description); Equal("[focus]", result.Resources[1].Template.ArgumentHint);
        return Task.CompletedTask;
    }

    private static Task PreCancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var enumerated = false;
        IEnumerable<PromptTemplateReadOutcome> Values() { enumerated = true; yield return Text("review.md", "Body"); }
        try { PromptTemplateCatalogBuilder.Build(Values(), _ => throw new Exception("Decoder should not run"), cancellation.Token); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token)
        { Equal(false, enumerated); return Task.CompletedTask; }
        throw new InvalidOperationException("Pre-cancellation did not stop catalog construction.");
    }

    private static Task DecoderCancellation()
    {
        using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var original = new OperationCanceledException("Original decoder cancellation", foreign.Token);
        var observed = false;
        try { PromptTemplateCatalogBuilder.Build([Text("review.md", "---\nyaml\n---\nBody")], _ => throw original); }
        catch (OperationCanceledException error) when (ReferenceEquals(error, original)) { observed = true; }
        catch { throw new InvalidOperationException("Original decoder cancellation changed."); }
        Equal(true, observed);
        using var cancellation = new CancellationTokenSource();
        try
        {
            PromptTemplateCatalogBuilder.Build([Text("review.md", "---\nyaml\n---\nBody")], _ =>
                { cancellation.Cancel(); return new("description"); }, cancellation.Token);
        }
        catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { return Task.CompletedTask; }
        throw new InvalidOperationException("Late cancellation produced a partial catalog.");
    }

    private static Task EnumerationFailure()
    {
        var original = new InvalidOperationException("Original caller enumeration failure");
        IEnumerable<PromptTemplateReadOutcome> Values() { yield return Text("review.md", "Body"); throw original; }
        try { Build(Values()); }
        catch (InvalidOperationException error) when (ReferenceEquals(error, original)) { return Task.CompletedTask; }
        throw new InvalidOperationException("Enumeration failure was converted or lost.");
    }

    private static PromptTemplateCatalogSnapshot Build(IEnumerable<PromptTemplateReadOutcome> values) =>
        PromptTemplateCatalogBuilder.Build(values, _ => throw new Exception("Unexpected YAML decoder invocation"));
    private static PromptTemplateReadText Text(string path, string content) => new(Identity(path), content);
    private static PromptTemplateResourceIdentity Identity(string path) => new(path, path[(path.LastIndexOf('/') + 1)..],
        new(path, "caller", PromptTemplateSourceScope.Temporary, PromptTemplateSourceOrigin.TopLevel));
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
