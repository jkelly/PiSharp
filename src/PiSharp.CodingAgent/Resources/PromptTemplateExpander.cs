using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent.Resources;

/// <summary>Pi v0.99.1 command arguments and single-pass prompt substitution, without execution.</summary>
public static class PromptTemplateExpander
{
    private static readonly Regex Placeholders = new(
        @"\$\{([0-9]+|ARGUMENTS|@):-([^}]*)\}|\$\{@:([0-9]+)(?::([0-9]+))?\}|\$(ARGUMENTS|@|[0-9]+)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static ImmutableArray<string> ParseCommandArgs(string argsString)
    {
        ArgumentNullException.ThrowIfNull(argsString);
        var args = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder(); char? quote = null;
        foreach (var value in argsString)
        {
            if (quote is not null)
            {
                if (value == quote) quote = null;
                else current.Append(value);
            }
            else if (value is '\'' or '"') quote = value;
            else if (PromptTemplateParser.IsEcmaWhitespace(value))
            {
                if (current.Length == 0) continue;
                args.Add(current.ToString()); current.Clear();
            }
            else current.Append(value);
        }
        if (current.Length != 0) args.Add(current.ToString());
        return args.ToImmutable();
    }

    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(args);
        var allArgs = string.Join(" ", args);
        return Placeholders.Replace(content, match =>
        {
            if (match.Groups[1].Success)
            {
                var target = match.Groups[1].Value;
                var value = target is "@" or "ARGUMENTS" ? allArgs : Positional(target);
                return value.Length == 0 ? match.Groups[2].Value : value;
            }
            if (match.Groups[3].Success)
            {
                var position = ClampedNumber(match.Groups[3].Value, args.Count);
                var start = Math.Max(0, position - 1);
                var count = match.Groups[4].Success ? ClampedNumber(match.Groups[4].Value, args.Count) : args.Count;
                return string.Join(" ", args.Skip(start).Take(count));
            }
            var simple = match.Groups[5].Value;
            return simple is "@" or "ARGUMENTS" ? allArgs : Positional(simple);
        });

        string Positional(string digits)
        {
            var position = ClampedNumber(digits, args.Count);
            return position >= 1 && position <= args.Count ? args[position - 1] : "";
        }
    }

    /// <summary>Reads the pinned first slash token for frontends without parsing extension commands.</summary>
    public static string? GetInvocationName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.StartsWith('/')) return null;
        var end = 1;
        while (end < text.Length && !PromptTemplateParser.IsEcmaWhitespace(text[end])) end++;
        return end == 1 ? null : text[1..end];
    }

    public static string ExpandPromptTemplate(string text, IReadOnlyList<PromptTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(templates);
        var name = GetInvocationName(text);
        if (name is null) return text;
        var end = name.Length + 1;
        var template = templates.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (template is null) return text;
        while (end < text.Length && PromptTemplateParser.IsEcmaWhitespace(text[end])) end++;
        return SubstituteArgs(template.Content, ParseCommandArgs(text[end..]));
    }

    // All source digit strings are nonnegative. Values beyond the array size have the same
    // observable result as JS parseInt (including values rounded to infinity), without overflow.
    private static int ClampedNumber(string digits, int arraySize)
    {
        var value = 0;
        foreach (var digit in digits)
        {
            var next = (long)value * 10 + digit - '0';
            if (next > arraySize) return arraySize + 1;
            value = (int)next;
        }
        return value;
    }
}
