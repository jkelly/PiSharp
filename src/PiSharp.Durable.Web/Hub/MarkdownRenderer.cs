// PiSharp-only (decision 0008). Agent text is Markdown from a model: Markdig renders it with raw HTML disabled, and HtmlSanitizer strips
// anything a browser could run (scripts, event handlers, javascript: URLs) before it reaches the page. Links open in a new tab without
// the opener.
using Ganss.Xss;
using Markdig;

namespace PiSharp.Durable.Web.Hub;

/// <summary>Markdown to safe HTML.</summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    private readonly HtmlSanitizer sanitizer;

    public MarkdownRenderer()
    {
        sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" }) sanitizer.AllowedSchemes.Add(scheme);
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement anchor)
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };
    }

    /// <summary>The HTML of <paramref name="markdown"/>.</summary>
    public string Render(string markdown) => sanitizer.Sanitize(Markdown.ToHtml(markdown ?? "", pipeline));
}
