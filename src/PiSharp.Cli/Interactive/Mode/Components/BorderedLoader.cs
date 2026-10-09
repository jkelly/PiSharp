// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/bordered-loader.ts.
// AbortSignal maps to CancellationToken.
using PiSharp.Tui.Pi;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Loader wrapped with borders for extension UI</summary>
internal sealed class BorderedLoader : Container, IInputHandler, IDisposableComponent
{
    private readonly Loader loader;
    private readonly bool cancellable;
    private readonly CancellationTokenSource? signalController;

    public BorderedLoader(ITui tui, Theme theme, string message, bool cancellable = true)
    {
        this.cancellable = cancellable;
        string BorderColor(string s) => theme.Fg("border", s);
        AddChild(new DynamicBorder(BorderColor));
        if (this.cancellable)
        {
            loader = new CancellableLoader(tui, s => theme.Fg("accent", s), s => theme.Fg("muted", s), message);
        }
        else
        {
            signalController = new CancellationTokenSource();
            loader = new Loader(tui, s => theme.Fg("accent", s), s => theme.Fg("muted", s), message);
        }
        AddChild(loader);
        if (this.cancellable)
        {
            AddChild(new Spacer(1));
            AddChild(new Text(KeybindingHints.KeyHint("tui.select.cancel", "cancel"), 1, 0));
        }
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder(BorderColor));
    }

    public CancellationToken Signal
    {
        get
        {
            if (cancellable) return ((CancellableLoader)loader).Signal;
            return signalController?.Token ?? new CancellationTokenSource().Token;
        }
    }

    public Action? OnAbort
    {
        set
        {
            if (cancellable) ((CancellableLoader)loader).OnAbort = value;
        }
    }

    public void HandleInput(string data)
    {
        if (cancellable) ((CancellableLoader)loader).HandleInput(data);
    }

    public void Dispose() => loader.Dispose();
}
