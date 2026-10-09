// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/bash-execution.ts.
// truncateTail is PiSharp.Agent's ToolOutputTruncator.Tail (the same algorithm and limits); stripAnsi is PiSharp.Tools'.
using PiSharp.Agent.Tools;
using PiSharp.Tools.Processes;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Component for displaying bash command execution with streaming output.</summary>
internal sealed class BashExecutionComponent : Container
{
    // Preview line limit when not expanded (matches tool execution behavior)
    private const int PreviewLines = 20;

    private readonly string command;
    private readonly List<string> outputLines = [];
    private string status = "running";
    private int? exitCode;
    private readonly Loader loader;
    private ToolOutputTruncationResult? truncationResult;
    private string? fullOutputPath;
    private bool expanded;
    private readonly Container contentContainer;
    /// <summary>"dim" marks <c>!!</c> commands, whose output is excluded from the model context.</summary>
    private readonly string colorKey;
    private int outputPad;

    public BashExecutionComponent(string command, ITui ui, bool excludeFromContext = false, int outputPad = 1)
    {
        this.command = command;
        colorKey = excludeFromContext ? "dim" : "bashMode";
        this.outputPad = outputPad;
        Func<string, string> borderColor = str => theme.Fg(colorKey, str);

        // Add spacer
        AddChild(new Spacer(1));

        // Top border
        AddChild(new DynamicBorder(borderColor));

        // Content container (holds dynamic content between borders)
        contentContainer = new Container();
        AddChild(contentContainer);

        loader = new Loader(ui, spinner => theme.Fg(colorKey, spinner), text => theme.Fg("muted", text),
            $"Running... ({KeybindingHints.KeyText("tui.select.cancel")} to cancel)"); // Plain text for loader

        // Bottom border
        AddChild(new DynamicBorder(borderColor));

        UpdateDisplay();
    }

    /// <summary>Set whether the output is expanded (shows full output) or collapsed (preview only).</summary>
    public void SetExpanded(bool expanded)
    {
        this.expanded = expanded;
        UpdateDisplay();
    }

    public void SetOutputPad(int outputPad)
    {
        this.outputPad = outputPad;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public void AppendOutput(string chunk)
    {
        // Strip ANSI codes and normalize line endings
        // Note: binary data is already sanitized in tui-renderer.ts executeBashCommand
        var clean = ShellAnsiText.StripAnsi(chunk).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        // Append to output lines
        var newLines = clean.Split('\n');
        if (outputLines.Count > 0 && newLines.Length > 0)
        {
            // Append first chunk to last line (incomplete line continuation)
            outputLines[^1] += newLines[0];
            outputLines.AddRange(newLines.Skip(1));
        }
        else outputLines.AddRange(newLines);

        UpdateDisplay();
    }

    public void SetComplete(int? exitCode, bool cancelled, ToolOutputTruncationResult? truncationResult = null, string? fullOutputPath = null)
    {
        this.exitCode = exitCode;
        status = cancelled ? "cancelled" : exitCode is not null && exitCode != 0 ? "error" : "complete";
        this.truncationResult = truncationResult;
        this.fullOutputPath = fullOutputPath;

        // Stop loader
        loader.Stop();

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        // Apply truncation for LLM context limits (same limits as bash tool)
        var fullOutput = string.Join("\n", outputLines);
        var contextTruncation = ToolOutputTruncator.Tail(fullOutput, new ToolOutputTruncationOptions(ToolTruncate.DefaultMaxLines, ToolTruncate.DefaultMaxBytes));

        // Get the lines to potentially display (after context truncation)
        var availableLines = contextTruncation.Content.Length > 0 ? contextTruncation.Content.Split('\n') : [];

        // Apply preview truncation based on expanded state
        var previewLogicalLines = availableLines.Skip(Math.Max(0, availableLines.Length - PreviewLines)).ToArray();
        var hiddenLineCount = availableLines.Length - previewLogicalLines.Length;

        // Rebuild content container
        contentContainer.Clear();

        // Command header
        var header = new Text(theme.Fg(colorKey, theme.Bold($"$ {command}")), outputPad, 0);
        contentContainer.AddChild(header);

        // Output
        if (availableLines.Length > 0)
        {
            if (expanded)
            {
                // Show all lines
                var displayText = string.Join("\n", availableLines.Select(line => theme.Fg("muted", line)));
                contentContainer.AddChild(new Text("\n" + displayText, outputPad, 0));
            }
            else
            {
                // Use shared visual truncation utility with width-aware caching
                var styledOutput = string.Join("\n", previewLogicalLines.Select(line => theme.Fg("muted", line)));
                contentContainer.AddChild(new CollapsedPreview("\n" + styledOutput, outputPad));
            }
        }

        // Loader or status
        if (status == "running") contentContainer.AddChild(loader);
        else
        {
            var statusParts = new List<string>();

            // Show how many lines are hidden (collapsed preview)
            if (hiddenLineCount > 0)
            {
                if (expanded)
                    statusParts.Add(theme.Fg("muted", "(") + KeybindingHints.KeyHint("app.tools.expand", "to collapse") + theme.Fg("muted", ")"));
                else
                    statusParts.Add(theme.Fg("muted", $"... {hiddenLineCount} more lines (") + KeybindingHints.KeyHint("app.tools.expand", "to expand") + theme.Fg("muted", ")"));
            }

            if (status == "cancelled") statusParts.Add(theme.Fg("warning", "(cancelled)"));
            else if (status == "error") statusParts.Add(theme.Fg("error", $"(exit {exitCode})"));

            // Add truncation warning (context truncation, not preview truncation)
            var wasTruncated = truncationResult?.Truncated == true || contextTruncation.Truncated;
            if (wasTruncated && !string.IsNullOrEmpty(fullOutputPath))
                statusParts.Add(theme.Fg("warning", $"Output truncated. Full output: {fullOutputPath}"));

            if (statusParts.Count > 0) contentContainer.AddChild(new Text("\n" + string.Join("\n", statusParts), outputPad, 0));
        }
    }

    /// <summary>Get the raw output for creating BashExecutionMessage.</summary>
    public string GetOutput() => string.Join("\n", outputLines);

    /// <summary>Get the command that was executed.</summary>
    public string GetCommand() => command;

    /// <summary>The collapsed output: the last visual lines at the render width, cached per width.</summary>
    private sealed class CollapsedPreview(string styledInput, int outputPad) : IComponent
    {
        private int? cachedWidth;
        private List<string>? cachedLines;

        public List<string> Render(int width)
        {
            if (cachedLines is null || cachedWidth != width)
            {
                var result = VisualTruncate.TruncateToVisualLines(styledInput, PreviewLines, width, outputPad);
                cachedLines = result.VisualLines;
                cachedWidth = width;
            }
            return cachedLines;
        }

        public void Invalidate()
        {
            cachedWidth = null;
            cachedLines = null;
        }
    }
}
