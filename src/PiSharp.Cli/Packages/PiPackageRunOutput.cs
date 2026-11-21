// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/main.ts (packageCommandOutput,
// announcePackageInstalls, onPackageProgress) and packages/coding-agent/src/core/resource-loader.ts (the packageCommandOutput and
// onPackageProgress options of DefaultResourceLoader, handed to its DefaultPackageManager).
using PiSharp.Cli.Pi;

namespace PiSharp.Cli.Packages;

/// <summary>How one run of the Pi entry treats the package managers that resolve its resources (#11264). Interactive mode installs
/// and refreshes packages again on <c>/reload</c> while the TUI draws on the terminal, where git and npm output would corrupt its
/// screen: their output is captured, and until the TUI starts each install is announced on stderr instead. The other modes keep live
/// output.</summary>
internal sealed class PiPackageRunOutput
{
    private readonly TextWriter? _stderr;
    private readonly bool _color;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private bool _announce;

    /// <param name="commandOutput">Source packageCommandOutput: capture for interactive mode, else inherit.</param>
    /// <param name="stderr">Where installs are announced; null never announces.</param>
    /// <param name="color">Announcements are dim (chalk.dim) when the host's stderr takes color.</param>
    internal PiPackageRunOutput(PiPackageCommandOutput commandOutput, TextWriter? stderr = null, bool color = false)
    {
        CommandOutput = commandOutput; _stderr = stderr; _color = color;
        // Source announcePackageInstalls: only a run that captures the output announces.
        _announce = commandOutput == PiPackageCommandOutput.Capture && stderr is not null;
    }

    /// <summary>A run with live output and no announcements (source default: inherit, no onPackageProgress).</summary>
    internal static PiPackageRunOutput Inherit { get; } = new(PiPackageCommandOutput.Inherit);

    internal PiPackageCommandOutput CommandOutput { get; }

    /// <summary>Source <c>announcePackageInstalls = false</c>: called right before the interactive mode takes the terminal. Installs
    /// during a later reload are silent; a failure is reported by the mode with the end of the captured output.</summary>
    internal void StopAnnouncing() { lock (_announced) _announce = false; }

    /// <summary>Source onPackageProgress: the message of a <c>start</c> event (<c>Installing npm:foo...</c>, <c>Refreshing git:...</c>)
    /// on stderr while announcing. PiSharp resolves a run's sources more than once before the session starts (the extension pass
    /// before project trust, the pass after it, and the resource pass), so a message that was announced is not repeated.</summary>
    internal void OnProgress(PiPackageProgressEvent progress)
    {
        if (progress.Type != "start" || string.IsNullOrEmpty(progress.Message)) return;
        lock (_announced)
        {
            if (!_announce || !_announced.Add(progress.Message)) return;
            _stderr!.Write((_color ? "\u001b[2m" + progress.Message + "\u001b[22m" : progress.Message) + "\n");
            _stderr.Flush();
        }
    }

    /// <summary>Source DefaultResourceLoader's package manager: the processes run as <see cref="CommandOutput"/> says and progress goes
    /// to <see cref="OnProgress"/>. With inherited output git and npm write to <paramref name="output"/> and
    /// <paramref name="errorOutput"/>; a capturing run hands no stream of the terminal to the processes.</summary>
    internal PiPackageManager CreateManager(PiHost host, string cwd, string agentDir, string home, PiSettings settings, TextWriter output, TextWriter errorOutput,
        IEnumerable<string>? builtinExtensions = null)
    {
        var capture = CommandOutput == PiPackageCommandOutput.Capture;
        var manager = new PiPackageManager(cwd, agentDir, home, settings, host.GetEnvironment,
            PiPackageProcesses.For(host, capture ? TextWriter.Null : output, capture ? TextWriter.Null : errorOutput, CommandOutput), builtinExtensions);
        manager.SetProgressCallback(OnProgress);
        return manager;
    }
}
