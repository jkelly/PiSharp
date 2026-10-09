// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/main.ts (--export).
using PiSharp.CodingAgent.Export;

namespace PiSharp.Cli.Pi;

/// <summary><c>--export &lt;file&gt; [output]</c> over IMPL-G's exportFromFile port (<see cref="SessionHtmlExport.ExportFromFileAsync"/>):
/// <c>Exported to: &lt;path&gt;</c>, or <c>Error: &lt;message&gt;</c> and exit 1.</summary>
internal static class PiExport
{
    internal static async Task<int> RunAsync(string input, string? output, PiHost host, CancellationToken token)
    {
        string result;
        try { result = await SessionHtmlExport.ExportFromFileAsync(input, new(OutputPath: output), workingDirectory: host.Cwd, cancellationToken: token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var message = string.IsNullOrEmpty(error.Message) ? "Failed to export session" : error.Message;
            await PiCommand.Line(host.Stderr, host.Color ? PiCommand.Red + "Error: " + message + "\u001b[39m" : "Error: " + message).ConfigureAwait(false);
            return 1;
        }
        await PiCommand.Line(host.Stdout, $"Exported to: {result}").ConfigureAwait(false);
        return 0;
    }
}
