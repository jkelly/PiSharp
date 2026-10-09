using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Mermaid;
using PiSharp.Cli.Interactive.Mode.Mermaid.Differential;
using static Expect;

/// <summary>modes/interactive/components/mermaid.ts over the grok-mermaid 0.2.3 port. The port is compared with goldens the original
/// library produced under Node (tools/GrokMermaid/gen-goldens.mjs, 433 authored cases: every diagram kind and syntax form, Unicode
/// widths, malformed input, caps and streaming prefixes); the transformer expectations are authored from mermaid.ts.</summary>
internal static class MermaidCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("mermaid.grok-port-matches-the-original-library", Sync(Differential)),
        ("mermaid.transformer-replaces-top-level-blocks-by-mode", Sync(Transformer)),
    ];

    private static void Differential()
    {
        var results = GrokMermaidDifferential.Run(Path.Combine(AppContext.BaseDirectory, "Mermaid")).ToList();
        var failed = results.Where(result => !result.Passed).Take(5).Select(result => result.Id + ": " + result.Detail).ToList();
        Check(results.Count == 433 && failed.Count == 0, $"{results.Count} cases, failures: " + string.Join(" | ", failed));
    }

    private static void Transformer()
    {
        const string source = "graph LR\n  A[Start] --> B[End]";
        var markdown = "Before\n\n```mermaid\n" + source + "\n```\n\nAfter\n";
        var art = GrokMermaid.Render(source) ?? throw new InvalidOperationException("The sample diagram did not render.");
        // marked 18 lexes paragraph "Before", space "\n\n", the code block (raw without its trailing newline), space "\n\n", paragraph.
        var expected = "Before\n\n" + string.Join("  \n", art.Plain.Select(MermaidTransformer.CodeSpan)) + "\n" + "\n\nAfter\n";
        var mode = "streaming";
        var transform = MermaidTransformer.Create(() => mode);
        MarkdownTransformContext Context(string type = MarkdownMessageType.Assistant, bool streaming = false, int width = 120) => new(type, streaming, width);
        Equal(expected, transform(markdown, Context()), "final render");
        Equal(expected, transform(markdown, Context(streaming: true)), "streaming render in streaming mode");
        Equal(markdown, transform(markdown, Context(MarkdownMessageType.AssistantThinking)), "thinking is never transformed");
        Equal(markdown, transform(markdown, Context(width: art.Width - 1)), "a diagram wider than the available width stays code");
        mode = "final";
        Equal(markdown, transform(markdown, Context(streaming: true)), "final mode skips streaming messages");
        Equal(expected, transform(markdown, Context()), "final mode renders the final message");
        mode = "off";
        Equal(markdown, transform(markdown, Context()), "off");
        // A diagram with warnings keeps its source and gains the first warning (and the count of the others) as a code span.
        mode = "streaming";
        const string broken = "graph TD\n  A --> B\n  this is not mermaid";
        var warned = GrokMermaid.Render(broken);
        if (warned is { Warnings.Count: > 0 })
        {
            var raw = "```mermaid\n" + broken + "\n```";
            var suffix = warned.Warnings.Count > 1 ? $" (+{warned.Warnings.Count - 1} more)" : "";
            Equal(raw + "\n" + MermaidTransformer.CodeSpan("Mermaid diagram not rendered: " + warned.Warnings[0] + suffix) + "  \n",
                transform(raw, Context()), "warning");
            // While streaming, warnings are not reported: the partial diagram renders.
            Equal(string.Join("  \n", warned.Plain.Select(MermaidTransformer.CodeSpan)) + "\n", transform(raw, Context(streaming: true)), "streaming with warnings");
        }
        Equal("`` `edge` ``", MermaidTransformer.CodeSpan("`edge`"), "padded backtick fence");
        Equal("``hel`lo``", MermaidTransformer.CodeSpan("hel`lo"), "fence longer than the backtick run");
        Equal("` `", MermaidTransformer.CodeSpan(""), "blank row");
    }
}
