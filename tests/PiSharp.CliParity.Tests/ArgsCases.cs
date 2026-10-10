using PiSharp.Cli.Pi;

// cli/args.ts parseArgs, ported case by case from test/args.test.ts, plus the help, version and diagnostic output of main.ts.
internal static partial class Program
{
    private static PiArgs P(params string[] args) => PiArgs.Parse(args);
    private static void NoDiagnostics(PiArgs parsed, string what) => Equal(0, parsed.Diagnostics.Count, what + " diagnostics");
    private static void Diagnostics(PiArgs parsed, string what, params (string Type, string Message)[] expected) =>
        Names(expected.Select(item => item.Type + ":" + item.Message), parsed.Diagnostics.Select(item => item.Type + ":" + item.Message), what);

    private static IEnumerable<(string, Func<Task>)> ArgsCases() =>
    [
        ("args.version-and-help", Sync(() =>
        {
            Check(P("--version").Version && P("-v").Version, "--version/-v");
            var all = P("--version", "--help", "some message");
            Check(all.Version && all.Help && all.Messages.Contains("some message"), "--version with --help and a message");
            Check(P("--help").Help && P("-h").Help, "--help/-h");
        })),
        ("args.print-consumes-next-prompt-unless-option", Sync(() =>
        {
            Check(P("--print").Print && P("-p").Print, "--print/-p");
            var prompt = "---\ntitle: hello\n---\nSay hi.";
            var frontmatter = P("-p", prompt);
            Check(frontmatter.Print && frontmatter.Messages.SequenceEqual([prompt]) && frontmatter.UnknownFlags.Count == 0, "prompt starting with ---");
            var options = P("-p", "--provider", "openai", "Say hi.");
            Check(options.Print && options.Provider == "openai" && options.Messages.SequenceEqual(["Say hi."]), "options after -p are not prompts");
            Check(P("-p", "@file.md").FileArgs.SequenceEqual(["file.md"]) && P("-p", "@file.md").Messages.Count == 0, "@file after -p stays a file");
        })),
        ("args.continue-resume", Sync(() =>
        {
            Check(P("--continue").Continue && P("-c").Continue, "--continue/-c");
            Check(P("--resume").Resume && P("-r").Resume, "--resume/-r");
        })),
        ("args.flags-with-values", Sync(() =>
        {
            Equal("openai", P("--provider", "openai").Provider, "--provider");
            Equal("gpt-4o", P("--model", "gpt-4o").Model, "--model");
            Equal("sk-test-key", P("--api-key", "sk-test-key").ApiKey, "--api-key");
            Equal("You are a helpful assistant", P("--system-prompt", "You are a helpful assistant").SystemPrompt, "--system-prompt");
            Names(["Additional context"], P("--append-system-prompt", "Additional context").AppendSystemPrompt!, "--append-system-prompt");
            Names(["Context A", "Context B"], P("--append-system-prompt", "Context A", "--append-system-prompt", "Context B").AppendSystemPrompt!, "two appends");
            Equal("/path/to/session.jsonl", P("--session", "/path/to/session.jsonl").Session, "--session");
            Equal("orchestrated-session", P("--session-id", "orchestrated-session").SessionId, "--session-id");
            var fork = P("--fork", "1234abcd");
            Check(fork.Fork == "1234abcd" && fork.Messages.Count == 0, "--fork");
            Equal("session.jsonl", P("--export", "session.jsonl").Export, "--export");
            Equal("high", P("--thinking", "high").Thinking, "--thinking");
            Names(["gpt-4o", "claude-sonnet", "gemini-pro"], P("--models", "gpt-4o,claude-sonnet,gemini-pro").Models!, "--models");
            Names(["gpt-4o", "claude-sonnet"], P("--models", "gpt-4o, ,claude-sonnet,").Models!, "--models empty entries");
            // A value flag at the end of the arguments has no value: it is a boolean unknown flag, as `i + 1 < args.length` falls through.
            Equal(true, P("--provider").UnknownFlags.ContainsKey("provider"), "--provider without a value");
        })),
        ("args.mode-values-and-errors", Sync(() =>
        {
            foreach (var (text, mode) in new[] { ("text", PiOutputMode.Text), ("json", PiOutputMode.Json), ("rpc", PiOutputMode.Rpc) })
            { var parsed = P("--mode", text); Equal(mode, parsed.Mode, "--mode " + text); NoDiagnostics(parsed, "--mode " + text); }
            foreach (var bad in new[] { "yaml", "" })
            {
                var parsed = P("--mode", bad, "--version");
                Check(parsed.Mode is null && parsed.Version && parsed.Messages.Count == 0 && parsed.UnknownFlags.Count == 0, "invalid mode " + bad);
                Diagnostics(parsed, "invalid mode " + bad, ("error", $"Invalid mode \"{bad}\". Valid values: text, json, rpc"));
            }
            var missing = P("--mode");
            Check(missing.Mode is null && missing.UnknownFlags.Count == 0, "missing mode");
            Diagnostics(missing, "missing mode", ("error", "--mode requires text, json, or rpc"));
            var option = P("--mode", "--version");
            Check(option.Mode is null && option.Version && option.UnknownFlags.Count == 0, "--mode before an option");
            Diagnostics(option, "--mode before an option", ("error", "--mode requires text, json, or rpc"));
            Diagnostics(P("--mode", "json", "--mode", "yaml"), "invalid after valid", ("error", "Invalid mode \"yaml\". Valid values: text, json, rpc"));
        })),
        ("args.name-and-normalization", Sync(() =>
        {
            Equal("my-session", P("--name", "my-session").Name, "--name");
            Equal("quick-session", P("-n", "quick-session").Name, "-n");
            Equal("", P("--name", "").Name, "empty name kept for main");
            Equal("named session", PiArgs.NormalizeSessionName("  named session  "), "normalized");
            Equal(null, PiArgs.NormalizeSessionName("   "), "whitespace-only");
            Diagnostics(P("--name"), "missing name", ("error", "--name requires a value"));
            var together = P("--name", "named-run", "--print", "--model", "gpt-4o", "hello");
            Check(together.Name == "named-run" && together.Print && together.Model == "gpt-4o" && together.Messages.SequenceEqual(["hello"]), "--name with others");
        })),
        ("args.no-session-and-session-id-preserved", Sync(() =>
        {
            Check(P("--no-session").NoSession, "--no-session");
            var help = P("--session-id", "ephemeral-id", "--help"); Check(help.SessionId == "ephemeral-id" && help.Help, "--session-id with --help");
            var list = P("--session-id", "ephemeral-id", "--list-models"); Check(list.SessionId == "ephemeral-id" && list.ListModels == "", "--session-id with --list-models");
            var none = P("--session-id", "ephemeral-id", "--no-session"); Check(none.SessionId == "ephemeral-id" && none.NoSession, "--session-id with --no-session");
        })),
        ("args.extensions-mcp-resources-themes", Sync(() =>
        {
            Names(["./my-extension.ts"], P("--extension", "./my-extension.ts").Extensions!, "--extension");
            Names(["./my-extension.ts"], P("-e", "./my-extension.ts").Extensions!, "-e");
            Names(["./ext1.ts", "./ext2.ts"], P("--extension", "./ext1.ts", "-e", "./ext2.ts").Extensions!, "two extensions");
            Check(P("--no-extensions").NoExtensions, "--no-extensions");
            var both = P("--no-extensions", "-e", "foo.ts", "-e", "bar.ts");
            Check(both.NoExtensions && both.Extensions!.SequenceEqual(["foo.ts", "bar.ts"]), "--no-extensions with -e");
            var mcp = P("--no-mcp"); Check(mcp.NoMcp && mcp.UnknownFlags.Count == 0, "--no-mcp");
            Names(["./skill-dir"], P("--skill", "./skill-dir").Skills!, "--skill");
            Names(["./skill-a", "./skill-b"], P("--skill", "./skill-a", "--skill", "./skill-b").Skills!, "two skills");
            Names(["./prompts"], P("--prompt-template", "./prompts").PromptTemplates!, "--prompt-template");
            Names(["./one", "./two"], P("--prompt-template", "./one", "--prompt-template", "./two").PromptTemplates!, "two templates");
            Names(["./theme.json"], P("--theme", "./theme.json").Themes!, "--theme");
            Names(["./dark.json", "./light.json"], P("--theme", "./dark.json", "--theme", "./light.json").Themes!, "two themes");
            Equal("light", P("--use-theme", "light").UseTheme, "--use-theme");
            var missingTheme = P("--use-theme", "--print");
            Check(missingTheme.UseTheme is null && missingTheme.Print, "--use-theme without a name");
            Diagnostics(missingTheme, "--use-theme without a name", ("error", "--use-theme requires a theme name"));
            Check(P("--no-skills").NoSkills && P("-ns").NoSkills, "--no-skills");
            Check(P("--no-prompt-templates").NoPromptTemplates && P("-np").NoPromptTemplates, "--no-prompt-templates");
            Check(P("--no-themes").NoThemes, "--no-themes");
            Check(P("--no-context-files").NoContextFiles && P("-nc").NoContextFiles, "--no-context-files");
        })),
        ("args.approval-verbose-offline-tui-mode", Sync(() =>
        {
            Equal(true, P("--approve").ProjectTrustOverride, "--approve"); Equal(true, P("-a").ProjectTrustOverride, "-a");
            Equal(false, P("--no-approve").ProjectTrustOverride, "--no-approve"); Equal(false, P("-na").ProjectTrustOverride, "-na");
            Check(P("--verbose").Verbose, "--verbose"); Check(P("--offline").Offline, "--offline");
            Equal("regular", P("--tui-mode", "regular").TuiMode, "regular"); Equal("fullscreen", P("--tui-mode", "fullscreen").TuiMode, "fullscreen");
            Diagnostics(P("--tui-mode", "other"), "invalid tui mode", ("error", "Invalid TUI mode \"other\". Valid values: regular, fullscreen"));
            Diagnostics(P("--tui-mode"), "missing tui mode", ("error", "--tui-mode requires regular or fullscreen"));
            var old = P("--ui-mode", "fullscreen");
            Check(old.TuiMode is null && old.UnknownFlags["ui-mode"] == "fullscreen", "--ui-mode is unknown");
        })),
        ("args.tool-flags-and-modifier-errors", Sync(() =>
        {
            Check(P("--no-tools").NoTools && P("-nt").NoTools, "--no-tools");
            Check(P("--no-builtin-tools").NoBuiltinTools && P("-nbt").NoBuiltinTools, "--no-builtin-tools");
            Names(["read", "bash"], P("--tools", "read,bash").Tools!, "--tools"); Names(["read", "bash"], P("-t", "read,bash").Tools!, "-t");
            var modifiers = P("-t", "+codemode,-write");
            Names(["+codemode", "-write"], modifiers.Tools!, "modifiers"); NoDiagnostics(modifiers, "modifiers");
            var mixed = P("--tools", "read,+codemode");
            Check(mixed.Tools is null, "mixed tools unset");
            Diagnostics(mixed, "mixed", ("error", "--tools: tool names cannot be mixed with +name or -name entries"));
            var pattern = P("-t", "+mcp__radius__*");
            Check(pattern.Tools is null, "pattern tools unset");
            Diagnostics(pattern, "pattern", ("error", "-t: +name and -name entries take exact tool names, not patterns: +mcp__radius__*"));
            Names(["read", "bash"], P("--exclude-tools", "read,bash").ExcludeTools!, "--exclude-tools"); Names(["read", "bash"], P("-xt", "read,bash").ExcludeTools!, "-xt");
            var noTools = P("--no-tools", "--tools", "read,bash"); Check(noTools.NoTools && noTools.Tools!.SequenceEqual(["read", "bash"]), "--no-tools with --tools");
            var noBuiltin = P("--no-builtin-tools", "--tools", "read,bash"); Check(noBuiltin.NoBuiltinTools && noBuiltin.Tools!.SequenceEqual(["read", "bash"]), "--no-builtin-tools with --tools");
        })),
        ("args.messages-files-unknown-flags-and-separator", Sync(() =>
        {
            Names(["hello", "world"], P("hello", "world").Messages, "messages");
            Names(["README.md", "src/main.ts"], P("@README.md", "@src/main.ts").FileArgs, "@files");
            var mixed = P("@file.txt", "explain this", "@image.png");
            Check(mixed.FileArgs.SequenceEqual(["file.txt", "image.png"]) && mixed.Messages.SequenceEqual(["explain this"]), "mixed");
            var unknown = P("--unknown-flag", "message");
            Check(unknown.Messages.Count == 0 && unknown.UnknownFlags["unknown-flag"] == "message", "unknown flag with value");
            Check(P("--unknown-flag").UnknownFlags.TryGetValue("unknown-flag", out var flag) && flag is null, "boolean unknown flag");
            Equal("value", P("--unknown-flag=value").UnknownFlags["unknown-flag"], "unknown flag with =");
            var separated = P("-p", "--", "- Summarize these points", "@notes.md", "--model");
            Check(separated.Print && separated.Messages.SequenceEqual(["- Summarize these points", "--model"]) && separated.FileArgs.SequenceEqual(["notes.md"]), "-- ends options");
            Diagnostics(P("-x"), "unknown short option", ("error", "Unknown option: -x"));
            var complex = P("--provider", "anthropic", "--model", "claude-sonnet", "--print", "--thinking", "high", "@prompt.md", "Do the task");
            Check(complex.Provider == "anthropic" && complex.Model == "claude-sonnet" && complex.Print && complex.Thinking == "high" &&
                complex.FileArgs.SequenceEqual(["prompt.md"]) && complex.Messages.SequenceEqual(["Do the task"]), "complex combination");
        })),
        ("args.thinking-warning-and-tool-policy", Sync(() =>
        {
            var bad = P("--thinking", "huge");
            Check(bad.Thinking is null, "invalid thinking unset");
            Diagnostics(bad, "invalid thinking", ("warning", "Invalid thinking level \"huge\". Valid values: off, minimal, low, medium, high, xhigh, max"));
            Equal("explicit", P("--tool-policy", "explicit").ToolPolicy, "--tool-policy explicit");
            Diagnostics(P("--tool-policy", "open"), "invalid policy", ("error", "Invalid tool policy \"open\". Valid values: pi, explicit"));
            Diagnostics(P("--tool-policy"), "missing policy", ("error", "--tool-policy requires pi or explicit"));
        })),
        ("main.version-help-and-diagnostic-output", async () =>
        {
            using var sandbox = new Sandbox("main-output");
            var (code, stdout, stderr) = await sandbox.Run("--version");
            Check(code == 0 && stdout == PiConfig.Version + "\n" && stderr == "", "--version prints the version");
            (code, stdout, stderr) = await sandbox.Run("--help");
            Check(code == 0 && stdout.StartsWith("pisharp - AI coding assistant with read, bash, edit, write tools\n\nUsage:\n  pisharp [options] [--] [@files...] [messages...]\n", StringComparison.Ordinal) &&
                stdout.EndsWith("  ls         - List directory contents (read-only, off by default)\n\n", StringComparison.Ordinal) && stderr == "", "--help text");
            Check(stdout.Contains("  --tool-policy <policy>         Tool gate: pi (default; any path and command) or explicit (exact grants only)\n", StringComparison.Ordinal), "--tool-policy help line");
            Check(stdout.Contains("  PI_CODING_AGENT_DIR              - Config directory (default: ~/.pi/agent)\n", StringComparison.Ordinal), "env var padding");
            (code, stdout, stderr) = await sandbox.Run("--mode", "yaml", "--thinking", "huge");
            Equal(1, code, "invalid mode exits 1");
            Equal("", stdout, "no stdout");
            Equal("Error: Invalid mode \"yaml\". Valid values: text, json, rpc\nWarning: Invalid thinking level \"huge\". Valid values: off, minimal, low, medium, high, xhigh, max\n", stderr, "diagnostics");
            (code, _, stderr) = await sandbox.Run("--fork", "abc", "--continue", "--no-session");
            Check(code == 1 && stderr == "Error: --fork cannot be combined with --continue, --no-session\n", "fork conflicts: " + stderr);
            (code, _, stderr) = await sandbox.Run("--session-id", "bad id", "-p", "x");
            Check(code == 1 && stderr == "Error: Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character\n", "invalid session id: " + stderr);
            (code, _, stderr) = await sandbox.Run("--session-id", "abc", "--resume");
            Check(code == 1 && stderr == "Error: --session-id cannot be combined with --resume\n", "session id conflicts: " + stderr);
            (code, _, stderr) = await sandbox.Run("--mode", "rpc", "@file.txt");
            Check(code == 1 && stderr == "Error: @file arguments are not supported in RPC mode\n", "rpc @file: " + stderr);
            (code, _, stderr) = await sandbox.Run("--name", "   ", "-p", "x");
            Check(code == 1 && stderr == "Error: --name requires a non-empty value\n", "blank --name: " + stderr);
            (code, _, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "x");
            Check(code == 1 && stderr == "Error: --provider requires --model (for example: --provider anthropic --model <pattern>)\n", "--provider requires --model: " + stderr);
        }),
    ];
}
