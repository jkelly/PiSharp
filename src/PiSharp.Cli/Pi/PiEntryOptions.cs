using System.Collections.Immutable;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

namespace PiSharp.Cli.Pi;

/// <summary>Owner decision 0004: <c>pi</c> lets file tools use any path and bash run any command with the full environment;
/// <c>explicit</c> keeps PiSharp's exact grants. Session files, extension manifests, approvals and snapshots are protected in both.</summary>
internal enum PiToolPolicyMode { Pi, Explicit }

/// <summary>The tool gate of one run. <see cref="ProtectedDirectories"/> are the session directories whose <c>.jsonl</c> files no file
/// tool may touch under the <c>pi</c> policy; <see cref="Environment"/> is the full environment bash commands receive.</summary>
internal sealed record PiToolPolicy(PiToolPolicyMode Mode)
{
    internal ImmutableArray<string> ProtectedDirectories { get; init; } = [];
    /// <summary>Directories whose <c>.jsonl</c> files are protected at any depth (the agent directory's <c>sessions</c> tree).</summary>
    internal ImmutableArray<string> ProtectedTrees { get; init; } = [];
    internal IReadOnlyDictionary<string, string>? Environment { get; init; }
    /// <summary>The rg/fd tools manager the grep and find tools use (null: no search tools); status messages go to the reporter.</summary>
    internal PiToolsManager? Search { get; init; }
    internal Action<PiToolStatus>? ReportToolStatus { get; init; }
    internal static PiToolPolicy Explicit { get; } = new(PiToolPolicyMode.Explicit);
}

/// <summary>Everything a Pi-style entry point (plain <c>pisharp</c>, <c>-p</c>, <c>--mode json|rpc</c>) decided before the session host
/// starts. The RPC host reads it from <see cref="Current"/> (set around the host call) in place of its explicit-path defaults.</summary>
internal sealed record PiEntryOptions
{
    private static readonly AsyncLocal<PiEntryOptions?> Ambient = new();
    /// <summary>The options of the Pi entry running on this logical call, or null for the explicit <c>session</c> verbs.</summary>
    internal static PiEntryOptions? Current => Ambient.Value;
    internal IDisposable Enter()
    {
        var previous = Ambient.Value; Ambient.Value = this;
        return new Restore(previous);
    }
    private sealed class Restore(PiEntryOptions? previous) : IDisposable { public void Dispose() => Ambient.Value = previous; }

    internal required PiToolPolicy ToolPolicy { get; init; }
    internal required StartupSettingsSnapshot Settings { get; init; }
    internal required OriginalSystemPromptAdmission SystemPrompt { get; init; }
    internal required LiveSessionSelection Selection { get; init; }
    internal required LiveSessionRuntime LiveRuntime { get; init; }
    /// <summary>The new session's header id and ISO timestamp (source newSession), or null when the session is opened.</summary>
    internal string? HeaderId { get; init; }
    internal string? HeaderTimestamp { get; init; }
    /// <summary>The name <c>--name</c> sets on the session (source appendSessionInfo).</summary>
    internal string? SessionName { get; init; }
    internal SkillCliBinding? Skills { get; init; }
    internal PromptTemplateCliConfiguration PromptTemplates { get; init; } = new([]);
    internal string? ThinkingLevel { get; init; }
    internal bool ThinkingFromCli { get; init; }
    /// <summary>Messages and images the interactive frontend submits at startup (IMPL-I): source initialMessage/initialImages/initialMessages.</summary>
    internal string? InitialMessage { get; init; }
    internal ImmutableArray<JsonData> InitialImages { get; init; } = [];
    internal ImmutableArray<string> InitialMessages { get; init; } = [];
    /// <summary>Data for the interactive mode (IMPL-I): the theme to start with, the TUI mode and verbose startup.</summary>
    internal string? Theme { get; init; }
    internal string? TuiMode { get; init; }
    internal bool Verbose { get; init; }
    internal ImmutableArray<PiTheme> Themes { get; init; } = [];
    internal bool ShowStartupHeader { get; init; } = true;
    internal bool ShowStartupDetails { get; init; } = true;
    /// <summary>Source runMigrations results the interactive mode reports (migrated auth providers, deprecation warnings).</summary>
    internal ImmutableArray<string> MigratedAuthProviders { get; init; } = [];
    internal ImmutableArray<string> DeprecationWarnings { get; init; } = [];
    /// <summary>The project trust of the session cwd and of other directories the run resolved (IMPL-H, IMPL-E).</summary>
    internal Func<string, bool> ProjectTrusted { get; init; } = _ => false;
    internal ImmutableArray<PiDiagnostic> StartupDiagnostics { get; init; } = [];
    /// <summary>Extension inputs parsed from the command line for IMPL-E (<c>-e</c>, <c>--no-extensions</c>, unknown flags).</summary>
    internal ImmutableArray<string> ExtensionPaths { get; init; } = [];
    internal bool NoExtensions { get; init; }
    internal ImmutableDictionary<string, string?> ExtensionFlagValues { get; init; } = ImmutableDictionary<string, string?>.Empty;
    /// <summary>The extensions the run loaded (TypeScript/JavaScript in the Node host), bound to the session by the RPC host; null without.</summary>
    internal PiSharp.Cli.Extensions.Pi.PiExtensionHost? Extensions { get; init; }
    /// <summary>Source ExtensionMode of the run: <c>tui</c>, <c>rpc</c>, <c>json</c> or <c>print</c>.</summary>
    internal string ExtensionMode { get; init; } = "print";
    /// <summary>Interactive mode inputs (IMPL-I): directories, resources and the live host link.</summary>
    internal PiSharp.Cli.Interactive.Mode.InteractiveStartup? Interactive { get; init; }
}
