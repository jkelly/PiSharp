// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/codemode/src/source.ts.
using System.Text.Json;

namespace PiSharp.Codemode;

/// <summary>Options of the first <c>// @options:</c> line.</summary>
public sealed record CodemodeSourceOptions(long? MaxOutputTokens = null, long? TimeoutMs = null);

/// <summary>The script with the options line replaced by an empty line, so line numbers are unchanged.</summary>
public sealed record ParsedCodemodeSource(string Code, CodemodeSourceOptions Options);

public sealed class CodemodeSourceException(string message) : Exception(message)
{
    public string Name => "CodemodeSourceError";
}

/// <summary>Codemode source format: JavaScript, optionally preceded by one options line.</summary>
public static class CodemodeSource
{
    public const string OptionsPrefix = "// @options:";
    private const string SupportedFieldsText = "`max_output_tokens` and `timeout_ms`";
    /// <summary>Largest delay setTimeout supports, which bounds <c>timeout_ms</c>.</summary>
    private const long MaxTimeoutMs = 2_147_483_647;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    /// <summary>Lark grammar for providers with grammar-constrained tool input (String.raw, leading newline kept).</summary>
    public const string Grammar = "\nstart: options_source | plain_source\noptions_source: OPTIONS_LINE NEWLINE SOURCE\nplain_source: SOURCE\n\n" +
        "OPTIONS_LINE: /[ \\t]*\\/\\/ @options:[^\\r\\n]*/\nNEWLINE: /\\r?\\n/\nSOURCE: /[\\s\\S]+/\n";

    /// <summary>Split an optional first-line <c>// @options: {...}</c> from the script. Throws
    /// <see cref="CodemodeSourceException"/> for empty input and invalid options.</summary>
    public static ParsedCodemodeSource Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Js.Trim(input).Length == 0)
            throw new CodemodeSourceException("Expected JavaScript source text (non-empty). Provide JS only, optionally with a first line `// @options: {\"max_output_tokens\": 1000}`.");
        var newline = input.IndexOf('\n');
        var firstLine = newline == -1 ? input : input[..newline];
        if (firstLine.EndsWith('\r')) firstLine = firstLine[..^1];
        var trimmed = TrimStart(firstLine);
        if (!trimmed.StartsWith(OptionsPrefix, StringComparison.Ordinal)) return new(input, new());
        var code = newline == -1 ? "" : input[newline..];
        if (Js.Trim(code).Length == 0) throw new CodemodeSourceException("The @options line must be followed by JavaScript source on subsequent lines");
        return new(code, ParseOptions(Js.Trim(trimmed[OptionsPrefix.Length..])));
    }

    private static string TrimStart(string value)
    {
        var trimmed = Js.Trim(value + "x");
        return trimmed[..^1];
    }

    private static CodemodeSourceOptions ParseOptions(string directive)
    {
        if (directive.Length == 0) throw new CodemodeSourceException($"@options must be a JSON object with supported fields {SupportedFieldsText}");
        JsonElement value;
        try { using var document = JsonDocument.Parse(directive); value = document.RootElement.Clone(); }
        catch (JsonException error)
        { throw new CodemodeSourceException($"@options must be valid JSON with supported fields {SupportedFieldsText}: {error.Message}"); }
        if (value.ValueKind != JsonValueKind.Object) throw new CodemodeSourceException($"@options must be a JSON object with supported fields {SupportedFieldsText}");
        JsonElement? maxOutputTokens = null, timeoutMs = null;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name == "max_output_tokens") maxOutputTokens = property.Value;
            else if (property.Name == "timeout_ms") timeoutMs = property.Value;
            else throw new CodemodeSourceException($"@options only supports {SupportedFieldsText}; got `{property.Name}`");
        }
        long? tokens = null, timeout = null;
        if (maxOutputTokens is { } tokenValue)
        {
            if (!SafeInteger(tokenValue, out var parsed)) throw new CodemodeSourceException("@options field `max_output_tokens` must be a non-negative safe integer");
            tokens = parsed;
        }
        if (timeoutMs is { } timeoutValue)
        {
            if (!SafeInteger(timeoutValue, out var parsed) || parsed == 0 || parsed > MaxTimeoutMs)
                throw new CodemodeSourceException($"@options field `timeout_ms` must be a positive integer up to {MaxTimeoutMs}");
            timeout = parsed;
        }
        return new(tokens, timeout);
    }

    private static bool SafeInteger(JsonElement value, out long result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        var number = value.GetDouble();
        if (!double.IsFinite(number) || Math.Floor(number) != number || number < 0 || number > MaxSafeInteger) return false;
        result = (long)number; return true;
    }
}
