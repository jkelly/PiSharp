// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/status-indicator.ts.
// WorkingIndicatorOptions (core/extensions/types.ts: frames, intervalMs) is PiSharp.Tui's LoaderIndicatorOptions.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>"working" | "retry" | "compaction" | "branchSummary".</summary>
internal enum StatusIndicatorKind { Working, Retry, Compaction, BranchSummary }

internal class StatusIndicator : Loader
{
    public StatusIndicatorKind Kind { get; }

    public StatusIndicator(StatusIndicatorKind kind, ITui ui, Func<string, string> spinnerColorFn, Func<string, string> messageColorFn, string message,
        LoaderIndicatorOptions? indicator = null)
        : base(ui, spinnerColorFn, messageColorFn, message, indicator)
    {
        Kind = kind;
    }

    public string RenderInBorder(int width)
    {
        var rendered = base.Render(width + 2);
        var line = rendered.Count > 1 ? rendered[1] : "";
        return TextUtils.TruncateToWidth(line.StartsWith(' ') ? TextUtils.JsTrimEnd(line[1..]) : TextUtils.JsTrimEnd(line), width, "");
    }

    public string RenderSpinnerInBorder(int width) => TextUtils.TruncateToWidth(GetRenderedIndicator(), width, "");

    public override void Dispose() => Stop();
}

internal sealed class WorkingStatusIndicator(ITui ui, string message, LoaderIndicatorOptions? indicator = null, Func<string, string>? colorFn = null)
    : StatusIndicator(StatusIndicatorKind.Working, ui, colorFn ?? (text => theme.Fg("accent", text)), colorFn ?? (text => theme.Fg("muted", text)), message, indicator);

internal sealed class RetryStatusIndicator : StatusIndicator
{
    private CountdownTimer? countdown;

    public RetryStatusIndicator(ITui ui, int attempt, int maxAttempts, double delayMs)
        : base(StatusIndicatorKind.Retry, ui, spinner => theme.Fg("warning", spinner), text => theme.Fg("muted", text),
            RetryMessage(attempt, maxAttempts, (int)Math.Ceiling(delayMs / 1000)))
    {
        countdown = new CountdownTimer(delayMs, ui, seconds => SetMessage(RetryMessage(attempt, maxAttempts, seconds)), () => countdown = null);
    }

    private static string RetryMessage(int attempt, int maxAttempts, int seconds) =>
        $"Retrying ({attempt}/{maxAttempts}) in {seconds}s... ({KeybindingHints.KeyText("app.interrupt")} to cancel)";

    public override void Dispose()
    {
        countdown?.Dispose();
        countdown = null;
        base.Dispose();
    }
}

/// <summary>"manual" | "threshold" | "overflow".</summary>
internal enum CompactionStatusReason { Manual, Threshold, Overflow }

internal sealed class CompactionStatusIndicator(ITui ui, CompactionStatusReason reason)
    : StatusIndicator(StatusIndicatorKind.Compaction, ui, spinner => theme.Fg("accent", spinner), text => theme.Fg("muted", text), Label(reason))
{
    private static string Label(CompactionStatusReason reason)
    {
        var cancelHint = $"({KeybindingHints.KeyText("app.interrupt")} to cancel)";
        return reason == CompactionStatusReason.Manual
            ? $"Compacting context... {cancelHint}"
            : $"{(reason == CompactionStatusReason.Overflow ? "Context overflow detected, " : "")}Auto-compacting... {cancelHint}";
    }
}

internal sealed class BranchSummaryStatusIndicator(ITui ui)
    : StatusIndicator(StatusIndicatorKind.BranchSummary, ui, spinner => theme.Fg("accent", spinner), text => theme.Fg("muted", text),
        $"Summarizing branch... ({KeybindingHints.KeyText("app.interrupt")} to cancel)");

internal sealed class IdleStatus : IComponent
{
    public void Invalidate()
    {
        // No cached state to invalidate.
    }

    public List<string> Render(int width)
    {
        var emptyLine = new string(' ', Math.Max(0, width));
        return [emptyLine, emptyLine];
    }
}
