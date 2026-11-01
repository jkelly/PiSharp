// PiSharp-only (decision 0008; shared with the Web Agent by decision 0011). Agent and user text is Markdown from a model or a person: Markdig
// renders it with raw HTML disabled, and HtmlSanitizer strips anything a browser could run (scripts, event handlers, javascript: URLs)
// before it reaches the page. Links open in a new tab without the opener. HTML from anywhere else goes through Sanitize, the same filter.
using Ganss.Xss;
using Markdig;

namespace PiSharp.Web.Common;

/// <summary>Markdown to safe HTML. One instance is shared (a singleton): rendering and sanitizing are safe to call from many threads.</summary>
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

    /// <summary>A renderer whose sanitizer allows http, https and mailto links and the <c>class</c> attribute.</summary>
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

    /// <summary>The HTML of <paramref name="markdown"/>; raw HTML in it is shown as text, never run.</summary>
    public string Render(string markdown) => sanitizer.Sanitize(Markdown.ToHtml(markdown ?? "", pipeline));

    /// <summary><paramref name="html"/> with everything a browser could run removed, by the same rules as <see cref="Render"/>.</summary>
    public string Sanitize(string html) => sanitizer.Sanitize(html ?? "");
}
