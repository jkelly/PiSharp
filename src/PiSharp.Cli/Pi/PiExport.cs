// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/main.ts (--export) and
// packages/coding-agent/src/core/export-html/index.ts (exportFromFile: input checks and the default output name).
using System.Collections.Immutable;
using PiSharp.CodingAgent.Export;
using PiSharp.CodingAgent;
using PiSharp.Sessions.Lifecycle;

namespace PiSharp.Cli.Pi;

/// <summary><c>--export &lt;file&gt; [output]</c>: the session's HTML, written next to the current directory as
/// <c>pi-session-&lt;name&gt;.html</c> unless an output path is given. The page is PiSharp's renderer (IMPL-G owns the template).</summary>
internal static class PiExport
{
    internal static async Task<int> RunAsync(string input, string? output, PiHost host, CancellationToken token)
    {
        string result;
        try { result = await ExportFromFileAsync(input, output, host, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or SessionHtmlRenderException or ArgumentException)
        {
            await PiCommand.Line(host.Stderr, host.Color ? PiCommand.Red + "Error: " + error.Message + "\u001b[39m" : "Error: " + error.Message).ConfigureAwait(false);
            return 1;
        }
        await PiCommand.Line(host.Stdout, $"Exported to: {result}").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Source exportFromFile.</summary>
    internal static async Task<string> ExportFromFileAsync(string input, string? output, PiHost host, CancellationToken token)
    {
        var resolvedInput = PiPaths.ResolvePath(input, host.Cwd, host.Home);
        if (!File.Exists(resolvedInput)) throw new IOException($"File not found: {resolvedInput}");
        var lifecycle = new SessionLifecycleReadOnly(new(CopyOptions: new(PiSharp.Cli.Commands.PiPayloadBudget.SessionReader(new(MaximumLines: 100_000, MaximumRecords: 100_000)),
            PiSharp.Cli.Commands.PiPayloadBudget.SessionFileBytes)));
        var inspected = await lifecycle.InspectAsync(resolvedInput, true, null, cancellationToken: token).ConfigureAwait(false);
        var read = inspected.CopyInspection.Log;
        if (read.Header is null) throw new InvalidDataException($"Session file is not a valid {PiConfig.AppName} session: {resolvedInput}");
        var entries = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        var html = new SessionHtmlRenderer().Render(new(read.Header, entries, inspected.View?.Context.LeafId ?? entries.LastOrDefault()?.Id), token);
        var outputPath = output is not null ? PiPaths.NormalizePath(output, host.Home) : $"{PiConfig.AppName}-session-{(Path.GetFileName(resolvedInput) is var name && name.EndsWith(".jsonl", StringComparison.Ordinal) ? name[..^6] : Path.GetFileName(resolvedInput))}.html";
        var target = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(host.Cwd, outputPath);
        await File.WriteAllBytesAsync(target, [.. html.Utf8Bytes], token).ConfigureAwait(false);
        return outputPath;
    }
}
