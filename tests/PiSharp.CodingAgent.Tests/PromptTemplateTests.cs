using PiSharp.CodingAgent.Resources;

internal static class PromptTemplateTests
{
    public const string Prefix = "prompt template pure ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "quoted adjacent and unmatched arguments", Quotes),
        (Prefix + "ECMAScript whitespace differs from CLR whitespace", Whitespace),
        (Prefix + "empty quotes and literal backslashes match pinned parser", EmptyAndBackslash),
        (Prefix + "positional wildcards and unsupported patterns", Simple),
        (Prefix + "defaults and replacement values are not recursive", Defaults),
        (Prefix + "slices zero start zero length and missing arguments", Slices),
        (Prefix + "large digit strings and ASCII placeholder grammar", LargeDigits),
        (Prefix + "slash matching is exact first match and newline aware", Commands),
        (Prefix + "document BOM newline normalization and preserved plain body", Documents),
        (Prefix + "frontmatter delimiter prefix and empty extraction", Delimiters),
        (Prefix + "metadata fallback hint and UTF16 truncation", Metadata),
        (Prefix + "YAML is injected once and original decoder error propagates", Decoder)
    ];

    private static Task Quotes()
    {
        Args(["first arg", "second", "third arg"], "\"first arg\" second 'third arg'");
        Args(["prequotedpost", "tail"], "pre'quoted'post tail");
        Args(["unfinished text"], "'unfinished text");
        Args(["a'b", "c\"d"], "\"a'b\" 'c\"d'");
        return Task.CompletedTask;
    }

    private static Task Whitespace()
    {
        Args(["a", "b", "c", "d"], "a\n\n\tb\u00A0c\uFEFFd");
        Args(["a\u0085b", "c\u001Cd"], "a\u0085b c\u001Cd");
        Args(["line1\nline2", "last"], "\"line1\nline2\"\u2028last");
        return Task.CompletedTask;
    }

    private static Task EmptyAndBackslash()
    {
        Args([], "'' \"\"   ");
        Args([" "], "\"\" \" \"");
        Args(["quoted \\text\\"], "\"quoted \\\"text\\\"\"");
        Args(["a\\", "b"], "a\\ b");
        Equal("Price: \\", PromptTemplateExpander.SubstituteArgs("Price: \\$100", []));
        return Task.CompletedTask;
    }

    private static Task Simple()
    {
        Equal("a b a b (a b) /", PromptTemplateExpander.SubstituteArgs("$1 $2 $@ ($ARGUMENTS) $0/$3", ["a", "b"]));
        Equal("a.5 aS $arguments $ARGS $$ $A", PromptTemplateExpander.SubstituteArgs("$1.5 $ARGUMENTSS $arguments $ARGS $$ $A", ["a"]));
        Equal("$a", PromptTemplateExpander.SubstituteArgs("$$1", ["a"]));
        Equal("a  c", PromptTemplateExpander.SubstituteArgs("$@", ["a", "", "c"]));
        return Task.CompletedTask;
    }

    private static Task Defaults()
    {
        Equal("7 brief fallback", PromptTemplateExpander.SubstituteArgs("${1:-7} ${2:-brief} ${0:-fallback}", []));
        Equal("default default", PromptTemplateExpander.SubstituteArgs("${@:-default} ${ARGUMENTS:-default}", [""]));
        Equal("  ", PromptTemplateExpander.SubstituteArgs("${@:-default}", [" ", ""]));
        Equal("$ARGUMENTS $1 ${@:2}", PromptTemplateExpander.SubstituteArgs("$1 ${9:-$1} $2", ["$ARGUMENTS", "${@:2}"]));
        Equal("$@\nnext", PromptTemplateExpander.SubstituteArgs("${1:-$@\nnext}", []));
        return Task.CompletedTask;
    }

    private static Task Slices()
    {
        Equal("b c|a b c d||d||a b c d", PromptTemplateExpander.SubstituteArgs(
            "${@:2:2}|${@:0}|${@:2:0}|${@:4:99}|${@:99}|${@:1}", ["a", "b", "c", "d"]));
        Equal("", PromptTemplateExpander.SubstituteArgs("${@:0:100}", []));
        Equal("${@:2} test", PromptTemplateExpander.SubstituteArgs("${@:1}", ["${@:2}", "test"]));
        return Task.CompletedTask;
    }

    private static Task LargeDigits()
    {
        var huge = new string('9', 400);
        Equal("|fallback||a b", PromptTemplateExpander.SubstituteArgs(
            "$" + huge + "|${" + huge + ":-fallback}|${@:" + huge + "}|${@:1:" + huge + "}", ["a", "b"]));
        Equal("a|a b", PromptTemplateExpander.SubstituteArgs("$0001|${@:0000}", ["a", "b"]));
        Equal("$\u0661 ${\u0661:-x} ${@:1:-1}", PromptTemplateExpander.SubstituteArgs("$\u0661 ${\u0661:-x} ${@:1:-1}", ["a"]));
        return Task.CompletedTask;
    }

    private static Task Commands()
    {
        PromptTemplate[] templates = [new("review", "first", "$1: ${@:2}"), new("review", "second", "wrong")];
        Equal("API compatibility: rest", PromptTemplateExpander.ExpandPromptTemplate("/review\n\"API compatibility\" rest", templates));
        Equal(": ", PromptTemplateExpander.ExpandPromptTemplate("/review\n\n", templates));
        foreach (var text in new[] { " /review x", "/Review x", "/review-more x", "/ x", "/", "plain" })
            Equal(text, PromptTemplateExpander.ExpandPromptTemplate(text, templates));
        Equal("label-2: Here is some description #2.", PromptTemplateExpander.ExpandPromptTemplate("/review label-2\n\nHere is some description #2.", templates));
        return Task.CompletedTask;
    }

    private static Task Documents()
    {
        Equal(new PromptTemplateDocument(null, "\n  plain\nbody  \n"), PromptTemplateParser.ExtractDocument("\uFEFF\r\n  plain\rbody  \r\n"));
        Equal(new PromptTemplateDocument(null, "\uFEFFbody"), PromptTemplateParser.ExtractDocument("\uFEFF\uFEFFbody"));
        Equal(new PromptTemplateDocument("description: value", "Body\nnext"), PromptTemplateParser.ExtractDocument("\uFEFF---\r\ndescription: value\r\n---\r\n\nBody\rnext\n"));
        Equal(new PromptTemplateDocument(null, "---\nname: test\nBody"), PromptTemplateParser.ExtractDocument("---\nname: test\nBody"));
        return Task.CompletedTask;
    }

    private static Task Delimiters()
    {
        Equal(new PromptTemplateDocument(null, "Body"), PromptTemplateParser.ExtractDocument("---\n---\nBody"));
        Equal(new PromptTemplateDocument("escription: value", "suffix\nBody"), PromptTemplateParser.ExtractDocument("---description: value\n---suffix\nBody"));
        Equal(new PromptTemplateDocument("# comment", "Body"), PromptTemplateParser.ExtractDocument("---\n# comment\n---\uFEFFBody\uFEFF"));
        return Task.CompletedTask;
    }

    private static Task Metadata()
    {
        var template = PromptTemplateParser.Parse("review.md", "---\nsynthetic\n---\nBody", _ => new("A description", "[focus]"));
        Equal(new PromptTemplate("review", "A description", "Body", "[focus]"), template);
        template = PromptTemplateParser.Parse("review.MD", "---\nsynthetic\n---\n  First line\nSecond", _ => new("", ""));
        Equal(new PromptTemplate("review.MD", "First line", "First line\nSecond"), template);
        // Frontmatter trims the body; plain documents keep leading spaces and blank lines.
        template = PromptTemplateParser.Parse("plain.md", "\n  First line\nSecond\n", _ => throw new Exception("Unexpected decoder"));
        Equal("  First line", template.Description); Equal("\n  First line\nSecond\n", template.Content);
        var longLine = new string('a', 59) + "\uD83D\uDE00";
        template = PromptTemplateParser.Parse("utf16.md", longLine, _ => new());
        Equal(new string('a', 59) + "\uD83D...", template.Description);
        return Task.CompletedTask;
    }

    private static Task Decoder()
    {
        var calls = 0;
        PromptTemplateMetadata Decode(string yaml) { calls++; Equal("description: |\n  first\n  second", yaml); return new("first\nsecond\n"); }
        Equal("first\nsecond\n", PromptTemplateParser.Parse("block.md", "---\ndescription: |\n  first\n  second\n---\nBody", Decode).Description);
        Equal(1, calls);
        PromptTemplateParser.Parse("empty.md", "---\n---\nBody", _ => throw new Exception("Empty YAML must bypass decoder"));
        var original = new FormatException("Synthetic malformed YAML");
        try { PromptTemplateParser.Parse("invalid.md", "---\nfoo: [bar\n---\nBody", _ => throw original); }
        catch (FormatException error) when (ReferenceEquals(error, original)) { return Task.CompletedTask; }
        throw new InvalidOperationException("Original decoder failure was not preserved.");
    }

    private static void Args(string[] expected, string text)
    {
        if (!expected.SequenceEqual(PromptTemplateExpander.ParseCommandArgs(text), StringComparer.Ordinal))
            throw new InvalidOperationException("Argument sequence differs from the authored source expectation.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
}
