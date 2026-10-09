// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/external-editor.ts.
using System.Diagnostics;
using System.Text;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>Source ExternalEditorOptions: the editor command (split on spaces, as upstream does) and the text to edit.</summary>
internal sealed record ExternalEditorOptions(string Command, string Content);

/// <summary>Source ExternalEditorResult: <c>{ status: "complete", content }</c> or <c>{ status: "failed" }</c>.</summary>
internal sealed record ExternalEditorResult(string Status, string? Content = null)
{
    public static ExternalEditorResult Complete(string content) => new("complete", content);
    public static ExternalEditorResult Failed { get; } = new("failed");
}

/// <summary>Runs the editor with the terminal handed over (stdio inherited) and returns its exit code, or null when it could not
/// be started or ended without one (Node's <c>"error"</c> event, a signal). <paramref name="useShell"/> mirrors
/// <c>spawn(..., { shell: process.platform === "win32" })</c>.</summary>
internal delegate Task<int?> ExternalEditorProcessRunner(string editor, IReadOnlyList<string> arguments, bool useShell);

internal static class ExternalEditor
{
    /// <summary>editInExternalEditor({ command, content }).</summary>
    public static Task<ExternalEditorResult> EditInExternalEditorAsync(string command, string content,
        ExternalEditorProcessRunner? processRunner = null, TextWriter? output = null) =>
        EditInExternalEditorAsync(new ExternalEditorOptions(command, content), processRunner, output);

    /// <summary>Source editInExternalEditor: writes the content to <c>prompt.md</c> in a private <c>pi-editor-*</c> temporary
    /// directory, runs the editor on it, and reads it back (BOM stripped, one trailing newline removed) when the editor exits 0.
    /// The directory is always removed. <paramref name="output"/> receives the launch notice (default: standard output).</summary>
    public static async Task<ExternalEditorResult> EditInExternalEditorAsync(ExternalEditorOptions options,
        ExternalEditorProcessRunner? processRunner = null, TextWriter? output = null)
    {
        // mkdtempSync(join(tmpdir(), "pi-editor-")): CreateTempSubdirectory creates it with mode 0700 on Unix.
        var directory = Directory.CreateTempSubdirectory("pi-editor-").FullName;
        var filePath = Path.Join(directory, "prompt.md");
        try
        {
            await File.WriteAllTextAsync(filePath, options.Content, new UTF8Encoding(false)).ConfigureAwait(false);
            var parts = options.Command.Split(' ');
            var editor = parts[0];
            var editorArgs = parts.Skip(1).ToList();
            var writer = output ?? Console.Out;
            writer.Write($"Launching external editor: {options.Command}\nPi will resume when the editor exits.\n");
            writer.Flush();

            // Upstream avoids spawnSync on Windows so libuv's console read does not race the editor for console input; the
            // default runner waits for the child asynchronously the same way.
            int? exitCode;
            try { exitCode = await (processRunner ?? RunProcess)(editor, [.. editorArgs, filePath], OperatingSystem.IsWindows()).ConfigureAwait(false); }
            catch (Exception) { exitCode = null; }

            if (exitCode != 0) return ExternalEditorResult.Failed;

            var content = StripBom(await File.ReadAllTextAsync(filePath, new UTF8Encoding(false)).ConfigureAwait(false));
            return ExternalEditorResult.Complete(content.EndsWith('\n') ? content[..^1] : content);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception)
            {
                // Cleanup is best effort.
            }
        }
    }

    private static string StripBom(string text) => text.Length > 0 && text[0] == '﻿' ? text[1..] : text;

    /// <summary>The default runner: Node's <c>spawn</c> with <c>stdio: "inherit"</c>. With <paramref name="useShell"/> the command
    /// line is joined with spaces and run through <c>%ComSpec% /d /s /c "..."</c>, as Node does for <c>shell: true</c> on Windows;
    /// otherwise the editor is started directly with the arguments.</summary>
    public static async Task<int?> RunProcess(string editor, IReadOnlyList<string> arguments, bool useShell)
    {
        var startInfo = new ProcessStartInfo { UseShellExecute = false };
        if (useShell)
        {
            var commandLine = string.Join(' ', [editor, .. arguments]);
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"{commandLine}\"";
        }
        else
        {
            startInfo.FileName = editor;
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        }
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process did not start.");
        }
        catch (Exception)
        {
            return null;
        }
        using (process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            // A child killed by a signal has no exit code in Node (code null); .NET reports 128 + signal on Unix.
            return process.ExitCode;
        }
    }
}
