// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/extension-input.ts.
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Source ExtensionInputOptions. <see cref="SetInterval"/> overrides the countdown's interval scheduler (tests).</summary>
internal sealed record ExtensionInputOptions(
    ITui? Tui = null,
    double? Timeout = null,
    string? InitialValue = null,
    string? Description = null,
    Func<Action, double, IDisposable>? SetInterval = null);

/// <summary>Simple text input component for extensions.</summary>
internal sealed class ExtensionInputComponent : Container, IFocusable, IInputHandler
{
    private readonly Input input;
    private readonly Action<string> onSubmitCallback;
    private readonly Action onCancelCallback;
    private readonly Text titleText;
    private readonly string baseTitle;
    private readonly CountdownTimer? countdown;

    // Focusable implementation - propagate to input for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; input.Focused = value; }
    }

    public ExtensionInputComponent(string title, string? placeholder, Action<string> onSubmit, Action onCancel, ExtensionInputOptions? opts = null)
    {
        _ = placeholder; // Upstream accepts and ignores the placeholder.
        onSubmitCallback = onSubmit;
        onCancelCallback = onCancel;
        baseTitle = title;

        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        titleText = new Text(theme.Fg("accent", title), 1, 0);
        AddChild(titleText);
        if (!string.IsNullOrEmpty(opts?.Description))
        {
            AddChild(new Spacer(1));
            AddChild(new Text(theme.Fg("text", opts.Description), 1, 0));
        }
        AddChild(new Spacer(1));

        if (opts?.Timeout is { } timeout && timeout > 0 && opts.Tui is not null)
        {
            countdown = new CountdownTimer(
                timeout,
                opts.Tui,
                s => titleText.SetText(theme.Fg("accent", $"{baseTitle} ({s}s)")),
                () => onCancelCallback(),
                opts.SetInterval);
        }

        input = new Input();
        if (!string.IsNullOrEmpty(opts?.InitialValue)) input.SetValue(opts.InitialValue);
        AddChild(input);
        AddChild(new Spacer(1));
        AddChild(new Text($"{KeybindingHints.KeyHint("tui.select.confirm", "submit")}  {KeybindingHints.KeyHint("tui.select.cancel", "cancel")}", 1, 0));
        AddChild(new Spacer(1));
        AddChild(new DynamicBorder());
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        if (kb.Matches(keyData, "tui.select.confirm") || keyData == "\n") onSubmitCallback(input.GetValue());
        else if (kb.Matches(keyData, "tui.select.cancel")) onCancelCallback();
        else input.HandleInput(keyData);
    }

    public void Dispose() => countdown?.Dispose();
}
