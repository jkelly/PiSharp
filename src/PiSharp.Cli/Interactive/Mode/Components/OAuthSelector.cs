// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/components/oauth-selector.ts.
// Also ports InteractiveMode.getLoginProviderOptions (modes/interactive/interactive-mode.ts), which builds this selector's
// provider list, over the narrow ILoginProviderCatalog.
using System.Globalization;
using System.Text.RegularExpressions;
using PiSharp.Tui.Pi;
using static PiSharp.Cli.Interactive.Mode.ThemeGlobals;

namespace PiSharp.Cli.Interactive.Mode.Components;

/// <summary>
/// The parts of a provider's ApiKeyAuth or OAuthAuth (pi-ai) the login UI reads: display name, whether it has an interactive
/// <c>login</c>, OAuth <c>isSubscription</c> and OAuth <c>loginLabel</c>.
/// </summary>
internal sealed record AuthMethodInfo(string Name, bool HasLogin = true, bool? IsSubscription = null, string? LoginLabel = null);

/// <summary>Source AuthCheck (pi-ai): <c>Type</c> is "api_key" or "oauth".</summary>
internal sealed record AuthCheck(string Type, string? Source = null);

/// <summary>Source AuthSelectorProvider. <c>AuthType</c> is "oauth" or "api_key".</summary>
internal sealed record AuthSelectorProvider(string Id, string Name, string AuthType, AuthMethodInfo? Method = null, AuthCheck? Status = null,
    bool? Subscription = null);

/// <summary>A provider as modelRuntime.getProviders() lists it: id, name and its <c>auth.oauth</c> / <c>auth.apiKey</c> methods.</summary>
internal sealed record LoginProviderInfo(string Id, string Name, AuthMethodInfo? OAuth = null, AuthMethodInfo? ApiKey = null);

/// <summary>Source modelRuntime.getProviderAuthStatus(id).</summary>
internal sealed record ProviderAuthStatus(bool Configured, string? Label = null, string? Source = null);

/// <summary>The modelRuntime members getLoginProviderOptions reads.</summary>
internal interface ILoginProviderCatalog
{
    IReadOnlyList<LoginProviderInfo> GetProviders();
    ProviderAuthStatus GetProviderAuthStatus(string providerId);
    bool IsUsingOAuth(string providerId);
}

internal static partial class AuthSelectorFormatting
{
    public static string FormatAuthSelectorProviderType(string authType, bool? subscription = null)
    {
        if (authType == "api_key") return "API key";
        return subscription == false ? "account" : "subscription";
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]*(?:, [A-Z][A-Z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentVariableList();

    /// <summary>Themed suffix describing whether and how a login option is configured, for example " ✓ configured".</summary>
    public static string FormatAuthSelectorProviderStatus(AuthSelectorProvider provider)
    {
        if (provider.Status is not { } status) return theme.Fg("muted", " • not configured");
        if (status.Type != provider.AuthType)
        {
            var label = $"{FormatAuthSelectorProviderType(status.Type, provider.Subscription)} configured";
            return theme.Fg("muted", " • ") + theme.Fg("warning", label);
        }
        if (string.IsNullOrEmpty(status.Source) || status.Source == "OAuth" || status.Source == "stored credential")
            return theme.Fg("success", " ✓ configured");
        var source = EnvironmentVariableList().IsMatch(status.Source) ? $"env: {status.Source}" : status.Source;
        return theme.Fg("success", $" ✓ {source}");
    }

    /// <summary>Source InteractiveMode.getLoginProviderOptions: every provider's OAuth and API-key options, sorted by name.</summary>
    public static List<AuthSelectorProvider> GetLoginProviderOptions(ILoginProviderCatalog runtime, string? authType = null)
    {
        var options = new List<AuthSelectorProvider>();
        foreach (var provider in runtime.GetProviders())
        {
            var authStatus = runtime.GetProviderAuthStatus(provider.Id);
            var status = authStatus.Configured
                ? new AuthCheck(runtime.IsUsingOAuth(provider.Id) ? "oauth" : "api_key", authStatus.Label ?? authStatus.Source)
                : null;
            var subscription = provider.OAuth?.IsSubscription == true;
            if ((authType is null || authType == "oauth") && provider.OAuth is { } oauth)
                options.Add(new(provider.Id, provider.Name, "oauth", oauth, status, subscription));
            if ((authType is null || authType == "api_key") && provider.ApiKey is { } apiKey)
                options.Add(new(provider.Id, provider.Name, "api_key", apiKey, status, subscription));
        }
        // Array.prototype.sort is stable; OrderBy keeps that.
        return [.. options.OrderBy(option => option.Name, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None))];
    }
}

/// <summary>Component that renders an auth provider selector.</summary>
internal sealed class OAuthSelectorComponent : Container, IFocusable, IInputHandler
{
    private readonly Input searchInput;

    // Focusable implementation - propagate to search input for IME cursor positioning
    private bool focused;
    public bool Focused
    {
        get => focused;
        set { focused = value; searchInput.Focused = value; }
    }

    private readonly Container listContainer;
    private readonly IReadOnlyList<AuthSelectorProvider> allProviders;
    private IReadOnlyList<AuthSelectorProvider> filteredProviders;
    private int selectedIndex;
    private readonly string mode;
    private readonly Action<string, string> onSelectCallback;
    private readonly Action onCancelCallback;
    private readonly bool showAuthTypeLabels;

