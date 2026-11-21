// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/mcp/src/naming.ts, packages/mcp/test/naming.test.ts.
// Authored native expectations read from the cited source and its test; not an upstream execution capture.
using PiSharp.Extensions.Mcp.Configuration;

internal static partial class Program
{
    internal static IEnumerable<(string Id, Func<Task> Run)> Sync160McpCases() =>
    [
        ("mcp160.server-summary-first-line-of-description-else-instructions", ServerSummary160),
        ("mcp160.server-summary-trims-like-javascript", ServerSummaryTrim160)
    ];

    // naming.test.ts "takes the first line of the configured description, else of the instructions".
    private static Task ServerSummary160()
    {
        Equal("Docs search.", McpCatalogPlanner.ServerSummary(" Docs search.\nMore. ", "Ignored."));
        Equal("Web search for current information.", McpCatalogPlanner.ServerSummary(null, "\n  Web search for current information.\nDetails."));
        Equal("From instructions.", McpCatalogPlanner.ServerSummary("  ", "From instructions."));
        Equal("", McpCatalogPlanner.ServerSummary(null, null));
        return Task.CompletedTask;
    }

    // naming.ts: `description?.trim() || instructions?.trim() || ""`, then the first of `split("\n")`, trimmed. The trims are
    // String.prototype.trim (line terminators, no-break space and the byte order mark are white space; a carriage return before the
    // line feed goes with the line), and only a line feed ends the first line.
    private static Task ServerSummaryTrim160()
    {
        Equal("First", McpCatalogPlanner.ServerSummary("\uFEFF\u00A0First\r\nSecond", null));
        Equal("Kept together\rSecond", McpCatalogPlanner.ServerSummary("Kept together\rSecond\nThird", null));
        Equal("From instructions.", McpCatalogPlanner.ServerSummary("\u2028\n\t", "\u3000From instructions.\u2029\nMore"));
        Equal("", McpCatalogPlanner.ServerSummary("", ""));
        Equal("", McpCatalogPlanner.ServerSummary(" \n ", null));
        Equal("Only", McpCatalogPlanner.ServerSummary("Only", "Ignored even when the description has one line."));
        return Task.CompletedTask;
    }
}
