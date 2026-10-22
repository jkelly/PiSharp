// Decision 0011 (run W2, WU7): how a tool call matches allow rules (Settings › Tools' allow-list, and the grants "Always allow" makes in a
// chat). A rule names a tool, and for bash or powershell optionally a command pattern where * matches anything (a trailing " *" also
// matches no arguments: "dotnet test *" allows "dotnet test"). A chained command (&&, ||, ;, |, & or a new line, outside quotes) is allowed
// only when every part is. A part with command substitution ($(...), backticks, <(...), >(...)) never matches a pattern, since it can run
// anything; only a rule without a command allows it. Patterns compare with runs of white space collapsed.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Contracts;

namespace PiSharp.WebAgent.Chat;

/// <summary>Allow-rule matching for tool calls.</summary>
public static partial class ToolAllowList
{
    /// <summary>The tools whose calls carry a <c>command</c> that patterns match.</summary>
    public static IReadOnlyList<string> CommandTools { get; } = ["bash", "powershell"];

    /// <summary>The command of a bash or powershell call, or null for another tool or a call without one.</summary>
    public static string? CommandOf(string tool, JsonObject? arguments) =>
        CommandTools.Contains(tool, StringComparer.Ordinal) && arguments?["command"] is JsonValue value && value.TryGetValue<string>(out var command)
            ? command : null;

    /// <summary>Whether <paramref name="rules"/> allow a call of <paramref name="tool"/> with <paramref name="arguments"/>.</summary>
    public static bool Allows(IEnumerable<ToolAllowRule> rules, string tool, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(tool);
        var forTool = rules.Where(rule => rule is not null && string.Equals(rule.Tool, tool, StringComparison.Ordinal)).ToList();
        if (forTool.Count == 0) return false;
        if (forTool.Any(rule => rule.Command is null)) return true;
        var command = CommandOf(tool, arguments);
        if (command is null) return false;
        var parts = SplitCommand(command);
        return parts.Count > 0 && parts.All(part => forTool.Any(rule => MatchesPattern(rule.Command!, part)));
    }

    /// <summary>The rules "Allow always" adds for a call: one per part of a bash or powershell command (exactly as run), else the whole
    /// tool.</summary>
    public static IReadOnlyList<ToolAllowRule> RulesFor(string tool, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var command = CommandOf(tool, arguments);
        if (command is null) return [new ToolAllowRule(tool)];
        var parts = SplitCommand(command);
        return parts.Count == 0 ? [new ToolAllowRule(tool)] : [.. parts.Distinct(StringComparer.Ordinal).Select(part => new ToolAllowRule(tool, part))];
    }

    /// <summary>Whether one command <paramref name="part"/> (no chaining) matches <paramref name="pattern"/>.</summary>
    public static bool MatchesPattern(string pattern, string part)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(part);
        var text = Collapse(part);
        if (text.Length == 0 || Substitution().IsMatch(text)) return false;
        var normalized = Collapse(pattern);
        if (normalized.Length == 0) return false;
        var anyArguments = normalized.EndsWith(" *", StringComparison.Ordinal);
        var stem = anyArguments ? normalized[..^2] : normalized;
        var regex = new StringBuilder("^");
        regex.Append(string.Join(".*", stem.Split('*').Select(Regex.Escape)));
        regex.Append(anyArguments ? "(?: .*)?$" : "$");
        return Regex.IsMatch(text, regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }

    /// <summary>The parts of a command chained with <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, <c>&amp;</c> or new lines, outside
    /// single and double quotes, trimmed; empty parts are left out.</summary>
    public static IReadOnlyList<string> SplitCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        for (var index = 0; index < command.Length; index++)
        {
            var c = command[index];
            if (quote is not null)
            {
                current.Append(c);
                if (c == '\\' && quote == '"' && index + 1 < command.Length) current.Append(command[++index]);
                else if (c == quote) quote = null;
                continue;
            }
            switch (c)
            {
                case '\\' when index + 1 < command.Length:
                    current.Append(c).Append(command[++index]);
                    break;
                case '\'' or '"':
                    quote = c;
                    current.Append(c);
                    break;
                case ';' or '|' or '&' or '\n' or '\r':
                    // A redirection such as 2>&1 or >&2 is not a separator.
                    if (c == '&' && index > 0 && command[index - 1] is '>' or '<')
                    {
                        current.Append(c);
                        break;
                    }
                    Flush();
                    if (index + 1 < command.Length && command[index + 1] == c && c is '|' or '&') index++;
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }
        Flush();
        return parts;

        void Flush()
        {
            var part = current.ToString().Trim();
            if (part.Length > 0) parts.Add(part);
            current.Clear();
        }
    }

    private static string Collapse(string text) => Whitespace().Replace(text.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\$\(|`|<\(|>\(")]
    private static partial Regex Substitution();
}
