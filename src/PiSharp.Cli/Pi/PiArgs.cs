// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/cli/args.ts.
using System.Collections.Immutable;

namespace PiSharp.Cli.Pi;

internal enum PiOutputMode { Text, Json, Rpc }
internal sealed record PiArgsDiagnostic(string Type, string Message);

/// <summary>Source <c>Args</c>: the parsed Pi command line. Options left null were not given. <see cref="ToolPolicy"/> is
/// PiSharp's <c>--tool-policy</c> (decision 0004), parsed before the unknown-flag fallback.</summary>
internal sealed class PiArgs
{
    public string? Provider, Model, ApiKey, SystemPrompt, Name, Session, SessionId, Fork, SessionDir, Export, UseTheme, TuiMode, Thinking, ToolPolicy;
    public List<string>? AppendSystemPrompt, Models, Tools, ExcludeTools, Extensions, Skills, PromptTemplates, Themes;
    public bool Continue, Resume, Help, Version, NoSession, NoTools, NoBuiltinTools, NoExtensions, NoMcp, Print, NoSkills,
        NoPromptTemplates, NoThemes, NoContextFiles, Offline, Verbose;
    public PiOutputMode? Mode;
    /// <summary>Source <c>listModels</c>: null when absent, "" for the bare flag, else the search pattern.</summary>
    public string? ListModels;
    /// <summary>Source <c>projectTrustOverride</c>: <c>--approve</c> true, <c>--no-approve</c> false.</summary>
    public bool? ProjectTrustOverride;
    public List<string> Messages { get; } = [];
    public List<string> FileArgs { get; } = [];
    /// <summary>Unknown long flags (potential extension flags): name to a string value, or null for a boolean flag.</summary>
    public Dictionary<string, string?> UnknownFlags { get; } = new(StringComparer.Ordinal);
    public List<PiArgsDiagnostic> Diagnostics { get; } = [];

    internal static readonly ImmutableArray<string> ValidThinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    internal static bool IsValidThinkingLevel(string level) => ValidThinkingLevels.Contains(level, StringComparer.Ordinal);

    /// <summary>Source normalizeSessionName: the trimmed name, or null when it is empty.</summary>
    internal static string? NormalizeSessionName(string value)
    {
        var name = JsTrim(value);
        return name.Length > 0 ? name : null;
    }

