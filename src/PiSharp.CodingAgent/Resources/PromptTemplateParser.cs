namespace PiSharp.CodingAgent.Resources;

/// <summary>Pure text boundary for Pi v0.99.1 prompt files. Does not read files or implement YAML.</summary>
public static class PromptTemplateParser
{
    public static PromptTemplateDocument ExtractDocument(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        // Pi strips exactly one leading BOM before normalizing CRLF and lone CR.
        if (content.StartsWith('\uFEFF')) content = content[1..];
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalized.StartsWith("---", StringComparison.Ordinal)) return new(null, normalized);
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return new(null, normalized);
        // Deliberately preserve the source prefix checks, including noncanonical delimiters.
        var yaml = end < 4 ? "" : normalized[4..end];
        return new(yaml.Length == 0 ? null : yaml, TrimEcmaWhitespace(normalized[(end + 4)..]));
    }

    /// <param name="fileName">Caller-supplied leaf filename, as returned by the discovery owner.</param>
    /// <param name="decodeFrontmatter">YAML decoder boundary. It must select only string-valued description
    /// and argument-hint fields; empty/null/scalar roots follow the pinned decoder's semantics.</param>
    public static PromptTemplate Parse(string fileName, string content,
        Func<string, PromptTemplateMetadata> decodeFrontmatter)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(decodeFrontmatter);
        var document = ExtractDocument(content);
        var metadata = document.FrontmatterYaml is null ? new PromptTemplateMetadata() : decodeFrontmatter(document.FrontmatterYaml);
        var name = fileName.EndsWith(".md", StringComparison.Ordinal) ? fileName[..^3] : fileName;
        var description = metadata.Description ?? "";
        if (description.Length == 0)
        {
            foreach (var line in document.Body.Split('\n'))
            {
                if (TrimEcmaWhitespace(line).Length == 0) continue;
                description = line[..Math.Min(60, line.Length)] + (line.Length > 60 ? "..." : "");
                break;
            }
        }
        return new(name, description, document.Body,
            string.IsNullOrEmpty(metadata.ArgumentHint) ? null : metadata.ArgumentHint);
    }

    internal static bool IsEcmaWhitespace(char value) => value is >= '\u0009' and <= '\u000D' or
        '\u0020' or '\u00A0' or '\u1680' or >= '\u2000' and <= '\u200A' or
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';

    private static string TrimEcmaWhitespace(string value)
    {
        var start = 0; var end = value.Length;
        while (start < end && IsEcmaWhitespace(value[start])) start++;
        while (end > start && IsEcmaWhitespace(value[end - 1])) end--;
        return value[start..end];
    }
}
