// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/interactive-mode.ts (/login for
// every provider: handleLoginCommand, startProviderLogin, showLoginAuthTypeSelector, showLoginProviderSelector, getLoginProviderOptions,
// loginProvider, showAuthPrompt, notifyAuthDialog, completeProviderAuthentication), components/oauth-selector.ts (labels and status)
// and components/login-dialog.ts (showAuth, showDeviceCode, showWaiting, showInfo, showProgress, showPrompt, showManualInput).
using System.Globalization;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;

namespace PiSharp.Cli.Interactive;

internal sealed partial class InteractiveSessionFrontend
{
    /// <summary>Caller holds <c>state</c>. Starts the every-provider /login flow for an optional provider reference.</summary>
    private void StartProviderLoginLocked(ProviderLoginHost host, string reference)
    {
        var cancellation = new CancellationTokenSource(); loginCancellation = cancellation;
        loginRun = RunProviderLoginAsync(host, reference, cancellation);
    }

    private async Task RunProviderLoginAsync(ProviderLoginHost host, string reference, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        string? outcome; ProviderAuthCatalog.LoginOption? option = null;
        try
        {
            option = await ChooseLoginOptionAsync(host, reference, cancellation.Token).ConfigureAwait(false);
            if (option is null) outcome = null;
            else
            {
                lock (state)
                {
                    QueueLoginRenderLocked("Login to " + option.Name);
                    if (option.AuthType == "api_key" && option.Provider.Id == "amazon-bedrock")
                        QueueLoginRenderLocked("You can also use an AWS profile, IAM keys, or role-based credentials.");
                }
                await host.LoginAsync(option, new ProviderLoginInteraction(this, host), cancellation.Token).ConfigureAwait(false);
                outcome = (option.AuthType == "oauth" ? "Logged in to " : "Saved API key for ") + option.Name + ". Credentials saved to " + host.AuthPath;
            }
        }
        catch (Exception error) when (error is OperationCanceledException || error.Message == "Login cancelled") { outcome = "Login cancelled"; }
        catch (LoginChoiceException error) { outcome = error.Message; }
        catch (Exception error)
        {
            var message = error is OAuthLifecycleException lifecycle ? (lifecycle.OriginalException ?? error).Message : error.Message;
            outcome = option?.AuthType == "api_key" ? "[error] Failed to save API key for " + option.Name + ": " + message
                : "[error] Failed to login to " + (option?.Name ?? reference) + ": " + message;
        }
        Task rendered;
        lock (state)
        {
            loginPrompt?.Answer.TrySetCanceled(); loginPrompt = null;
            if (ReferenceEquals(loginCancellation, cancellation)) loginCancellation = null;
            rendered = outcome is null ? Task.CompletedTask : QueueLoginRenderLocked(outcome);
        }
        cancellation.Dispose();
        try { await rendered.ConfigureAwait(false); } catch (Exception) { /* A closed view only loses the login outcome line. */ }
    }

    private sealed class LoginChoiceException(string message) : Exception(message);