    /// <param name="mode">"login" or "logout".</param>
    /// <param name="onSelect">(providerId, authType).</param>
    public OAuthSelectorComponent(string mode, IReadOnlyList<AuthSelectorProvider> providers, Action<string, string> onSelect, Action onCancel,
        string? initialSearchInput = null)
    {
        this.mode = mode;
        allProviders = providers;
        filteredProviders = providers;
        showAuthTypeLabels = providers.Select(provider => provider.AuthType).Distinct().Count() > 1;
        onSelectCallback = onSelect;
        onCancelCallback = onCancel;

        // Add top border
        AddChild(new DynamicBorder());
        AddChild(new Spacer(1));

        // Add title
        var title = mode == "login" ? "Select provider to configure:" : "Select provider to logout:";
        AddChild(new TruncatedText(theme.Fg("accent", theme.Bold(title)), 1, 0));
        AddChild(new Spacer(1));

        searchInput = new Input();
        if (!string.IsNullOrEmpty(initialSearchInput)) searchInput.SetValue(initialSearchInput);
        searchInput.OnSubmit = _ =>
        {
            if (selectedIndex < filteredProviders.Count && filteredProviders[selectedIndex] is { } selectedProvider)
                onSelectCallback(selectedProvider.Id, selectedProvider.AuthType);
        };
        AddChild(searchInput);
        AddChild(new Spacer(1));

        // Create list container
        listContainer = new Container();
        AddChild(listContainer);

        AddChild(new Spacer(1));

        // Add bottom border
        AddChild(new DynamicBorder());

        // Initial render
        FilterProviders(initialSearchInput ?? "");
    }

    private void FilterProviders(string query)
    {
        filteredProviders = query.Length > 0
            ? Fuzzy.Filter(allProviders, query, provider => $"{provider.Name} {provider.Id} {provider.AuthType} {provider.Method?.Name ?? ""}")
            : allProviders;
        selectedIndex = Math.Max(0, Math.Min(selectedIndex, Math.Max(0, filteredProviders.Count - 1)));
        UpdateList();
    }

    private void UpdateList()
    {
        listContainer.Clear();

        const int maxVisible = 8;
        var startIndex = Math.Max(0, Math.Min(selectedIndex - maxVisible / 2, filteredProviders.Count - maxVisible));
        var endIndex = Math.Min(startIndex + maxVisible, filteredProviders.Count);

        for (var i = startIndex; i < endIndex; i++)
        {
            var provider = filteredProviders[i];
            var isSelected = i == selectedIndex;

            var statusIndicator = AuthSelectorFormatting.FormatAuthSelectorProviderStatus(provider);
            var authTypeLabel = showAuthTypeLabels
                ? theme.Fg("muted", $" [{AuthSelectorFormatting.FormatAuthSelectorProviderType(provider.AuthType, provider.Subscription)}]")
                : "";
            string line;
            if (isSelected)
            {
                var prefix = theme.Fg("accent", "→ ");
                var text = theme.Fg("accent", provider.Name);
                line = prefix + text + authTypeLabel + statusIndicator;
            }
            else
            {
                var text = $"  {theme.Fg("text", provider.Name)}";
                line = text + authTypeLabel + statusIndicator;
            }

            listContainer.AddChild(new TruncatedText(line, 1, 0));
        }

        if (startIndex > 0 || endIndex < filteredProviders.Count)
        {
            var scrollInfo = theme.Fg("muted", $"  ({(selectedIndex + 1).ToString(CultureInfo.InvariantCulture)}/{filteredProviders.Count.ToString(CultureInfo.InvariantCulture)})");
            listContainer.AddChild(new TruncatedText(scrollInfo, 1, 0));
        }

        // Show "no providers" if empty
        if (filteredProviders.Count == 0)
        {
            var message = allProviders.Count == 0
                ? mode == "login" ? "No providers available" : "No providers logged in. Use /login first."
                : "No matching providers";
            listContainer.AddChild(new TruncatedText(theme.Fg("muted", $"  {message}"), 1, 0));
        }
    }

    public void HandleInput(string keyData)
    {
        var kb = KeybindingsManager.Global;
        // Up arrow
        if (kb.Matches(keyData, "tui.select.up"))
        {
            if (filteredProviders.Count == 0) return;
            selectedIndex = Math.Max(0, selectedIndex - 1);
            UpdateList();
        }
        // Down arrow
        else if (kb.Matches(keyData, "tui.select.down"))
        {
            if (filteredProviders.Count == 0) return;
            selectedIndex = Math.Min(filteredProviders.Count - 1, selectedIndex + 1);
            UpdateList();
        }
        // Enter
        else if (kb.Matches(keyData, "tui.select.confirm"))
        {
            if (selectedIndex < filteredProviders.Count && filteredProviders[selectedIndex] is { } selectedProvider)
                onSelectCallback(selectedProvider.Id, selectedProvider.AuthType);
        }
        // Escape or Ctrl+C
        else if (kb.Matches(keyData, "tui.select.cancel"))
        {
            onCancelCallback();
        }
        // Pass everything else to search input
        else
        {
            searchInput.HandleInput(keyData);
            FilterProviders(searchInput.GetValue());
        }
    }
}

