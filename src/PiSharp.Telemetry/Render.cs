// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/telemetry/src/render.ts.
using System.Text;

namespace PiSharp.Telemetry;

/// <summary>render.ts.</summary>
public static class TraceRenderer
{
    /// <summary><c>renderTrace</c>: stored spans as a chronological forest, treating absent parents as roots. Lines are joined with
    /// <c>\n</c>; attribute values are written as JSON.stringify writes them and numbers as JavaScript writes them.</summary>
    public static string RenderTrace(IEnumerable<StoredSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        // new Map(spans.map(...)): a repeated ID keeps its first place and its last span.
        var byId = new Dictionary<string, StoredSpan>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var span in spans)
        {
            ArgumentNullException.ThrowIfNull(span);
            if (!byId.ContainsKey(span.Id)) order.Add(span.Id);
            byId[span.Id] = span;
        }

        var roots = new List<StoredSpan>();
        var children = new Dictionary<string, List<StoredSpan>>(StringComparer.Ordinal);
        foreach (var span in order.Select(id => byId[id]).OrderBy(span => span.StartedMs))
        {
            if (span.ParentId is not { } parentId || !byId.ContainsKey(parentId)) roots.Add(span);
            else if (children.TryGetValue(parentId, out var siblings)) siblings.Add(span);
            else children[parentId] = [span];
        }

        var lines = new List<string>();
        var pending = new Stack<(StoredSpan Span, int Depth)>();
        for (var index = roots.Count - 1; index >= 0; index--) pending.Push((roots[index], 0));
        while (pending.Count > 0)
        {
            var (span, depth) = pending.Pop();
            var indent = new string(' ', 2 * depth);
            var error = "";
            if (span.Status.IsError)
            {
                error = " error";
                if (span.Status.Error is { } detail) error += $" {detail.Name}: {detail.Message}";
            }
            lines.Add($"{indent}{span.Name}{FormatAttributes(span.Attributes)} ({JsText.Number(span.EndedMs - span.StartedMs)}ms){error}");
            foreach (var item in span.Events)
                lines.Add($"{indent}  · {item.Name}{FormatAttributes(item.Attributes)} (+{JsText.Number(item.TimeMs)}ms)");
            foreach (var link in span.Links)
                lines.Add($"{indent}  → link trace {link.TraceId} span {link.SpanId}{FormatAttributes(link.Attributes)}");
            if (children.TryGetValue(span.Id, out var descendants))
                for (var index = descendants.Count - 1; index >= 0; index--) pending.Push((descendants[index], depth + 1));
        }
        return string.Join("\n", lines);
    }

    private static string FormatAttributes(SpanAttributes? attributes)
    {
        if (attributes is null) return "";
        var sb = new StringBuilder();
        foreach (var (name, value) in attributes)
        {
            sb.Append(' ').Append(name).Append('=');
            // JSON.stringify(undefined) is undefined, which the template writes as text.
            if (value is null) sb.Append("undefined");
            else JsText.Write(sb, value);
        }
        return sb.ToString();
    }
}
