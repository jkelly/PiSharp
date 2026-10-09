// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/login-dialog.ts.
// Upstream shows every prompt (including AuthPrompt type "secret": API keys and tokens) in a plain Input and echoes the
// submitted value as "> value". That stays the default here. ShowPrompt's opt-in `secret` flag is a PiSharp addition: it
// renders the typed value and the submitted echo as one '*' per grapheme, leaving the value itself unchanged.
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>Login dialog component - replaces editor during OAuth login flow.</summary>
internal sealed partial class LoginDialogComponent : Container, IFocusable, IInputHandler
{
    private readonly Container contentContainer;
    private readonly Input input;
    private readonly MaskedInputView maskedInput;
    private bool inputMasked;
    private readonly ITui tui;
    /// <summary>The shown sign-in URL, which <c>app.message.copy</c> copies.</summary>
    private AuthUrlComponent? authUrl;
    private readonly CancellationTokenSource abortController = new();
    private TaskCompletionSource<string>? inputPromise;
    private readonly Action<bool, string?> onComplete;

    // Focusable implementation - propagate to input for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; input.Focused = value; }
    }

    /// <summary>utils/open-browser.ts openBrowser, called by <see cref="ShowAuth"/>.</summary>
    public Action<string> OpenBrowser { get; init; } = PiSharp.Cli.Commands.McpCommand.OpenBrowser;
    /// <summary>utils/clipboard.ts copyToClipboard for the auth URL; null uses <see cref="AuthUrlComponent.DefaultCopyToClipboard"/>.</summary>
    public Func<string, Task>? CopyToClipboard { get; init; }

    /// <param name="onComplete">(success, message).</param>
    public LoginDialogComponent(ITui tui, string providerId, Action<bool, string?> onComplete, string? providerNameOverride = null, string? titleOverride = null)
    {
        this.tui = tui;
        this.onComplete = onComplete;

        var providerName = string.IsNullOrEmpty(providerNameOverride) ? providerId : providerNameOverride;
        var title = titleOverride ?? $"Login to {providerName}";

        // Top border
        AddChild(new DynamicBorder());

        // Title
        AddChild(new Text(theme.Fg("accent", theme.Bold(title)), 1, 0));

        // Dynamic content area
        contentContainer = new Container();
        AddChild(contentContainer);

        // Input (always present, used when needed)
        input = new Input();
        maskedInput = new MaskedInputView(input);
        input.OnSubmit = _ =>
        {
            if (inputPromise is { } pending)
            {
                var value = input.GetValue();
                ReplaceInputWithSubmittedText(value);
                inputPromise = null;
                pending.TrySetResult(value);
            }
        };
        input.OnEscape = Cancel;

        // Bottom border
        AddChild(new DynamicBorder());
    }

    /// <summary>Source <c>signal</c>: cancelled when the user cancels the dialog.</summary>
    public CancellationToken Signal => abortController.Token;

    /// <summary>The component standing for the input in the content area: the input itself, or its masked view.</summary>
    private IComponent InputView => inputMasked ? maskedInput : input;

    private void ReplaceInputWithSubmittedText(string value)
    {
        var shown = inputMasked ? MaskedInputView.Mask(value) : value;
        var view = InputView;
        for (var i = 0; i < contentContainer.Children.Count; i++)
            if (ReferenceEquals(contentContainer.Children[i], view)) contentContainer.Children[i] = new Text($"> {shown}", 0, 0);
    }

    private void Cancel()
    {
        abortController.Cancel();
        if (inputPromise is { } pending)
        {
            inputPromise = null;
            pending.TrySetException(new OperationCanceledException("Login cancelled"));
        }
        onComplete(false, "Login cancelled");
    }

    /// <summary>Called by onAuth callback - show URL and optional instructions.</summary>
    public void ShowAuth(string url, string? instructions = null)
    {
        contentContainer.Clear();
        contentContainer.AddChild(new Spacer(1));
        authUrl = new AuthUrlComponent(tui, url, CopyToClipboard);
        contentContainer.AddChild(authUrl);

        if (!string.IsNullOrEmpty(instructions))
        {
            contentContainer.AddChild(new Spacer(1));
            contentContainer.AddChild(new Text(theme.Fg("warning", instructions), 1, 0));
        }

        OpenBrowser(url);
        tui.RequestRender();
    }

    /// <summary>Called by onDeviceCode callback - show URL and user code.</summary>
    public void ShowDeviceCode(string userCode, string verificationUri)
    {
        authUrl = null;
        contentContainer.Clear();
        contentContainer.AddChild(new Spacer(1));
        var linkedUrl = $"\u001b]8;;{verificationUri}\u0007{verificationUri}\u001b]8;;\u0007";
        contentContainer.AddChild(new Text(theme.Fg("accent", linkedUrl), 1, 0));

        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        var hyperlink = $"\u001b]8;;{verificationUri}\u0007{clickHint}\u001b]8;;\u0007";
        contentContainer.AddChild(new Text(theme.Fg("dim", hyperlink), 1, 0));
        contentContainer.AddChild(new Spacer(1));
        contentContainer.AddChild(new Text(theme.Fg("warning", $"Enter code: {userCode}"), 1, 0));

        tui.RequestRender();
    }

    /// <summary>Show input for manual code/URL entry (for callback server providers).</summary>
    public Task<string> ShowManualInput(string prompt)
    {
        input.SetValue("");
        inputMasked = false;
        contentContainer.AddChild(new Spacer(1));
        contentContainer.AddChild(new Text(theme.Fg("dim", prompt), 1, 0));
        contentContainer.AddChild(input);
        contentContainer.AddChild(new Text($"({KeybindingHints.KeyHint("tui.select.cancel", "to cancel")})", 1, 0));
        tui.RequestRender();

        return Pending();
    }

    /// <summary>
    /// Called by onPrompt callback - show prompt and wait for input.
    /// Note: Does NOT clear content, appends to existing (preserves URL from showAuth).
    /// </summary>
    /// <param name="secret">PiSharp addition (upstream has no masking): render the value masked, for AuthPrompt type "secret".</param>
    public Task<string> ShowPrompt(string message, string? placeholder = null, bool secret = false)
    {
        inputMasked = secret;
        contentContainer.AddChild(new Spacer(1));
        contentContainer.AddChild(new Text(theme.Fg("text", message), 1, 0));
        if (!string.IsNullOrEmpty(placeholder))
            contentContainer.AddChild(new Text(theme.Fg("dim", $"e.g., {placeholder}"), 1, 0));
        contentContainer.AddChild(InputView);
        contentContainer.AddChild(new Text(
            $"({KeybindingHints.KeyHint("tui.select.cancel", "to cancel,")} {KeybindingHints.KeyHint("tui.select.confirm", "to submit")})", 1, 0));

        input.SetValue("");
        tui.RequestRender();

        return Pending();
    }

    private Task<string> Pending()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        inputPromise = pending;
        return pending.Task;
    }

    /// <summary>Show informational text before another login step.</summary>
    public void ShowDetails(IEnumerable<string> lines)
    {
        authUrl = null;
        contentContainer.Clear();
        contentContainer.AddChild(new Spacer(1));
        foreach (var line in lines) contentContainer.AddChild(new Text(line, 1, 0));
        tui.RequestRender();
    }

    /// <summary>Show provider-owned information and links without starting an auth callback flow.</summary>
    public void ShowInfo(string message, IReadOnlyList<AuthInfoLink>? links = null, bool showCloseHint = false)
    {
        contentContainer.AddChild(new Spacer(1));
        contentContainer.AddChild(new Text(theme.Fg("text", message), 1, 0));
        foreach (var link in links ?? [])
        {
            var text = !string.IsNullOrEmpty(link.Label) ? $"{link.Label}: {link.Url}" : link.Url;
            var hyperlink = $"\u001b]8;;{link.Url}\u0007{text}\u001b]8;;\u0007";
            contentContainer.AddChild(new Text(theme.Fg("accent", hyperlink), 1, 0));
        }
        if (showCloseHint)
        {
            contentContainer.AddChild(new Spacer(1));
            contentContainer.AddChild(new Text($"({KeybindingHints.KeyHint("tui.select.cancel", "to close")})", 1, 0));
        }
        tui.RequestRender();
    }

    /// <summary>Show waiting message (for polling flows like GitHub Copilot).</summary>
    public void ShowWaiting(string message)
    {
        contentContainer.AddChild(new Spacer(1));
        contentContainer.AddChild(new Text(theme.Fg("dim", message), 1, 0));
        contentContainer.AddChild(new Text($"({KeybindingHints.KeyHint("tui.select.cancel", "to cancel")})", 1, 0));
        tui.RequestRender();
    }

    /// <summary>Called by onProgress callback.</summary>
    public void ShowProgress(string message)
    {
        contentContainer.AddChild(new Text(theme.Fg("dim", message), 1, 0));
        tui.RequestRender();
    }

    public void HandleInput(string data)
    {
        var kb = KeybindingsManager.Global;

        if (kb.Matches(data, "tui.select.cancel"))
        {
            Cancel();
            return;
        }
        if (authUrl is not null && kb.Matches(data, "app.message.copy"))
        {
            _ = authUrl.Copy();
            return;
        }

        // Pass to input
        input.HandleInput(data);
    }
}

internal sealed partial class LoginDialogComponent
{
    /// <summary>
    /// Renders an <see cref="Input"/> with its value masked: the prompt, one '*' per grapheme and the cursor cell at the end (the
    /// hidden value's cursor position is not shown). Editing still goes to the wrapped input.
    /// </summary>
    private sealed class MaskedInputView(Input input) : IComponent
    {
        private const string Prompt = "> ";

        public static string Mask(string value) => new('*', TextUtils.Graphemes(value).Count());

        public void Invalidate() { }

        public List<string> Render(int width)
        {
            var available = width - TextUtils.VisibleWidth(Prompt);
            if (available <= 0) return [TextUtils.TruncateToWidth(Prompt, width, "")];
            var masked = Mask(input.GetValue());
            // Keep the end (where the cursor is) visible, leaving one cell for the cursor.
            if (masked.Length > available - 1) masked = masked[^Math.Max(0, available - 1)..];
            var marker = input.Focused ? TuiBase.CursorMarker : "";
            var line = masked + marker + "\u001b[7m \u001b[27m";
            return [Prompt + line + new string(' ', Math.Max(0, available - TextUtils.VisibleWidth(line)))];
        }
    }
}
