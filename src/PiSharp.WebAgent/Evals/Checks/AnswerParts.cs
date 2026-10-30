// Decision 0011 (run W5, E2): the parts of an answer the checks read (contracts: "the JSON check reads the answer's first JSON object or json
// code block"; a contains or regex check with scope first-code-block reads "the first fenced code block of the answer (an answer without one
// fails)"). Fenced code blocks are CommonMark's: a line of three or more backticks or tildes, indented at most three spaces, opens one (an
// info string may follow: ```json); a line of the same character, at least as many and nothing after them, closes it; an unclosed block runs
// to the end of the answer. The JSON object is the first json code block's content when the answer has one (a json block that is not an
// object is reported as such, it is never skipped for a later object), else the first '{' in the text that starts a whole JSON object,
// tried at most MaxJsonStarts times so an answer full of braces costs little.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.WebAgent.Evals.Checks;

/// <summary>A fenced code block of an answer.</summary>
/// <param name="Info">The info string after the opening fence (<c>json</c>, <c>csharp</c>), trimmed; empty when there is none.</param>
/// <param name="Content">The lines between the fences, with the opening fence's indentation removed.</param>
/// <param name="Closed">Whether a closing fence ended it (an unclosed block runs to the end of the answer).</param>
public sealed record CodeBlock(string Info, string Content, bool Closed)
{
    /// <summary>The info string's first word, lower case (<c>json</c> for <c>```JSON title="x"</c>).</summary>
    public string Language => Info.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } words ? words[0].ToLowerInvariant() : "";
}

/// <summary>Where a JSON check found its JSON.</summary>
public enum JsonSource
{
    /// <summary>None was found.</summary>
    None,

    /// <summary>The answer's first json code block (<c>```json</c>).</summary>
    CodeBlock,

    /// <summary>The first JSON object in the answer's text.</summary>
    Text,
}

/// <summary>What a JSON check read: the object, where it came from, and why there is none.</summary>
/// <param name="Object">The JSON object, or null.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Problem">Why there is no object although there is a json code block (it does not parse, or is not an object), else null.</param>
public sealed record AnswerJson(JsonObject? Object, JsonSource Source, string? Problem = null);

/// <summary>Fenced code blocks and the first JSON object of an answer.</summary>
public static class AnswerParts
{
    /// <summary>The most '{' positions tried when looking for a JSON object in an answer's text.</summary>
    public const int MaxJsonStarts = 100;

    /// <summary>The fenced code blocks of <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<CodeBlock> CodeBlocks(string? text)
    {
        var blocks = new List<CodeBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (!Fence(lines[index], out var indent, out var fence, out var count, out var info)) continue;
            var content = new StringBuilder();
            var closed = false;
            var line = index + 1;
            for (; line < lines.Length; line++)
            {
                if (Closing(lines[line], fence, count)) { closed = true; break; }
                if (line > index + 1) content.Append('\n');
                content.Append(Unindent(lines[line], indent));
            }
            blocks.Add(new CodeBlock(info, content.ToString(), closed));
            index = line;
        }
        return blocks;
    }

    /// <summary>The first fenced code block of <paramref name="text"/>, or null when it has none.</summary>
    public static CodeBlock? FirstCodeBlock(string? text) => CodeBlocks(text) is { Count: > 0 } blocks ? blocks[0] : null;

    /// <summary>The JSON object a JSON check reads in <paramref name="text"/>: the first json code block's, else the first object in the
    /// text.</summary>
    public static AnswerJson FindJson(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new AnswerJson(null, JsonSource.None);
        if (CodeBlocks(text).FirstOrDefault(block => block.Language == "json") is { } block)
        {
            var content = block.Content.Trim();
            if (content.Length == 0) return new AnswerJson(null, JsonSource.CodeBlock, "the json code block is empty");
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            {
                return new AnswerJson(null, JsonSource.CodeBlock, "the json code block is not valid JSON");
            }
            return node is JsonObject json ? new AnswerJson(json, JsonSource.CodeBlock)
                : new AnswerJson(null, JsonSource.CodeBlock, "the json code block is not a JSON object");
        }
        return FirstObject(text) is { } found ? new AnswerJson(found, JsonSource.Text) : new AnswerJson(null, JsonSource.None);
    }

    /// <summary>The first JSON object in <paramref name="text"/>: the first '{' (of at most <see cref="MaxJsonStarts"/>) where a whole
    /// object starts; whatever follows it does not matter. Null when there is none.</summary>
    public static JsonObject? FirstObject(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('{', StringComparison.Ordinal)) return null;
        var bytes = Encoding.UTF8.GetBytes(text);
        var options = new JsonReaderOptions { MaxDepth = 64, AllowMultipleValues = true };
        var tried = 0;
        for (var start = Array.IndexOf(bytes, (byte)'{'); start >= 0 && tried < MaxJsonStarts; start = Array.IndexOf(bytes, (byte)'{', start + 1))
        {
            tried++;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(start), options);
                if (JsonNode.Parse(ref reader) is JsonObject json)
                {
                    // Counting the properties makes a duplicate name throw here rather than later.
                    _ = json.Count;
                    return json;
                }
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            {
                // Not an object here: try the next '{'.
            }
        }
        return null;
    }

    private static bool Fence(string line, out int indent, out char fence, out int count, out string info)
    {
        indent = 0;
        fence = '\0';
        count = 0;
        info = "";
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3 || indent >= line.Length || line[indent] is not ('`' or '~')) return false;
        fence = line[indent];
        var at = indent;
        while (at < line.Length && line[at] == fence) at++;
        count = at - indent;
        if (count < 3) return false;
        info = line[at..].Trim();
        // A backtick fence's info string has no backtick (CommonMark), so ```inline``` code on one line is not a fence.
        return fence != '`' || !info.Contains('`', StringComparison.Ordinal);
    }

    private static bool Closing(string line, char fence, int count)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ') indent++;
        if (indent > 3) return false;
        var at = indent;
        while (at < line.Length && line[at] == fence) at++;
        return at - indent >= count && line[at..].Trim().Length == 0;
    }

    private static string Unindent(string line, int indent)
    {
        var at = 0;
        while (at < indent && at < line.Length && line[at] == ' ') at++;
        return line[at..];
    }
}
