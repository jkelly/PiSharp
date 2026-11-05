// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): coding-agent/src/modes/interactive/components/login-dialog.ts.
// Upstream shows every prompt (including AuthPrompt type "secret": API keys and tokens) in a plain Input and echoes the
// submitted value as "> value". That stays the default here. ShowPrompt's opt-in `secret` flag is a PiSharp addition: it
// renders the typed value and the submitted echo as one '*' per grapheme, leaving the value itself unchanged.
using System.Globalization;
using System.Text;
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
    /// <summary>The device-code confirm action (copy the code, open the page) and its hint row, until it is used or replaced.</summary>
    private (bool OpenBrowserOnConfirm, bool CopyCodeOnConfirm, string UserCode, string VerificationUri, Container Hint)? deviceCodeAction;
    private Container? deviceCodeHint;
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
    /// <summary>utils/clipboard.ts copyToClipboard for the auth URL and the device code; null uses
    /// <see cref="AuthUrlComponent.DefaultCopyToClipboard"/>.</summary>
    public Func<string, Task>? CopyToClipboard { get; init; }

    /// <summary>Source webUrl: the <c>http:</c> or <c>https:</c> URL <paramref name="value"/> names, normalized, or null. The shared launcher
    /// also opens files and custom protocols, so login only opens web URLs. Control characters are percent-encoded (as the URL
    /// serializer does), so none can end a terminal hyperlink early.</summary>
    internal static string? WebUrl(string value)
    {
        // URL parsing strips leading and trailing C0 controls and spaces and removes tabs and newlines.
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] <= ' ') start++;
        while (end > start && value[end - 1] <= ' ') end--;
        var encoded = new StringBuilder(end - start);
        foreach (var ch in value.AsSpan(start, end - start))
        {
            if (ch is '\t' or '\n' or '\r') continue;
            if (ch < ' ' || ch == '\u007f') encoded.Append('%').Append(((int)ch).ToString("X2", CultureInfo.InvariantCulture));
            else encoded.Append(ch);
        }
        if (!Uri.TryCreate(encoded.ToString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return null;
        var href = uri.AbsoluteUri;
        return href.Any(char.IsControl) ? null : href;
    }

    /// <summary>Source displayUrl: <paramref name="value"/> normalized as a web URL, else with control characters replaced: an untrusted
    /// string printed as is could end a hyperlink early or send its own escape sequences to the terminal.</summary>
    internal static string DisplayUrl(string value) => WebUrl(value) ?? TextUtils.ReplaceControlCharacters(value);

    /// <summary>Source webLink: <paramref name="text"/> as a terminal hyperlink to <paramref name="url"/> when that is a web URL, else
    /// plain text.</summary>
    internal static string WebLink(string text, string url) => WebUrl(url) is { } target ? TerminalImage.Hyperlink(text, target) : text;

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

    private void ClearDeviceCodeAction()
    {
        if (deviceCodeHint is not null) contentContainer.RemoveChild(deviceCodeHint);
        deviceCodeHint = null;
        deviceCodeAction = null;
    }

    private void Cancel()
    {
        ClearDeviceCodeAction();
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
        ClearDeviceCodeAction();
        contentContainer.Clear();
        contentContainer.AddChild(new Spacer(1));
        var target = WebUrl(url);
        authUrl = new AuthUrlComponent(tui, target ?? TextUtils.ReplaceControlCharacters(url), CopyToClipboard);
        contentContainer.AddChild(authUrl);

        if (!string.IsNullOrEmpty(instructions))
        {
            contentContainer.AddChild(new Spacer(1));
            contentContainer.AddChild(new Text(theme.Fg("warning", instructions), 1, 0));
        }

        if (target is not null) OpenBrowser(target);
        tui.RequestRender();
    }

    /// <summary>Called by onDeviceCode callback - show URL and user code. Unless the provider turned both off
    /// (<paramref name="openBrowserOnConfirm"/>, <paramref name="copyCodeOnConfirm"/>: absent means true), a hint row offers
    /// <c>tui.select.confirm</c> to copy the code and open the verification page; it then reports progress in the same row.</summary>
    public void ShowDeviceCode(string userCode, string verificationUri, bool? openBrowserOnConfirm = null, bool? copyCodeOnConfirm = null)
    {
        authUrl = null;
        var openBrowser = openBrowserOnConfirm ?? true;
        var copyCode = copyCodeOnConfirm ?? true;
        ClearDeviceCodeAction();
        contentContainer.Clear();
        contentContainer.AddChild(new Spacer(1));
        var shownUrl = DisplayUrl(verificationUri);
        contentContainer.AddChild(new Text(theme.Fg("accent", WebLink(shownUrl, shownUrl)), 1, 0));

        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        contentContainer.AddChild(new Text(theme.Fg("dim", WebLink(clickHint, verificationUri)), 1, 0));
        contentContainer.AddChild(new Spacer(1));
        contentContainer.AddChild(new Text(theme.Fg("warning", $"Enter code: {userCode}"), 1, 0));

        if (!Signal.IsCancellationRequested && (openBrowser || copyCode))
        {
            var actionLabel = openBrowser ? copyCode ? "to open browser and copy code" : "to open browser" : "to copy code";
            // Reuse this single row for progress/result text, including in narrow terminals.
            var hint = new Container();
            hint.AddChild(new TruncatedText($"({KeybindingHints.KeyHint("tui.select.confirm", actionLabel)})", 1, 0));
            contentContainer.AddChild(hint);
            deviceCodeHint = hint;
            deviceCodeAction = (openBrowser, copyCode, userCode, verificationUri, hint);
        }
        tui.RequestRender();
    }

    private void SetDeviceCodeStatus(Container hint, string message)
    {
        if (Signal.IsCancellationRequested || !focused || !contentContainer.Children.Contains(hint)) return;
        hint.Clear();
        hint.AddChild(new TruncatedText(theme.Fg("dim", message), 1, 0));
        tui.RequestRender();
    }

    private void ActivateDeviceCode((bool OpenBrowserOnConfirm, bool CopyCodeOnConfirm, string UserCode, string VerificationUri, Container Hint) action)
    {
        SetDeviceCodeStatus(action.Hint, action.CopyCodeOnConfirm ? "Copying code..." : "Browser open requested");
        if (action.CopyCodeOnConfirm)
        {
            // Do not delay polling or browser opening while clipboard tools are running; the result is reported on the UI loop.
            Task copy;
            try { copy = (CopyToClipboard ?? AuthUrlComponent.DefaultCopyToClipboard ?? throw new InvalidOperationException("Clipboard is not available"))(action.UserCode); }
            catch (Exception error) { copy = Task.FromException(error); }
            _ = copy.ContinueWith(done => tui.Loop.Post(() => SetDeviceCodeStatus(action.Hint, done.IsCompletedSuccessfully ? "Code copied to clipboard" : "Copy code manually")),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        if (action.OpenBrowserOnConfirm)
        {
            try
            {
                if (WebUrl(action.VerificationUri) is { } target) OpenBrowser(target);
            }
            catch (Exception)
            {
                // Launcher failures must not interrupt login.
            }
        }
    }

    /// <summary>Show input for manual code/URL entry (for callback server providers).</summary>
    public Task<string> ShowManualInput(string prompt)
    {
        ClearDeviceCodeAction();
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
        ClearDeviceCodeAction();
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
        ClearDeviceCodeAction();
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
            var url = DisplayUrl(link.Url);
            var text = !string.IsNullOrEmpty(link.Label) ? $"{link.Label}: {url}" : url;
            contentContainer.AddChild(new Text(theme.Fg("accent", WebLink(text, url)), 1, 0));
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

        if (deviceCodeAction is { } action && inputPromise is null && !Signal.IsCancellationRequested && focused && kb.Matches(data, "tui.select.confirm"))
        {
            // Consume once so a repeated Enter cannot launch multiple browsers or clipboard writes.
            deviceCodeAction = null;
            ActivateDeviceCode(action);
            tui.RequestRender();
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
            // Drawn like Input: the TUI resolves the fake cursor to reverse video, or omits it when the hardware cursor is shown.
            var line = masked + marker + TuiBase.RenderFakeCursor(" ");
            return [Prompt + line + new string(' ', Math.Max(0, available - TextUtils.VisibleWidth(line)))];
        }
    }
}
