// Decision 0011 (run W2, WU5): what the vault accepts. Provider ids, labels and settings are checked before anything is stored; an API key
// is checked for shape only (one line, no spaces) and its value never appears in a message. Settings are for non-secret configuration
// (a base URL, an AWS region or profile, a Cloudflare account id), so names that look like secrets and values that repeat the key are
// refused rather than stored in plain text.
using System.Text.RegularExpressions;
using PiSharp.AI.Catalogs;

namespace PiSharp.WebAgent.Credentials;

public sealed partial class CredentialVault
{
    /// <summary>The longest label.</summary>
    public const int MaximumLabelLength = 64;

    /// <summary>The longest API key accepted.</summary>
    public const int MaximumKeyLength = 8192;

    /// <summary>The most settings one account holds.</summary>
    public const int MaximumSettings = 16;

    /// <summary>Providers that take no credentials at all (the faux provider of <c>--faux</c>).</summary>
    public static IReadOnlySet<string> NoCredentialProviders { get; } = new HashSet<string>(StringComparer.Ordinal) { "faux" };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ProviderPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]{0,63}$")]
    private static partial Regex SettingNamePattern();

    [GeneratedRegex("KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase)]
    private static partial Regex SecretSettingName();

    /// <summary>Whether <paramref name="provider"/> connects only by signing in (openai-codex), so it takes no API key.</summary>
    public static bool SignInOnly(string provider) =>
        NoCredentialProviders.Contains(provider) ||
        BuiltinProviders.All.FirstOrDefault(definition => definition.Id == provider)?.Auth == AI.Catalogs.ProviderAuthKind.OAuthOnly;

    private static string CheckProvider(string? provider)
    {
        var id = provider?.Trim() ?? "";
        if (!ProviderPattern().IsMatch(id)) throw new ArgumentException("Choose a provider (a PiSharp provider id such as openai).");
        return id;
    }

    /// <summary>The trimmed key, or null when none was entered.</summary>
    private static string? CheckKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var trimmed = key.Trim();
        if (trimmed.Length > MaximumKeyLength) throw new ArgumentException($"The API key is longer than {MaximumKeyLength} characters.");
        if (trimmed.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            throw new ArgumentException("The API key contains spaces or line breaks; paste the key on its own.");
        return trimmed;
    }

    private static string CheckLabel(string? label)
    {
        var name = label?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Enter a name for the account.");
        if (name.Length > MaximumLabelLength) throw new ArgumentException($"The name is longer than {MaximumLabelLength} characters.");
        if (name.Any(char.IsControl)) throw new ArgumentException("The name contains a control character.");
        return name;
    }

    private static Dictionary<string, string> CheckSettings(IReadOnlyDictionary<string, string>? settings)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawName, rawValue) in settings ?? AccountInfo.EmptyMap)
        {
            var name = rawName?.Trim() ?? "";
            if (!SettingNamePattern().IsMatch(name)) throw new ArgumentException($"“{Clip(name)}” is not a setting name (letters, digits, _ and .).");
            if (SecretSettingName().IsMatch(name))
                throw new ArgumentException($"Settings hold no secrets, and “{name}” looks like one; enter the key in the API key field.");
            var value = rawValue?.Trim() ?? "";
            if (value.Length == 0) continue;
            if (value.Length > 2048) throw new ArgumentException($"The value of {name} is longer than 2048 characters.");
            if (value.Any(char.IsControl)) throw new ArgumentException($"The value of {name} contains a control character.");
            result[name] = value;
        }
        if (result.Count > MaximumSettings) throw new ArgumentException($"An account holds at most {MaximumSettings} settings.");
        return result;
    }

    private static void CheckSettingsHoldNoKey(IReadOnlyDictionary<string, string> settings, string? key)
    {
        if (key is not null && settings.Values.Any(value => value.Contains(key, StringComparison.Ordinal)))
            throw new ArgumentException("A setting repeats the API key; settings hold no secrets.");
    }

    private static string Clip(string text) => text.Length <= 40 ? text : text[..39] + "…";

    private static string DefaultKeyLabel(string provider) =>
        (BuiltinProviders.All.FirstOrDefault(definition => definition.Id == provider)?.Name ?? provider) + " key";

    /// <summary><paramref name="label"/>, or with " 2", " 3", … when another account of the provider has it.</summary>
    private static string UniqueLabel(IReadOnlyList<AccountInfo> providerRows, string label)
    {
        var taken = providerRows.Select(row => row.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(label)) return label;
        for (var number = 2; ; number++)
        {
            var suffix = " " + number;
            var candidate = (label.Length + suffix.Length > MaximumLabelLength ? label[..(MaximumLabelLength - suffix.Length)] : label) + suffix;
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}
