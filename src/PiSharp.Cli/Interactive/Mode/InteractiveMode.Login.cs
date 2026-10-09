// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts (/login and /logout:
// getLoginProviderOptions, getLogoutProviderOptions, handleLoginCommand, startProviderLogin, showLoginAuthTypeSelector,
// showLoginProviderSelector, showOAuthSelector, completeProviderAuthentication, showAmbientAuthDialog, showApiKeyLoginDialog,
// showAuthSelect, showAuthPrompt, notifyAuthDialog, loginProvider, showLoginDialog, offerRadiusMcpServer). Credentials go through
// the CLI's ProviderLoginHost (auth.json). Secret prompts render unmasked like upstream 1.1.0 (the dialog keeps a masking option).
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode;

internal sealed record LoginProviderCompletionOption(string Id, string Name, List<string> AuthTypes, bool? Subscription);

internal sealed partial class InteractiveMode
{
    private const string RadiusProviderId = "radius";
    private const string RadiusLoginIntro = "Radius is a service crafted for Pi by the builders of Pi, Earendil Works";
    private IReadOnlyDictionary<string, string> storedCredentialTypes = new Dictionary<string, string>(StringComparer.Ordinal);

    private async Task RefreshStoredCredentialTypesAsync()
    {
        try
        {
            if (context.Login is null) return;
            storedCredentialTypes = (await context.Login.Store.ListAsync(CancellationToken.None)).ToDictionary(row => row.Provider, row => row.Type, StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
    }

    /// <summary>The provider catalog with each provider's configured status (stored credential, else an environment variable).</summary>
    private sealed class LoginCatalog(InteractiveMode mode) : ILoginProviderCatalog
    {
        public IReadOnlyList<LoginProviderInfo> GetProviders() => ProviderAuthCatalog.Entries().Select(provider => new LoginProviderInfo(provider.Id, provider.Name,
            provider.OAuth is { } oauth ? new AuthMethodInfo(oauth.Name, true, oauth.IsSubscription, oauth.LoginLabel) : null,
            new AuthMethodInfo(provider.ApiKeyName, provider.ApiKeyLogin != ApiKeyLoginKind.Secret || provider.EnvironmentVariables.Length > 0))).ToList();

        public ProviderAuthStatus GetProviderAuthStatus(string providerId)
        {
            if (mode.storedCredentialTypes.ContainsKey(providerId)) return new(true, null, "stored credential");
            var provider = ProviderAuthCatalog.Find(providerId);
            var variable = provider?.EnvironmentVariables.FirstOrDefault(name => !string.IsNullOrEmpty(mode.context.GetEnvironment(name)));
            return variable is not null ? new(true, null, variable) : new(false);
        }

        public bool IsUsingOAuth(string providerId) => mode.storedCredentialTypes.TryGetValue(providerId, out var type) && type == "oauth";
    }

    private List<AuthSelectorProvider> GetLoginProviderOptions(string? authType = null) =>
        AuthSelectorFormatting.GetLoginProviderOptions(new LoginCatalog(this), authType);

    private static List<LoginProviderCompletionOption> GetLoginProviderCompletionOptions(IReadOnlyList<AuthSelectorProvider> providerOptions)
    {
        var byId = new Dictionary<string, LoginProviderCompletionOption>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var provider in providerOptions)
        {
            if (byId.TryGetValue(provider.Id, out var existing))
            {
                if (!existing.AuthTypes.Contains(provider.AuthType))
                {
                    existing.AuthTypes.Add(provider.AuthType);
                    existing.AuthTypes.Sort((a, b) => (a == "oauth" ? 0 : 1).CompareTo(b == "oauth" ? 0 : 1));
                }
                continue;
            }
            byId[provider.Id] = new(provider.Id, provider.Name, [provider.AuthType], provider.Subscription);
            order.Add(provider.Id);
        }
        return [.. order.Select(id => byId[id]).OrderBy(option => option.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, false))];
    }

