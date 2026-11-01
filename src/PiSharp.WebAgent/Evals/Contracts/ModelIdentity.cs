// Decision 0011 (run W5, E0): who a leaderboard row, an Arena contestant or a suite target's drift history is about. The approved plan's
// "Defaults chosen" fixes it: the provider, the model and the settings that change answers (thinking, temperature, service tier), never the
// account, so a model rated through two accounts is one row (mockup evals-leaderboard: "openai-codex/gpt-6.1-sol · thinking medium",
// "default settings"). Thinking "off" and no thinking are the same; temperature counts to two decimals. Key is the canonical text the
// rater and the stores group by.
using System.Globalization;
using System.Text.Json.Serialization;
using PiSharp.WebAgent.Runs;

namespace PiSharp.WebAgent.Evals;

/// <summary>A model with the settings that change its answers: the leaderboard's identity (never the account).</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model id.</param>
/// <param name="Thinking">The thinking level (<see cref="RunSpec.ThinkingLevels"/> other than <c>off</c>), or null for none.</param>
/// <param name="Temperature">The temperature, 0 to 2, or null for the provider's default.</param>
/// <param name="ServiceTier">The service tier (<see cref="RunSpec.ServiceTiers"/>), or null for the provider's default.</param>
public sealed record ModelIdentity(string Provider, string Model, string? Thinking = null, double? Temperature = null, string? ServiceTier = null)
{
    /// <summary>The identity of what <paramref name="spec"/> asks for (its provider, model, thinking, temperature and service tier).</summary>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static ModelIdentity Of(RunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ModelIdentity(spec.Provider, spec.Model, spec.Thinking, spec.Temperature, spec.ServiceTier).Normalize();
    }

    /// <summary>The same identity, trimmed: thinking <c>off</c> as null, the temperature rounded to two decimals.</summary>
    /// <exception cref="ArgumentException">A value is not allowed; the message says which.</exception>
    public ModelIdentity Normalize()
    {
        var provider = Provider?.Trim() ?? "";
        var model = Model?.Trim() ?? "";
        if (provider.Length == 0) throw new ArgumentException("Choose a provider.");
        if (model.Length == 0) throw new ArgumentException("Choose a model.");
        if (provider.Length > 200 || model.Length > 200 || provider.Contains('/') || provider.Any(char.IsControl) || model.Any(char.IsControl))
            throw new ArgumentException($"\"{provider}/{model}\" is not a provider and model.");
        var thinking = string.IsNullOrWhiteSpace(Thinking) ? null : Thinking.Trim();
        if (thinking is not null && !RunSpec.ThinkingLevels.Contains(thinking))
            throw new ArgumentException($"Thinking is {string.Join(", ", RunSpec.ThinkingLevels)}, not \"{thinking}\".");
        if (Temperature is { } value && (!double.IsFinite(value) || value < 0 || value > 2)) throw new ArgumentException("Temperature is 0 to 2.");
        var tier = string.IsNullOrWhiteSpace(ServiceTier) ? null : ServiceTier.Trim();
        if (tier is not null && !RunSpec.ServiceTiers.Contains(tier))
            throw new ArgumentException($"The service tier is {string.Join(", ", RunSpec.ServiceTiers)}, not \"{tier}\".");
        return new ModelIdentity(provider, model, thinking is "off" ? null : thinking,
            Temperature is { } temperature ? Math.Round(temperature, 2, MidpointRounding.AwayFromZero) : null, tier);
    }

    /// <summary><c>provider/model</c>, as the pages show it in mono type.</summary>
    [JsonIgnore]
    public string Name => Provider + "/" + Model;

    /// <summary>The canonical text of the identity: <c>provider/model</c>, then <c>;thinking=</c>, <c>;temperature=</c> and <c>;tier=</c> for
    /// each setting that is set, in that order (for example <c>openai-codex/gpt-6.1-sol;thinking=medium</c>). Two identities are the same
    /// leaderboard row exactly when their keys are equal. Call it on a normalized identity.</summary>
    [JsonIgnore]
    public string Key => Name
        + (Thinking is null ? "" : ";thinking=" + Thinking)
        + (Temperature is { } temperature ? ";temperature=" + temperature.ToString("0.##", CultureInfo.InvariantCulture) : "")
        + (ServiceTier is null ? "" : ";tier=" + ServiceTier);

    /// <summary>The settings as the leaderboard's second line says them: <c>thinking medium · temperature 0.7 · service tier priority</c>, or
    /// <c>default settings</c> when none is set.</summary>
    [JsonIgnore]
    public string SettingsLabel
    {
        get
        {
            var parts = new List<string>(3);
            if (Thinking is not null) parts.Add("thinking " + Thinking);
            if (Temperature is { } temperature) parts.Add("temperature " + temperature.ToString("0.##", CultureInfo.InvariantCulture));
            if (ServiceTier is not null) parts.Add("service tier " + ServiceTier);
            return parts.Count == 0 ? "default settings" : string.Join(" · ", parts);
        }
    }

    /// <summary>Whether <paramref name="other"/> is the same leaderboard row (same <see cref="Key"/> once both are normalized).</summary>
    public bool SameAs(ModelIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Normalize().Key, other.Normalize().Key, StringComparison.Ordinal);
    }
}