    /// <summary>JavaScript <c>String.prototype.trim</c>: white space and line terminators, including U+FEFF.</summary>
    internal static string JsTrim(string value) => value.Trim(JsWhiteSpace);
    private static readonly char[] JsWhiteSpace =
        [' ', '\t', '\n', '\v', '\f', '\r', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff'];

    private static List<string> SplitList(string value) =>
        [.. value.Split(',').Select(JsTrim).Where(entry => entry.Length > 0)];

    /// <summary>Source parseArgs, option by option in the source's order.</summary>
    internal static PiArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var result = new PiArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            bool HasValue() => i + 1 < args.Count;
            if (arg == "--")
            {
                foreach (var positional in args.Skip(i + 1))
                    if (positional.StartsWith('@')) result.FileArgs.Add(positional[1..]); else result.Messages.Add(positional);
                break;
            }
            else if (arg is "--help" or "-h") result.Help = true;
            else if (arg is "--version" or "-v") result.Version = true;
            else if (arg == "--mode")
            {
                var mode = i + 1 < args.Count ? args[i + 1] : null;
                if (mode is null || mode.StartsWith('-'))
                { result.Diagnostics.Add(new("error", "--mode requires text, json, or rpc")); continue; }
                i++;
                if (mode is not ("text" or "json" or "rpc"))
                { result.Diagnostics.Add(new("error", $"Invalid mode \"{mode}\". Valid values: text, json, rpc")); continue; }
                result.Mode = mode switch { "json" => PiOutputMode.Json, "rpc" => PiOutputMode.Rpc, _ => PiOutputMode.Text };
            }
            else if (arg is "--continue" or "-c") result.Continue = true;
            else if (arg is "--resume" or "-r") result.Resume = true;
            else if (arg == "--provider" && HasValue()) result.Provider = args[++i];
            else if (arg == "--model" && HasValue()) result.Model = args[++i];
            else if (arg == "--api-key" && HasValue()) result.ApiKey = args[++i];
            else if (arg == "--system-prompt" && HasValue()) result.SystemPrompt = args[++i];
            else if (arg == "--append-system-prompt" && HasValue()) (result.AppendSystemPrompt ??= []).Add(args[++i]);
            else if (arg is "--name" or "-n")
            {
                if (HasValue()) result.Name = args[++i];
                else result.Diagnostics.Add(new("error", "--name requires a value"));
            }
            else if (arg == "--no-session") result.NoSession = true;
            else if (arg == "--session" && HasValue()) result.Session = args[++i];
            else if (arg == "--session-id" && HasValue()) result.SessionId = args[++i];
            else if (arg == "--fork" && HasValue()) result.Fork = args[++i];
            else if (arg == "--session-dir" && HasValue()) result.SessionDir = args[++i];
            else if (arg == "--models" && HasValue()) result.Models = SplitList(args[++i]);
            else if (arg is "--no-tools" or "-nt") result.NoTools = true;
            else if (arg is "--no-builtin-tools" or "-nbt") result.NoBuiltinTools = true;
            else if (arg is "--tools" or "-t" && HasValue())
            {
                var tools = SplitList(args[++i]);
                var error = ToolListError(tools);
                if (error is not null) result.Diagnostics.Add(new("error", $"{arg}: {error}"));
                else result.Tools = tools;
            }
            else if (arg is "--exclude-tools" or "-xt" && HasValue()) result.ExcludeTools = SplitList(args[++i]);
            else if (arg == "--thinking" && HasValue())
            {
                var level = args[++i];
                if (IsValidThinkingLevel(level)) result.Thinking = level;
                else result.Diagnostics.Add(new("warning", $"Invalid thinking level \"{level}\". Valid values: {string.Join(", ", ValidThinkingLevels)}"));
            }
            else if (arg is "--print" or "-p")
            {
                result.Print = true;
                var next = i + 1 < args.Count ? args[i + 1] : null;
                if (next is not null && !next.StartsWith('@') && (!next.StartsWith('-') || next.StartsWith("---", StringComparison.Ordinal)))
                { result.Messages.Add(next); i++; }
            }
            else if (arg == "--export" && HasValue()) result.Export = args[++i];
            else if (arg is "--extension" or "-e" && HasValue()) (result.Extensions ??= []).Add(args[++i]);
            else if (arg is "--no-extensions" or "-ne") result.NoExtensions = true;
            else if (arg == "--no-mcp") result.NoMcp = true;
            else if (arg == "--skill" && HasValue()) (result.Skills ??= []).Add(args[++i]);
            else if (arg == "--prompt-template" && HasValue()) (result.PromptTemplates ??= []).Add(args[++i]);
            else if (arg == "--theme" && HasValue()) (result.Themes ??= []).Add(args[++i]);
            else if (arg == "--use-theme")
            {
                var themeName = i + 1 < args.Count ? args[i + 1] : null;
                if (themeName is null || themeName.StartsWith('-')) result.Diagnostics.Add(new("error", "--use-theme requires a theme name"));
                else { result.UseTheme = themeName; i++; }
            }
            else if (arg is "--no-skills" or "-ns") result.NoSkills = true;
            else if (arg is "--no-prompt-templates" or "-np") result.NoPromptTemplates = true;
            else if (arg == "--no-themes") result.NoThemes = true;
            else if (arg is "--no-context-files" or "-nc") result.NoContextFiles = true;
            else if (arg == "--list-models")
            {
                // A following search pattern is neither a flag nor a file argument.
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-') && !args[i + 1].StartsWith('@')) result.ListModels = args[++i];
                else result.ListModels = "";
            }
            else if (arg == "--tui-mode")
            {
                var mode = i + 1 < args.Count ? args[i + 1] : null;
                if (mode is "regular" or "fullscreen") { result.TuiMode = mode; i++; }
                else if (mode is null || mode.StartsWith('-')) result.Diagnostics.Add(new("error", "--tui-mode requires regular or fullscreen"));
                else { i++; result.Diagnostics.Add(new("error", $"Invalid TUI mode \"{mode}\". Valid values: regular, fullscreen")); }
            }
            else if (arg == "--verbose") result.Verbose = true;
            else if (arg is "--approve" or "-a") result.ProjectTrustOverride = true;
            else if (arg is "--no-approve" or "-na") result.ProjectTrustOverride = false;
            else if (arg == "--offline") result.Offline = true;
            // PiSharp (decision 0004): the tool gate for this run.
            else if (arg == "--tool-policy")
            {
                var policy = i + 1 < args.Count ? args[i + 1] : null;
                if (policy is null || policy.StartsWith('-')) result.Diagnostics.Add(new("error", "--tool-policy requires pi or explicit"));
                else if (policy is not ("pi" or "explicit")) { i++; result.Diagnostics.Add(new("error", $"Invalid tool policy \"{policy}\". Valid values: pi, explicit")); }
                else { result.ToolPolicy = policy; i++; }
            }
            else if (arg.StartsWith('@')) result.FileArgs.Add(arg[1..]);
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=');
                if (eq != -1) result.UnknownFlags[arg[2..eq]] = arg[(eq + 1)..];
                else
                {
                    var flagName = arg[2..];
                    var next = i + 1 < args.Count ? args[i + 1] : null;
                    if (next is not null && !next.StartsWith('-') && !next.StartsWith('@')) { result.UnknownFlags[flagName] = next; i++; }
                    else result.UnknownFlags[flagName] = null;
                }
            }
            else if (arg.StartsWith('-')) result.Diagnostics.Add(new("error", $"Unknown option: {arg}"));
            else result.Messages.Add(arg);
        }
        return result;
    }

    /// <summary>Source getToolListError (settings-manager.ts).</summary>
    internal static string? ToolListError(IReadOnlyList<string> entries)
    {
        var modifiers = entries.Where(IsToolModifier).ToList();
        if (modifiers.Count == 0) return null;
        if (modifiers.Count < entries.Count) return "tool names cannot be mixed with +name or -name entries";
        var pattern = modifiers.FirstOrDefault(entry => entry.Contains('*'));
        return pattern is null ? null : $"+name and -name entries take exact tool names, not patterns: {pattern}";
    }
    internal static bool IsToolModifier(string entry) => entry.StartsWith('+') || entry.StartsWith('-');
}
