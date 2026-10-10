// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/core/tools/render-utils.ts.
// Also the rendering parts of coding-agent/src/core/tools/truncate.ts (DEFAULT_MAX_LINES, DEFAULT_MAX_BYTES, formatSize),
// coding-agent/src/core/tools/path-utils.ts (resolveToCwd) and coding-agent/src/utils/paths.ts (resolvePath,
// getCwdRelativePath, formatPathRelativeToCwdOrAbsolute), plus the JavaScript value semantics (JSON.stringify, template
// strings, truthiness, Number#toString) the renderers rely on. stripAnsi and sanitizeBinaryOutput reuse PiSharp.Tools.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;
using PiSharp.Tools.Processes;
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

internal static partial class RenderUtils
{
    /// <summary>os.homedir() (tests may replace it).</summary>
    public static Func<string> HomeDirectory { get; set; } = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ShortenPath(JsonNode? path) => path is not null && path.GetValueKind() == JsonValueKind.String ? ShortenPath(path.GetValue<string>()) : "";

    public static string ShortenPath(string path)
    {
        var home = HomeDirectory();
        if (path.StartsWith(home, StringComparison.Ordinal)) return "~" + path[home.Length..];
        return path;
    }

    public static string LinkPath(string styledText, string rawPath, string cwd)
    {
        if (!TerminalImage.GetCapabilities().Hyperlinks) return styledText;
        var absolutePath = ToolPaths.ResolvePath(rawPath, cwd);
        string url;
        try { url = new Uri(absolutePath).AbsoluteUri; }
        catch (UriFormatException) { return styledText; }
        return TerminalImage.Hyperlink(styledText, url);
    }

    /// <summary>str(): a string as is, null/undefined as "", anything else null (an invalid argument).</summary>
    public static string? Str(JsonNode? value)
    {
        if (value is null) return "";
        return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    public static string ReplaceTabs(string text) => text.Replace("\t", "   ", StringComparison.Ordinal);

    public static string NormalizeDisplayText(string text) => text.Replace("\r", "", StringComparison.Ordinal);

    public static string GetTextOutput(JsonNode? result, bool showImages)
    {
        if (result is null) return "";

        var content = ToolJson.Get(result, "content") as JsonArray ?? [];
        var textBlocks = content.Where(c => ToolJson.GetString(c, "type") == "text").ToList();
        var imageBlocks = content.Where(c => ToolJson.GetString(c, "type") == "image").ToList();

        var output = string.Join("\n", textBlocks.Select(c =>
            ShellAnsiText.SanitizeBinaryOutput(ShellAnsiText.StripAnsi(ToolJson.TruthyString(ToolJson.Get(c, "text")))).Replace("\r", "", StringComparison.Ordinal)));

        var caps = TerminalImage.GetCapabilities();
        if (imageBlocks.Count > 0 && (caps.Images == ImageProtocol.None || !showImages))
        {
            var imageIndicators = string.Join("\n", imageBlocks.Select(img =>
            {
                var mime = ToolJson.GetString(img, "mimeType");
                var mimeType = mime ?? "image/unknown";
                var data = ToolJson.GetString(img, "data");
                var dims = !string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(mime) ? TerminalImage.GetImageDimensions(data, mime) : null;
                return TerminalImage.ImageFallback(mimeType, dims);
            }));
            output = output.Length > 0 ? output + "\n" + imageIndicators : imageIndicators;
        }

        return output;
    }

    private const int CollapsedArgsChars = 100;

    /// <summary>
    /// Generic tool call header: the title followed by the arguments. Collapsed, they are <c>key=value</c> pairs on the title line,
    /// cut to 100 characters. Expanded, each is a <c>key: value</c> line below the title, with strings shown raw and continuation
    /// lines indented.
    /// </summary>
    public static string FormatToolCallWithArgs(string title, JsonNode? args, Theme theme, bool expanded)
    {
        var header = theme.Fg("toolTitle", theme.Bold(title));
        if (args is null) return header;
        var entries = args is JsonObject obj
            ? obj.Select(pair => (pair.Key, pair.Value)).ToList()
            : [("args", args)];
        if (entries.Count == 0) return header;
        if (expanded)
        {
            var lines = entries.Select(entry =>
            {
                var text = entry.Value is not null && entry.Value.GetValueKind() == JsonValueKind.String
                    ? entry.Value.GetValue<string>() : ToolJson.Stringify(entry.Value, 2);
                return $"  {entry.Key}: {string.Join("\n    ", ReplaceTabs(text).Replace("\r", "", StringComparison.Ordinal).Split('\n'))}";
            });
            return header + "\n" + theme.Fg("muted", string.Join("\n", lines));
        }
        var pairs = string.Join(" ", entries.Select(entry => $"{entry.Key}={ToolJson.Stringify(entry.Value)}"));
        var preview = pairs.Length > CollapsedArgsChars ? pairs[..(CollapsedArgsChars - 3)] + "..." : pairs;
        return header + " " + theme.Fg("muted", preview);
    }

    public static string InvalidArgText(Theme theme) => theme.Fg("error", "[invalid arg]");

    public static string RenderToolPath(string? rawPath, Theme theme, string cwd, string? emptyFallback = null)
    {
        if (rawPath is null) return InvalidArgText(theme);
        var value = rawPath.Length > 0 ? rawPath : emptyFallback;
        if (string.IsNullOrEmpty(value)) return theme.Fg("toolOutput", "...");
        return LinkPath(theme.Fg("accent", ShortenPath(value)), value, cwd);
    }

    /// <summary>The expand hint used by the collapsed previews: <c>... (N more lines, ctrl+o to expand)</c>.</summary>
    internal static string MoreLinesHint(Theme theme, string text) =>
        theme.Fg("muted", text) + " " + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")");
}

