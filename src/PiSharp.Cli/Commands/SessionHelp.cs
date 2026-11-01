namespace PiSharp.Cli.Commands;

/// <summary><c>pisharp session --help</c> and <c>pisharp session &lt;verb&gt; --help</c>: the explicit-path verbs and their usage.
/// PiSharp-only (decision 0004); a bad command line still answers with one <c>session_command_result</c> JSON line.</summary>
internal static class SessionHelp
{
    private static readonly string App = Pi.PiConfig.DisplayName;

    /// <summary>Each verb's usage, as its command reports it.</summary>
    private static readonly (string[] Verbs, string Usage)[] Usages =
    [
        (["create", "prompt", "resume", "inspect", "tree", "history"], SessionCommands.Usage),
        (["rpc"], RpcSessionCommand.Usage),
        (["chat"], InteractiveSessionCommand.Usage),
        (["terminal"], TerminalSessionCommand.Usage),
        (["list"], SessionCatalogCommand.Usage),
        (["copy-inspect", "copy", "migrate"], SessionCopyCommand.Usage),
        (["context-edit"], SessionContextEditCommand.Usage),
        (["compact", "branch-summary"], SessionSummaryCommand.Usage),
    ];

    internal static string Text => string.Join('\n',
        "Usage:",
        $"  {App} session <verb> [options]",
        $"  {App} session <verb> --help",
        "",
        "PiSharp's explicit-path session commands, for tests and scripted runs. Paths are absolute, tools use the explicit",
        "tool policy and reach only what the --allow-* options grant, and most verbs run against an offline script of",
        "provider responses (--offline-script). A bad command line prints one JSON line and exits with code 2.",
        "",
        "Verbs:",
        "  create                       Create a session file in a workspace",
        "  prompt, resume               Send one --message and save the answer",
        "  inspect, tree, history       Print a session's entries, tree or message history",
        "  rpc                          Speak the RPC protocol over stdin and stdout (--offline-script or --live)",
        "  chat                         Read prompts and slash commands from stdin",
        "  terminal                     Open the terminal UI (--terminal-preview or --live)",
        "  list                         List the sessions in --session-store folders",
        "  copy-inspect, copy, migrate  Check, copy or migrate a session file",
        "  context-edit                 Replace the content of one entry",
        "  compact, branch-summary      Compact a session or summarize a branch",
        "");

    /// <summary>The help for <paramref name="args"/> (<c>session</c>, <c>session --help|-h</c> or <c>session &lt;verb&gt; --help|-h</c>),
    /// or null when they are a command to run.</summary>
    internal static string? For(string[] args)
    {
        if (args is ["session"] or ["session", "--help" or "-h"]) return Text;
        if (args is not ["session", var verb, "--help" or "-h"]) return null;
        foreach (var (verbs, usage) in Usages)
            if (verbs.Contains(verb))
                // The usage clauses are ';'-separated: one line per command form, its notes indented under it.
                return "Usage:\n" + string.Concat(usage.Split("; ").Select(clause =>
                    (clause.StartsWith("session ", StringComparison.Ordinal) ? $"  {App} " : "    ") + clause + "\n"));
        return null;
    }
}