    private static string GetLoginProviderSearchText(LoginProviderCompletionOption provider) =>
        $"{provider.Id} {provider.Name} {string.Join(" ", provider.AuthTypes.Select(type => $"{type} {AuthSelectorFormatting.FormatAuthSelectorProviderType(type, provider.Subscription)}"))}";

    private static string FormatLoginProviderCompletionDescription(LoginProviderCompletionOption provider)
    {
        var authTypes = string.Join("/", provider.AuthTypes.Select(type => AuthSelectorFormatting.FormatAuthSelectorProviderType(type, provider.Subscription)));
        return provider.Name == provider.Id ? authTypes : $"{provider.Name} · {authTypes}";
    }

    private List<AuthSelectorProvider> FindLoginProviderOptions(string providerRef)
    {
        var normalized = TextUtils.JsTrim(providerRef).ToLowerInvariant();
        if (normalized.Length == 0) return [];
        return [.. GetLoginProviderOptions().Where(provider => provider.Id.ToLowerInvariant() == normalized || provider.Name.ToLowerInvariant() == normalized)];
    }

    private async Task HandleLoginCommandAsync(string? providerRef)
    {
        await RefreshStoredCredentialTypesAsync();
        if (string.IsNullOrEmpty(providerRef)) { ShowLoginAuthTypeSelector(); return; }
        var providerOptions = FindLoginProviderOptions(providerRef);
        if (providerOptions.Count == 1) { await StartProviderLoginAsync(providerOptions[0]); return; }
        if (providerOptions.Count > 1 && providerOptions.Select(provider => provider.Id).Distinct().Count() == 1)
        {
            ShowLoginAuthTypeSelector(providerOptions);
            return;
        }
        ShowLoginProviderSelector(null, providerRef);
    }

    /// <summary><paramref name="onBack"/> reopens the selector the login was started from when the user cancels it.</summary>
    private async Task StartProviderLoginAsync(AuthSelectorProvider providerOption, Action? onBack = null)
    {
        if (providerOption.AuthType == "oauth") await ShowLoginDialogAsync(providerOption.Id, providerOption.Name, onBack);
        else if (providerOption.Method?.HasLogin != false) await ShowApiKeyLoginDialogAsync(providerOption.Id, providerOption.Name, onBack);
        else ShowAmbientAuthDialog(providerOption, onBack);
    }

    private void ShowLoginAuthTypeSelector(IReadOnlyList<AuthSelectorProvider>? providerOptions = null)
    {
        var radiusOption = providerOptions is null ? GetLoginProviderOptions("oauth").FirstOrDefault(provider => provider.Id == RadiusProviderId) : null;
        var radiusText = radiusOption is not null ? $"Sign in with {radiusOption.Name}" : null;
        var radiusLabel = radiusOption is not null ? radiusText + AuthSelectorFormatting.FormatAuthSelectorProviderStatus(radiusOption) : null;
        var oauthProvider = providerOptions?.FirstOrDefault(provider => provider.AuthType == "oauth");
        var subscriptionLabel = oauthProvider?.Method?.LoginLabel ?? "Sign in with an account";
        const string apiKeyLabel = "Sign in with an API key";
        var availableAuthTypes = providerOptions is not null ? providerOptions.Select(provider => provider.AuthType).ToHashSet(StringComparer.Ordinal) : ["oauth", "api_key"];
        var options = new List<string>();
        if (availableAuthTypes.Contains("oauth")) options.Add(subscriptionLabel);
        if (availableAuthTypes.Contains("api_key")) options.Add(apiKeyLabel);
        if (radiusLabel is not null) options.Add(radiusLabel);
        if (options.Count == 0) { ShowStatus("No login methods available."); return; }
        if (providerOptions is not null && options.Count == 1)
        {
            if (providerOptions.Count > 0) Run(() => StartProviderLoginAsync(providerOptions[0]));
            return;
        }
        var title = providerOptions is { Count: > 0 } ? $"Select authentication method for {providerOptions[0].Name}:" : "Select authentication method:";
        ShowSelector(done =>
        {
            void OnSelect(string option)
            {
                done();
                if (radiusOption is not null && option == radiusLabel)
                {
                    Run(() => StartProviderLoginAsync(radiusOption, () => ShowLoginAuthTypeSelector()));
                    return;
                }
                var authType = option == subscriptionLabel ? "oauth" : "api_key";
                if (providerOptions is not null)
                {
                    if (providerOptions.FirstOrDefault(provider => provider.AuthType == authType) is { } providerOption)
                        Run(() => StartProviderLoginAsync(providerOption, () => ShowLoginAuthTypeSelector(providerOptions)));
                    return;
                }
                ShowLoginProviderSelector(authType);
            }
            void OnCancel() { done(); ui.RequestRender(); }
            if (radiusLabel is not null && radiusText is not null)
            {
                var menu = RadiusLoginSelector.CreateLoginMenuSelector(ui, title, options, new RadiusOption(radiusLabel, radiusText), OnSelect, OnCancel);
                return (menu, menu, () => (menu as IDisposableComponent)?.Dispose());
            }
            var selector = new ExtensionSelectorComponent(title, options, OnSelect, OnCancel);
            return (selector, selector, selector.Dispose);
        });
    }