/// <summary>truncate.ts: the limits and size formatting the renderers show.</summary>
internal static class ToolTruncate
{
    public const int DefaultMaxLines = 2000;
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>Format bytes as human-readable size.</summary>
    public static string FormatSize(double bytes)
    {
        if (bytes < 1024) return ToolJson.JsNumber(bytes) + "B";
        if (bytes < 1024 * 1024) return (bytes / 1024).ToString("F1", CultureInfo.InvariantCulture) + "KB";
        return (bytes / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";
    }
}

/// <summary>path-utils.ts resolveToCwd and utils/paths.ts resolvePath / formatPathRelativeToCwdOrAbsolute.</summary>
internal static partial class ToolPaths
{
    [GeneratedRegex("[  -   　]", RegexOptions.CultureInvariant)]
    private static partial Regex UnicodeSpaces();

    /// <summary>resolvePath with the default options (tilde expansion).</summary>
    public static string ResolvePath(string input, string baseDirectory) => PiPaths.ResolvePath(input, baseDirectory, RenderUtils.HomeDirectory());

    /// <summary>Resolve a path relative to the given cwd. Handles ~ expansion, unicode spaces, a leading @ and absolute paths.</summary>
    public static string ResolveToCwd(string filePath, string cwd)
    {
        var normalized = UnicodeSpaces().Replace(filePath, " ");
        if (normalized.StartsWith('@')) normalized = normalized[1..];
        return ResolvePath(normalized, cwd);
    }

    public static string? GetCwdRelativePath(string filePath, string cwd)
    {
        var resolvedCwd = ResolvePath(cwd, Environment.CurrentDirectory);
        var resolvedPath = ResolvePath(filePath, resolvedCwd);
        var relativePath = Relative(resolvedCwd, resolvedPath);
        var isInsideCwd = relativePath.Length == 0 ||
            (relativePath != ".." && !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relativePath));
        return isInsideCwd ? (relativePath.Length > 0 ? relativePath : ".") : null;
    }

    public static string FormatPathRelativeToCwdOrAbsolute(string filePath, string cwd)
    {
        var absolutePath = ResolvePath(filePath, cwd);
        return (GetCwdRelativePath(absolutePath, cwd) ?? absolutePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>node:path relative(): "" for the same path, the target itself across Windows drives.</summary>
    public static string Relative(string from, string to)
    {
        var relative = Path.GetRelativePath(from, to);
        return relative == "." ? "" : relative;
    }
}

/// <summary>JavaScript value semantics over System.Text.Json nodes.</summary>
internal static class ToolJson
{
    /// <summary>A property of an object node, null when absent, JSON null or not an object.</summary>
    public static JsonNode? Get(JsonNode? node, string name) => node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

    /// <summary><c>value !== undefined</c>: the property is present (JSON null included).</summary>
    public static bool Has(JsonNode? node, string name) => node is JsonObject obj && obj.ContainsKey(name);

    public static string? GetString(JsonNode? node, string name) => AsString(Get(node, name));

    public static string? AsString(JsonNode? value) => value is not null && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static double? AsNumber(JsonNode? value) => value is not null && value.GetValueKind() == JsonValueKind.Number
        ? double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) : null;

    public static double? GetNumber(JsonNode? node, string name) => AsNumber(Get(node, name));

    /// <summary><c>value || ""</c> for a value expected to be a string.</summary>
    public static string TruthyString(JsonNode? value) => Truthy(value) ? Template(value) : "";

    public static bool Truthy(JsonNode? value)
    {
        if (value is null) return false;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => AsNumber(value) is { } number && number != 0 && !double.IsNaN(number),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true
        };
    }