    /// <summary>handleLoginCommand: one matching option starts; several options of one provider choose the method; otherwise the
    /// auth-type selector (no reference) or the provider selector filtered by the reference.</summary>
    private async Task<ProviderAuthCatalog.LoginOption?> ChooseLoginOptionAsync(ProviderLoginHost host, string reference, CancellationToken token)
    {
        var stored = await StoredTypesAsync(host, token).ConfigureAwait(false);
        if (reference.Length == 0)
        {
            var radius = ProviderAuthCatalog.LoginOptions("oauth").First(option => option.Provider.Id == "radius");
            var radiusLabel = "Sign in with " + radius.Name + Status(radius, stored, host);
            var choice = await AskAsync("Select authentication method:",
                [new("oauth", "Sign in with an account"), new("api_key", "Sign in with an API key"), new("radius", radiusLabel)], token).ConfigureAwait(false);
            if (choice == "radius") return radius;
            return await SelectProviderAsync(host, stored, choice, null, token).ConfigureAwait(false);
        }
        var matches = ProviderAuthCatalog.FindLoginOptions(reference);
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1 && matches.Select(match => match.Provider.Id).Distinct().Count() == 1)
        {
            var provider = matches[0].Provider;
            var methods = new List<AnthropicOAuthLoginOption>();
            if (matches.Any(match => match.AuthType == "oauth")) methods.Add(new("oauth", provider.OAuth?.LoginLabel ?? "Sign in with an account"));
            if (matches.Any(match => match.AuthType == "api_key")) methods.Add(new("api_key", "Sign in with an API key"));
            var method = await AskAsync($"Select authentication method for {provider.Name}:", methods, token).ConfigureAwait(false);
            return matches.First(match => match.AuthType == method);
        }
        return await SelectProviderAsync(host, stored, null, reference, token).ConfigureAwait(false);
    }

    /// <summary>showLoginProviderSelector: "Select provider to configure:" sorted by name, with method labels and configured status.</summary>
    private async Task<ProviderAuthCatalog.LoginOption?> SelectProviderAsync(ProviderLoginHost host, IReadOnlyDictionary<string, string> stored,
        string? authType, string? search, CancellationToken token)
    {
        var options = ProviderAuthCatalog.LoginOptions(authType).OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (search is { Length: > 0 })
            options = options.Where(option => $"{option.Provider.Name} {option.Provider.Id} {option.AuthType} {(option.AuthType == "oauth" ? option.Provider.OAuth?.Name : option.Provider.ApiKeyName)}"
                .Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (options.Count == 0)
            throw new LoginChoiceException(search is { Length: > 0 } ? $"[error] No login provider matches \"{search}\"." :
                authType == "oauth" ? "No account providers available." : authType == "api_key" ? "No API key providers available." : "No login providers available.");
        var showTypes = options.Select(option => option.AuthType).Distinct().Count() > 1;
        var labels = options.Select((option, index) => new AnthropicOAuthLoginOption(index.ToString(CultureInfo.InvariantCulture),
            option.Name + (showTypes ? " [" + TypeLabel(option.AuthType, option.Provider.OAuth?.IsSubscription) + "]" : "") + Status(option, stored, host))).ToList();
        var chosen = await AskAsync("Select provider to configure:", labels, token).ConfigureAwait(false);
        return options[int.Parse(chosen, CultureInfo.InvariantCulture)];
    }

    private static string TypeLabel(string authType, bool? subscription) => authType == "api_key" ? "API key" : subscription == false ? "account" : "subscription";

    /// <summary>formatAuthSelectorProviderStatus over the stored credential type, else a set environment variable.</summary>
    private static string Status(ProviderAuthCatalog.LoginOption option, IReadOnlyDictionary<string, string> stored, ProviderLoginHost host)
    {
        if (stored.TryGetValue(option.Provider.Id, out var type))
            return type == option.AuthType ? " ✓ configured" : " • " + TypeLabel(type, option.Provider.OAuth?.IsSubscription) + " configured";
        var variables = option.Provider.EnvironmentVariables.Where(name => host.ReadEnvironment(name) is { Length: > 0 }).Take(1).ToList();
        if (variables.Count != 0)
            return option.AuthType == "api_key" ? " ✓ env: " + variables[0] : " • API key configured";
        return " • not configured";
    }

    private static async Task<IReadOnlyDictionary<string, string>> StoredTypesAsync(ProviderLoginHost host, CancellationToken token)
    {
        try { return (await host.Store.ListAsync(token).ConfigureAwait(false)).ToDictionary(row => row.Provider, row => row.Type, StringComparer.Ordinal); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return new Dictionary<string, string>(); }
    }

    /// <summary>A numbered selection (or a free-text prompt without options); /cancel rejects with "Login cancelled".</summary>
    private async Task<string> AskAsync(string text, IReadOnlyList<AnthropicOAuthLoginOption>? options, CancellationToken cancellationToken)
    {
        var display = options is null ? text : text + "\n" + string.Join('\n', options.Select((option, index) =>
            (index + 1).ToString(CultureInfo.InvariantCulture) + ": " + option.Label)) + "\n(Enter a number to select, /cancel to cancel)";
        var prompt = new LoginPrompt(new(TaskCreationOptions.RunContinuationsAsynchronously), options);
        lock (state)
        {
            cancellationToken.ThrowIfCancellationRequested();
            loginPrompt?.Answer.TrySetCanceled(); loginPrompt = prompt;
            QueueLoginRenderLocked(display);
        }
        using var registration = cancellationToken.Register(() =>
        {
            lock (state) if (ReferenceEquals(loginPrompt, prompt)) loginPrompt = null;
            prompt.Answer.TrySetException(new OperationCanceledException("Login cancelled", cancellationToken));
        });
        return await prompt.Answer.Task.ConfigureAwait(false);
    }

    /// <summary>showAuthPrompt and notifyAuthDialog over the cooked view.</summary>
    private sealed class ProviderLoginInteraction(InteractiveSessionFrontend owner, ProviderLoginHost host) : IProviderAuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            return prompt.Kind switch
            {
                AuthPromptKind.Select => owner.AskAsync(prompt.Message, [.. (prompt.Options ?? []).Select(option => new AnthropicOAuthLoginOption(option.Id, option.Label))], cancellationToken),
                AuthPromptKind.ManualCode => owner.AskAsync(prompt.Message + "\n(/cancel to cancel)", null, cancellationToken),
                _ => owner.AskAsync(prompt.Message + (string.IsNullOrEmpty(prompt.Placeholder) ? "" : "\ne.g., " + prompt.Placeholder) + "\n(/cancel to cancel)", null, cancellationToken)
            };
        }

        public void Notify(AuthEvent authEvent)
        {
            ArgumentNullException.ThrowIfNull(authEvent);
            lock (owner.state)
                switch (authEvent.Kind)
                {
                    case AuthEventKind.AuthUrl:
                        owner.QueueLoginRenderLocked(authEvent.Url ?? "");
                        if (!string.IsNullOrEmpty(authEvent.Instructions)) owner.QueueLoginRenderLocked(authEvent.Instructions);
                        break;
                    case AuthEventKind.DeviceCode:
                        owner.QueueLoginRenderLocked(authEvent.VerificationUri ?? "");
                        owner.QueueLoginRenderLocked("Enter code: " + authEvent.UserCode);
                        owner.QueueLoginRenderLocked("Waiting for authentication...\n(/cancel to cancel)");
                        break;
                    case AuthEventKind.Info:
                        owner.QueueLoginRenderLocked(string.Join('\n', new[] { authEvent.Message ?? "" }.Concat((authEvent.Links ?? [])
                            .Select(link => link.Label is { Length: > 0 } label ? $"{label}: {link.Url}" : link.Url))));
                        break;
                    default: owner.QueueLoginRenderLocked(authEvent.Message ?? ""); break;
                }
            if (authEvent.Kind == AuthEventKind.AuthUrl && authEvent.Url is { } url) host.OpenBrowser(url);
        }
    }
}
