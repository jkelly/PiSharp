// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/markdown-transform.ts.
// MarkdownTransformContext and MarkdownTransformer are declared in core/extensions/types.ts; they live here until the extension
// runner port declares them. The Mermaid transformer (MermaidTransformer.cs, over the grok-mermaid port) is a MarkdownTransformer
// like any other.
namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>The kinds of Markdown a transformer sees (<see cref="MarkdownTransformContext.MessageType"/>).</summary>
internal static class MarkdownMessageType
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string AssistantThinking = "assistant-thinking";
}

/// <param name="MessageType">"user" | "assistant" | "assistant-thinking" (<see cref="MarkdownMessageType"/>).</param>
/// <param name="IsStreaming">Whether the message is still streaming.</param>
/// <param name="AvailableWidth">Content width the Markdown is rendered at.</param>
internal sealed record MarkdownTransformContext(string MessageType, bool IsStreaming, int AvailableWidth);

/// <summary>Rewrites Markdown before rendering. A null result keeps the current Markdown (upstream ignores non-string results).</summary>
internal delegate string? MarkdownTransformer(string markdown, MarkdownTransformContext context);

internal static class MarkdownTransform
{
    public static Func<string, int, string> CreateMarkdownTransform(string messageType, bool isStreaming, IReadOnlyList<MarkdownTransformer> transformers) =>
        (markdown, availableWidth) => ApplyMarkdownTransformers(markdown, new MarkdownTransformContext(messageType, isStreaming, availableWidth), transformers);

    private static string ApplyMarkdownTransformers(string markdown, MarkdownTransformContext context, IReadOnlyList<MarkdownTransformer> transformers)
    {
        var transformedMarkdown = markdown;
        foreach (var transformer in transformers)
        {
            try
            {
                var transformed = transformer(transformedMarkdown, context);
                if (transformed is not null) transformedMarkdown = transformed;
            }
            catch
            {
                // Keep the current Markdown and continue with the next transformer.
            }
        }
        return transformedMarkdown;
    }
}