    private void ShowLoginProviderSelector(string? authType = null, string? initialSearchInput = null)
    {
        var providerOptions = GetLoginProviderOptions(authType);
        if (providerOptions.Count == 0)
        {
            ShowStatus(authType == "oauth" ? "No account providers available." : authType == "api_key" ? "No API key providers available." : "No login providers available.");
            return;
        }
        ShowSelector(done =>
        {
            var selector = new OAuthSelectorComponent("login", providerOptions, (providerId, selectedAuthType) =>
            {
                done();
                var providerOption = providerOptions.FirstOrDefault(provider => provider.Id == providerId && provider.AuthType == selectedAuthType);
                if (providerOption is null) return;
                Run(() => StartProviderLoginAsync(providerOption, () => ShowLoginProviderSelector(authType, initialSearchInput)));
            }, () =>
            {
                done();
                if (authType is not null) ShowLoginAuthTypeSelector();
                else ui.RequestRender();
            }, initialSearchInput);
            return (selector, selector, null);
        });
    }

    private async Task ShowOAuthSelectorAsync(string mode)
    {
        if (mode == "login") { await RefreshStoredCredentialTypesAsync(); ShowLoginAuthTypeSelector(); return; }
        List<AuthSelectorProvider> providerOptions;
        try
        {
            if (context.Login is null) throw new InvalidOperationException("Credential storage is unavailable.");
            using var timeout = new CancellationTokenSource(15_000);
            var stored = await context.Login.Store.ListAsync(timeout.Token);
            providerOptions = [.. stored.Select(row =>
            {
                var provider = ProviderAuthCatalog.Find(row.Provider);
                return new AuthSelectorProvider(row.Provider, provider?.Name ?? row.Provider, row.Type, null, new AuthCheck(row.Type, "stored credential"),
                    provider?.OAuth?.IsSubscription == true);
            }).OrderBy(option => option.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, false))];
        }
        catch (Exception error)
        {
            ShowError($"Could not read stored credentials: {error.Message}");
            return;
        }
        if (providerOptions.Count == 0)
        {
            ShowStatus("No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.");
            return;
        }
        ShowSelector(done =>
        {
            var selector = new OAuthSelectorComponent(mode, providerOptions, (providerId, _) => Run(async () =>
            {
                done();
                var providerOption = providerOptions.FirstOrDefault(provider => provider.Id == providerId);
                if (providerOption is null) return;
                try
                {
                    using var timeout = new CancellationTokenSource(15_000);
                    await context.Login!.Store.DeleteAsync(providerOption.Id, timeout.Token);
                    await RefreshStoredCredentialTypesAsync();
                    try { await context.OnCredentialsChanged(providerOption.Id); } catch { }
                    await RefreshAvailableModelsAsync();
                    UpdateAvailableProviderCount();
                    ShowStatus(providerOption.AuthType == "oauth"
                        ? $"Logged out of {providerOption.Name}"
                        : $"Removed stored API key for {providerOption.Name}. Environment variables and models.json config are unchanged.");
                }
                catch (Exception error) { ShowError($"Logout failed: {error.Message}"); }
            }), () => { done(); ui.RequestRender(); });
            return (selector, selector, null);
        });
    }

    private async Task CompleteProviderAuthenticationAsync(string providerId, string providerName, string authType)
    {
        var actionLabel = authType == "oauth" ? $"Logged in to {providerName}" : $"Saved API key for {providerName}";
        await RefreshStoredCredentialTypesAsync();
        try { await context.OnCredentialsChanged(providerId); } catch { }
        await RefreshAvailableModelsAsync();
        UpdateAvailableProviderCount();
        footer.Invalidate();
        UpdateEditorBorderColor();
        ShowStatus($"{actionLabel}. Credentials saved to {context.Login?.AuthPath}");
        _ = MaybeWarnAboutAnthropicSubscriptionAuthAsync();
    }

    private void ShowAmbientAuthDialog(AuthSelectorProvider providerOption, Action? onBack)
    {
        void RestoreEditor()
        {
            editorContainer.Clear();
            editorContainer.AddChild(editor);
            ui.SetFocus(editor);
            ui.RequestRender();
        }
        var dialog = new LoginDialogComponent(ui, providerOption.Id, (_, _) => { RestoreEditor(); onBack?.Invoke(); }, providerOption.Name, $"{providerOption.Name} setup")
        { CopyToClipboard = context.CopyToClipboard };
        dialog.ShowInfo($"{providerOption.Method?.Name ?? "Authentication"} is configured outside {AppName}.", [], true);
        editorContainer.Clear();
        editorContainer.AddChild(dialog);
        ui.SetFocus(dialog);
        ui.RequestRender();
    }

    private Task ShowApiKeyLoginDialogAsync(string providerId, string providerName, Action? onBack) =>
        RunLoginDialogAsync(providerId, providerName, "api_key", onBack);

    private Task ShowLoginDialogAsync(string providerId, string providerName, Action? onBack) =>
        RunLoginDialogAsync(providerId, providerName, "oauth", onBack);

    private async Task RunLoginDialogAsync(string providerId, string providerName, string method, Action? onBack)
    {
        var dialog = new LoginDialogComponent(ui, providerId, (_, _) => { }, providerName) { CopyToClipboard = context.CopyToClipboard };
        if (method == "api_key" && providerId == "amazon-bedrock")
            dialog.ShowDetails([
                theme.Fg("text", "You can also use an AWS profile, IAM keys, or role-based credentials."),
                theme.Fg("muted", "See:"),
                theme.Fg("accent", $"  {Path.Join(context.DocsPath, "providers.md")}")
            ]);
        editorContainer.Clear();
        editorContainer.AddChild(dialog);
        ui.SetFocus(dialog);
        ui.RequestRender();
        void RestoreEditor()
        {
            editorContainer.Clear();
            editorContainer.AddChild(editor);
            ui.SetFocus(editor);
            ui.RequestRender();
        }
        try
        {
            await LoginProviderAsync(dialog, providerId, providerName, method);
            RestoreEditor();
            await CompleteProviderAuthenticationAsync(providerId, providerName, method);
            if (method == "oauth" && providerId == RadiusProviderId) OfferRadiusMcpServer(providerId, providerName);
        }
        catch (Exception error)
        {
            RestoreEditor();
            var message = error is OAuthLifecycleException lifecycle ? (lifecycle.OriginalException ?? error).Message : error.Message;
            if (message == "Login cancelled" || error is OperationCanceledException) onBack?.Invoke();
            else if (method == "oauth") ShowError($"Failed to login to {providerName}: {message}");
            else ShowError($"Failed to save API key for {providerName}: {message}");
        }
    }

    private async Task LoginProviderAsync(LoginDialogComponent dialog, string providerId, string providerName, string method)
    {
        if (context.Login is null) throw new InvalidOperationException("Provider login is unavailable in this host.");
        var option = ProviderAuthCatalog.LoginOptions(method).FirstOrDefault(entry => entry.Provider.Id == providerId)
            ?? throw new InvalidOperationException($"Unknown provider: {providerId}");
        programStatus.SetBlocked("login", new BlockedStatus(ProgramBlockedKind.Auth, $"Log in to {providerName}"));
        try { await context.Login.LoginAsync(option, new DialogInteraction(this, dialog, providerId), dialog.Signal); }
        finally { programStatus.SetBlocked("login", null); }
    }

    /// <summary>showAuthPrompt and notifyAuthDialog: select prompts open a selector; text and secret prompts the dialog's input
    /// (unmasked, as upstream).</summary>
    private sealed class DialogInteraction(InteractiveMode mode, LoginDialogComponent dialog, string providerId) : IProviderAuthInteraction
    {
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken) =>
            mode.context.Loop.InvokeAsync(() => prompt.Kind switch
            {
                AuthPromptKind.Select => mode.ShowAuthSelectAsync(dialog, prompt, providerId),
                AuthPromptKind.ManualCode => dialog.ShowManualInput(prompt.Message),
                _ => dialog.ShowPrompt(prompt.Message, prompt.Placeholder)
            }).Unwrap().WaitAsync(cancellationToken);

        public void Notify(AuthEvent authEvent) => mode.context.Loop.Post(() =>
        {
            switch (authEvent.Kind)
            {
                case AuthEventKind.AuthUrl: dialog.ShowAuth(authEvent.Url ?? "", authEvent.Instructions); break;
                case AuthEventKind.DeviceCode:
                    dialog.ShowDeviceCode(authEvent.UserCode ?? "", authEvent.VerificationUri ?? "");
                    dialog.ShowWaiting("Waiting for authentication...");
                    break;
                case AuthEventKind.Info: dialog.ShowInfo(authEvent.Message ?? "", authEvent.Links); break;
                default: dialog.ShowProgress(authEvent.Message ?? ""); break;
            }
        });
    }

    private Task<string> ShowAuthSelectAsync(LoginDialogComponent dialog, AuthPrompt prompt, string providerId)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void RestoreDialog()
        {
            editorContainer.Clear();
            editorContainer.AddChild(dialog);
            ui.SetFocus(dialog);
            ui.RequestRender();
        }
        var options = prompt.Options ?? [];
        var selector = new ExtensionSelectorComponent(prompt.Message, options.Select(option => option.Label).ToList(), label =>
        {
            RestoreDialog();
            if (options.FirstOrDefault(option => option.Label == label)?.Id is { } id) result.TrySetResult(id);
            else result.TrySetException(new InvalidOperationException("Login cancelled"));
        }, () =>
        {
            RestoreDialog();
            result.TrySetException(new InvalidOperationException("Login cancelled"));
        }, new ExtensionSelectorOptions(Description: providerId == RadiusProviderId ? RadiusLoginIntro : null));
        editorContainer.Clear();
        editorContainer.AddChild(selector);
        ui.SetFocus(selector);
        ui.RequestRender();
        return result.Task;
    }

    /// <summary>Offer to point the Radius MCP server in the global mcp.json at the Radius login.</summary>
    private void OfferRadiusMcpServer(string providerId, string providerName)
    {
        if (context.Mcp?.RadiusServerOffer(providerId) is not { } offer) return;
        ShowSelector(done =>
        {
            var selector = new ExtensionSelectorComponent($"Configure {providerName} MCP in {offer.ConfigPath}?", ["Yes", "No"], option =>
            {
                done();
                if (option != "Yes") return;
                try { offer.Apply(); }
                catch (Exception error) { ShowError($"Could not update {offer.ConfigPath}: {error.Message}"); return; }
                Run(HandleReloadCommandAsync);
            }, () => { done(); ui.RequestRender(); });
            return (selector, selector, selector.Dispose);
        });
    }
}
