// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/providers/amazon-bedrock.ts (the AWS SDK default credential
// chain, whose @aws-sdk/credential-provider-process runs a profile's credential_process through child_process.exec).
using System.Diagnostics;
using System.Text;
using PiSharp.AI.Protocols.Bedrock;

namespace PiSharp.Cli.Commands;

/// <summary>Runs an AWS profile's <c>credential_process</c> as Node's <c>child_process.exec</c> does for the AWS SDK: through
/// <c>%ComSpec% /d /s /c "command"</c> on Windows and <c>/bin/sh -c</c> elsewhere, with the inherited environment, no timeout,
/// and a 1 MiB stdout limit (exec's maxBuffer). A non-zero exit or an overflow fails with the command's stderr.</summary>
internal static class AwsCredentialProcess
{
    private const int MaximumBuffer = 1024 * 1024;

    public static async Task<string> RunAsync(string command, CancellationToken token)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec ? comSpec : "cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.RedirectStandardInput = true;
        info.UseShellExecute = false; info.CreateNoWindow = true;
        info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or FileNotFoundException)
        { throw new AwsCredentialsException($"Command failed: {command}", error); }
        process.StandardInput.Close();
        using var registration = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = ReadLimitedAsync(process.StandardOutput, process);
        var errors = ReadLimitedAsync(process.StandardError, process);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var (stdout, stdoutOverflow) = await output.ConfigureAwait(false);
        var (stderr, stderrOverflow) = await errors.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (stdoutOverflow || stderrOverflow) throw new AwsCredentialsException("stdout maxBuffer length exceeded");
        if (process.ExitCode != 0) throw new AwsCredentialsException($"Command failed: {command}\n{stderr}");
        return stdout;
    }

    private static async Task<(string Text, bool Overflow)> ReadLimitedAsync(StreamReader reader, Process process)
    {
        var text = new StringBuilder(); var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaximumBuffer)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (text.ToString(0, MaximumBuffer), true);
            }
        }
        return (text.ToString(), false);
    }
}