    /// <summary>String(value) / a template literal substitution.</summary>
    public static string Template(JsonNode? value)
    {
        if (value is null) return "null";
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => JsNumber(AsNumber(value)!.Value),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(",", value.AsArray().Select(item => item is null ? "" : Template(item))),
            JsonValueKind.Object => "[object Object]",
            _ => "null"
        };
    }

    /// <summary>JSON.stringify(value) or JSON.stringify(value, null, indent).</summary>
    public static string Stringify(JsonNode? value, int indent = 0)
    {
        var builder = new StringBuilder();
        Write(builder, value, indent > 0 ? new string(' ', indent) : null, "");
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonNode? value, string? indent, string current)
    {
        switch (value)
        {
            case null: builder.Append("null"); return;
            case JsonObject obj:
                if (obj.Count == 0) { builder.Append("{}"); return; }
                builder.Append('{');
                var inner = current + indent;
                var first = true;
                foreach (var (key, item) in obj)
                {
                    if (!first) builder.Append(',');
                    first = false;
                    if (indent is not null) builder.Append('\n').Append(inner);
                    Quote(builder, key);
                    builder.Append(indent is not null ? ": " : ":");
                    Write(builder, item, indent, inner);
                }
                if (indent is not null) builder.Append('\n').Append(current);
                builder.Append('}');
                return;
            case JsonArray array:
                if (array.Count == 0) { builder.Append("[]"); return; }
                builder.Append('[');
                var nested = current + indent;
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    if (indent is not null) builder.Append('\n').Append(nested);
                    Write(builder, array[index], indent, nested);
                }
                if (indent is not null) builder.Append('\n').Append(current);
                builder.Append(']');
                return;
        }
        switch (value.GetValueKind())
        {
            case JsonValueKind.String: Quote(builder, value.GetValue<string>()); return;
            case JsonValueKind.Number:
                var number = AsNumber(value)!.Value;
                builder.Append(double.IsFinite(number) ? JsNumber(number) : "null");
                return;
            case JsonValueKind.True: builder.Append("true"); return;
            case JsonValueKind.False: builder.Append("false"); return;
            default: builder.Append("null"); return;
        }
    }

    /// <summary>JSON.stringify's QuoteJSONString (well-formed: lone surrogates escaped).</summary>
    public static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { builder.Append(c).Append(text[index + 1]); index++; }
                    else if (char.IsSurrogate(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>Number.prototype.toString() for a double (shortest round-trip digits, JavaScript exponent rules).</summary>
    public static string JsNumber(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        if (value == 0) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var sign = "";
        if (text.StartsWith('-')) { sign = "-"; text = text[1..]; }
        var mantissa = text; var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0) { mantissa = text[..e]; exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); }
        var dot = mantissa.IndexOf('.');
        var integer = dot < 0 ? mantissa : mantissa[..dot];
        var fraction = dot < 0 ? "" : mantissa[(dot + 1)..];
        var all = integer + fraction;
        var trimmed = all.TrimStart('0');
        var leadingZeros = all.Length - trimmed.Length;
        var n = integer.Length + exponent - leadingZeros;
        var digits = trimmed.TrimEnd('0');
        var k = digits.Length;
        if (k <= n && n <= 21) return sign + digits + new string('0', n - k);
        if (0 < n && n <= 21) return sign + digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return sign + "0." + new string('0', -n) + digits;
        var exp = n - 1;
        var expText = exp >= 0 ? "+" + exp.ToString(CultureInfo.InvariantCulture) : exp.ToString(CultureInfo.InvariantCulture);
        return k == 1 ? sign + digits + "e" + expText : sign + digits[0] + "." + digits[1..] + "e" + expText;
    }
}
