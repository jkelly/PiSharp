// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/utils/open-browser.ts.
using System.ComponentModel;
using System.Diagnostics;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

internal static class BrowserOpener
{
    /// <summary>The launcher command for a platform (<c>process.platform</c> names): <c>open</c> on macOS,
    /// <c>rundll32 url.dll,FileProtocolHandler</c> on Windows, <c>xdg-open</c> elsewhere.</summary>
    public static (string Command, string[] Args) LauncherFor(string platform, string target) => platform switch
    {
        "darwin" => ("open", [target]),
        "win32" => ("rundll32", ["url.dll,FileProtocolHandler", target]),
        _ => ("xdg-open", [target]),
    };

    /// <summary>Open a URL or file in the platform browser/default handler. This never invokes a shell: on Windows
    /// <c>cmd /c start</c> would re-parse metacharacters (&amp;, |, ^, ...) and make attacker-controlled URLs injectable.
    /// Launch is best-effort; failures (for example a missing xdg-open) are ignored because callers still show the target.</summary>
    public static void OpenBrowser(string target, Action<string, IReadOnlyList<string>>? spawn = null, string? platform = null)
    {
        var (command, args) = LauncherFor(platform ?? ClipboardEnvironment.CurrentPlatform, target);
        (spawn ?? SpawnDetached)(command, args);
    }

    /// <summary><c>spawn(cmd, args, { stdio: "ignore", detached: true }).on("error", () =&gt; {}).unref()</c>: the child's output is
    /// drained and discarded so it cannot write over the TUI, and nothing waits for it.</summary>
    private static void SpawnDetached(string command, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.Exited += static (sender, _) => (sender as Process)?.Dispose();
            if (!process.Start()) { process.Dispose(); return; }
            try { process.StandardInput.Close(); } catch (IOException) { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
        }
    }
}
