// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/modes/diagnostics-stderr.ts (writeDiagnosticsToStderr).
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Pi;

/// <summary>Writes runtime diagnostics to stderr for the CLI and the non-interactive modes.</summary>
internal static class PiDiagnosticsStderr
{
    /// <summary>One diagnostic as <c>Error: ...</c> (red), <c>Warning: ...</c> (yellow) or a dim info line; plain without color.</summary>
    internal static string Format(string type, string message, bool color)
    {
        var prefix = type == "error" ? "Error: " : type == "warning" ? "Warning: " : "";
        if (!color) return prefix + message;
        return type == "error" ? PiCommand.Red + prefix + message + "\u001b[39m"
            : type == "warning" ? PiCommand.Yellow + prefix + message + "\u001b[39m"
            : PiCommand.Dim + prefix + message + "\u001b[22m";
    }

    internal static async Task WriteAsync(TextWriter stderr, IEnumerable<PiDiagnostic> diagnostics, bool color)
    {
        foreach (var diagnostic in diagnostics) await PiCommand.Line(stderr, Format(diagnostic.Type, diagnostic.Message, color)).ConfigureAwait(false);
    }

    internal static Task WriteAsync(TextWriter stderr, IEnumerable<AgentSessionRuntimeDiagnostic> diagnostics, bool color) =>
        WriteAsync(stderr, diagnostics.Select(diagnostic => new PiDiagnostic(diagnostic.Type, diagnostic.Message)), color);
}
