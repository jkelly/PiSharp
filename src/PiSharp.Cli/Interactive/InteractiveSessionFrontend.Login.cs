// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts
// (/login: handleLoginCommand, showLoginDialog, showAuthPrompt, notifyAuthDialog), components/login-dialog.ts and
// packages/coding-agent/src/core/slash-commands.ts (login).
using System.Globalization;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;

namespace PiSharp.Cli.Interactive;

internal sealed partial class InteractiveSessionFrontend
{
    private ProviderLoginHost? loginHost;
    private CancellationTokenSource? loginCancellation;
    private Task loginRun = Task.CompletedTask, loginRender = Task.CompletedTask;
    private LoginPrompt? loginPrompt;
    private sealed record LoginPrompt(TaskCompletionSource<string> Answer, IReadOnlyList<AnthropicOAuthLoginOption>? Options);

    /// <summary>Installs the provider login used by <c>/login</c>; without one the command reports that no login is available.</summary>
    internal void BindLogin(ProviderLoginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (state) { if (loginHost is not null) throw new InvalidOperationException("Login may bind once."); loginHost = host; }
    }

    /// <summary>The running login, settled when it succeeds, fails or is cancelled. Its outcome is rendered, never thrown.</summary>
    internal Task LoginCompletion { get { lock (state) return loginRun; } }

    /// <summary>Caller holds <c>state</c>. Returns true when the line belongs to /login: a command or the answer to its prompt.</summary>
    private bool TryLoginLineLocked(string line, out string? display)
    {
        display = null;
        if (loginPrompt is { } prompt)
        {
            if (line == "/cancel") { loginPrompt = null; prompt.Answer.TrySetException(new OperationCanceledException("Login cancelled")); return true; }
            if (prompt.Options is { } options)
            {
                if (!int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > options.Count)
                { display = "[login] Enter a number from 1 to " + options.Count.ToString(CultureInfo.InvariantCulture) + ", or /cancel."; return true; }
                loginPrompt = null; prompt.Answer.TrySetResult(options[number - 1].Id); return true;
            }
            loginPrompt = null; prompt.Answer.TrySetResult(line); return true;
        }
        if (line != "/login" && !line.StartsWith("/login ", StringComparison.Ordinal)) return false;
        var provider = line.Length > 7 ? line[7..].Trim() : "";
        if (loginHost is null) display = "No login providers available.";
        else if (!loginRun.IsCompleted) display = "[warning] A login is already in progress.";
        else if (provider.Length != 0 && !string.Equals(provider, ProviderLoginHost.AnthropicProvider, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(provider, ProviderLoginHost.AnthropicName, StringComparison.OrdinalIgnoreCase))
            display = "[error] No login provider matches \"" + provider + "\". Available: anthropic.";
        else
        {
            var cancellation = new CancellationTokenSource(); loginCancellation = cancellation;
            QueueLoginRenderLocked("Login to " + ProviderLoginHost.AnthropicName);
            loginRun = RunLoginAsync(loginHost, cancellation);
        }
        return true;
    }

    private async Task RunLoginAsync(ProviderLoginHost host, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        string outcome;
        try
        {
            await host.LoginAnthropicAsync(new LoginInteraction(this, host), cancellation.Token).ConfigureAwait(false);
            outcome = "Logged in to " + ProviderLoginHost.AnthropicName;
        }
        catch (Exception error) when (error is OperationCanceledException || error.Message == "Login cancelled") { outcome = "Login cancelled"; }
        catch (Exception error)
        {
            var message = error is OAuthLifecycleException lifecycle ? (lifecycle.OriginalException ?? error).Message : error.Message;
            outcome = "[error] Failed to login to " + ProviderLoginHost.AnthropicName + ": " + message;
        }
        Task rendered;
        lock (state)
        {
            loginPrompt?.Answer.TrySetCanceled(); loginPrompt = null;
            if (ReferenceEquals(loginCancellation, cancellation)) loginCancellation = null;
            rendered = QueueLoginRenderLocked(outcome);
        }
        cancellation.Dispose();
        try { await rendered.ConfigureAwait(false); } catch (Exception) { /* A closed view only loses the login outcome line. */ }
    }

    /// <summary>Caller holds <c>state</c>. Login lines render in order, after any line already queued.</summary>
    private Task QueueLoginRenderLocked(string text)
    {
        var previous = loginRender;
        return loginRender = Next();
        async Task Next()
        {
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            // A closed view only loses login progress lines.
            try { await RenderAsync(text, CancellationToken.None).ConfigureAwait(false); } catch (ObjectDisposedException) { }
        }
    }

    private void CancelLogin()
    {
        lock (state) { loginPrompt?.Answer.TrySetCanceled(); loginPrompt = null; }
        try { loginCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Source showAuthPrompt/notifyAuthDialog over the cooked view: a select lists numbered options; a manual-code prompt takes
    /// the next submitted line. <c>/cancel</c> rejects either with "Login cancelled"; a prompt whose signal aborts is withdrawn.</summary>
    private sealed class LoginInteraction(InteractiveSessionFrontend owner, ProviderLoginHost host) : IAnthropicOAuthLoginInteraction
    {
        public void Notify(AnthropicOAuthLoginEvent loginEvent)
        {
            ArgumentNullException.ThrowIfNull(loginEvent);
            lock (owner.state)
            {
                if (loginEvent.Kind == AnthropicOAuthLoginEventKind.AuthUrl)
                {
                    owner.QueueLoginRenderLocked(loginEvent.Url!);
                    if (!string.IsNullOrEmpty(loginEvent.Instructions)) owner.QueueLoginRenderLocked(loginEvent.Instructions);
                }
                else owner.QueueLoginRenderLocked(loginEvent.Message ?? "");
            }
            if (loginEvent.Kind == AnthropicOAuthLoginEventKind.AuthUrl) host.OpenBrowser(loginEvent.Url!);
        }

        public Task<string> SelectAsync(string message, IReadOnlyList<AnthropicOAuthLoginOption> options, CancellationToken cancellationToken) =>
            Ask(message + "\n" + string.Join('\n', options.Select((option, index) => (index + 1).ToString(CultureInfo.InvariantCulture) + ": " + option.Label)) +
                "\n(Enter a number to select, /cancel to cancel)", options, cancellationToken);

        public Task<string> PromptManualCodeAsync(string message, string placeholder, CancellationToken cancellationToken) =>
            Ask(message + "\n(/cancel to cancel)", null, cancellationToken);

        private async Task<string> Ask(string text, IReadOnlyList<AnthropicOAuthLoginOption>? options, CancellationToken cancellationToken)
        {
            var prompt = new LoginPrompt(new(TaskCreationOptions.RunContinuationsAsynchronously), options);
            lock (owner.state)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.loginPrompt?.Answer.TrySetCanceled(); owner.loginPrompt = prompt;
                owner.QueueLoginRenderLocked(text);
            }
            using var registration = cancellationToken.Register(() =>
            {
                lock (owner.state) if (ReferenceEquals(owner.loginPrompt, prompt)) owner.loginPrompt = null;
                prompt.Answer.TrySetException(new OperationCanceledException("Login cancelled", cancellationToken));
            });
            return await prompt.Answer.Task.ConfigureAwait(false);
        }
    }
}
